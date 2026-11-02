// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// Lays the disk cards out in equal columns: at most <see cref="MaxColumns"/> per row, so one disk fills the whole width and two share it. A column never gets narrower than
/// <see cref="MinColumnWidth"/>; a narrow window shows one card per row. The cards keep their own margins.
/// </summary>
public sealed class DiskGridPanel : Panel
{
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(DiskGridPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(DiskGridPanel),
        new FrameworkPropertyMetadata(360d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }

    /// <summary>The number of columns for this many cards in this width.</summary>
    public static int ColumnsFor(int count, double width, int maxColumns, double minColumnWidth)
    {
        var columns = Math.Max(1, Math.Min(count, maxColumns));
        while (columns > 1 && !double.IsInfinity(width) && width / columns < minColumnWidth) columns--;
        return columns;
    }

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Visible();
        if (children.Count == 0) return default;
        var columns = ColumnsFor(children.Count, availableSize.Width, MaxColumns, MinColumnWidth);
        var cell = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / columns;
        double height = 0, widest = 0;
        for (var start = 0; start < children.Count; start += columns)
        {
            double row = 0;
            foreach (var child in children.Skip(start).Take(columns))
            {
                child.Measure(new Size(cell, double.PositiveInfinity));
                row = Math.Max(row, child.DesiredSize.Height); widest = Math.Max(widest, child.DesiredSize.Width);
            }
            height += row;
        }
        return new(double.IsInfinity(cell) ? widest * columns : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Visible();
        if (children.Count == 0) return finalSize;
        var columns = ColumnsFor(children.Count, finalSize.Width, MaxColumns, MinColumnWidth);
        var cell = finalSize.Width / columns;
        double top = 0;
        for (var start = 0; start < children.Count; start += columns)
        {
            var row = children.Skip(start).Take(columns).ToList();
            var height = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++) row[i].Arrange(new Rect(i * cell, top, cell, height));
            top += height;
        }
        return finalSize;
    }
}
