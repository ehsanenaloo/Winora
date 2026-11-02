// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;

namespace Winora.Application;

/// <summary>A service by its short name and the name people see.</summary>
public sealed record ServiceRef(string Name, string DisplayName);

/// <summary>
/// Everything a change needs to know about one service, read just before the change. <see cref="ImageTrusted"/> is true when the program is a Microsoft-signed file in the Windows folder.
/// <see cref="RunningDependents"/> are the services that depend on this one and run now; <see cref="EnabledDependents"/> are the dependents that are not disabled.
/// </summary>
/// <remarks><see cref="SafetyFactsKnown"/> is false when the dependents could not be read; an empty list then means unknown, not none. <see cref="LaunchProtected"/> is true when Windows starts the service as a protected process (anti-malware services registered with an early-launch driver use this).</remarks>
public sealed record ServiceObservation(string Name, string DisplayName, ServiceStartMode StartMode, ServiceRunState RunState, ServiceKind Kind, string? ImagePath,
    bool ImageTrusted, bool AcceptsStop, IReadOnlyList<ServiceRef> RunningDependents, IReadOnlyList<ServiceRef> EnabledDependents, bool LaunchProtected = false, bool SafetyFactsKnown = true);

/// <summary>The answer of a single service lookup: <see cref="Code"/> is ok, service.not_found, service.access_denied or service.failed.</summary>
public sealed record ServiceLookup(string Code, ServiceObservation? Service)
{
    public bool Found => Service is not null;
    public static ServiceLookup Of(ServiceObservation service) => new(ServiceCode.Ok, service);
    public static ServiceLookup Missing { get; } = new(ServiceCode.NotFound, null);
    public static ServiceLookup Fail(string code) => new(code, null);
}

/// <summary>The result of one call to the service manager. <see cref="Names"/> lists the services that block a stop.</summary>
public sealed record ServiceControlResult(bool Ok, string Code, IReadOnlyList<ServiceRef>? Names = null)
{
    public static ServiceControlResult Success { get; } = new(true, ServiceCode.Ok);
    public static ServiceControlResult Fail(string code, IReadOnlyList<ServiceRef>? names = null) => new(false, code, names);
}

/// <summary>
/// The Service Control Manager. Every call works on one service name. The Windows adapter keeps all start, stop and configuration calls in one file; the service in this
/// layer has already checked the safety policy, the journal and the compare-and-set.
/// </summary>
public interface IServiceControl
{
    ServiceLookup Observe(string name);
    /// <summary>Sets the start type to Automatic, AutomaticDelayed, Manual or Disabled and nothing else.</summary>
    /// <param name="expectedBefore">The start mode the person reviewed; the change is refused as stale when the service has another one at the moment of the write.</param>
    ServiceControlResult SetStartMode(string name, ServiceStartMode mode, ServiceStartMode? expectedBefore = null);
    /// <summary>Asks the service manager to start the service and returns when it answers. The caller waits for the state.</summary>
    ServiceControlResult Start(string name);
    /// <summary>Asks the service to stop. Never stops dependents.</summary>
    ServiceControlResult Stop(string name);
}

public enum ServiceChangeKind { SetStartMode, Start, Stop, Restart }

/// <summary>Dismissed: the person closed a record whose outcome was never confirmed. It has no undo.</summary>
public enum ServiceChangeState { Applied, Failed, Refused, Dismissed }

/// <summary>
/// One change, recorded before it is made. <see cref="BeforeFingerprint"/> identifies what the review showed; <see cref="AfterFingerprint"/> is set when the change ends and identifies
/// the configuration Winora left, so an undo can tell that nobody changed the startup type since. <see cref="RevertOf"/> names the change this one undoes.
/// </summary>
public sealed record ServiceChange(Guid Id, ServiceChangeKind Kind, string Name, string DisplayName, ServiceStartMode? BeforeMode, ServiceStartMode? AfterMode,
    ServiceRunState BeforeState, string? BeforeFingerprint, string? AfterFingerprint, Guid? RevertOf);

