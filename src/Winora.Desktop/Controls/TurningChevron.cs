// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// A chevron that turns between a closed and an open angle through <see cref="Motion"/> (instant when Windows animations are off). A down chevron with 0 and 180 degrees reads as
/// down and up; with <see cref="SideClosed"/> the closed chevron points forward, toward the text it opens (right in left-to-right, left in right-to-left), and the open one points down.
/// </summary>
public sealed class TurningChevron : Decorator
{
    private readonly RotateTransform turn = new();

    public TurningChevron()
    {
        var icon = new SymbolIcon { Symbol = SymbolRegular.ChevronDown20, FontSize = IconSizes.Control, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        Child = icon;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = turn;
        IsHitTestVisible = false;
        Loaded += (_, _) => Set(animate: false);
    }

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(TurningChevron),
        new FrameworkPropertyMetadata(false, (d, _) => ((TurningChevron)d).Set(animate: true)));
    public static readonly DependencyProperty SideClosedProperty = DependencyProperty.Register(nameof(SideClosed), typeof(bool), typeof(TurningChevron),
        new FrameworkPropertyMetadata(false, (d, _) => ((TurningChevron)d).Set(animate: false)));

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    /// <summary>The closed state points sideways (a group that folds) instead of down (a section that opens).</summary>
    public bool SideClosed { get => (bool)GetValue(SideClosedProperty); set => SetValue(SideClosedProperty, value); }

    /// <summary>The angle the chevron has or is turning to.</summary>
    public double TargetAngle { get; private set; }
    private void Set(bool animate)
    {
        // The mirror of a right-to-left page flips the turned glyph, so the same angle points right in a left-to-right page and left in a right-to-left one.
        var closed = SideClosed ? -90 : 0;
        var open = SideClosed ? 0 : 180;
        TargetAngle = IsOpen ? open : closed;
        if (!animate || !Motion.Enabled || !IsLoaded) { turn.BeginAnimation(RotateTransform.AngleProperty, null); turn.Angle = TargetAngle; return; }
        turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(TargetAngle, Motion.Normal) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
