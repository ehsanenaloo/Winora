// SPDX-License-Identifier: GPL-3.0-only
using Wpf.Ui.Appearance;
using Winora.Desktop.Controls;

namespace Winora.Desktop;

/// <summary>
/// Applies the chosen accent color to the WPF-UI resources. The brushes of the theme dictionary read the accent only when the dictionary loads,
/// so the theme must be applied again after this call.
/// </summary>
public static class AccentApplier
{
    public static void Apply(string? code, string? customHex, ApplicationTheme theme)
    {
        AccentService.IsDarkApplied = theme == ApplicationTheme.Dark;
        if (theme == ApplicationTheme.HighContrast) return;
        if (AccentService.Resolve(code, customHex, theme == ApplicationTheme.Dark) is not { } color) return;
        if (theme != ApplicationTheme.Dark) { ApplicationAccentColorManager.Apply(color.ToColor(), theme, false); return; }
        // The dark color is already the light fill that black text needs. WPF-UI would lighten it again (to near white), so the
        // fill (secondary) is the color itself; primary is a little darker for hover, tertiary a little lighter.
        ApplicationAccentColorManager.Apply(color.ToColor(), AccentSafe.Mix(color, Rgb.Black, 0.1).ToColor(), color.ToColor(), AccentSafe.Mix(color, Rgb.White, 0.15).ToColor());
    }
}
