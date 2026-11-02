// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Windows;

/// <summary>Live state of one service or driver as reported by the Service Control Manager.</summary>
internal sealed record ScmEntry(string? DisplayName, int State, int ServiceType);

internal interface IServiceStateReader
{
    /// <summary>Snapshot keyed by service name (case-insensitive). Throws on source-level failure.</summary>
    IReadOnlyDictionary<string, ScmEntry> Read(CancellationToken cancellationToken);
}

/// <summary>
/// One read-only EnumServicesStatusEx pass over a manager handle opened with SC_MANAGER_ENUMERATE_SERVICE only. It does not open
/// individual services and does not control them.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ScmStateReader : IServiceStateReader
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceWin32 = 0x30, ServiceDriver = 0x0B, StateAll = 3;
    private const int InfoLevelProcess = 0, ErrorMoreData = 234;
    private const int MaxEntries = 100_000;

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName; public IntPtr DisplayName;
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusEx(IntPtr scManager, int infoLevel, uint serviceType, uint serviceState, IntPtr buffer, uint bufferSize,
        out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public IReadOnlyDictionary<string, ScmEntry> Read(CancellationToken ct)
    {
        var result = new Dictionary<string, ScmEntry>(StringComparer.OrdinalIgnoreCase);
        var manager = OpenSCManager(null, null, ScManagerEnumerateService);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(256 * 1024);
        var size = 256u * 1024;
        try
        {
            uint resume = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var ok = EnumServicesStatusEx(manager, InfoLevelProcess, ServiceWin32 | ServiceDriver, StateAll, buffer, size, out var needed, out var returned, ref resume, null);
                var error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != ErrorMoreData) throw new Win32Exception(error);
                var step = Marshal.SizeOf<EnumServiceStatusProcess>();
                for (var i = 0; i < returned && result.Count < MaxEntries; i++)
                {
                    var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + i * step);
                    var name = Marshal.PtrToStringUni(entry.ServiceName);
                    if (string.IsNullOrEmpty(name)) continue;
                    result[name] = new(Marshal.PtrToStringUni(entry.DisplayName), (int)entry.CurrentState, (int)entry.ServiceType);
                }
                if (ok || result.Count >= MaxEntries) break;
                if (needed > size && needed <= 4 * 1024 * 1024)
                {
                    Marshal.FreeHGlobal(buffer);
                    size = needed;
                    buffer = Marshal.AllocHGlobal((int)size);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); CloseServiceHandle(manager); }
        return result;
    }
}

