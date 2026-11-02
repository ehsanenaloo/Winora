// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>
/// Keeps text on an accent-colored control readable. In the light theme the accent is darkened in 5% steps until white text reaches 4.5:1.
/// In the dark theme it is lightened until black text reaches 4.5:1. A color that never gets there falls back to a fixed accent.
/// </summary>
public static class AccentSafe
{
    public const double MinimumRatio = 4.5;
    public static readonly Rgb LightFallback = new(0x00, 0x5F, 0xB8);
    public static readonly Rgb DarkFallback = new(0x60, 0xCD, 0xFF);

    public static Rgb Adjust(Rgb accent, bool dark)
    {
        var text = dark ? Rgb.Black : Rgb.White;
        var color = accent;
        for (var step = 0; step <= 20; step++)
        {
            if (Ratio(color, text) >= MinimumRatio) return color;
            color = dark ? Mix(color, Rgb.White, 0.05) : Mix(color, Rgb.Black, 0.05);
        }
        return dark ? DarkFallback : LightFallback;
    }

    public static double Ratio(Rgb a, Rgb b)
    {
        var l1 = Luminance(a); var l2 = Luminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    /// <summary>True when white reads better than black on this color (the check mark on a swatch).</summary>
    public static bool WhiteReadsBetter(Rgb color)
    {
        var l = Luminance(color);
        return 1.05 / (l + 0.05) >= (l + 0.05) / 0.05;
    }

    public static Rgb Mix(Rgb from, Rgb to, double amount) => new(
        (byte)Math.Round(from.R + (to.R - from.R) * amount), (byte)Math.Round(from.G + (to.G - from.G) * amount), (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static double Luminance(Rgb c)
    {
        static double Lin(double v) { v /= 255d; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
