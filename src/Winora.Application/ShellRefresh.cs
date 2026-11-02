// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>
/// Fixed, parameterless shell actions that make a registry change take effect immediately. Not a generic process or command endpoint:
/// the kinds are a closed enum and implementations may only perform exactly these actions.
/// </summary>
public enum ShellRefreshKind
{
    /// <summary>Broadcast WM_SETTINGCHANGE (fixed strings) so running apps re-read user settings.</summary>
    BroadcastSettingChange,
    /// <summary>Gracefully restart the user's Explorer shell and verify the taskbar returns. Open File Explorer windows close.</summary>
    RestartExplorer,
    // The kinds below are owner approval pending (DEC-IW-034). Each applies values that the registry already holds; none takes a parameter.
    /// <summary>SPI_SETMOUSE with the pointer values stored in the user profile.</summary>
    ApplyMouse,
    /// <summary>SPI_SETKEYBOARDDELAY and SPI_SETKEYBOARDSPEED with the values stored in the user profile.</summary>
    ApplyKeyboardRepeat,
    /// <summary>SPI_SETSTICKYKEYS, SPI_SETFILTERKEYS and SPI_SETTOGGLEKEYS: only the shortcut flag is taken from the user profile.</summary>
    ApplyAccessibilityKeys,
    /// <summary>WM_SETTINGCHANGE with the string "intl".</summary>
    BroadcastIntl,
    /// <summary>WM_SETTINGCHANGE with the string "Environment".</summary>
    BroadcastEnvironment,
}

public sealed record ShellRefreshResult(bool Succeeded, string Code);

public interface IShellRefresh
{
    Task<ShellRefreshResult> RefreshAsync(ShellRefreshKind kind, CancellationToken cancellationToken);
}
