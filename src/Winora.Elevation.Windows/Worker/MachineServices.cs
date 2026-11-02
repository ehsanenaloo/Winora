// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol.Definitions;

namespace Winora.Elevation.Windows.Worker;

/// <summary>A service-control fault with the catalog code the worker reports.</summary>
public sealed class MachineServiceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>What the rules for a service need to know: whether it exists, whether its image is a Microsoft-signed file in the Windows folder, and which other services that are not disabled depend on it.</summary>
public sealed record ServiceFacts(bool Exists, bool ImageTrusted, IReadOnlyList<string> EnabledDependents)
{
    public static ServiceFacts Missing { get; } = new(false, false, []);
}

/// <summary>Why a change of a service start type is refused before anything is written.</summary>
public enum ServiceRefusal { None, MissingService, HasEnabledDependents, ImageNotTrusted }

/// <summary>
/// The service rules of DEC-IW-033, shared by the worker handler and the in-process backend. A service whose image is not a Microsoft-signed file in the Windows folder is never
/// changed (the names in <see cref="DefinitionInvariants.NonMicrosoftImageExceptions"/> are the only exception). A service that another enabled service depends on is never
/// disabled.
/// </summary>
public static class ServicePolicy
{
    public static ServiceRefusal Check(IMachineServices services, string serviceName, byte desiredStart)
    {
        var facts = services.Inspect(serviceName);
        if (!facts.Exists) return ServiceRefusal.MissingService;
        var excepted = DefinitionInvariants.NonMicrosoftImageExceptions.Contains(serviceName, StringComparer.OrdinalIgnoreCase);
        if (!facts.ImageTrusted && !excepted) return ServiceRefusal.ImageNotTrusted;
        if (desiredStart == 4 && facts.EnabledDependents.Count > 0) return ServiceRefusal.HasEnabledDependents;
        return ServiceRefusal.None;
    }
}

/// <summary>
/// The only service access of the worker (trust.md 7.1 item 3). It reads one service's start type and changes it among automatic (2), manual (3), disabled (4) and automatic with delayed
/// start (5, a virtual value: start type 2 plus the delayed flag). There is no create, start, stop, delete or any other change, and no path or account is touched.
/// </summary>
public interface IMachineServices
{
    /// <summary>The service's snapshot: missing service, or its start kind and delayed flag. A driver or an unreadable configuration throws <c>setting.state_unrecognized</c>.</summary>
    MachineSnapshot Read(string serviceName);

    /// <summary>Sets the start type of an existing service to 2, 3, 4 or 5 (automatic delayed) and nothing else.</summary>
    void SetStart(string serviceName, byte startKind);

    /// <summary>The facts the service rules need. Read-only.</summary>
    ServiceFacts Inspect(string serviceName);
}

/// <summary>The real service control manager. Only the worker and the administrator Desktop app construct it; tests use their own <see cref="IMachineServices"/>.</summary>
public sealed unsafe class WindowsMachineServices : IMachineServices
{
    private const uint OwnProcess = 0x10, ShareProcess = 0x20, InteractiveFlag = 0x100;

    public MachineSnapshot Read(string serviceName)
    {
        var (manager, service) = Open(serviceName, Native.ServiceQueryConfig, out var exists);
        if (!exists) return MachineSnapshot.Service(false, 0);
        try
        {
            var buffer = new byte[8192];
            fixed (byte* p = buffer)
                if (!Native.QueryServiceConfig(service, p, (uint)buffer.Length, out _)) throw Fault();
            var type = BitConverter.ToUInt32(buffer, 0) & ~InteractiveFlag;
            var start = BitConverter.ToUInt32(buffer, 4);
            if (type is not (OwnProcess or ShareProcess or (OwnProcess | ShareProcess)) || start > 4) throw new MachineServiceException(ErrorCode.SettingStateUnrecognized);
            return MachineSnapshot.Service(true, (byte)start, start == 2 && IsDelayed(service));
        }
        finally { Native.CloseServiceHandle(service); Native.CloseServiceHandle(manager); }
    }

