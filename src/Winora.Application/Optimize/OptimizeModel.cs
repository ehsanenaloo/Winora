// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;
namespace Winora.Application;

/// <summary>The state of one target as read just before a change: its snapshot and the revision of its key. A change is written only if both are still the same.</summary>
public sealed record MachineObservation(MachineSnapshot Snapshot, ulong KeyRevision);

public sealed record MachineObserveResult(MachineObservation? Observation, string? Code)
{
    public bool Ok => Observation is not null;
}

public sealed record MachineWriteResult(bool Ok, string Code, MachineSnapshot? After, ulong KeyRevision);

/// <summary>
/// Reads and writes one machine setting of the verified catalog. The backend resolves the target, the choices and the restore domain from the definition, never from free text, writes
/// only after comparing with what was observed, flushes and reads back.
/// </summary>
public interface IMachineSettingsBackend
{
    /// <summary>False when this process cannot write machine settings (it has no administrator rights).</summary>
    bool CanWrite { get; }
    /// <param name="adapter">The adapter of a display driver definition; null for every other kind.</param>
    MachineObserveResult Observe(MachineSettingDefinition definition, DisplayAdapter? adapter = null);
    MachineWriteResult Apply(MachineSettingDefinition definition, string choiceId, MachineObservation observed, DisplayAdapter? adapter = null);
    MachineWriteResult Restore(MachineSettingDefinition definition, MachineValue value, MachineObservation observed, DisplayAdapter? adapter = null);
}

/// <summary>A value as the journal stores it: the kind and the payload as plain data.</summary>
public sealed record StoredValue(string Kind, ulong? Number, string? Text)
{
    public static StoredValue From(MachineValue value) => new(value.Kind.ToString(), value.Number, value.Text);

    public MachineValue? ToValue() => Kind switch
    {
        nameof(MachineValueKind.Absent) => MachineValue.Absent,
        nameof(MachineValueKind.DWord) when Number is <= uint.MaxValue => MachineValue.DWord((uint)Number.Value),
        nameof(MachineValueKind.QWord) when Number is not null => MachineValue.QWord(Number.Value),
        nameof(MachineValueKind.String) when Text is not null => MachineValue.String(Text),
        _ => null,
    };

    public bool Matches(MachineValue value) => ToValue() is { } own && own.Equals(value);
}

public enum OptimizeChangeKind { Apply, Restore }

/// <summary>Dismissed: the person closed a record whose outcome was never confirmed (see <see cref="OptimizeService.DismissAsync"/>). It has no undo.</summary>
public enum OptimizeState { Applied, Failed, Refused, Dismissed }

/// <summary>One change of a machine setting, recorded before it is made. Group and Label are set when several settings are changed together as one named group with one undo; older lines have neither. <see cref="Before"/> is the value that undoing it puts back; <see cref="RevertOf"/> names the change this one undoes.</summary>
/// <param name="TargetInstanceId">For a display driver setting: the device instance id of the adapter this change was made on.</param>
/// <param name="TargetName">For a display driver setting: the friendly name of the adapter.</param>
/// <param name="TargetKey">For a display driver setting: the driver key (below HKLM) that was resolved for the adapter and written.</param>
public sealed record OptimizeChange(Guid Id, string DefinitionId, int Version, OptimizeChangeKind Kind, string? ChoiceId, StoredValue Before, StoredValue After, Guid? RevertOf,
    string? TargetInstanceId = null, string? TargetName = null, string? TargetKey = null, Guid? Group = null, string? Label = null);

/// <param name="Written">The journal holds the line that says the write was made (it comes after the write and before the end line). A record without an end line but with this line was written by Winora.</param>
public sealed record OptimizeRecord(OptimizeChange Change, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, OptimizeState? State, string Code, bool Written = false);

