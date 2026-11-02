// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol.Definitions;

/// <summary>
/// The anti-rollback floor: <c>u8 version(1) || u64 serial (little-endian) || hash[32]</c>, 41 bytes, written as ONE registry value so a torn pair cannot exist.
/// Serial 0 with an all-zero hash is the floor of a machine that never ran a set.
/// </summary>
public readonly record struct FloorRecord(ulong Serial, Bytes32 Hash)
{
    public const byte FormatVersion = 1;
    public const int EncodedLength = 41;
    public static FloorRecord None => new(0, new Bytes32(new byte[32]));

    public byte[] Encode()
    {
        var b = new byte[EncodedLength];
        b[0] = FormatVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(1), Serial);
        Hash.CopyTo(b.AsSpan(9));
        return b;
    }

    /// <summary>Strict decode: exact length, version 1. A malformed value is <c>trust.store_insecure</c> for the caller, never a silent floor of zero.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out FloorRecord record)
    {
        record = None;
        if (bytes.Length != EncodedLength || bytes[0] != FormatVersion) return false;
        record = new FloorRecord(BinaryPrimitives.ReadUInt64LittleEndian(bytes[1..]), new Bytes32(bytes.Slice(9, 32).ToArray()));
        return true;
    }
}

public enum FloorReadStatus { Absent, Present, Malformed, Insecure }

/// <summary>What a store read found. <c>Insecure</c> means the key's effective permissions or type failed the checks of.</summary>
public readonly record struct FloorRead(FloorReadStatus Status, FloorRecord Record)
{
    public static FloorRead Absent => new(FloorReadStatus.Absent, FloorRecord.None);
    public static FloorRead Of(FloorRecord record) => new(FloorReadStatus.Present, record);
    public static FloorRead Malformed => new(FloorReadStatus.Malformed, FloorRecord.None);
    public static FloorRead Insecure => new(FloorReadStatus.Insecure, FloorRecord.None);
}

/// <summary>Port of the floor store. The production store lives in the Windows project (HKLM Trust key); only the worker advances it.</summary>
public interface IAntiRollbackStore
{
    FloorRead Read();
    /// <summary>Compare-and-swap: writes <paramref name="next"/> only if the stored floor still equals <paramref name="expected"/> (absent equals <see cref="FloorRecord.None"/>).</summary>
    bool TryAdvance(FloorRecord expected, FloorRecord next);
}

/// <summary>Floor rules of.</summary>
public static class RollbackPolicy
{
    /// <summary>
    /// Null when the set may be used. A serial below the floor is <c>definition.rollback</c>; the same serial with another hash is <c>definition.set_mismatch</c>
    /// (equivocation); a greater serial is accepted.
    /// </summary>
    public static string? Check(FloorRecord floor, ulong serial, Bytes32 hash)
    {
        if (serial < floor.Serial) return ErrorCode.DefinitionRollback;
        if (serial == floor.Serial && floor.Serial != 0 && !floor.Hash.Equals(hash)) return ErrorCode.DefinitionSetMismatch;
        return null;
    }

    /// <summary>Reads the store and applies <see cref="Check"/>. An unreadable or unsafe store is <c>trust.store_insecure</c>.</summary>
    public static string? Evaluate(IAntiRollbackStore store, ulong serial, Bytes32 hash, out FloorRecord floor)
    {
        var read = store.Read();
        floor = read.Record;
        return read.Status switch
        {
            FloorReadStatus.Absent or FloorReadStatus.Present => Check(read.Record, serial, hash),
            _ => ErrorCode.TrustStoreInsecure,
        };
    }

    /// <summary>
    /// Raises the floor to the verified set after the batch was accepted. No write happens for a serial that is not greater; a lost race (the store changed)
    /// returns false and the caller must stop before it mutates anything.
    /// </summary>
    public static bool Advance(IAntiRollbackStore store, FloorRecord current, ulong serial, Bytes32 hash)
    {
        if (serial < current.Serial) return false;
        if (serial == current.Serial && current.Serial != 0) return current.Hash.Equals(hash);
        return store.TryAdvance(current, new FloorRecord(serial, hash));
    }
}

/// <summary>Hash of a definition set used as H_d: <c>SHA-256("winora/defset/v1/hash\0" || bytes)</c>.</summary>
public static class DefinitionSetHash
{
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("winora/defset/v1/hash\0");

    public static Bytes32 Compute(ReadOnlySpan<byte> setBytes)
    {
        var buffer = new byte[Label.Length + setBytes.Length];
        Label.CopyTo(buffer, 0);
        setBytes.CopyTo(buffer.AsSpan(Label.Length));
        return new Bytes32(SHA256.HashData(buffer));
    }

    /// <summary>
    /// Hash of a set that is split over several files. The caller passes the files in their stable order (ordinal by name). One file hashes exactly like <see cref="Compute"/>;
    /// several files hash their lengths and bytes in order, so moving content between files or reordering them changes the hash.
    /// </summary>
    public static Bytes32 ComputeMany(IReadOnlyList<byte[]> files)
    {
        if (files.Count == 1) return Compute(files[0]);
        using var stream = new MemoryStream();
        stream.Write(Label);
        stream.Write("many\0"u8);
        Span<byte> length = stackalloc byte[8];
        foreach (var file in files)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, file.Length);
            stream.Write(length);
            stream.Write(file);
        }
        return new Bytes32(SHA256.HashData(stream.ToArray()));
    }
}
