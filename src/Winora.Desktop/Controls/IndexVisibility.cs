// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Winora.Desktop.Controls;

/// <summary>Shows an element only while a tab index is the one given: a part of the fixed band that belongs to one tab of its page.</summary>
public sealed class IndexVisibility : IValueConverter
{
    private readonly int index;

    private IndexVisibility(int index) => this.index = index;

    public static readonly IndexVisibility First = new(0);
    public static readonly IndexVisibility Second = new(1);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i && i == index ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
