// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.RegularExpressions;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// Read-only inventory of registered desktop applications from the Uninstall keys (HKLM 64-bit, HKLM WOW6432 and HKCU).
/// Only display metadata is read. The uninstall command values are deliberately never read, stored or exposed here; the removal flow reads them with UninstallRegistrationReader at the moment of a removal.
/// Packaged (MSIX/AppX) applications are not covered by this adapter.
/// </summary>
public sealed partial class InstalledApplicationDiscovery : IDiscoverySource<InstalledApplication>
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly string SourceName = nameof(InstalledApplication);
    private static readonly string[] UpdateReleaseTypes = ["Update", "Security Update", "Hotfix", "Update Rollup", "Service Pack", "Critical Update"];

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex GuidKey();

    private sealed record Root(RegistryHiveKind Hive, RegistryViewKind View, string HiveName, string ViewName, InstallationScope Scope);
    private static readonly Root[] Roots =
    [
        new(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, "HKLM", "64", InstallationScope.Machine),
        new(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, "HKLM", "32", InstallationScope.Machine),
        new(RegistryHiveKind.CurrentUser, RegistryViewKind.Registry64, "HKCU", "64", InstallationScope.User),
    ];

    private readonly IRegistryReader? registry;
    private readonly int maxItems;
    private readonly Func<DateTimeOffset> clock;

    public InstalledApplicationDiscovery() : this(HostReaders.Registry(), DiscoverySupport.DefaultMaxItems, () => DateTimeOffset.UtcNow) { }

    internal InstalledApplicationDiscovery(IRegistryReader? registry, int maxItems, Func<DateTimeOffset> clock)
    {
        this.registry = registry; this.maxItems = maxItems; this.clock = clock;
    }

    public Task<DiscoveryResult<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (registry is null) return Task.FromResult(DiscoverySupport.Unavailable<InstalledApplication>(clock()));
        return Task.Run(() => Collect(registry, cancellationToken), cancellationToken);
    }

    private DiscoveryResult<InstalledApplication> Collect(IRegistryReader reg, CancellationToken ct)
    {
        var items = new List<InstalledApplication>();
        var issues = new List<DiscoveryIssue>();
        var capped = false;
        foreach (var root in Roots)
        {
            ct.ThrowIfCancellationRequested();
            var tag = $"{root.HiveName}/{root.ViewName}";
            IReadOnlyList<string>? names;
            try { names = reg.GetSubKeyNames(root.Hive, root.View, UninstallPath); }
            catch (Exception e) when (!DiscoverySupport.IsCancellation(e)) { issues.Add(DiscoverySupport.FromException(SourceName, e, tag)); continue; }
            if (names is null) continue;

            foreach (var keyName in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (items.Count >= maxItems)
                {
                    if (!capped) { capped = true; issues.Add(DiscoverySupport.Issue(SourceName, ErrorCode.DiscoveryItemUnreadable, "item-cap")); }
                    break;
                }
                try
                {
                    var values = reg.GetValues(root.Hive, root.View, UninstallPath + "\\" + keyName);
                    if (values is null) continue;
                    var app = Map(root, keyName, values);
                    if (app is not null) items.Add(app);
                }
                catch (Exception e) when (!DiscoverySupport.IsCancellation(e))
                {
                    issues.Add(DiscoverySupport.FromException(SourceName, e, $"{tag}/{keyName}"));
                }
            }
        }
        items.Sort((a, b) =>
        {
            var c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Identity.StableIdentity, b.Identity.StableIdentity);
        });
        return new(items, issues, clock());
    }

    private static InstalledApplication? Map(Root root, string keyName, IReadOnlyDictionary<string, RegistryValueData> v)
    {
        var displayName = v.GetValueOrDefault("DisplayName")?.AsString?.Trim();
        if (string.IsNullOrEmpty(displayName)) return null;
        if (!string.IsNullOrWhiteSpace(v.GetValueOrDefault("ParentKeyName")?.AsString)) return null;
        var release = v.GetValueOrDefault("ReleaseType")?.AsString?.Trim();
        if (release is not null && UpdateReleaseTypes.Contains(release, StringComparer.OrdinalIgnoreCase)) return null;

        var msi = v.GetValueOrDefault("WindowsInstaller")?.AsNumber == 1 || GuidKey().IsMatch(keyName);
        var provenance = $"registry:{root.HiveName}\\{root.ViewName}\\{keyName}";
        var publisher = Clean(v.GetValueOrDefault("Publisher")?.AsString, 256);
        var identity = new InstallationIdentity(msi ? InstallationKind.Msi : InstallationKind.Win32, DiscoverySupport.Fold(provenance), root.Scope, root.View, publisher);
        return new InstalledApplication(identity, DiscoverySupport.Cap(displayName, 512)!, Clean(v.GetValueOrDefault("InstallLocation")?.AsString, 1024), provenance,
            Clean(v.GetValueOrDefault("DisplayVersion")?.AsString, 128), ParseDate(v.GetValueOrDefault("InstallDate")?.AsString),
            v.GetValueOrDefault("EstimatedSize")?.AsNumber is { } kb and >= 0 ? kb : null,
            v.GetValueOrDefault("SystemComponent")?.AsNumber == 1, Clean(v.GetValueOrDefault("DisplayIcon")?.AsString, 1024));
    }

    private static string? Clean(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : DiscoverySupport.Cap(text.Trim(), max);

    internal static DateOnly? ParseDate(string? text) =>
        text is not null && DateOnly.TryParseExact(text.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
