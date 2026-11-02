// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>
/// The state and the commands of the Action center: the staged changes of every page, the runs, the notifications, the title bar button, the status strip and the marks in the navigation.
/// It holds no timer: the window asks for <see cref="Tick"/> once a second only while the pane shows runs. Events of the activity service can come from any thread; they are moved to the
/// thread that created this object.
/// </summary>
public sealed class ActionCenterViewModel : ObservableObject, IDisposable
{
    public const int ChangesTab = 0, ActivityTab = 1;

    private readonly LocalizationService text;
    private readonly ActivityService activity;
    private readonly Func<IEnumerable<StagingView>> staging;
    private readonly Func<string> currentPage;
    private readonly Action<string> navigate;
    private readonly Action openHistory;
    private readonly IPagePreferences preferences;
    private readonly FeedbackCenter feedback;
    private readonly TimeProvider time;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;

    private readonly HashSet<string> failedPages = [];
    private readonly Dictionary<Guid, RunCardViewModel> cards = [];
    private readonly Dictionary<Guid, NoteViewModel> noteCards = [];
    private Guid lastFinishedHandled;
    private Guid unseenRunId;
    private RunResult? unseenResult;
    private bool suppressToast;
    private bool stagingPosted;
    private bool dockedOpen, drawerOpen, sized;
    private double width = 1120;
    private int selectedTab;
    private bool shellHidden;
    private bool hintShown;
    private string announcement = "";
    private int staged;
    private IReadOnlyList<StagingView> views = [];
    private int lastTotalStaged;

    public ActionCenterViewModel(LocalizationService text, ActivityService activity, Func<IEnumerable<StagingView>> staging, Func<string> currentPage, Action<string> navigate, Action openHistory,
        IPagePreferences preferences, FeedbackCenter feedback, TimeProvider? time = null)
    {
        this.text = text; this.activity = activity; this.staging = staging; this.currentPage = currentPage; this.navigate = navigate; this.openHistory = openHistory;
        this.preferences = preferences; this.feedback = feedback; this.time = time ?? TimeProvider.System;
        hintShown = preferences.Read(ActionCenterRules.HintPreference) == "1";
        dockedOpen = ActionCenterRules.StartsOpen(preferences.Read(ActionCenterRules.OpenPreference), width);
        ReviewCommand = new RelayCommand(Review, () => CanReview);
        DiscardCommand = new RelayCommand(DiscardAll, () => TotalStaged > 0 && views.Any(v => v.CanDiscard()));
        MarkAllReadCommand = new RelayCommand(() => activity.MarkAllRead(), () => activity.UnreadCount > 0);
        ClearAllCommand = new RelayCommand(() => activity.ClearAll(), () => Notes.Count > 0);
        ShowHistoryCommand = new RelayCommand(() => { openHistory(); CloseIfDrawer(); });
        ToggleCommand = new RelayCommand(Toggle);
        CloseCommand = new RelayCommand(Close);
        OpenCommand = new RelayCommand(OpenForStrip);
        RailOpenCommand = new RelayCommand(OpenFromRail);
        ViewActivityCommand = new RelayCommand(() => Open(ActivityTab, remember: false));
        StripStopCommand = new RelayCommand(StopStripRun, () => StripRun?.CanStop == true);
        activity.RunsChanged += OnRunsChanged;
        activity.NotificationsChanged += OnNotesChanged;
        activity.NotificationPosted += OnNotePosted;
        activity.StartRefused += OnStartRefused;
        feedback.Notified = OnToast;
        text.PropertyChanged += OnLanguage;
        RebuildRuns(); RebuildNotes(); RebuildChanges();
    }

    public void Dispose()
    {
        activity.RunsChanged -= OnRunsChanged;
        activity.NotificationsChanged -= OnNotesChanged;
        activity.NotificationPosted -= OnNotePosted;
        activity.StartRefused -= OnStartRefused;
        text.PropertyChanged -= OnLanguage;
        if (feedback.Notified == (Action<ToastItem>)OnToast) feedback.Notified = null;
    }

