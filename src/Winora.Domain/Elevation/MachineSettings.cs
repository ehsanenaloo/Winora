// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
namespace Winora.Domain.Elevation;

/// <summary>
/// Target kind byte of a snapshot (A.3.1). Kind 2 exists only for the self-test. <see cref="DisplayDriverValue"/> is a definition kind only: the state of such a target is
/// carried as a registry snapshot (kind 0) of the resolved driver key and the kind never appears on the wire.
/// </summary>
public enum MachineTargetKind : byte { RegistryValue = 0, ServiceStartType = 1, Selftest = 2, DisplayDriverValue = 3 }

/// <summary>Value kind of a machine value (A.3.2). The numeric values are the wire kind byte; 4 to 255 are reserved and rejected.</summary>
public enum MachineValueKind : byte { Absent = 0, DWord = 1, QWord = 2, String = 3 }

public enum MachineRecovery { Reversible, Conditional }
/// <summary>Admissible service start types. <see cref="AutomaticDelayed"/> is automatic start with the delayed flag; it is a separate configuration written with its own call.</summary>
public enum ServiceStartKind { Automatic, Manual, Disabled, AutomaticDelayed }

/// <summary>Class of the sources of a definition: A Microsoft documentation, B vendor documentation or a recognized expert, C community or observed behavior.</summary>
public enum SourceClass { A, B, C }

/// <summary>How the bits of a bit-field target are stored: as a DWORD, or as a decimal number in a REG_SZ value.</summary>
public enum BitStorage { DWord, DecimalString }
public enum ManagedByKind { PolicyRegistryValue, PolicyCsp }

/// <summary>
/// A typed machine-scope value. Immutable and bounded: DWORD fits 32 bits, a string is at most 1024 bytes of
/// well-formed UTF-16. Numbers are unsigned because the wire has no signed integers. The type carries no behavior beyond validation.
/// </summary>
public sealed record MachineValue
{
    public MachineValueKind Kind { get; }
    public ulong? Number { get; }
    public string? Text { get; }
    private MachineValue(MachineValueKind kind, ulong? number, string? text) { Kind = kind; Number = number; Text = text; }

    public static MachineValue Absent { get; } = new(MachineValueKind.Absent, null, null);
    public static MachineValue DWord(uint value) => new(MachineValueKind.DWord, value, null);
    public static MachineValue QWord(ulong value) => new(MachineValueKind.QWord, value, null);
    public static MachineValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > ElevationLimits.MaxRestoreStringBytes / 2) throw new ArgumentException("string value too long", nameof(value));
        if (!IsWellFormed(value)) throw new ArgumentException("string value is not well-formed UTF-16", nameof(value));
        return new(MachineValueKind.String, null, value);
    }

    /// <summary>Number of payload bytes the value occupies on the wire (the <c>len</c> field of A.3.2).</summary>
    public int PayloadLength => Kind switch { MachineValueKind.DWord => 4, MachineValueKind.QWord => 8, MachineValueKind.String => Text!.Length * 2, _ => 0 };

    /// <summary>Builds a value from a decoded kind byte and payload; false for reserved kinds, wrong lengths or malformed text.</summary>
    public static bool TryFromWire(byte kind, ReadOnlySpan<byte> payload, out MachineValue value)
    {
        value = Absent;
        switch (kind)
        {
            case (byte)MachineValueKind.Absent when payload.IsEmpty: return true;
            case (byte)MachineValueKind.DWord when payload.Length == 4: value = DWord(BinaryPrimitives.ReadUInt32LittleEndian(payload)); return true;
            case (byte)MachineValueKind.QWord when payload.Length == 8: value = QWord(BinaryPrimitives.ReadUInt64LittleEndian(payload)); return true;
            case (byte)MachineValueKind.String when payload.Length % 2 == 0 && payload.Length <= ElevationLimits.MaxRestoreStringBytes && IsWellFormedUtf16(payload):
                value = String(System.Text.Encoding.Unicode.GetString(payload)); return true;
            default: return false;
        }
    }

    internal static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c)) { if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false; i++; }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }

    internal static bool IsWellFormedUtf16(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i += 2)
        {
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[i..]);
            if (char.IsHighSurrogate(c))
            {
                if (i + 3 >= bytes.Length || !char.IsLowSurrogate((char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i + 2)..]))) return false;
                i += 2;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }
}

