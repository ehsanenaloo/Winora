// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
namespace Winora.Domain.Elevation;

/// <summary>
/// Constants of the elevation wire protocol (technical-docs/protocol.md). Every number cites the section that defines it; a test
/// keeps the literals equal to the document. Platform neutral: no Windows, crypto or serializer type is referenced here.
/// </summary>
public static class ElevationLimits
{
    // Section 9, A.7: framing
    public const byte ProtocolVersion = 1;
    public const byte MagicByte0 = 0x57;
    public const byte MagicByte1 = 0x45;
    public const int FrameHeaderBytes = 16;
    public const int TagBytes = 16;
    /// <summary>Largest frame including header and tag. Replaces the maximum message size of the retired foundation validator stub.</summary>
    public const int MaxFrameBytes = 65536;
    public const int MaxCiphertextBytes = MaxFrameBytes - FrameHeaderBytes - TagBytes;
    public const int MaxHelloPayloadBytes = 512;
    public const int MaxPlainAbortPayloadBytes = 96;
    public const int MaxFramesPerDirection = 64;
    public const int NonceBytes = 12;

    // Section 10.1, 13.1: batch
    public const int MaxBatchOperations = 8;
    /// <summary>Section 19.1: exactly one operation per batch in milestone M4a.</summary>
    public const int M4aMaxBatchOperations = 1;

    // A.1, A.3: field bounds
    public const int MaxIdentifierBytes = 64;
    public const int MaxSidBytes = 68;
    public const int MaxSubAuthorities = 15;
    public const int MaxRestoreStringBytes = 1024;
    public const int MaxSelftestPayloadBytes = 37;
    public const int MaxDetailLength = 24;

    // A.1, A.4, A.8: fixed sizes
    public const int RendezvousBytes = 16;
    public const int DigestBytes = 32;
    public const int NonceFieldBytes = 32;
    public const int PublicKeyBytes = 65;
    public const int ClientHelloPayloadBytes = 181;
    public const int ServerHelloPayloadBytes = 149;
    public const int FinishedPayloadBytes = 32;
    public const int PreparePayloadBytes = 16;
    public const int CommitPayloadBytes = 48;
    public const int AttestationRecordBytes = 106;
    public const int EarlyFailureRecordBytes = 16;

    // Section 10.2 and 13.1: timers in milliseconds (monotonic clock only)
    public const int ConnectWaitMs = 30_000;
    public const int AwaitHelloMs = 10_000;
    public const int AwaitFinishedMs = 10_000;
    public const int AwaitBatchBeginMs = 30_000;
    public const int AwaitPrepareMs = 60_000;
    public const int AwaitCommitMs = 30_000;
    public const int AwaitByeMs = 10_000;
    public const int AttestationWaitMs = 30_000;
    public const int ServerHelloWaitMs = 30_000;
    public const int FrameReadMs = 10_000;
    public const int IdleMs = 60_000;
    public const int WorkerLifetimeMs = 300_000;
    public const int EarlyFailureGraceMs = 5_000;
    public const int RecordPollMs = 250;
    /// <summary>Identity-failed connects a worker tolerates (section 5); the 4th ends the session.</summary>
    public const int RejectedConnectBudget = 3;

    /// <summary>Deadline of a worker state (section 10.2); null for no state timer.</summary>
    public static int? DeadlineMs(SessionState state) => state switch
    {
        SessionState.AwaitConnect => ConnectWaitMs,
        SessionState.AwaitHello => AwaitHelloMs,
        SessionState.AwaitFinished => AwaitFinishedMs,
        SessionState.AwaitBatchBegin => AwaitBatchBeginMs,
        SessionState.AwaitPrepare => AwaitPrepareMs,
        SessionState.AwaitCommit => AwaitCommitMs,
        SessionState.AwaitBye => AwaitByeMs,
        _ => null,
    };
}

/// <summary>Build-identity byte (protocol.md 2, A.3.4). Release builds carry 0, VM-only builds carry 1 (D-09).</summary>
public enum ElevationFlavor : byte { Release = 0, VmTrust = 1 }

public static class ElevationFlavors
{
    public const byte ReleaseByte = 0;
    /// <summary>The VmTrust flavor byte. A release client or worker never accepts it (protocol.md 8.2).</summary>
    public const byte VmTrustByte = 1;
    public static bool IsDefined(byte value) => value is ReleaseByte or VmTrustByte;
}

