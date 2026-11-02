// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;

namespace Winora.Desktop;

/// <summary>The lines the History page and the Quarantine tab show for one quarantine change.</summary>
internal static class QuarantineHistoryText
{
    public static string Title(LocalizationService text, QuarantineChange c)
    {
        var program = c.Program.Length > 0 ? c.Program : text["SetAside"];
        var item = c.OriginalPath.Length > 0 ? c.OriginalPath : text["V2ClUnknownItem"];
        return c.Action switch
        {
            QuarantineAction.Move => text.Format("HistQuarantineMove", program, item),
            QuarantineAction.Restore => text.Format("HistQuarantineRestore", program, item),
            QuarantineAction.Purge => text.Format("HistQuarantinePurge", program, item, LeftoverService.RetentionDays),
            _ => text.Format("HistQuarantineDelete", program, item),
        };
    }

    public static HistoryResult Result(QuarantineRecord r) => r.Ok ? HistoryResult.Applied : HistoryResult.Failed;
}

/// <summary>
/// Moves into Quarantine, restores, deletes and purges. None is undone from History: a moved item is restored or deleted on the Cleanup page, Quarantine tab, and a deleted item is gone.
/// </summary>
public sealed class QuarantineHistorySource(LeftoverService service, LocalizationService text) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Cleanup;

    public IReadOnlyList<HistoryEntry> Read() => service.History(200).Select(r =>
    {
        var state = r.End is null ? null : r.Ok ? "Applied" : r.End.Status == QuarantineStatus.Refused ? "Refused" : "Failed";
        var reason = !r.Ok ? null : r.Change.Action switch { QuarantineAction.Move => "HistUndoQuarantine", QuarantineAction.Restore => "HistUndoQuarantineRestored", _ => "HistUndoCleanup" };
        return HistoryText.Entry(HistoryArea.Cleanup, r.Change.Id, r.Change.AtUtc, QuarantineHistoryText.Result(r), QuarantineHistoryText.Title(text, r.Change),
            HistoryText.OutcomeOf(text, state, "", state is not null), r.End is null ? "interrupted" : r.Ok ? "ok" : r.End.Reason, false, false, false, reason);
    }).ToList();

    public Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken) => Task.FromResult(HistoryUndoOutcome.Failure("history.not_undoable"));
}
