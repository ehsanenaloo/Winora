// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>
/// The page edge of every page: PagePadding when the page is wide, MediumPagePadding below <see cref="WideWidth"/>, CompactPagePadding below
/// <see cref="MediumWidth"/>. Pages used fixed 32 px on some and a responsive edge on others, so content and banners did not line up at narrow
/// widths. A Border gets it as Padding, any other element as Margin.
/// </summary>
public static class PageInset
{
    public const double WideWidth = 1100;
    public const double MediumWidth = 820;

    public static readonly DependencyProperty ResponsiveProperty = DependencyProperty.RegisterAttached("Responsive", typeof(bool), typeof(PageInset), new PropertyMetadata(false, OnChanged));
    public static bool GetResponsive(DependencyObject d) => (bool)d.GetValue(ResponsiveProperty);
    public static void SetResponsive(DependencyObject d, bool value) => d.SetValue(ResponsiveProperty, value);

    /// <summary>The resource key of the edge for a page of this width.</summary>
    public static string KeyFor(double width) => width >= WideWidth ? "PagePadding" : width >= MediumWidth ? "MediumPagePadding" : "CompactPagePadding";

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.SizeChanged -= OnSize; element.Loaded -= OnLoaded;
        if (e.NewValue is not true) return;
        element.SizeChanged += OnSize; element.Loaded += OnLoaded;
        Apply(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((FrameworkElement)sender);
    private static void OnSize(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) Apply((FrameworkElement)sender); }

    private static void Apply(FrameworkElement element)
    {
        var width = AvailableWidth(element);
        if (width <= 0 || element.TryFindResource(KeyFor(width)) is not Thickness edge) return;
        if (element is Border border) { if (border.Padding != edge) border.Padding = edge; }
        else if (element.Margin != edge) element.Margin = edge;
    }

    // The width the page has: the viewport of the scroll viewer around it, or the width of its parent.
    private static double AvailableWidth(FrameworkElement element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer viewer && viewer.ViewportWidth > 0) return viewer.ViewportWidth;
            if (node is FrameworkElement { ActualWidth: > 0 } parent && node is not ContentPresenter) return parent.ActualWidth;
        }
        return element.ActualWidth;
    }
}
