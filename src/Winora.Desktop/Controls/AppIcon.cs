// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>
/// The icon of an app on a list row: the real icon on a plate that follows the theme (so an icon with a transparent background stays visible; the icon is never tinted), or the letter badge
/// while there is no icon. Decorative: hidden from screen readers. Drawn the same way in right to left layouts.
/// </summary>
public sealed class AppIcon : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(AppIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public static readonly DependencyProperty NameTextProperty = DependencyProperty.Register(nameof(NameText), typeof(string), typeof(AppIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The name of the app; its first letter is the fallback.</summary>
    public string NameText { get => (string)GetValue(NameTextProperty); set => SetValue(NameTextProperty, value); }

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(AppIcon),
        new FrameworkPropertyMetadata(32d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public const double PlateRadius = 4;
    public const double PlatePadding = 3;

    static AppIcon()
    {
        FocusableProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        // A right to left layout mirrors everything an element draws, the icon and the letter included: flip back so they read the same in both directions.
        var flipped = FlowDirection == FlowDirection.RightToLeft;
        if (flipped) dc.PushTransform(new ScaleTransform(-1, 1, Size / 2, Size / 2));
        Paint(dc);
        if (flipped) dc.Pop();
    }

    private void Paint(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Source is not { } image) { AvatarBadge.Draw(dc, NameText, Size, dpi); return; }
        var plate = TryFindResource("WinoraIconPlateBrush") as Brush ?? Brushes.Transparent;
        var edge = TryFindResource("WinoraCardBorderBrush") as Brush;
        dc.DrawRoundedRectangle(plate, edge is null ? null : new Pen(edge, 1), new Rect(0.5, 0.5, Size - 1, Size - 1), PlateRadius, PlateRadius);
        var inner = Size - 2 * PlatePadding;
        dc.DrawImage(image, new Rect(PlatePadding, PlatePadding, inner, inner));
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