public interface IOptimizeJournal
{
    void Begin(OptimizeChange change, DateTimeOffset atUtc);
    void Complete(Guid id, OptimizeState state, string code, DateTimeOffset atUtc);
    /// <summary>Records that the write of the change was made. Called after the write returned and before the end line.</summary>
    void Written(Guid id, DateTimeOffset atUtc) { }
    /// <summary>The newest changes first.</summary>
    IReadOnlyList<OptimizeRecord> History(int max);
}

/// <summary>A setting of the catalog as it stands on this PC.</summary>
/// <param name="Problem">A stable code when the setting cannot be offered now (unsupported build, managed by policy, unreadable state); null when it can.</param>
/// <param name="Adapter">For a display driver setting: the adapter this row stands for. Each adapter is its own item.</param>
public sealed record OptimizeItem(MachineSettingDefinition Definition, MachineValue? Current, string? CurrentChoiceId, string? Problem, DisplayAdapter? Adapter = null)
{
    public bool IsAvailable => Problem is null;
}

/// <param name="AdapterInstanceId">For a display driver setting: the device instance id of the adapter the user chose. Required for that kind, ignored for the others.</param>
public sealed record OptimizeRequest(string DefinitionId, string ChoiceId, StoredValue? ExpectedBefore = null, string? AdapterInstanceId = null, Guid? Group = null, string? Label = null);

public sealed record OptimizePreview(OptimizeRequest Request, MachineSettingDefinition? Definition, StoredValue? Before, StoredValue? After, bool WillChange, string? RefusalCode, DisplayAdapter? Adapter = null)
{
    public bool IsRefused => RefusalCode is not null;
}

/// <param name="RecordLost">The change was made but its outcome could not be written to the history; it shows there as interrupted and cannot be undone from it.</param>
public sealed record OptimizeApplyResult(OptimizeState State, string Code, OptimizeChange? Change, bool RecordLost = false)
{
    public bool Ok => State == OptimizeState.Applied;
}

/// <summary>Stable codes of machine setting changes.</summary>
public static class OptimizeCode
{
    public const string Ok = "ok";
    /// <summary>An interrupted change that the journal shows as written and whose value is in place; completed at the next start by <see cref="OptimizeService.ReconcileInterruptedAsync"/>.</summary>
    public const string Reconciled = "optimize.reconciled";
    /// <summary>The person closed an unconfirmed record without writing anything.</summary>
    public const string Dismissed = "optimize.dismissed";
    /// <summary>The person closed an unconfirmed record by putting the value from before back.</summary>
    public const string ResolvedRestored = "optimize.resolved_restored";
    /// <summary>The record is not an unconfirmed one.</summary>
    public const string NotUnconfirmed = "optimize.not_unconfirmed";
    public const string InterruptedUnchanged = "optimize.interrupted_unchanged";
    public const string UnknownDefinition = "optimize.unknown_definition";
    public const string UnknownChoice = "optimize.unknown_choice";
    public const string UnsupportedBuild = "optimize.unsupported_build";
    public const string UnsupportedEdition = "optimize.unsupported_edition";
    public const string ServiceInUse = "optimize.service_in_use";
    public const string ServiceNotTrusted = "optimize.service_not_trusted";
    public const string ManagedByPolicy = "optimize.managed_by_policy";
    public const string StateUnrecognized = "optimize.state_unrecognized";
    public const string Unreadable = "optimize.unreadable";
    public const string AlreadySet = "optimize.already_set";
    public const string Stale = "optimize.stale";
    public const string Conflict = "optimize.conflict";
    public const string NeedsAdministrator = "optimize.needs_administrator";
    public const string AccessDenied = "optimize.access_denied";
    public const string CannotRevert = "optimize.cannot_revert";
    public const string NotReversible = "optimize.not_reversible";
    public const string VerifyFailed = "optimize.verify_failed";
    public const string Failed = "optimize.failed";
    public const string JournalFailed = "optimize.journal_failed";
    public const string NoCatalog = "optimize.no_catalog";
    public const string GroupPartial = "optimize.group_partial";
    public const string NoAdapter = "optimize.no_adapter";
}
