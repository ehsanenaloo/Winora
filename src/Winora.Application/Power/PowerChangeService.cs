// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public enum PowerChangeKind { SetActive, SetValue, CreateCopy, DeleteCopy, SetHibernation }

/// <summary>Dismissed: the person closed a record whose outcome was never confirmed. It has no undo.</summary>
public enum PowerChangeState { Applied, Failed, Refused, Dismissed }

/// <summary>
/// One change to the power configuration, recorded before it is made. <see cref="Before"/> and <see cref="After"/> are setting values. For a plan switch <see cref="OtherPlan"/> is the
/// plan that was active before; for a copy it is the new plan. <see cref="RevertOf"/> names the change that this one undoes.
/// </summary>
public sealed record PowerChange(Guid Id, PowerChangeKind Kind, Guid Plan, string PlanName, string? SettingId, PowerSource? Source, long? Before, long? After, Guid? OtherPlan, Guid? RevertOf, Guid? Group = null, string? Label = null);

/// <summary>A journal entry. A record without an end is a change that was interrupted.</summary>
/// <param name="Written">The journal holds the line that says the write was made (after the write, before the end line).</param>
public sealed record PowerChangeRecord(PowerChange Change, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, PowerChangeState? State, string Code, bool Written = false);

/// <summary>Append-only record of power changes. <see cref="Begin"/> must be durable before it returns.</summary>
public interface IPowerJournal
{
    void Begin(PowerChange change, DateTimeOffset atUtc);
    void Complete(Guid id, PowerChangeState state, string code, Guid? otherPlan, DateTimeOffset atUtc);
    /// <summary>Records the id of the plan a copy created, as soon as Windows returned it, before the change is completed. The change stays open.</summary>
    void NoteCreated(Guid id, Guid plan, DateTimeOffset atUtc);
    /// <summary>Records that the write of the change was made. Called after the write returned and before the end line.</summary>
    void Written(Guid id, DateTimeOffset atUtc) { }
    /// <summary>The newest changes first.</summary>
    IReadOnlyList<PowerChangeRecord> History(int max);
}

/// <summary>What the user asks for. <see cref="ExpectedBefore"/> is the value the review showed; when the value changed in between, nothing is written.</summary>
public sealed record PowerRequest(PowerChangeKind Kind, Guid Plan, string? SettingId = null, PowerSource? Source = null, uint? Value = null, string? Name = null, long? ExpectedBefore = null, Guid? ExpectedActivePlan = null);

/// <summary>What would happen. <see cref="RefusalCode"/> is set when the change cannot be made; the other fields are then not meaningful.</summary>
public sealed record PowerPreview(PowerRequest Request, string PlanName, long? Before, long? After, bool WillChange, string? RefusalCode)
{
    public bool IsRefused => RefusalCode is not null;
}

/// <param name="RecordLost">The change was made but its outcome could not be written to the history; it shows there as interrupted and cannot be undone from it.</param>
public sealed record PowerApplyResult(PowerChangeState State, string Code, PowerChange? Change, bool RecordLost = false)
{
    public bool Ok => State == PowerChangeState.Applied;
}

/// <summary>Stable codes of power changes.</summary>
public static class PowerCode
{
    public const string Ok = "ok";
    public const string UnknownPlan = "power.unknown_plan";
    public const string UnknownSetting = "power.unknown_setting";
    public const string Unsupported = "power.unsupported";
    public const string InvalidValue = "power.invalid_value";
    public const string AlreadySet = "power.already_set";
    public const string Stale = "power.stale";
    public const string Reconciled = "power.reconciled";
    public const string InterruptedUnchanged = "power.interrupted_unchanged";
    public const string Dismissed = "power.dismissed";
    public const string ResolvedRestored = "power.resolved_restored";
    public const string NotUnconfirmed = "power.not_unconfirmed";
    public const string Conflict = "power.conflict";
    public const string NotOwned = "power.not_owned";
    public const string IsActive = "power.is_active";
    public const string CannotRevert = "power.cannot_revert";
    public const string VerifyFailed = "power.verify_failed";
    public const string ReapplyFailed = "power.reapply_failed";
    public const string AccessDenied = "power.access_denied";
    public const string Failed = "power.failed";
    public const string JournalFailed = "power.journal_failed";
    public const string PlanUnavailable = "power.plan_unavailable";
    public const string GroupPartial = "power.group_partial";
}

