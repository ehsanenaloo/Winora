// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>
/// A round badge with the first letter of a name, used on list rows (startup items, preinstalled apps). The colour comes from the name, so a row keeps its colour;
/// it never carries meaning and is hidden from screen readers.
/// </summary>
public sealed class AvatarBadge : FrameworkElement
{
    public static readonly DependencyProperty NameTextProperty = DependencyProperty.Register(nameof(NameText), typeof(string), typeof(AvatarBadge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string NameText { get => (string)GetValue(NameTextProperty); set => SetValue(NameTextProperty, value); }

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(AvatarBadge),
        new FrameworkPropertyMetadata(36d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    static AvatarBadge()
    {
        FocusableProperty.OverrideMetadata(typeof(AvatarBadge), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(AvatarBadge), new FrameworkPropertyMetadata(false));
    }

    /// <summary>The letter shown: the first letter or digit of the name, upper case, or a bullet for an empty name.</summary>
    public static string LetterOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "•";
        foreach (var rune in name.EnumerateRunes())
            if (System.Text.Rune.IsLetterOrDigit(rune)) return rune.ToString().ToUpper(CultureInfo.CurrentCulture);
        return "•";
    }

    /// <summary>A hue from 0 to 359 that depends only on the name (stable between runs, unlike string.GetHashCode).</summary>
    public static int HueOf(string? name)
    {
        var hash = 17;
        foreach (var c in name ?? "") hash = unchecked(hash * 31 + char.ToLowerInvariant(c));
        return (int)((uint)hash % 360u);
    }

    /// <summary>Background and text colour. The background is dark enough for white text (at least 4.5:1) in both themes; in a high contrast theme the system colours are used.</summary>
    public static (Color Back, Color Text) Colors(string? name)
    {
        if (SystemParameters.HighContrast) return (SystemColors.ControlColor, SystemColors.ControlTextColor);
        return (BackFor(HueOf(name)), System.Windows.Media.Colors.White);
    }

    /// <summary>The background of a hue: darkened until white text on it has at least 4.5:1 (yellow and green need a darker tone than blue).</summary>
    public static Color BackFor(int hue)
    {
        var lightness = 0.36;
        var color = FromHsl(hue, 0.45, lightness);
        while (ContrastWithWhite(color) < 4.5 && lightness > 0.1) { lightness -= 0.02; color = FromHsl(hue, 0.45, lightness); }
        return color;
    }

    private static double ContrastWithWhite(Color c)
    {
        static double Channel(byte v) { var x = v / 255d; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        var luminance = 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        return 1.05 / (luminance + 0.05);
    }

    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        // A right to left layout mirrors what an element draws: flip back so the letter reads the same in both directions (as AppIcon does).
        var flipped = FlowDirection == FlowDirection.RightToLeft;
        if (flipped) dc.PushTransform(new ScaleTransform(-1, 1, Size / 2, Size / 2));
        Draw(dc, NameText, Size, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (flipped) dc.Pop();
    }

    /// <summary>Draws the badge at the origin. Shared with <see cref="AppIcon"/>, which shows it while an app has no icon.</summary>
    internal static void Draw(DrawingContext dc, string? name, double size, double pixelsPerDip)
    {
        var (back, text) = Colors(name);
        var half = size / 2;
        dc.DrawEllipse(new SolidColorBrush(back), null, new Point(half, half), half, half);
        // The invariant culture keeps the digit as the name writes it: a Persian culture would shape the "7" of "7-Zip" as a Persian digit.
        var formatted = new FormattedText(LetterOf(name), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size * 0.42, new SolidColorBrush(text), pixelsPerDip);
        dc.DrawText(formatted, new Point(half - formatted.Width / 2, half - formatted.Height / 2));
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}

/// <summary>
/// A horizontal bar made of up to three parts (High, Medium, Low) in proportion to their counts. The parts are separated by a gap and by a different fill, so the bar reads without colour: the
/// owner shows the labels and the counts beside it.
/// </summary>
public sealed class SegmentedBar : FrameworkElement
{
    public static readonly DependencyProperty FirstProperty = DependencyProperty.Register(nameof(First), typeof(double), typeof(SegmentedBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SecondProperty = DependencyProperty.Register(nameof(Second), typeof(double), typeof(SegmentedBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThirdProperty = DependencyProperty.Register(nameof(Third), typeof(double), typeof(SegmentedBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double First { get => (double)GetValue(FirstProperty); set => SetValue(FirstProperty, value); }
    public double Second { get => (double)GetValue(SecondProperty); set => SetValue(SecondProperty, value); }
    public double Third { get => (double)GetValue(ThirdProperty); set => SetValue(ThirdProperty, value); }

    private const double Gap = 3;

    static SegmentedBar()
    {
        FocusableProperty.OverrideMetadata(typeof(SegmentedBar), new FrameworkPropertyMetadata(false));
        HeightProperty.OverrideMetadata(typeof(SegmentedBar), new FrameworkPropertyMetadata(10d));
    }

    /// <summary>The widths of the parts for a total width. A part with a count gets at least a visible stub; an empty part gets none.</summary>
    public static IReadOnlyList<double> Widths(double total, double a, double b, double c)
    {
        var values = new[] { Math.Max(0, a), Math.Max(0, b), Math.Max(0, c) };
        var shown = values.Count(v => v > 0);
        var sum = values.Sum();
        if (shown == 0 || sum <= 0 || total <= 0) return [0, 0, 0];
        var usable = Math.Max(0, total - Gap * (shown - 1));
        var min = Math.Min(6d, usable / shown);
        var widths = values.Select(v => v <= 0 ? 0 : Math.Max(min, usable * v / sum)).ToArray();
        var over = widths.Sum() - usable;
        if (over > 0)
        {
            var biggest = Array.IndexOf(widths, widths.Max());
            widths[biggest] -= over;
        }
        return widths;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var total = ActualWidth; var height = ActualHeight;
        var widths = Widths(total, First, Second, Third);
        string[] keys = ["WinoraRingLowBrush", "WinoraRingFairBrush", "WinoraRingGoodBrush"];
        var x = 0d;
        for (var i = 0; i < 3; i++)
        {
            if (widths[i] <= 0) continue;
            var brush = TryFindResource(keys[i]) as Brush ?? Brushes.Transparent;
            // The middle part is hatched so the three parts differ without colour.
            var rect = new Rect(x, 0, widths[i], height);
            dc.DrawRoundedRectangle(brush, null, rect, height / 2, height / 2);
            if (i == 1) DrawHatch(dc, rect, height);
            x += widths[i] + Gap;
        }
        if (widths.All(w => w <= 0))
            dc.DrawRoundedRectangle(TryFindResource("WinoraSurfaceSunkenBrush") as Brush ?? Brushes.Transparent, null, new Rect(0, 0, total, height), height / 2, height / 2);
    }

    private void DrawHatch(DrawingContext dc, Rect rect, double height)
    {
        var pen = new Pen(TryFindResource("WinoraSurfaceCardBrush") as Brush ?? Brushes.Transparent, 1.5);
        dc.PushClip(new RectangleGeometry(rect, height / 2, height / 2));
        for (var x = rect.Left - height; x < rect.Right; x += 6) dc.DrawLine(pen, new Point(x, rect.Bottom), new Point(x + height, rect.Top));
        dc.Pop();
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
