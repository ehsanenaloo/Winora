// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>
/// Keeps the main window inside the work area of the screen. A fixed design size larger than the screen put the title bar and the navigation button off screen
/// on a 1024 by 768 display (the window was centred, so its left and top edges moved to negative coordinates).
/// </summary>
public static class WindowSizing
{
    public readonly record struct Fit(double Width, double Height, double MinWidth, double MinHeight);

    /// <summary>All sizes are in device-independent units. A work area of zero or less (not known) leaves the design size unchanged.</summary>
    public static Fit FitToWorkArea(double width, double height, double minWidth, double minHeight, double workWidth, double workHeight)
    {
        if (workWidth <= 0 || workHeight <= 0) return new(width, height, minWidth, minHeight);
        return new(Math.Min(width, workWidth), Math.Min(height, workHeight), Math.Min(minWidth, workWidth), Math.Min(minHeight, workHeight));
    }
}
