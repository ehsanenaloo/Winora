// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;

namespace Winora.Application;

/// <summary>One trigger of a task. <see cref="Kind"/> is a stable key (time, daily, weekly, monthly, monthly_dow, idle, registration, boot, logon, session_state, event, custom). <see cref="At"/> is the start time of day as HH:mm when the trigger has one.</summary>
public sealed record TaskTriggerInfo(string Kind, bool Enabled, string? At = null);

/// <summary>One action of a task. <see cref="Kind"/> is exec, com_handler, email, message or other; <see cref="Command"/> and <see cref="Arguments"/> are set for exec only.</summary>
public sealed record TaskActionInfo(string Kind, string? Command, string? Arguments);

/// <summary>Everything the manager shows about one task. Read as text only; nothing here starts a task.</summary>
public sealed record ScheduledTaskEntry(string Path, string Name, TaskRunState State, bool Enabled, bool Hidden, string? Author, string? Description,
    DateTimeOffset? LastRunUtc, DateTimeOffset? NextRunUtc, int? LastResult, string? RunAs, bool RunsElevated, IReadOnlyList<TaskTriggerInfo> Triggers,
    IReadOnlyList<TaskActionInfo> Actions, int XmlLength)
{
    public bool RunsAtLogonOrBoot => Triggers.Any(t => t.Kind is "logon" or "boot");
}

/// <summary>A listed task with the verdict of the safety policy for it.</summary>
public sealed record ScheduledTaskItem(ScheduledTaskEntry Entry, TaskPolicyInfo Policy);

/// <summary>The current state of one task, read just before a change. <see cref="Xml"/> is the task definition as Task Scheduler stores it.</summary>
public sealed record TaskObservation(bool Enabled, TaskRunState State, string Xml);

/// <summary>The answer of a single task lookup: <see cref="Code"/> is ok, task.not_found, task.access_denied or task.failed.</summary>
public sealed record TaskLookup(string Code, TaskObservation? Task)
{
    public bool Found => Task is not null;
    public static TaskLookup Of(TaskObservation task) => new(ScheduledTaskCode.Ok, task);
    public static TaskLookup Missing { get; } = new(ScheduledTaskCode.NotFound, null);
    public static TaskLookup Fail(string code) => new(code, null);
}

public sealed record TaskControlResult(bool Ok, string Code)
{
    public static TaskControlResult Success { get; } = new(true, ScheduledTaskCode.Ok);
    public static TaskControlResult Fail(string code) => new(false, code);
}

/// <summary>Read side of Task Scheduler.</summary>
public interface IScheduledTaskCatalog
{
    /// <summary>All tasks, including hidden ones. A folder or task that cannot be read becomes an issue; the rest is still returned.</summary>
    Task<DiscoveryResult<ScheduledTaskEntry>> ListAsync(CancellationToken cancellationToken);
    TaskLookup Observe(string path);
}

/// <summary>Write side of Task Scheduler. Every call works on one task path and uses the Task Scheduler API only; no command line is built.</summary>
public interface IScheduledTaskControl
{
    /// <param name="expectedFingerprint">The fingerprint of the definition that was reviewed; a task with another definition at the moment of the change is refused as stale.</param>
    TaskControlResult SetEnabled(string path, bool enabled, string? expectedFingerprint = null);
    TaskControlResult Run(string path);
    TaskControlResult Stop(string path);
    TaskControlResult Delete(string path, string? expectedFingerprint = null);
    /// <summary>Registers a task from its definition. Fails with task.exists when the path is taken; never replaces a task.</summary>
    TaskControlResult Register(string path, string xml);
}

/// <summary>Copies of task definitions that Winora deleted. Names are random ids, never derived from the task.</summary>
public interface IScheduledTaskBackups
{
    /// <summary>Stores the definition durably and returns its id. Throws <see cref="IOException"/> when it cannot be stored.</summary>
    string Save(string xml);
    string? Load(string id);
}

public enum ScheduledTaskChangeKind { Enable, Disable, Run, Stop, Delete, Restore }

/// <summary>Dismissed: the person closed a record whose outcome was never confirmed. It has no undo.</summary>
public enum ScheduledTaskChangeState { Applied, Failed, Refused, Dismissed }

