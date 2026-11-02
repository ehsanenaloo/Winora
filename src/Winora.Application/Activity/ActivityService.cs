// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application.Activity;

/// <summary>
/// The run registry and the notification list behind the Action center. It has no timer and no UI types: a UI shows elapsed time from <see cref="RunSnapshot.StartedAt"/>, and the
/// change events fire only when something changes. Events are raised on the calling thread after the lock is released; a subscriber marshals to its own thread.
/// Only text, severity, time and page key of a notification are kept on disk, so after a restart an item can only open its page.
/// </summary>
public sealed class ActivityService
{
    private const int MaxFinished = 10;
    private readonly object gate = new();
    private readonly INotificationStore? store;
    private readonly TimeProvider time;
    private readonly List<Run> running = [];
    private readonly List<RunSnapshot> finished = [];
    private readonly List<ActivityNotification> notifications = [];

    public ActivityService(INotificationStore? store = null, TimeProvider? time = null)
    {
        this.store = store;
        this.time = time ?? TimeProvider.System;
        string? text = null;
        try { text = store?.Read(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var i in NotificationFile.Parse(text, this.time.GetUtcNow()))
            notifications.Add(new ActivityNotification(Guid.NewGuid(), i.Severity, i.Text, i.PageKey, i.At, IsRead: true));
    }

    public event EventHandler? RunsChanged;
    public event EventHandler? NotificationsChanged;
    /// <summary>Raised for each new notification, so a UI can show it as a short toast.</summary>
    public event EventHandler<ActivityNotification>? NotificationPosted;
    /// <summary>Raised when <see cref="TryStart"/> refuses a system-changing run because another one is running; the argument is the run in the way.</summary>
    public event EventHandler<RunSnapshot>? StartRefused;

    /// <summary>True when the last write of the notification list failed. The list stays in memory.</summary>
    public bool LastSaveFailed { get; private set; }

    private sealed class Run
    {
        public required Guid Id; public required string Title; public required string PageKey; public required bool ChangesSystem;
        public required DateTimeOffset StartedAt; public required Action? Stop; public double? Share; public bool Stopping;
        public RunSnapshot Snapshot(RunResult? result = null, DateTimeOffset? at = null, bool notified = false) =>
            new(Id, Title, PageKey, ChangesSystem, Share, Stop is not null, at is null ? (Stopping ? RunState.Stopping : RunState.Running) : RunState.Finished, StartedAt, result, at, notified);
    }

    public IReadOnlyList<RunSnapshot> Runs { get { lock (gate) return running.Select(r => r.Snapshot()).ToList(); } }
    /// <summary>The most recent finished runs, newest first.</summary>
    public IReadOnlyList<RunSnapshot> RecentRuns { get { lock (gate) return finished.ToList(); } }
    public RunSnapshot? LastFinished { get { lock (gate) return finished.Count > 0 ? finished[0] : null; } }
    public RunSnapshot? Find(Guid id) { lock (gate) return running.Where(r => r.Id == id).Select(r => r.Snapshot()).FirstOrDefault() ?? finished.FirstOrDefault(r => r.Id == id); }

    /// <summary>Notifications, newest first.</summary>
    public IReadOnlyList<ActivityNotification> Notifications { get { lock (gate) return notifications.AsEnumerable().Reverse().ToList(); } }
    public int UnreadCount { get { lock (gate) return notifications.Count(n => !n.IsRead); } }

    /// <summary>Starts a run. A run that changes the system is refused while another one is running; the refusal names it. Read-only runs never block or are blocked.</summary>
    public RunStartResult TryStart(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RunSnapshot snapshot;
        RunSnapshot? refused = null;
        lock (gate)
        {
            if (request.ChangesSystem && running.FirstOrDefault(r => r.ChangesSystem) is { } blocking) refused = blocking.Snapshot();
            var run = new Run
            {
                Id = Guid.NewGuid(), Title = NotificationFile.CleanText(request.Title), PageKey = NotificationFile.CleanPageKey(request.PageKey), ChangesSystem = request.ChangesSystem,
                StartedAt = time.GetUtcNow(), Stop = request.Stop, Share = Clamp(request.Share),
            };
            if (refused is null) running.Add(run);
            snapshot = run.Snapshot();
        }
        if (refused is not null)
        {
            StartRefused?.Invoke(this, refused);
            return new RunStartResult(null, refused);
        }
        RunsChanged?.Invoke(this, EventArgs.Empty);
        return new RunStartResult(snapshot, null);
    }

    /// <summary>Starts a run and returns its handle. A refused system-changing run gives a handle with <see cref="ActivityRun.Started"/> false.</summary>
    public ActivityRun Begin(RunRequest request) => new(this, TryStart(request));

    private static double? Clamp(double? share) => share is { } s && !double.IsNaN(s) ? Math.Clamp(s, 0, 1) : null;

    /// <summary>Sets the share (0..1) of a running run, or null to make it indeterminate. Returns false when the run is not running or nothing changed.</summary>
    public bool Report(Guid id, double? share)
    {
        lock (gate)
        {
            var run = running.FirstOrDefault(r => r.Id == id);
            var value = Clamp(share);
            if (run is null || run.Share == value) return false;
            run.Share = value;
        }
        RunsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Asks a run to stop through its callback. False when the run does not exist, has no Stop, was already asked, or the callback failed. The run ends when its owner calls <see cref="Finish"/>.</summary>
    public bool RequestStop(Guid id)
    {
        Action stop;
        lock (gate)
        {
            var run = running.FirstOrDefault(r => r.Id == id);
            if (run?.Stop is not { } s || run.Stopping) return false;
            stop = s;
            run.Stopping = true;
        }
        RunsChanged?.Invoke(this, EventArgs.Empty);
        try { stop(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            lock (gate) { var run = running.FirstOrDefault(r => r.Id == id); if (run is not null) run.Stopping = false; }
            RunsChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Ends a run and posts its notification: ok, warning and error runs by severity, a stopped run as information. <paramref name="message"/> is the notification text (the title when empty).
    /// <paramref name="notify"/> false ends the run without a notification (a read that finished quietly, a run the person stopped). Returns false when the run is not running.
    /// </summary>
    public bool Finish(Guid id, RunResult result, string? message = null, bool notify = true)
    {
        ActivityNotification? note = null;
        lock (gate)
        {
            var run = running.FirstOrDefault(r => r.Id == id);
            if (run is null) return false;
            running.Remove(run);
            finished.Insert(0, run.Snapshot(result, time.GetUtcNow(), notify));
            if (finished.Count > MaxFinished) finished.RemoveRange(MaxFinished, finished.Count - MaxFinished);
            if (notify)
            {
                var text = NotificationFile.CleanText(message);
                note = Add(result switch { RunResult.Ok => NotificationSeverity.Ok, RunResult.Warning => NotificationSeverity.Warning, RunResult.Error => NotificationSeverity.Error, _ => NotificationSeverity.Info },
                    text.Length > 0 ? text : run.Title, run.PageKey, null);
            }
        }
        RunsChanged?.Invoke(this, EventArgs.Empty);
        if (note is not null) Posted(note);
        return true;
    }

    /// <summary>Posts a notification for an event worth telling: a warning, an error, a restart that is needed, an offered undo. Plain confirmations are not posted here.</summary>
    public ActivityNotification? Notify(NotificationSeverity severity, string text, string pageKey, ActivityAction? action = null)
    {
        var clean = NotificationFile.CleanText(text);
        if (clean.Length == 0 || !Enum.IsDefined(severity)) return null;
        ActivityNotification note;
        lock (gate) note = Add(severity, clean, NotificationFile.CleanPageKey(pageKey), action);
        Posted(note);
        return note;
    }

    public ActivityNotification? NotifyRestartNeeded(string text, string pageKey) => Notify(NotificationSeverity.Restart, text, pageKey);

    public ActivityNotification? OfferUndo(string text, string pageKey, ActivityAction undo) => Notify(NotificationSeverity.Info, text, pageKey, undo);

    private ActivityNotification Add(NotificationSeverity severity, string text, string pageKey, ActivityAction? action)
    {
        var now = time.GetUtcNow();
        var note = new ActivityNotification(Guid.NewGuid(), severity, text, pageKey, now, IsRead: false, action);
        notifications.Add(note);
        Prune(now);
        Save();
        return note;
    }

    private void Prune(DateTimeOffset now)
    {
        var oldest = now - TimeSpan.FromDays(NotificationFile.MaxAgeDays);
        notifications.RemoveAll(n => n.At < oldest);
        if (notifications.Count > NotificationFile.MaxItems) notifications.RemoveRange(0, notifications.Count - NotificationFile.MaxItems);
    }

    private void Save()
    {
        if (store is null) return;
        try { store.Write(NotificationFile.Serialize(notifications.Select(n => new NotificationFile.Item(n.Severity, n.Text, n.PageKey, n.At)))); LastSaveFailed = false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastSaveFailed = true; }
    }

    private void Posted(ActivityNotification note)
    {
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
        NotificationPosted?.Invoke(this, note);
    }

    public bool MarkRead(Guid id, bool read = true) => Change(list =>
    {
        var i = list.FindIndex(n => n.Id == id);
        if (i < 0 || list[i].IsRead == read) return false;
        list[i] = list[i] with { IsRead = read };
        return true;
    }, persist: false);

    public bool MarkAllRead() => Change(list =>
    {
        var any = false;
        for (var i = 0; i < list.Count; i++) if (!list[i].IsRead) { list[i] = list[i] with { IsRead = true }; any = true; }
        return any;
    }, persist: false);

    public bool Remove(Guid id) => Change(list => list.RemoveAll(n => n.Id == id) > 0, persist: true);

    public bool ClearAll() => Change(list => { var any = list.Count > 0; list.Clear(); return any; }, persist: true);

    private bool Change(Func<List<ActivityNotification>, bool> edit, bool persist)
    {
        lock (gate)
        {
            if (!edit(notifications)) return false;
            if (persist) Save();
        }
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
