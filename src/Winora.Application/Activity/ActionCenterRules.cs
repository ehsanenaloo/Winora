// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application.Activity;

public enum ActionCenterIndicator { None, Running, Succeeded, Warning, Unread }

public enum PaneMode { Docked, Drawer }

public enum StripKind { None, Run, Staged, Notification }

public enum RailDot { None, Running, Warning, Pending, Unread }

/// <summary>The decisions of the Action center that need no window: where it sits, whether it starts open, what its corner indicator and the status strip say.</summary>
public static class ActionCenterRules
{
    /// <summary>Preference key for the open or closed state; the value is "1" or "0".</summary>
    public const string OpenPreference = "ac.open";
    /// <summary>Preference key that is "1" once the first-time hint was shown.</summary>
    public const string HintPreference = "ac.hint";
    /// <summary>At this width and below the pane is a drawer over the content.</summary>
    public const double DrawerMaxWidth = 1000;
    /// <summary>On the first start the pane is open from this width.</summary>
    public const double OpenFromWidth = 1200;

    public static PaneMode Mode(double width) => width <= DrawerMaxWidth ? PaneMode.Drawer : PaneMode.Docked;

    /// <summary>The remembered choice, or on the first start open only when the window is wide enough.</summary>
    public static bool StartsOpen(string? remembered, double width) =>
        remembered switch { "1" => true, "0" => false, _ => width >= OpenFromWidth };

    /// <summary>Whether the pane shows now. Docked, it follows the remembered choice. A drawer is never open by itself: it shows only after the person opened it.</summary>
    public static bool IsShown(PaneMode mode, bool dockedOpen, bool drawerOpen) => mode == PaneMode.Drawer ? drawerOpen : dockedOpen;

    public static string OpenValue(bool open) => open ? "1" : "0";


    /// <summary>The corner mark of the title bar button: a spinner while anything runs, the result of the last finished run until it is seen, a dot for unread notifications.</summary>
    public static ActionCenterIndicator Indicator(int runs, RunResult? unseenResult, int unread)
    {
        if (runs > 0) return ActionCenterIndicator.Running;
        if (unseenResult is RunResult.Ok) return ActionCenterIndicator.Succeeded;
        if (unseenResult is RunResult.Warning or RunResult.Error) return ActionCenterIndicator.Warning;
        return unread > 0 ? ActionCenterIndicator.Unread : ActionCenterIndicator.None;
    }

    /// <summary>What the strip at the bottom of the content says. It exists only while the pane is closed.</summary>
    public static StripKind Strip(bool paneOpen, int runs, int staged, bool hasNewNotification)
    {
        if (paneOpen) return StripKind.None;
        if (runs > 0) return StripKind.Run;
        if (staged > 0) return StripKind.Staged;
        return hasNewNotification ? StripKind.Notification : StripKind.None;
    }

    /// <summary>
    /// The dot of the closed pane's rail. It replaces the count badge and the corner mark of the title bar button while the rail shows, so one place carries the state:
    /// a run first, then a failed result, then staged changes, then a finished result or a notification not yet seen.
    /// </summary>
    public static RailDot Dot(ActionCenterIndicator indicator, int staged) => indicator switch
    {
        ActionCenterIndicator.Running => RailDot.Running,
        ActionCenterIndicator.Warning => RailDot.Warning,
        _ when staged > 0 => RailDot.Pending,
        ActionCenterIndicator.Succeeded or ActionCenterIndicator.Unread => RailDot.Unread,
        _ => RailDot.None,
    };

    /// <summary>Elapsed time as m:ss, or h:mm:ss from one hour.</summary>
    public static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }
}
