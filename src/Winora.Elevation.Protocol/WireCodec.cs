// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>Attestation record content: what the worker proves about itself to the client.</summary>
public sealed record AttestationRecord(
    ElevationFlavor Flavor, byte ProtoMin, byte ProtoMax, Bytes16 Rendezvous, uint WorkerProcessId, ulong WorkerStartFileTime,
    uint SessionId, Bytes32 ServerKeyHash, Bytes32 DefinitionSetHash)
{
    public ServerIdentity ServerIdentity => new(WorkerProcessId, WorkerStartFileTime, SessionId);
}

/// <summary>Early-failure record content (A.8.2): a closed reason and the first 8 bytes of the rendezvous.</summary>
public sealed record EarlyFailureRecord(EarlyFailureReason Reason, ulong RendezvousPrefix)
{
    public static EarlyFailureRecord For(EarlyFailureReason reason, Bytes16 rendezvous)
    {
        Span<byte> raw = stackalloc byte[16];
        rendezvous.CopyTo(raw);
        return new(reason, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw));
    }
    public bool Matches(Bytes16 rendezvous) => For(Reason, rendezvous).RendezvousPrefix == RendezvousPrefix;
}

/// <summary>
/// Byte-exact encoders and strict decoders for every message and record of Appendix A. Encoders take Domain values
/// that are valid by construction. Decoders reject trailing bytes, overlong lengths, out-of-range enums, bad charsets and
/// non-canonical forms, return a <see cref="DecodeResult{T}"/> and never throw for hostile input; each accepted input re-encodes to
/// exactly the same bytes (no malleability).
/// </summary>
public static class WireCodec
{
    private static readonly byte[] AttestationMagic = "WEATT1"u8.ToArray();
    private static readonly byte[] EarlyFailureMagic = "WEFAIL"u8.ToArray();

    // ------------------------------------------------------------------------------------------------------ encoders

    public static byte[] Encode(ClientHello m) => new WireWriter().U8(m.ProtoMin).U8(m.ProtoMax).U8((byte)m.Flavor).B16(m.Rendezvous).B32(m.ClientNonce)
        .Point(m.ClientPublicKey).B32(m.DefinitionSetHash).B32(m.BatchDigest).U8(m.Operations).ToArray();
    public static byte[] Encode(ServerHello m) => new WireWriter().U8(m.ProtoMin).U8(m.ProtoMax).U8((byte)m.Flavor).B16(m.Rendezvous).B32(m.ServerNonce)
        .Point(m.ServerPublicKey).B32(m.DefinitionSetHash).U8(m.Chosen).ToArray();
    public static byte[] Encode(Finished m) => new WireWriter().B32(m.Transcript).ToArray();
    public static byte[] Encode(BatchBegin m)
    {
        var w = new WireWriter().U8(m.Operations);
        foreach (var item in m.Items) w.Item(item);
        return w.ToArray();
    }
    public static byte[] Encode(BatchAccepted m) => new WireWriter().U8(m.Operations).Blob(m.WorkerTokenUserSid.AsSpan()).ToArray();
    public static byte[] Encode(Prepare m) => new WireWriter().OperationId(m.OperationId).ToArray();
    public static byte[] Encode(Prepared m) => new WireWriter().OperationId(m.OperationId).U8((byte)m.Status).Result(m.Result).Bytes(m.Observed.AsSpan())
        .U64(m.KeyRevision).U8(m.WillChange ? (byte)1 : (byte)0).B32(m.PreparedDigest).ToArray();
    public static byte[] Encode(Commit m) => new WireWriter().OperationId(m.OperationId).B32(m.PreparedDigest).ToArray();
    public static byte[] Encode(Outcome m) => new WireWriter().OperationId(m.OperationId).U8((byte)m.Status).Result(m.Result).Bytes(m.After.AsSpan()).U64(m.KeyRevision).ToArray();
    public static byte[] Encode(Abort m) => new WireWriter().Result(m.Result).ToArray();
    public static byte[] Encode(Bye _) => [];
    public static byte[] EncodeItem(BatchItem item) => new WireWriter().Item(item).ToArray();
    public static byte[] EncodeValue(MachineValue value) => new WireWriter().Value(value).ToArray();
    public static byte[] EncodeResult(ElevationResult result) => new WireWriter().Result(result).ToArray();
    public static byte[] Encode(ClientIdentity id) => new WireWriter().U32(id.ProcessId).U64(id.StartFileTime).Blob(id.UserSid.AsSpan()).Blob(id.LogonSid.AsSpan()).U32(id.SessionId).ToArray();
    public static byte[] Encode(ServerIdentity id) => new WireWriter().U32(id.ProcessId).U64(id.StartFileTime).U32(id.SessionId).ToArray();
    public static byte[] Encode(AttestationRecord r) => new WireWriter().Bytes(AttestationMagic).U8((byte)r.Flavor).U8(r.ProtoMin).U8(r.ProtoMax).U8(0)
        .B16(r.Rendezvous).U32(r.WorkerProcessId).U64(r.WorkerStartFileTime).U32(r.SessionId).B32(r.ServerKeyHash).B32(r.DefinitionSetHash).ToArray();
    public static byte[] Encode(EarlyFailureRecord r) => new WireWriter().Bytes(EarlyFailureMagic).U8(1).U8((byte)r.Reason).U64(r.RendezvousPrefix).ToArray();