/// <summary>Message types (protocol.md 10.1). The numeric values are the wire type byte.</summary>
public enum MessageType : byte
{
    ClientHello = 0x01, ServerHello = 0x02, Finished = 0x10, BatchBegin = 0x20, BatchAccepted = 0x21,
    Prepare = 0x30, Prepared = 0x31, Commit = 0x32, Outcome = 0x33, Abort = 0x40, Bye = 0x41,
}

/// <summary>Status byte (A.3.4). Ready appears only in Prepared; Applied and Partial only in Outcome.</summary>
public enum ElevationStatus : byte { Ready = 0, Applied = 1, AlreadyConfigured = 2, Failed = 3, Conflict = 4, Refused = 5, Partial = 6 }

/// <summary>Mode byte (A.3.4).</summary>
public enum PrepareMode : byte { ApplyChoice = 0, Restore = 1 }

/// <summary>Worker session states (protocol.md 10.2).</summary>
public enum SessionState { AwaitConnect, AwaitHello, AwaitFinished, AwaitBatchBegin, AwaitPrepare, AwaitCommit, AwaitBye }

/// <summary>Worker exit codes (protocol.md 13.2). For humans and logs only; the client cannot rely on reading them.</summary>
public enum WorkerExitCode { Finished = 0, NotElevated = 3, BadArguments = 4, TrustFailure = 5, Timeout = 6, ChannelFailure = 7, Busy = 8, DefinitionRefusal = 9 }

/// <summary>Early-failure record reason byte (protocol.md 6.4, A.8.2). Mapping to catalog codes belongs to the client adapter.</summary>
public enum EarlyFailureReason : byte
{
    Busy = 1, Hardening = 2, Environment = 3, Image = 4, RunDir = 5, Definition = 6, Rollback = 7, FloorStore = 8,
    SetMismatch = 9, Squatted = 10, Arguments = 11, Internal = 12,
}

/// <summary>
/// Persisted informational outcome codes that are not errors (errors.md): used in Result blocks next to the error catalog.
/// </summary>
public static class OutcomeCodes
{
    public const string Prepared = "operation.prepared";
    public const string Applied = "operation.applied";
    public const string AlreadyConfigured = "operation.already_configured";
    public static bool IsInformational(string code) => code is Prepared or Applied or AlreadyConfigured;
}

/// <summary>Identifier grammar of A.1: <c>[a-z0-9._]</c>, bounded, ASCII only.</summary>
public static class ElevationIdentifier
{
    public static bool IsValid(string? value, int minLength = 1, int maxLength = ElevationLimits.MaxIdentifierBytes)
    {
        if (value is null || value.Length < minLength || value.Length > maxLength) return false;
        foreach (var c in value) if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_')) return false;
        return true;
    }

    /// <summary>Detail token grammar of protocol.md 17.2: <c>[a-z0-9_]{1,24}</c>.</summary>
    public static bool IsValidToken(string? value)
    {
        if (value is null || value.Length is < 1 or > ElevationLimits.MaxDetailLength) return false;
        foreach (var c in value) if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return false;
        return true;
    }
}

/// <summary>
/// Operation ids are Guids in Domain and 16 raw bytes on the wire (A.3.5). The mapping is fixed here so both peers agree:
/// the bytes are the Guid in big-endian (text) order, so wire bytes a0a1..af are the Guid a0a1a2a3-a4a5-a6a7-a8a9-aaabacadaeaf.
/// </summary>
public static class ElevationIds
{
    public static void WriteOperationId(Guid id, Span<byte> destination)
    {
        if (destination.Length < 16 || !id.TryWriteBytes(destination, bigEndian: true, out _)) throw new ArgumentException("operation id destination too small", nameof(destination));
    }
    public static Guid ReadOperationId(ReadOnlySpan<byte> source)
    {
        if (source.Length != 16) throw new ArgumentException("operation id must be 16 bytes", nameof(source));
        return new Guid(source, bigEndian: true);
    }
}

/// <summary>Immutable 16-byte value (rendezvous R, A.1). The default value is all zero.</summary>
public readonly struct Bytes16 : IEquatable<Bytes16>
{
    private readonly ulong a, b;
    public Bytes16(ReadOnlySpan<byte> value)
    {
        if (value.Length != 16) throw new ArgumentException("expected 16 bytes", nameof(value));
        a = BinaryPrimitives.ReadUInt64LittleEndian(value); b = BinaryPrimitives.ReadUInt64LittleEndian(value[8..]);
    }
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 16) throw new ArgumentException("destination too small", nameof(destination));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, a); BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], b);
    }
    public byte[] ToArray() { var r = new byte[16]; CopyTo(r); return r; }
    public string ToHex() => Convert.ToHexStringLower(ToArray());
    public bool Equals(Bytes16 other) => a == other.a && b == other.b;
    public override bool Equals(object? obj) => obj is Bytes16 o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(a, b);
    public static bool operator ==(Bytes16 x, Bytes16 y) => x.Equals(y);
    public static bool operator !=(Bytes16 x, Bytes16 y) => !x.Equals(y);
}

