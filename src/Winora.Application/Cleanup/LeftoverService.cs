// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

public enum QuarantineAction { Move, Restore, Delete, Purge }

/// <summary>What is about to happen to one quarantine item. It is written to the journal before the disk is touched.</summary>
public sealed record QuarantineChange(Guid Id, QuarantineAction Action, Guid? ItemId, string Program, string OriginalPath, long Bytes, int Files, bool IsFolder, DateTimeOffset AtUtc);

/// <summary>How the change ended: the status and stable reason of the quarantine, and the id of the item it made (for a move).</summary>
public sealed record QuarantineChangeEnd(Guid Id, QuarantineStatus Status, string Reason, Guid? ItemId, DateTimeOffset EndedUtc);

/// <summary>One journal record. Without an end the change was interrupted.</summary>
public sealed record QuarantineRecord(QuarantineChange Change, QuarantineChangeEnd? End)
{
    public bool Ok => End is { Status: QuarantineStatus.Moved or QuarantineStatus.Restored or QuarantineStatus.Deleted };
}

/// <summary>Append-only record of every move into quarantine, restore, delete and purge.</summary>
public interface IQuarantineJournal
{
    void Begin(QuarantineChange change);
    void Complete(QuarantineChangeEnd end);
    /// <summary>The newest changes first.</summary>
    IReadOnlyList<QuarantineRecord> History(int max);
}

/// <summary>The result of moving the folders a person ticked: one result per folder, in order.</summary>
public sealed record LeftoverMoveResult(LeftoverFolder Folder, QuarantineResult Result);

/// <summary>
/// The leftover flow after a program was removed (uninstall.md, "Leftovers"): find the folders it left, move the ones the person ticked into quarantine, and restore or delete items there.
/// Every step is journaled before it touches the disk. Items older than <see cref="RetentionDays"/> are deleted by <see cref="Purge"/>, which the app runs at start.
/// </summary>
public sealed class LeftoverService(LeftoverFolderScanner scanner, IFolderQuarantine folders, IQuarantine quarantine, IQuarantineActions actions, IQuarantineJournal journal, TimeProvider? clock = null)
    : IQuarantineActions
{
    /// <summary>How long an item stays in quarantine before the purge at app start deletes it for good.</summary>
    public const int RetentionDays = 30;

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public LeftoverFindings Find(LeftoverTarget target, IReadOnlyCollection<InstalledApplication> stillInstalled, CancellationToken cancellationToken) =>
        scanner.Find(target, stillInstalled, cancellationToken);

    /// <summary>Moves each folder into quarantine. Nothing is deleted here. A folder that changed since the scan (now holds a link, is in use, sits on another drive) is refused and stays.</summary>
    public IReadOnlyList<LeftoverMoveResult> Move(LeftoverTarget target, IReadOnlyList<LeftoverFolder> selected, CancellationToken cancellationToken)
    {
        var results = new List<LeftoverMoveResult>();
        foreach (var folder in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A folder that is gone by now was removed by someone else (often the uninstaller itself): nothing to move and nothing to journal.
            if (!scanner.StillThere(folder.Path)) { results.Add(new(folder, new(QuarantineStatus.Refused, QuarantineReason.AlreadyGone, null))); continue; }
            var itemId = Guid.NewGuid();
            var change = new QuarantineChange(Guid.NewGuid(), QuarantineAction.Move, itemId, target.DisplayName, folder.Path, folder.Bytes, folder.Files, true, time.GetUtcNow());
            journal.Begin(change);
            QuarantineResult result;
            try { result = folders.MoveFolder(folder.Path, target.DisplayName, cancellationToken, itemId); }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { result = new(QuarantineStatus.Failed, QuarantineReason.Unexpected, null); }
            journal.Complete(new(change.Id, result.Status, result.Reason, result.Entry?.Id, time.GetUtcNow()));
            results.Add(new(folder, result));
        }
        return results;
    }

    /// <summary>An item that is not in quarantine, or whose quarantined copy is missing, is refused before anything is journaled: there is nothing to put back.</summary>
    public QuarantineResult Restore(Guid id, CancellationToken cancellationToken)
    {
        var entry = quarantine.List().FirstOrDefault(e => e.Id == id);
        if (entry is null) return new(QuarantineStatus.Refused, QuarantineActionReason.NoSuchEntry, null);
        if (entry.ContentMissing) return new(QuarantineStatus.Refused, QuarantineReason.ContentMissing, entry);
        return Journaled(QuarantineAction.Restore, id, () => actions.Restore(id, cancellationToken));
    }

    public QuarantineResult Delete(Guid id) => Journaled(QuarantineAction.Delete, id, () => actions.Delete(id));

    /// <summary>Deletes for good every item that has been in quarantine longer than <see cref="RetentionDays"/> days. Returns how many were deleted.</summary>
    public int Purge()
    {
        var cutoff = time.GetUtcNow().AddDays(-RetentionDays);
        var done = 0;
        foreach (var entry in quarantine.List().Where(e => e.QuarantinedAtUtc < cutoff).ToList())
            if (Journaled(QuarantineAction.Purge, entry.Id, () => actions.Delete(entry.Id)).Ok) done++;
        return done;
    }

    public IReadOnlyList<QuarantineRecord> History(int max) => journal.History(max);

    private QuarantineResult Journaled(QuarantineAction action, Guid id, Func<QuarantineResult> run)
    {
        var entry = quarantine.List().FirstOrDefault(e => e.Id == id);
        var change = new QuarantineChange(Guid.NewGuid(), action, id, entry?.Program ?? "", entry?.OriginalPath ?? "", entry?.Fingerprint.Size ?? 0, entry?.Files ?? 0, entry?.IsFolder ?? false, time.GetUtcNow());
        journal.Begin(change);
        QuarantineResult result;
        try { result = run(); }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { result = new(QuarantineStatus.Failed, QuarantineReason.Unexpected, null); }
        journal.Complete(new(change.Id, result.Status, result.Reason, id, time.GetUtcNow()));
        return result;
    }
}