    /// <summary>Encodes any message DTO by type; null for an unknown object.</summary>
    public static byte[]? EncodeMessage(object message) => message switch
    {
        ClientHello m => Encode(m), ServerHello m => Encode(m), Finished m => Encode(m), BatchBegin m => Encode(m), BatchAccepted m => Encode(m),
        Prepare m => Encode(m), Prepared m => Encode(m), Commit m => Encode(m), Outcome m => Encode(m), Abort m => Encode(m), Bye m => Encode(m),
        _ => null,
    };

    public static MessageType? TypeOf(object message) => message switch
    {
        ClientHello => MessageType.ClientHello, ServerHello => MessageType.ServerHello, Finished => MessageType.Finished, BatchBegin => MessageType.BatchBegin,
        BatchAccepted => MessageType.BatchAccepted, Prepare => MessageType.Prepare, Prepared => MessageType.Prepared, Commit => MessageType.Commit,
        Outcome => MessageType.Outcome, Abort => MessageType.Abort, Bye => MessageType.Bye, _ => null,
    };

    // ------------------------------------------------------------------------------------------------------ decoders

    public static DecodeResult<object> DecodeMessage(MessageType type, ReadOnlySpan<byte> payload) => type switch
    {
        MessageType.ClientHello => Box(DecodeClientHello(payload)),
        MessageType.ServerHello => Box(DecodeServerHello(payload)),
        MessageType.Finished => Box(DecodeFinished(payload)),
        MessageType.BatchBegin => Box(DecodeBatchBegin(payload)),
        MessageType.BatchAccepted => Box(DecodeBatchAccepted(payload)),
        MessageType.Prepare => Box(DecodePrepare(payload)),
        MessageType.Prepared => Box(DecodePrepared(payload)),
        MessageType.Commit => Box(DecodeCommit(payload)),
        MessageType.Outcome => Box(DecodeOutcome(payload)),
        MessageType.Abort => Box(DecodeAbort(payload)),
        MessageType.Bye => Box(DecodeBye(payload)),
        _ => DecodeResult<object>.Framing(),
    };

    private static DecodeResult<object> Box<T>(DecodeResult<T> r) where T : class => r.IsSuccess ? DecodeResult<object>.Ok(r.Value!) : DecodeResult<object>.Fail(r.Failure!);

    public static DecodeResult<ClientHello> DecodeClientHello(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        if (payload.Length != ElevationLimits.ClientHelloPayloadBytes
            || !r.U8(out var pmin) || !r.U8(out var pmax) || !r.U8(out var flavor) || !r.B16(out var rend) || !r.B32(out var nc) || !r.Point(out var q)
            || !r.B32(out var hd) || !r.B32(out var bd) || !r.U8(out var ops) || !r.AtEnd
            || pmin < ElevationLimits.ProtocolVersion || pmax < pmin || !ElevationFlavors.IsDefined(flavor) || ops is < 1 or > ElevationLimits.MaxBatchOperations)
            return DecodeResult<ClientHello>.Framing();
        return DecodeResult<ClientHello>.Ok(new ClientHello(pmin, pmax, (ElevationFlavor)flavor, rend, nc, q, hd, bd, ops));
    }

