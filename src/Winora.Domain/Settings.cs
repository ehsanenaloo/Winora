// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Domain;

/// <summary>
/// user-scope registry settings. Definitions are trusted DATA authored in this repository; profiles, journals and UI input only
/// reference them by id. There is deliberately no type here that carries an arbitrary key path from outside the catalog.
/// </summary>
public enum SettingCategory { Explorer, Taskbar, Privacy, Search, Appearance, Desktop, Start, Input, Apps, Performance, System, Network, Updates, Services }

/// <summary>The categories of the per-user catalog. System, Network, Updates and Services hold machine settings only.</summary>
public static class SettingCategories
{
    public static readonly IReadOnlyList<SettingCategory> PerUser = [SettingCategory.Explorer, SettingCategory.Taskbar, SettingCategory.Privacy, SettingCategory.Search, SettingCategory.Appearance, SettingCategory.Desktop, SettingCategory.Start, SettingCategory.Input, SettingCategory.Apps, SettingCategory.Performance];
}
public enum RiskLevel { Low, Medium, High }
public enum SettingScope { CurrentUser }

/// <summary>How a member of a setting is read and written. Value: one registry value. The two key modes are for settings whose effect is the presence of a key.</summary>
public enum RegistryKeyMode
{
    Value,
    /// <summary>The key itself. Present means it exists. It is only deleted when it holds no value and no subkey.</summary>
    KeyOnly,
    /// <summary>A key whose default value is an empty string. It is only deleted when that default value is all it holds. The value name is empty.</summary>
    KeyWithEmptyDefault,
}

/// <summary>HKCU-relative location. No hive field: cannot express HKLM.</summary>
public sealed record RegistryTarget(string SubKey, string ValueName, RegistryViewKind View = RegistryViewKind.Registry64)
{
    public RegistryKeyMode Mode { get; init; }
    public bool IsKey => Mode != RegistryKeyMode.Value;
}

/// <summary>What a spec does to its member. Set writes Kind and Raw (Kind None removes the value). The bit operations change only the masked bits of a number that is already stored.</summary>
public enum RegistryValueOp { Set, ClearBits, SetBits, KeyPresent, KeyAbsent }

/// <summary>
/// Desired value shape. Kind None (and null Raw) means the value must be absent. Bit operations name the container kind (DWord, or String holding a decimal number)
/// and carry a mask; Raw is null. A grouped setting puts the specs of its further members in <see cref="More"/>.
/// </summary>
public sealed record RegistryValueSpec(RegistryDataKind Kind, byte[]? Raw)
{
    public RegistryValueOp Op { get; init; }
    public uint Mask { get; init; }
    public IReadOnlyList<RegistryValueSpec>? More { get; init; }

    public static RegistryValueSpec DWord(uint value) => new(RegistryDataKind.DWord, BitConverter.GetBytes(value));
    public static RegistryValueSpec Absent { get; } = new(RegistryDataKind.None, null);
    public static RegistryValueSpec ClearBits(RegistryDataKind container, uint mask) => new(container, null) { Op = RegistryValueOp.ClearBits, Mask = mask };
    public static RegistryValueSpec SetBits(RegistryDataKind container, uint mask) => new(container, null) { Op = RegistryValueOp.SetBits, Mask = mask };
    /// <summary>The key exists (KeyOnly members).</summary>
    public static RegistryValueSpec KeyPresent { get; } = new(RegistryDataKind.None, null) { Op = RegistryValueOp.KeyPresent };
    /// <summary>The key exists and its default value is an empty string (KeyWithEmptyDefault members).</summary>
    public static RegistryValueSpec KeyWithEmptyDefault { get; } = new(RegistryDataKind.String, []) { Op = RegistryValueOp.KeyPresent };
    public static RegistryValueSpec KeyAbsent { get; } = new(RegistryDataKind.None, null) { Op = RegistryValueOp.KeyAbsent };

    public bool IsBitOp => Op is RegistryValueOp.ClearBits or RegistryValueOp.SetBits;
    public bool IsKeyOp => Op is RegistryValueOp.KeyPresent or RegistryValueOp.KeyAbsent;

    /// <summary>This spec first, then the specs of the further members.</summary>
    public IReadOnlyList<RegistryValueSpec> Members => More is null ? [this] : [this with { More = null }, .. More];

    /// <summary>A grouped spec: the first member's spec with the others attached.</summary>
    public static RegistryValueSpec Group(params RegistryValueSpec[] members) =>
        members.Length == 1 ? members[0] : members[0] with { More = members.Skip(1).ToList() };
}

public sealed record SettingChoice(string Id, string LabelKey, RegistryValueSpec Value);

