// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Winora.Domain;

namespace Winora.Application;

/// <summary>
/// Scheduled task changes with a preview, a journal entry before the change, a read-back after it and a conditional undo. A task that someone else changed after the review is never
/// touched. Disable, stop and delete obey <see cref="ScheduledTaskPolicy"/>. A delete first stores a copy of the task definition; undoing it registers the task again from that copy,
/// and only when the path is free and the copy is the one the journal names.
/// </summary>
public sealed class ScheduledTaskService
{
    private const int MaxPathLength = 260;
    private const int StopAttempts = 20;
    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '|', '?', '*'];

    private readonly IScheduledTaskCatalog catalog;
    private readonly IScheduledTaskControl control;
    private readonly IScheduledTaskBackups backups;
    private readonly IScheduledTaskJournal journal;
    private readonly ScheduledTaskPolicy policy;
    private readonly TimeProvider clock;
    private readonly TimeSpan stopWait;
    private readonly SemaphoreSlim gate = new(1, 1);

    public ScheduledTaskService(IScheduledTaskCatalog catalog, IScheduledTaskControl control, IScheduledTaskBackups backups, IScheduledTaskJournal journal,
        ScheduledTaskPolicy? policy = null, TimeProvider? clock = null, TimeSpan? stopWait = null)
    {
        this.catalog = catalog; this.control = control; this.backups = backups; this.journal = journal;
        this.policy = policy ?? ScheduledTaskPolicy.Default;
        this.clock = clock ?? TimeProvider.System;
        this.stopWait = stopWait ?? TimeSpan.FromSeconds(3);
    }

    public IReadOnlyList<ScheduledTaskChangeRecord> History(int max) => journal.History(max);

    /// <summary>
    /// Closes the records of enable and disable changes that were begun and never finished. Proven cases only: the journal shows the write as made and the task is as enabled or disabled as the
    /// change made it (Applied, code reconciled), or the task still is as it was before (Failed, interrupted_unchanged). A delete is made when the write line is there and the
    /// task is gone, and not made while the backed-up definition is still in place. Everything else stays unconfirmed: a state equal to the intended one
    /// without the written line does not show who set it. The person resolves those records (<see cref="DismissAsync"/>, <see cref="RestoreBeforeAsync"/>). Run, stop and restore records are only dismissed.
    /// </summary>
    public Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken = default) => UnderGate(() =>
    {
        var done = 0;
        foreach (var record in History(int.MaxValue).Where(IsUnconfirmed))
        {
            var c = record.Change;
            ScheduledTaskChangeState state; string code;
            if (c.Kind == ScheduledTaskChangeKind.Delete)
            {
                // Not made: the task is still there with the definition that was backed up. Made: the write line is there and the task is gone, so the backup can bring it back.
                if (c.BeforeFingerprint is null || c.BackupId is null || !IsValidPath(c.Path)) continue;
                var lookup = catalog.Observe(c.Path);
                if (lookup.Task is { } still && Fingerprint(still.Xml) == c.BeforeFingerprint) (state, code) = (ScheduledTaskChangeState.Failed, ScheduledTaskCode.InterruptedUnchanged);
                else if (record.Written && lookup.Code == ScheduledTaskCode.NotFound) (state, code) = (ScheduledTaskChangeState.Applied, ScheduledTaskCode.Reconciled);
                else continue;
                try { journal.Complete(c.Id, state, code, null, clock.GetUtcNow()); done++; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays open */ }
                continue;
            }
            if (c.Kind is not (ScheduledTaskChangeKind.Enable or ScheduledTaskChangeKind.Disable) || c.BeforeEnabled is not { } before || c.AfterEnabled is not { } intended || intended == before) continue;
            if (catalog.Observe(c.Path).Task is not { } live) continue;
            // The definition must be the one the change was made on: only its enabled flag may differ. A record without that identity (older ones) stays unconfirmed.
            if (record.Written && live.Enabled == intended && c.IdentityFingerprint is { } identity && identity == IdentityFingerprint(live.Xml)) (state, code) = (ScheduledTaskChangeState.Applied, ScheduledTaskCode.Reconciled);
            else if (live.Enabled == before) (state, code) = (ScheduledTaskChangeState.Failed, ScheduledTaskCode.InterruptedUnchanged);
            else continue;
            try { journal.Complete(c.Id, state, code, state == ScheduledTaskChangeState.Applied ? Fingerprint(live.Xml) : null, clock.GetUtcNow()); done++; }
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
    public static bool IsUnconfirmed(ScheduledTaskChangeRecord record) => record.State is null && record.EndedUtc is null;

    /// <summary>The person keeps the current state and closes an unconfirmed record. Nothing is written and the record gets no undo (Dismissed).</summary>
    public Task<ScheduledTaskApplyResult> DismissAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var record = History(int.MaxValue).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.NotUnconfirmed, null);
            try { journal.Complete(changeId, ScheduledTaskChangeState.Dismissed, ScheduledTaskCode.Dismissed, null, clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.JournalFailed, null); }
            return new(ScheduledTaskChangeState.Dismissed, ScheduledTaskCode.Dismissed, record.Change);
        }, cancellationToken);

    /// <summary>The request that puts the enabled state from before back, or null (only enable and disable changes, and only when the task now is in the other state).</summary>
    private ScheduledTaskRequest? RestoreRequest(ScheduledTaskChangeRecord record)
    {
        var c = record.Change;
        if (!IsUnconfirmed(record) || c.Kind is not (ScheduledTaskChangeKind.Enable or ScheduledTaskChangeKind.Disable) || c.BeforeEnabled is not { } before) return null;
        if (catalog.Observe(c.Path).Task is not { } live || live.Enabled == before) return null;
        var request = new ScheduledTaskRequest(before ? ScheduledTaskChangeKind.Enable : ScheduledTaskChangeKind.Disable, c.Path, Fingerprint(live.Xml));
        return Preview(request).IsRefused ? null : request;
    }

    /// <summary>Whether the enabled state from before can be put back for an unconfirmed record.</summary>
    public bool CanRestoreBefore(ScheduledTaskChangeRecord record) => RestoreRequest(record) is not null;

    /// <summary>
    /// The person puts the enabled state from before back and closes the unconfirmed record. It is an ordinary change with the usual checks, its own record and its own undo, written only while
    /// the task is as it was just read. The unconfirmed record is then Dismissed with the code resolved_restored.
    /// </summary>
    public Task<ScheduledTaskApplyResult> RestoreBeforeAsync(Guid changeId, CancellationToken cancellationToken = default, string? expectedToken = null) =>
        RunAsync(() =>
        {
            var record = History(int.MaxValue).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.NotUnconfirmed, null);
            if (RestoreRequest(record) is not { } request) return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.CannotRevert, null);
            if (expectedToken is not null && expectedToken != TokenOf(record.Change, request)) return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.Stale, null);
            var result = ApplyCore(request, null, cancellationToken);
            if (result.Ok)
            {
                try { journal.Complete(changeId, ScheduledTaskChangeState.Dismissed, ScheduledTaskCode.ResolvedRestored, null, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result with { RecordLost = true }; }
            }
            return result;
        }, cancellationToken);

    private static string TokenOf(ScheduledTaskChange original, ScheduledTaskRequest request) => RecoveryToken.Of("task", original.Id, request.ExpectedFingerprint, request.Kind);

    /// <summary>What the restore would do, read now. Null when it is not possible. The token binds the confirmation to what was shown.</summary>
    public RecoveryPlan? PlanRestoreBefore(ScheduledTaskChangeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (RestoreRequest(record) is not { } request || catalog.Observe(record.Change.Path).Task is not { } live) return null;
        return new([new(record.Change.Path, live.Enabled ? "Enabled" : "Disabled", request.Kind == ScheduledTaskChangeKind.Enable ? "Enabled" : "Disabled")], TokenOf(record.Change, request));
    }

    public ScheduledTaskPolicy Policy => policy;

    /// <summary>The identity of a task definition: a SHA-256 of its XML. A change is made only while the definition still has the identity the review showed.</summary>
    public static string Fingerprint(string xml) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));

    /// <summary>The identity of a task definition apart from its enabled flag: the fingerprint of the definition with the Enabled setting taken out. An enable or a disable keeps it.</summary>
    public static string IdentityFingerprint(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml);
            foreach (var enabled in document.Descendants().Where(e => e.Name.LocalName == "Enabled" && e.Parent?.Name.LocalName == "Settings").ToList()) enabled.Remove();
            return Fingerprint(document.ToString(SaveOptions.DisableFormatting));
        }
        catch (XmlException) { return Fingerprint(xml); }
    }

    /// <summary>A task path is absolute, has no empty or dot segments and none of the characters Task Scheduler does not allow in a name.</summary>
    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path[0] != '\\' || path[^1] == '\\') return false;
        foreach (var segment in path[1..].Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.TrimEnd() != segment || segment.IndexOfAny(InvalidNameChars) >= 0 || segment.Any(char.IsControl)) return false;
        }
        return true;
    }

    // ---- list ----

    public async Task<DiscoveryResult<ScheduledTaskItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        var items = result.Items.Select(e => new ScheduledTaskItem(e, policy.Classify(e.Path, e.Author))).ToList();
        return new(items, result.Issues, result.CollectedAtUtc);
    }

    // ---- preview ----

    public ScheduledTaskPreview Preview(ScheduledTaskRequest request) => PreviewCore(request, out _);

    private ScheduledTaskPreview PreviewCore(ScheduledTaskRequest request, out TaskObservation? observed)
    {
        ArgumentNullException.ThrowIfNull(request);
        observed = null;
        var none = new TaskPolicyInfo(TaskPolicyClass.Other, TaskRisk.None, null, null);
        if (!IsValidPath(request.Path)) return Refused(request, "", none, ScheduledTaskCode.InvalidPath);
        var name = request.Path[(request.Path.LastIndexOf('\\') + 1)..];
        var lookup = catalog.Observe(request.Path);

        if (request.Kind == ScheduledTaskChangeKind.Restore) return PreviewRestore(request, name, lookup);
        if (!lookup.Found) return Refused(request, name, none, lookup.Code);
        var task = observed = lookup.Task!;
        var info = policy.Classify(request.Path, AuthorOf(task.Xml));
        if (ScheduledTaskPolicy.Refusal(request.Kind, info) is { } refusal) return Refused(request, name, info, refusal);
        var fingerprint = Fingerprint(task.Xml);
        ScheduledTaskPreview Ok(bool? before, bool? after, bool willChange) => new(request, name, info, before, after, task.State, fingerprint, willChange, null);
        var state = StateRefusal(request.Kind, task.Enabled, task.State);
        if (state == ScheduledTaskCode.AlreadySet) return Ok(task.Enabled, request.Kind == ScheduledTaskChangeKind.Enable, false);
        if (state is not null) return Refused(request, name, info, state);
        return request.Kind switch
        {
            ScheduledTaskChangeKind.Enable => Ok(task.Enabled, true, true),
            ScheduledTaskChangeKind.Disable => Ok(task.Enabled, false, true),
            ScheduledTaskChangeKind.Delete => Ok(task.Enabled, null, true),
            _ => Ok(task.Enabled, task.Enabled, true),
        };
    }

    /// <summary>The refusal for what the task is doing now, or <see cref="ScheduledTaskCode.AlreadySet"/> when there is nothing to change.</summary>
    private static string? StateRefusal(ScheduledTaskChangeKind kind, bool enabled, TaskRunState state) => kind switch
    {
        ScheduledTaskChangeKind.Enable => enabled ? ScheduledTaskCode.AlreadySet : null,
        ScheduledTaskChangeKind.Disable => enabled ? null : ScheduledTaskCode.AlreadySet,
        ScheduledTaskChangeKind.Run => !enabled ? ScheduledTaskCode.Disabled : state == TaskRunState.Running ? ScheduledTaskCode.IsRunning : null,
        ScheduledTaskChangeKind.Stop => state == TaskRunState.Running ? null : ScheduledTaskCode.NotRunning,
        ScheduledTaskChangeKind.Delete => state == TaskRunState.Running ? ScheduledTaskCode.IsRunning : null,
        _ => ScheduledTaskCode.Failed,
    };

    /// <summary>Whether the action fits a listed task, from the list data alone (no Task Scheduler call): null when it does, otherwise the code that explains why not. The change itself is checked again when it is made.</summary>
    public static string? Availability(ScheduledTaskChangeKind kind, ScheduledTaskItem item) =>
        ScheduledTaskPolicy.Refusal(kind, item.Policy) ?? StateRefusal(kind, item.Entry.Enabled, item.Entry.State);

    private ScheduledTaskPreview PreviewRestore(ScheduledTaskRequest request, string name, TaskLookup lookup)
    {
        var info = policy.Classify(request.Path, null);
        if (lookup.Found) return Refused(request, name, info, ScheduledTaskCode.Exists);
        if (lookup.Code != ScheduledTaskCode.NotFound) return Refused(request, name, info, lookup.Code);
        if (request.BackupId is null) return Refused(request, name, info, ScheduledTaskCode.BackupMissing);
        string? xml;
        try { xml = backups.Load(request.BackupId); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Refused(request, name, info, ScheduledTaskCode.BackupMissing); }
        if (xml is null) return Refused(request, name, info, ScheduledTaskCode.BackupMissing);
        if (request.BackupFingerprint is null || Fingerprint(xml) != request.BackupFingerprint || !IsTaskDefinition(xml)) return Refused(request, name, info, ScheduledTaskCode.BackupInvalid);
        return new(request, name, info, null, null, TaskRunState.Unknown, request.BackupFingerprint, true, null);
    }

    private static ScheduledTaskPreview Refused(ScheduledTaskRequest request, string name, TaskPolicyInfo info, string code) => new(request, name, info, null, null, TaskRunState.Unknown, null, false, code);

    private static bool IsTaskDefinition(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            string? root = null;
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && root is null) root = reader.LocalName;
            return root == "Task";
        }
        catch (XmlException) { return false; }
    }

    /// <summary>The author line of a task definition, for the Microsoft check of the policy.</summary>
    private static string? AuthorOf(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Author") return reader.ReadElementContentAsString();
            return null;
        }
        catch (XmlException) { return null; }
    }

    // ---- apply ----

    public Task<ScheduledTaskApplyResult> ApplyAsync(ScheduledTaskRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(() => ApplyCore(request, null, cancellationToken), cancellationToken);

    private async Task<ScheduledTaskApplyResult> RunAsync(Func<ScheduledTaskApplyResult> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private ScheduledTaskApplyResult ApplyCore(ScheduledTaskRequest request, Guid? revertOf, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var preview = PreviewCore(request, out var observed);
        if (preview.IsRefused) return new(ScheduledTaskChangeState.Refused, preview.RefusalCode!, null);
        if (!preview.WillChange) return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.AlreadySet, null);
        var needsFingerprint = request.Kind is ScheduledTaskChangeKind.Enable or ScheduledTaskChangeKind.Disable or ScheduledTaskChangeKind.Delete;
        if (needsFingerprint && request.ExpectedFingerprint is { } expected && expected != preview.Fingerprint) return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.Stale, null);

        string? backupId = null;
        if (request.Kind == ScheduledTaskChangeKind.Delete)
        {
            try
            {
                backupId = backups.Save(observed!.Xml);
                if (backups.Load(backupId) != observed.Xml) return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.BackupFailed, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.BackupFailed, null); }
        }
        else if (request.Kind == ScheduledTaskChangeKind.Restore) backupId = request.BackupId;

        ct.ThrowIfCancellationRequested();
        var change = new ScheduledTaskChange(Guid.NewGuid(), request.Kind, request.Path, preview.Name, preview.BeforeEnabled, preview.AfterEnabled, backupId,
            request.Kind == ScheduledTaskChangeKind.Restore ? null : preview.Fingerprint, null, revertOf,
            request.Kind is ScheduledTaskChangeKind.Enable or ScheduledTaskChangeKind.Disable && observed is not null ? IdentityFingerprint(observed.Xml) : null);
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.JournalFailed, null); }

        // The journal write took time: the task must still be the one that was reviewed and backed up.
        if (needsFingerprint)
        {
            var now = catalog.Observe(request.Path);
            if (now.Task is not { } live || Fingerprint(live.Xml) != preview.Fingerprint)
            {
                try { journal.Complete(change.Id, ScheduledTaskChangeState.Refused, ScheduledTaskCode.Stale, null, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing was changed; the entry stays open */ }
                return new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.Stale, null);
            }
        }

        ScheduledTaskChangeState state; string code; string? afterFingerprint;
        try { (state, code, afterFingerprint) = Execute(request, backupId, needsFingerprint ? preview.Fingerprint : null, () => MarkWritten(change.Id)); }
        catch (Exception e) when (e is not OperationCanceledException) { (state, code, afterFingerprint) = (ScheduledTaskChangeState.Failed, ScheduledTaskCode.Failed, null); } // the entry is begun, so it must be completed
        var lost = JournalGuard.CompleteLost(() => journal.Complete(change.Id, state, code, afterFingerprint, clock.GetUtcNow())); // the change is made; a lost line shows in the history as interrupted
        return new(state, code, change with { AfterFingerprint = afterFingerprint }, lost);
    }

    private void MarkWritten(Guid id)
    {
        try { journal.Written(id, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays unconfirmed if the end line is lost too */ }
    }

    private (ScheduledTaskChangeState, string, string?) Execute(ScheduledTaskRequest request, string? backupId, string? expectedFingerprint, Action written)
    {
        TaskControlResult result;
        switch (request.Kind)
        {
            case ScheduledTaskChangeKind.Enable:
            case ScheduledTaskChangeKind.Disable:
            {
                var target = request.Kind == ScheduledTaskChangeKind.Enable;
                result = control.SetEnabled(request.Path, target, expectedFingerprint);
                if (!result.Ok) return (ScheduledTaskChangeState.Failed, result.Code, null);
                written();
                var after = catalog.Observe(request.Path);
                return after.Task is { } t && t.Enabled == target ? (ScheduledTaskChangeState.Applied, ScheduledTaskCode.Ok, Fingerprint(t.Xml)) : (ScheduledTaskChangeState.Failed, ScheduledTaskCode.VerifyFailed, null);
            }
            case ScheduledTaskChangeKind.Run:
                result = control.Run(request.Path);
                if (result.Ok) written();
                return result.Ok ? (ScheduledTaskChangeState.Applied, ScheduledTaskCode.Ok, null) : (ScheduledTaskChangeState.Failed, result.Code, null);
            case ScheduledTaskChangeKind.Stop:
            {
                result = control.Stop(request.Path);
                if (!result.Ok) return (ScheduledTaskChangeState.Failed, result.Code, null);
                written();
                for (var attempt = 0; attempt < StopAttempts; attempt++)
                {
                    var now = catalog.Observe(request.Path);
                    if (now.Task is { State: not TaskRunState.Running }) return (ScheduledTaskChangeState.Applied, ScheduledTaskCode.Ok, null);
                    if (stopWait > TimeSpan.Zero) Thread.Sleep(stopWait / StopAttempts);
                }
                return (ScheduledTaskChangeState.Failed, ScheduledTaskCode.VerifyFailed, null);
            }
            case ScheduledTaskChangeKind.Delete:
            {
                result = control.Delete(request.Path, expectedFingerprint);
                if (!result.Ok) return (ScheduledTaskChangeState.Failed, result.Code, null);
                written();
                return catalog.Observe(request.Path).Code == ScheduledTaskCode.NotFound ? (ScheduledTaskChangeState.Applied, ScheduledTaskCode.Ok, null) : (ScheduledTaskChangeState.Failed, ScheduledTaskCode.VerifyFailed, null);
            }
            case ScheduledTaskChangeKind.Restore:
            {
                var xml = backups.Load(backupId!);
                if (xml is null) return (ScheduledTaskChangeState.Failed, ScheduledTaskCode.BackupMissing, null);
                result = control.Register(request.Path, xml);
                if (!result.Ok) return (ScheduledTaskChangeState.Failed, result.Code, null);
                written();
                var after = catalog.Observe(request.Path);
                return after.Task is { } t ? (ScheduledTaskChangeState.Applied, ScheduledTaskCode.Ok, Fingerprint(t.Xml)) : (ScheduledTaskChangeState.Failed, ScheduledTaskCode.VerifyFailed, null);
            }
            default:
                return (ScheduledTaskChangeState.Failed, ScheduledTaskCode.Failed, null);
        }
    }

    // ---- revert ----

    /// <summary>Whether the change can still be undone: it was applied, it was not undone already, and the state it left is still there.</summary>
    public bool CanRevert(ScheduledTaskChangeRecord record) => CanRevertAll([record])[0];

    /// <summary>The same answer for many records with one read of the journal.</summary>
    public IReadOnlyList<bool> CanRevertAll(IReadOnlyList<ScheduledTaskChangeRecord> records)
    {
        var history = History(int.MaxValue);
        return records.Select(r => RevertRequest(r, history) is { } request && !Preview(request).IsRefused).ToList();
    }

    public Task<ScheduledTaskApplyResult> RevertAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var history = History(int.MaxValue);
            var record = history.FirstOrDefault(r => r.Change.Id == changeId);
            var request = record is null ? null : RevertRequest(record, history);
            return request is null ? new(ScheduledTaskChangeState.Refused, ScheduledTaskCode.CannotRevert, null) : ApplyCore(request, record!.Change.Id, cancellationToken);
        }, cancellationToken);

    private ScheduledTaskRequest? RevertRequest(ScheduledTaskChangeRecord record, IReadOnlyList<ScheduledTaskChangeRecord> history)
    {
        if (record.State != ScheduledTaskChangeState.Applied) return null;
        var c = record.Change;
        if (c.RevertOf is not null) return null;
        if (history.Any(r => r.Change.RevertOf == c.Id && r.State == ScheduledTaskChangeState.Applied)) return null;
        switch (c.Kind)
        {
            case ScheduledTaskChangeKind.Enable:
            case ScheduledTaskChangeKind.Disable:
                // Only while the definition is the one Winora left.
                if (c.AfterFingerprint is null || c.BeforeEnabled is null || !IsValidPath(c.Path)) return null;
                if (catalog.Observe(c.Path).Task is not { } now || Fingerprint(now.Xml) != c.AfterFingerprint) return null;
                return new(c.Kind == ScheduledTaskChangeKind.Enable ? ScheduledTaskChangeKind.Disable : ScheduledTaskChangeKind.Enable, c.Path, c.AfterFingerprint);
            case ScheduledTaskChangeKind.Delete:
                if (c.BackupId is null || c.BeforeFingerprint is null || !IsValidPath(c.Path)) return null;
                return new(ScheduledTaskChangeKind.Restore, c.Path, BackupId: c.BackupId, BackupFingerprint: c.BeforeFingerprint);
            default:
                return null;
        }
    }
}
