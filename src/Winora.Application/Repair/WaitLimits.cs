// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>The Settings choice "Wait for slow tools". Long doubles the wait and Very long makes it four times as long, inside the fixed range of each wait.</summary>
public enum SlowToolWait { Normal, Long, VeryLong }

/// <summary>The waits that may be made longer: programs Winora starts and that only end on their own (uninstallers, DISM, SFC and the other repair tools).</summary>
public enum WaitKind
{
    QuietUninstall,
    InteractiveUninstall,
    SystemFileCheck,
    DiskCheck,
    ComponentCheck,
    ComponentScan,
    ComponentRestore,
    FeatureList,
    FeatureChange,
    NetworkReset,
    StoreReset,
}

/// <summary>
/// One wait. <see cref="Usual"/> is how long a normal run takes; after it the page says "Still running". <see cref="Normal"/> is the limit with the Normal choice. A limit is never below
/// <see cref="Minimum"/> and never above <see cref="Maximum"/>, whatever the choice.
/// </summary>
public sealed record WaitLimit(WaitKind Kind, TimeSpan Usual, TimeSpan Normal, TimeSpan Minimum, TimeSpan Maximum);

/// <summary>
/// The time limits of the slow programs. When a limit is reached Winora stops waiting and never ends the program: the run is recorded as "still running" and the result is not known.
/// Short waits that are not in this table (the DNS probe, service start and stop) keep their fixed limit.
/// </summary>
public static class WaitLimits
{
    public const string PreferenceKey = "slow_tool_wait";

    private static TimeSpan M(int minutes) => TimeSpan.FromMinutes(minutes);

    public static IReadOnlyList<WaitLimit> All { get; } =
    [
        new(WaitKind.QuietUninstall, M(5), M(20), M(20), M(60)),
        new(WaitKind.InteractiveUninstall, M(15), M(60), M(60), M(180)),
        new(WaitKind.SystemFileCheck, M(30), M(90), M(90), M(240)),
        new(WaitKind.DiskCheck, M(60), M(90), M(90), M(240)),
        new(WaitKind.ComponentCheck, M(2), M(15), M(15), M(60)),
        new(WaitKind.ComponentScan, M(30), M(60), M(60), M(180)),
        new(WaitKind.ComponentRestore, M(60), M(120), M(120), M(360)),
        new(WaitKind.FeatureList, M(3), M(15), M(15), M(60)),
        new(WaitKind.FeatureChange, M(10), M(60), M(60), M(180)),
        new(WaitKind.NetworkReset, M(1), M(3), M(3), M(10)),
        new(WaitKind.StoreReset, M(3), M(5), M(5), M(15)),
    ];

    public static WaitLimit Of(WaitKind kind) => All.First(l => l.Kind == kind);

    public static int Factor(SlowToolWait wait) => wait switch { SlowToolWait.Long => 2, SlowToolWait.VeryLong => 4, _ => 1 };

    /// <summary>The limit for this choice: the Normal limit times the factor, kept inside the fixed range.</summary>
    public static TimeSpan For(WaitKind kind, SlowToolWait wait)
    {
        var limit = Of(kind);
        var scaled = TimeSpan.FromTicks(limit.Normal.Ticks * Factor(Enum.IsDefined(wait) ? wait : SlowToolWait.Normal));
        return scaled < limit.Minimum ? limit.Minimum : scaled > limit.Maximum ? limit.Maximum : scaled;
    }

    public static WaitKind KindOf(RepairTool tool) => tool switch
    {
        RepairTool.SystemFileCheck => WaitKind.SystemFileCheck,
        RepairTool.DiskCheck => WaitKind.DiskCheck,
        RepairTool.ComponentCheckHealth => WaitKind.ComponentCheck,
        RepairTool.ComponentScanHealth => WaitKind.ComponentScan,
        RepairTool.ComponentRestoreHealth => WaitKind.ComponentRestore,
        RepairTool.FeatureList => WaitKind.FeatureList,
        RepairTool.FeatureEnable or RepairTool.FeatureDisable => WaitKind.FeatureChange,
        RepairTool.StoreCacheReset => WaitKind.StoreReset,
        _ => WaitKind.NetworkReset,
    };

    public static TimeSpan For(RepairTool tool, SlowToolWait wait) => For(KindOf(tool), wait);

    /// <summary>How long a normal run of the tool takes. Past this time the page shows "Still running".</summary>
    public static TimeSpan UsualTime(RepairTool tool) => Of(KindOf(tool)).Usual;

    /// <summary>The stored text of a choice. Anything unknown reads as Normal.</summary>
    public static SlowToolWait Parse(string? text) => text switch { "long" => SlowToolWait.Long, "very_long" => SlowToolWait.VeryLong, _ => SlowToolWait.Normal };

    public static string Code(SlowToolWait wait) => wait switch { SlowToolWait.Long => "long", SlowToolWait.VeryLong => "very_long", _ => "normal" };
}