/// <summary>
/// Read-only service and driver inventory: configuration from HKLM\SYSTEM\CurrentControlSet\Services plus live state and resolved display
/// name from one SCM enumeration. Nothing is started, stopped or reconfigured.
/// </summary>
public sealed class ServiceDiscovery : IDiscoverySource<ServiceInfo>
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";
    private static readonly string SourceName = nameof(ServiceInfo);
    private readonly IRegistryReader? registry;
    private readonly IServiceStateReader? scm;
    private readonly int maxItems;
    private readonly Func<DateTimeOffset> clock;

    public ServiceDiscovery() : this(HostReaders.Registry(), OperatingSystem.IsWindows() ? new ScmStateReader() : null,
        DiscoverySupport.DefaultMaxItems, () => DateTimeOffset.UtcNow) { }

    internal ServiceDiscovery(IRegistryReader? registry, IServiceStateReader? scm, int maxItems, Func<DateTimeOffset> clock)
    {
        this.registry = registry; this.scm = scm; this.maxItems = maxItems; this.clock = clock;
    }

    public Task<DiscoveryResult<ServiceInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (registry is null || scm is null) return Task.FromResult(DiscoverySupport.Unavailable<ServiceInfo>(clock()));
        return Task.Run(() => Collect(registry, scm, cancellationToken), cancellationToken);
    }

    private DiscoveryResult<ServiceInfo> Collect(IRegistryReader reg, IServiceStateReader stateReader, CancellationToken ct)
    {
        var items = new List<ServiceInfo>();
        var issues = new List<DiscoveryIssue>();

        IReadOnlyDictionary<string, ScmEntry> states = new Dictionary<string, ScmEntry>();
        try { states = stateReader.Read(ct); }
        catch (Exception e) when (!DiscoverySupport.IsCancellation(e)) { issues.Add(DiscoverySupport.FromException(SourceName, e, "scm")); }

        IReadOnlyList<string>? names;
        try { names = reg.GetSubKeyNames(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, ServicesPath); }
        catch (Exception e) when (!DiscoverySupport.IsCancellation(e))
        {
            issues.Add(DiscoverySupport.FromException(SourceName, e, "services-key"));
            return new(items, issues, clock());
        }
        if (names is null)
        {
            issues.Add(DiscoverySupport.Issue(SourceName, ErrorCode.DiscoveryUnavailable));
            return new(items, issues, clock());
        }

        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (items.Count >= maxItems) { issues.Add(DiscoverySupport.Issue(SourceName, ErrorCode.DiscoveryItemUnreadable, "item-cap")); break; }
            try
            {
                var values = reg.GetValues(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, ServicesPath + "\\" + name);
                if (values is null) continue;
                states.TryGetValue(name, out var live);
                var info = Map(name, values, live);
                if (info is not null) items.Add(info);
            }
            catch (Exception e) when (!DiscoverySupport.IsCancellation(e))
            {
                issues.Add(DiscoverySupport.FromException(SourceName, e, name));
            }
        }
        items.Sort((a, b) => { var c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : string.CompareOrdinal(a.StableId, b.StableId); });
        return new(items, issues, clock());
    }

    /// <summary>Keys without a numeric Type are not services (parameter holders, groups) and are skipped.</summary>
    internal static ServiceInfo? Map(string name, IReadOnlyDictionary<string, RegistryValueData> v, ScmEntry? live)
    {
        if (v.GetValueOrDefault("Type")?.AsNumber is not { } type) return null;
        var start = v.GetValueOrDefault("Start")?.AsNumber;
        var delayed = v.GetValueOrDefault("DelayedAutostart")?.AsNumber == 1;
        var display = ReadableText(live?.DisplayName) ?? ReadableText(v.GetValueOrDefault("DisplayName")?.AsString) ?? name;
        var kind = MapKind(type);
        var account = v.GetValueOrDefault("ObjectName")?.AsString;
        return new ServiceInfo("service:" + DiscoverySupport.Fold(name), DiscoverySupport.Cap(display.Trim(), 512)!, MapStart(start, delayed),
            live is null ? ServiceRunState.Unknown : MapState(live.State), kind,
            string.IsNullOrWhiteSpace(account) ? null : DiscoverySupport.Cap(account.Trim(), 256),
            DiscoverySupport.Cap(v.GetValueOrDefault("ImagePath")?.AsString),
            DiscoverySupport.Cap(ReadableText(v.GetValueOrDefault("Description")?.AsString)));
    }

    /// <summary>
    /// A value that starts with "@" is an indirect resource reference ("@file,-id"). Resolving it needs library loading, so it is not shown. The Windows form
    /// "@file,-id;fallback text" carries a readable fallback after the first semicolon: that text is used. A reference without a fallback gives null, so the
    /// caller falls back to the service name. The service control manager returns these raw strings for many drivers.
    /// </summary>
    internal static string? ReadableText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('@')) return trimmed;
        var semicolon = trimmed.IndexOf(';');
        if (semicolon < 0) return null;
        var fallback = trimmed[(semicolon + 1)..].Trim();
        return fallback.Length == 0 || fallback.StartsWith('@') ? null : fallback;
    }

    internal static ServiceKind MapKind(long type) =>
        (type & 0x1) != 0 ? ServiceKind.KernelDriver :
        (type & 0x2) != 0 ? ServiceKind.FileSystemDriver :
        (type & 0x10) != 0 ? ServiceKind.Win32Own :
        (type & 0x20) != 0 ? ServiceKind.Win32Shared :
        (type & 0xFF) != 0 ? ServiceKind.Other : ServiceKind.Unknown;

    internal static ServiceStartMode MapStart(long? start, bool delayed) => start switch
    {
        0 => ServiceStartMode.Boot, 1 => ServiceStartMode.System, 2 => delayed ? ServiceStartMode.AutomaticDelayed : ServiceStartMode.Automatic,
        3 => ServiceStartMode.Manual, 4 => ServiceStartMode.Disabled, _ => ServiceStartMode.Unknown,
    };

    internal static ServiceRunState MapState(int state) => state switch
    {
        1 => ServiceRunState.Stopped, 2 => ServiceRunState.StartPending, 3 => ServiceRunState.StopPending, 4 => ServiceRunState.Running,
        5 => ServiceRunState.ContinuePending, 6 => ServiceRunState.PausePending, 7 => ServiceRunState.Paused, _ => ServiceRunState.Unknown,
    };
}
