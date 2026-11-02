// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>Which two-choice settings are the two sides of one switch, and which side is On.</summary>
public static class ChoiceSwitch
{
    private static readonly string[] OnWords = ["on", "enabled", "enable", "show", "shown", "allow", "allowed"];
    private static readonly string[] OffWords = ["off", "disabled", "disable", "hide", "hidden", "block", "blocked", "deny"];

    /// <summary>
    /// A setting with two choices that are the two sides of one switch (on and off, show and hide, allowed and blocked, or Windows default against one of them) is shown as a switch. "Windows default"
    /// counts as the side the other choice is not. Any other pair, such as Windows default against a value ("20 percent" against "10 percent"), stays a choice control, because a switch
    /// needs to say which side is On.
    /// </summary>
    public static (RowChoice? On, RowChoice? Off) Pair(IReadOnlyList<RowChoice> list)
    {
        if (list.Count != 2) return (null, null);
        if (list[0].IsOn is { } first && list[1].IsOn is { } second && first != second) return first ? (list[0], list[1]) : (list[1], list[0]);
        int Kind(RowChoice c) => OnWords.Contains(c.Id) ? 1 : OffWords.Contains(c.Id) ? 2 : c.Id == "default" ? 3 : 0;
        var (a, b) = (Kind(list[0]), Kind(list[1]));
        if (a == 1 && b == 2) return (list[0], list[1]);
        if (a == 2 && b == 1) return (list[1], list[0]);
        if (a == 3 && b == 2) return (list[0], list[1]);
        if (a == 2 && b == 3) return (list[1], list[0]);
        if (a == 3 && b == 1) return (list[1], list[0]);
        if (a == 1 && b == 3) return (list[0], list[1]);
        // A setting kept as it is against the same setting turned off (Manual against Disabled, Required data only against Windows default): the kept one is On.
        if (a == 0 && b == 2) return (list[0], list[1]);
        if (a == 2 && b == 0) return (list[1], list[0]);
        return (null, null);
    }
}