    public static DecodeResult<ServerHello> DecodeServerHello(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        if (payload.Length != ElevationLimits.ServerHelloPayloadBytes
            || !r.U8(out var pmin) || !r.U8(out var pmax) || !r.U8(out var flavor) || !r.B16(out var rend) || !r.B32(out var ns) || !r.Point(out var q)
            || !r.B32(out var hd) || !r.U8(out var chosen) || !r.AtEnd
            || pmin < ElevationLimits.ProtocolVersion || pmax < pmin || !ElevationFlavors.IsDefined(flavor) || chosen < pmin || chosen > pmax)
            return DecodeResult<ServerHello>.Framing();
        return DecodeResult<ServerHello>.Ok(new ServerHello(pmin, pmax, (ElevationFlavor)flavor, rend, ns, q, hd, chosen));
    }

    public static DecodeResult<Finished> DecodeFinished(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        return payload.Length == ElevationLimits.FinishedPayloadBytes && r.B32(out var t) && r.AtEnd ? DecodeResult<Finished>.Ok(new Finished(t)) : DecodeResult<Finished>.Framing();
    }

    public static DecodeResult<BatchBegin> DecodeBatchBegin(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        if (!r.U8(out var ops) || ops is < 1 or > ElevationLimits.MaxBatchOperations) return DecodeResult<BatchBegin>.Framing();
        var items = new List<BatchItem>(ops);
        var ids = new HashSet<Guid>();
        for (var i = 0; i < ops; i++)
        {
            if (!r.Item(out var item) || !ids.Add(item.OperationId)) return DecodeResult<BatchBegin>.Framing();
            items.Add(item);
        }
        return r.AtEnd ? DecodeResult<BatchBegin>.Ok(new BatchBegin(items)) : DecodeResult<BatchBegin>.Framing();
    }

    public static DecodeResult<BatchAccepted> DecodeBatchAccepted(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        if (!r.U8(out var ops) || ops is < 1 or > ElevationLimits.MaxBatchOperations || !r.Sid(out var sid) || !r.AtEnd) return DecodeResult<BatchAccepted>.Framing();
        return DecodeResult<BatchAccepted>.Ok(new BatchAccepted(ops, sid));
    }

    public static DecodeResult<Prepare> DecodePrepare(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        return payload.Length == ElevationLimits.PreparePayloadBytes && r.OperationId(out var id) && r.AtEnd ? DecodeResult<Prepare>.Ok(new Prepare(id)) : DecodeResult<Prepare>.Framing();
    }

    public static DecodeResult<Prepared> DecodePrepared(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        if (!r.OperationId(out var id) || !r.U8(out var status) || status is not (0 or 2 or 3 or 4 or 5) || !r.Result(out var result) || !r.Snapshot(out var snap)
            || !r.U64(out var rev) || !r.U8(out var will) || will > 1 || (will == 1 && status != 0) || !r.B32(out var digest) || !r.AtEnd)
            return DecodeResult<Prepared>.Framing();
        return DecodeResult<Prepared>.Ok(new Prepared(id, (ElevationStatus)status, result, snap, rev, will == 1, digest));
    }

    public static DecodeResult<Commit> DecodeCommit(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        return payload.Length == ElevationLimits.CommitPayloadBytes && r.OperationId(out var id) && r.B32(out var d) && r.AtEnd ? DecodeResult<Commit>.Ok(new Commit(id, d)) : DecodeResult<Commit>.Framing();
    }

    public static DecodeResult<Outcome> DecodeOutcome(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        if (!r.OperationId(out var id) || !r.U8(out var status) || status is not (1 or 2 or 3 or 4 or 6) || !r.Result(out var result) || !r.Snapshot(out var snap) || !r.U64(out var rev) || !r.AtEnd)
            return DecodeResult<Outcome>.Framing();
        return DecodeResult<Outcome>.Ok(new Outcome(id, (ElevationStatus)status, result, snap, rev));
    }

    public static DecodeResult<Abort> DecodeAbort(ReadOnlySpan<byte> payload)
    {
        var r = new WireReader(payload);
        return r.Result(out var result) && r.AtEnd ? DecodeResult<Abort>.Ok(new Abort(result)) : DecodeResult<Abort>.Framing();
    }

