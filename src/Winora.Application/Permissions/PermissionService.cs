// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>
/// The Permissions page logic. It changes the consent values of the current user that the Settings app writes, one value at a time and never anything else. Every change has a
/// preview with the value before and after, a journal line before the first write, a compare-and-set per value, a read-back of each value and a conditional undo of the whole group. A value that
/// someone else changed after the review is never overwritten. When a later value of a group cannot be written, the earlier ones are put back.
/// </summary>
public sealed class PermissionService(IConsentStore store, IPermissionJournal journal, TimeProvider? clock = null)
{
    private const int HistoryRead = int.MaxValue;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);

    public IReadOnlyList<PermissionChangeRecord> History(int max) => journal.History(max);

    // ---- read ----

    public PermissionSnapshot Read() =>
        new(PermissionCatalog.All.Select(c => new PermissionRowData(c, store.Read(c.Id))).ToList());

    // ---- requests ----

    public static PermissionRequest ForValue(ConsentTarget target, PermissionChoice choice) => new(PermissionGroup.Single, [target], choice);

    /// <summary>
    /// The request for values staged on the page, each with its own choice. One value is a Single change; several are one Staged change with one journal entry and one undo,
    /// unless <paramref name="group"/> names the quick switch that staged them.
    /// </summary>
    public static PermissionRequest ForValues(IReadOnlyList<(ConsentTarget Target, PermissionChoice Choice)> values, PermissionGroup? group = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var kind = group ?? (values.Count == 1 ? PermissionGroup.Single : PermissionGroup.Staged);
        var first = values.Count > 0 ? values[0].Choice : PermissionChoice.Deny;
        return new(kind, values.Select(v => v.Target).ToList(), first, values.Select(v => v.Choice).ToList());
    }

    /// <summary>
    /// The request for a group: the global value of each capability of the group, plus its desktop value where the capability has a NonPackaged key. Camera and microphone use Deny
    /// when any of their values is not Deny yet, otherwise Allow; the lockdown always denies.
    /// </summary>
    public PermissionRequest ForGroup(PermissionGroup group, PermissionSnapshot? snapshot = null)
    {
        snapshot ??= Read();
        var ids = group == PermissionGroup.Lockdown ? PermissionCatalog.Lockdown : PermissionCatalog.CameraMicrophone;
        var targets = new List<ConsentTarget>();
        var allDenied = true;
        foreach (var id in ids)
        {
            var row = snapshot.Find(id);
            if (row is null || !row.Read.Exists) continue;
            targets.Add(new(id, ConsentScope.Global));
            if (row.Read.Global != ConsentState.Deny) allDenied = false;
            if (!row.Read.HasDesktopKey) continue;
            targets.Add(new(id, ConsentScope.Desktop));
            if (row.Read.Desktop != ConsentState.Deny) allDenied = false;
        }
        var choice = group == PermissionGroup.Lockdown || !allDenied ? PermissionChoice.Deny : PermissionChoice.Allow;
        return new(group, targets, choice);
    }

    /// <summary>True when every global value of the camera and microphone is Deny, so that the quick switch would turn them on again.</summary>
    public static bool IsCameraMicrophoneOff(PermissionSnapshot snapshot) =>
        PermissionCatalog.CameraMicrophone.Select(snapshot.Find).All(r => r is { Read.Exists: true } && r.Read.Global == ConsentState.Deny && (!r.Read.HasDesktopKey || r.Read.Desktop == ConsentState.Deny));

    // ---- preview ----

    public PermissionPreview Preview(PermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Targets.Count == 0) return new(request, [], PermissionCode.NothingToDo);
        var entries = new List<PermissionPreviewEntry>();
        var limits = new Dictionary<string, PermissionLimit>(StringComparer.Ordinal);
        foreach (var (target, choice) in request.Targets.Select((t, i) => (t, request.ChoiceAt(i))).DistinctBy(p => (p.t.Capability, p.t.Scope, p.t.AppKey)))
        {
            var after = ConsentValues.RawOf(choice);
            if (!IsValidTarget(target)) return new(request, [], PermissionCode.Unknown);
            if (!limits.TryGetValue(target.Capability, out var limit))
            {
                var read = store.Read(target.Capability);
                var capability = PermissionCatalog.Find(target.Capability)!;
                limit = limits[target.Capability] = new PermissionRowData(capability, read).Limit;
            }
            var current = store.ReadValue(target);
            if (!current.Ok) { entries.Add(new(target, null, after, false, current.Code, limit)); continue; }
            if (!ConsentValues.IsWritable(current.Raw)) { entries.Add(new(target, current.Raw, after, false, PermissionCode.Unusual, limit)); continue; }
            var same = ConsentValues.StateOf(current.Raw) == ConsentValues.StateOf(after);
            entries.Add(new(target, current.Raw, after, !same, same ? PermissionCode.AlreadySet : null, limit));
        }
        return new(request, entries, null);
    }

    private static bool IsValidTarget(ConsentTarget target) =>
        PermissionCatalog.IsKnown(target.Capability) && (target.Scope == ConsentScope.App ? ConsentAppNames.IsPackageKey(target.AppKey) : target.AppKey is null);

    // ---- apply ----

    public Task<PermissionApplyResult> ApplyAsync(PermissionPreview preview, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(preview);
            if (preview.IsRefused) return Refused(preview.RefusalCode!);
            var wanted = preview.Changes;
            if (wanted.Count == 0) return Refused(PermissionCode.AlreadySet);
            var fresh = Preview(preview.Request);
            if (fresh.IsRefused) return Refused(fresh.RefusalCode!);
            foreach (var entry in wanted)
            {
                var now = fresh.Entries.FirstOrDefault(e => e.Target == entry.Target);
                if (now is null || !now.WillChange || now.BeforeRaw != entry.BeforeRaw) return Refused(PermissionCode.Stale);
            }
            var change = new PermissionChange(Guid.NewGuid(), preview.Request.Group, wanted.Select(e => new PermissionEntry(e.Target, e.BeforeRaw, e.AfterRaw)).ToList(), null);
            return Write(change);
        }, cancellationToken);

    private static PermissionApplyResult Refused(string code) => new(PermissionChangeState.Refused, code, null, []);

    private async Task<PermissionApplyResult> RunAsync(Func<PermissionApplyResult> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private PermissionApplyResult Write(PermissionChange change)
    {
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Refused(PermissionCode.JournalFailed); }

        var results = new List<PermissionEntryResult>();
        var done = new List<PermissionEntry>();
        var state = PermissionChangeState.Applied;
        var code = PermissionCode.Ok;
        for (var index = 0; index < change.Entries.Count; index++)
        {
            var entry = change.Entries[index];
            var written = store.Write(entry.Target, entry.BeforeRaw, entry.AfterRaw);
            if (!written.Ok) { results.Add(new(entry.Target, written.Code)); state = PermissionChangeState.Failed; code = written.Code; break; }
            if (index == change.Entries.Count - 1)
            {
                // The proof that every value of the change was written: a record that stops before the last value has no such line, so the values it did not write never count as its own.
                // When this line is lost the record stays unconfirmed.
                try { journal.Written(change.Id, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the end line follows */ }
            }
            var back = store.ReadValue(entry.Target);
            if (!back.Ok || back.Raw != entry.AfterRaw)
            {
                results.Add(new(entry.Target, PermissionCode.VerifyFailed));
                state = PermissionChangeState.Failed; code = PermissionCode.VerifyFailed;
                // Unreadable: the write may have landed, so it is put back with the rest. A value that reads back as something else belongs to another writer and is left alone.
                if (!back.Ok) done.Add(entry);
                break;
            }
            results.Add(new(entry.Target, PermissionCode.Ok));
            done.Add(entry);
        }
        if (state == PermissionChangeState.Failed && done.Count > 0)
        {
            foreach (var entry in done.AsEnumerable().Reverse())
            {
                var undone = store.Write(entry.Target, entry.AfterRaw, entry.BeforeRaw);
                var i = results.FindIndex(r => r.Target == entry.Target);
                if (undone.Ok) results[i] = new(entry.Target, PermissionCode.RolledBack);
                else { results[i] = new(entry.Target, PermissionCode.RollbackFailed); code = PermissionCode.RollbackFailed; state = PermissionChangeState.Partial; }
            }
        }
        var lost = false;
        try { journal.Complete(change.Id, state, code, results, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The change is made; the history shows it as interrupted. One more try, then the caller is told.
            try { journal.Complete(change.Id, state, code, results, clock.GetUtcNow()); }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { lost = true; }
        }
        return new(state, code, change, results, lost);
    }

    // ---- interrupted changes ----

    /// <summary>
    /// Closes the records of changes that were begun and never finished. Proven cases only. The journal shows a write as made and every value holds the text the change was to write: Applied
    /// (code reconciled). Every value still holds the text it had before: Failed (not made). Everything else stays unconfirmed: a value equal to the intended one without the written line does
    /// not show who wrote it, and a mix or a value that cannot be read proves nothing. The person resolves those records (<see cref="DismissAsync"/>, <see cref="RestoreBeforeAsync"/>).
    /// </summary>
    public async Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var done = 0;
                foreach (var record in History(HistoryRead).Where(IsUnconfirmed))
                {
                    var c = record.Change;
                    if (c.Entries.Count == 0 || c.Entries.Any(e => !IsValidTarget(e.Target))) continue;
                    var reads = c.Entries.Select(e => (Entry: e, Now: store.ReadValue(e.Target))).ToList();
                    if (reads.Any(r => !r.Now.Ok)) continue;
                    var wrote = reads.All(r => r.Now.Raw == r.Entry.AfterRaw); var old = reads.All(r => r.Now.Raw == r.Entry.BeforeRaw);
                    PermissionChangeState state; string code;
                    if (record.Written && wrote && !old) (state, code) = (PermissionChangeState.Applied, PermissionCode.Reconciled);
                    else if (old && !wrote) (state, code) = (PermissionChangeState.Failed, PermissionCode.InterruptedUnchanged);
                    else continue;
                    try { journal.Complete(c.Id, state, code, [], clock.GetUtcNow()); done++; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays open */ }
                }
                return done;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>A record with a begin line and no end line.</summary>
    public static bool IsUnconfirmed(PermissionChangeRecord record) => record.State is null && record.EndedUtc is null;

    /// <summary>The person keeps the current values and closes an unconfirmed record. Nothing is written and the record gets no undo (Dismissed).</summary>
    public async Task<PermissionApplyResult> DismissAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var record = History(HistoryRead).FirstOrDefault(r => r.Change.Id == changeId);
                if (record is null || !IsUnconfirmed(record)) return Refused(PermissionCode.NotUnconfirmed);
                try { journal.Complete(changeId, PermissionChangeState.Dismissed, PermissionCode.Dismissed, [], clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Refused(PermissionCode.JournalFailed); }
                return new PermissionApplyResult(PermissionChangeState.Dismissed, PermissionCode.Dismissed, record.Change, []);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>The values of an unconfirmed record that do not hold the text from before, with what they hold now. Empty when there is nothing to put back or a value cannot be read.</summary>
    private List<PermissionEntry> RestoreEntries(PermissionChangeRecord record)
    {
        var result = new List<PermissionEntry>();
        if (!IsUnconfirmed(record) || record.Change.Entries.Count == 0) return result;
        foreach (var entry in record.Change.Entries)
        {
            if (!IsValidTarget(entry.Target)) return [];
            var now = store.ReadValue(entry.Target);
            // An ordinary change writes only absent, Allow or Deny, and replaces only such a value: a form Winora does not change ends the restore for the whole record.
            if (!now.Ok || !ConsentValues.IsWritable(now.Raw) || !ConsentValues.IsWritable(entry.BeforeRaw)) return [];
            if (now.Raw != entry.BeforeRaw) result.Add(new PermissionEntry(entry.Target, now.Raw, entry.BeforeRaw));
        }
        return result;
    }

    /// <summary>Whether values from before can be put back for an unconfirmed record.</summary>
    public bool CanRestoreBefore(PermissionChangeRecord record) => RestoreEntries(record).Count > 0;

    private static string Key(ConsentTarget t) => t.Capability + "/" + t.Scope + (t.AppKey is null ? "" : "/" + t.AppKey);

    private static RecoveryPlan PlanOf(PermissionChangeRecord record, IReadOnlyList<PermissionEntry> entries) =>
        new(entries.Select(e => new RecoveryLine(Key(e.Target), e.BeforeRaw ?? "", e.AfterRaw ?? "")).ToList(),
            RecoveryToken.Of("permission", record.Change.Id, string.Join(';', entries.Select(e => Key(e.Target) + "=" + e.BeforeRaw + ">" + e.AfterRaw))));

    /// <summary>What a restore of the values from before would do now: each value, what it holds and what would be put there, with a token for that state. Null when no restore is possible.</summary>
    public RecoveryPlan? PlanRestoreBefore(PermissionChangeRecord record) => RestoreEntries(record) is { Count: > 0 } entries ? PlanOf(record, entries) : null;

    /// <summary>
    /// The person puts the values from before back and closes the unconfirmed record. It is an ordinary change with its own record and undo; each value is written only while it holds what was
    /// just read. The unconfirmed record is then Dismissed with the code resolved_restored.
    /// </summary>
    public async Task<PermissionApplyResult> RestoreBeforeAsync(Guid changeId, CancellationToken cancellationToken = default, string? expectedToken = null)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var record = History(HistoryRead).FirstOrDefault(r => r.Change.Id == changeId);
                if (record is null || !IsUnconfirmed(record)) return Refused(PermissionCode.NotUnconfirmed);
                var entries = RestoreEntries(record);
                if (entries.Count == 0) return Refused(PermissionCode.CannotRevert);
                // What was reviewed is what is written: a value that changed since the review needs a new review.
                if (expectedToken is not null && PlanOf(record, entries).Token != expectedToken) return Refused(PermissionCode.Stale);
                var result = Write(new PermissionChange(Guid.NewGuid(), record.Change.Group, entries, null));
                if (result.Ok)
                {
                    try { journal.Complete(changeId, PermissionChangeState.Dismissed, PermissionCode.ResolvedRestored, [], clock.GetUtcNow()); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result with { RecordLost = true }; }
                }
                return result;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // ---- revert ----

    /// <summary>Whether the change can still be undone: it was applied, it was not undone already, and every value is still what the change left.</summary>
    public bool CanRevert(PermissionChangeRecord record) => RevertPlan(record, History(HistoryRead)) is not null;

    /// <summary>The same answer for many records with one read of the journal.</summary>
    public IReadOnlyList<bool> CanRevertAll(IReadOnlyList<PermissionChangeRecord> records)
    {
        var history = History(HistoryRead);
        return records.Select(r => RevertPlan(r, history) is not null).ToList();
    }

    public Task<PermissionApplyResult> RevertAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var history = History(HistoryRead);
            var record = history.FirstOrDefault(r => r.Change.Id == changeId);
            var plan = record is null ? null : RevertPlan(record, history);
            return plan is null ? Refused(PermissionCode.CannotRevert) : Write(plan);
        }, cancellationToken);

    // The undo writes the text each value had before the change (or removes the value) and only while every value still equals the text the change wrote.
    private PermissionChange? RevertPlan(PermissionChangeRecord record, IReadOnlyList<PermissionChangeRecord> history)
    {
        if (record.State is not (PermissionChangeState.Applied or PermissionChangeState.Partial)) return null;
        var c = record.Change;
        if (c.RevertOf is not null || c.Entries.Count == 0) return null;
        if (history.Any(r => r.Change.RevertOf == c.Id && r.State == PermissionChangeState.Applied)) return null;
        // A partial change left only the values whose rollback failed.
        var entries = record.State == PermissionChangeState.Applied
            ? c.Entries
            : c.Entries.Where(e => record.Results.Any(r => r.Target == e.Target && r.Code == PermissionCode.RollbackFailed)).ToList();
        if (entries.Count == 0) return null;
        foreach (var e in entries)
        {
            if (!IsValidTarget(e.Target) || !ConsentValues.IsWritable(e.BeforeRaw) || !ConsentValues.IsWritable(e.AfterRaw)) return null;
            var now = store.ReadValue(e.Target);
            if (!now.Ok || now.Raw != e.AfterRaw) return null;
        }
        return new(Guid.NewGuid(), c.Group, entries.Select(e => new PermissionEntry(e.Target, e.AfterRaw, e.BeforeRaw)).ToList(), c.Id);
    }
}
