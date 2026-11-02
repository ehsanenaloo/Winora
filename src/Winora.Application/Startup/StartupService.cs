// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;

namespace Winora.Application;

/// <summary>
/// The startup manager. An entry is turned on or off through the Explorer StartupApproved value that Task Manager uses, so nothing is deleted: the Run value and the
/// shortcut file stay as they are. Every change has a preview, a journal line before the write, a compare-and-set write, a read-back and a conditional undo. A value that someone else changed
/// after the review is never overwritten. Known entries of Windows itself, and RunOnce entries, are listed but not changed. "Known" means an exact pair of value name and program file in the
/// Windows image (for example the Windows Security icon); any other program, also one in the Windows folder, can be turned off, so a planted entry cannot hide behind a Windows tool.
/// </summary>
public sealed class StartupService(IDiscoverySource<StartupItem> discovery, IStartupApprovals approvals, IStartupFiles files, IStartupJournal journal, IElevationState elevation, TimeProvider? clock = null)
{
    private const int HistoryRead = int.MaxValue;

    // The machine-wide Run values that Windows itself writes, with the program file each one starts. Only these are protected.
    private static readonly Dictionary<string, string> WindowsEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SecurityHealth"] = "SecurityHealthSystray.exe",
    };

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);

    public IReadOnlyList<StartupChangeRecord> History(int max) => journal.History(max);

    /// <summary>
    /// Closes the records of changes that were begun and never finished. Proven cases only: the journal shows the write as made and the entry holds the bytes the change wrote (Applied, code
    /// reconciled), or the entry still holds the bytes it had before (Failed, interrupted_unchanged). Everything else stays unconfirmed: bytes equal to the intended ones without the written
    /// line do not show who wrote them. The person resolves those records (<see cref="DismissAsync"/>, <see cref="RestoreBeforeAsync"/>).
    /// </summary>
    public Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken = default) => UnderGate(() =>
    {
        var done = 0;
        foreach (var record in History(HistoryRead).Where(IsUnconfirmed))
        {
            var c = record.Change;
            if (c.BeforeHex == c.AfterHex || NowHex(c) is not { } now) continue;
            var wrote = now.Hex == c.AfterHex; var old = now.Hex == c.BeforeHex;
            StartupChangeState state; string code;
            if (record.Written && wrote && !old) (state, code) = (StartupChangeState.Applied, StartupCode.Reconciled);
            else if (old && !wrote) (state, code) = (StartupChangeState.Failed, StartupCode.InterruptedUnchanged);
            else continue;
            try { journal.Complete(c.Id, state, code, clock.GetUtcNow()); done++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays open */ }
        }
        return done;
    }, cancellationToken);

    /// <summary>
    /// Runs on the same gate as apply, dismiss and restore. A reconciler that ran beside them could close a record whose change is still being made, or write Applied over a record the person
    /// just dismissed. The records are read after the gate is held, so what it sees is final for that moment.
    /// </summary>
    private async Task<T> UnderGate<T>(Func<T> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>A record with a begin line and no end line.</summary>
    public static bool IsUnconfirmed(StartupChangeRecord record) => record.State is null && record.EndedUtc is null;

    /// <summary>The bytes of the entry now, as hex (null for an absent value). Null when the entry cannot be read.</summary>
    private (string? Hex, byte[]? Bytes)? NowHex(StartupChange c)
    {
        var current = approvals.Read(c.Scope, c.Location);
        return current.Ok ? (current.Bytes is null ? null : StartupApprovalFormat.ToHex(current.Bytes), current.Bytes) : null;
    }

    /// <summary>The person keeps the current value and closes an unconfirmed record. Nothing is written and the record gets no undo (Dismissed).</summary>
    public Task<StartupApplyResult> DismissAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var record = History(HistoryRead).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return Task.FromResult(new StartupApplyResult(StartupChangeState.Refused, StartupCode.NotUnconfirmed, null));
            try { journal.Complete(changeId, StartupChangeState.Dismissed, StartupCode.Dismissed, clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Task.FromResult(new StartupApplyResult(StartupChangeState.Refused, StartupCode.JournalFailed, null)); }
            return Task.FromResult(new StartupApplyResult(StartupChangeState.Dismissed, StartupCode.Dismissed, record.Change));
        }, cancellationToken);

    /// <summary>The change that puts the bytes from before back, or null when there is nothing to put back or the entry cannot be read.</summary>
    private StartupChange? RestorePlan(StartupChangeRecord record)
    {
        if (!IsUnconfirmed(record)) return null;
        var c = record.Change;
        if (c.Scope == InstallationScope.Machine && !elevation.IsElevated) return null;
        if ((c.BeforeHex is not null && !IsHex(c.BeforeHex)) || NowHex(c) is not { } now || now.Hex == c.BeforeHex) return null;
        return new(Guid.NewGuid(), c.EntryId, c.Name, c.Scope, c.Location, c.Action == StartupAction.Enable ? StartupAction.Disable : StartupAction.Enable, now.Hex, c.BeforeHex, null);
    }

    /// <summary>Whether the bytes from before can be put back for an unconfirmed record.</summary>
    public bool CanRestoreBefore(StartupChangeRecord record) => RestorePlan(record) is not null;

    /// <summary>
    /// The person puts the bytes from before back and closes the unconfirmed record. It is an ordinary change with its own record and undo, written only while the entry holds what was just
    /// read. The unconfirmed record is then Dismissed with the code resolved_restored.
    /// </summary>
    public Task<StartupApplyResult> RestoreBeforeAsync(Guid changeId, CancellationToken cancellationToken = default, string? expectedToken = null) =>
        RunAsync(async () =>
        {
            var record = History(HistoryRead).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return new StartupApplyResult(StartupChangeState.Refused, StartupCode.NotUnconfirmed, null);
            if (RestorePlan(record) is not { } plan) return new(StartupChangeState.Refused, StartupCode.CannotRevert, null);
            var preview = await PreviewAsync(new StartupRequest(plan.EntryId, RestoreAction(plan.AfterHex)), cancellationToken).ConfigureAwait(false);
            if (preview.IsRefused) return new(StartupChangeState.Refused, preview.RefusalCode!, null);
            if (expectedToken is not null && expectedToken != TokenOf(record.Change, plan)) return new(StartupChangeState.Refused, StartupCode.Stale, null);
            var result = Write(plan, StartupApprovalFormat.FromHex(plan.BeforeHex), StartupApprovalFormat.FromHex(plan.AfterHex));
            if (result.Ok)
            {
                try { journal.Complete(changeId, StartupChangeState.Dismissed, StartupCode.ResolvedRestored, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result with { RecordLost = true }; }
            }
            return result;
        }, cancellationToken);

    private static StartupAction RestoreAction(string? beforeHex) =>
        StartupApprovalFormat.Decode(StartupApprovalFormat.FromHex(beforeHex)) == StartupApproval.Disabled ? StartupAction.Disable : StartupAction.Enable;

    private static string TokenOf(StartupChange original, StartupChange plan) => RecoveryToken.Of("startup", original.Id, plan.BeforeHex, plan.AfterHex);

    /// <summary>
    /// What the restore would do, read now: the entry, its state now and the state it returns to. Null when the restore is not possible
    /// (the entry is protected, cannot be changed here or cannot be read). The token binds the confirmation to what was shown.
    /// </summary>
    public async Task<RecoveryPlan?> PlanRestoreBeforeAsync(StartupChangeRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (RestorePlan(record) is not { } plan) return null;
        var preview = await PreviewAsync(new StartupRequest(plan.EntryId, RestoreAction(plan.AfterHex)), cancellationToken).ConfigureAwait(false);
        if (preview.IsRefused) return null;
        var now = StartupApprovalFormat.Decode(StartupApprovalFormat.FromHex(plan.BeforeHex));
        var to = StartupApprovalFormat.Decode(StartupApprovalFormat.FromHex(plan.AfterHex));
        return new([new(record.Change.Name, now.ToString(), to.ToString())], TokenOf(record.Change, plan));
    }

    // ---- entries ----

    /// <summary>The facts and the change rules for one discovered item. The file is not run.</summary>
    public StartupEntry Describe(StartupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var facts = files.Inspect(item.Command, item.Location?.Store == StartupApprovalStore.Run32);
        var block = item.Source == StartupSource.ScheduledTask ? StartupBlock.ScheduledTask
            : item.Source == StartupSource.RegistryRunOnce ? StartupBlock.RunOnce
            : item.Location is null ? StartupBlock.Unmanaged
            : IsProtected(item, facts) ? StartupBlock.Protected
            : StartupBlock.None;
        return new(item, facts, block, item.Scope == InstallationScope.Machine && !elevation.IsElevated);
    }

    public async Task<DiscoveryResult<StartupEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var found = await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return new(found.Items.Select(Describe).ToList(), found.Issues, found.CollectedAtUtc);
    }

    /// <summary>An exact known entry: a machine Run value of Windows whose program is that file in the Windows image, with no argument that points outside the Windows folder.</summary>
    private static bool IsProtected(StartupItem item, StartupFileFacts facts)
    {
        if (item.Scope != InstallationScope.Machine || item.Location?.ValueName is not { } name || !WindowsEntries.TryGetValue(name, out var program)) return false;
        return facts.InWindowsFolder && facts.TargetPath is { } path && string.Equals(Path.GetFileName(path), program, StringComparison.OrdinalIgnoreCase);
    }

    // ---- preview ----

    public async Task<StartupPreview> PreviewAsync(StartupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var found = await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var item = found.Items.FirstOrDefault(i => string.Equals(i.StableId, request.EntryId, StringComparison.Ordinal));
        if (item is null) return Refused(request, null, StartupCode.NotFound);
        return PreviewOf(request, Describe(item));
    }

    private StartupPreview PreviewOf(StartupRequest request, StartupEntry entry)
    {
        var after = request.Action == StartupAction.Enable ? StartupApproval.Enabled : StartupApproval.Disabled;
        if (entry.Block == StartupBlock.ScheduledTask) return Refused(request, entry, StartupCode.TaskManaged);
        if (entry.Block == StartupBlock.RunOnce) return Refused(request, entry, StartupCode.RunOnce);
        if (entry.Block == StartupBlock.Unmanaged || entry.Item.Location is not { } location) return Refused(request, entry, StartupCode.Unmanaged);
        if (entry.NeedsAdministrator) return Refused(request, entry, StartupCode.NeedsAdministrator);
        var read = approvals.Read(entry.Item.Scope, location);
        if (!read.Ok) return Refused(request, entry, read.Code == StartupCode.AccessDenied ? StartupCode.AccessDenied : StartupCode.Unreadable);
        var before = StartupApprovalFormat.Decode(read.Bytes);
        var beforeHex = StartupApprovalFormat.ToHex(read.Bytes);
        if (entry.Block == StartupBlock.Protected && !(request.Action == StartupAction.Enable && before != StartupApproval.Enabled))
            return new(request, entry, before, beforeHex, after, false, StartupCode.Protected);
        return new(request, entry, before, beforeHex, after, before != after, null);
    }

    private static StartupPreview Refused(StartupRequest request, StartupEntry? entry, string code) =>
        new(request, entry, entry?.Item.Approval ?? StartupApproval.Unknown, null, request.Action == StartupAction.Enable ? StartupApproval.Enabled : StartupApproval.Disabled, false, code);

    // ---- apply ----

    public Task<StartupApplyResult> ApplyAsync(StartupRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var preview = await PreviewAsync(request, cancellationToken).ConfigureAwait(false);
            if (preview.IsRefused) return new StartupApplyResult(StartupChangeState.Refused, preview.RefusalCode!, null);
            if (!preview.WillChange) return new(StartupChangeState.Refused, StartupCode.AlreadySet, null);
            if (request.CheckExpected && preview.BeforeHex != request.ExpectedBeforeHex) return new(StartupChangeState.Refused, StartupCode.Stale, null);
            var entry = preview.Entry!;
            var newBytes = StartupApprovalFormat.Encode(request.Action, clock.GetUtcNow());
            var change = new StartupChange(Guid.NewGuid(), entry.Item.StableId, entry.Item.Name, entry.Item.Scope, entry.Item.Location!, request.Action, preview.BeforeHex, StartupApprovalFormat.ToHex(newBytes), null);
            return Write(change, StartupApprovalFormat.FromHex(preview.BeforeHex), newBytes);
        }, cancellationToken);

    private async Task<StartupApplyResult> RunAsync(Func<Task<StartupApplyResult>> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private StartupApplyResult Write(StartupChange change, byte[]? expected, byte[]? newBytes)
    {
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(StartupChangeState.Refused, StartupCode.JournalFailed, null); }

        StartupChangeState state; string code;
        try { (state, code) = Execute(change, expected, newBytes, () => MarkWritten(change.Id)); }
        catch (Exception e) when (e is not OperationCanceledException) { (state, code) = (StartupChangeState.Failed, StartupCode.Failed); } // the entry is begun, so it must be completed
        var lost = JournalGuard.CompleteLost(() => journal.Complete(change.Id, state, code, clock.GetUtcNow())); // the change is made; a lost line shows in the history as interrupted
        return new(state, code, change, lost);
    }

    private void MarkWritten(Guid id)
    {
        try { journal.Written(id, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays unconfirmed if the end line is lost too */ }
    }

    private (StartupChangeState, string) Execute(StartupChange change, byte[]? expected, byte[]? newBytes, Action written)
    {
        var result = approvals.Write(change.Scope, change.Location, expected, newBytes);
        if (!result.Ok) return (StartupChangeState.Failed, result.Code);
        written();
        var back = approvals.Read(change.Scope, change.Location);
        return back.Ok && Same(back.Bytes, newBytes) ? (StartupChangeState.Applied, StartupCode.Ok) : (StartupChangeState.Failed, StartupCode.VerifyFailed);
    }

    private static bool Same(byte[]? a, byte[]? b) => a is null || b is null ? a is null && b is null : a.AsSpan().SequenceEqual(b);

    // ---- revert ----

    /// <summary>Whether the change can still be undone: it was applied, it was not undone already, and the value is still what the change left.</summary>
    public bool CanRevert(StartupChangeRecord record) => RevertPlan(record, History(HistoryRead)) is not null;

    /// <summary>The same answer for many records with one read of the journal.</summary>
    public IReadOnlyList<bool> CanRevertAll(IReadOnlyList<StartupChangeRecord> records)
    {
        var history = History(HistoryRead);
        return records.Select(r => RevertPlan(r, history) is not null).ToList();
    }

    public Task<StartupApplyResult> RevertAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var history = History(HistoryRead);
            var record = history.FirstOrDefault(r => r.Change.Id == changeId);
            var plan = record is null ? null : RevertPlan(record, history);
            return Task.FromResult(plan is null
                ? new StartupApplyResult(StartupChangeState.Refused, StartupCode.CannotRevert, null)
                : Write(plan, StartupApprovalFormat.FromHex(plan.BeforeHex), StartupApprovalFormat.FromHex(plan.AfterHex)));
        }, cancellationToken);

    // The undo writes the bytes the entry had before the change (or removes the value) and only while the value still equals the bytes the change wrote.
    private StartupChange? RevertPlan(StartupChangeRecord record, IReadOnlyList<StartupChangeRecord> history)
    {
        if (record.State != StartupChangeState.Applied) return null;
        var c = record.Change;
        if (c.RevertOf is not null || c.AfterHex is null) return null;
        if (history.Any(r => r.Change.RevertOf == c.Id && r.State == StartupChangeState.Applied)) return null;
        if (c.Scope == InstallationScope.Machine && !elevation.IsElevated) return null;
        if (!IsHex(c.AfterHex) || (c.BeforeHex is not null && !IsHex(c.BeforeHex))) return null;
        var current = approvals.Read(c.Scope, c.Location);
        if (!current.Ok || StartupApprovalFormat.ToHex(current.Bytes) != c.AfterHex) return null;
        return new(Guid.NewGuid(), c.EntryId, c.Name, c.Scope, c.Location, c.Action == StartupAction.Enable ? StartupAction.Disable : StartupAction.Enable, c.AfterHex, c.BeforeHex, c.Id);
    }

    private static bool IsHex(string text) => text.Length is > 0 and <= 2 * StartupApprovalFormat.Length * 4 && text.Length % 2 == 0 && text.All(Uri.IsHexDigit);
}
