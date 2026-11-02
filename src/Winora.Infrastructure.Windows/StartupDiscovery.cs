// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Windows;

/// <summary>Startup folder and file metadata seam (listing, shortcut bytes, version info). All members are read-only.</summary>
internal interface IStartupFileSystem
{
    IReadOnlyList<(string Folder, InstallationScope Scope)> StartupFolders();
    /// <summary>Full paths of files directly inside the folder, or null if the folder is missing.</summary>
    IReadOnlyList<string>? ListFiles(string folder);
    /// <summary>First bytes of a file (bounded), or null if unreadable.</summary>
    byte[]? ReadHead(string path, int maxBytes);
    /// <summary>Company name from the file's version resource, or null; must tolerate every failure.</summary>
    string? CompanyName(string path);
    /// <summary>Environment expansion used only to locate a file for version info; the displayed command is never expanded.</summary>
    string Expand(string text);
}

internal sealed class HostStartupFileSystem : IStartupFileSystem
{
    public IReadOnlyList<(string, InstallationScope)> StartupFolders() =>
    [
        (Environment.GetFolderPath(Environment.SpecialFolder.Startup), InstallationScope.User),
        (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), InstallationScope.Machine),
    ];

    public IReadOnlyList<string>? ListFiles(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder).Take(2000).ToArray();
    }

    public byte[]? ReadHead(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var buffer = new byte[(int)Math.Min(maxBytes, stream.Length)];
            var read = 0;
            while (read < buffer.Length) { var n = stream.Read(buffer, read, buffer.Length - read); if (n == 0) break; read += n; }
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? CompanyName(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).CompanyName : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public string Expand(string text) => Environment.ExpandEnvironmentVariables(text);
}

/// <summary>
/// Read-only startup inventory: HKCU/HKLM Run and RunOnce (64-bit and WOW6432 views for HKLM), per-user and common Startup folders,
/// and the enabled/disabled flag from the Explorer StartupApproved keys. Commands are data and are never executed.
/// </summary>
public sealed class StartupDiscovery : IDiscoverySource<StartupItem>
{
    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion";
    private const string ApprovedRoot = CurrentVersion + @"\Explorer\StartupApproved";
    private const int MaxPublisherLookups = 300;
    private const int MaxNameLength = 256;
    private static readonly string SourceName = nameof(StartupItem);

    private readonly IRegistryReader? registry;
    private readonly IStartupFileSystem? files;
    private readonly int maxItems;
    private readonly Func<DateTimeOffset> clock;

    public StartupDiscovery() : this(HostReaders.Registry(), OperatingSystem.IsWindows() ? new HostStartupFileSystem() : null,
        DiscoverySupport.DefaultMaxItems, () => DateTimeOffset.UtcNow) { }

    internal StartupDiscovery(IRegistryReader? registry, IStartupFileSystem? files, int maxItems, Func<DateTimeOffset> clock)
    {
        this.registry = registry; this.files = files; this.maxItems = maxItems; this.clock = clock;
    }

