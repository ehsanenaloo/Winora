// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application.Activity;

namespace Winora.Desktop;

public enum TaskbarProgressKind { None, Normal, Paused, Error, Indeterminate }

public enum TaskbarBadge { None, Failed, RestartNeeded }

public enum WindowPresence { Front, Behind, Minimized, Hidden }

public enum CloseOutcome { Exit, HideToTray }

public enum TrayCommand { Open, RunInfo, Stop, Exit, Separator }

public sealed record TrayMenuEntry(TrayCommand Command, string Text, bool Enabled);

/// <summary>A Windows notice when a long run ended while the window was not in front. A click opens <see cref="PageKey"/>.</summary>
public sealed record FinishNotice(string Title, string Text, string PageKey, RunResult Result);

/// <summary>
/// What Winora shows outside its window: taskbar progress, the taskbar badge, the notification area icon and its menu, and the finish notice. Everything comes from the
/// <see cref="ActivityService"/>; no page talks to the taskbar or the tray. A run counts as long after <see cref="LongRun"/>; shorter runs never touch the taskbar, so page reads do not flash it.
/// The model holds no timer: <see cref="NextCheck"/> tells the window when a running run turns long, and it is null when nothing runs.
/// </summary>
public sealed class TaskbarTrayModel : IDisposable
{
    public static readonly TimeSpan LongRun = TimeSpan.FromSeconds(2);

    private readonly LocalizationService text;
    private readonly ActivityService activity;
    private readonly Func<bool> keepInTray;
    private readonly Func<bool> notifyDone;
    private readonly TimeProvider time;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;

    private HashSet<Guid> running = [];
    private WindowPresence presence = WindowPresence.Front;
    private bool unseenFailure, restartNeeded, noticeShowing, exitPending, exitRaised, closedToTray;

    // Hidden because the person closed it. A window that is hidden only because it was not shown yet (start up) must not end the app.
    private bool HiddenByClose => presence == WindowPresence.Hidden && closedToTray;

    /// <param name="keepInTray">The setting "Keep in the notification area when the window closes".</param>
    /// <param name="notifyDone">The setting that also tells about runs that ended well. A failure is always told.</param>
    public TaskbarTrayModel(LocalizationService text, ActivityService activity, Func<bool> keepInTray, Func<bool>? notifyDone = null, TimeProvider? time = null)
    {
        this.text = text; this.activity = activity; this.keepInTray = keepInTray; this.notifyDone = notifyDone ?? (static () => true); this.time = time ?? TimeProvider.System;
        activity.RunsChanged += OnRunsChanged;
        activity.NotificationPosted += OnPosted;
        text.PropertyChanged += OnLanguage;
        Update();
    }

    public void Dispose()
    {
        activity.RunsChanged -= OnRunsChanged;
        activity.NotificationPosted -= OnPosted;
        text.PropertyChanged -= OnLanguage;
    }

    /// <summary>Raised on the thread that created the model after any state below changed.</summary>
    public event EventHandler? Changed;
    public event EventHandler<FinishNotice>? NoticeRequested;
    /// <summary>The app should end now: an Exit was confirmed and the runs have stopped, or the window was closed during a run and that run is over.</summary>
    public event EventHandler? ExitRequested;

    public TaskbarProgressKind ProgressKind { get; private set; }
    /// <summary>0 to 1. Only meaningful for Normal, Paused and Error.</summary>
    public double ProgressValue { get; private set; }
    /// <summary>The long run the taskbar and the tray show: a run that changes the system first, then the oldest one.</summary>
    public RunSnapshot? ShownRun { get; private set; }
    /// <summary>When the next running run turns long, so the window can check again once. Null while nothing runs or every run is already long.</summary>
    public TimeSpan? NextCheck { get; private set; }
    public WindowPresence Presence => presence;
    public bool ExitPending => exitPending;
    public bool NoticeShowing => noticeShowing;

    public TaskbarBadge Badge => unseenFailure ? TaskbarBadge.Failed : restartNeeded ? TaskbarBadge.RestartNeeded : TaskbarBadge.None;
    /// <summary>What a screen reader says for the badge.</summary>
    public string BadgeDescription => Badge switch
    {
        TaskbarBadge.Failed => text["TaskbarBadgeFailed"],
        TaskbarBadge.RestartNeeded => text["TaskbarBadgeRestart"],
        _ => "",
    };

    /// <summary>
    /// The notification area icon shows while the window is hidden and something needs it (a long run, a pending exit, the setting), and while a finish notice is up.
    /// It never shows while Winora is idle with the window open.
    /// </summary>
    public bool TrayVisible => noticeShowing || HiddenByClose && (keepInTray() || ShownRun is not null || exitPending || HasKeepAliveRun());

    public string TrayToolTip => exitPending ? text["TrayStopping"] : RunLine ?? text["AppName"];

    /// <summary>"Disk scan: 45%" for the shown run.</summary>
    public string? RunLine => ShownRun is { } run ? text.Format("TrayRunFormat", run.Title, ShareText(run)) : null;

    public bool CanStop => ShownRun is { CanStop: true, State: RunState.Running } && !exitPending;

    /// <summary>Open, the run and its share, Stop, Exit. The run line and Stop appear only while a long run is going.</summary>
    public IReadOnlyList<TrayMenuEntry> Menu
    {
        get
        {
            var list = new List<TrayMenuEntry> { new(TrayCommand.Open, text["TrayOpen"], true) };
            if (RunLine is { } line)
            {
                list.Add(new(TrayCommand.Separator, "", false));
                list.Add(new(TrayCommand.RunInfo, line, false));
                list.Add(new(TrayCommand.Stop, text["TrayStop"], CanStop));
            }
            list.Add(new(TrayCommand.Separator, "", false));
            list.Add(new(TrayCommand.Exit, text["TrayExit"], !exitPending));
            return list;
        }
    }

