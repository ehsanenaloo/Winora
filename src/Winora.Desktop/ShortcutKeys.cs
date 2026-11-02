// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Input;

namespace Winora.Desktop;

/// <summary>Maps a key press to the id of a documented shortcut (<see cref="Shortcuts"/>).</summary>
public static class ShortcutKeys
{
    /// <summary>The id of the global shortcut a key press triggers, or null. Back and Forward follow the reading direction: Back is Alt+Left in left-to-right and Alt+Right in right-to-left; Forward is the other arrow.</summary>
    public static string? Match(Key key, Key systemKey, ModifierKeys modifiers, TextDirection direction = TextDirection.LeftToRight)
    {
        var actual = key == Key.System ? systemKey : key;
        return (actual, modifiers) switch
        {
            (Key.F, ModifierKeys.Control) => Shortcuts.Find,
            (Key.F5, ModifierKeys.None) => Shortcuts.Refresh,
            (Key.Left, ModifierKeys.Alt) when direction == TextDirection.LeftToRight => Shortcuts.Back,
            (Key.Right, ModifierKeys.Alt) when direction == TextDirection.RightToLeft => Shortcuts.Back,
            (Key.Right, ModifierKeys.Alt) when direction == TextDirection.LeftToRight => Shortcuts.Forward,
            (Key.Left, ModifierKeys.Alt) when direction == TextDirection.RightToLeft => Shortcuts.Forward,
            (Key.OemComma, ModifierKeys.Control) => Shortcuts.Settings,
            (Key.F1, ModifierKeys.None) => Shortcuts.Help,
            (Key.K, ModifierKeys.Control) => Shortcuts.Search,
            (Key.Z, ModifierKeys.Control) => Shortcuts.Undo,
            (>= Key.D1 and <= Key.D5, ModifierKeys.Control) => Shortcuts.Sections + ":" + (actual - Key.D1),
            (>= Key.NumPad1 and <= Key.NumPad5, ModifierKeys.Control) => Shortcuts.Sections + ":" + (actual - Key.NumPad1),
            (Key.Enter, ModifierKeys.Control) => Shortcuts.Confirm,
            (Key.A, ModifierKeys.Control | ModifierKeys.Shift) => Shortcuts.ActionCenter,
            (Key.F6, ModifierKeys.None) => Shortcuts.Regions,
            (Key.F6, ModifierKeys.Shift) => Shortcuts.RegionsBack,
            _ => null,
        };
    }
}
