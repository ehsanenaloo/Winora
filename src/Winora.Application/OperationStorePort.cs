// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

/// <summary>
/// Durable journal plus the simulated resource. Every write is one atomic store transaction.
/// Persistence faults surface as <see cref="StoreException"/> (IOException); refusals as <see cref="OperationException"/>.
/// </summary>
public interface IOperationStore
{
    bool ReadActual();
    ResourceState ReadResource();
    /// <summary>
    /// One transaction: reject if an operation is active (operation.recovery_required), read the actual resource,
    /// reject a stale <paramref name="expected"/> token (plan.stale), build the record from that state and insert it at revision 0.
    /// </summary>
    OperationRecord Prepare(PlanToken? expected, Func<ResourceState, OperationRecord> build);
    /// <summary>Revision-guarded transition: expects operation.Revision to equal the stored revision, validates the transition, writes the ledger event, returns the stored record.</summary>
    /// <param name="expectedResource">When given, the same transaction also verifies the resource still has this value and revision (operation.resource_changed otherwise).</param>
    OperationRecord Save(OperationRecord operation, ResourceState? expectedResource = null);
    /// <summary>
    /// Compare-and-swap on resource value and revision, additionally bound to the journal row revision so a concurrent
    /// recovery that already resolved the row prevents the mutation (journal.revision_conflict). Returns the new resource revision or null if the CAS was lost.
    /// </summary>
    long? TryCompareExchange(Guid operationId, long operationRevision, bool expectedValue, long expectedResourceRevision, bool newValue);
    OperationRecord Get(Guid id);
    IReadOnlyList<OperationRecord> ListOperations();
    /// <summary>Rows needing recovery attention: active rows (Prepared, Executing, compensation prepared) and any non-quarantined row whose payload is untrusted.</summary>
    IReadOnlyList<JournalEntry> ListRecoverableEntries();
    /// <summary>Moves an untrusted row out of the active set without touching the resource, recording a ledger event.</summary>
    void Quarantine(Guid id, long expectedRevision, string code);
    IReadOnlyList<QuarantinedEntry> ListQuarantined();
}