    private static readonly Bye ByeValue = new();
    public static DecodeResult<Bye> DecodeBye(ReadOnlySpan<byte> payload) => payload.IsEmpty ? DecodeResult<Bye>.Ok(ByeValue) : DecodeResult<Bye>.Framing();

    public static DecodeResult<BatchItem> DecodeItem(ReadOnlySpan<byte> bytes)
    {
        var r = new WireReader(bytes);
        return r.Item(out var item) && r.AtEnd ? DecodeResult<BatchItem>.Ok(item) : DecodeResult<BatchItem>.Framing();
    }

    public static DecodeResult<MachineValue> DecodeValue(ReadOnlySpan<byte> bytes)
    {
        var r = new WireReader(bytes);
        return r.Value(out var v) && r.AtEnd ? DecodeResult<MachineValue>.Ok(v) : DecodeResult<MachineValue>.Framing();
    }

    public static DecodeResult<MachineSnapshot> DecodeSnapshot(ReadOnlySpan<byte> bytes)
    {
        var r = new WireReader(bytes);
        return r.Snapshot(out var s) && r.AtEnd ? DecodeResult<MachineSnapshot>.Ok(s) : DecodeResult<MachineSnapshot>.Framing();
    }

    public static DecodeResult<ElevationResult> DecodeResultBlock(ReadOnlySpan<byte> bytes)
    {
        var r = new WireReader(bytes);
        return r.Result(out var v) && r.AtEnd ? DecodeResult<ElevationResult>.Ok(v) : DecodeResult<ElevationResult>.Framing();
    }

    public static DecodeResult<AttestationRecord> DecodeAttestation(ReadOnlySpan<byte> bytes)
    {
        var r = new WireReader(bytes);
        if (bytes.Length != ElevationLimits.AttestationRecordBytes || !r.Take(6, out var magic) || !magic.SequenceEqual(AttestationMagic)
            || !r.U8(out var flavor) || !r.U8(out var pmin) || !r.U8(out var pmax) || !r.U8(out var reserved) || reserved != 0
            || !r.B16(out var rend) || !r.U32(out var pid) || !r.U64(out var start) || !r.U32(out var session) || !r.B32(out var qsHash) || !r.B32(out var hd) || !r.AtEnd
            || !ElevationFlavors.IsDefined(flavor) || pmin < ElevationLimits.ProtocolVersion || pmax < pmin)
            return DecodeResult<AttestationRecord>.Framing();
        return DecodeResult<AttestationRecord>.Ok(new AttestationRecord((ElevationFlavor)flavor, pmin, pmax, rend, pid, start, session, qsHash, hd));
    }

    public static DecodeResult<EarlyFailureRecord> DecodeEarlyFailure(ReadOnlySpan<byte> bytes)
    {
        var r = new WireReader(bytes);
        if (bytes.Length != ElevationLimits.EarlyFailureRecordBytes || !r.Take(6, out var magic) || !magic.SequenceEqual(EarlyFailureMagic)
            || !r.U8(out var version) || version != 1 || !r.U8(out var reason) || reason is < 1 or > 12 || !r.U64(out var prefix) || !r.AtEnd)
            return DecodeResult<EarlyFailureRecord>.Framing();
        return DecodeResult<EarlyFailureRecord>.Ok(new EarlyFailureRecord((EarlyFailureReason)reason, prefix));
    }

    // ------------------------------------------------------------------------------------------------------ canonical form

    /// <summary>
    /// Canonical-form rules the grammar of A.3.1 leaves implicit: in a registry snapshot
    /// keyExists = 0 forces valueExists = 0, and valueExists = 0 forces dataKind = 0 with empty data. Other kinds are canonical by layout.
    /// </summary>
    public static bool IsCanonical(MachineSnapshot snapshot)
    {
        if (snapshot.Kind != MachineTargetKind.RegistryValue) return true;
        var b = snapshot.AsSpan();
        var keyExists = b[1]; var valueExists = b[2]; var dataKind = b[4];
        if (keyExists == 0 && valueExists != 0) return false;
        if (valueExists == 0 && (dataKind != 0 || b.Length != 7)) return false;
        return true;
    }
}
