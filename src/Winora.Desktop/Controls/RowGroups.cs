// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace Winora.Desktop.Controls;

/// <summary>Groups the rows of the change history by day for the timeline. The view models hold the plain list of rows and the flag; the grouped view is a WPF thing and is made here.</summary>
public static class RowGroups
{
    /// <summary>The default view of the rows, grouped by the day of each row when <paramref name="byDay"/> is set.</summary>
    public static ICollectionView ViewOf(IEnumerable rows, bool byDay)
    {
        var view = CollectionViewSource.GetDefaultView(rows);
        if (byDay && view.GroupDescriptions.Count == 0) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryRowViewModel.Day)));
        return view;
    }

    /// <summary>For a binding of the rows and the group flag to the items of a list.</summary>
    public static IMultiValueConverter ByDay { get; } = new DayConverter();

    private sealed class DayConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [IEnumerable rows, var flag] ? ViewOf(rows, flag is true) : Binding.DoNothing;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