/// <summary>
/// Canonical, self-delimiting snapshot of a machine target (A.3.1): the first byte is the <see cref="MachineTargetKind"/>.
/// Construction validates the layout only (kinds, lengths, flags); semantic checks against a definition belong to the caller.
/// </summary>
public sealed class MachineSnapshot : IEquatable<MachineSnapshot>
{
    private readonly byte[] bytes;
    private MachineSnapshot(byte[] canonical) => bytes = canonical;
    public MachineTargetKind Kind => (MachineTargetKind)bytes[0];
    public ReadOnlySpan<byte> AsSpan() => bytes;
    public int Length => bytes.Length;
    public byte[] ToArray() => (byte[])bytes.Clone();

    /// <summary>Self-test snapshot (kind 2): <c>02 len payload</c>; payload follows the identifier grammar, at most 37 bytes.</summary>
    public static MachineSnapshot Selftest(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > ElevationLimits.MaxSelftestPayloadBytes || (payload.Length > 0 && !ElevationIdentifier.IsValid(payload, 1, ElevationLimits.MaxSelftestPayloadBytes)))
            throw new ArgumentException("self-test payload is out of grammar", nameof(payload));
        var result = new byte[2 + payload.Length];
        result[0] = (byte)MachineTargetKind.Selftest; result[1] = (byte)payload.Length;
        System.Text.Encoding.ASCII.GetBytes(payload, result.AsSpan(2));
        return new(result);
    }
    public static MachineSnapshot SelftestEmpty { get; } = Selftest("");

    /// <summary>Registry snapshot (kind 0, A.3.1). <c>keyExists = false</c> forces no value; an absent value has kind 0 and no data. View: 0 = 64-bit, 1 = 32-bit.</summary>
    public static MachineSnapshot Registry(bool keyExists, bool valueExists, RegistryViewKind view, MachineValue? value)
    {
        if (!keyExists && valueExists) throw new ArgumentException("a value cannot exist without its key", nameof(valueExists));
        var v = valueExists ? value ?? throw new ArgumentNullException(nameof(value)) : MachineValue.Absent;
        if (valueExists && v.Kind == MachineValueKind.Absent) throw new ArgumentException("an existing value has a data kind", nameof(value));
        var data = v.Kind switch
        {
            MachineValueKind.DWord => BitConverter.GetBytes((uint)v.Number!.Value),
            MachineValueKind.QWord => BitConverter.GetBytes(v.Number!.Value),
            MachineValueKind.String => System.Text.Encoding.Unicode.GetBytes(v.Text!),
            _ => [],
        };
        var result = new byte[7 + data.Length];
        result[0] = (byte)MachineTargetKind.RegistryValue; result[1] = keyExists ? (byte)1 : (byte)0; result[2] = valueExists ? (byte)1 : (byte)0;
        result[3] = view == RegistryViewKind.Registry32 ? (byte)1 : (byte)0; result[4] = (byte)v.Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(5), (ushort)data.Length);
        data.CopyTo(result.AsSpan(7));
        return new(result);
    }

    /// <summary>Service start-type snapshot (kind 1): start kind 0 boot, 1 system, 2 automatic, 3 demand, 4 disabled; the delayed flag is 1 only for an existing automatic service with delayed start.</summary>
    public static MachineSnapshot Service(bool exists, byte startKind, bool delayed = false)
    {
        if (startKind > 4) throw new ArgumentOutOfRangeException(nameof(startKind));
        if (delayed && (startKind != 2 || !exists)) throw new ArgumentException("only an existing automatic service can be delayed", nameof(delayed));
        return new([(byte)MachineTargetKind.ServiceStartType, exists ? (byte)1 : (byte)0, startKind, delayed ? (byte)1 : (byte)0]);
    }

    /// <summary>The comparable start value of a delayed automatic service. It is a virtual value: on the wire the start kind is 2 with the delayed flag set.</summary>
    public const byte DelayedStartValue = 5;

    /// <summary>Comparable start value of an admissible service start type (automatic 2, manual 3, disabled 4, automatic delayed 5).</summary>
    public static byte ServiceStartByte(ServiceStartKind kind) => kind switch { ServiceStartKind.Automatic => 2, ServiceStartKind.Manual => 3, ServiceStartKind.AutomaticDelayed => DelayedStartValue, _ => 4 };

    /// <summary>Reads a kind-0 snapshot: key presence, view and the value (<see cref="MachineValue.Absent"/> when no value exists).</summary>
    public bool TryReadRegistry(out bool keyExists, out RegistryViewKind view, out MachineValue value)
    {
        keyExists = false; view = RegistryViewKind.Registry64; value = MachineValue.Absent;
        if (Kind != MachineTargetKind.RegistryValue) return false;
        keyExists = bytes[1] == 1; view = bytes[3] == 1 ? RegistryViewKind.Registry32 : RegistryViewKind.Registry64;
        if (bytes[2] == 0) return true;
        return MachineValue.TryFromWire(bytes[4], bytes.AsSpan(7), out value);
    }

    /// <summary>Reads a kind-1 snapshot: whether the service exists and its wire start kind.</summary>
    public bool TryReadService(out bool exists, out byte startKind)
    {
        exists = false; startKind = 0;
        if (Kind != MachineTargetKind.ServiceStartType) return false;
        exists = bytes[1] == 1; startKind = bytes[2];
        return true;
    }

    /// <summary>Whether a kind-1 snapshot carries the delayed automatic start flag.</summary>
    public bool IsDelayedService => Kind == MachineTargetKind.ServiceStartType && bytes[3] == 1;

    /// <summary>
    /// The comparable value of either target kind: the registry value, or the start value of an existing service as a DWORD (2 automatic, 3 manual, 4 disabled, 5 automatic delayed).
    /// False for a missing service, a start kind outside that set, a kind-2 snapshot or an unreadable layout.
    /// </summary>
    public bool TryReadValue(out MachineValue value)
    {
        value = MachineValue.Absent;
        switch (Kind)
        {
            case MachineTargetKind.RegistryValue: return TryReadRegistry(out _, out _, out value);
            case MachineTargetKind.ServiceStartType:
                if (bytes[1] != 1 || bytes[2] is < 2 or > 4) return false;
                value = MachineValue.DWord(bytes[2] == 2 && bytes[3] == 1 ? DelayedStartValue : bytes[2]);
                return true;
            default: return false;
        }
    }

    /// <summary>Parses one snapshot from the front of <paramref name="source"/>; <paramref name="consumed"/> is its length. Rejects any layout violation.</summary>
    public static bool TryParse(ReadOnlySpan<byte> source, out MachineSnapshot snapshot, out int consumed)
    {
        snapshot = SelftestEmpty; consumed = 0;
        if (source.IsEmpty) return false;
        int length;
        switch (source[0])
        {
            case (byte)MachineTargetKind.RegistryValue:
            {
                if (source.Length < 7) return false;
                if (source[1] > 1 || source[2] > 1 || source[3] > 1 || source[4] > 3) return false;
                if (source[1] == 0 && source[2] != 0) return false;   // no key, so no value
                if (source[2] == 0 && source[4] != 0) return false;   // no value, so no data kind (the length rule below then forces empty data)
                int len = BinaryPrimitives.ReadUInt16LittleEndian(source[5..]);
                var valid = source[4] switch
                {
                    0 => len == 0, 1 => len == 4, 2 => len == 8,
                    _ => len % 2 == 0 && len <= ElevationLimits.MaxRestoreStringBytes,
                };
                length = 7 + len;
                if (!valid || source.Length < length) return false;
                if (source[4] == 3 && !MachineValue.IsWellFormedUtf16(source.Slice(7, len))) return false;
                break;
            }
            case (byte)MachineTargetKind.ServiceStartType:
                length = 4;
                if (source.Length < length || source[1] > 1 || source[2] > 4 || source[3] > 1) return false;
                if (source[3] == 1 && (source[1] != 1 || source[2] != 2)) return false;   // only an existing automatic service can be delayed
                break;
            case (byte)MachineTargetKind.Selftest:
            {
                if (source.Length < 2 || source[1] > ElevationLimits.MaxSelftestPayloadBytes) return false;
                length = 2 + source[1];
                if (source.Length < length) return false;
                foreach (var b in source.Slice(2, source[1])) if (!(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'_')) return false;
                break;
            }
            default: return false;
        }
        snapshot = new(source[..length].ToArray()); consumed = length;
        return true;
    }

    public bool Equals(MachineSnapshot? other) => other is not null && bytes.AsSpan().SequenceEqual(other.bytes);
    public override bool Equals(object? obj) => Equals(obj as MachineSnapshot);
    public override int GetHashCode() { var h = new HashCode(); h.AddBytes(bytes); return h.ToHashCode(); }
}

