// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Serialization;
using Winora.Domain.Elevation;

namespace Winora.Domain;

/// <summary>
/// One machine-scope operation as the journal stores it. Identity and intent are written once at <c>Prepared</c> and never change;
/// snapshots are canonical machine snapshots in lowercase hex so records compare by value. The row carries ids and snapshots only:
/// the write target is always re-resolved from the verified definition set by <see cref="DefinitionId"/> and <see cref="DefinitionVersion"/>.
/// </summary>
/// <param name="SetSerial">Serial of the definition set the row was planned under.</param>
/// <param name="SetHash">H_d of that set, 64 lowercase hex characters.</param>
/// <param name="BatchDigest">Batch digest the user consented to, 64 hex characters.</param>
/// <param name="PlanToken">Plan token digest of the operation, 64 hex characters.</param>
/// <param name="ChoiceId">Choice applied; empty for a row that only exists as a restore.</param>
/// <param name="CompensationCommitted">True once a revert Commit may have been sent; only meaningful with <see cref="CompensationPrepared"/>.</param>
/// <param name="Before">Snapshot read unelevated at plan time. Never rewritten.</param>
/// <param name="BeforeRevision">Key revision observed with <see cref="Before"/>.</param>
/// <param name="WorkerBefore">Snapshot the worker observed at Prepare; must equal <see cref="Before"/>. Null until Prepared was received.</param>
/// <param name="After">Client's own read-back after the write, or the live state recorded by a reconciliation.</param>
/// <param name="AfterRevision">Key revision observed with <see cref="After"/>.</param>
/// <param name="Restore">Snapshot a revert writes (from the definition's restore domain), recorded when the compensation is prepared.</param>
/// <param name="RestorePlanToken">Plan token digest of the restore.</param>
/// <param name="BootSession">Boot session identifier at the time of the last durable write that can precede a worker write.</param>
/// <param name="ReviewAcknowledged">True after the user chose "Mark as reviewed" for a row recovery could not resolve.</param>
public sealed record MachineOperationRecord(
    Guid Id, string DefinitionId, int DefinitionVersion, long SetSerial, string SetHash, string BatchDigest, string PlanToken, string ChoiceId,
    OperationState State, bool CompensationPrepared, bool CompensationCommitted, string Code,
    string Before, ulong BeforeRevision, string? WorkerBefore, string? After, ulong? AfterRevision, string? Restore, string? RestorePlanToken,
    bool RequiresRestart, int OwnerProcessId, long OwnerProcessStartTicks, string BootSession, bool ReviewAcknowledged, int SchemaVersion,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
    /// <summary>Journal row revision (column, not payload). Save expects the stored value and the store advances it.</summary>
    [JsonIgnore] public long Revision { get; init; }
}

/// <summary>A machine_operations row as stored; <see cref="Record"/> is null when the payload cannot be trusted.</summary>
public sealed record MachineJournalEntry(Guid Id, long Revision, MachineOperationRecord? Record, string? Fault);

/// <summary>One immutable ledger line of a machine row.</summary>
public sealed record MachineOperationEvent(
    long Sequence, Guid OperationId, long Revision, OperationState? FromState, bool? FromCompensation, bool? FromCommitted,
    OperationState ToState, bool ToCompensation, bool ToCommitted, string Code, DateTimeOffset AtUtc);

/// <summary>Hex helpers for canonical snapshots and digests stored in journal rows.</summary>
public static class MachineHex
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    public static string Encode(MachineSnapshot snapshot) => Convert.ToHexStringLower(snapshot.AsSpan());

    /// <summary>Strict decode of a stored snapshot: valid hex that is exactly one canonical snapshot.</summary>
    public static bool TryDecodeSnapshot(string? hex, out MachineSnapshot snapshot)
    {
        snapshot = MachineSnapshot.SelftestEmpty;
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0 || !IsHex(hex)) return false;
        var bytes = Convert.FromHexString(hex);
        if (!MachineSnapshot.TryParse(bytes, out var parsed, out var consumed) || consumed != bytes.Length) return false;
        snapshot = parsed;
        return true;
    }

    public static bool IsHexDigest(string? text) => text is { Length: 64 } && IsHex(text);

    private static bool IsHex(string text)
    {
        foreach (var c in text) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
