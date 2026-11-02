// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
namespace Winora.Domain;

/// <summary>
/// Durable journal row for one registry-setting operation (schema v3 table setting_operations). It carries identity, intent and typed
/// before/after snapshots ONLY. It never carries a write target: the target is always re-resolved from the trusted catalog by
/// <see cref="SettingId"/> and checked against <see cref="DefinitionVersion"/> and <see cref="TargetFingerprint"/>.
/// </summary>
public sealed record SettingOperationRecord(
    Guid Id, string SettingId, int DefinitionVersion, string TargetFingerprint, string ChoiceId, OperationState State,
    bool CompensationPrepared, string Code, RegistrySnapshot Before, RegistrySnapshot? After, long? ObservedRevision, long? AppliedRevision,
    bool NeedsExplorerRestart, bool NeedsSignOut, int OwnerProcessId, long OwnerProcessStartTicks, int SchemaVersion,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, Guid? Group = null, string? Label = null)
{
    public const int CurrentSchemaVersion = 1;
    /// <summary>Journal row revision (column, not payload). Save expects the stored value and the store advances it.</summary>
    [JsonIgnore] public long Revision { get; init; }
}

/// <summary>A setting_operations row as stored; Record is null when the payload cannot be trusted.</summary>
public sealed record SettingJournalEntry(Guid Id, long Revision, SettingOperationRecord? Record, string? Fault);

