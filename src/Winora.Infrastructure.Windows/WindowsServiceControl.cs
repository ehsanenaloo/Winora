// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Windows.Worker;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// The one file that starts or stops a service (DEC-IW-270). It opens one service by its short name with the least access the call needs: query for a lookup, start for a start, stop for a
/// stop. The startup type is written by the reviewed <see cref="IMachineServices"/> code that the definition engine also uses (delayed flag included). A service name is an argument of the
/// service manager only; it is never part of a command line and no program is started from here. The callers (<see cref="ServiceManagerService"/>) have already checked the safety policy, the
/// journal and the compare-and-set; this class does no policy of its own. A source scan keeps these calls out of every other file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceControl(IMachineServices machine) : IServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint QueryConfig = 0x0001, QueryStatus = 0x0004, EnumerateDependents = 0x0008, StartRight = 0x0010, StopRight = 0x0020;
    private const uint ControlStop = 1, StateAll = 3;
    private const int InfoLevelProcess = 0, ConfigDelayedAutoStart = 3, ConfigLaunchProtected = 12;
    private const int ErrorAccessDenied = 5, ErrorServiceDoesNotExist = 1060, ErrorDependentRunning = 1051, ErrorRequestTimeout = 1053, ErrorAlreadyRunning = 1056, ErrorDisabled = 1058,
        ErrorCannotAcceptControl = 1061, ErrorNotActive = 1062, ErrorDependencyFailed = 1068, ErrorDependencyMissing = 1075, ErrorLockedDatabase = 1055;
    private const int MaxDependents = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenService(nint manager, string name, uint access);

    [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
    private static extern bool CloseServiceHandle(nint handle);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    private static extern bool QueryServiceConfig(nint service, nint buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    private static extern bool QueryServiceConfig2(nint service, int level, nint buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(nint service, int level, nint buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", EntryPoint = "GetServiceDisplayNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetServiceDisplayName(nint manager, string name, [Out] char[] buffer, ref uint size);

    [DllImport("advapi32.dll", EntryPoint = "EnumDependentServicesW", SetLastError = true)]
    private static extern bool EnumDependentServices(nint service, uint state, nint buffer, uint size, out uint needed, out uint count);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    private static extern bool StartService(nint service, uint argc, nint argv);

    [DllImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    private static extern bool ControlService(nint service, uint control, out ServiceStatus status);

    private readonly record struct Opened(nint Manager, nint Service, int Error)
    {
        public bool Ok => Service != 0;
    }

    private static Opened Open(string name, uint access)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0) return new(0, 0, Marshal.GetLastWin32Error());
        var service = OpenService(manager, name, access);
        if (service != 0) return new(manager, service, 0);
        var error = Marshal.GetLastWin32Error();
        CloseServiceHandle(manager);
        return new(0, 0, error);
    }

    private static void Close(Opened o)
    {
        if (o.Service != 0) CloseServiceHandle(o.Service);
        if (o.Manager != 0) CloseServiceHandle(o.Manager);
    }

    public ServiceLookup Observe(string name)
    {
        var o = Open(name, QueryConfig | QueryStatus | EnumerateDependents);
        if (!o.Ok) return o.Error == ErrorServiceDoesNotExist ? ServiceLookup.Missing : ServiceLookup.Fail(o.Error == ErrorAccessDenied ? ServiceCode.AccessDenied : ServiceCode.Failed);
        try
        {
            var config = Marshal.AllocHGlobal(8192);
            try
            {
                if (!QueryServiceConfig(o.Service, config, 8192, out _)) return ServiceLookup.Fail(ServiceCode.Failed);
                var type = (uint)Marshal.ReadInt32(config, 0);
                var start = (uint)Marshal.ReadInt32(config, 4);
                var pathPointer = Marshal.ReadIntPtr(config, nint.Size == 8 ? 16 : 12);
                var path = pathPointer >= config && pathPointer < config + 8190 ? Marshal.PtrToStringUni(pathPointer) : null;
                var status = ReadStatus(o.Service);
                if (status is null) return ServiceLookup.Fail(ServiceCode.Failed);
                var delayed = start == 2 && IsDelayed(o.Service);
                var (all, running, dependentsKnown) = Dependents(o.Service);
                var (trusted, enabled, factsKnown) = Facts(name, all);
                return ServiceLookup.Of(new(name, DisplayName(o.Manager, name), StartMode(start, delayed), RunState(status.Value.CurrentState), Kind(type), path, trusted,
                    (status.Value.ControlsAccepted & 1) != 0, running, enabled, IsLaunchProtected(o.Service), dependentsKnown && factsKnown));
            }
            finally { Marshal.FreeHGlobal(config); }
        }
        finally { Close(o); }
    }

    /// <summary>Whether the program is a Microsoft-signed file in the Windows folder, and the dependents that are not disabled (with display names from the full list).</summary>
    private (bool Trusted, IReadOnlyList<ServiceRef> Enabled, bool Known) Facts(string name, IReadOnlyList<ServiceRef> all)
    {
        try
        {
            var facts = machine.Inspect(name);
            return (facts.ImageTrusted, facts.EnabledDependents.Select(n => all.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)) ?? new ServiceRef(n, n)).ToList(), true);
        }
        catch (MachineServiceException) { return (false, [], false); }
    }

    private static ServiceStatus? ReadStatus(nint service)
    {
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            if (!QueryServiceStatusEx(service, InfoLevelProcess, buffer, 64, out _)) return null;
            return new ServiceStatus
            {
                ServiceType = (uint)Marshal.ReadInt32(buffer, 0), CurrentState = (uint)Marshal.ReadInt32(buffer, 4), ControlsAccepted = (uint)Marshal.ReadInt32(buffer, 8),
            };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsDelayed(nint service)
    {
        var buffer = Marshal.AllocHGlobal(16);
        try { return QueryServiceConfig2(service, ConfigDelayedAutoStart, buffer, 16, out _) && Marshal.ReadInt32(buffer) != 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>True when the service is started as a protected process (SERVICE_LAUNCH_PROTECTED_INFO is not none). A failed query counts as not protected.</summary>
    private static bool IsLaunchProtected(nint service)
    {
        var buffer = Marshal.AllocHGlobal(16);
        try { return QueryServiceConfig2(service, ConfigLaunchProtected, buffer, 16, out _) && Marshal.ReadInt32(buffer) != 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string DisplayName(nint manager, string name)
    {
        uint size = 512;
        var buffer = new char[size];
        return GetServiceDisplayName(manager, name, buffer, ref size) && size > 0 && size < buffer.Length ? new string(buffer, 0, (int)size) : name;
    }

    /// <summary>All services that depend on this one, and the ones among them that are not stopped. Names come with the display name.</summary>
    /// <remarks>The last value is false when the list could not be read; "no dependents" is then not known.</remarks>
    private static (IReadOnlyList<ServiceRef> All, IReadOnlyList<ServiceRef> Running, bool Known) Dependents(nint service)
    {
        // With a null buffer the call succeeds only when there is nothing to list, and fails with ERROR_MORE_DATA when there is something.
        var probe = EnumDependentServices(service, StateAll, 0, 0, out var needed, out _);
        if (probe) return ([], [], true);
        if (Marshal.GetLastWin32Error() != 234 /* ERROR_MORE_DATA */ || needed is 0 or > 1 << 20) return ([], [], false);
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumDependentServices(service, StateAll, buffer, needed, out _, out var count)) return ([], [], false);
            // ENUM_SERVICE_STATUS: two string pointers and a SERVICE_STATUS of seven DWORDs, padded to pointer size.
            var stride = (2 * nint.Size + 28 + nint.Size - 1) / nint.Size * nint.Size;
            List<ServiceRef> all = [], running = [];
            for (var i = 0; i < count && i < MaxDependents; i++)
            {
                var at = buffer + i * stride;
                var namePointer = Marshal.ReadIntPtr(at, 0);
                var displayPointer = Marshal.ReadIntPtr(at, nint.Size);
                if (namePointer < buffer || namePointer >= buffer + (int)needed - 2) continue;
                if (Marshal.PtrToStringUni(namePointer) is not { Length: > 0 } name) continue;
                var display = displayPointer >= buffer && displayPointer < buffer + (int)needed - 2 ? Marshal.PtrToStringUni(displayPointer) : null;
                var item = new ServiceRef(name, string.IsNullOrWhiteSpace(display) ? name : display);
                all.Add(item);
                if (Marshal.ReadInt32(at, 2 * nint.Size + 4) != 1) running.Add(item);
            }
            return (all, running, true);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static ServiceStartMode StartMode(uint start, bool delayed) => start switch
    {
        0 => ServiceStartMode.Boot, 1 => ServiceStartMode.System, 2 => delayed ? ServiceStartMode.AutomaticDelayed : ServiceStartMode.Automatic,
        3 => ServiceStartMode.Manual, 4 => ServiceStartMode.Disabled, _ => ServiceStartMode.Unknown,
    };

    internal static ServiceRunState RunState(uint state) => state switch
    {
        1 => ServiceRunState.Stopped, 2 => ServiceRunState.StartPending, 3 => ServiceRunState.StopPending, 4 => ServiceRunState.Running,
        5 => ServiceRunState.ContinuePending, 6 => ServiceRunState.PausePending, 7 => ServiceRunState.Paused, _ => ServiceRunState.Unknown,
    };

    /// <summary>The interactive flag is ignored; a per-user service (user own or user share bit) is "other" because Windows creates it again at every sign in.</summary>
    internal static ServiceKind Kind(uint type) => (type & ~0x100u) switch
    {
        0x10 => ServiceKind.Win32Own, 0x20 or 0x30 => ServiceKind.Win32Shared, 0x01 => ServiceKind.KernelDriver, 0x02 => ServiceKind.FileSystemDriver, _ => ServiceKind.Other,
    };

    // ---- changes ----

    public ServiceControlResult SetStartMode(string name, ServiceStartMode mode, ServiceStartMode? expectedBefore = null)
    {
        if (expectedBefore is { } expected && (Observe(name).Service is not { } live || live.StartMode != expected)) return ServiceControlResult.Fail(ServiceCode.Stale);
        byte kind = mode switch
        {
            ServiceStartMode.Automatic => 2, ServiceStartMode.Manual => 3, ServiceStartMode.Disabled => 4, ServiceStartMode.AutomaticDelayed => MachineSnapshot.DelayedStartValue,
            _ => 0,
        };
        if (kind == 0) return ServiceControlResult.Fail(ServiceCode.Failed);
        try
        {
            // Disabling is checked again right here: the dependents are read once more and an unreadable answer refuses.
            if (mode == ServiceStartMode.Disabled)
            {
                if (machine.Inspect(name).EnabledDependents.Count > 0) return ServiceControlResult.Fail(ServiceCode.HasEnabledDependents);
            }
            machine.SetStart(name, kind);
            return ServiceControlResult.Success;
        }
        catch (MachineServiceException e) { return ServiceControlResult.Fail(e.Code == Winora.Domain.ErrorCode.AccessDenied ? ServiceCode.AccessDenied : mode == ServiceStartMode.Disabled ? ServiceCode.FactsUnreadable : ServiceCode.Failed); }
    }

    public ServiceControlResult Start(string name)
    {
        var o = Open(name, StartRight | QueryStatus);
        if (!o.Ok) return Fail(o.Error, start: true, name);
        try { return StartService(o.Service, 0, 0) ? ServiceControlResult.Success : Fail(Marshal.GetLastWin32Error(), start: true, name); }
        finally { Close(o); }
    }

    public ServiceControlResult Stop(string name)
    {
        var o = Open(name, StopRight | QueryStatus | EnumerateDependents);
        if (!o.Ok) return Fail(o.Error, start: false, name);
        try { return ControlService(o.Service, ControlStop, out _) ? ServiceControlResult.Success : Fail(Marshal.GetLastWin32Error(), start: false, name); }
        finally { Close(o); }
    }

    private ServiceControlResult Fail(int error, bool start, string name)
    {
        switch (error)
        {
            case ErrorAccessDenied: return ServiceControlResult.Fail(ServiceCode.AccessDenied);
            case ErrorServiceDoesNotExist: return ServiceControlResult.Fail(ServiceCode.NotFound);
            case ErrorDependentRunning: return ServiceControlResult.Fail(ServiceCode.HasRunningDependents, Observe(name).Service?.RunningDependents);
            case ErrorRequestTimeout: return ServiceControlResult.Fail(start ? ServiceCode.StartTimeout : ServiceCode.StopTimeout);
            case ErrorAlreadyRunning: return ServiceControlResult.Fail(ServiceCode.AlreadyRunning);
            case ErrorDisabled: return ServiceControlResult.Fail(ServiceCode.Disabled);
            case ErrorCannotAcceptControl: return ServiceControlResult.Fail(start ? ServiceCode.Busy : ServiceCode.CannotStop);
            case ErrorNotActive: return ServiceControlResult.Fail(ServiceCode.NotRunning);
            case ErrorLockedDatabase: return ServiceControlResult.Fail(ServiceCode.Busy);
            case ErrorDependencyFailed or ErrorDependencyMissing: return ServiceControlResult.Fail(ServiceCode.DependencyFailed);
            default: return ServiceControlResult.Fail(error is 1054 or 1064 or 1066 or 1067 or 1069 or 1072 ? ServiceCode.Refused : ServiceCode.Failed);
        }
    }
}