    public Task<DiscoveryResult<StartupItem>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (registry is null || files is null) return Task.FromResult(DiscoverySupport.Unavailable<StartupItem>(clock()));
        return Task.Run(() => Collect(registry, files, cancellationToken), cancellationToken);
    }

    private sealed record Root(RegistryHiveKind Hive, RegistryViewKind View, string HiveName, string ViewName, InstallationScope Scope);
    private static readonly Root[] Roots =
    [
        new(RegistryHiveKind.CurrentUser, RegistryViewKind.Registry64, "hkcu", "64", InstallationScope.User),
        new(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, "hklm", "64", InstallationScope.Machine),
        new(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, "hklm", "32", InstallationScope.Machine),
    ];

    private DiscoveryResult<StartupItem> Collect(IRegistryReader reg, IStartupFileSystem fs, CancellationToken ct)
    {
        var items = new List<StartupItem>();
        var issues = new List<DiscoveryIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var publishers = new PublisherLookup(fs);
        var capped = false;

        bool Add(StartupItem item)
        {
            if (!seen.Add(item.StableId)) return true;
            if (items.Count >= maxItems)
            {
                if (!capped) { capped = true; issues.Add(DiscoverySupport.Issue(SourceName, ErrorCode.DiscoveryItemUnreadable, "item-cap")); }
                return false;
            }
            items.Add(item);
            return true;
        }

        foreach (var root in Roots)
        {
            foreach (var runOnce in new[] { false, true })
            {
                ct.ThrowIfCancellationRequested();
                var kindId = runOnce ? "runonce" : "run";
                var tag = $"{root.HiveName}/{root.ViewName}/{kindId}";
                var subPath = CurrentVersion + (runOnce ? @"\RunOnce" : @"\Run");
                IReadOnlyDictionary<string, RegistryValueData>? values;
                try { values = reg.GetValues(root.Hive, root.View, subPath); }
                catch (Exception e) when (!DiscoverySupport.IsCancellation(e)) { issues.Add(DiscoverySupport.FromException(SourceName, e, tag)); continue; }
                if (values is null) continue;
                var approvals = runOnce ? null : LoadApprovals(reg, root.Hive, root.View == RegistryViewKind.Registry32 ? "Run32" : "Run", tag, issues);

                foreach (var name in values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    var data = values[name];
                    try
                    {
                        if (name.Length == 0) continue; // the key's (Default) value is not a startup entry
                        var command = data.AsString;
                        if (string.IsNullOrWhiteSpace(name) || command is null)
                        {
                            issues.Add(DiscoverySupport.Issue(SourceName, ErrorCode.DiscoveryItemUnreadable, $"{tag}:{name}"));
                            continue;
                        }
                        var approval = runOnce ? StartupApproval.Unknown
                            : approvals is not null && approvals.TryGetValue(name, out var flag) ? DecodeApproval(flag) : StartupApproval.Enabled;
                        var shown = DiscoverySupport.Cap(command)!;
                        var item = new StartupItem($"startup:{kindId}:{root.HiveName}:{root.ViewName}:{DiscoverySupport.Fold(name)}",
                            DiscoverySupport.Cap(name, 256)!, shown, runOnce ? StartupSource.RegistryRunOnce : StartupSource.RegistryRun, root.Scope, approval,
                            new ResourceIdentity("registry", root.Scope == InstallationScope.User ? "user" : "machine",
                                $"{root.HiveName}/{root.ViewName}/{subPath.Replace('\\', '/')}/{DiscoverySupport.Fold(name)}"),
                            publishers.ForCommand(shown, ct),
                            runOnce || name.Length > MaxNameLength ? null : new StartupLocation(root.View == RegistryViewKind.Registry32 ? StartupApprovalStore.Run32 : StartupApprovalStore.Run, name));
                        if (!Add(item)) break;
                    }
                    catch (Exception e) when (!DiscoverySupport.IsCancellation(e))
                    {
                        issues.Add(DiscoverySupport.FromException(SourceName, e, $"{tag}:{name}"));
                    }
                }
            }
        }

        foreach (var (folder, scope) in SafeFolders(fs, issues))
        {
            ct.ThrowIfCancellationRequested();
            var scopeName = scope == InstallationScope.User ? "user" : "machine";
            var tag = $"folder/{scopeName}";
            IReadOnlyList<string>? list;
            try { list = fs.ListFiles(folder); }
            catch (Exception e) when (!DiscoverySupport.IsCancellation(e)) { issues.Add(DiscoverySupport.FromException(SourceName, e, tag)); continue; }
            if (list is null) continue;
            var approvals = LoadApprovals(reg, scope == InstallationScope.User ? RegistryHiveKind.CurrentUser : RegistryHiveKind.LocalMachine, "StartupFolder", tag, issues);

            foreach (var path in list.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                var ext = Path.GetExtension(fileName);
                if (!(ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)))
                    continue;
                try
                {
                    var command = path;
                    if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        var head = fs.ReadHead(path, ShortcutParser.MaxBytes + 1);
                        if (head is not null && ShortcutParser.TryRead(head, out var target, out var args) && target is not null)
                            command = (target.Contains(' ') ? $"\"{target}\"" : target) + (string.IsNullOrEmpty(args) ? "" : " " + args);
                    }
                    var approval = approvals is not null && approvals.TryGetValue(fileName, out var flag) ? DecodeApproval(flag) : StartupApproval.Enabled;
                    var shown = DiscoverySupport.Cap(command)!;
                    var item = new StartupItem($"startup:folder:{scopeName}:{DiscoverySupport.Fold(fileName)}",
                        DiscoverySupport.Cap(Path.GetFileNameWithoutExtension(fileName), 256)!, shown, StartupSource.StartupFolder, scope, approval,
                        new ResourceIdentity("file", scopeName, DiscoverySupport.Cap(path.Replace('\\', '/'))!), publishers.ForCommand(shown, ct),
                        fileName.Length > MaxNameLength ? null : new StartupLocation(StartupApprovalStore.StartupFolder, fileName));
                    if (!Add(item)) break;
                }
                catch (Exception e) when (!DiscoverySupport.IsCancellation(e))
                {
                    issues.Add(DiscoverySupport.FromException(SourceName, e, $"{tag}:{fileName}"));
                }
            }
        }

        items.Sort((a, b) => { var c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : string.CompareOrdinal(a.StableId, b.StableId); });
        return new(items, issues, clock());
    }

    private static IReadOnlyList<(string, InstallationScope)> SafeFolders(IStartupFileSystem fs, List<DiscoveryIssue> issues)
    {
        try { return fs.StartupFolders(); }
        catch (Exception e) when (!DiscoverySupport.IsCancellation(e)) { issues.Add(DiscoverySupport.FromException(SourceName, e, "folders")); return []; }
    }

    private static IReadOnlyDictionary<string, RegistryValueData>? LoadApprovals(IRegistryReader reg, RegistryHiveKind hive, string leaf, string tag, List<DiscoveryIssue> issues)
    {
        try { return reg.GetValues(hive, RegistryViewKind.Registry64, ApprovedRoot + "\\" + leaf); }
        catch (Exception e) when (!DiscoverySupport.IsCancellation(e))
        {
            issues.Add(DiscoverySupport.FromException(SourceName, e, $"{tag}/approved"));
            return null;
        }
    }

    /// <summary>
    /// StartupApproved values are binary (normally 12 bytes). Observed behavior of the shell, not a published contract:
    /// first byte 0x02 or 0x06 means enabled, 0x03 or 0x07 means disabled. Anything else is reported as Unknown.
    /// </summary>
    internal static StartupApproval DecodeApproval(RegistryValueData value)
    {
        var bytes = value.AsBytes;
        if (bytes is null || bytes.Length == 0) return StartupApproval.Unknown;
        return bytes[0] switch { 0x02 or 0x06 => StartupApproval.Enabled, 0x03 or 0x07 => StartupApproval.Disabled, _ => StartupApproval.Unknown };
    }

    /// <summary>Bounded, cached, read-only publisher lookup from file version info.</summary>
    private sealed class PublisherLookup(IStartupFileSystem fs)
    {
        private readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);
        private int lookups;

        public string? ForCommand(string command, CancellationToken ct)
        {
            var exe = ExtractExecutable(command);
            if (exe is null) return null;
            string path;
            try { path = fs.Expand(exe); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathRooted(path) || path.Contains('%')) return null;
            if (cache.TryGetValue(path, out var cached)) return cached;
            if (lookups >= MaxPublisherLookups) return null;
            ct.ThrowIfCancellationRequested();
            lookups++;
            string? company = null;
            try { company = fs.CompanyName(path); } catch (Exception e) when (!DiscoverySupport.IsCancellation(e)) { }
            company = string.IsNullOrWhiteSpace(company) ? null : DiscoverySupport.Cap(company.Trim(), 256);
            cache[path] = company;
            return company;
        }

        internal static string? ExtractExecutable(string command) => StartupCommand.ExtractExecutable(command);
    }
}
