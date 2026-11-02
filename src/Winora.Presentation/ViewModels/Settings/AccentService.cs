// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;

namespace Winora.Desktop;

/// <summary>One choice of the accent color setting. The preview color is the light-theme color; the dark theme uses a lighter variant.</summary>
public sealed class AccentOption(string code, string labelKey, Rgb preview, LocalizationService text) : ChoiceOption(code, labelKey, text)
{
    public Rgb Preview { get; private set; } = preview;
    /// <summary>The check mark on the swatch is white or black, whichever reads better on this color (a light accent swatch would lose a white mark).</summary>
    public bool MarkIsLight { get; private set; } = AccentSafe.WhiteReadsBetter(preview);
    public void SetPreview(Rgb color) { Preview = color; MarkIsLight = AccentSafe.WhiteReadsBetter(color); OnPropertyChanged(nameof(Preview)); OnPropertyChanged(nameof(MarkIsLight)); }
}

/// <summary>
/// The accent color of the application: the Windows accent, one of five fixed colors, or a color the user typed. Every color is checked against
/// the text that sits on it (4.5:1) and adjusted when it falls short. The preference is only a code and a hex string, never a path.
/// </summary>
public static class AccentService
{
    public const string System = "System";
    public const string Custom = "Custom";
    public const string PreferenceKey = "accent";
    public const string HexPreferenceKey = "accent.hex";
    public const string DefaultCustomHex = "#0E7490";

    /// <summary>Set by the window after each theme apply so the settings page can judge the custom color for the theme in use.</summary>
    public static bool IsDarkApplied { get; set; }

    /// <summary>Code, resource key and the light and dark colors. System has none: Windows decides.</summary>
    public static readonly IReadOnlyList<(string Code, string LabelKey, Rgb Light, Rgb Dark)> Palette =
    [
        ("Teal", "AccentTeal", new(0x00, 0x73, 0x6B), new(0x4F, 0xD1, 0xC5)),
        ("Green", "AccentGreen", new(0x10, 0x7C, 0x10), new(0x6C, 0xCB, 0x5F)),
        ("Purple", "AccentPurple", new(0x7A, 0x3E, 0xB5), new(0xC4, 0xA1, 0xFF)),
        ("Orange", "AccentOrange", new(0xB5, 0x48, 0x00), new(0xFF, 0xA4, 0x5C)),
        ("Rose", "AccentRose", new(0xC1, 0x27, 0x5F), new(0xFF, 0x8C, 0xB0)),
    ];

    public static bool IsKnown(string? code) => code == System || code == Custom || Palette.Any(p => p.Code == code);

    public static string Normalize(string? code) => IsKnown(code) ? code! : System;

    /// <summary>Accepts #RGB-free input of the form #RRGGBB (the # is optional). Anything else is rejected.</summary>
    public static bool TryParseHex(string? text, out Rgb color)
    {
        color = default;
        if (text is null) return false;
        var value = text.Trim().TrimStart('#');
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string ToHex(Rgb c) => c.ToString();

    /// <summary>The color to apply, or null for the Windows accent. A custom color that cannot be read falls back to the default custom color.</summary>
    public static Rgb? Resolve(string? code, string? customHex, bool dark)
    {
        code = Normalize(code);
        if (code == System) return null;
        if (code == Custom)
        {
            var parsed = TryParseHex(customHex, out var c) ? c : TryParseHex(DefaultCustomHex, out var d) ? d : AccentSafe.LightFallback;
            return AccentSafe.Adjust(parsed, dark);
        }
        var entry = Palette.First(p => p.Code == code);
        return dark ? entry.Dark : entry.Light;
    }

    /// <summary>True when the typed color had to be changed so that text on it stays readable.</summary>
    public static bool WasAdjusted(string? customHex, bool dark) =>
        TryParseHex(customHex, out var c) && AccentSafe.Adjust(c, dark) != c;
}
