// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Winora.Application;

namespace Winora.Desktop.Controls;

/// <summary>The one mapping from the kind of a file group to its color and pattern on the disk map, the legend and the list. The view models only know the kind.</summary>
public static class DiskLook
{
    private static readonly ConcurrentDictionary<DiskFileKind, Brush> Cache = new();

    /// <summary>The color strip of a list row: the color the type has on the map.</summary>
    public static Brush KindBrush(DiskFileKind kind) => Cache.GetOrAdd(kind, k =>
    {
        var brush = new SolidColorBrush(TreemapControl.ColorOf(k));
        brush.Freeze();
        return brush;
    });

    /// <summary>The swatch of a legend entry: a line pattern when the legend shows patterns or high contrast is on, the plain color otherwise.</summary>
    public static Brush LegendSwatch(LegendItem item) => item.ShowPattern || SystemParameters.HighContrast
        ? TreemapPatterns.BrushFor(item.Kind, TreemapControl.ColorOf(item.Kind), SystemParameters.HighContrast)
        : new SolidColorBrush(TreemapControl.ColorOf(item.Kind));

    /// <summary>For a binding from a <see cref="DiskFileKind"/> to a brush.</summary>
    public static IValueConverter KindBrushOf { get; } = new Map(value => value is DiskFileKind kind ? KindBrush(kind) : Brushes.Transparent);

    /// <summary>For a binding from a <see cref="LegendItem"/> to a brush.</summary>
    public static IValueConverter LegendSwatchOf { get; } = new Map(value => value is LegendItem item ? LegendSwatch(item) : Brushes.Transparent);

    private sealed class Map(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => convert(value);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
