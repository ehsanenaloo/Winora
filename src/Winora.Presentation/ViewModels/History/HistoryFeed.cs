// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;
namespace Winora.Desktop;

/// <summary>
/// Puts the journals of all areas into one list, newest first, with the entries of a batch (a profile run, a Windows Update mode) folded into one row. It only reads: undo goes to the source of the
/// area or to the batch handler, and both run the same service calls as the page of the area.
/// </summary>
public sealed class HistoryFeed(IReadOnlyList<IHistorySource> sources, IHistoryBatchHandler batches)
{
    private readonly Dictionary<string, IHistorySource> owners = [];

    public IReadOnlyList<IHistorySource> Sources => sources;

    public HistorySnapshot Read()
    {
        var all = new List<HistoryEntry>();
        var unreadable = new List<HistoryArea>();
        var found = new Dictionary<string, IHistorySource>();
        foreach (var source in sources)
        {
            try
            {
                var read = source.Read();
                all.AddRange(read);
                foreach (var entry in read) found[entry.Key] = source;
            }
            catch (Exception e) when (e is not OperationCanceledException) { unreadable.Add(source.Area); }
        }
        lock (owners) { owners.Clear(); foreach (var (key, source) in found) owners[key] = source; }
        // The line a successful undo writes only repeats what the undone entry says; a failed undo stays, because nothing was undone.
        var visible = all.Where(e => !(e.IsUndo && e.Result.IsSuccess())).OrderByDescending(e => e.AtUtc).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();

        var items = new List<TimelineItem>();
        var seen = new HashSet<Guid>();
        foreach (var entry in visible)
        {
            if (entry.BatchId is not { } batch) { items.Add(new(entry, [])); continue; }
            if (!seen.Add(batch)) continue;
            var members = visible.Where(e => e.BatchId == batch).ToList();
            items.Add(new(Summarize(batch, members), members));
        }
        var counts = new HistoryCounts(visible.Count, visible.Count(e => e.Result.IsSuccess()), visible.Count(e => e.Result == HistoryResult.Undone),
            visible.Count(e => e.Result.IsNotApplied()), visible.Count(e => e.Result == HistoryResult.ChangedSince));
        return new(items, counts, unreadable);
    }

    private HistoryEntry Summarize(Guid batch, IReadOnlyList<HistoryEntry> members)
    {
        var first = members[0];
        var kind = members.Select(m => m.BatchKind).FirstOrDefault(k => k != HistoryBatchKind.None);
        var live = members.Where(m => !m.Result.IsNotApplied()).ToList();
        var result = live.Count == 0 ? HistoryResult.Failed
            : live.All(m => m.Result == HistoryResult.Undone) ? HistoryResult.Undone
            : live.Any(m => m.Result.IsSuccess()) ? HistoryResult.Applied
            : HistoryResult.ChangedSince;
        // The title counts the changes that were made, like the Profiles page and the Overview card; failed members are listed but not counted.
        var undoable = members.Count(m => m.CanUndo);
        var area = kind switch { HistoryBatchKind.Profile => HistoryArea.Profiles, _ => first.Area };
        return new($"batch:{batch:N}", area, batch, members.Max(m => m.AtUtc), result, batches.Title(kind, first.BatchLabel ?? "", live.Count), "", "ok", false, members.Any(m => m.RestartNeeded),
            undoable > 0 && result != HistoryResult.Undone, undoable > 0 ? null : members.Select(m => m.UndoReason).FirstOrDefault(r => r is not null) ?? "HistUndoNotPossible",
            batch, first.BatchLabel, kind);
    }

    /// <summary>Undoes one entry or a whole batch. The caller has already asked the person.</summary>
    public Task<HistoryUndoOutcome> UndoAsync(TimelineItem item, CancellationToken cancellationToken)
    {
        if (item.IsBatch && item.Entry.BatchId is { } batch) return batches.UndoAsync(item.Entry.BatchKind, batch, cancellationToken);
        return UndoAsync(item.Entry, cancellationToken);
    }

    private IHistorySource? SourceOf(HistoryEntry entry)
    {
        IHistorySource? source;
        lock (owners) owners.TryGetValue(entry.Key, out source);
        return source ?? sources.FirstOrDefault(s => s.Area == entry.Area);
    }

    public Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        SourceOf(entry) is { } source ? source.UndoAsync(entry, cancellationToken) : Task.FromResult(HistoryUndoOutcome.Failure("history.no_source"));

    /// <summary>Keeps the current value and closes an unconfirmed record. The caller has already asked the person.</summary>
    public Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        SourceOf(entry) is { } source ? source.DismissAsync(entry, cancellationToken) : Task.FromResult(HistoryUndoOutcome.Failure("history.no_source"));

    /// <summary>What the restore would do now, read from the area. Null when it cannot be done.</summary>
    public Task<RecoveryPlan?> PlanRestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        SourceOf(entry) is { } source ? source.PlanRestoreBeforeAsync(entry, cancellationToken) : Task.FromResult<RecoveryPlan?>(null);

    /// <summary>Puts the value from before back and closes an unconfirmed record. The caller has already asked the person.</summary>
    public Task<HistoryUndoOutcome> RestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken, string? expectedToken = null) =>
        SourceOf(entry) is { } source ? source.RestoreBeforeAsync(entry, cancellationToken, expectedToken) : Task.FromResult(HistoryUndoOutcome.Failure("history.no_source"));
}
