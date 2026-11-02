// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;
namespace Winora.Desktop;

/// <summary>The journals the History page reads, one area each. Profiles stands for a profile run, which spans several areas. Simulation is the developer simulation and is empty in release builds.</summary>
public enum HistoryArea { Settings, Optimize, Power, Network, Tasks, Startup, Bloatware, Permissions, Profiles, Cleanup, Repair, Apps, Services, Simulation }

/// <summary>
/// What happened to a change, the one set of status words every history row uses. Removed (an app) and Restored (an app or file brought back) are successes like Applied;
/// Cancelled means the user stopped it; Failed also stands for refused, partly applied and interrupted: nothing (or not everything) was changed. Unconfirmed means Winora stopped waiting for a
/// slow program that was still running, so the result is not known.
/// </summary>
public enum HistoryResult { Applied, Undone, Failed, ChangedSince, Cancelled, Removed, Restored, Unconfirmed, Dismissed }

public static class HistoryResults
{
    /// <summary>True for the results that changed what the user asked for.</summary>
    public static bool IsSuccess(this HistoryResult result) => result is HistoryResult.Applied or HistoryResult.Removed or HistoryResult.Restored;
    /// <summary>True for the results where nothing (or not everything) was changed.</summary>
    public static bool IsNotApplied(this HistoryResult result) => result is HistoryResult.Failed or HistoryResult.Cancelled or HistoryResult.Dismissed;
}

/// <summary>What a batch of changes is: a profile run, a Windows Update mode, or another named group written together with one undo.</summary>
public enum HistoryBatchKind { None, Profile, Updates, Group }

/// <summary>
/// One change as the timeline shows it. <see cref="Title"/> is one sentence in the language of the window. <see cref="CanUndo"/> is false for an entry that cannot be undone, and then
/// <see cref="UndoReason"/> says why in plain words. <see cref="UsesOwnReview"/> is set when the area asks for the undo confirmation itself (settings of the current user).
/// </summary>
public sealed record HistoryEntry(string Key, HistoryArea Area, Guid Id, DateTimeOffset AtUtc, HistoryResult Result, string Title, string Outcome, string Code, bool IsUndo, bool RestartNeeded,
    bool CanUndo, string? UndoReason, Guid? BatchId = null, string? BatchLabel = null, HistoryBatchKind BatchKind = HistoryBatchKind.None, bool UsesOwnReview = false, bool CanDismiss = false, bool CanRestoreBefore = false);

/// <summary>A row of the timeline: one change, or a batch with its members.</summary>
public sealed record TimelineItem(HistoryEntry Entry, IReadOnlyList<HistoryEntry> Members)
{
    public bool IsBatch => Members.Count > 0;
    public DateTimeOffset AtUtc => Entry.AtUtc;
}

public sealed record HistoryCounts(int Total, int Applied, int Undone, int Failed, int ChangedSince);

/// <summary>What was read. <see cref="Unreadable"/> lists the areas whose journal could not be read; the others are still shown.</summary>
public sealed record HistorySnapshot(IReadOnlyList<TimelineItem> Items, HistoryCounts Counts, IReadOnlyList<HistoryArea> Unreadable)
{
    public static HistorySnapshot Empty { get; } = new([], new(0, 0, 0, 0, 0), []);
    public bool AnyApplied => Counts.Applied > 0 || Counts.Undone > 0 || Counts.ChangedSince > 0;
}

public sealed record HistoryUndoOutcome(bool Ok, string Code, int Done = 0, int Skipped = 0, bool Cancelled = false, string? Message = null, bool RecordLost = false)
{
    public static HistoryUndoOutcome Success(int done = 1) => new(true, "ok", done);
    public static HistoryUndoOutcome Failure(string code) => new(false, code);
    /// <summary>A failure with a finished sentence in the language of the window (it names the item and says what to do).</summary>
    public static HistoryUndoOutcome Failure(string code, string message) => new(false, code, Message: message);
}

/// <summary>One journal behind the timeline. Read returns every entry of the area; undo runs through the service of the area, which compares first and refuses when the state changed.</summary>
public interface IHistorySource
{
    HistoryArea Area { get; }
    IReadOnlyList<HistoryEntry> Read();
    Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken);
    /// <summary>Keeps the current value and closes an unconfirmed record (nothing is written). Areas without unconfirmed records refuse.</summary>
    Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken) => Task.FromResult(HistoryUndoOutcome.Failure("history.cannot_resolve"));
    /// <summary>Puts the value from before back as an ordinary change and closes the unconfirmed record.</summary>
    Task<HistoryUndoOutcome> RestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken, string? expectedToken = null) => Task.FromResult(HistoryUndoOutcome.Failure("history.cannot_resolve"));
    /// <summary>What putting the value from before back would do now (current and target values), or null when it cannot be done. The token must come back with the confirmed restore.</summary>
    Task<RecoveryPlan?> PlanRestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken) => Task.FromResult<RecoveryPlan?>(null);
}

/// <summary>Text and undo of batches (a profile run spans several journals, so it is not the business of one source).</summary>
public interface IHistoryBatchHandler
{
    string Title(HistoryBatchKind kind, string label, int count);
    Task<HistoryUndoOutcome> UndoAsync(HistoryBatchKind kind, Guid batch, CancellationToken cancellationToken);
}