    private string ShareText(RunSnapshot run) =>
        run.State == RunState.Stopping ? text["TrayStopping"] : run.Share is { } s ? text.Percent(Math.Round(s * 100)) : text["Working"];

    /// <summary>The window tells where it is. Coming to the front clears the failure badge: the person can see the result now.</summary>
    public void SetPresence(WindowPresence value)
    {
        if (value == WindowPresence.Front) unseenFailure = false;
        if (value != WindowPresence.Hidden) { exitRaised = false; closedToTray = false; }
        presence = value;
        Update();
    }

    /// <summary>The window is about to close. It hides to the notification area when the setting is on or a run must not be cut off: a long run or one that changes the system.</summary>
    public CloseOutcome WindowClosing()
    {
        var outcome = exitPending ? CloseOutcome.Exit : keepInTray() || HasKeepAliveRun() ? CloseOutcome.HideToTray : CloseOutcome.Exit;
        closedToTray = outcome == CloseOutcome.HideToTray;
        return outcome;
    }

    /// <summary>True when Exit must ask first because a run is going.</summary>
    public bool ExitNeedsConfirmation => activity.Runs.Count > 0;

    /// <summary>
    /// Exit was chosen (and confirmed while a run goes). Every run is asked to stop; the app ends when none is left. A run without Stop finishes first,
    /// so a change is never cut off in the middle.
    /// </summary>
    public void ConfirmExit()
    {
        exitPending = true;
        foreach (var run in activity.Runs) activity.RequestStop(run.Id);
        Update();
    }

    /// <summary>Exit was asked and then refused, or the window came back.</summary>
    public void CancelExit()
    {
        if (!exitPending) return;
        exitPending = false; exitRaised = false;
        Update();
    }

    /// <summary>Stop on the tray menu: asks the shown run to stop through the activity service.</summary>
    public bool StopShown() => ShownRun is { } run && !exitPending && activity.RequestStop(run.Id);

    /// <summary>The finish notice went away. A click opens the page; otherwise a window the person closed during the run lets the app end now.</summary>
    public void NoticeClosed(bool clicked)
    {
        if (!noticeShowing) return;
        noticeShowing = false;
        if (clicked) exitRaised = false;
        Update(allowExit: !clicked);
    }

    /// <summary>The window calls this at <see cref="NextCheck"/>.</summary>
    public void Refresh() => Update();

    private bool IsLong(RunSnapshot run, DateTimeOffset now) => now - run.StartedAt >= LongRun;

    private bool HasKeepAliveRun()
    {
        var now = time.GetUtcNow();
        return activity.Runs.Any(r => r.ChangesSystem || IsLong(r, now));
    }

    private void OnRunsChanged(object? sender, EventArgs e) => OnUi(() => Update());

    private void OnPosted(object? sender, ActivityNotification note)
    {
        if (note.Severity != NotificationSeverity.Restart) return;
        OnUi(() => { restartNeeded = true; Update(); });
    }

    private void OnLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnUi(() => Changed?.Invoke(this, EventArgs.Empty));

    private void OnUi(Action action)
    {
        if (context is null || Environment.CurrentManagedThreadId == ownerThread) action();
        else context.Post(_ => action(), null);
    }

    private void Update(bool allowExit = true)
    {
        var now = time.GetUtcNow();
        var runs = activity.Runs;
        foreach (var id in running.Where(id => runs.All(r => r.Id != id)).ToList())
            if (activity.Find(id) is { Result: { } } ended) Ended(ended);
        running = [.. runs.Select(r => r.Id)];

        ShownRun = runs.Where(r => IsLong(r, now)).OrderByDescending(r => r.ChangesSystem).ThenBy(r => r.StartedAt).FirstOrDefault();
        (ProgressKind, ProgressValue) = ShownRun switch
        {
            { State: RunState.Stopping } r => (TaskbarProgressKind.Paused, r.Share ?? 1),
            { Share: null } => (TaskbarProgressKind.Indeterminate, 0),
            { Share: { } s } => (TaskbarProgressKind.Normal, s),
            null when unseenFailure => (TaskbarProgressKind.Error, 1),
            _ => (TaskbarProgressKind.None, 0),
        };
        var waits = runs.Where(r => !IsLong(r, now)).Select(r => r.StartedAt + LongRun - now).ToList();
        NextCheck = waits.Count > 0 ? TimeSpan.FromTicks(Math.Max(0, waits.Min().Ticks)) : null;

        var exit = allowExit && !exitRaised && (exitPending && runs.Count == 0
            || HiddenByClose && !exitPending && !keepInTray() && !noticeShowing && !runs.Any(r => r.ChangesSystem || IsLong(r, now)));
        Changed?.Invoke(this, EventArgs.Empty);
        if (exit)
        {
            exitRaised = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Ended(RunSnapshot run)
    {
        if (run.Result == RunResult.Error && presence != WindowPresence.Front) unseenFailure = true;
        var tookLong = run.FinishedAt is { } end && end - run.StartedAt >= LongRun;
        if (!tookLong || run.Result is RunResult.Stopped or null || presence == WindowPresence.Front || exitPending) return;
        if (run.Result == RunResult.Ok && !notifyDone()) return;
        var title = text.Format(run.Result switch { RunResult.Error => "NoticeFailedFormat", RunResult.Warning => "NoticeWarningFormat", _ => "NoticeDoneFormat" }, run.Title);
        var page = text.Has(run.PageKey) ? text[run.PageKey] : run.PageKey;
        noticeShowing = true;
        NoticeRequested?.Invoke(this, new FinishNotice(title, text.Format("NoticeOpenFormat", page), run.PageKey, run.Result.Value));
    }
}
