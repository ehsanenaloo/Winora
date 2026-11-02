// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>A color as three bytes. The accent color setting works with this; Winora.Desktop turns it into a WPF color or brush.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb White { get; } = new(255, 255, 255);
    public static Rgb Black { get; } = new(0, 0, 0);
    public static Rgb Gray { get; } = new(128, 128, 128);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
