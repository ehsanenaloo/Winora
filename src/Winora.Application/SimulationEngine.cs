// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

// Synchronous by design: one bounded local simulated step; no remote or Windows calls.
public sealed class SimulationEngine(IOperationStore store)
{
    public const string FeatureId = "simulation.boolean";
    public const string ResourceId = "simulation:/setting";

    public SimulationPlan Plan(bool desired)
    {
        var state = store.ReadResource();
        return new(state.Value, desired, state.Value != desired) { Token = new(state.Value, state.Revision, desired) };
    }

    public OperationRecord Apply(bool desired, CancellationToken cancellationToken = default) => Apply(desired, null, cancellationToken);

    /// <summary>Applies the desired value. When <paramref name="plan"/> is supplied, fails with plan.stale unless the resource still matches the preview.</summary>
    public OperationRecord Apply(bool desired, PlanToken? plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan is not null && plan.Desired != desired) throw new OperationException(ErrorCode.PlanStale);
        var record = store.Prepare(plan, state => state.Value == desired
            ? NewRecord(state, desired, OperationState.AlreadyConfigured, "operation.already_configured") with { After = state.Value }
            : NewRecord(state, desired, OperationState.Prepared, "operation.prepared")); // Failures here MUST escape before mutation.
        if (record.State == OperationState.AlreadyConfigured) return record;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                store.Save(record with { State = OperationState.Failed, Code = ErrorCode.Cancelled });
                cancellationToken.ThrowIfCancellationRequested();
            }
            record = store.Save(record with { State = OperationState.Executing, Code = "operation.executing" });
            try
            {
                var revision = store.TryCompareExchange(record.Id, record.Revision, record.Before, record.SourceRevision!.Value, desired);
                var actual = store.ReadResource();
                if (revision is null)
                    record = record with { State = OperationState.Failed, After = actual.Value, Code = ErrorCode.ExternalConflict };
                else if (actual.Value == desired && actual.Revision == revision)
                    record = record with { State = OperationState.Applied, After = actual.Value, AppliedRevision = revision, Code = "operation.applied" };
                else
                    record = record with { State = OperationState.Partial, After = actual.Value, Code = ErrorCode.VerificationFailed };
            }
            catch (Exception e) when (e is IOException or InvalidOperationException && !IsRevisionConflict(e))
            {
                record = Reconcile(record, store.ReadResource());
            }
            return store.Save(record);
        }
        catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict)
        {
            // A concurrent recovery already resolved this row before the mutation; its verdict stands.
            return store.Get(record.Id);
        }
    }

    public IReadOnlyList<OperationRecord> Recover()
    {
        var recovered = new List<OperationRecord>();
        foreach (var entry in store.ListRecoverableEntries())
        {
            var fault = entry.Fault ?? (entry.Record is null ? ErrorCode.JournalInvalid : DefinitionFault(entry.Record));
            if (fault is not null)
            {
                store.Quarantine(entry.Id, entry.Revision, fault);
                continue;
            }
            var record = entry.Record!;
            // The verdict commits only if the resource is still what it was when read: a live writer that mutates in between
            // would otherwise be declared "unchanged". A moved resource is simply re-read.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var actual = store.ReadResource();
                var result = record.CompensationPrepared ? ReconcileCompensation(record, actual) : Reconcile(record, actual);
                try { recovered.Add(store.Save(result, actual)); break; }
                catch (OperationException e) when (e.Code == ErrorCode.ResourceChanged) { /* re-read and decide again */ }
                catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict) { break; /* another process resolved it first */ }
            }
        }
        return recovered;
    }

    public OperationRecord Rollback(Guid id)
    {
        var record = store.Get(id);
        Validate(record);
        if (record.State == OperationState.Reverted) return record; // idempotent
        if (record.State is not (OperationState.Applied or OperationState.PendingRestart) || record.CompensationPrepared)
            throw new OperationException(ErrorCode.NotReversible);
        if (record.AppliedRevision is not long applied) throw new OperationException(ErrorCode.RevisionUnknown);
        var actual = store.ReadResource();
        try
        {
            if (actual.Value != record.After || actual.Revision != applied) return Conflict(record, applied, actual);
            try
            {
                // The resource check and the compensation intent commit atomically, so no other writer can slip in between.
                record = store.Save(record with { CompensationPrepared = true, Code = "operation.rollback_prepared" }, actual);
            }
            catch (OperationException e) when (e.Code == ErrorCode.ResourceChanged)
            {
                return Conflict(record, applied, store.ReadResource());
            }
            try
            {
                var revision = store.TryCompareExchange(record.Id, record.Revision, actual.Value, actual.Revision, record.Before);
                var after = store.ReadResource();
                if (revision is null)
                    record = record with { State = OperationState.Partial, After = after.Value, CompensationPrepared = false, Code = ErrorCode.ExternalConflict };
                else
                    record = record with { State = after.Value == record.Before ? OperationState.Reverted : OperationState.Partial, After = after.Value,
                        CompensationPrepared = false, Code = after.Value == record.Before ? "operation.reverted" : ErrorCode.RollbackFailed };
            }
            catch (Exception e) when (e is IOException or InvalidOperationException && !IsRevisionConflict(e))
            {
                record = ReconcileCompensation(record, store.ReadResource());
            }
            return store.Save(record);
        }
        catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict)
        {
            return store.Get(id); // concurrent rollback or recovery already decided
        }
    }

    // The resource no longer matches what this operation produced: a newer Winora operation owns it (refuse, record stays as history)
    // or something external changed it (classify Partial, never overwrite).
    private OperationRecord Conflict(OperationRecord record, long applied, ResourceState actual)
    {
        if (store.ListOperations().Any(o => o.Id != record.Id && o.SourceRevision >= applied))
            throw new OperationException(ErrorCode.Superseded);
        return store.Save(record with { State = OperationState.Partial, After = actual.Value, Code = ErrorCode.ExternalConflict });
    }
    private static bool IsRevisionConflict(Exception e) => e is OperationException { Code: ErrorCode.RevisionConflict };

    private static OperationRecord NewRecord(ResourceState state, bool desired, OperationState initial, string code) =>
        new(Guid.NewGuid(), FeatureId, initial, state.Value, desired, null, code, ResourceId) { SourceRevision = state.Revision };

    private static string? DefinitionFault(OperationRecord record) =>
        record.SchemaVersion != 1 || record.DefinitionVersion != 1 || record.FeatureId != FeatureId || record.ResourceId != ResourceId
            ? ErrorCode.UntrustedDefinition : null;

    private static void Validate(OperationRecord record)
    {
        if (DefinitionFault(record) is { } fault) throw new InvalidDataException(fault);
    }

    // Recovery reads the actual resource; it never replays journal targets and never guesses a terminal outcome.
    private static OperationRecord Reconcile(OperationRecord record, ResourceState actual)
    {
        if (record.SourceRevision is long source)
        {
            if (actual.Revision == source && actual.Value == record.Before)
                return record with { State = OperationState.Failed, After = actual.Value, Code = "operation.reconciled_unchanged" };
            if (actual.Revision == source + 1 && actual.Value == record.Desired)
                return record with { State = OperationState.Applied, After = actual.Value, AppliedRevision = actual.Revision, Code = "operation.reconciled_applied" };
            return record with { State = OperationState.Partial, After = actual.Value, Code = ErrorCode.ReconciledPartial };
        }
        // Migrated v1 row without revisions: value-only evidence.
        return actual.Value == record.Desired
            ? record with { State = OperationState.Applied, After = actual.Value, AppliedRevision = actual.Revision, Code = "operation.reconciled_applied" }
            : actual.Value == record.Before
                ? record with { State = OperationState.Failed, After = actual.Value, Code = "operation.reconciled_unchanged" }
                : record with { State = OperationState.Partial, After = actual.Value, Code = ErrorCode.ReconciledPartial };
    }

    private static OperationRecord ReconcileCompensation(OperationRecord record, ResourceState actual)
    {
        var restored = record with { CompensationPrepared = false, After = actual.Value };
        var untouched = record.AppliedRevision is long applied ? actual.Value == record.After && actual.Revision == applied : actual.Value == record.After;
        var reverted = record.AppliedRevision is long a ? actual.Value == record.Before && actual.Revision == a + 1 : actual.Value == record.Before;
        return untouched ? restored with { Code = "operation.rollback_abandoned" }
            : reverted ? restored with { State = OperationState.Reverted, Code = "operation.reverted" }
            : restored with { State = OperationState.Partial, Code = ErrorCode.RollbackUncertain };
    }
}
