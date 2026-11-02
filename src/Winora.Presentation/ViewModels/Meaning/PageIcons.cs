// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>The icon of every page: the navigation item and the tile in the page header show the same symbol.</summary>
public static class PageIcons
{
    private static readonly Dictionary<string, IconKey> Map = new()
    {
        ["Overview"] = IconKey.Home24,
        ["Profiles"] = IconKey.Sparkle24,
        ["Customize"] = IconKey.PaintBrush24,
        ["Optimize"] = IconKey.Flash24,
        ["Power"] = IconKey.PlugConnected24,
        ["Network"] = IconKey.Globe24,
        ["Permissions"] = IconKey.ShieldLock24,
        ["Updates"] = IconKey.ArrowSync24,
        ["Cleanup"] = IconKey.Broom24,
        ["Bloatware"] = IconKey.Delete24,
        ["Startup"] = IconKey.Rocket24,
        ["ScheduledTasks"] = IconKey.CalendarClock24,
        ["Services"] = IconKey.Wrench24,
        ["Applications"] = IconKey.Apps24,
        ["Tools"] = IconKey.Toolbox24,
        ["History"] = IconKey.History24,
        ["Settings"] = IconKey.Settings24,
    };

    /// <summary>The icon for a page key; pages that are tabs of another page (Disk usage, Free space, Quarantine) take the icon of their parent.</summary>
    public static IconKey For(string? key) => key is not null && Map.TryGetValue(key, out var symbol) ? symbol : key switch
    {
        "DiskUsage" or "FreeSpace" or "Quarantine" => IconKey.Broom24,
        _ => IconKey.None,
    };

    public static IReadOnlyDictionary<string, IconKey> All => Map;
}
