// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Domain;

/// <summary>Read-only discovery results. Platform neutral; adapters fill them, hosts display them. Nothing here mutates Windows.</summary>
public enum StartupSource { RegistryRun, RegistryRunOnce, StartupFolder, ScheduledTask }
public enum StartupApproval { Unknown, Enabled, Disabled }
/// <summary>The StartupApproved sub key that holds the enabled flag of an entry: Run (HKCU or the 64-bit HKLM Run key), Run32 (the 32-bit HKLM Run key) or StartupFolder.</summary>
public enum StartupApprovalStore { Run, Run32, StartupFolder }
public enum TaskRunState { Unknown, Ready, Running, Disabled, Queued }
public enum ServiceStartMode { Unknown, Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }
public enum ServiceRunState { Unknown, Stopped, StartPending, StopPending, Running, ContinuePending, PausePending, Paused }
public enum ServiceKind { Unknown, Win32Own, Win32Shared, KernelDriver, FileSystemDriver, Other }
/// <summary>BuiltIn: one of the three plans whose GUIDs Windows defines (power saver, balanced, high performance). Custom: any other plan.</summary>
public enum PowerPlanKind { BuiltIn, Custom }

/// <summary>Stable id is source-qualified and case-folded, never a translated label.</summary>
/// <summary>Where the enabled flag of a startup entry lives: the hive follows the scope, the value name is the Run value name or the shortcut file name with its extension.</summary>
public sealed record StartupLocation(StartupApprovalStore Store, string ValueName);

public sealed record StartupItem(string StableId, string Name, string Command, StartupSource Source, InstallationScope Scope,
    StartupApproval Approval, ResourceIdentity Resource, string? Publisher = null, StartupLocation? Location = null);
public sealed record ScheduledTaskInfo(string StableId, string Path, string Name, TaskRunState State, bool Hidden,
    string? Author, DateTimeOffset? LastRunUtc, DateTimeOffset? NextRunUtc, bool RunsAtLogonOrBoot, string? Command, bool IsVendorMicrosoft);
public sealed record ServiceInfo(string StableId, string DisplayName, ServiceStartMode StartMode, ServiceRunState RunState, ServiceKind Kind,
    string? Account, string? ImagePath, string? Description);

/// <summary>Stable id is the lower-case plan GUID without braces; the name is the friendly name Windows stores for the plan.</summary>
public sealed record PowerPlanInfo(string StableId, string Name, bool IsActive, PowerPlanKind Kind);

/// <summary>One item or one source that could not be read. Detail is safe text (codes, key names); never a raw exception message.</summary>
public sealed record DiscoveryIssue(string Source, WinoraError Error);
public sealed record DiscoveryResult<T>(IReadOnlyList<T> Items, IReadOnlyList<DiscoveryIssue> Issues, DateTimeOffset CollectedAtUtc)
{
    public bool IsComplete => Issues.Count == 0;
    public static DiscoveryResult<T> Empty(DateTimeOffset now) => new([], [], now);
}
