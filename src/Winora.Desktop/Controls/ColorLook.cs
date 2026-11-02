// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>The one mapping from a plain color (<see cref="Rgb"/>) of the view models to WPF colors and brushes.</summary>
public static class ColorLook
{
    public static Color ToColor(this Rgb rgb) => Color.FromRgb(rgb.R, rgb.G, rgb.B);

    public static Rgb ToRgb(this Color color) => new(color.R, color.G, color.B);

    public static Brush BrushOf(Rgb rgb)
    {
        var brush = new SolidColorBrush(rgb.ToColor());
        brush.Freeze();
        return brush;
    }

    /// <summary>For a binding from an <see cref="Rgb"/> to a brush.</summary>
    public static IValueConverter Brush { get; } = new Map(value => value is Rgb rgb ? BrushOf(rgb) : Brushes.Transparent);

    /// <summary>For a binding from "the mark is light" to the white or black brush of the check mark on an accent swatch.</summary>
    public static IValueConverter MarkBrush { get; } = new Map(value => value is true ? Brushes.White : Brushes.Black);

    private sealed class Map(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => convert(value);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
