// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;
namespace Winora.Application;

/// <summary>One entry of a verified set's <c>revoked</c> list (trust.md 6.6).</summary>
public sealed record RevokedEntry(string Id, int BelowVersion, bool AllowRestore);

/// <summary>A definition set that passed signature, schema, invariants and the floor check. The Application layer never sees unverified bytes.</summary>
public sealed record VerifiedDefinitionSet(long Serial, string HashHex, IReadOnlyList<MachineSettingDefinition> Definitions, IReadOnlyList<RevokedEntry> Revoked)
{
    public MachineSettingDefinition? Find(string id) => Definitions.FirstOrDefault(d => d.Id == id);
    public RevokedEntry? RevocationOf(string id, int version) => Revoked.FirstOrDefault(r => r.Id == id && version < r.BelowVersion);
}

/// <summary>Where the verified set comes from. <see cref="Code"/> is a catalog code when no usable set exists (signature, rollback, floor store).</summary>
public sealed record DefinitionSetResult(VerifiedDefinitionSet? Set, string? Code);

public interface IMachineDefinitionSource
{
    DefinitionSetResult Get();
}

/// <summary>The result of an UNELEVATED read of the machine state of one target (trust.md 7.1 item 5).</summary>
public sealed record MachineStateRead(MachineSnapshot? Snapshot, ulong KeyRevision, string? Code)
{
    public bool Ok => Snapshot is not null;
    public static MachineStateRead Of(MachineSnapshot snapshot, ulong keyRevision) => new(snapshot, keyRevision, null);
    public static MachineStateRead Failed(string code) => new(null, 0, code);
}

public interface IMachineStateReader
{
    /// <summary>Reads the target of <paramref name="definition"/> as a standard user: current snapshot and key revision.</summary>
    MachineStateRead Read(MachineSettingDefinition definition);
}

public interface IMachinePolicyReader
{
    /// <summary>True when any <c>managedBy</c> channel of the definition currently holds a managed value (trust.md 7.1 item 7).</summary>
    bool IsManagedByPolicy(MachineSettingDefinition definition);
}

/// <summary>Digests of protocol.md A.5, behind a port because the Application layer does not reference the protocol library.</summary>
public interface IMachineDigests
{
    Bytes32 PlanTokenApply(string definitionId, uint version, string choiceId, MachineSnapshot expected, ulong keyRevision);
    Bytes32 PlanTokenRestore(string definitionId, uint version, MachineSnapshot recordedAfter, ulong recordedRevision);
    Bytes32 BatchDigest(BatchBegin batch);
}

public enum WorkerPresence { Gone, Alive, Inconclusive }

/// <summary>Probe of <c>&lt;InstallRoot&gt;\run\worker.lock</c> (trust.md 11.4). Missing install or lock counts as gone.</summary>
public interface IWorkerPresence
{
    WorkerPresence Probe();
}

/// <summary>Identifier of the current boot; a row written before a reboot cannot have a live worker (trust.md 11.4).</summary>
public interface IBootSession
{
    string Id { get; }
}

/// <summary>What the host passes with an apply or revert: the digest the user saw and whether a person was asked (D-12).</summary>
/// <param name="SecondConfirmationGiven">True when Windows is set to elevate without a prompt and the user gave the extra explicit confirmation the host must ask for (D-12).</param>
public sealed record ElevationConfirmation(string BatchDigestShort, bool Interactive, bool SecondConfirmationGiven = false);

/// <summary>One operation of a finished or interrupted batch as the channel reports it.</summary>
public sealed record MachineBatchOperation(BatchItem Item, Prepared? Prepared, Outcome? Outcome, bool CommitSent);

/// <summary>
/// What a channel session produced. <see cref="FailureCode"/> is a catalog code when the session ended before a usable answer (launch declined, attestation, handshake,
/// worker abort). <see cref="CommitSent"/> is true once a Commit frame was written: from then on the worker may have written.
/// </summary>
public sealed record MachineBatchResult(bool Launched, string? FailureCode, bool DeclinedByCaller, bool CommitSent, IReadOnlyList<MachineBatchOperation> Operations);

/// <summary>Called after a Prepared that will change something, before Commit is sent; false aborts the session without a mutation.</summary>
public delegate ValueTask<bool> MachineBeforeCommit(BatchItem item, Prepared prepared);

/// <summary>
/// The single channel to the elevated helper (technical-docs/protocol.md): launch, attestation, handshake, one batch, Bye. Implemented in the Windows client adapter.
/// A session raises the administrator prompt at most once and never persists anything.
/// </summary>
public interface IMachineChannel
{
    Task<MachineBatchResult> RunBatchAsync(BatchBegin batch, MachineBeforeCommit beforeCommit, bool secondConfirmationGiven, CancellationToken cancellationToken);
}

/// <summary>
/// A reviewed plan for one machine change. Everything the consent dialog shows comes from here (protocol.md consent content). The operation id is fixed at plan time
/// because the batch digest the user sees commits to it.
/// </summary>
public sealed record MachinePlan(
    Guid OperationId, string DefinitionId, int DefinitionVersion, string ChoiceId, string TitleKey, MachineSnapshot Before, ulong BeforeRevision, MachineValue? DesiredValue,
    bool WillChange, Bytes32 PlanToken, long SetSerial, string SetHash, bool RequiresRestart, string? RefusalCode)
{
    public bool IsRefused => RefusalCode is not null;
}

/// <summary>What a batch, revert or recovery step ended with. <see cref="RecoveryRequired"/> is true when the row stays open for a later recovery.</summary>
public sealed record MachineOutcome(Guid OperationId, string DefinitionId, OperationState State, string Code, bool RecoveryRequired);

public interface IMachineSettingsExecutor
{
    Task<MachinePlan> PlanAsync(string definitionId, string choiceId, CancellationToken cancellationToken);
    /// <summary>The first 8 hex characters of the batch digest the consent dialog and the visible command line show.</summary>
    string BatchDigestShort(IReadOnlyList<MachinePlan> plans);
    Task<IReadOnlyList<MachineOutcome>> ApplyBatchAsync(IReadOnlyList<MachinePlan> reviewed, ElevationConfirmation confirmation, CancellationToken cancellationToken);
    /// <summary>The digest the consent for a revert must show; raises the same typed refusals as <see cref="RevertAsync"/> and changes nothing.</summary>
    string RevertDigestShort(Guid operationId);
    Task<MachineOutcome> RevertAsync(Guid operationId, ElevationConfirmation confirmation, CancellationToken cancellationToken);
    /// <summary>Read-only: never launches the worker, never signals a process, never replays a journaled target.</summary>
    Task<IReadOnlyList<MachineOutcome>> RecoverAsync(CancellationToken cancellationToken);
    /// <summary>Read-only startup check of applied machine rows against the live state (trust.md 11.6).</summary>
    IReadOnlyList<MachineOutcome> DriftCheck();
    /// <summary>The user-initiated "Mark as reviewed" for a row that recovery cannot resolve (trust.md 11.5). No machine action.</summary>
    MachineOutcome MarkReviewed(Guid operationId);
}
