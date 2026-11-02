// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>The digests of A.5 for the executor, delegating to the protocol library.</summary>
public sealed class MachineDigests : IMachineDigests
{
    public Bytes32 PlanTokenApply(string definitionId, uint version, string choiceId, MachineSnapshot expected, ulong keyRevision) =>
        Digests.PlanTokenApply(definitionId, version, choiceId, expected, keyRevision);
    public Bytes32 PlanTokenRestore(string definitionId, uint version, MachineSnapshot recordedAfter, ulong recordedRevision) =>
        Digests.PlanTokenRestore(definitionId, version, recordedAfter, recordedRevision);
    public Bytes32 BatchDigest(BatchBegin batch) => Digests.BatchDigest(batch);
}

/// <summary>
/// UNELEVATED, read-only access to the machine registry for the executor (: a definition is admitted only when its state is readable by a
/// standard user). It opens one exact key, in the view the definition names, with query rights only. The key revision is the key's last-write FILETIME.
/// A value that is not a DWORD, QWORD or a bounded string is <c>setting.state_unrecognized</c>: it is not representable and is never changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineStateReader : IMachineStateReader
{
    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr cls, IntPtr clsLen, IntPtr reserved, IntPtr subKeys, IntPtr maxSubKey,
        IntPtr maxClass, IntPtr values, IntPtr maxValueName, IntPtr maxValue, IntPtr security, out long lastWrite);

    public MachineStateRead Read(MachineSettingDefinition definition)
    {
        if (definition.TargetKind == MachineTargetKind.ServiceStartType && definition.Service is { } service) return ReadService(RegistryHive.LocalMachine, ServicesKey, service.ServiceName);
        if (definition.TargetKind == MachineTargetKind.DisplayDriverValue) return MachineStateRead.Failed(ErrorCode.TargetKindUnsupported);
        if (definition.Registry is not { } target || definition.TargetKind != MachineTargetKind.RegistryValue) return MachineStateRead.Failed(ErrorCode.TargetNotAllowed);
        return ReadValue(RegistryHive.LocalMachine, target.SubKey, target.ValueName, target.View);
    }

    internal const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>
    /// Reads a service's start type from its service key (display only; the worker re-reads it from the service control manager before any change). A missing key is a missing
    /// service. Automatic with the delayed flag is the delayed automatic state. The key revision of a service is always 0.
    /// </summary>
    internal static MachineStateRead ReadService(RegistryHive hiveName, string servicesKey, string serviceName)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(hiveName, RegistryView.Registry64);
            using var key = hive.OpenSubKey(servicesKey + "\\" + serviceName, RegistryRights.QueryValues);
            if (key is null) return MachineStateRead.Of(MachineSnapshot.Service(false, 0), 0);
            if (key.GetValue("Start", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int start || key.GetValueKind("Start") != RegistryValueKind.DWord || start is < 0 or > 4)
                return MachineStateRead.Failed(ErrorCode.SettingStateUnrecognized);
            var isDelayed = start == 2 && key.GetValue("DelayedAutostart") is int delayed && delayed != 0;
            return MachineStateRead.Of(MachineSnapshot.Service(true, (byte)start, isDelayed), 0);
        }
        catch (IOException) { return MachineStateRead.Failed(ErrorCode.DiscoveryItemUnreadable); }
        catch (UnauthorizedAccessException) { return MachineStateRead.Failed(ErrorCode.AccessDenied); }
        catch (SecurityException) { return MachineStateRead.Failed(ErrorCode.AccessDenied); }
    }

    /// <summary>Reads one value into a snapshot. The hive is a parameter so tests can read their own sandbox under HKCU; production passes HKLM only.</summary>
    internal static MachineStateRead ReadValue(RegistryHive hiveName, string subKey, string valueName, RegistryViewKind view)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(hiveName, view == RegistryViewKind.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64);
            using var key = hive.OpenSubKey(subKey, RegistryRights.QueryValues);
            if (key is null) return MachineStateRead.Of(MachineSnapshot.Registry(false, false, view, null), 0);
            var rc = RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var lastWrite);
            if (rc == 5) return MachineStateRead.Failed(ErrorCode.AccessDenied);
            if (rc != 0) return MachineStateRead.Failed(ErrorCode.DiscoveryItemUnreadable);
            var revision = (ulong)lastWrite;
            var data = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (data is null) return MachineStateRead.Of(MachineSnapshot.Registry(true, false, view, null), revision);
            var kind = key.GetValueKind(valueName);
            try
            {
                return kind switch
                {
                    RegistryValueKind.DWord when data is int d => MachineStateRead.Of(MachineSnapshot.Registry(true, true, view, MachineValue.DWord(unchecked((uint)d))), revision),
                    RegistryValueKind.QWord when data is long q => MachineStateRead.Of(MachineSnapshot.Registry(true, true, view, MachineValue.QWord(unchecked((ulong)q))), revision),
                    RegistryValueKind.String when data is string s => MachineStateRead.Of(MachineSnapshot.Registry(true, true, view, MachineValue.String(s)), revision),
                    _ => MachineStateRead.Failed(ErrorCode.SettingStateUnrecognized),
                };
            }
            catch (ArgumentException) { return MachineStateRead.Failed(ErrorCode.SettingStateUnrecognized); }   // too long or not well-formed text
        }
        catch (IOException) { return MachineStateRead.Failed(ErrorCode.DiscoveryItemUnreadable); }
        catch (UnauthorizedAccessException) { return MachineStateRead.Failed(ErrorCode.AccessDenied); }
        catch (SecurityException) { return MachineStateRead.Failed(ErrorCode.AccessDenied); }
    }
}

