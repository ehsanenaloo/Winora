// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public enum PowerSource { Ac, Dc }

public enum PowerSettingKind { Seconds, Choice, Percent }

/// <summary>One allowed value of a choice setting. The label comes from the resource key.</summary>
public sealed record PowerOption(uint Value, string LabelKey);

/// <summary>A power plan setting Winora may change: its two GUIDs, the kind of value and the values it offers. Titles and descriptions come from the keys Power_{Id}_Title and Power_{Id}_Description.</summary>
public sealed record PowerSettingDefinition(string Id, Guid Subgroup, Guid Setting, PowerSettingKind Kind, IReadOnlyList<PowerOption> Options, uint MaxValue, string Basis, bool CostsBattery = false)
{
    public bool IsAllowed(uint value) => Kind switch
    {
        PowerSettingKind.Choice => Options.Any(o => o.Value == value),
        PowerSettingKind.Percent => value <= 100,
        _ => value <= PowerSettings.MaxSeconds,
    };
}

/// <summary>
/// The settings the Power page can change. The GUIDs are the documented power setting GUIDs of the Windows power policy API and are read back with powercfg on
/// Windows 11; a setting that a PC does not have (a lid on a desktop) is shown as not available.
/// </summary>
public static class PowerSettings
{
    public const uint MaxSeconds = 7 * 24 * 3600;
    public static readonly IReadOnlyList<uint> TimeoutPresets = [0, 60, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800, 18000];
    public static readonly IReadOnlyList<uint> PercentPresets = [5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    private static readonly Guid Video = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid Sleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid Disk = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    private static readonly Guid Buttons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid Processor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid Usb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid PciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");

    private static IReadOnlyList<PowerOption> Presets(IEnumerable<uint> values) => values.Select(v => new PowerOption(v, "")).ToList();
    private static IReadOnlyList<PowerOption> Choices(string prefix, uint count) => Enumerable.Range(0, (int)count).Select(i => new PowerOption((uint)i, $"{prefix}_{i}")).ToList();
    private static IReadOnlyList<PowerOption> Actions(bool displayOff) =>
        new[] { new PowerOption(0, "PowerAction_None"), new PowerOption(1, "PowerAction_Sleep"), new PowerOption(2, "PowerAction_Hibernate"), new PowerOption(3, "PowerAction_Shutdown") }
        .Concat(displayOff ? [new PowerOption(4, "PowerAction_DisplayOff")] : []).ToList();

    public static readonly IReadOnlyList<PowerSettingDefinition> All =
    [
        new("display_off", Video, new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), PowerSettingKind.Seconds, Presets(TimeoutPresets), MaxSeconds, "powercfg alias VIDEOIDLE, Microsoft power setting GUIDs"),
        new("sleep", Sleep, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), PowerSettingKind.Seconds, Presets(TimeoutPresets), MaxSeconds, "powercfg alias STANDBYIDLE, Microsoft power setting GUIDs"),
        new("hibernate", Sleep, new("9d7815a6-7ee4-497e-8888-515a05f02364"), PowerSettingKind.Seconds, Presets(TimeoutPresets), MaxSeconds, "powercfg alias HIBERNATEIDLE, Microsoft power setting GUIDs"),
        new("disk_off", Disk, new("6738e2c4-e8a5-4a42-b16a-e040e769756e"), PowerSettingKind.Seconds, Presets(TimeoutPresets), MaxSeconds, "powercfg alias DISKIDLE, Microsoft power setting GUIDs", true),
        new("lid_close", Buttons, new("5ca83367-6e45-459f-a27b-476b1d01c936"), PowerSettingKind.Choice, Actions(false), 3, "powercfg alias LIDACTION, Microsoft power setting GUIDs"),
        new("power_button", Buttons, new("7648efa3-dd9c-4e3e-b566-50f929386280"), PowerSettingKind.Choice, Actions(true), 4, "powercfg alias PBUTTONACTION, Microsoft power setting GUIDs"),
        new("cpu_max", Processor, new("bc5038f7-23e0-4960-96da-33abaf5935ec"), PowerSettingKind.Percent, Presets(PercentPresets), 100, "powercfg alias PROCTHROTTLEMAX, Microsoft power setting GUIDs", true),
        new("cpu_min", Processor, new("893dee8e-2bef-41e0-89c6-b55d0929964c"), PowerSettingKind.Percent, Presets(PercentPresets), 100, "powercfg alias PROCTHROTTLEMIN, Microsoft power setting GUIDs", true),
        new("boost_mode", Processor, new("be337238-0d82-4146-a960-4f3749d470c7"), PowerSettingKind.Choice, Choices("PowerOpt_Boost", 7), 6, "powercfg alias PERFBOOSTMODE, values 0 to 6 read with powercfg -qh on Windows 11", true),
        new("pcie_aspm", PciExpress, new("ee12f906-d277-404b-b6da-e5fa1a576df5"), PowerSettingKind.Choice, Choices("PowerOpt_Aspm", 3), 2, "Microsoft Learn: PCI Express Link State Power Management (alias ASPM), values 0 to 2", true),
        new("usb_suspend", Usb, new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"), PowerSettingKind.Choice, Choices("PowerOpt_Usb", 2), 1, "USB selective suspend setting, values 0 and 1 read with powercfg -qh on Windows 11", true),
        new("wake_timers", Sleep, new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d"), PowerSettingKind.Choice, Choices("PowerOpt_Wake", 3), 2, "powercfg alias RTCWAKE, values 0 to 2 read with powercfg -qh on Windows 11"),
        new("sleep_unattended", Sleep, new("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0"), PowerSettingKind.Seconds, Presets(TimeoutPresets), MaxSeconds, "powercfg alias UNATTENDSLEEP, read with powercfg -qh on Windows 11"),
    ];

    public static PowerSettingDefinition? Find(string id) => All.FirstOrDefault(d => d.Id == id);
}

public sealed record PowerProfileEntry(string SettingId, PowerSource Source, uint Value);

/// <summary>A named set of setting values. Titles come from the keys PowerProfile_{Id}_Title and PowerProfile_{Id}_Description.</summary>
public sealed record PowerProfile(string Id, IReadOnlyList<PowerProfileEntry> Entries, bool RaisesPower);

/// <summary>
/// The profiles of the Power page. The values are Winora's own choice inside the ranges that Windows reports for each setting. The Performance profile writes mains
/// values only: no profile raises power use on battery.
/// </summary>
public static class PowerProfiles
{
    private static IEnumerable<PowerProfileEntry> Both(string id, uint value) => [new(id, PowerSource.Ac, value), new(id, PowerSource.Dc, value)];