// ---- Definition schema: data only. Admission invariants (allow/deny lists, aliasing, restore domain) belong to the
// ---- definition verifier of a later work package and are deliberately not implemented here. There is no hive field: HKLM only.

public sealed record MachineRegistryTarget(string SubKey, string ValueName, RegistryViewKind View)
{
    /// <summary>When set, the definition owns only these bits of the value; every other bit is preserved on a change and on an undo.</summary>
    public MachineBitField? Bits { get; init; }
    /// <summary>When true, REG_SZ values of the definition may contain a percent sign. The text is still written as plain REG_SZ and never expanded.</summary>
    public bool AllowPercentInText { get; init; }
}

/// <summary>The bits a bit-field definition owns: a non-zero mask over a 32-bit number stored as a DWORD or as a decimal REG_SZ. An absent value reads as zero.</summary>
public sealed record MachineBitField(uint Mask, BitStorage Storage);
/// <summary>
/// One value of the driver key of display adapters of one vendor. The definition names only the value and the PCI vendor id (4 hex digits, upper case); the key is never written in a
/// definition: code resolves it for each present adapter.
/// </summary>
public sealed record MachineDisplayDriverTarget(string ValueName, string VendorId);
/// <summary>Service name <c>[A-Za-z0-9_.-]{1,64}</c>, addressed through the service control manager, never a path.</summary>
public sealed record MachineServiceTarget(string ServiceName);
public sealed record MachineChoice(string Id, string LabelKey, MachineValue? Value, ServiceStartKind? Start)
{
    /// <summary>What the target holds when this choice is applied: the registry value, or the wire start kind of a service as a DWORD.</summary>
    public MachineValue? EffectiveValue => Value ?? (Start is { } s ? MachineValue.DWord(MachineSnapshot.ServiceStartByte(s)) : null);
}
/// <summary>A documented policy channel that can manage the setting.</summary>
public sealed record ManagedByChannel(ManagedByKind Kind, string Location, string? ValueName);

