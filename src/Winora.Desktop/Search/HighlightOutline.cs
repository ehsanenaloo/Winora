// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Winora.Desktop.Search;

/// <summary>
/// The outline a page draws around the item the search opened: 2 px in the accent colour (the window text colour in high contrast), kept until keyboard focus leaves the item.
/// There is no timer and no fade, so reduced motion needs no special case. A page calls <see cref="Show"/> from its <see cref="IHighlightTarget"/> implementation.
/// </summary>
public static class HighlightOutline
{
    private static OutlineAdorner? current;
    private static FrameworkElement? madeFocusable;

    /// <summary>Scrolls the item into view, moves keyboard focus into it and draws the outline. Returns false when the item is not shown.</summary>
    public static bool Show(FrameworkElement item)
    {
        Clear();
        if (!item.IsVisible) return false;
        item.BringIntoView();
        item.UpdateLayout();
        var focusTarget = FirstFocusable(item);
        if (focusTarget is null && !item.Focusable) { item.Focusable = true; madeFocusable = item; }
        (focusTarget ?? (item.Focusable ? item : null))?.Focus();
        var layer = AdornerLayer.GetAdornerLayer(item);
        if (layer is null) return focusTarget is not null;
        var adorner = new OutlineAdorner(item);
        layer.Add(adorner);
        current = adorner;
        item.IsKeyboardFocusWithinChanged += OnFocusWithinChanged;
        return true;
    }

    public static void Clear()
    {
        if (madeFocusable is not null) { madeFocusable.Focusable = false; madeFocusable = null; }
        if (current is null) return;
        var adorner = current; current = null;
        if (adorner.AdornedElement is FrameworkElement item) item.IsKeyboardFocusWithinChanged -= OnFocusWithinChanged;
        AdornerLayer.GetAdornerLayer(adorner.AdornedElement)?.Remove(adorner);
    }

    /// <summary>True while an outline is shown (used by tests).</summary>
    public static FrameworkElement? Current => current?.AdornedElement as FrameworkElement;

    private static void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Clear();
    }

    private static UIElement? FirstFocusable(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element && KeyboardNavigation.GetIsTabStop(element)) return element;
            if (FirstFocusable(child) is { } nested) return nested;
        }
        return null;
    }

    private sealed class OutlineAdorner : Adorner
    {
        public OutlineAdorner(UIElement element) : base(element) => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext drawingContext)
        {
            var brush = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? SystemColors.HighlightBrush;
            var pen = new Pen(brush, 2);
            if (pen.CanFreeze) pen.Freeze();
            var size = AdornedElement.RenderSize;
            drawingContext.DrawRoundedRectangle(null, pen, new Rect(-1, -1, size.Width + 2, size.Height + 2), 6, 6);
        }
    }
}
