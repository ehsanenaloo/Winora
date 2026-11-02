<!-- SPDX-License-Identifier: GPL-3.0-only -->
# Activity service

One service in `Winora.Application.Activity` keeps the runs and the notifications of the app. The Action center in the main window shows it. Other shell parts (taskbar, tray, Windows notification) can read the same service. It has no timer and no UI types.

## What counts as a run

Every long action of a page is a run: scans and reads, cleanup, repair tools, profile apply, program and app removal, leftover moves, restore point, diagnostics export, settings apply, and every change that writes to the system (Customize, Optimize, Power, Network, Updates, Permissions, Startup, Services, Scheduled tasks, Quarantine, History undo). A run has a title, the key of its page, a flag `ChangesSystem`, a share from 0 to 1 (or none while it cannot tell), an optional Stop callback and a start time.

Rules:

- A run that changes the system is refused while another one runs. The refusal raises `StartRefused` with the run in the way. The page then does not start its work, and the Action center shows "Another change is running" with a link to it.
- Read-only runs never block and are never blocked.
- A run ends with one of `Ok`, `Warning`, `Error` or `Stopped`. A stop is the owner's choice, so it posts no notification. A read that ends well finishes quietly.
- Leaving a page stops the read-only runs of the page. It does not stop a run that changes the system.

## API

| Member | Use |
|---|---|
| `Begin(RunRequest)` | Starts a run and returns an `ActivityRun` handle. `Started` is false when the run was refused. |
| `ActivityRun.Report(share)` | Sets the share. Null makes the run indeterminate. |
| `ActivityRun.Finish(result, message, quiet)` | Ends the run. The message becomes the notification text. Disposing a handle that was not finished ends it quietly as Ok. |
| `TryStart`, `Report`, `Finish`, `RequestStop(id)` | The same on the service, by run id. `RequestStop` calls the Stop callback once. The owner of the run still ends it. |
| `Runs`, `RecentRuns`, `LastFinished`, `Find(id)` | Snapshots of running and finished runs (the last 10 finished are kept). |
| `Notify`, `NotifyRestartNeeded`, `OfferUndo` | Events that are not runs: a warning, a restart that is needed, an offered undo. |
| `Notifications`, `UnreadCount`, `MarkRead`, `MarkAllRead`, `Remove`, `ClearAll` | The notification list, newest first. |
| `RunsChanged`, `NotificationsChanged`, `NotificationPosted`, `StartRefused` | Events. They are raised on the calling thread after the lock is released. A subscriber moves to its own thread. |

In the Desktop project a page gets its handle from `RunSource` (`Runs.Begin(title, changesSystem, stop)`). A page created alone has no service, and its handles do nothing.

## Notifications

A notification has a severity (Ok, Info, Warning, Error, Restart), text, page key and time. The list keeps the newest 200 or the last 30 days, whichever is less, in the admin-only data folder. Only text, severity, time and page key are written. An action exists in memory only, so an item loaded after a restart can only open its page. A damaged or edited file gives an empty list or only its valid items.

Toasts are the short view of the same list. A toast that is a warning, an error, "restart needed" or has an action is also a notification. A plain confirmation stays a toast. A run that ends on another page shows a toast with View.

## Where it shows

- Ctrl+Shift+A opens or closes the panel. The title bar has no button for it: the rail below opens it and the close button in the panel closes it.
- While the panel is closed it stays as a 40 px rail at the end side of the window (the left side in Persian), in docked and drawer layout alike: the panel icon, the state and the name written down. The state is the count of staged changes in an accent badge, then one mark: a spinner while a run exists (with the run count from two), a check for a finished run, a warning for a failed one, or an accent dot for an unseen event. Click, Enter or Space opens the panel on the tab the state points to (Changes for staged changes, Activity for a run, a result or an event), and closing the panel moves the keyboard back to the rail.
- Status strip at the bottom of the page while the panel is closed: the details with Stop, Review or Open.
- Navigation: a spinner next to the page of a running run, a warning mark after a failure until the page is opened.
- Screen readers hear the start and the end of a run from a live region, and the rail and the open panel name the counts.

## Outside the window

`TaskbarTrayModel` (in `Winora.Presentation`) reads the same service and drives the taskbar button and the notification area. No page talks to them. A run counts as long after 2 seconds; shorter runs never reach the taskbar, so a page read does not make it flash. The model has no timer: while a run is younger than 2 seconds the window sets one single check for the moment it turns long, and nothing is scheduled while no run exists.

- Taskbar progress: the long run that changes the system, else the oldest long run. Green with its share, indeterminate while it cannot tell, yellow while it stops. Red when a run failed while the window was not in front; it clears when the window comes to the front.
- Taskbar badge, only two: "A run failed" (until the window is in front) and "Restart needed" (until Winora ends). Each has a text for screen readers. The badge is drawn in the status colors of the theme, and in the system highlight colors in high contrast.
- Finish notice: when a long run ends with a result other than Stopped while the window is not in front. It is a notice of the notification area icon; a click opens the page of the run. A run that ended well is told only when the setting for completion notices is on; a failure is always told.
- Notification area icon: while the window is closed and a long run or a system change goes on, and while a finish notice is up. Its tool tip and menu show the run and its share; the menu has Open, the run, Stop and Exit. Exit while a run goes asks first, then asks every run to stop and ends Winora when none is left. A run without Stop ends first.
- Closing the window during a long run or a system change hides it; Winora ends when that run is over. With the setting "Keep in the notification area when the window closes" (off by default) closing always hides the window, and the icon offers Open and Exit.

Tests: `ActivityServiceTests`, `TaskbarTrayModelTests`, `TaskbarTrayTests`, `ActionCenterViewModelTests`, `ActionCenterUiTests`, and one test per run kind in the page test classes (they use `RunProbe`).