/// <summary>Fixed actions that make an applied change visible at once. Each maps to one reviewed action in the shell refresh; none takes a parameter.</summary>
[Flags]
public enum SettingRefresh
{
    None = 0,
    /// <summary>Apply the pointer speed values of the user profile with SPI_SETMOUSE.</summary>
    Mouse = 1,
    /// <summary>Apply the keyboard delay and speed values with SPI_SETKEYBOARDDELAY and SPI_SETKEYBOARDSPEED.</summary>
    KeyboardRepeat = 2,
    /// <summary>Apply the shortcut flag of Sticky, Filter and Toggle keys with the matching SPI calls.</summary>
    AccessibilityKeys = 4,
    /// <summary>WM_SETTINGCHANGE with the string "intl".</summary>
    Intl = 8,
    /// <summary>WM_SETTINGCHANGE with the string "Environment".</summary>
    Environment = 16,
}

/// <summary>
/// Stable, version-pinned catalog row. Ids are original, dotted, lowercase (e.g. explorer.show_file_extensions). A row with <see cref="ExtraTargets"/> is a group:
/// every choice sets all members together, and a state where only some members match a choice is reported as custom.
/// </summary>
public sealed record SettingDefinition(string Id, int Version, string TitleKey, string DescriptionKey, SettingCategory Category,
    RiskLevel Risk, RecoveryKind Recovery, bool RequiresExplorerRestart, bool RequiresSignOut, int? MinBuild, int? MaxBuild,
    RegistryTarget Target, IReadOnlyList<SettingChoice> Choices, string DefaultChoiceId, string? RecommendedChoiceId, string SourceNote)
{
    /// <summary>The further members of a grouped setting, in member order. Member 0 is <see cref="Target"/>.</summary>
    public IReadOnlyList<RegistryTarget> ExtraTargets { get; init; } = [];
    public SettingRefresh Refresh { get; init; }
    public IReadOnlyList<RegistryTarget> Targets => ExtraTargets.Count == 0 ? [Target] : [Target, .. ExtraTargets];
    public bool IsGroup => ExtraTargets.Count > 0;
}

public enum SettingCompatibility { Supported, UnsupportedBuild, Unknown }
/// <summary>Current effective state of one definition. MatchedChoiceId null means the live value matches no authored choice.</summary>
public sealed record SettingState(SettingDefinition Definition, SettingCompatibility Compatibility, string? MatchedChoiceId,
    RegistrySnapshot Actual, long? LastWriteRevision, DiscoveryIssue? Issue);

/// <summary>Binds a preview to the exact state it observed (typed snapshot + key write time, to catch change-away-and-back).</summary>
public sealed record SettingPlan(string SettingId, int DefinitionVersion, string ChoiceId, RegistrySnapshot Observed,
    long? ObservedRevision, bool WillChange, string? CurrentChoiceId, string Token);

public sealed record SettingOutcome(Guid OperationId, string SettingId, string ChoiceId, OperationState State, string Code,
    string? BeforeChoiceId, string? AfterChoiceId, bool NeedsExplorerRestart, bool NeedsSignOut, DateTimeOffset AtUtc, Guid? Group = null, string? Label = null);

/// <summary>Plain-text location of a setting for review screens and the command line. Technical text, never translated.</summary>
public static class SettingLocations
{
    public static string Of(RegistryTarget target) =>
        @"HKCU\" + target.SubKey.Trim('\\') + (target.IsKey ? "" : @"\" + target.ValueName) + (target.View == RegistryViewKind.Registry32 ? " (32-bit)" : "");

    /// <summary>All members, separated by "; ".</summary>
    public static string Of(SettingDefinition definition) => string.Join("; ", definition.Targets.Select(Of));

    /// <summary>The value names of all members, for messages that say what will be written.</summary>
    public static string Names(SettingDefinition definition) => string.Join(", ", definition.Targets.Select(t => t.IsKey ? t.SubKey.Split('\\')[^1] : t.ValueName));

    /// <summary>Technical rendering of what a spec writes, for example "DWORD 1". Never translated.</summary>
    public static string Describe(RegistryValueSpec value) => string.Join(", ", value.Members.Select(DescribeOne));

    private static string DescribeOne(RegistryValueSpec value) => value switch
    {
        { Op: RegistryValueOp.ClearBits } => $"clear bits 0x{value.Mask:X}",
        { Op: RegistryValueOp.SetBits } => $"set bits 0x{value.Mask:X}",
        { Op: RegistryValueOp.KeyPresent } => "key",
        { Op: RegistryValueOp.KeyAbsent } => "-",
        { Kind: RegistryDataKind.DWord, Raw: { Length: 4 } raw } => "DWORD " + BitConverter.ToUInt32(raw, 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        { Kind: RegistryDataKind.String, Raw: { } raw } => "REG_SZ \"" + System.Text.Encoding.Unicode.GetString(raw) + "\"",
        _ => "-",
    };
}
