// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>Append-only little-endian writer for the encodings of Appendix A. Inputs are Domain values already validated by construction.</summary>
internal sealed class WireWriter
{
    private byte[] buffer = new byte[128];
    private int length;
    public int Length => length;

    private Span<byte> Reserve(int count)
    {
        if (length + count > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + count));
        var span = buffer.AsSpan(length, count);
        length += count;
        return span;
    }
    public WireWriter U8(byte value) { Reserve(1)[0] = value; return this; }
    public WireWriter U16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value); return this; }
    public WireWriter U32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value); return this; }
    public WireWriter U64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value); return this; }
    public WireWriter Bytes(ReadOnlySpan<byte> value) { value.CopyTo(Reserve(value.Length)); return this; }
    public WireWriter Ascii(string value) { Encoding.ASCII.GetBytes(value, Reserve(value.Length)); return this; }
    public WireWriter B16(Bytes16 value) { value.CopyTo(Reserve(16)); return this; }
    public WireWriter B32(Bytes32 value) { value.CopyTo(Reserve(32)); return this; }
    public WireWriter Point(PublicPoint65 value) => Bytes(value.AsSpan());
    /// <summary>S(x): u8 length then ASCII bytes.</summary>
    public WireWriter S(string value) { U8((byte)value.Length); return Ascii(value); }
    /// <summary>B(x): u32 length then bytes.</summary>
    public WireWriter Blob(ReadOnlySpan<byte> value) { U32((uint)value.Length); return Bytes(value); }
    public WireWriter OperationId(Guid id) { ElevationIds.WriteOperationId(id, Reserve(16)); return this; }
    public WireWriter Value(MachineValue value)
    {
        U8((byte)value.Kind).U16((ushort)value.PayloadLength);
        switch (value.Kind)
        {
            case MachineValueKind.DWord: U32((uint)value.Number!.Value); break;
            case MachineValueKind.QWord: U64(value.Number!.Value); break;
            case MachineValueKind.String: Bytes(Encoding.Unicode.GetBytes(value.Text!)); break;
        }
        return this;
    }
    public WireWriter Result(ElevationResult result) => S(result.Code).U8((byte)result.Severity).U8(result.Retryable ? (byte)1 : (byte)0).S(result.Detail);
    public WireWriter Item(BatchItem item) =>
        OperationId(item.OperationId).S(item.DefinitionId).U32(item.DefinitionVersion).U8((byte)item.Mode).S(item.ChoiceId).Value(item.RestoreValue).B32(item.PlanTokenDigest);
    public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();
}

/// <summary>Strict forward-only reader. Every method returns false on any shortfall or grammar violation and never throws.</summary>
internal ref struct WireReader
{
    private readonly ReadOnlySpan<byte> data;
    private int position;
    public WireReader(ReadOnlySpan<byte> data) { this.data = data; position = 0; }
    public readonly int Remaining => data.Length - position;
    public readonly bool AtEnd => position == data.Length;

    public bool U8(out byte value)
    {
        value = 0;
        if (Remaining < 1) return false;
        value = data[position++];
        return true;
    }
    public bool U16(out ushort value)
    {
        value = 0;
        if (Remaining < 2) return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]); position += 2;
        return true;
    }
    public bool U32(out uint value)
    {
        value = 0;
        if (Remaining < 4) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]); position += 4;
        return true;
    }
    public bool U64(out ulong value)
    {
        value = 0;
        if (Remaining < 8) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(data[position..]); position += 8;
        return true;
    }
    public bool Take(int count, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (count < 0 || Remaining < count) return false;
        value = data.Slice(position, count); position += count;
        return true;
    }
    public bool B16(out Bytes16 value)
    {
        value = default;
        if (!Take(16, out var s)) return false;
        value = new Bytes16(s);
        return true;
    }
    public bool B32(out Bytes32 value)
    {
        value = default;
        if (!Take(32, out var s)) return false;
        value = new Bytes32(s);
        return true;
    }
    public bool Point(out PublicPoint65 value)
    {
        value = default;
        if (!Take(ElevationLimits.PublicKeyBytes, out var s) || s[0] != 0x04) return false;
        value = new PublicPoint65(s);
        return true;
    }
    public bool OperationId(out Guid id)
    {
        id = Guid.Empty;
        if (!Take(16, out var s)) return false;
        id = ElevationIds.ReadOperationId(s);
        return id != Guid.Empty;
    }
    /// <summary>S(x): u8 length in [min, 64], then ASCII from [a-z0-9._].</summary>
    public bool S(int min, out string value)
    {
        value = "";
        if (!U8(out var len) || len < min || len > ElevationLimits.MaxIdentifierBytes || !Take(len, out var raw)) return false;
        foreach (var b in raw) if (!(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'_')) return false;
        value = Encoding.ASCII.GetString(raw);
        return true;
    }
    /// <summary>B(x) holding a binary SID: u32 length at most 68, well-formed structure.</summary>
    public bool Sid(out BinarySid sid)
    {
        sid = default;
        if (!U32(out var len) || len > ElevationLimits.MaxSidBytes || !Take((int)len, out var raw) || !BinarySid.IsValid(raw)) return false;
        sid = new BinarySid(raw);
        return true;
    }
    public bool Value(out MachineValue value)
    {
        value = MachineValue.Absent;
        if (!U8(out var kind) || !U16(out var len) || !Take(len, out var payload)) return false;
        return MachineValue.TryFromWire(kind, payload, out value);
    }
    /// <summary>One canonical snapshot (A.3.1 plus the canonical-form rules of the implementation clarifications).</summary>
    public bool Snapshot(out MachineSnapshot snapshot)
    {
        snapshot = MachineSnapshot.SelftestEmpty;
        if (!MachineSnapshot.TryParse(data[position..], out var parsed, out var consumed) || !WireCodec.IsCanonical(parsed)) return false;
        position += consumed; snapshot = parsed;
        return true;
    }
    public bool Result(out ElevationResult result)
    {
        result = null!;
        if (!S(1, out var code) || !U8(out var severity) || severity > 3 || !U8(out var retryable) || retryable > 1 || !S(0, out var detail)) return false;
        if (detail.Length != 0 && !ElevationIdentifier.IsValidToken(detail)) return false;
        result = new ElevationResult(code, (ErrorSeverity)severity, retryable == 1, detail);
        return true;
    }
    public bool Item(out BatchItem item)
    {
        item = null!;
        if (!OperationId(out var id) || !S(1, out var definition) || !U32(out var version) || !U8(out var mode) || mode > 1) return false;
        if (!S(mode == 0 ? 1 : 0, out var choice) || (mode == 1 && choice.Length != 0) || !Value(out var restore)) return false;
        if (mode == 0 && restore.Kind != MachineValueKind.Absent) return false;
        if (!B32(out var plan)) return false;
        item = new BatchItem(id, definition, version, (PrepareMode)mode, choice, restore, plan);
        return true;
    }
}