public sealed record MachineSettingDefinition(
    string Id, int Version, string TitleKey, string DescriptionKey, SettingCategory Category, RiskLevel Risk,
    MachineRecovery Recovery, bool RequiresRestart, int MinBuild, int? MaxBuild,
    MachineTargetKind TargetKind, MachineRegistryTarget? Registry, MachineServiceTarget? Service,
    IReadOnlyList<MachineChoice> Choices, string DefaultChoiceId, string? RecommendedChoiceId,
    IReadOnlyList<MachineValue> RestoreDomain, IReadOnlyList<int> RestoreCompatible, bool AllowRestore,
    IReadOnlyList<ManagedByChannel> ManagedBy, IReadOnlyList<string> SourceRefs, string ReviewRef)
{
    private static readonly IReadOnlyList<MachineValue> ServiceDomain = [MachineValue.DWord(2), MachineValue.DWord(3), MachineValue.DWord(4), MachineValue.DWord(MachineSnapshot.DelayedStartValue)];

    /// <summary>Class of the sources (default A: Microsoft documentation).</summary>
    public SourceClass SourceClass { get; init; } = SourceClass.A;
    /// <summary>Non-Microsoft source addresses (https, from a fixed host list) for classes B and C.</summary>
    public IReadOnlyList<string> CommunitySources { get; init; } = [];
    /// <summary>The target of a <see cref="MachineTargetKind.DisplayDriverValue"/> definition; null for every other kind.</summary>
    public MachineDisplayDriverTarget? DisplayDriver { get; init; }
    /// <summary>Windows editions the setting applies to (Home, Pro, Enterprise, Education); empty means every edition.</summary>
    public IReadOnlyList<string> Editions { get; init; } = [];

    /// <summary>True when every policy channel is the registry value the definition itself writes, so a value written by the app is the same value a policy would set.</summary>
    public bool ManagedOnlyAtTarget => Registry is { } r && ManagedBy.Count > 0 && ManagedBy.All(c =>
        c.Kind == ManagedByKind.PolicyRegistryValue && string.Equals(c.Location, r.SubKey, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ValueName, r.ValueName, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the definition owns only some bits of a numeric value.</summary>
    public bool IsBitField => Registry?.Bits is not null;

    /// <summary>
    /// The value the definition compares: the whole value for a plain target, the start value for a service, and for a bit field the owned bits of a DWORD or decimal text
    /// (an absent value counts as zero). False for a value that is not representable.
    /// </summary>
    public bool TryReadComparable(MachineSnapshot snapshot, out MachineValue value)
    {
        if (Registry?.Bits is not { } bits) return snapshot.TryReadValue(out value);
        value = MachineValue.Absent;
        if (!snapshot.TryReadRegistry(out _, out _, out var raw)) return false;
        if (!TryReadBits(raw, bits, out var number)) return false;
        value = MachineValue.DWord(number & bits.Mask);
        return true;
    }

    /// <summary>The raw number of a bit-field value: zero when absent, the DWORD, or the parsed decimal text. False for another kind or unparsable text.</summary>
    public static bool TryReadBits(MachineValue raw, MachineBitField bits, out uint number)
    {
        number = 0;
        switch (raw.Kind)
        {
            case MachineValueKind.Absent: return true;
            case MachineValueKind.DWord when bits.Storage == BitStorage.DWord: number = (uint)raw.Number!.Value; return true;
            case MachineValueKind.String when bits.Storage == BitStorage.DecimalString:
                return raw.Text!.Length is > 0 and <= 10 && raw.Text.All(char.IsAsciiDigit) && uint.TryParse(raw.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number);
            default: return false;
        }
    }

    /// <summary>The raw value after changing only the owned bits to <paramref name="owned"/> and keeping all others from <paramref name="raw"/>. Null when <paramref name="raw"/> is not representable.</summary>
    public MachineValue? WithBits(MachineValue raw, uint owned)
    {
        if (Registry?.Bits is not { } bits || !TryReadBits(raw, bits, out var number)) return null;
        var result = (number & ~bits.Mask) | (owned & bits.Mask);
        return bits.Storage == BitStorage.DWord ? MachineValue.DWord(result) : MachineValue.String(result.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The values the target may hold before and after a change: the declared restore domain, or for a service the three admissible start types.</summary>
    public IReadOnlyList<MachineValue> EffectiveRestoreDomain => TargetKind == MachineTargetKind.ServiceStartType ? ServiceDomain : RestoreDomain;
}
