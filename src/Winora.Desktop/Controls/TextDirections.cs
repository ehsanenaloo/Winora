// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Winora.Desktop.Controls;

/// <summary>The one mapping from the reading direction of the language (<see cref="TextDirection"/>) to the flow direction of a window.</summary>
public static class TextDirections
{
    public static FlowDirection ToFlow(this TextDirection direction) => direction == TextDirection.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>For a binding from a <see cref="TextDirection"/> to a <see cref="FlowDirection"/>.</summary>
    public static IValueConverter Flow { get; } = new FlowConverter();

    private sealed class FlowConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is TextDirection d ? d.ToFlow() : FlowDirection.LeftToRight;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
