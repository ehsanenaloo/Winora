// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>SHA-256 digests of A.5 with the domain-separation labels of A.2. The labels carry one trailing NUL byte.</summary>
public static class Digests
{
    private static readonly byte[] BatchLabel = Encoding.ASCII.GetBytes("winora/batch/v1\0");
    private static readonly byte[] PlanLabel = Encoding.ASCII.GetBytes("winora/plan/v1\0");
    private static readonly byte[] PlanRestoreLabel = Encoding.ASCII.GetBytes("winora/plan/v1/restore\0");
    private static readonly byte[] PreparedLabel = Encoding.ASCII.GetBytes("winora/prepared/v1\0");

    public static Bytes32 Sha256(ReadOnlySpan<byte> data) => new(SHA256.HashData(data));

    /// <summary>A.5.1, apply: commits to definition, version, choice, the expected snapshot and the key revision.</summary>
    public static Bytes32 PlanTokenApply(string definitionId, uint version, string choiceId, MachineSnapshot expected, ulong keyRevision) =>
        Sha256(new WireWriter().Bytes(PlanLabel).S(definitionId).U32(version).S(choiceId).Bytes(expected.AsSpan()).U64(keyRevision).ToArray());

    /// <summary>A.5.1, restore: commits to the recorded after-snapshot and its key revision.</summary>
    public static Bytes32 PlanTokenRestore(string definitionId, uint version, MachineSnapshot recordedAfter, ulong recordedRevision) =>
        Sha256(new WireWriter().Bytes(PlanRestoreLabel).S(definitionId).U32(version).Bytes(recordedAfter.AsSpan()).U64(recordedRevision).ToArray());

    /// <summary>A.5.2: binds, per operation, everything the user sees before consent.</summary>
    public static Bytes32 BatchDigest(BatchBegin batch)
    {
        var w = new WireWriter().Bytes(BatchLabel).U8(batch.Operations);
        foreach (var item in batch.Items) w.Item(item);
        return Sha256(w.ToArray());
    }

    /// <summary>A.5.3: the worker's commitment to what it observed; Commit repeats it.</summary>
    public static Bytes32 PreparedDigest(Bytes32 transcript, Guid operationId, Bytes32 planTokenDigest, MachineSnapshot snapshot, ulong keyRevision, bool willChange) =>
        Sha256(new WireWriter().Bytes(PreparedLabel).B32(transcript).OperationId(operationId).B32(planTokenDigest).Bytes(snapshot.AsSpan()).U64(keyRevision)
            .U8(willChange ? (byte)1 : (byte)0).ToArray());
}
