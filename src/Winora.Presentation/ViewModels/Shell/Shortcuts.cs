// SPDX-License-Identifier: GPL-3.0-only

namespace Winora.Desktop;

/// <summary>One documented keyboard shortcut. <see cref="Display"/> is technical text (never translated); the description is a resource.</summary>
public sealed record ShortcutInfo(string Id, string Display, string DescriptionKey, bool IsGlobal, string? RtlDisplay = null)
{
    /// <summary>The keys as shown on a page that reads in this direction.</summary>
    public string DisplayFor(TextDirection direction) => direction == TextDirection.RightToLeft && RtlDisplay is not null ? RtlDisplay : Display;
}

/// <summary>
/// The documented keyboard set, in one place: the F1 dialog lists exactly this, and the window handles exactly the global ones.
/// Non-global rows describe behavior of controls (Escape, Enter, Tab) that is covered by their own tests.
/// </summary>
public static class Shortcuts
{
    public const string Find = "find", Refresh = "refresh", Back = "back", Forward = "forward", Settings = "settings", Help = "help", Search = "search", Undo = "undo", Sections = "sections", Confirm = "confirm", ActionCenter = "actioncenter", Regions = "regions", RegionsBack = "regionsBack";

    public static IReadOnlyList<ShortcutInfo> All { get; } =
    [
        new(Find, "Ctrl+F", "ShortcutFind", true),
        new(Refresh, "F5", "ShortcutRefresh", true),
        new(Back, "Alt+Left", "ShortcutBack", true, "Alt+Right"),
        new(Forward, "Alt+Right", "ShortcutForward", true, "Alt+Left"),
        new(Settings, "Ctrl+,", "ShortcutSettings", true),
        new(Help, "F1", "ShortcutHelp", true),
        new(Search, "Ctrl+K", "ShortcutSearch", true),
        new(Undo, "Ctrl+Z", "ShortcutUndo", true),
        new(Sections, "Ctrl+1 - Ctrl+5", "ShortcutSections", true),
        new(Confirm, "Ctrl+Enter", "ShortcutConfirm", true),
        new(ActionCenter, "Ctrl+Shift+A", "ShortcutActionCenter", true),
        new(Regions, "F6", "ShortcutRegions", true),
        new(RegionsBack, "Shift+F6", "ShortcutRegionsBack", true),
        new("escape", "Esc", "ShortcutEscape", false),
        new("enter", "Enter", "ShortcutEnter", false),
        new("tab", "Tab", "ShortcutTab", false),
    ];
}

/// <summary>Binding model of the F1 dialog.</summary>
public sealed class ShortcutsModel(LocalizationService text) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Never raised. It lets WPF bindings listen through a weak event; on a plain object they hold the object, and with it the text service, for the life of the process.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public LocalizationService Text { get; } = text;
    public IReadOnlyList<ShortcutRow> Rows { get; } = Shortcuts.All.Select(s => new ShortcutRow(s.DisplayFor(text.Direction), text[s.DescriptionKey])).ToList();
}

public sealed record ShortcutRow(string Keys, string Description)
{
    public string AutomationName => Keys + ", " + Description;
}
