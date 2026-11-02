// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

/// <summary>The Windows build the app is running on. Defined here; the real implementation lives with the Windows adapters.</summary>
public interface IOsBuildProvider
{
    /// <summary>Current OS build number, or null when it cannot be determined (build-bounded settings then fail closed).</summary>
    int? CurrentBuild { get; }

    /// <summary>The <c>EditionID</c> of this Windows (for example <c>Professional</c>), or null when unknown. A definition limited to some editions is then unsupported.</summary>
    string? EditionId => null;

    /// <summary>The feature release name of this Windows (for example <c>24H2</c>, the <c>DisplayVersion</c> value), or null when unknown.</summary>
    string? DisplayVersion => null;

    /// <summary>The update revision number (the <c>UBR</c> value, the part after the build number), or null when unknown.</summary>
    int? UpdateRevision => null;
}

/// <summary>
/// Durable journal for registry-setting operations (schema v3). Every write is one atomic BEGIN IMMEDIATE transaction with an
/// insert-only ledger event. Rows are identity, intent and typed snapshots only; the store knows nothing about registry paths.
/// Persistence faults surface as <see cref="StoreException"/>; refusals as <see cref="OperationException"/>.
/// </summary>
public interface ISettingOperationStore
{
    /// <summary>
    /// One transaction: reject if ANY setting operation is active (operation.recovery_required; the database also enforces one active
    /// row per setting), validate the initial state, insert the row at revision 0 and write its first ledger event.
    /// </summary>
    SettingOperationRecord PrepareSetting(SettingOperationRecord record);
    /// <summary>Revision-guarded transition: expects record.Revision to equal the stored revision, validates the transition and immutable identity/intent, writes the ledger event, returns the stored row.</summary>
    SettingOperationRecord SaveSetting(SettingOperationRecord record);
    /// <summary>Trusted row by id (quarantined or untrusted rows throw journal.operation_missing / the fault code).</summary>
    SettingOperationRecord GetSetting(Guid id);
    /// <summary>All trusted, non-quarantined rows in insertion order. Untrusted payloads are skipped (recovery quarantines them).</summary>
    IReadOnlyList<SettingOperationRecord> ListSettingOperations();
    /// <summary>Active rows plus any non-quarantined row whose payload cannot be trusted.</summary>
    IReadOnlyList<SettingJournalEntry> ListRecoverableSettingEntries();
    /// <summary>Moves an untrusted or mismatched row out of the active set without touching the registry, recording a ledger event.</summary>
    void QuarantineSetting(Guid id, long expectedRevision, string code);
    IReadOnlyList<QuarantinedEntry> ListQuarantinedSettings();
}