/// <summary>
/// One change, recorded before it is made. <see cref="BeforeFingerprint"/> identifies the definition the review showed; <see cref="AfterFingerprint"/> is set when the change ends and
/// identifies the state Winora left, so an undo can tell that nobody changed the task since. <see cref="RevertOf"/> names the change this one undoes. <see cref="IdentityFingerprint"/> identifies
/// the definition apart from its enabled flag (enable and disable only): a record without one cannot be closed as applied from the live state.
/// </summary>
public sealed record ScheduledTaskChange(Guid Id, ScheduledTaskChangeKind Kind, string Path, string Name, bool? BeforeEnabled, bool? AfterEnabled, string? BackupId,
    string? BeforeFingerprint, string? AfterFingerprint, Guid? RevertOf, string? IdentityFingerprint = null);

/// <summary>A journal entry. A record without an end is a change that was interrupted.</summary>
/// <param name="Written">The journal holds the line that says the write was made (after the write, before the end line).</param>
public sealed record ScheduledTaskChangeRecord(ScheduledTaskChange Change, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, ScheduledTaskChangeState? State, string Code, bool Written = false);

/// <summary>Append-only record of task changes. <see cref="Begin"/> must be durable before it returns.</summary>
public interface IScheduledTaskJournal
{
    void Begin(ScheduledTaskChange change, DateTimeOffset atUtc);
    void Complete(Guid id, ScheduledTaskChangeState state, string code, string? afterFingerprint, DateTimeOffset atUtc);
    /// <summary>Records that the write of the change was made. Called after the write returned and before the end line.</summary>
    void Written(Guid id, DateTimeOffset atUtc) { }
    /// <summary>The newest changes first.</summary>
    IReadOnlyList<ScheduledTaskChangeRecord> History(int max);
}

/// <summary>What the user asks for. <see cref="ExpectedFingerprint"/> is the definition the review showed; when it changed in between, nothing is written.</summary>
public sealed record ScheduledTaskRequest(ScheduledTaskChangeKind Kind, string Path, string? ExpectedFingerprint = null, string? BackupId = null, string? BackupFingerprint = null);

/// <summary>What would happen. <see cref="RefusalCode"/> is set when the change cannot be made; the other fields are then not meaningful.</summary>
public sealed record ScheduledTaskPreview(ScheduledTaskRequest Request, string Name, TaskPolicyInfo Policy, bool? BeforeEnabled, bool? AfterEnabled, TaskRunState State,
    string? Fingerprint, bool WillChange, string? RefusalCode)
{
    public bool IsRefused => RefusalCode is not null;
}

public sealed record ScheduledTaskApplyResult(ScheduledTaskChangeState State, string Code, ScheduledTaskChange? Change, bool RecordLost = false)
{
    public bool Ok => State == ScheduledTaskChangeState.Applied;
}

/// <summary>Stable codes of task changes.</summary>
public static class ScheduledTaskCode
{
    public const string Ok = "ok";
    public const string NotFound = "task.not_found";
    public const string InvalidPath = "task.invalid_path";
    public const string Protected = "task.protected";
    public const string NotOptional = "task.not_optional";
    public const string MicrosoftDelete = "task.microsoft_delete";
    public const string AlreadySet = "task.already_set";
    public const string Stale = "task.stale";
    public const string InterruptedUnchanged = "task.interrupted_unchanged";
    public const string Reconciled = "task.reconciled";
    public const string Dismissed = "task.dismissed";
    public const string ResolvedRestored = "task.resolved_restored";
    public const string NotUnconfirmed = "task.not_unconfirmed";
    public const string IsRunning = "task.is_running";
    public const string NotRunning = "task.not_running";
    public const string Disabled = "task.disabled";
    public const string Exists = "task.exists";
    public const string CannotRevert = "task.cannot_revert";
    public const string BackupFailed = "task.backup_failed";
    public const string BackupMissing = "task.backup_missing";
    public const string BackupInvalid = "task.backup_invalid";
    public const string VerifyFailed = "task.verify_failed";
    public const string AccessDenied = "task.access_denied";
    public const string Failed = "task.failed";
    public const string JournalFailed = "task.journal_failed";
}