    // ---- threads ----

    private void OnUi(Action action)
    {
        if (context is null || Environment.CurrentManagedThreadId == ownerThread) action();
        else context.Post(_ => action(), null);
    }

    private void OnLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var card in cards.Values) card.Relabel();
        noteCards.Clear();
        RebuildChanges(); RebuildNotes(); RaiseAll();
    }

    // ---- open, close, layout ----

    /// <summary>The window tells the width the content really has (scaled text counts). It decides docked or drawer, the label of the button and the first start.</summary>
    public void SetWidth(double value)
    {
        value = Math.Max(0, value);
        var wasDrawer = IsDrawer;
        var wasShown = IsOpen;
        if (!sized) { sized = true; width = value; dockedOpen = ActionCenterRules.StartsOpen(preferences.Read(ActionCenterRules.OpenPreference), value); }
        else width = value;
        if (!wasDrawer && IsDrawer) drawerOpen = false;
        foreach (var n in new[] { nameof(IsDrawer), nameof(IsOpen), nameof(ShowBackdrop) }) OnPropertyChanged(n);
        if (wasShown != IsOpen) OpenChanged();
    }

    public PaneMode Mode => ActionCenterRules.Mode(width);
    public bool IsDrawer => Mode == PaneMode.Drawer;
    public bool IsOpen => ActionCenterRules.IsShown(Mode, dockedOpen, drawerOpen) && !shellHidden;
    public bool ShowBackdrop => IsDrawer && IsOpen;

    /// <summary>True while the first-run guide hides the shell: the pane, the button and the strip are not shown then.</summary>
    public bool ShellHidden { get => shellHidden; set { if (SetProperty(ref shellHidden, value)) { OpenChanged(); OnPropertyChanged(nameof(ShowButton)); } } }
    public bool ShowButton => !shellHidden;

    public int SelectedTab
    {
        get => selectedTab;
        set { if (SetProperty(ref selectedTab, value)) { OnPropertyChanged(nameof(ShowChangesTab)); OnPropertyChanged(nameof(ShowActivityTab)); if (value == ActivityTab && IsOpen) MarkSeen(); } }
    }
    public bool ShowChangesTab => selectedTab == ChangesTab;
    public bool ShowActivityTab => selectedTab == ActivityTab;

    public void Open(int tab, bool remember)
    {
        SelectedTab = tab;
        SetOpen(true, remember);
    }

    public void Close() => SetOpen(false, remember: true);

    public void Toggle() => SetOpen(!IsOpen, remember: true);

    private void CloseIfDrawer() { if (IsDrawer) SetOpen(false, remember: false); }

    private void SetOpen(bool open, bool remember)
    {
        var before = IsOpen;
        if (open) MarkHintShown();
        if (IsDrawer) drawerOpen = open;
        else
        {
            dockedOpen = open;
            if (remember) preferences.Write(ActionCenterRules.OpenPreference, ActionCenterRules.OpenValue(open));
        }
        foreach (var n in new[] { nameof(IsOpen), nameof(ShowBackdrop) }) OnPropertyChanged(n);
        if (before != IsOpen) OpenChanged();
    }

    private void OpenChanged()
    {
        if (IsOpen && selectedTab == ActivityTab) MarkSeen();
        RaiseStrip();
        RaiseRail();
        Opened?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(ButtonName));
    }

    /// <summary>Raised after the pane opened or closed, so the window can move the focus.</summary>
    public event EventHandler? Opened;

    private void OpenForStrip()
    {
        var tab = StripKind == StripKind.Staged ? ChangesTab : ActivityTab;
        Open(tab, remember: !IsDrawer);
    }

    private void OpenFromRail()
    {
        var tab = RailDot switch { RailDot.Pending => ChangesTab, RailDot.None => selectedTab, _ => ActivityTab };
        Open(tab, remember: true);
    }

    // ---- commands and state marks ----

    public ICommand ToggleCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand ViewActivityCommand { get; }

    public LocalizationService Text => text;
    public string Name => text["ACName"];
    public int TotalStaged => staged;
    public bool HasBadge => staged > 0;
    public string BadgeText => text.FormatNumber(staged);
    public ActionCenterIndicator Indicator => ActionCenterRules.Indicator(cards.Count, unseenResult, activity.UnreadCount);
    public bool IndicatorIsRunning => Indicator == ActionCenterIndicator.Running;
    public bool IndicatorIsSucceeded => Indicator == ActionCenterIndicator.Succeeded;
    public bool IndicatorIsWarning => Indicator == ActionCenterIndicator.Warning;
    public bool IndicatorIsUnread => Indicator == ActionCenterIndicator.Unread;
    public string IndicatorText => cards.Count > 1 ? text.FormatNumber(cards.Count) : "";
    public bool IndicatorShowsCount => cards.Count > 1;

    /// <summary>The name a screen reader reads for the rail and the pane, with the counts.</summary>
    public string ButtonName
    {
        get
        {
            var name = text["ACName"];
            if (staged > 0) name += ", " + text.Format("ACPendingShort", staged);
            if (cards.Count > 0) name += ", " + text.Format("ACRunningShort", cards.Count);
            if (activity.UnreadCount > 0) name += ", " + text.Format("ACNewShort", activity.UnreadCount);
            return name;
        }
    }
    public string ButtonToolTip => text["ACName"] + " (Ctrl+Shift+A)";

    // ---- rail ----

    /// <summary>The closed pane stays as a narrow rail at the inline end, docked and drawer alike. It hides while the pane is open and while the first-run guide hides the shell.</summary>
    public bool ShowRail => !IsOpen && !shellHidden;
    public ICommand RailOpenCommand { get; }
    public RailDot RailDot => ActionCenterRules.Dot(Indicator, staged);
    public bool HasRailDot => RailDot != RailDot.None;

    private void RaiseRail()
    {
        foreach (var n in new[] { nameof(ShowRail), nameof(RailDot), nameof(HasRailDot) }) OnPropertyChanged(n);
    }

    // ---- changes ----

    public ObservableCollection<ChangeGroupViewModel> Groups { get; } = [];
    public bool HasStaged => staged > 0;
    public bool ShowEmptyChanges => staged == 0;
    public RelayCommand ReviewCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public string SummaryText { get; private set; } = "";
    public string NoteText { get; private set; } = "";
    public string ScopeText { get; private set; } = "";
    public bool HasScope => ScopeText.Length > 0;
    public string ScopeNote => text["ACOtherPending"];
    public string ReviewText { get; private set; } = "";
    public string ReviewHelp { get; private set; } = "";
    public string DiscardText { get; private set; } = "";
    public string DiscardHelp { get; private set; } = "";
    public string BusyNote => text["ACBusyReview"];
    public string ViewText => text["ACView"];
    public bool ShowBusyNote => staged > 0 && SystemBusy;
    public bool SystemBusy => cards.Values.Any(c => c.ChangesSystem);

    private StagingView? ReviewView
    {
        get
        {
            var key = currentPage();
            return views.FirstOrDefault(v => v.PageKey == key) ?? views.FirstOrDefault();
        }
    }

    public bool CanReview => ReviewView is { } v && !SystemBusy && v.CanReview();

    /// <summary>Starts the review dialog of the page that the footer names. Ctrl+Enter calls it too.</summary>
    public bool TryReview()
    {
        if (!CanReview) return false;
        Review();
        return true;
    }

    private void Review()
    {
        if (ReviewView is not { } view || SystemBusy) return;
        CloseIfDrawer();
        view.Review();
    }

    private void DiscardAll()
    {
        foreach (var view in views.ToList()) if (view.CanDiscard()) view.Discard();
    }

    /// <summary>Called by the shell when the staged set of a page may have changed. Many changes in a row cost one rebuild.</summary>
    public void RefreshStaging()
    {
        if (stagingPosted) return;
        stagingPosted = true;
        OnUi(() =>
        {
            stagingPosted = false;
            RebuildChanges();
        });
    }

    private void RebuildChanges()
    {
        views = staging().Where(v => v.Changes.Count > 0).ToList();
        staged = views.Sum(v => v.Changes.Count);
        Groups.Clear();
        foreach (var view in views)
        {
            var title = text[view.PageKey];
            var items = view.Changes.Select(c => new ChangeItemViewModel(c, text.Format("ACDiscardChange", c.Title), text["ACBefore"], text["ACAfter"], c.Risk is { } r ? text.Enum(r) : "")).ToList();
            var key = view.PageKey;
            Groups.Add(new ChangeGroupViewModel(key, title, text.FormatNumber(items.Count), text.Format("ACOpenPage", title), items, new RelayCommand(() => { navigate(key); CloseIfDrawer(); })));
        }
        var single = views.Count == 1;
        var busy = SystemBusy;
        var reviewView = ReviewView;
        SummaryText = busy ? text["ACApplying"] : staged == 0 ? text["ACNoChanges"] : single ? views[0].Summary : text.Format("ABPendingFormat", staged);
        NoteText = busy ? text["ACApplyingNote"] : staged == 0 ? text["ACSelectToStart"] : text["ACNothingApplied"];
        ScopeText = views.Count > 1 && reviewView is not null
            ? text.Format("ACNext", text[reviewView.PageKey], reviewView.Changes.Count, staged)
            : "";
        ReviewText = single ? views[0].ReviewText : text["ABReview"];
        ReviewHelp = single ? views[0].ReviewHelp : text["ABReviewHelp"];
        DiscardText = single ? views[0].DiscardText : text["ACDiscardAll"];
        DiscardHelp = single ? views[0].DiscardHelp : text["ACDiscardAllHelp"];
        foreach (var n in new[] { nameof(TotalStaged), nameof(HasBadge), nameof(BadgeText), nameof(HasStaged), nameof(ShowEmptyChanges), nameof(SummaryText), nameof(NoteText), nameof(ScopeText), nameof(HasScope),
            nameof(ReviewText), nameof(ReviewHelp), nameof(DiscardText), nameof(DiscardHelp), nameof(ShowBusyNote), nameof(ButtonName), nameof(ChangesTabTitle) }) OnPropertyChanged(n);
        NotifyCommands();
        RaiseStrip();
        RaiseRail();
        if (lastTotalStaged == 0 && staged > 0) FirstStagedHint();
        lastTotalStaged = staged;
    }

    private void NotifyCommands()
    {
        ReviewCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged();
        MarkAllReadCommand.NotifyCanExecuteChanged(); ClearAllCommand.NotifyCanExecuteChanged(); StripStopCommand.NotifyCanExecuteChanged();
    }

    public string ChangesTabTitle => staged > 0 ? $"{text["ACTabChanges"]} {text.FormatNumber(staged)}" : text["ACTabChanges"];

    // ---- first-time hint ----

    /// <summary>Raised once per user when something is staged for the first time while the pane is closed, so the button can pulse.</summary>
    public event EventHandler? HintRequested;

    /// <summary>Records that the first-time hint is known, so it is not shown. Used when the person opens the pane on their own.</summary>
    public void MarkHintShown()
    {
        if (hintShown) return;
        hintShown = true;
        preferences.Write(ActionCenterRules.HintPreference, "1");
    }

    private void FirstStagedHint()
    {
        if (hintShown || IsOpen || shellHidden) return;
        hintShown = true;
        preferences.Write(ActionCenterRules.HintPreference, "1");
        HintRequested?.Invoke(this, EventArgs.Empty);
        feedback.Show(new ToastItem(ToastKind.Info, text["ACHintToast"], text["ACOpen"], () => { Open(ChangesTab, remember: !IsDrawer); return Task.CompletedTask; }, text["ACHintHelp"], quiet: true));
    }

    // ---- activity ----

    public ObservableCollection<RunCardViewModel> Runs { get; } = [];
    public ObservableCollection<NoteViewModel> Notes { get; } = [];
    public bool HasRuns => Runs.Count > 0;
    public bool ShowNoRuns => Runs.Count == 0;
    public bool HasNotes => Notes.Count > 0;
    public bool ShowNoNotes => Notes.Count == 0;
    public bool ShowOneAtATime => Runs.Any(r => r.ChangesSystem);
    public string RunningHeading => text["ACRunning"] + " " + text.FormatNumber(Runs.Count);
    public string RecentHeading => text["ACRecent"] + " " + text.FormatNumber(Notes.Count);
    public string ActivityTabTitle => Runs.Count + Notes.Count(n => n.IsNew) > 0 ? $"{text["ACTabActivity"]} {text.FormatNumber(Runs.Count + Notes.Count(n => n.IsNew))}" : text["ACTabActivity"];
    public RelayCommand MarkAllReadCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand ShowHistoryCommand { get; }

    /// <summary>Spoken by the live region when a run starts or ends.</summary>
    public string Announcement { get => announcement; private set => SetProperty(ref announcement, value); }

    private void OnRunsChanged(object? sender, EventArgs e) => OnUi(RebuildRuns);
    private void OnNotesChanged(object? sender, EventArgs e) => OnUi(() => { RebuildNotes(); if (IsOpen && selectedTab == ActivityTab) MarkSeen(); });

    private void OnStartRefused(object? sender, RunSnapshot blocking) => OnUi(() =>
        feedback.Show(new ToastItem(ToastKind.Warning, text["ACBusyToast"], text["ACView"], () => { Open(ActivityTab, remember: false); return Task.CompletedTask; }, text["ACViewHelp"], quiet: true)));

    private void RebuildRuns()
    {
        var current = activity.Runs;
        var announce = new List<string>();
        foreach (var run in current)
        {
            if (cards.TryGetValue(run.Id, out var card)) card.Update(run);
            else
            {
                card = new RunCardViewModel(run, text, text[run.PageKey], id => activity.RequestStop(id), key => { navigate(key); CloseIfDrawer(); });
                card.Tick(time.GetUtcNow());
                cards[run.Id] = card;
                announce.Add(text.Format("ACRunStarted", run.Title));
            }
        }
        foreach (var gone in cards.Keys.Where(id => current.All(r => r.Id != id)).ToList()) cards.Remove(gone);
        Sync(Runs, current.Select(r => cards[r.Id]).ToList());

        // A run that finished since the last look: remember the result for the corner mark and the navigation.
        var recent = activity.RecentRuns;
        var newlyFinished = recent.TakeWhile(r => r.Id != lastFinishedHandled).Reverse().ToList();
        if (recent.Count > 0) lastFinishedHandled = recent[0].Id;
        foreach (var run in newlyFinished)
        {
            if (run.Result is RunResult.Error or RunResult.Warning) failedPages.Add(run.PageKey);
            if (run.Notified && run.Result is not null) { unseenRunId = run.Id; unseenResult = run.Result; }
            if (run.Result == RunResult.Stopped) announce.Add(text.Format("ACRunStopped", run.Title));
            else if (run.Result is RunResult.Error) announce.Add(text.Format("ACRunFailed", run.Title));
            else if (run.Notified || run.Result is RunResult.Warning) announce.Add(text.Format("ACRunEnded", run.Title));
        }
        if (failedPages.Contains(currentPage())) failedPages.Remove(currentPage());
        if (announce.Count > 0)
        {
            var said = string.Join(". ", announce);
            Announcement = said == Announcement ? said + "\u200B" : said;
        }
        foreach (var n in new[] { nameof(HasRuns), nameof(ShowNoRuns), nameof(RunningHeading), nameof(ShowOneAtATime), nameof(SystemBusy), nameof(Indicator), nameof(IndicatorIsRunning), nameof(IndicatorIsSucceeded), nameof(IndicatorIsWarning), nameof(IndicatorIsUnread), nameof(IndicatorText), nameof(IndicatorShowsCount), nameof(ButtonName),
            nameof(ActivityTabTitle), nameof(ShowBusyNote), nameof(StripKind), nameof(StripRun), nameof(StripTitle), nameof(StripIsDeterminate), nameof(StripShare), nameof(StripShareText), nameof(StripCanStop) }) OnPropertyChanged(n);
        RebuildChanges();
        RaiseRail();
        MarksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildNotes()
    {
        var list = activity.Notifications;
        var seen = new HashSet<Guid>();
        var vms = new List<NoteViewModel>();
        foreach (var note in list)
        {
            seen.Add(note.Id);
            if (noteCards.TryGetValue(note.Id, out var vm)) vm.Update(note);
            else noteCards[note.Id] = vm = new NoteViewModel(note, text, note.PageKey.Length > 0 ? text[note.PageKey] : "", id => activity.Remove(id), key => { navigate(key); CloseIfDrawer(); });
            vms.Add(vm);
        }
        foreach (var gone in noteCards.Keys.Where(id => !seen.Contains(id)).ToList()) noteCards.Remove(gone);
        Sync(Notes, vms);
        foreach (var n in new[] { nameof(HasNotes), nameof(ShowNoNotes), nameof(RecentHeading), nameof(ActivityTabTitle), nameof(Indicator), nameof(IndicatorIsRunning), nameof(IndicatorIsSucceeded), nameof(IndicatorIsWarning), nameof(IndicatorIsUnread), nameof(ButtonName), nameof(StripKind), nameof(StripTitle), nameof(StripNoteLevel), nameof(StripMore), nameof(HasStripMore) })
            OnPropertyChanged(n);
        NotifyCommands();
        RaiseRail();
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
    {
        for (var i = target.Count - 1; i >= 0; i--) if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
            var at = target.IndexOf(wanted[i]);
            if (at >= 0) target.Move(at, i); else target.Insert(i, wanted[i]);
        }
    }

    /// <summary>Once a second while the pane or the strip shows runs: refreshes the elapsed times.</summary>
    public void Tick()
    {
        var now = time.GetUtcNow();
        foreach (var card in cards.Values) card.Tick(now);
    }

    /// <summary>The pane was opened on the Activity tab: everything that was new is seen.</summary>
    private void MarkSeen()
    {
        unseenResult = null; unseenRunId = Guid.Empty;
        activity.MarkAllRead();
        foreach (var n in new[] { nameof(Indicator), nameof(IndicatorIsRunning), nameof(IndicatorIsSucceeded), nameof(IndicatorIsWarning), nameof(IndicatorIsUnread), nameof(ButtonName) }) OnPropertyChanged(n);
        RaiseStrip();
        RaiseRail();
    }

    // ---- notifications and toasts ----

    private void OnNotePosted(object? sender, ActivityNotification note) => OnUi(() =>
    {
        if (suppressToast) return;
        if (IsOpen && selectedTab == ActivityTab) return;
        // A run that ends on another page: the page itself says nothing there, so the toast does, with a way to the details.
        if (note.PageKey == currentPage()) return;
        var kind = note.Severity switch { NotificationSeverity.Warning => ToastKind.Warning, NotificationSeverity.Error => ToastKind.Error, NotificationSeverity.Restart => ToastKind.RestartNeeded, NotificationSeverity.Ok => ToastKind.Success, _ => ToastKind.Info };
        feedback.Show(new ToastItem(kind, note.Text, text["ACView"], () => { Open(ActivityTab, remember: false); return Task.CompletedTask; }, text["ACViewHelp"], quiet: true));
    });

    /// <summary>A toast that is also a notification (warning, error, restart needed, any toast with an action) is copied into the list.</summary>
    private void OnToast(ToastItem toast)
    {
        var severity = toast.Kind switch
        {
            ToastKind.Warning => NotificationSeverity.Warning,
            ToastKind.Error => NotificationSeverity.Error,
            ToastKind.RestartNeeded => NotificationSeverity.Restart,
            ToastKind.Success => NotificationSeverity.Ok,
            _ => NotificationSeverity.Info,
        };
        suppressToast = true;
        try { activity.Notify(severity, toast.Text, currentPage()); }
        finally { suppressToast = false; }
    }

    // ---- navigation marks ----

    public event EventHandler? MarksChanged;

    public NavMark MarkFor(string pageKey)
    {
        if (cards.Values.Any(c => c.PageKey == pageKey)) return NavMark.Running;
        return failedPages.Contains(pageKey) ? NavMark.Failed : NavMark.None;
    }

    /// <summary>The person opened a page: its failure mark goes away, and the footer may now name this page.</summary>
    public void PageOpened(string pageKey)
    {
        if (failedPages.Remove(pageKey)) MarksChanged?.Invoke(this, EventArgs.Empty);
        RebuildChanges();
    }

    // ---- status strip ----

    public StripKind StripKind => shellHidden ? StripKind.None : ActionCenterRules.Strip(IsOpen, cards.Count, staged, activity.UnreadCount > 0 || unseenResult is not null);
    public bool ShowStrip => StripKind != StripKind.None;
    public bool StripIsRun => StripKind == StripKind.Run;
    public bool StripIsStaged => StripKind == StripKind.Staged;
    public bool StripIsNote => StripKind == StripKind.Notification;
    public bool StripShowsOpen => StripKind is StripKind.Run or StripKind.Staged;
    public RunCardViewModel? StripRun => Runs.FirstOrDefault(r => r.ChangesSystem) ?? Runs.FirstOrDefault();
    public RelayCommand StripStopCommand { get; }
    public bool StripCanStop => StripRun?.CanStop == true;
    public bool StripIsDeterminate => StripRun?.IsDeterminate == true;
    public double StripShare => StripRun?.Share ?? 0;
    public string StripShareText => StripRun?.ShareText ?? "";
    public string StripStopText => text["ACStop"];
    public string StripOpenText => text["ACOpen"];
    public string StripNothingApplied => text["ACNothingApplied"];

    public string StripTitle => StripKind switch
    {
        StripKind.Run => StripRun is { } r ? r.Title + (Runs.Count > 1 ? " +" + text.FormatNumber(Runs.Count - 1) : "") : "",
        StripKind.Staged => views.Count == 1 ? views[0].Summary : text.Format("ABPendingFormat", staged),
        StripKind.Notification => Notes.FirstOrDefault(n => n.IsNew) is { } n ? n.Text : Notes.FirstOrDefault()?.Text ?? text["ACFinished"],
        _ => "",
    };
    public NoteLevel StripNoteLevel => Notes.FirstOrDefault(n => n.IsNew)?.Level ?? Notes.FirstOrDefault()?.Level ?? NoteLevel.Info;
    public int StripMoreCount => Math.Max(0, Notes.Count(n => n.IsNew) - 1);
    public bool HasStripMore => StripMoreCount > 0;
    public string StripMore => "+" + text.FormatNumber(StripMoreCount);
    public string StripReviewText => ReviewText;
    public string StripViewText => text["ACView"];
    public string StripRegionName => text["ACStripName"];

    private void StopStripRun()
    {
        if (StripRun is { } run) activity.RequestStop(run.Id);
    }

    private void RaiseStrip()
    {
        foreach (var n in new[] { nameof(StripKind), nameof(ShowStrip), nameof(StripIsRun), nameof(StripIsStaged), nameof(StripIsNote), nameof(StripShowsOpen), nameof(StripRun), nameof(StripTitle), nameof(StripNoteLevel), nameof(StripMore), nameof(HasStripMore),
            nameof(StripCanStop), nameof(StripIsDeterminate), nameof(StripShare), nameof(StripShareText), nameof(StripReviewText) }) OnPropertyChanged(n);
        StripStopCommand.NotifyCanExecuteChanged();
    }

    private void RaiseAll() => OnPropertyChanged(string.Empty);
}
