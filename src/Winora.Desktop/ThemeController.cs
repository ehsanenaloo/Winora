// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Winora.Desktop;

/// <summary>
/// Puts the theme of the window on screen: light, dark or the system-color dictionary of high contrast, the backdrop, the overlay of Winora's tokens and the accent color. Which theme it is
/// comes from <see cref="ThemeRules"/>; this class only applies it.
/// </summary>
public sealed class ThemeController(FluentWindow window, ShellViewModel model)
{
    private bool watchingSystemTheme;
    private bool reapplyingAccent;

    /// <summary>Test seam: forces the high-contrast path without changing the real Windows setting. Null follows Windows.</summary>
    public bool? HighContrastOverride { get; set; }

    /// <summary>Stops following the theme of Windows (the window is closing).</summary>
    public void Stop()
    {
        if (watchingSystemTheme) SystemThemeWatcher.UnWatch(window);
        watchingSystemTheme = false;
    }

    // The system-theme watcher switches WPF-UI between light and dark by itself; keep Winora's token overlay in step with it.
    public void OnApplicationThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        ThemeOverlay.Apply(theme == ApplicationTheme.Dark, ThemeOverlay.IsHighContrast);
        // The watcher writes the Windows accent when Windows changes theme; a chosen accent is written again on top of it.
        if (model.Settings.Accent != AccentService.System && !ThemeOverlay.IsHighContrast && !reapplyingAccent)
        {
            reapplyingAccent = true;
            try
            {
                AccentApplier.Apply(model.Settings.Accent, model.Settings.AccentHexValue, theme);
                ApplicationThemeManager.Apply(theme, window.WindowBackdropType, false);
            }
            finally { reapplyingAccent = false; }
        }
    }

    /// <summary>Light, dark or system theme; high contrast always switches to the system-color dictionary.</summary>
    public void Apply()
    {
        if (watchingSystemTheme) { SystemThemeWatcher.UnWatch(window); watchingSystemTheme = false; }
        var highContrast = HighContrastOverride ?? SystemParameters.HighContrast;
        var backdrop = highContrast ? WindowBackdropType.None : WindowBackdropType.Mica;
        window.WindowBackdropType = backdrop;
        var theme = ThemeRules.Resolve(model.Theme, SystemThemeManager.GetCachedSystemTheme() == SystemTheme.Dark, highContrast) switch
        {
            EffectiveTheme.HighContrast => ApplicationTheme.HighContrast,
            EffectiveTheme.Dark => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light,
        };
        // The chosen accent goes in before the theme dictionary loads: its brushes (switches, checks) read the accent only then.
        // Applied before first layout too: WPF-UI templates need the accent resources it creates.
        AccentApplier.Apply(model.Settings.Accent, model.Settings.AccentHexValue, theme);
        ApplicationThemeManager.Apply(theme, backdrop, model.Settings.Accent == AccentService.System);
        ThemeOverlay.Apply(theme == ApplicationTheme.Dark, highContrast);
        if (!highContrast && model.Theme == "System" && window.IsLoaded) { SystemThemeWatcher.Watch(window, backdrop, true); watchingSystemTheme = true; }
        // Window brushes are theme resources, so they follow the dictionary that was just applied.
        window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
        window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorPrimaryBrush");
    }
}