/// <summary>Immutable 32-byte value: SHA-256 digests (H_d, batch, plan token, prepared), transcript T and nonces.</summary>
public readonly struct Bytes32 : IEquatable<Bytes32>
{
    private readonly ulong a, b, c, d;
    public Bytes32(ReadOnlySpan<byte> value)
    {
        if (value.Length != 32) throw new ArgumentException("expected 32 bytes", nameof(value));
        a = BinaryPrimitives.ReadUInt64LittleEndian(value); b = BinaryPrimitives.ReadUInt64LittleEndian(value[8..]);
        c = BinaryPrimitives.ReadUInt64LittleEndian(value[16..]); d = BinaryPrimitives.ReadUInt64LittleEndian(value[24..]);
    }
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 32) throw new ArgumentException("destination too small", nameof(destination));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, a); BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], b);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], c); BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], d);
    }
    public byte[] ToArray() { var r = new byte[32]; CopyTo(r); return r; }
    public string ToHex() => Convert.ToHexStringLower(ToArray());
    /// <summary>The "short form" shown in consent (A.5.2): the first 4 bytes as 8 lowercase hex characters.</summary>
    public string ShortHex() => ToHex()[..8];
    public bool Equals(Bytes32 other) => a == other.a && b == other.b && c == other.c && d == other.d;
    public override bool Equals(object? obj) => obj is Bytes32 o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(a, b, c, d);
    public static bool operator ==(Bytes32 x, Bytes32 y) => x.Equals(y);
    public static bool operator !=(Bytes32 x, Bytes32 y) => !x.Equals(y);
}

/// <summary>SEC1 uncompressed P-256 public key: 65 bytes starting with 0x04 (section 8.1). The curve membership check is the handshake engine's job.</summary>
public readonly struct PublicPoint65 : IEquatable<PublicPoint65>
{
    private readonly byte[]? bytes;
    public PublicPoint65(ReadOnlySpan<byte> value)
    {
        if (value.Length != ElevationLimits.PublicKeyBytes || value[0] != 0x04) throw new ArgumentException("expected a 65-byte uncompressed point", nameof(value));
        bytes = value.ToArray();
    }
    public ReadOnlySpan<byte> AsSpan() => bytes ?? throw new InvalidOperationException("uninitialized public point");
    public bool Equals(PublicPoint65 other) => bytes is not null && other.bytes is not null && bytes.AsSpan().SequenceEqual(other.bytes);
    public override bool Equals(object? obj) => obj is PublicPoint65 o && Equals(o);
    public override int GetHashCode() { var h = new HashCode(); if (bytes is not null) h.AddBytes(bytes); return h.ToHashCode(); }
    public static bool operator ==(PublicPoint65 x, PublicPoint65 y) => x.Equals(y);
    public static bool operator !=(PublicPoint65 x, PublicPoint65 y) => !x.Equals(y);
}

/// <summary>Binary SID (A.1): revision 1, at most 15 sub-authorities, so at most 68 bytes. Structure only; no Windows API.</summary>
public readonly struct BinarySid : IEquatable<BinarySid>
{
    private readonly byte[]? bytes;
    public BinarySid(ReadOnlySpan<byte> value)
    {
        if (!IsValid(value)) throw new ArgumentException("not a well-formed binary SID", nameof(value));
        bytes = value.ToArray();
    }
    public static bool IsValid(ReadOnlySpan<byte> value) =>
        value.Length >= 8 && value.Length <= ElevationLimits.MaxSidBytes && value[0] == 1 && value[1] <= ElevationLimits.MaxSubAuthorities && value.Length == 8 + 4 * value[1];
    public ReadOnlySpan<byte> AsSpan() => bytes ?? throw new InvalidOperationException("uninitialized SID");
    public bool Equals(BinarySid other) => bytes is not null && other.bytes is not null && bytes.AsSpan().SequenceEqual(other.bytes);
    public override bool Equals(object? obj) => obj is BinarySid o && Equals(o);
    public override int GetHashCode() { var h = new HashCode(); if (bytes is not null) h.AddBytes(bytes); return h.ToHashCode(); }
    public static bool operator ==(BinarySid x, BinarySid y) => x.Equals(y);
    public static bool operator !=(BinarySid x, BinarySid y) => !x.Equals(y);
}
