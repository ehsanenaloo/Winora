// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Winora.Desktop.Controls;

/// <summary>
/// A ring that fills to <see cref="Value"/> (0 to 100). It runs one code animation from zero to the value each time <see cref="AnimationToken"/> changes and Windows animations are on;
/// any other change of the value is shown at once. With animations off the final value is drawn and no clock runs. The number in the middle is <see cref="DisplayNumber"/>.
/// </summary>
public sealed class ScoreRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ScoreRing), new FrameworkPropertyMetadata(0.0, OnValueChanged));
    public static readonly DependencyProperty AnimationTokenProperty = DependencyProperty.Register(nameof(AnimationToken), typeof(int), typeof(ScoreRing), new FrameworkPropertyMetadata(0, OnTokenChanged));
    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(nameof(DisplayValue), typeof(double), typeof(ScoreRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnDisplayChanged));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(ScoreRing), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(nameof(ArcBrush), typeof(Brush), typeof(ScoreRing), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(ScoreRing), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    private static readonly DependencyPropertyKey DisplayNumberKey = DependencyProperty.RegisterReadOnly(nameof(DisplayNumber), typeof(string), typeof(ScoreRing), new FrameworkPropertyMetadata("0"));
    public static readonly DependencyProperty DisplayNumberProperty = DisplayNumberKey.DependencyProperty;

    static ScoreRing()
    {
        FocusableProperty.OverrideMetadata(typeof(ScoreRing), new FrameworkPropertyMetadata(false));
        FlowDirectionProperty.OverrideMetadata(typeof(ScoreRing), new FrameworkPropertyMetadata(FlowDirection.LeftToRight, (d, _) => ((ScoreRing)d).UpdateNumber()));
    }

    public ScoreRing() { IsHitTestVisible = false; UpdateNumber(); }

    /// <summary>The target, 0 to 100.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    /// <summary>Changing this plays the fill animation once.</summary>
    public int AnimationToken { get => (int)GetValue(AnimationTokenProperty); set => SetValue(AnimationTokenProperty, value); }
    /// <summary>The value on screen right now. It differs from <see cref="Value"/> only while the animation runs.</summary>
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); private set => SetValue(DisplayValueProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush ArcBrush { get => (Brush)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    /// <summary>The rounded display value with the digits of the page language (Persian digits when the page runs right to left).</summary>
    public string DisplayNumber => (string)GetValue(DisplayNumberProperty);
    /// <summary>True while the fill animation runs.</summary>
    public bool IsAnimating { get; private set; }

    public static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 100);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ScoreRing)d).Settle();

    private static void OnTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ScoreRing)d).Play();

    private static void OnDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ScoreRing)d).UpdateNumber();

    // A new value without a new token: show it at once.
    private void Settle()
    {
        if (IsAnimating) { Play(); return; }
        DisplayValue = Clamp(Value);
    }

    private void Play()
    {
        var target = Clamp(Value);
        BeginAnimation(DisplayValueProperty, null);
        if (!Motion.Enabled || target <= 0)
        {
            IsAnimating = false;
            DisplayValue = target;
            return;
        }
        IsAnimating = true;
        var animation = new DoubleAnimation(0, target, Motion.Count) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => { IsAnimating = false; DisplayValue = Clamp(Value); };
        BeginAnimation(DisplayValueProperty, animation);
    }

    private void UpdateNumber()
    {
        var text = ((int)Math.Round(DisplayValue, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        SetValue(DisplayNumberKey, FlowDirection == FlowDirection.RightToLeft ? DigitConverter.ToPersian(text) : text);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 160 : availableSize.Height);
        return new(Math.Max(side, Thickness * 2 + 8), Math.Max(side, Thickness * 2 + 8));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= Thickness * 2) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (side - Thickness) / 2;
        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);
        var fraction = Clamp(DisplayValue) / 100;
        if (fraction <= 0) return;
        var pen = new Pen(ArcBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (fraction >= 0.9995) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
        // The arc starts at the top and runs clockwise. In right-to-left pages the ring is mirrored by the framework.
        var angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure(start, [new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true)], false);
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RingPeer(this);

    private sealed class RingPeer(ScoreRing owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(ScoreRing);
        protected override bool IsControlElementCore() => System.Windows.Automation.AutomationProperties.GetName(owner) is { Length: > 0 };
    }
}