/// <summary>
/// Policy awareness. A setting is managed when any documented channel of its definition holds a value: a policy registry value at the declared
/// location, or a Policy CSP setting in the policy manager store. A channel that cannot be read counts as managed (fail closed): Winora does not fight management.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachinePolicyReader : IMachinePolicyReader
{
    private const string PolicyManagerDevice = @"SOFTWARE\Microsoft\PolicyManager\current\device";

    public bool IsManagedByPolicy(MachineSettingDefinition definition)
    {
        foreach (var channel in definition.ManagedBy)
        {
            var (subKey, valueName) = channel.Kind switch
            {
                ManagedByKind.PolicyRegistryValue => (channel.Location, channel.ValueName),
                ManagedByKind.PolicyCsp => CspLocation(channel.Location),
                _ => (null, null),
            };
            if (subKey is null || valueName is null) return true;
            var read = WindowsMachineStateReader.ReadValue(RegistryHive.LocalMachine, subKey, valueName, RegistryViewKind.Registry64);
            if (!read.Ok) return true;
            if (read.Snapshot!.TryReadRegistry(out _, out _, out var value) && value.Kind != MachineValueKind.Absent) return true;
        }
        return false;
    }

    /// <summary>Policy CSP names are <c>Area/Setting</c>; the effective value lives under <c>PolicyManager\current\device\Area</c> as the value <c>Setting</c>.</summary>
    internal static (string? SubKey, string? ValueName) CspLocation(string location)
    {
        var parts = location.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiLetterOrDigit))
            ? (PolicyManagerDevice + "\\" + parts[0], parts[1])
            : (null, null);
    }
}

/// <summary>Identifier of the current boot: the prefetcher's boot counter, which Windows increments at every start. Falls back to a coarse boot time if it is unreadable.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBootSession : IBootSession
{
    private readonly Lazy<string> id = new(Compute);
    public string Id => id.Value;

    private static string Compute()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", false);
            if (key?.GetValue("BootId") is int boot) return "boot-" + boot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { /* fall through to the time-based identifier */ }
        var started = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return "up-" + (started.ToUnixTimeSeconds() / 120).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The worker-gone probe of: a worker holds <c>&lt;InstallRoot&gt;\run\worker.lock</c> with no sharing. A read-sharing test open that fails with a sharing violation
/// means a worker is alive; success or a missing file means gone; anything else is inconclusive and recovery defers.
/// </summary>
public sealed class WorkerLockProbe(string runDirectory) : IWorkerPresence
{
    public const string LockFileName = "worker.lock";
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    public WorkerPresence Probe()
    {
        var path = Path.Combine(runDirectory, LockFileName);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return WorkerPresence.Gone;
        }
        catch (FileNotFoundException) { return WorkerPresence.Gone; }
        catch (DirectoryNotFoundException) { return WorkerPresence.Gone; }
        catch (IOException e) when (e.HResult is SharingViolation or LockViolation) { return WorkerPresence.Alive; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { return WorkerPresence.Inconclusive; }
    }
}
