// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop.Search;

/// <summary>Turns the catalogs of the app into search seeds. Nothing here reads the machine.</summary>
public static class SearchSeeds
{
    public static IEnumerable<SearchSeed> Pages(IEnumerable<DestinationInfo> destinations) =>
        destinations.Where(d => d.IsAvailable).Select(d => new SearchSeed("page:" + d.Key, SearchGroup.Pages, d.Key, null, d.Key, null, d.SectionKey is null ? [] : [d.SectionKey], d.Icon));

    public static IEnumerable<SearchSeed> UserSettings(IEnumerable<SettingDefinition> definitions) =>
        definitions.Select(d => new SearchSeed("setting:" + d.Id, SearchGroup.Settings, d.TitleKey, d.DescriptionKey, "Customize", d.Id,
            ["Customize", LocalizationService.EnumKey(d.Category)], IconKey.PaintBrush24, d.Risk));

    public static IEnumerable<SearchSeed> MachineSettings(IEnumerable<MachineSettingDefinition> definitions) =>
        definitions.Select(d => new SearchSeed("machine:" + d.Id, SearchGroup.Settings, d.TitleKey, d.DescriptionKey, "Optimize", d.Id,
            ["Optimize", LocalizationService.EnumKey(d.Category)], IconKey.Flash24, d.Risk, d.SourceClass));

    public static IEnumerable<SearchSeed> Tools() =>
        RepairTools.All.Select(t => new SearchSeed("tool:" + t.Id, SearchGroup.Actions, $"Tools_{t.Id}_Title", $"Tools_{t.Id}_Desc", "Tools", t.Id, ["Tools"], IconKey.Toolbox24,
            t.Risk == RepairRisk.Low ? RiskLevel.Low : RiskLevel.Medium));

    public static IEnumerable<SearchSeed> Profiles(IEnumerable<Profile> profiles) =>
        profiles.Where(p => p.NameKey is not null).Select(p => new SearchSeed("profile:" + p.Id, SearchGroup.Actions, p.NameKey!, p.DescriptionKey, "Profiles", p.Id, ["Profiles"], IconKey.Sparkle24));

    /// <summary>Actions that only open a page. The search never changes anything.</summary>
    public static IEnumerable<SearchSeed> Actions() =>
    [
        new("action:scan", SearchGroup.Actions, "SearchActionScan", "SearchActionScanDesc", "Overview", null, ["Overview"], IconKey.ArrowClockwise24),
        new("action:history", SearchGroup.Actions, "SearchActionHistory", "SearchActionHistoryDesc", "History", null, ["History"], IconKey.History24),
        new("action:profile", SearchGroup.Actions, "SearchActionProfile", "SearchActionProfileDesc", "Profiles", null, ["Profiles"], IconKey.Sparkle24),
    ];
}
