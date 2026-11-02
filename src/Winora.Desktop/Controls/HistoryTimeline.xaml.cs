// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// One timeline for all journals: day groups, batch rows that expand into their members, a result badge, the area and the time, and the reason when an entry cannot be undone. The list is virtualized
/// and keeps its own scroll bar below <see cref="ListMaxHeight"/>. Up and Down move the selection, which fills the details pane; Ctrl+Z on the page opens the undo review.
/// </summary>
public partial class HistoryTimeline : UserControl
{
    public static readonly DependencyProperty ListMaxHeightProperty = DependencyProperty.Register(nameof(ListMaxHeight), typeof(double), typeof(HistoryTimeline), new PropertyMetadata(520.0));

    public HistoryTimeline()
    {
        InitializeComponent();
        // A list that never scrolls itself (no height limit) leaves the wheel to the page.
        TimelineList.PreviewMouseWheel += (sender, e) =>
        {
            if (e.Handled || !double.IsPositiveInfinity(ListMaxHeight) || System.Windows.Media.VisualTreeHelper.GetParent(this) is not UIElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
        };
    }

    public double ListMaxHeight { get => (double)GetValue(ListMaxHeightProperty); set => SetValue(ListMaxHeightProperty, value); }
    public System.Windows.Controls.ListView ListView => TimelineList;
}
