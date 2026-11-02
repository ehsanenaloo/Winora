// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>
/// Fades the top or bottom edge of a scroll viewer while more content lies beyond it, so a list that is cut off reads as one that scrolls.
/// Used where the scroll bar stays hidden until the mouse moves over it (the navigation list).
/// </summary>
public static class ScrollEdgeFade
{
    public const double FadeLength = 24;

    // An opacity mask reads only the alpha of its colors.
    private static readonly Color Opaque = Colors.Black;

    private static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached("IsAttached", typeof(bool), typeof(ScrollEdgeFade));

    public static void Attach(ScrollViewer viewer)
    {
        if ((bool)viewer.GetValue(IsAttachedProperty)) return;
        viewer.SetValue(IsAttachedProperty, true);
        viewer.ScrollChanged += (_, _) => Update(viewer);
        viewer.SizeChanged += (_, _) => Update(viewer);
        Update(viewer);
    }

    /// <summary>True while the edge has content beyond it; the mask is drawn only then.</summary>
    public static (bool Top, bool Bottom) Edges(ScrollViewer viewer) =>
        (viewer.VerticalOffset > 0.5, viewer.VerticalOffset + viewer.ViewportHeight < viewer.ExtentHeight - 0.5);

    private static void Update(ScrollViewer viewer)
    {
        var (top, bottom) = Edges(viewer);
        var height = viewer.ActualHeight;
        if (!(top || bottom) || height <= FadeLength * 2) { viewer.OpacityMask = null; return; }
        // Absolute points in the viewer's own space: the fade keeps its length whatever the height.
        var mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, height) };
        var share = FadeLength / height;
        mask.GradientStops.Add(new GradientStop(top ? Colors.Transparent : Opaque, 0));
        mask.GradientStops.Add(new GradientStop(Opaque, share));
        mask.GradientStops.Add(new GradientStop(Opaque, 1 - share));
        mask.GradientStops.Add(new GradientStop(bottom ? Colors.Transparent : Opaque, 1));
        mask.Freeze();
        viewer.OpacityMask = mask;
    }
}