/// <summary>
/// Power plan changes with a preview, a journal entry before the change, a read-back after it and a conditional revert. A value that someone else changed after the review is never
/// overwritten. A plan can be deleted only when Winora made it as a copy; built-in and user-made plans are never deleted.
/// </summary>
public sealed class PowerChangeService(IPowerPlans plans, IPowerJournal journal, TimeProvider? clock = null, IPowerSystem? system = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);

    public IReadOnlyList<PowerChangeRecord> History(int max) => journal.History(max);

    /// <summary>Battery and hibernation access, or null when the PC does not provide it.</summary>
    public IPowerSystem? PowerSystem => system;

    /// <summary>True for a plan that Winora made as a copy and that is still there; only such a plan can be deleted.</summary>
    public bool IsOwnedCopy(Guid plan) => OwnedCopies().Contains(plan);

    // ---- preview ----

    public PowerPreview Preview(PowerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == PowerChangeKind.SetHibernation)
        {
            if (system?.HibernationEnabled() is not { } now) return Refused(request, "", PowerCode.Unsupported);
            if (request.Value is not (0 or 1)) return Refused(request, "", PowerCode.InvalidValue);
            return new(request, "", now ? 1 : 0, request.Value, (request.Value == 1) != now, null);
        }
        var snapshot = plans.Read(cancellationToken);
        var plan = snapshot.Plans.FirstOrDefault(p => p.Id == request.Plan);
        if (plan is null && request.Kind == PowerChangeKind.CreateCopy && request.Plan == PowerPlanCatalog.UltimatePerformance)
            return new(request, request.Name ?? "", null, null, true, null);
        if (plan is null) return Refused(request, "", PowerCode.UnknownPlan);
        switch (request.Kind)
        {
            case PowerChangeKind.SetActive:
                return plan.IsActive ? new(request, plan.Name, null, null, false, null) : new(request, plan.Name, null, null, true, null);
            case PowerChangeKind.CreateCopy:
                return new(request, plan.Name, null, null, true, null);
            case PowerChangeKind.DeleteCopy:
                if (plan.IsActive) return Refused(request, plan.Name, PowerCode.IsActive);
                if (plan.IsBuiltIn || !OwnedCopies().Contains(plan.Id)) return Refused(request, plan.Name, PowerCode.NotOwned);
                return new(request, plan.Name, null, null, true, null);
            case PowerChangeKind.SetValue:
                if (PowerSettings.Find(request.SettingId ?? "") is not { } setting) return Refused(request, plan.Name, PowerCode.UnknownSetting);
                if (request.Source is null) return Refused(request, plan.Name, PowerCode.UnknownSetting);
                if (request.Value is not { } value || !setting.IsAllowed(value)) return Refused(request, plan.Name, PowerCode.InvalidValue);
                if (plans.ReadValue(plan.Id, setting, request.Source.Value) is not { } current) return Refused(request, plan.Name, PowerCode.Unsupported);
                return new(request, plan.Name, current, value, current != value, null);
            default:
                return Refused(request, plan.Name, PowerCode.UnknownSetting);
        }
    }

    private static PowerPreview Refused(PowerRequest request, string name, string code) => new(request, name, null, null, false, code);

    // ---- apply ----

    public Task<PowerApplyResult> ApplyAsync(PowerRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(() => ApplyCore(request, null, cancellationToken), cancellationToken);

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    // ---- groups: a profile or a plan install is several changes with one undo ----

    /// <summary>Applies the requests in order as one group. The first change that fails stops the group; what was applied stays recorded and can be undone with <see cref="RevertGroupAsync"/>.</summary>
    public Task<IReadOnlyList<PowerApplyResult>> ApplyGroupAsync(IReadOnlyList<PowerRequest> requests, string label, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<PowerApplyResult>>(() =>
        {
            var group = Guid.NewGuid();
            var results = new List<PowerApplyResult>();
            foreach (var request in requests)
            {
                var result = ApplyCore(request, null, cancellationToken, group, label);
                results.Add(result);
                if (!result.Ok && result.Code != PowerCode.AlreadySet) break;
            }
            return results;
        }, cancellationToken);

    /// <summary>The preview of each request of a group, for the review. Requests that would change nothing are included with WillChange false.</summary>
    public IReadOnlyList<PowerPreview> PreviewGroup(IReadOnlyList<PowerRequest> requests, CancellationToken cancellationToken = default) =>
        requests.Select(r => Preview(r, cancellationToken)).ToList();

    /// <summary>Installs the Ultimate Performance plan as a copy under <paramref name="name"/> and makes it the active plan, as one group. Undo switches back and removes the copy.</summary>
    public Task<IReadOnlyList<PowerApplyResult>> InstallPerformancePlanAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<PowerApplyResult>>(() =>
        {
            var group = Guid.NewGuid();
            const string label = "performance_plan";
            var copy = ApplyCore(new(PowerChangeKind.CreateCopy, PowerPlanCatalog.UltimatePerformance, Name: name), null, cancellationToken, group, label);
            if (!copy.Ok)
            {
                var code = copy.Code is PowerCode.AccessDenied or PowerCode.JournalFailed ? copy.Code : PowerCode.PlanUnavailable;
                return [new(copy.State, code, copy.Change)];
            }
            var created = copy.Change!.OtherPlan!.Value;
            var active = ApplyCore(new(PowerChangeKind.SetActive, created), null, cancellationToken, group, label);
            return [copy, active];
        }, cancellationToken);

    /// <summary>True when at least one change of the group can still be undone.</summary>
    public bool CanRevertGroup(Guid group)
    {
        var history = History(int.MaxValue);
        var members = history.Where(r => r.Change.Group == group && r.Change.RevertOf is null).ToList();
        if (members.Count == 0) return false;
        var snapshot = plans.Read(CancellationToken.None);
        return members.Any(r => RevertRequest(r, history, snapshot) is not null);
    }

    /// <summary>Undoes the changes of a group, newest first. A change whose value was changed by someone else since is left alone; the result then has the code group_partial.</summary>
    public Task<(IReadOnlyList<PowerApplyResult> Results, int Skipped)> RevertGroupAsync(Guid group, CancellationToken cancellationToken = default) =>
        RunAsync<(IReadOnlyList<PowerApplyResult>, int)>(() =>
        {
            var history = History(int.MaxValue);
            var members = history.Where(r => r.Change.Group == group && r.Change.RevertOf is null && r.State == PowerChangeState.Applied).ToList();
            var results = new List<PowerApplyResult>();
            var skipped = 0;
            var undoGroup = Guid.NewGuid();
            foreach (var member in members)
            {
                var request = RevertRequest(member, history);
                if (request is null) { skipped++; continue; }
                var result = ApplyCore(request, member.Change.Id, cancellationToken, undoGroup, member.Change.Label);
                results.Add(result);
                if (!result.Ok) { skipped++; }
            }
            return (results, skipped);
        }, cancellationToken);

    private PowerApplyResult ApplyCore(PowerRequest request, Guid? revertOf, CancellationToken ct, Guid? group = null, string? label = null)
    {
        var preview = Preview(request, ct);
        if (preview.IsRefused) return new(PowerChangeState.Refused, preview.RefusalCode!, null);
        if (!preview.WillChange) return new(PowerChangeState.Refused, PowerCode.AlreadySet, null);
        if (request.Kind is PowerChangeKind.SetValue or PowerChangeKind.SetHibernation && request.ExpectedBefore is { } expected && preview.Before != expected) return new(PowerChangeState.Refused, PowerCode.Stale, null);

        var snapshot = plans.Read(ct);
        var previousActive = snapshot.Plans.FirstOrDefault(p => p.IsActive)?.Id;
        if (request.Kind == PowerChangeKind.SetActive && request.ExpectedActivePlan is { } reviewedActive && previousActive != reviewedActive) return new(PowerChangeState.Refused, PowerCode.Stale, null);
        var change = new PowerChange(Guid.NewGuid(), request.Kind, request.Plan, preview.PlanName, request.SettingId, request.Source, preview.Before, preview.After, request.Kind == PowerChangeKind.SetActive ? previousActive : null, revertOf, group, label);
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(PowerChangeState.Refused, PowerCode.JournalFailed, null); }

        // The journal write took time: a value must still be what the person reviewed.
        if (request.Kind is PowerChangeKind.SetValue or PowerChangeKind.SetHibernation)
        {
            var again = Preview(request, ct);
            if (again.IsRefused || again.Before != preview.Before)
            {
                var stale = again.IsRefused ? again.RefusalCode! : PowerCode.Stale;
                try { journal.Complete(change.Id, PowerChangeState.Refused, stale, null, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing was changed; the entry stays open */ }
                return new(PowerChangeState.Refused, stale, null);
            }
        }

        // The same for a plan switch: the plan that was active when it was recorded as the one to come back to must still be the active one.
        if (request.Kind == PowerChangeKind.SetActive && plans.Read(ct).Plans.FirstOrDefault(p => p.IsActive)?.Id != previousActive)
        {
            try { journal.Complete(change.Id, PowerChangeState.Refused, PowerCode.Stale, null, clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing was changed; the entry stays open */ }
            return new(PowerChangeState.Refused, PowerCode.Stale, null);
        }

        Guid? created = null;
        var (state, code) = Execute(request, change, ct, ref created, () => MarkWritten(change.Id));
        var lost = false;
        try { journal.Complete(change.Id, state, code, created, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The change is made; the history shows it as interrupted. One more try, then the caller is told (a new plan copy is then not recorded as Winora's).
            try { journal.Complete(change.Id, state, code, created, clock.GetUtcNow()); }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { lost = true; }
        }
        return new(state, code, created is null ? change : change with { OtherPlan = created }, lost);
    }

    private void MarkWritten(Guid id)
    {
        try { journal.Written(id, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays unconfirmed if the end line is lost too */ }
    }

    private (PowerChangeState, string) Execute(PowerRequest request, PowerChange change, CancellationToken ct, ref Guid? created, Action written)
    {
        switch (request.Kind)
        {
            case PowerChangeKind.SetHibernation:
            {
                var on = request.Value == 1;
                var result = system!.SetHibernation(on);
                if (!result.Ok) return (PowerChangeState.Failed, result.Code);
                written();
                return system.HibernationEnabled() == on ? (PowerChangeState.Applied, PowerCode.Ok) : (PowerChangeState.Failed, PowerCode.VerifyFailed);
            }
            case PowerChangeKind.SetActive:
            {
                var result = plans.SetActive(request.Plan);
                if (!result.Ok) return (PowerChangeState.Failed, result.Code);
                written();
                return plans.Read(ct).Plans.Any(p => p.Id == request.Plan && p.IsActive) ? (PowerChangeState.Applied, PowerCode.Ok) : (PowerChangeState.Failed, PowerCode.VerifyFailed);
            }
            case PowerChangeKind.CreateCopy:
            {
                var name = string.IsNullOrWhiteSpace(request.Name) ? change.PlanName + " (Winora)" : request.Name.Trim();
                if (name.Length > 100) name = name[..100];
                var result = plans.Duplicate(request.Plan, name, out var copy);
                if (!result.Ok) return (PowerChangeState.Failed, result.Code);
                created = copy;
                try { journal.NoteCreated(change.Id, copy, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the end line carries the id too */ }
                return plans.Read(ct).Plans.Any(p => p.Id == copy) ? (PowerChangeState.Applied, PowerCode.Ok) : (PowerChangeState.Failed, PowerCode.VerifyFailed);
            }
            case PowerChangeKind.DeleteCopy:
            {
                var result = plans.Delete(request.Plan);
                if (!result.Ok) return (PowerChangeState.Failed, result.Code);
                written();
                return plans.Read(ct).Plans.Any(p => p.Id == request.Plan) ? (PowerChangeState.Failed, PowerCode.VerifyFailed) : (PowerChangeState.Applied, PowerCode.Ok);
            }
            default:
            {
                var setting = PowerSettings.Find(request.SettingId!)!;
                var result = plans.WriteValue(request.Plan, setting, request.Source!.Value, request.Value!.Value, change.Before is { } was ? (uint)was : null);
                if (!result.Ok) return (PowerChangeState.Failed, result.Code);
                written();
                if (plans.ReadValue(request.Plan, setting, request.Source.Value) != request.Value) return (PowerChangeState.Failed, PowerCode.VerifyFailed);
                // A change to the active plan takes effect when the plan is applied again.
                if (plans.Read(ct).Plans.Any(p => p.Id == request.Plan && p.IsActive) && !plans.SetActive(request.Plan).Ok) return (PowerChangeState.Applied, PowerCode.ReapplyFailed);
                return (PowerChangeState.Applied, PowerCode.Ok);
            }
        }
    }

    // ---- interrupted changes ----

    /// <summary>
    /// Closes the records of changes that were begun and never finished. Proven cases only. A value, hibernation or plan switch whose journal shows the write as made and whose live state is the
    /// intended one is Applied (code reconciled). A state that is still the one from before is Failed (interrupted_unchanged). A copy whose plan id was noted in the journal and that still
    /// exists is Applied. Everything else stays unconfirmed: a state that equals the intended one without the written line does not show who made it. The person resolves those records
    /// (<see cref="DismissAsync"/>, <see cref="RestoreBeforeAsync"/>).
    /// </summary>
    public Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken = default) =>
        RunAsync<int>(() =>
        {
            var done = 0;
            var snapshot = plans.Read(CancellationToken.None);
            foreach (var record in History(int.MaxValue).Where(IsUnconfirmed))
            {
                var c = record.Change;
                // A copy whose id Windows returned to Winora and that was noted in the journal is Winora's by that record: the plan still being there completes it.
                if (c.Kind == PowerChangeKind.CreateCopy)
                {
                    if (c.OtherPlan is { } noted && snapshot.Plans.Any(p => p.Id == noted))
                    {
                        try { journal.Complete(c.Id, PowerChangeState.Applied, PowerCode.Reconciled, noted, clock.GetUtcNow()); done++; }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays open */ }
                    }
                    continue;
                }
                if (ReadNow(c, snapshot) is not { } now) continue;
                bool wrote, old;
                if (c.Kind == PowerChangeKind.SetActive) { wrote = now != -1; old = now == -1; }
                else { wrote = now == c.After; old = now == c.Before; }
                PowerChangeState state; string code;
                if (record.Written && wrote && !old) (state, code) = (PowerChangeState.Applied, PowerCode.Reconciled);
                else if (old && !wrote) (state, code) = (PowerChangeState.Failed, PowerCode.InterruptedUnchanged);
                else continue;
                try { journal.Complete(c.Id, state, code, null, clock.GetUtcNow()); done++; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays open */ }
            }
            return done;
        }, cancellationToken);

    /// <summary>A record with a begin line and no end line.</summary>
    public static bool IsUnconfirmed(PowerChangeRecord record) => record.State is null && record.EndedUtc is null;

    /// <summary>The live state a record is about: the value, 1 or 0 for hibernation, and for a plan switch the intended value (or -1 while the plan from before is still active). Null when it cannot be read.</summary>
    private long? ReadNow(PowerChange c, PowerSnapshot snapshot) => c.Kind switch
    {
        PowerChangeKind.SetValue => PowerSettings.Find(c.SettingId ?? "") is { } setting && c.Source is { } source && snapshot.Plans.Any(p => p.Id == c.Plan) ? plans.ReadValue(c.Plan, setting, source) : null,
        PowerChangeKind.SetHibernation => system?.HibernationEnabled() is { } on ? (on ? 1 : 0) : null,
        PowerChangeKind.SetActive => snapshot.Plans.Any(p => p.Id == c.Plan && p.IsActive) ? c.After ?? 1 : snapshot.Plans.Any(p => p.Id == c.OtherPlan && p.IsActive) ? -1 : null,
        _ => null,
    };

    /// <summary>The person keeps the current state and closes an unconfirmed record. Nothing is written and the record gets no undo (Dismissed).</summary>
    public Task<PowerApplyResult> DismissAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync<PowerApplyResult>(() =>
        {
            var record = History(int.MaxValue).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return new(PowerChangeState.Refused, PowerCode.NotUnconfirmed, null);
            try { journal.Complete(changeId, PowerChangeState.Dismissed, PowerCode.Dismissed, null, clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(PowerChangeState.Refused, PowerCode.JournalFailed, null); }
            return new(PowerChangeState.Dismissed, PowerCode.Dismissed, record.Change);
        }, cancellationToken);

    /// <summary>Whether the state from before can be put back for an unconfirmed record (a value, hibernation or a plan switch whose earlier plan still exists).</summary>
    public bool CanRestoreBefore(PowerChangeRecord record) => RestoreBeforeRequest(record, plans.Read(CancellationToken.None)) is not null;

    private static string PlanName(PowerSnapshot snapshot, Guid? id) => id is { } g ? snapshot.Plans.FirstOrDefault(p => p.Id == g)?.Name ?? g.ToString() : "";

    private static RecoveryPlan PlanOf(PowerChangeRecord record, PowerRequest request, PowerSnapshot snapshot)
    {
        var line = request.Kind switch
        {
            PowerChangeKind.SetHibernation => new RecoveryLine("hibernation", request.ExpectedBefore?.ToString() ?? "", request.Value?.ToString() ?? ""),
            PowerChangeKind.SetValue => new RecoveryLine($"{request.SettingId}/{request.Source}/{PlanName(snapshot, request.Plan)}", request.ExpectedBefore?.ToString() ?? "", request.Value?.ToString() ?? ""),
            PowerChangeKind.SetActive => new RecoveryLine("active_plan", PlanName(snapshot, request.ExpectedActivePlan), PlanName(snapshot, request.Plan)),
            _ => new RecoveryLine(request.Kind.ToString(), "", ""),
        };
        return new([line], RecoveryToken.Of("power", record.Change.Id, request));
    }

    /// <summary>What a restore of the state from before would do now: the setting, what it holds and what would be put there, with a token for that state. Null when no restore is possible.</summary>
    public RecoveryPlan? PlanRestoreBefore(PowerChangeRecord record)
    {
        var snapshot = plans.Read(CancellationToken.None);
        return RestoreBeforeRequest(record, snapshot) is { } request ? PlanOf(record, request, snapshot) : null;
    }

    private PowerRequest? RestoreBeforeRequest(PowerChangeRecord record, PowerSnapshot snapshot)
    {
        if (!IsUnconfirmed(record)) return null;
        var c = record.Change;
        if (ReadNow(c, snapshot) is not { } now) return null;
        switch (c.Kind)
        {
            case PowerChangeKind.SetHibernation:
                return c.Before is { } was && now != was ? new(PowerChangeKind.SetHibernation, Guid.Empty, Value: (uint)was, ExpectedBefore: now) : null;
            case PowerChangeKind.SetValue:
                return c.Before is { } before && now != before && c.SettingId is not null && c.Source is not null ? new(PowerChangeKind.SetValue, c.Plan, c.SettingId, c.Source, (uint)before, ExpectedBefore: now) : null;
            case PowerChangeKind.SetActive:
                return now != -1 && c.OtherPlan is { } previous && snapshot.Plans.Any(p => p.Id == previous) ? new(PowerChangeKind.SetActive, previous, ExpectedActivePlan: c.Plan) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The person puts the state from before back and closes the unconfirmed record. It is an ordinary change with the usual preview checks, its own record and its own undo, and it writes only
    /// while the live state is the one just read. The unconfirmed record is then Dismissed with the code resolved_restored.
    /// </summary>
    public Task<PowerApplyResult> RestoreBeforeAsync(Guid changeId, CancellationToken cancellationToken = default, string? expectedToken = null) =>
        RunAsync<PowerApplyResult>(() =>
        {
            var record = History(int.MaxValue).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return new(PowerChangeState.Refused, PowerCode.NotUnconfirmed, null);
            var snapshot = plans.Read(cancellationToken);
            if (RestoreBeforeRequest(record, snapshot) is not { } request) return new(PowerChangeState.Refused, PowerCode.CannotRevert, null);
            // What was reviewed is what is written: a state that changed since the review needs a new review.
            if (expectedToken is not null && PlanOf(record, request, snapshot).Token != expectedToken) return new(PowerChangeState.Refused, PowerCode.Stale, null);
            var result = ApplyCore(request, null, cancellationToken, null, record.Change.Label);
            if (result.Ok)
            {
                try { journal.Complete(changeId, PowerChangeState.Dismissed, PowerCode.ResolvedRestored, null, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result with { RecordLost = true }; }
            }
            return result;
        }, cancellationToken);

    // ---- revert ----

    /// <summary>Whether the change can still be undone: it was applied, it was not undone already, and the state it left is still there.</summary>
    public bool CanRevert(PowerChangeRecord record) => RevertRequest(record, History(int.MaxValue)) is not null;

    /// <summary>The same answer for many records with one read of the journal and of the plans.</summary>
    public IReadOnlyList<bool> CanRevertAll(IReadOnlyList<PowerChangeRecord> records)
    {
        var history = History(int.MaxValue);
        var snapshot = plans.Read(CancellationToken.None);
        return records.Select(r => RevertRequest(r, history, snapshot) is not null).ToList();
    }

    /// <summary>The plans Winora made as copies and that are still there, with one read of the journal.</summary>
    public IReadOnlySet<Guid> OwnedCopySet() => OwnedCopies();

    public Task<PowerApplyResult> RevertAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync<PowerApplyResult>(() =>
        {
            var history = History(int.MaxValue);
            var record = history.FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null) return new(PowerChangeState.Refused, PowerCode.CannotRevert, null);
            var request = RevertRequest(record, history);
            return request is null ? new(PowerChangeState.Refused, PowerCode.CannotRevert, null) : ApplyCore(request, record.Change.Id, cancellationToken);
        }, cancellationToken);

    private PowerRequest? RevertRequest(PowerChangeRecord record, IReadOnlyList<PowerChangeRecord> history, PowerSnapshot? shared = null)
    {
        if (record.State != PowerChangeState.Applied) return null;
        var c = record.Change;
        if (c.RevertOf is not null) return null;
        if (history.Any(r => r.Change.RevertOf == c.Id && r.State == PowerChangeState.Applied)) return null;
        var snapshot = shared ?? plans.Read(CancellationToken.None);
        switch (c.Kind)
        {
            case PowerChangeKind.SetHibernation:
                if (c.Before is not { } wasOn || c.After is not { } nowOn || system?.HibernationEnabled() is not { } actual || (actual ? 1 : 0) != nowOn) return null;
                return new(PowerChangeKind.SetHibernation, Guid.Empty, Value: (uint)wasOn, ExpectedBefore: nowOn);
            case PowerChangeKind.SetActive:
                // Only while the plan that was switched to is still the active one, and the earlier plan still exists.
                if (c.OtherPlan is not { } previous || !snapshot.Plans.Any(p => p.Id == previous) || !snapshot.Plans.Any(p => p.Id == c.Plan && p.IsActive)) return null;
                return new(PowerChangeKind.SetActive, previous);
            case PowerChangeKind.SetValue:
                if (PowerSettings.Find(c.SettingId ?? "") is not { } setting || c.Source is null || c.Before is null || c.After is null) return null;
                if (!snapshot.Plans.Any(p => p.Id == c.Plan) || plans.ReadValue(c.Plan, setting, c.Source.Value) != c.After) return null;
                return new(PowerChangeKind.SetValue, c.Plan, c.SettingId, c.Source, (uint)c.Before.Value, ExpectedBefore: c.After);
            case PowerChangeKind.CreateCopy:
                if (c.OtherPlan is not { } copy || !snapshot.Plans.Any(p => p.Id == copy && !p.IsActive && !p.IsBuiltIn)) return null;
                return new(PowerChangeKind.DeleteCopy, copy);
            default:
                return null;
        }
    }

    private HashSet<Guid> OwnedCopies()
    {
        var history = journal.History(int.MaxValue);
        var owned = new HashSet<Guid>();
        foreach (var r in history.Reverse())
        {
            if (r.State != PowerChangeState.Applied) continue;
            if (r.Change.Kind == PowerChangeKind.CreateCopy && r.Change.OtherPlan is { } copy) owned.Add(copy);
            else if (r.Change.Kind == PowerChangeKind.DeleteCopy) owned.Remove(r.Change.Plan);
        }
        return owned;
    }
}