/// <summary>A journal entry. A record without an end is a change that was interrupted.</summary>
/// <param name="Written">The journal holds the line that says the write was made (after the write, before the end line).</param>
public sealed record ServiceChangeRecord(ServiceChange Change, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, ServiceChangeState? State, string Code, bool Written = false);

/// <summary>Append-only record of service changes. <see cref="Begin"/> must be durable before it returns.</summary>
public interface IServiceJournal
{
    void Begin(ServiceChange change, DateTimeOffset atUtc);
    void Complete(Guid id, ServiceChangeState state, string code, string? afterFingerprint, DateTimeOffset atUtc);
    /// <summary>Records that the write of the change was made. Called after the write returned and before the end line.</summary>
    void Written(Guid id, DateTimeOffset atUtc) { }
    /// <summary>The newest changes first.</summary>
    IReadOnlyList<ServiceChangeRecord> History(int max);
}

/// <summary>What the user asks for. <paramref name="Mode"/> is the new startup type of a <see cref="ServiceChangeKind.SetStartMode"/>. <paramref name="ExpectedFingerprint"/> is what the review showed.</summary>
public sealed record ServiceRequest(ServiceChangeKind Kind, string Name, ServiceStartMode? Mode = null, string? ExpectedFingerprint = null);

/// <summary>What would happen. <see cref="RefusalCode"/> is set when the change cannot be made; <see cref="BlockedBy"/> then names the services in the way (dependents).</summary>
public sealed record ServicePreview(ServiceRequest Request, string DisplayName, ServicePolicyInfo Policy, ServiceStartMode BeforeMode, ServiceStartMode? AfterMode,
    ServiceRunState State, RiskLevel Risk, string? Fingerprint, bool WillChange, string? RefusalCode, IReadOnlyList<ServiceRef> BlockedBy, IReadOnlyList<ServiceRef> RunningDependents)
{
    public bool IsRefused => RefusalCode is not null;
}

public sealed record ServiceApplyResult(ServiceChangeState State, string Code, ServiceChange? Change, IReadOnlyList<ServiceRef> Names, bool RecordLost = false)
{
    public bool Ok => State == ServiceChangeState.Applied;
}

/// <summary>A service of the machine definitions with the type Winora suggests and the risk the definition gives it.</summary>
public sealed record ServiceRecommendation(string ServiceName, string DefinitionId, RiskLevel Risk, ServiceStartMode Suggested, string SourceClass);

/// <summary>Stable codes of service changes.</summary>
public static class ServiceCode
{
    public const string Ok = "ok";
    public const string NotFound = "service.not_found";
    public const string InvalidName = "service.invalid_name";
    public const string Protected = "service.protected";
    public const string Driver = "service.driver";
    public const string BootCritical = "service.boot_critical";
    public const string NotControllable = "service.not_controllable";
    public const string AlreadySet = "service.already_set";
    public const string Stale = "service.stale";
    public const string InterruptedUnchanged = "service.interrupted_unchanged";
    public const string Reconciled = "service.reconciled";
    public const string Dismissed = "service.dismissed";
    public const string ResolvedRestored = "service.resolved_restored";
    public const string NotUnconfirmed = "service.not_unconfirmed";
    public const string AlreadyRunning = "service.already_running";
    public const string NotRunning = "service.not_running";
    public const string Busy = "service.busy";
    public const string Disabled = "service.disabled";
    public const string CannotStop = "service.cannot_stop";
    public const string HasRunningDependents = "service.has_running_dependents";
    public const string HasEnabledDependents = "service.has_enabled_dependents";
    public const string FactsUnreadable = "service.facts_unreadable";
    public const string StopTimeout = "service.stop_timeout";
    public const string StartTimeout = "service.start_timeout";
    public const string Refused = "service.refused";
    public const string DependencyFailed = "service.dependency_failed";
    public const string RestartStartFailed = "service.restart_start_failed";
    public const string CannotRevert = "service.cannot_revert";
    public const string VerifyFailed = "service.verify_failed";
    public const string AccessDenied = "service.access_denied";
    public const string Failed = "service.failed";
    public const string JournalFailed = "service.journal_failed";
}
