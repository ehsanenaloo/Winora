// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>Which look the window takes. Winora.Desktop maps it to the theme of the UI toolkit.</summary>
public enum EffectiveTheme { Light, Dark, HighContrast }

/// <summary>The theme the window shows: high contrast beats everything, then the choice of the person, then the theme of Windows.</summary>
public static class ThemeRules
{
    public static EffectiveTheme Resolve(string preference, bool systemIsDark, bool highContrast) =>
        highContrast ? EffectiveTheme.HighContrast
        : preference == "Dark" ? EffectiveTheme.Dark
        : preference == "Light" ? EffectiveTheme.Light
        : systemIsDark ? EffectiveTheme.Dark : EffectiveTheme.Light;
}

/// <summary>How the navigation pane looks at a given width.</summary>
/// <param name="Minimal">Below the minimum width the pane is collapsed behind the toggle.</param>
/// <param name="Open">Whether the pane is open with labels; null when the pane is minimal and keeps whatever state it has.</param>
public readonly record struct PaneLayout(bool Minimal, bool? Open);

/// <summary>The rules of the navigation pane and its sections.</summary>
public static class NavigationRules
{
    public const double MinimalBelow = 820, OpenFrom = 1100;

    /// <summary>Text scaling shrinks the room in device units: the pane is judged by the width the scaled content really has.</summary>
    public static double EffectiveWidth(double actualWidth, double contentScale) => actualWidth / contentScale;

    /// <summary>Wide: open pane with labels. Narrower: compact icon-only pane. Below the minimum width: collapsed behind the toggle.</summary>
    public static PaneLayout PaneFor(double effectiveWidth) => effectiveWidth < MinimalBelow ? new(true, null) : new(false, effectiveWidth >= OpenFrom);

    /// <summary>A closed pane shows icons only and has no section toggles, so every item stays reachable there.</summary>
    public static bool SectionsCollapsible(bool minimal, bool paneOpen) => minimal || paneOpen;

    /// <summary>The items of a section show when it is open, or when sections cannot be closed.</summary>
    public static bool ItemVisible(bool sectionOpen, bool collapsible) => sectionOpen || !collapsible;

    /// <summary>The line between sections shows only in the icon-only pane, and not above the first section.</summary>
    public static bool DividerVisible(bool collapsible, NavSection section) => !collapsible && section != NavSection.Home;

    public static IconKey ChevronFor(bool sectionOpen, TextDirection direction) =>
        sectionOpen ? IconKey.ChevronDown12 : direction == TextDirection.RightToLeft ? IconKey.ChevronLeft12 : IconKey.ChevronRight12;

    /// <summary>The name of a navigation item that carries a mark (a run, a failure or staged changes), for assistive technology. Null when the item has no mark and keeps its plain name.</summary>
    public static string? MarkedName(LocalizationService text, string key, NavMark mark, int pending)
    {
        var name = text[key];
        if (mark == NavMark.Running) return $"{name}, {text["ACNavRunning"]}";
        if (mark == NavMark.Failed) return $"{name}, {text["ACNavFailed"]}";
        return pending > 0 ? $"{name}, {text.Format("NavPendingFormat", pending)}" : null;
    }
}

/// <summary>The areas F6 moves between.</summary>
public static class RegionRules
{
    public const string Navigation = "navigation", Content = "content", Toast = "toast", ActionCenter = "actioncenter";

    /// <summary>The areas in order: navigation, page, notification (while a toast shows), Action center (while it is open).</summary>
    public static List<string> Regions(bool toastShown, bool actionCenterOpen)
    {
        var list = new List<string> { Navigation, Content };
        if (toastShown) list.Add(Toast);
        if (actionCenterOpen) list.Add(ActionCenter);
        return list;
    }

    /// <summary>The area F6 (step 1) or Shift+F6 (step -1) moves to. An area that is not in the list counts as the page.</summary>
    public static string Next(IReadOnlyList<string> regions, string current, int step)
    {
        var index = regions.ToList().IndexOf(current);
        if (index < 0) index = 1;
        return regions[(index + step + regions.Count) % regions.Count];
    }
}

/// <summary>When the window asks before it closes, and how a dialog behaves when it is asked for in a hurry.</summary>
public static class DialogRules
{
    /// <summary>The window asks once before it closes with staged changes. It does not ask while a dialog is open, after the person agreed, or when nothing is staged.</summary>
    public static bool AskBeforeClose(bool askEnabled, bool alreadyConfirmed, bool modalOpen, int pending) => askEnabled && !alreadyConfirmed && !modalOpen && pending > 0;

    /// <summary>A dialog never grows past the window, so a long review or long details keep one scroll bar inside the window.</summary>
    public static double MaxHeight(double windowHeight, double contentScale) => Math.Max(320, windowHeight / contentScale - 96);

    public const int MaxAttempts = 5;

    /// <summary>
    /// A dialog that is asked for right after another one closed can find the host still closing and come back at once without being shown. That answer is asked again
    /// after a short wait, so it is never taken for a choice of the person. A dialog that opened was answered by a person.
    /// </summary>
    public static bool ShouldAskAgain(bool answeredNone, bool opened, int attempt) => answeredNone && !opened && attempt < MaxAttempts - 1;
}
