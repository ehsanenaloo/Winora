// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>
/// Smooth mouse wheel scrolling for every scroll viewer of the app. The wheel moves a target offset and the viewer eases toward it, frame by frame, until it arrives; nothing runs while
/// the page is still. It is off when the person turns it off in Settings or when Windows animations are off. A scroll viewer that scrolls by whole items, Ctrl and Shift with the wheel,
/// and a viewer that is already at its end (the wheel then goes to the viewer around it) keep the normal behavior.
/// </summary>
public static class SmoothScroll
{
    /// <summary>One wheel notch in pixels; the same as three lines of text.</summary>
    private const double NotchPixels = 48;
    private const double EaseSeconds = 0.09;
    private static bool registered;
    private static readonly ConditionalWeakTable<ScrollViewer, Glide> glides = [];

    public static bool Enabled { get; set; } = true;

    public static void Register()
    {
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || !Enabled || !Motion.Enabled || Keyboard.Modifiers != ModifierKeys.None) return;
        var viewer = (ScrollViewer)sender;
        if (Target(e.OriginalSource as DependencyObject, e.Delta < 0) != viewer) return;
        var glide = glides.GetValue(viewer, v => new Glide(v));
        glide.Push(-e.Delta / 120.0 * NotchPixels);
        e.Handled = true;
    }

    /// <summary>The scroll viewer that takes the wheel: the nearest one around the source that can move in that direction and scrolls by pixels.</summary>
    private static ScrollViewer? Target(DependencyObject? source, bool down)
    {
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is not ScrollViewer viewer || viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled || viewer.ScrollableHeight <= 0) continue;
            if (viewer.CanContentScroll && (viewer.TemplatedParent is not ItemsControl owner || VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Item)) continue; // scrolls by items
            var room = down ? viewer.ScrollableHeight - viewer.VerticalOffset : viewer.VerticalOffset;
            if (room > 0.5) return viewer;
        }
        return null;
    }

    private sealed class Glide(ScrollViewer viewer)
    {
        private double target;
        private bool running;
        private TimeSpan last;

        public void Push(double pixels)
        {
            target = Math.Clamp((running ? target : viewer.VerticalOffset) + pixels, 0, viewer.ScrollableHeight);
            if (running) return;
            running = true; last = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = (e as RenderingEventArgs)?.RenderingTime ?? last;
            var dt = last == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - last).TotalSeconds, 0.001, 0.1);
            last = now;
            if (!viewer.IsLoaded) { Stop(); return; }
            target = Math.Clamp(target, 0, viewer.ScrollableHeight);
            var current = viewer.VerticalOffset;
            var left = target - current;
            if (Math.Abs(left) < 0.5) { viewer.ScrollToVerticalOffset(target); Stop(); return; }
            viewer.ScrollToVerticalOffset(current + left * (1 - Math.Exp(-dt / EaseSeconds)));
        }

        private void Stop()
        {
            running = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }
}
