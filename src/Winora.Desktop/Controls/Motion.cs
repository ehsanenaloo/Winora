// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Winora.Desktop;

/// <summary>
/// Entrance motion in code. Follows the Windows "show animations" setting; when it is off every helper does nothing and the element is left in its final state.
/// Only Opacity and RenderTransform are animated. Nothing loops at idle: the pulse stops by itself after five seconds.
/// </summary>
public static class Motion
{
    private static bool? forced;

    /// <summary>The Windows setting, unless a test or a caller forces a value.</summary>
    public static bool Enabled
    {
        get => forced ?? SystemParameters.ClientAreaAnimation;
        set => forced = value;
    }

    public static void FollowSystem() => forced = null;

    public static TimeSpan Fast => Pick(0.083);
    public static TimeSpan Normal => Pick(0.167);
    public static TimeSpan Slow => Pick(0.25);
    /// <summary>The count-up of the score ring: one second, or zero when animations are off.</summary>
    public static TimeSpan Count => Pick(1.0);

    public static readonly TimeSpan PulseLimit = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PulsePeriod = TimeSpan.FromSeconds(1.5);

    private static TimeSpan Pick(double seconds) => Enabled ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    /// <summary>Fades the element in. Returns true when an animation started.</summary>
    public static bool FadeIn(UIElement element, TimeSpan? duration = null)
    {
        element.Opacity = 1;
        if (!Enabled) return false;
        var animation = new DoubleAnimation(0, 1, duration ?? Normal) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
        return true;
    }

    /// <summary>Moves the element up into place from below. Returns true when an animation started.</summary>
    public static bool SlideIn(UIElement element, double offset = 8, TimeSpan? duration = null)
    {
        if (!Enabled) return false;
        if (element.RenderTransform is not TranslateTransform { IsFrozen: false } transform)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }
        var animation = new DoubleAnimation(offset, 0, duration ?? Normal) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        transform.BeginAnimation(TranslateTransform.YProperty, animation);
        return true;
    }

    /// <summary>Pulses the opacity between 0.5 and 1.0 while something loads, for at most five seconds. Returns an action that stops it. Static when motion is off.</summary>
    public static Action Pulse(UIElement element)
    {
        element.Opacity = 1;
        if (!Enabled) return static () => { };
        var half = TimeSpan.FromTicks(PulsePeriod.Ticks / 2);
        var animation = new DoubleAnimation(1.0, 0.5, half)
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(PulseLimit),
            FillBehavior = FillBehavior.Stop,
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
        return () => element.BeginAnimation(UIElement.OpacityProperty, null);
    }
}
