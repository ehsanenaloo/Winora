// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>The one mapping from the name of a pictogram (<see cref="IconKey"/>) to the symbol of the UI toolkit. The view models only know the key.</summary>
public static class IconKeys
{
    private static readonly IReadOnlyDictionary<IconKey, SymbolRegular> Map =
        Enum.GetValues<IconKey>().ToDictionary(key => key, key => key == IconKey.None ? SymbolRegular.Empty : Enum.Parse<SymbolRegular>(key.ToString()));

    public static SymbolRegular SymbolOf(IconKey key) => Map[key];

    /// <summary>For a binding whose target is a <see cref="SymbolRegular"/>.</summary>
    public static IValueConverter Symbol { get; } = new SymbolConverter();

    private sealed class SymbolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is IconKey key ? SymbolOf(key) : SymbolRegular.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