    public static readonly IReadOnlyList<PowerProfile> All =
    [
        new("balanced", [.. Both("cpu_min", 5), .. Both("cpu_max", 100), .. Both("boost_mode", 2), .. Both("pcie_aspm", 2), .. Both("usb_suspend", 1)], false),
        new("performance",
        [
            new("cpu_min", PowerSource.Ac, 50), new("cpu_max", PowerSource.Ac, 100), new("boost_mode", PowerSource.Ac, 2),
            new("pcie_aspm", PowerSource.Ac, 0), new("usb_suspend", PowerSource.Ac, 0), new("disk_off", PowerSource.Ac, 0),
        ], true),
        new("battery_saver",
        [
            new("cpu_min", PowerSource.Dc, 5), new("cpu_max", PowerSource.Dc, 70), new("boost_mode", PowerSource.Dc, 0),
            new("pcie_aspm", PowerSource.Dc, 2), new("usb_suspend", PowerSource.Dc, 1), new("disk_off", PowerSource.Dc, 300),
            new("display_off", PowerSource.Dc, 120), new("sleep", PowerSource.Dc, 600), new("wake_timers", PowerSource.Dc, 0),
        ], false),
    ];

    public static PowerProfile? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>Fixed identifiers of the plan that Windows ships hidden on some editions.</summary>
public static class PowerPlanCatalog
{
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
}

/// <summary>Whole-system power facts and the hibernation file. Writing needs administrator rights.</summary>
public interface IPowerSystem
{
    /// <summary>True when the PC reports a battery. False for a desktop and when it is not known.</summary>
    bool HasBattery();
    /// <summary>Whether the hibernation file exists, or null when it cannot be read.</summary>
    bool? HibernationEnabled();
    PowerResult SetHibernation(bool enabled);
}

public sealed record PowerPlanRef(Guid Id, string Name, bool IsActive, bool IsBuiltIn);

public sealed record PowerSnapshot(IReadOnlyList<PowerPlanRef> Plans);

/// <summary>The result of one call to the power API. <see cref="Code"/> is a stable code: ok, power.unsupported, power.access_denied, power.failed.</summary>
public sealed record PowerResult(bool Ok, string Code)
{
    public static PowerResult Success { get; } = new(true, "ok");
    public static PowerResult Fail(string code) => new(false, code);
}

/// <summary>The power plan API. Reads and writes work on the machine-wide plans (administrator rights are needed to write).</summary>
public interface IPowerPlans
{
    PowerSnapshot Read(CancellationToken cancellationToken);
    /// <summary>The value of a setting, or null when this PC does not have it.</summary>
    uint? ReadValue(Guid plan, PowerSettingDefinition setting, PowerSource source);
    /// <param name="expectedBefore">The value that was reviewed; another value at the moment of the write is refused as stale.</param>
    PowerResult WriteValue(Guid plan, PowerSettingDefinition setting, PowerSource source, uint value, uint? expectedBefore = null);
    PowerResult SetActive(Guid plan);
    PowerResult Duplicate(Guid source, string name, out Guid created);
    PowerResult Delete(Guid plan);
}
