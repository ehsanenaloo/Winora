// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

/// <summary>
/// Durable journal for machine-scope operations (schema v4, technical-docs/trust.md 11). Same rules as <see cref="ISettingOperationStore"/>: every write is one
/// atomic BEGIN IMMEDIATE transaction with an insert-only ledger event, saves are revision-guarded, and at most ONE row of any kind (user or machine) is active.
/// Persistence faults surface as <see cref="StoreException"/>; refusals as <see cref="OperationException"/>.
/// </summary>
public interface IMachineOperationStore
{
    /// <summary>Rejects when ANY setting or machine row is active (operation.recovery_required), validates the initial state, inserts at revision 0 and writes the first event.</summary>
    MachineOperationRecord PrepareMachine(MachineOperationRecord record);
    /// <summary>Revision-guarded transition; identity, intent and the before snapshot are immutable once journaled.</summary>
    MachineOperationRecord SaveMachine(MachineOperationRecord record);
    MachineOperationRecord GetMachine(Guid id);
    IReadOnlyList<MachineOperationRecord> ListMachineOperations();
    /// <summary>Active rows plus any non-quarantined row whose payload cannot be trusted.</summary>
    IReadOnlyList<MachineJournalEntry> ListRecoverableMachineEntries();
    /// <summary>Moves an untrusted or unknown-definition row out of the active set without any machine action, recording a ledger event.</summary>
    void QuarantineMachine(Guid id, long expectedRevision, string code);
    IReadOnlyList<QuarantinedEntry> ListQuarantinedMachine();
    IReadOnlyList<MachineOperationEvent> ReadMachineLedger(Guid? operationId = null);
}
