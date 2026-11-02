// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;
namespace Winora.Application;

/// <summary>
/// The only entry point for machine-scope changes. It plans from an unelevated read, journals every durable boundary BEFORE the step it
/// guards, drives one channel session per batch, and decides every outcome from its OWN read-back, never from what the helper claims. It takes no path, root, database,
/// logger switch or shell-refresh parameter. Recovery and the drift check are read-only: they never launch the helper.
/// A batch holds one operation in this milestone (the channel session has no per-operation journal hooks yet); larger batches are refused.
/// </summary>
public sealed class MachineSettingsExecutor(
    IMachineOperationStore store, IMachineDefinitionSource definitions, IMachineStateReader reader, IMachinePolicyReader policy, IMachineChannel channel,
    IMachineDigests digests, IOsBuildProvider osBuild, IProcessLiveness liveness, IWorkerPresence worker, IBootSession boot, TimeProvider? timeProvider = null,
    Action<string>? boundary = null) : IMachineSettingsExecutor
{
    public const string CodePrepared = "operation.prepared";
    public const string CodeExecuting = "operation.executing";
    public const string CodeApplied = "operation.applied";
    public const string CodeAlreadyConfigured = "operation.already_configured";
    public const string CodeRestartPending = "operation.restart_pending";
    public const string CodeReverted = "operation.reverted";
    public const string CodeRollbackPrepared = "operation.rollback_prepared";
    public const string CodeRollbackCommitted = "operation.rollback_committed";
    public const string CodeRollbackAbandoned = "operation.rollback_abandoned";
    public const string CodeReconciledUnchanged = "operation.reconciled_unchanged";
    public const string CodeReconciledApplied = "operation.reconciled_applied";
    public const string CodeDeclined = ErrorCode.ElevationDeclined;
    public static readonly TimeSpan ReviewDelay = TimeSpan.FromMinutes(10);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    // ---- plan -------------------------------------------------------------------------------

    public Task<MachinePlan> PlanAsync(string definitionId, string choiceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Plan(definitionId, choiceId));
    }

    private MachinePlan Plan(string definitionId, string choiceId)
    {
        var result = definitions.Get();
        if (result.Set is null) return Refused(definitionId, choiceId, 0, "", result.Code ?? ErrorCode.DefinitionSignatureInvalid);
        var set = result.Set;
        var definition = set.Find(definitionId);
        if (definition is null) return Refused(definitionId, choiceId, set.Serial, set.HashHex, ErrorCode.DefinitionUnknown);
        MachinePlan Fail(string code) => Refused(definition.Id, choiceId, set.Serial, set.HashHex, code, definition);
        if (set.RevocationOf(definition.Id, definition.Version) is not null) return Fail(ErrorCode.DefinitionRevoked);
        if (definition.TargetKind == MachineTargetKind.DisplayDriverValue) return Fail(ErrorCode.TargetKindUnsupported);
        if (!HasTarget(definition)) return Fail(ErrorCode.TargetNotAllowed);
        if (osBuild.CurrentBuild is not { } build || build < definition.MinBuild || (definition.MaxBuild is { } max && build > max)) return Fail(ErrorCode.SettingUnsupportedBuild);
        var choice = definition.Choices.FirstOrDefault(c => c.Id == choiceId);
        if (choice?.EffectiveValue is not { } desiredValue) return Fail(ErrorCode.SettingChoiceInvalid);
        if (policy.IsManagedByPolicy(definition)) return Fail(ErrorCode.SettingManagedByPolicy);
        var read = reader.Read(definition);
        if (!read.Ok) return Fail(read.Code ?? ErrorCode.AccessDenied);
        if (!InRestoreDomain(definition, read.Snapshot!)) return Fail(ErrorCode.SettingStateUnrecognized);
        var willChange = !Satisfies(read.Snapshot!, desiredValue);
        var token = digests.PlanTokenApply(definition.Id, (uint)definition.Version, choice.Id, read.Snapshot!, read.KeyRevision);
        return new MachinePlan(Guid.NewGuid(), definition.Id, definition.Version, choice.Id, definition.TitleKey, read.Snapshot!, read.KeyRevision, desiredValue, willChange, token,
            set.Serial, set.HashHex, definition.RequiresRestart, null);
    }

    private static MachinePlan Refused(string definitionId, string choiceId, long serial, string setHash, string code, MachineSettingDefinition? definition = null) =>
        new(Guid.Empty, definitionId, definition?.Version ?? 0, choiceId, definition?.TitleKey ?? "", MachineSnapshot.SelftestEmpty, 0, null, false, default, serial, setHash,
            definition?.RequiresRestart ?? false, code);

    public string BatchDigestShort(IReadOnlyList<MachinePlan> plans) => Convert.ToHexStringLower(digests.BatchDigest(BuildBatch(plans).Batch).ToArray())[..8];

    // ---- apply ---------------------------------------------------------------

    public async Task<IReadOnlyList<MachineOutcome>> ApplyBatchAsync(IReadOnlyList<MachinePlan> reviewed, ElevationConfirmation confirmation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        if (reviewed.Count != 1) throw new ArgumentException("one operation per batch in this milestone", nameof(reviewed));
        var plan = reviewed[0];
        if (plan.IsRefused) throw new OperationException(plan.RefusalCode!);
        if (!confirmation.Interactive) throw new OperationException(ErrorCode.ElevationConfirmationRequired);
        cancellationToken.ThrowIfCancellationRequested();

        var id = plan.OperationId;
        var (batch, items) = BuildBatch(reviewed);
        var digest = digests.BatchDigest(batch);
        if (!string.Equals(Convert.ToHexStringLower(digest.ToArray())[..8], confirmation.BatchDigestShort, StringComparison.OrdinalIgnoreCase))
            throw new OperationException(ErrorCode.ElevationConfirmationRequired);

        // Re-plan under the same definition set: anything the user did not review voids the consent.
        var fresh = Plan(plan.DefinitionId, plan.ChoiceId);
        if (fresh.IsRefused) throw new OperationException(fresh.RefusalCode!);
        if (!fresh.PlanToken.Equals(plan.PlanToken) || fresh.SetHash != plan.SetHash || fresh.SetSerial != plan.SetSerial) throw new OperationException(ErrorCode.PlanStale);
        var (pid, ticks) = liveness.Current;
        var now = clock.GetUtcNow();
        var row = store.PrepareMachine(new MachineOperationRecord(id, plan.DefinitionId, plan.DefinitionVersion, plan.SetSerial, plan.SetHash, Convert.ToHexStringLower(digest.ToArray()),
            Convert.ToHexStringLower(plan.PlanToken.ToArray()), plan.ChoiceId, OperationState.Prepared, false, false, CodePrepared, MachineHex.Encode(plan.Before), plan.BeforeRevision,
            null, null, null, null, null, plan.RequiresRestart, pid, ticks, boot.Id, false, MachineOperationRecord.CurrentSchemaVersion, now, now));
        boundary?.Invoke("B1");

        var before = plan.Before;
        MachineBatchResult session;
        try
        {
            session = await channel.RunBatchAsync(batch, (item, prepared) =>
            {
                boundary?.Invoke("B5");
                // rule 3: the worker's own read must equal the client's before-state, or the plan is stale and nothing is written.
                if (!prepared.Observed.Equals(before) || prepared.KeyRevision != plan.BeforeRevision) return ValueTask.FromResult(false);
                var (p, t) = liveness.Current;
                row = Save(row with { State = OperationState.Executing, Code = CodeExecuting, WorkerBefore = MachineHex.Encode(prepared.Observed), OwnerProcessId = p, OwnerProcessStartTicks = t, BootSession = boot.Id });
                boundary?.Invoke("B6");
                return ValueTask.FromResult(true);
            }, confirmation.SecondConfirmationGiven, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before any Commit could be sent unless the row says otherwise; the row is resolved from the live state.
            return [FinishFromObservation(row, Plan0(plan), true)];
        }

        return [Conclude(row, plan, session)];
    }

    private MachineOutcome Conclude(MachineOperationRecord row, MachinePlan plan, MachineBatchResult session)
    {
        var op = session.Operations.FirstOrDefault();
        var prepared = op?.Prepared;
        var outcome = op?.Outcome;

        if (row.State == OperationState.Prepared)
        {
            // The session ended before a Commit became possible.
            if (session.DeclinedByCaller)
                return Terminal(row, OperationState.Failed, ErrorCode.PlanStale);
            if (prepared is null)
                return Terminal(row, OperationState.Failed, session.FailureCode ?? (session.Launched ? ErrorCode.ElevationChannelFailed : CodeDeclined));
            if (prepared.Status == ElevationStatus.AlreadyConfigured || prepared.Status == ElevationStatus.Ready && !prepared.WillChange)
                return Terminal(row, OperationState.AlreadyConfigured, CodeAlreadyConfigured, After: prepared.Observed, AfterRevision: prepared.KeyRevision);
            if (prepared.Status == ElevationStatus.Refused)
                return Terminal(row, OperationState.Failed, prepared.Result.Code);
            if (prepared.Status == ElevationStatus.Conflict)
                return Terminal(row, OperationState.Failed, ErrorCode.ExternalConflict);
            if (prepared.Status == ElevationStatus.Failed)
                return Terminal(row, OperationState.Failed, prepared.Result.Code);
            return Terminal(row, OperationState.Failed, session.FailureCode ?? ErrorCode.ElevationChannelFailed);
        }

        // Executing: Commit was (or may have been) sent. Decide from our own observation.
        boundary?.Invoke("B9");
        if (outcome is null || !session.CommitSent)
            return FinishFromObservation(row, plan, false);
        return FinishFromReadBack(row, plan, outcome);
    }

    private MachineOutcome FinishFromReadBack(MachineOperationRecord row, MachinePlan plan, Outcome outcome)
    {
        var definition = DefinitionOf(row);
        var read = definition is null ? MachineStateRead.Failed(ErrorCode.DefinitionUnknown) : reader.Read(definition);
        if (!read.Ok)
            return new(row.Id, row.DefinitionId, row.State, ErrorCode.RecoveryRequired, true);       // cannot read back: stay open; recovery re-reads later
        var desired = plan.DesiredValue!;
        var satisfied = Satisfies(read.Snapshot!, desired);
        var after = read.Snapshot!; var rev = read.KeyRevision;
        OperationState state; string code;
        switch (outcome.Status)
        {
            case ElevationStatus.Applied when satisfied:
                (state, code) = row.RequiresRestart ? (OperationState.PendingRestart, CodeRestartPending) : (OperationState.Applied, CodeApplied); break;
            case ElevationStatus.AlreadyConfigured when satisfied:
                (state, code) = (OperationState.AlreadyConfigured, CodeAlreadyConfigured); break;
            case ElevationStatus.Failed when after.Equals(MachineSnapshotOf(row.Before)):
                (state, code) = (OperationState.Failed, outcome.Result.Code); break;
            case ElevationStatus.Conflict when after.Equals(MachineSnapshotOf(row.Before)):
                (state, code) = (OperationState.Failed, ErrorCode.ExternalConflict); break;
            default:
                // The helper's claim and the machine disagree, or the state moved: never report success on the helper's word.
                (state, code) = (OperationState.Partial, outcome.Status == ElevationStatus.Applied ? ErrorCode.VerificationFailed : ErrorCode.ReconciledPartial); break;
        }
        boundary?.Invoke("B10");
        return Terminal(row, state, code, After: after, AfterRevision: rev);
    }

    // ---- revert --------------------------------------------------------------

    private sealed record RevertContext(MachineOperationRecord Row, MachineSettingDefinition Definition, MachineValue RestoreValue, MachineSnapshot After, ulong AfterRevision,
        Bytes32 Token, BatchBegin Batch, string DigestShort);

    /// <summary>Every precondition of a revert, checked by the CLIENT before any launch. Raises a typed refusal; changes nothing.</summary>
    private RevertContext BuildRevert(Guid operationId)
    {
        var row = store.GetMachine(operationId);
        if (row.State is not (OperationState.Applied or OperationState.PendingRestart) || row.CompensationPrepared) throw new OperationException(ErrorCode.NotReversible);
        var result = definitions.Get();
        if (result.Set is null) throw new OperationException(result.Code ?? ErrorCode.DefinitionSignatureInvalid);
        var set = result.Set;
        var definition = set.Find(row.DefinitionId) ?? throw new OperationException(ErrorCode.NotReversible);
        if (definition.Version != row.DefinitionVersion && !definition.RestoreCompatible.Contains(row.DefinitionVersion)) throw new OperationException(ErrorCode.NotReversible);
        if (set.RevocationOf(row.DefinitionId, row.DefinitionVersion) is { AllowRestore: false }) throw new OperationException(ErrorCode.NotReversible);
        if (definition.TargetKind == MachineTargetKind.DisplayDriverValue) throw new OperationException(ErrorCode.TargetKindUnsupported);
        if (!HasTarget(definition)) throw new OperationException(ErrorCode.TargetNotAllowed);
        if (!MachineHex.TryDecodeSnapshot(row.Before, out var beforeSnapshot) || !MachineHex.TryDecodeSnapshot(row.After, out var afterSnapshot) || row.AfterRevision is not { } afterRevision)
            throw new OperationException(ErrorCode.NotReversible);
        if (!beforeSnapshot.TryReadValue(out var restoreValue) || !definition.EffectiveRestoreDomain.Contains(restoreValue)) throw new OperationException(ErrorCode.NotReversible);

        var read = reader.Read(definition);
        if (!read.Ok) throw new OperationException(read.Code ?? ErrorCode.AccessDenied);
        if (!read.Snapshot!.Equals(afterSnapshot) || read.KeyRevision != afterRevision)
            throw new OperationException(store.ListMachineOperations().Any(o => o.Id != row.Id && o.DefinitionId == row.DefinitionId && o.CreatedAtUtc > row.CreatedAtUtc) ? ErrorCode.Superseded : ErrorCode.ExternalConflict);

        var token = digests.PlanTokenRestore(row.DefinitionId, (uint)row.DefinitionVersion, afterSnapshot, afterRevision);
        var batch = new BatchBegin([new BatchItem(row.Id, row.DefinitionId, (uint)row.DefinitionVersion, PrepareMode.Restore, "", restoreValue, token)]);
        return new(row, definition, restoreValue, afterSnapshot, afterRevision, token, batch, Convert.ToHexStringLower(digests.BatchDigest(batch).ToArray())[..8]);
    }

    public string RevertDigestShort(Guid operationId) => BuildRevert(operationId).DigestShort;

    public async Task<MachineOutcome> RevertAsync(Guid operationId, ElevationConfirmation confirmation, CancellationToken cancellationToken)
    {
        if (!confirmation.Interactive) throw new OperationException(ErrorCode.ElevationConfirmationRequired);
        cancellationToken.ThrowIfCancellationRequested();
        var context = BuildRevert(operationId);
        var (row, definition, restoreValue, afterSnapshot, afterRevision, token, batch) = (context.Row, context.Definition, context.RestoreValue, context.After, context.AfterRevision, context.Token, context.Batch);
        if (!string.Equals(context.DigestShort, confirmation.BatchDigestShort, StringComparison.OrdinalIgnoreCase)) throw new OperationException(ErrorCode.ElevationConfirmationRequired);

        var (pid, ticks) = liveness.Current;
        row = Save(row with { CompensationPrepared = true, Restore = row.Before, RestorePlanToken = Convert.ToHexStringLower(token.ToArray()), Code = CodeRollbackPrepared,
            OwnerProcessId = pid, OwnerProcessStartTicks = ticks, BootSession = boot.Id });
        boundary?.Invoke("C1");

        MachineBatchResult session;
        try
        {
            session = await channel.RunBatchAsync(batch, (_, prepared) =>
            {
                // C2: the worker compared live state with the recorded after-state; a mismatch is a conflict, not a stale plan.
                if (!prepared.Observed.Equals(afterSnapshot) || prepared.KeyRevision != afterRevision) return ValueTask.FromResult(false);
                row = Save(row with { CompensationCommitted = true, Code = CodeRollbackCommitted, BootSession = boot.Id });
                boundary?.Invoke("C3");
                return ValueTask.FromResult(true);
            }, confirmation.SecondConfirmationGiven, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ResolveCompensation(row, definition);
        }

        var op = session.Operations.FirstOrDefault();
        if (!row.CompensationCommitted)
        {
            // No Commit was possible: back to the applied row.
            var code = session.DeclinedByCaller ? ErrorCode.ExternalConflict : session.FailureCode ?? op?.Prepared?.Result.Code ?? CodeDeclined;
            row = Save(row with { CompensationPrepared = false, Code = CodeRollbackAbandoned });
            return new(row.Id, row.DefinitionId, row.State, code, false);
        }
        boundary?.Invoke("C4");
        if (op?.Outcome is null) return ResolveCompensation(row, definition);
        var after = reader.Read(definition);
        if (!after.Ok) return new(row.Id, row.DefinitionId, row.State, ErrorCode.RecoveryRequired, true);
        var reverted = Satisfies(after.Snapshot!, restoreValue);
        boundary?.Invoke("C5");
        row = Save(row with
        {
            State = reverted ? OperationState.Reverted : OperationState.Partial, CompensationPrepared = false, CompensationCommitted = false, After = MachineHex.Encode(after.Snapshot!),
            AfterRevision = after.KeyRevision, Code = reverted ? CodeReverted : ErrorCode.RollbackUncertain,
        });
        return new(row.Id, row.DefinitionId, row.State, row.Code, false);
    }

    // ---- recovery --------------------------------------------------

    public Task<IReadOnlyList<MachineOutcome>> RecoverAsync(CancellationToken cancellationToken)
    {
        var results = new List<MachineOutcome>();
        foreach (var entry in store.ListRecoverableMachineEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry.Record is { } live && liveness.IsAlive(live.OwnerProcessId, live.OwnerProcessStartTicks)) continue;     // its owner is still working
                var set = definitions.Get().Set;
                MachineSettingDefinition? definition = null;
                var fault = entry.Fault ?? (entry.Record is null ? ErrorCode.JournalInvalid : (definition = Trust(set, entry.Record)) is null ? ErrorCode.UntrustedDefinition : null);
                if (fault is not null || definition is null || entry.Record is null)
                {
                    store.QuarantineMachine(entry.Id, entry.Revision, fault ?? ErrorCode.UntrustedDefinition);
                    continue;
                }
                var outcome = Reconcile(entry.Record, definition);
                if (outcome is not null) results.Add(outcome);
            }
            catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict) { /* another process resolved it first */ }
        }
        return Task.FromResult<IReadOnlyList<MachineOutcome>>(results);
    }

    private MachineOutcome? Reconcile(MachineOperationRecord row, MachineSettingDefinition definition)
    {
        // Worker-gone gate: no verdict while a worker that might write could be alive (rows in Executing and rows with the revert commit flag).
        var needsGate = row.State == OperationState.Executing || row.CompensationCommitted;
        if (needsGate && !WorkerGone(row)) return new(row.Id, row.DefinitionId, row.State, ErrorCode.RecoveryRequired, true);
        var read = reader.Read(definition);
        if (!read.Ok) return new(row.Id, row.DefinitionId, row.State, ErrorCode.RecoveryRequired, true);        // stays open; the user may mark it reviewed (11.5)
        return row.CompensationPrepared ? ReconcileCompensation(row, definition, read) : ReconcileForward(row, definition, read);
    }

    private bool WorkerGone(MachineOperationRecord row) =>
        worker.Probe() == WorkerPresence.Gone || !string.Equals(row.BootSession, boot.Id, StringComparison.Ordinal);

    private MachineOutcome ReconcileForward(MachineOperationRecord row, MachineSettingDefinition definition, MachineStateRead read)
    {
        var live = read.Snapshot!;
        var choice = definition.Choices.FirstOrDefault(c => c.Id == row.ChoiceId);
        var desired = choice?.EffectiveValue;
        var unchanged = live.Equals(MachineSnapshotOf(row.Before));
        var isDesired = desired is not null && Satisfies(live, desired);
        if (row.State == OperationState.Prepared)
        {
            // B1..B5: no Commit was ever sent, so nothing of ours can have been written.
            if (unchanged && isDesired) return Finish(row, OperationState.AlreadyConfigured, CodeAlreadyConfigured, live, read.KeyRevision);
            if (unchanged) return Finish(row, OperationState.Failed, CodeReconciledUnchanged, live, read.KeyRevision);
            if (isDesired) return Finish(row, OperationState.Partial, ErrorCode.ReconciledPartial, live, read.KeyRevision);
            return Finish(row, OperationState.Partial, ErrorCode.ExternalConflict, live, read.KeyRevision);
        }
        // B6..B9 (Executing, worker gone): value plus revision decide whether the write was ours.
        if (unchanged) return Finish(row, OperationState.Failed, CodeReconciledUnchanged, live, read.KeyRevision);
        if (isDesired)
        {
            if (read.KeyRevision == row.BeforeRevision) return Finish(row, OperationState.Partial, ErrorCode.ExternalConflict, live, read.KeyRevision);   // cannot be attributed
            return row.RequiresRestart
                ? Finish(row, OperationState.PendingRestart, CodeRestartPending, live, read.KeyRevision)
                : Finish(row, OperationState.Applied, CodeReconciledApplied, live, read.KeyRevision);
        }
        return Finish(row, OperationState.Partial, ErrorCode.ReconciledPartial, live, read.KeyRevision);
    }

    private MachineOutcome ReconcileCompensation(MachineOperationRecord row, MachineSettingDefinition definition, MachineStateRead read)
    {
        var live = read.Snapshot!;
        var recordedAfter = row.After is null ? null : MachineSnapshotOf(row.After);
        var atAfter = recordedAfter is not null && live.Equals(recordedAfter);
        if (!row.CompensationCommitted)
        {
            // C1/C2: no Commit possible, so a revert never started.
            return atAfter ? Release(row, CodeRollbackAbandoned) : Finish(row, OperationState.Partial, ErrorCode.ExternalConflict, live, read.KeyRevision);
        }
        if (atAfter) return Release(row, CodeRollbackAbandoned);
        var restore = row.Restore is null ? null : MachineSnapshotOf(row.Restore);
        if (restore is not null && restore.TryReadValue(out var value) && Satisfies(live, value) && read.KeyRevision != row.AfterRevision)
            return Finish(row, OperationState.Reverted, CodeReverted, live, read.KeyRevision);
        return Finish(row, OperationState.Partial, ErrorCode.ExternalConflict, live, read.KeyRevision);
    }

    private MachineOutcome ResolveCompensation(MachineOperationRecord row, MachineSettingDefinition definition)
    {
        if (!WorkerGone(row)) return new(row.Id, row.DefinitionId, row.State, ErrorCode.RecoveryRequired, true);
        var read = reader.Read(definition);
        return read.Ok ? ReconcileCompensation(row, definition, read) : new(row.Id, row.DefinitionId, row.State, ErrorCode.RecoveryRequired, true);
    }

    private MachineOutcome FinishFromObservation(MachineOperationRecord row, MachinePlan plan, bool cancelled)
    {
        var definition = DefinitionOf(row);
        if (definition is null) return new(row.Id, row.DefinitionId, row.State, ErrorCode.RecoveryRequired, true);
        if (row.State == OperationState.Executing && !WorkerGone(row)) return new(row.Id, row.DefinitionId, row.State, ErrorCode.RecoveryRequired, true);
        var read = reader.Read(definition);
        if (!read.Ok) return new(row.Id, row.DefinitionId, row.State, ErrorCode.RecoveryRequired, true);
        return ReconcileForward(row, definition, read);
    }

    // ---- drift check and review ------------------------------------------

    /// <summary>
    /// Read-only startup check of machine rows in Applied or PendingRestart: when the live state no longer equals the recorded after-state the row becomes
    /// <c>Partial/operation.external_conflict</c>. Nothing is written to the machine.
    /// </summary>
    public IReadOnlyList<MachineOutcome> DriftCheck()
    {
        var results = new List<MachineOutcome>();
        var set = definitions.Get().Set;
        foreach (var row in store.ListMachineOperations().Where(r => r.State is OperationState.Applied or OperationState.PendingRestart && !r.CompensationPrepared))
        {
            var definition = Trust(set, row);
            if (definition is null || row.After is null) continue;
            var read = reader.Read(definition);
            if (!read.Ok) continue;
            if (read.Snapshot!.Equals(MachineSnapshotOf(row.After))) continue;
            try { results.Add(Finish(row, OperationState.Partial, ErrorCode.ExternalConflict, read.Snapshot, read.KeyRevision)); }
            catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict) { }
        }
        return results;
    }

    /// <summary>
    /// "Mark as reviewed" for a row recovery cannot resolve (target unreadable, probe inconclusive). Allowed only when the worker is provably gone or the row has not been
    /// written for ten minutes: the one decision that uses wall-clock time and the only one a person triggers. No machine action happens.
    /// </summary>
    public MachineOutcome MarkReviewed(Guid operationId)
    {
        var row = store.GetMachine(operationId);
        if (!MachineTransitions.IsActive(row.State, row.CompensationPrepared)) throw new OperationException(ErrorCode.InvalidTransition);
        var stale = clock.GetUtcNow() - row.UpdatedAtUtc >= ReviewDelay;
        if (!stale && !WorkerGone(row)) throw new OperationException(ErrorCode.RecoveryRequired);
        var saved = Save(row with { State = OperationState.Partial, CompensationPrepared = false, CompensationCommitted = false, Code = ErrorCode.RecoveryRequired, ReviewAcknowledged = true });
        return new(saved.Id, saved.DefinitionId, saved.State, saved.Code, false);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private (BatchBegin Batch, IReadOnlyList<BatchItem> Items) BuildBatch(IReadOnlyList<MachinePlan> plans)
    {
        var items = plans.Select(p => new BatchItem(p.OperationId, p.DefinitionId, (uint)p.DefinitionVersion, PrepareMode.ApplyChoice, p.ChoiceId, MachineValue.Absent, p.PlanToken)).ToList();
        return (new BatchBegin(items), items);
    }

    private MachinePlan Plan0(MachinePlan plan) => plan;

    private MachineOperationRecord Save(MachineOperationRecord record) => store.SaveMachine(record with { UpdatedAtUtc = clock.GetUtcNow() });

    private MachineOutcome Terminal(MachineOperationRecord row, OperationState state, string code, MachineSnapshot? After = null, ulong? AfterRevision = null)
    {
        var saved = Save(row with { State = state, Code = code, After = After is null ? row.After : MachineHex.Encode(After), AfterRevision = AfterRevision ?? row.AfterRevision });
        return new(saved.Id, saved.DefinitionId, saved.State, saved.Code, false);
    }

    private MachineOutcome Finish(MachineOperationRecord row, OperationState state, string code, MachineSnapshot after, ulong revision)
    {
        var saved = Save(row with
        {
            State = state, CompensationPrepared = false, CompensationCommitted = false, Code = code, After = MachineHex.Encode(after), AfterRevision = revision,
        });
        return new(saved.Id, saved.DefinitionId, saved.State, saved.Code, false);
    }

    private MachineOutcome Release(MachineOperationRecord row, string code)
    {
        var saved = Save(row with { CompensationPrepared = false, CompensationCommitted = false, Code = code });
        return new(saved.Id, saved.DefinitionId, saved.State, saved.Code, false);
    }

    private MachineSettingDefinition? DefinitionOf(MachineOperationRecord row) => Trust(definitions.Get().Set, row);

    /// <summary>The definition a journal row may be reconciled against: the id exists and its version is the row's or lists the row's version as restore-compatible.</summary>
    private static MachineSettingDefinition? Trust(VerifiedDefinitionSet? set, MachineOperationRecord row)
    {
        var definition = set?.Find(row.DefinitionId);
        if (definition is null) return null;
        return definition.Version == row.DefinitionVersion || definition.RestoreCompatible.Contains(row.DefinitionVersion) ? definition : null;
    }

    private static MachineSnapshot MachineSnapshotOf(string hex) => MachineHex.TryDecodeSnapshot(hex, out var s) ? s : MachineSnapshot.SelftestEmpty;

    /// <summary>The state is "desired" when the value (absent when no value exists) equals the choice value, whatever the key does.</summary>
    internal static bool Satisfies(MachineSnapshot snapshot, MachineValue desired) =>
        snapshot.TryReadValue(out var value) && value.Equals(desired);

    /// <summary>A prior state is admissible only when its value is a member of the restore domain by kind and exact content.</summary>
    internal static bool InRestoreDomain(MachineSettingDefinition definition, MachineSnapshot observed) =>
        observed.TryReadValue(out var value) && definition.EffectiveRestoreDomain.Contains(value)
        && (definition.TargetKind != MachineTargetKind.RegistryValue || observed.TryReadRegistry(out _, out var view, out _) && view == (definition.Registry?.View ?? view));

    /// <summary>A bit-field definition is changed only by the in-process backend (read-modify-write); this channel compares whole values and refuses it.</summary>
    private static bool HasTarget(MachineSettingDefinition definition) =>
        definition.TargetKind == MachineTargetKind.RegistryValue && definition.Registry is not null && !definition.IsBitField || definition.TargetKind == MachineTargetKind.ServiceStartType && definition.Service is not null;
}
