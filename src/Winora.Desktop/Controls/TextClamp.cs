// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>
/// Shows at most a number of whole lines of a wrapping text block: the height limit follows the line height and font of the text, so a larger Persian line height never cuts a line
/// in half. Zero or less removes the limit.
/// </summary>
public static class TextClamp
{
    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.RegisterAttached("MaxLines", typeof(int), typeof(TextClamp),
        new FrameworkPropertyMetadata(0, (d, _) => { if (d is TextBlock text) Bind(text); }));

    public static int GetMaxLines(DependencyObject d) => (int)d.GetValue(MaxLinesProperty);
    public static void SetMaxLines(DependencyObject d, int value) => d.SetValue(MaxLinesProperty, value);

    /// <summary>The height of the given number of lines, or infinity for no limit.</summary>
    public static double HeightOf(double lineHeight, double fontSize, FontFamily? family, int lines)
    {
        if (lines <= 0) return double.PositiveInfinity;
        var line = double.IsNaN(lineHeight) || lineHeight <= 0 ? (family?.LineSpacing ?? 1.33) * fontSize : lineHeight;
        return Math.Ceiling(line * lines);
    }

    private static void Bind(TextBlock text)
    {
        if (BindingOperations.IsDataBound(text, FrameworkElement.MaxHeightProperty)) return;
        var self = new RelativeSource(RelativeSourceMode.Self);
        var binding = new MultiBinding { Converter = HeightConverter.Instance };
        binding.Bindings.Add(new Binding(nameof(TextBlock.LineHeight)) { RelativeSource = self });
        binding.Bindings.Add(new Binding(nameof(TextBlock.FontSize)) { RelativeSource = self });
        binding.Bindings.Add(new Binding(nameof(TextBlock.FontFamily)) { RelativeSource = self });
        binding.Bindings.Add(new Binding { Path = new PropertyPath(MaxLinesProperty), RelativeSource = self });
        BindingOperations.SetBinding(text, FrameworkElement.MaxHeightProperty, binding);
    }

    private sealed class HeightConverter : IMultiValueConverter
    {
        public static readonly HeightConverter Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [double lineHeight, double fontSize, var family, int lines] ? HeightOf(lineHeight, fontSize, family as FontFamily, lines) : double.PositiveInfinity;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