/// <summary>Reading and changing a number that is stored as a DWORD or as a decimal string (the accessibility flags are strings).</summary>
public static class RegistryBits
{
    public static bool TryRead(RegistryDataKind? kind, byte[]? raw, out uint value)
    {
        value = 0;
        if (raw is null) return false;
        switch (kind)
        {
            case RegistryDataKind.DWord when raw.Length == 4:
                value = BitConverter.ToUInt32(raw);
                return true;
            case RegistryDataKind.String when raw.Length is > 0 and <= 20 && raw.Length % 2 == 0:
                var text = Encoding.Unicode.GetString(raw);
                return text.All(char.IsAsciiDigit) && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    public static byte[] Encode(RegistryDataKind container, uint value) => container == RegistryDataKind.DWord
        ? BitConverter.GetBytes(value)
        : Encoding.Unicode.GetBytes(value.ToString(CultureInfo.InvariantCulture));

    public static uint Apply(RegistryValueSpec spec, uint current) => spec.Op == RegistryValueOp.ClearBits ? current & ~spec.Mask : current | spec.Mask;

    /// <summary>Whether a stored number already has the bits the spec asks for.</summary>
    public static bool Satisfies(RegistryValueSpec spec, uint current) => spec.Op == RegistryValueOp.ClearBits ? (current & spec.Mask) == 0 : (current & spec.Mask) == spec.Mask;
}

/// <summary>Pure snapshot helpers. Snapshots hold byte arrays, so record equality is never used to compare them. A grouped setting has one snapshot per member, chained through <see cref="RegistrySnapshot.More"/>.</summary>
public static class SettingSnapshots
{
    public static IReadOnlyList<RegistrySnapshot> Members(RegistrySnapshot snapshot) => snapshot.More is null ? [snapshot] : [snapshot with { More = null }, .. snapshot.More];

    public static RegistrySnapshot Group(IReadOnlyList<RegistrySnapshot> members) => members.Count == 1 ? members[0] : members[0] with { More = members.Skip(1).ToList() };

    /// <summary>Same key/value presence, view, kind and bytes, member by member.</summary>
    public static bool SameSnapshot(RegistrySnapshot? a, RegistrySnapshot? b)
    {
        if (a is null || b is null) return a is null && b is null;
        var x = Members(a); var y = Members(b);
        return x.Count == y.Count && x.Zip(y).All(p => SameOne(p.First, p.Second));
    }

    private static bool SameOne(RegistrySnapshot a, RegistrySnapshot b) =>
        a.KeyExists == b.KeyExists && a.ValueExists == b.ValueExists && a.View == b.View && a.Kind == b.Kind && SameBytes(a.RawData, b.RawData);

    /// <summary>Whether the live value already is the desired one (an absent spec matches any missing value, including a missing key). A group matches when every member does.</summary>
    public static bool Matches(RegistrySnapshot snapshot, RegistryValueSpec spec)
    {
        var s = Members(snapshot); var d = spec.Members;
        return s.Count == d.Count && s.Zip(d).All(p => MatchesOne(p.First, p.Second));
    }

    private static bool MatchesOne(RegistrySnapshot snapshot, RegistryValueSpec spec)
    {
        switch (spec.Op)
        {
            case RegistryValueOp.KeyAbsent:
                return !snapshot.KeyExists;
            case RegistryValueOp.KeyPresent:
                return snapshot.KeyExists && (spec.Kind == RegistryDataKind.None ||
                    snapshot.ValueExists && snapshot.Kind == spec.Kind && SameBytes(snapshot.RawData, spec.Raw));
            case RegistryValueOp.ClearBits or RegistryValueOp.SetBits:
                return snapshot.ValueExists && snapshot.Kind == spec.Kind && RegistryBits.TryRead(snapshot.Kind, snapshot.RawData, out var number) && RegistryBits.Satisfies(spec, number);
            default:
                return spec.Kind == RegistryDataKind.None
                    ? !snapshot.ValueExists
                    : snapshot.ValueExists && snapshot.Kind == spec.Kind && SameBytes(snapshot.RawData, spec.Raw);
        }
    }

    /// <summary>The value spec that restores a recorded snapshot of a single value: Absent when no value existed, otherwise kind plus bytes.</summary>
    public static RegistryValueSpec ToSpec(RegistrySnapshot snapshot) =>
        snapshot.ValueExists && snapshot.Kind is { } kind && kind != RegistryDataKind.None
            ? new RegistryValueSpec(kind, snapshot.RawData ?? [])
            : RegistryValueSpec.Absent;

    /// <summary>The spec that restores a recorded snapshot of a definition, member by member. It restores exact values, never a bit pattern computed from today's value.</summary>
    public static RegistryValueSpec ToSpec(SettingDefinition definition, RegistrySnapshot snapshot)
    {
        var targets = definition.Targets; var members = Members(snapshot);
        var specs = new RegistryValueSpec[members.Count];
        for (var i = 0; i < members.Count; i++) specs[i] = ToMemberSpec(i < targets.Count ? targets[i].Mode : RegistryKeyMode.Value, members[i]);
        return RegistryValueSpec.Group(specs);
    }

    /// <summary>The spec that restores one recorded member.</summary>
    public static RegistryValueSpec ToMemberSpec(RegistryKeyMode mode, RegistrySnapshot member) => mode switch
    {
        RegistryKeyMode.KeyOnly => member.KeyExists ? RegistryValueSpec.KeyPresent : RegistryValueSpec.KeyAbsent,
        RegistryKeyMode.KeyWithEmptyDefault => member.KeyExists ? RegistryValueSpec.KeyWithEmptyDefault : RegistryValueSpec.KeyAbsent,
        _ => ToSpec(member),
    };

    public static bool SameBytes(byte[]? a, byte[]? b) => (a ?? []).AsSpan().SequenceEqual(b ?? []);

    /// <summary>
    /// A snapshot is legal for a definition when it has one member per target, each in the definition's view, and a present value has a kind some choice can write.
    /// A key member is legal when the key is missing or holds exactly what a choice creates.
    /// </summary>
    public static bool IsLegalFor(SettingDefinition definition, RegistrySnapshot snapshot)
    {
        var targets = definition.Targets; var members = Members(snapshot);
        if (members.Count != targets.Count) return false;
        for (var i = 0; i < targets.Count; i++)
        {
            var m = members[i];
            if (!Enum.IsDefined(m.View) || m.View != targets[i].View) return false;
            if (m.Kind is { } k && !Enum.IsDefined(k)) return false;
            switch (targets[i].Mode)
            {
                case RegistryKeyMode.KeyOnly:
                    if (m.ValueExists) return false;
                    break;
                case RegistryKeyMode.KeyWithEmptyDefault:
                    if (!m.KeyExists) { if (m.ValueExists) return false; break; }
                    if (!m.ValueExists || m.Kind != RegistryDataKind.String || m.RawData is { Length: > 0 }) return false;
                    break;
                default:
                    if (!m.ValueExists)
                    {
                        if (!(m.Kind is null or RegistryDataKind.None && m.RawData is null or { Length: 0 })) return false;
                        break;
                    }
                    if (!m.KeyExists || m.Kind is null or RegistryDataKind.None) return false;
                    // A member that no choice writes (a choice only removes it) may hold any kind: the journal restores the exact kind and bytes.
                    var index = i;
                    var written = definition.Choices.Select(c => c.Value.Members.Count > index ? c.Value.Members[index].Kind : RegistryDataKind.None).Where(k => k != RegistryDataKind.None).ToList();
                    if (written.Count > 0 && !written.Contains(m.Kind!.Value)) return false;
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// Whether a choice can be applied to the observed state. A bit operation changes a number that is already stored, so a missing or unreadable number refuses the change
    /// instead of guessing the other bits.
    /// </summary>
    public static bool IsApplicable(SettingChoice choice, RegistrySnapshot observed)
    {
        var specs = choice.Value.Members; var members = Members(observed);
        if (specs.Count != members.Count) return false;
        for (var i = 0; i < specs.Count; i++)
        {
            if (!specs[i].IsBitOp) continue;
            if (!(members[i].ValueExists && members[i].Kind == specs[i].Kind && RegistryBits.TryRead(members[i].Kind, members[i].RawData, out _))) return false;
        }
        return true;
    }

    /// <summary>True when no member holds anything: every value is missing and every key member is missing.</summary>
    public static bool IsUnset(SettingDefinition definition, RegistrySnapshot snapshot)
    {
        var targets = definition.Targets; var members = Members(snapshot);
        for (var i = 0; i < members.Count; i++)
        {
            var key = i < targets.Count && targets[i].IsKey;
            if (key ? members[i].KeyExists : members[i].ValueExists) return false;
        }
        return true;
    }

    /// <summary>Stable identity of a catalog target; stored in the journal so a changed catalog target is detected, never followed.</summary>
    public static string TargetFingerprint(SettingDefinition definition) =>
        string.Join(";", definition.Targets.Select(t =>
            string.Create(CultureInfo.InvariantCulture, $"hkcu|{t.View}|{t.SubKey}|{t.ValueName}") + (t.Mode == RegistryKeyMode.Value ? "" : "|" + t.Mode)));

    /// <summary>The choice whose value the snapshot matches, or null.</summary>
    public static string? MatchChoice(SettingDefinition definition, RegistrySnapshot? snapshot) =>
        snapshot is null ? null : definition.Choices.FirstOrDefault(c => Matches(snapshot, c.Value))?.Id;
}

/// <summary>Binds a preview to the definition version, choice, typed snapshot and key revision it observed.</summary>
public static class SettingPlanTokens
{
    public static string Compute(string settingId, int definitionVersion, string choiceId, RegistrySnapshot observed, long? revision)
    {
        var members = string.Join("\n", SettingSnapshots.Members(observed).Select(m =>
            $"{m.KeyExists}|{m.ValueExists}|{m.View}|{m.Kind}|{Convert.ToBase64String(m.RawData ?? [])}"));
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{settingId}\n{definitionVersion}\n{choiceId}\n{members}\n{revision?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