    public void SetStart(string serviceName, byte startKind)
    {
        if (startKind is < 2 or > MachineSnapshot.DelayedStartValue) throw new ArgumentOutOfRangeException(nameof(startKind));
        var (manager, service) = Open(serviceName, Native.ServiceQueryConfig | Native.ServiceChangeConfig, out var exists);
        if (!exists) throw new MachineServiceException(ErrorCode.SettingStateUnrecognized);
        try
        {
            var start = startKind == MachineSnapshot.DelayedStartValue ? 2u : startKind;
            var delayedNow = IsDelayed(service);
            // The delayed flag is cleared before a service leaves automatic start and set after it entered automatic start, so it is never set on a service that is not automatic.
            if (startKind is 3 or 4 && delayedNow && !SetDelayed(service, false)) throw Fault();
            if (!Native.ChangeServiceConfig(service, Native.ServiceNoChange, start, Native.ServiceNoChange, 0, 0, 0, 0, 0, 0, 0)) throw Fault();
            if (startKind == MachineSnapshot.DelayedStartValue && !delayedNow && !SetDelayed(service, true)) throw Fault();
            if (startKind == 2 && delayedNow && !SetDelayed(service, false)) throw Fault();
        }
        finally { Native.CloseServiceHandle(service); Native.CloseServiceHandle(manager); }
    }

    public ServiceFacts Inspect(string serviceName)
    {
        var (manager, service) = Open(serviceName, Native.ServiceQueryConfig | Native.ServiceEnumerateDependents, out var exists);
        if (!exists) return ServiceFacts.Missing;
        try
        {
            var path = BinaryPath(service);
            var trusted = path is not null && ServiceImage.IsMicrosoftSignedInWindows(path);
            var enabled = new List<string>();
            foreach (var name in DependentNames(service))
            {
                var (m, dependent) = Open(name, Native.ServiceQueryConfig, out var dependentExists);
                if (!dependentExists) continue;
                try { if (StartType(dependent) != 4) enabled.Add(name); }
                finally { Native.CloseServiceHandle(dependent); Native.CloseServiceHandle(m); }
            }
            return new(true, trusted, enabled);
        }
        finally { Native.CloseServiceHandle(service); Native.CloseServiceHandle(manager); }
    }

    private static string? BinaryPath(nint service)
    {
        var buffer = new byte[8192];
        fixed (byte* p = buffer)
        {
            if (!Native.QueryServiceConfig(service, p, (uint)buffer.Length, out _)) throw Fault();
            var pointer = *(nint*)(p + (nint.Size == 8 ? 16 : 12));
            if (pointer < (nint)p || pointer >= (nint)(p + buffer.Length - 2)) return null;   // the string must lie inside the buffer
            return Marshal.PtrToStringUni(pointer);
        }
    }

    private static uint StartType(nint service)
    {
        var buffer = new byte[8192];
        fixed (byte* p = buffer)
            if (!Native.QueryServiceConfig(service, p, (uint)buffer.Length, out _)) throw Fault();
        return BitConverter.ToUInt32(buffer, 4);
    }

    private static List<string> DependentNames(nint service)
    {
        var names = new List<string>();
        Native.EnumDependentServices(service, Native.ServiceStateAll, null, 0, out var needed, out var count);
        if (needed is 0 or > 1 << 20) return names;
        var buffer = new byte[needed];
        fixed (byte* p = buffer)
        {
            if (!Native.EnumDependentServices(service, Native.ServiceStateAll, p, needed, out _, out count)) throw Fault();
            var stride = (2 * nint.Size + 28 + nint.Size - 1) / nint.Size * nint.Size;   // ENUM_SERVICE_STATUS: two pointers and a SERVICE_STATUS, padded
            for (var i = 0; i < count && i < 4096; i++)
            {
                var pointer = *(nint*)(p + i * stride);
                if (pointer < (nint)p || pointer >= (nint)(p + buffer.Length - 2)) continue;
                if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } name) names.Add(name);
            }
        }
        return names;
    }

    private static bool IsDelayed(nint service)
    {
        var buffer = new byte[16];
        fixed (byte* p = buffer)
            if (!Native.QueryServiceConfig2(service, Native.ServiceConfigDelayedAutoStartInfo, p, (uint)buffer.Length, out _)) throw Fault();
        return BitConverter.ToInt32(buffer, 0) != 0;
    }

    private static bool SetDelayed(nint service, bool delayed)
    {
        int flag = delayed ? 1 : 0;
        return Native.ChangeServiceConfig2(service, Native.ServiceConfigDelayedAutoStartInfo, (byte*)&flag);
    }

    private static (nint Manager, nint Service) Open(string name, uint access, out bool exists)
    {
        exists = false;
        var manager = Native.OpenSCManager(null, null, Native.ScManagerConnect);
        if (manager == 0) throw Fault();
        var service = Native.OpenService(manager, name, access);
        if (service == 0)
        {
            var error = Marshal.GetLastWin32Error();
            Native.CloseServiceHandle(manager);
            if (error == Native.ErrorServiceDoesNotExist) return (0, 0);
            throw new MachineServiceException(ErrorCode.AccessDenied);
        }
        exists = true;
        return (manager, service);
    }

    private static MachineServiceException Fault() => new(ErrorCode.AccessDenied);
}
