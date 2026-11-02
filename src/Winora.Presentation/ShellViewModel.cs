// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Desktop.Search;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>Navigation, shared notices and the page view models. Page logic lives in the page view models.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly Dictionary<string, PageViewModel> pages = [];
    private readonly Dictionary<string, int> pending = [];
    private readonly ErrorPresenter errors;
    private readonly IElevationState elevationState;
    /// <summary>The app runs with administrator rights; the title bar shows a badge.</summary>
    public bool IsElevated => elevationState.IsElevated;
    private DestinationInfo selected;
    private PageViewModel current;

    public ShellViewModel(SimulationModel model, LocalizationService text, SettingsViewModel settings, IReviewPrompt prompt, ErrorPresenter errors,
        InventoryModel inventory, SettingsModel settingsModel, ILogger<ShellViewModel> logger, IDiskScanner diskScanner, IFolderPicker folderPicker,

        ICleanupCatalog cleanupCatalog, ICleanupScanner cleanupScanner, CleanupRunner cleanupRunner, ICleanupJournal cleanupJournal, IElevationState elevation, PowerChangeService powerService, IPowerPlans powerPlans, OptimizeService optimizeService, BloatwareService bloatwareService, IStoreLauncher storeLauncher, StartupService startupService, IFileLocationOpener fileLocation, ScheduledTaskService taskService, ServiceManagerService serviceManager, NetworkService networkService, UpdatesService updatesService, IWindowsSettingsLauncher settingsLauncher, ProfileService profileService, IProfileFileAccess profileFiles, PermissionService permissionService, RepairService repairService, FeatureService featureService, IClipboardService clipboard, DashboardViewModel? dashboard = null, IRestorePointGate? restorePoint = null, SignatureService? signatures = null, ISettingCatalog? settingCatalog = null, RestorePointService? restorePointService = null, HistoryFeed? historyFeedOverride = null, IReadOnlyList<IHistorySource>? extraHistorySources = null, IQuarantineActions? quarantineActions = null, PerformanceService? performance = null, IAppIconSource? icons = null, AppUninstallService? appUninstall = null, LeftoverService? leftovers = null, ActivityService? activity = null)

    {
        Model = model; Text = text; Settings = settings; this.errors = errors; elevationState = elevation; SettingsModel = settingsModel;
        Feedback = new FeedbackCenter(text, clipboard);
        Activity = activity ?? new ActivityService();
        settingsModel.Runs.Service = Activity;
        RestoreActions = new RestorePointActions(text, restorePointService, prompt, settingsLauncher, () => Feedback, new RunSource("Overview") { Service = Activity });
        IReadOnlyDictionary<string, ServiceRecommendation>? serviceRecommendations = null;
        IReadOnlyDictionary<string, ServiceRecommendation> ServiceRecommendationMap()
        {
            if (serviceRecommendations is not null) return serviceRecommendations;
            var map = ServiceRecommendations.From(optimizeService.Definitions());
            if (map.Count > 0) serviceRecommendations = map;
            return map;
        }
        var historyFeed = historyFeedOverride ?? new HistoryFeed(
        [
            new SettingsHistorySource(settingsModel, text), new OptimizeHistorySource(optimizeService, text, elevation), new PowerHistorySource(powerService, text, elevation),
            new NetworkHistorySource(networkService, text, elevation), new TaskHistorySource(taskService, text, elevation), new StartupHistorySource(startupService, text, elevation),
            new BloatwareHistorySource(bloatwareService, text), new PermissionHistorySource(permissionService, text), new CleanupHistorySource(cleanupJournal, text),
            new RepairHistorySource(repairService, text), new ServiceHistorySource(serviceManager, text, elevation),
            .. appUninstall is null ? [] : new IHistorySource[] { new AppsHistorySource(appUninstall, text) },
            .. leftovers is null ? [] : new IHistorySource[] { new QuarantineHistorySource(leftovers, text) },
#if DEBUG
            new SimulationHistorySource(model, text),
#endif
            .. extraHistorySources ?? [],
        ], new HistoryBatchHandler(text, profileService, optimizeService, powerService));
        HistoryViewModel history = new(text, model, settingsModel, historyFeed, prompt, errors);
        var facts = new NextStepsFacts(elevation, restorePointService, settingsModel, optimizeService, inventory, startupService, signatures, dashboard, history);
        var nextSteps = new NextStepsViewModel(text, facts, RunNextStepAction, () => dashboard?.ReadUtc);
        OverviewViewModel overview = new(text, model, Navigate, new InventorySummaryViewModel(text, inventory, errors), () => Customize?.PendingCount ?? 0, dashboard, nextSteps, new RecentChangesViewModel(history, text, Navigate), new QuickProfileViewModel(text, profileService, ReviewProfile), new SetupScoreViewModel(text, settingsModel, () => Navigate("Customize")));
        var preferences = new PagePreferences(settings);
        ActionCenter = new ActionCenterViewModel(text, Activity, () => Pages.Select(p => p.Staging).OfType<StagingView>(), () => CurrentKey, Navigate, () => Navigate("History"), preferences, Feedback);
        CustomizeViewModel customize = new(text, model, settingsModel, preferences);
        history.SnapshotChanged += (_, _) => nextSteps.Request();
        Overview = overview; Customize = customize; History = history;
        if (performance is not null)
        {
            overview.Performance = new PerformanceCheckViewModel(text, performance, async token =>
            {
                try { var list = await startupService.ListAsync(token); return list.Items.Count(e => e.Item.Approval == StartupApproval.Enabled); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
            });
            overview.Performance.Runs.Service = Activity;
        }
        OptimizeViewModel optimize = new(text, model, optimizeService, prompt, elevation, restorePoint, preferences);
        Destinations =
        [
            new("Overview", PageIcons.For("Overview"), "Overview", NavSection.Home),
            new("Profiles", PageIcons.For("Profiles"), "Profiles", NavSection.Home),
            new("Customize", PageIcons.For("Customize"), "Customize", NavSection.Tune),
            new("Optimize", PageIcons.For("Optimize"), "Optimize", NavSection.Tune),
            new("Power", PageIcons.For("Power"), "Power", NavSection.Tune),
            new("Network", PageIcons.For("Network"), "Network", NavSection.Tune),
            new("Permissions", PageIcons.For("Permissions"), "Permissions", NavSection.Tune),
            new("Updates", PageIcons.For("Updates"), "Updates", NavSection.Tune),
            new("Cleanup", PageIcons.For("Cleanup"), "Cleanup", NavSection.Clean),
            new("Bloatware", PageIcons.For("Bloatware"), "Bloatware", NavSection.Clean),
            new("Startup", PageIcons.For("Startup"), "Startup", NavSection.Manage),
            new("ScheduledTasks", PageIcons.For("ScheduledTasks"), "ScheduledTasks", NavSection.Manage),
            new("Services", PageIcons.For("Services"), "Services", NavSection.Manage),
            new("Applications", PageIcons.For("Applications"), "Applications", NavSection.Manage),
            new("Tools", PageIcons.For("Tools"), "Tools", NavSection.Tools),
            new("History", PageIcons.For("History"), "History"),
            new("Settings", PageIcons.For("Settings"), "Settings"),
        ];
        foreach (var d in Destinations)
            pages[d.Key] = d.Page switch
            {
                "Overview" => overview, "Customize" => customize, "History" => history, "Settings" => settings,
                "Startup" => new StartupViewModel(text, model, errors, inventory, startupService, fileLocation, prompt, elevation, taskService, signatures, OpenScheduledTask, icons, preferences),
                "ScheduledTasks" => new ScheduledTasksViewModel(text, model, errors, taskService, prompt, elevation, icons, preferences, clipboard.TrySetText),
                "Services" => new ServicesViewModel(text, model, errors, inventory, serviceManager, prompt, elevation, ServiceRecommendationMap),
                "Applications" => new ApplicationsViewModel(text, model, errors, inventory, appUninstall, prompt, icons, leftovers),
                "Bloatware" => new BloatwareViewModel(text, model, bloatwareService, prompt, elevation, storeLauncher, icons),
                "Power" => new PowerViewModel(text, model, powerService, powerPlans, prompt, elevation, restorePoint),
                "Network" => new NetworkViewModel(text, model, networkService, prompt),
                "Updates" => new UpdatesViewModel(text, model, updatesService, prompt, elevation, settingsLauncher, restorePoint),
                "Profiles" => new ProfilesViewModel(text, model, profileService, prompt, elevation, profileFiles, [customize, optimize]),
                "Permissions" => new PermissionsViewModel(text, model, permissionService, prompt, preferences),
                "Tools" => new ToolsViewModel(text, model, repairService, featureService, prompt, clipboard) { WaitChoice = () => settings.SlowToolWait },
                "Optimize" => optimize,

                "Cleanup" => new CleanupViewModel(text, model, new FreeSpaceViewModel(text, cleanupCatalog, cleanupScanner, cleanupRunner, cleanupJournal, prompt, elevation, fileLocation, dashboard is null ? null : dashboard.ReadDrivesNow), new DiskUsageViewModel(text, diskScanner, folderPicker, null, dashboard is null ? null : dashboard.ReadDrivesNow, fileLocation, clipboard), new QuarantineViewModel(text, model, errors, inventory, prompt, quarantineActions, leftovers)),
                _ => new PlannedViewModel(d.Key, d.Icon, text, model)
            };
        selected = Destinations[0]; current = pages["Overview"];
        LoadSections();
        FirstRun = new FirstRunViewModel(text, settings, profileService, restorePoint, restorePointService, RestoreActions, elevation, prompt, () => Feedback, Navigate);
        FirstRun.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(FirstRunViewModel.IsActive)) { OnPropertyChanged(nameof(ShowShell)); ActionCenter.ShellHidden = FirstRun.IsActive; } };
        Search = new(text, () => SearchSeeds.Pages(Destinations).Concat(SearchSeeds.UserSettings(settingCatalog?.All ?? [])).Concat(SearchSeeds.MachineSettings(optimizeService.Definitions()))
            .Concat(SearchSeeds.Profiles(profileService.Load().BuiltIn)).Concat(SearchSeeds.Tools()).Concat(SearchSeeds.Actions()), OpenSearchResult);
        settingsModel.Journal = Journal;
        FirstRun.Journal.Signal = Journal;
        if (overview.QuickProfile is { } quick) quick.Journal.Signal = Journal;
        foreach (var d in Destinations)
        {
            var key = d.Key; var page = pages[key];
            page.Feedback = Feedback; page.SectionKey = d.SectionKey;
            page.Runs.Service = Activity;
            page.Journal.Signal = Journal;
            page.OpenHistory = area => { history.ShowArea(area); Navigate("History"); };
            // The Quarantine tab is a part of the Cleanup page: it shares the page's toasts, journal signal and History link.
            if (page is CleanupViewModel cleanup)
            {
                cleanup.SetAside.Feedback = Feedback; cleanup.SetAside.Journal.Signal = Journal; cleanup.SetAside.OpenHistory = page.OpenHistory;
                cleanup.SetAside.Runs.Service = Activity; cleanup.SetAside.Runs.PageKey = key;
                cleanup.FreeSpace.Runs = cleanup.Runs; cleanup.DiskUsage.Runs = cleanup.Runs;
            }
            pending[key] = page.PendingCount;
            page.PropertyChanged += (_, e) => { RefreshPending(key); if (StagingNames.Contains(e.PropertyName ?? "")) ActionCenter.RefreshStaging(); if (page is INoticePage noticePage && e.PropertyName is "Notice" or "NoticeIsError") ToastNotice(noticePage); };
        }
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SimulationModel.IsBusy)) OnPropertyChanged(nameof(IsBusy)); };
        settingsModel.Feedback = Feedback;
        settingsModel.Notice.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(SettingsNoticeVisible)); ToastSettingsNotice(); };
        settingsModel.Changed += (_, _) => { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(ShowProgress)); };
        model.Changed += (_, _) => { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(ShowProgress)); OnPropertyChanged(nameof(OutcomeOpen)); NotifyOutcome(); };
        text.PropertyChanged += (_, _) => { OnPropertyChanged(string.Empty); };
        logger.LogInformation("Shell ready with {Count} destinations", Destinations.Count);
    }

    public SimulationModel Model { get; }
    public SettingsModel SettingsModel { get; }
    /// <summary>The runs and the notifications of the whole app. Pages register their long runs here; the Action center shows them.</summary>
    public ActivityService Activity { get; }
    /// <summary>The Action center: staged changes of every page, runs, notifications, the title bar button, the status strip and the marks in the navigation.</summary>
    public ActionCenterViewModel ActionCenter { get; }
    private static readonly HashSet<string> StagingNames = ["", "PendingCount", "PendingLines", "PendingNames", "SelectedNames", "SelectedTitles", "ActionBarSummary", "PendingSummaryText", "SelectionText", "SelectionBarText", "Staging"];
    /// <summary>Raised after every apply or undo, so pages that read once read again when they open next.</summary>
    public JournalSignal Journal { get; } = new();
    /// <summary>The first-run guide. While it shows, the navigation and the page are hidden.</summary>
    public FirstRunViewModel FirstRun { get; }
    public bool ShowShell => !FirstRun.IsActive;
    /// <summary>Create a restore point, open System Protection, explain how to start as administrator.</summary>
    public RestorePointActions RestoreActions { get; }
    /// <summary>The toast and the result report of the content area.</summary>
    public FeedbackCenter Feedback { get; }
    private readonly Dictionary<INoticePage, string> toasted = [];

    /// <summary>
    /// A success notice of a page is shown as a toast. The page sets the text and the error flag one after the other, so the decision waits until both are set.
    /// A failure stays as the page's banner and is not repeated here.
    /// </summary>
    private void ToastNotice(INoticePage page)
    {
        if (string.IsNullOrEmpty(page.Notice)) { toasted.Remove(page); return; }
        if (!page.NoticeIsError && !Settings.NotifyOnCompletion) return; // a failure is always shown
        var ui = HostServices.UiThread;
        if (!ui.IsAvailable) return;
        ui.PostBackground(() =>
        {
            if (page.Notice is not { Length: > 0 } notice || page.NoticeIsError || Feedback.HasReport) return;
            if (toasted.TryGetValue(page, out var last) && last == notice) return;
            toasted[page] = notice;
            Feedback.Success(notice);
        });
    }
    public SettingsNotice SettingsNotice => SettingsModel.Notice;
    public bool ShowSettingsNotice => current is CustomizeViewModel or HistoryViewModel;
    /// <summary>A success notice is a toast; everything else (warnings, errors, pending restart, actions) stays as the banner.</summary>
    public bool SettingsNoticeVisible => ShowSettingsNotice && SettingsModel.Notice.IsOpen && (SettingsModel.Notice.Severity != BannerSeverity.Success || SettingsModel.Notice.HasAction);
    private string toastedSettingsNotice = "";

    private void ToastSettingsNotice()
    {
        var notice = SettingsModel.Notice;
        if (!notice.IsOpen) { toastedSettingsNotice = ""; return; }
        if (notice.Severity != BannerSeverity.Success || notice.HasAction) return;
        var key = notice.Code + "|" + notice.Message;
        if (key == toastedSettingsNotice) return;
        toastedSettingsNotice = key;
        if (!Feedback.HasReport) Feedback.Success(notice.Message);
    }
    public LocalizationService Text { get; }
    public SettingsViewModel Settings { get; }
    public OverviewViewModel Overview { get; }
    public CustomizeViewModel Customize { get; }
    public HistoryViewModel History { get; }
    public IReadOnlyList<DestinationInfo> Destinations { get; }
    public IReadOnlyList<PageViewModel> Pages => (object?)Destinations is null ? [] : Destinations.Select(d => pages[d.Key]).ToList();

    /// <summary>Raised when the number of staged changes of a page changes (key, new count). The navigation shows a dot while it is above zero.</summary>
    public event Action<string, int>? PendingChanged;
    public int PendingFor(string key) => pending.GetValueOrDefault(key);
    /// <summary>Staged changes on all pages that have not been applied or discarded.</summary>
    public int TotalPending => pending.Values.Sum();
    private void RefreshPending(string key)
    {
        var count = pages[key].PendingCount;
        if (pending.GetValueOrDefault(key) == count) return;
        pending[key] = count;
        PendingChanged?.Invoke(key, count);
    }

    // ---- collapsible navigation sections ----

    public const string SectionsPreference = "nav.sections";
    private readonly Dictionary<NavSection, bool> expanded = [];

    /// <summary>Raised when a section opens or closes. The window shows or hides the items of the section.</summary>
    public event Action<NavSection>? SectionExpansionChanged;

    /// <summary>Sections the user opened or closed are remembered per user; the others start open only when the current page is inside them.</summary>
    private void LoadSections()
    {
        foreach (var part in (Settings.ReadPreference(SectionsPreference) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split(':');
            if (pair.Length == 2 && Enum.TryParse<NavSection>(pair[0], out var section) && section != NavSection.Footer && pair[1] is "0" or "1") expanded[section] = pair[1] == "1";
        }
        foreach (var section in Enum.GetValues<NavSection>().Where(s => s != NavSection.Footer && !expanded.ContainsKey(s))) expanded[section] = section == selected.Section;
    }

    public bool IsSectionExpanded(NavSection section) => section == NavSection.Footer || expanded.GetValueOrDefault(section, true);

    /// <summary>The user opened or closed a section; the choice is stored.</summary>
    public void SetSectionExpanded(NavSection section, bool value)
    {
        if (section == NavSection.Footer || IsSectionExpanded(section) == value) return;
        expanded[section] = value;
        SectionExpansionChanged?.Invoke(section);
        _ = Settings.WritePreferenceAsync(SectionsPreference, string.Join(';', expanded.OrderBy(p => p.Key).Select(p => $"{p.Key}:{(p.Value ? 1 : 0)}")));
    }

    /// <summary>The first page of a section (Ctrl+1 to Ctrl+5).</summary>
    /// <summary>
    /// Runs a global shortcut that only changes the state of the app. True when the key was consumed. The shortcuts that need the window (find, help, regions, search, undo) are handled there.
    /// </summary>
    public bool RunShortcut(string? id)
    {
        switch (id)
        {
            case null: return false;
            case Shortcuts.Refresh: RefreshCurrentPage(); return true;
            case Shortcuts.Back: GoBack(); return true;
            case Shortcuts.Forward: GoForward(); return true;
            case Shortcuts.Settings: Navigate("Settings"); return true;
            case Shortcuts.Confirm: return ActionCenter.TryReview();
            case Shortcuts.ActionCenter: ActionCenter.Toggle(); return true;
            default:
                if (id.StartsWith(Shortcuts.Sections + ":", StringComparison.Ordinal) && int.TryParse(id[(Shortcuts.Sections.Length + 1)..], out var index) && index is >= 0 and < 5)
                {
                    Navigate(FirstPageOf((NavSection)index));
                    return true;
                }
                return false;
        }
    }

    /// <summary>Ctrl+Z on the History page selects the newest entry that can be undone and opens its undo review; it never undoes anything without that review. False on any other page.</summary>
    public bool TryUndoNewest()
    {
        if (CurrentPage is not HistoryViewModel history) return false;
        if (history.UndoNewestCommand.CanExecute(null)) history.UndoNewestCommand.Execute(null);
        return true;
    }

    public string FirstPageOf(NavSection section) => Destinations.First(d => d.Section == section).Key;

    private void RunNextStepAction(NextStepAction action)
    {
        switch (action)
        {
            case NextStepAction.OpenCleanup: Navigate("Cleanup"); break;
            case NextStepAction.OpenHistory: Navigate("History"); break;
            case NextStepAction.OpenStartup: Navigate("Startup"); break;
            case NextStepAction.OpenProfiles: Navigate("Profiles"); break;
            case NextStepAction.Scan: if (Overview.ScanCommand.CanExecute(null)) Overview.ScanCommand.Execute(null); break;
            case NextStepAction.CreateRestorePoint: _ = CreateRestorePointThenRefreshAsync(); break;
            case NextStepAction.OpenSystemProtection: RestoreActions.OpenProtection(); break;
            case NextStepAction.ShowAdminHelp: RestoreActions.ShowAdminHelp(); break;
        }
    }

    /// <summary>Opens the Profiles page with the plan of one profile (the Quick profile card of Overview). Nothing is applied until the person confirms there.</summary>
    public void ReviewProfile(string profileId)
    {
        Navigate("Profiles");
        if (pages["Profiles"] is ProfilesViewModel profiles) profiles.ReviewProfile(profileId);
    }

    private async Task CreateRestorePointThenRefreshAsync()
    {
        await RestoreActions.CreateAsync().ConfigureAwait(true);
        Overview.NextSteps?.Request();
    }

    // ---- global search ----

    public GlobalSearchModel Search { get; }
    private string? pendingHighlight;

    /// <summary>Raised when the page that is already open should highlight an item.</summary>
    public event Action? HighlightRequested;

    /// <summary>What the search asked the page to show; the window takes it once after the page opened.</summary>
    public string? TakePendingHighlight() { var target = pendingHighlight; pendingHighlight = null; return target; }

    /// <summary>Opens the page of a search result and asks it to highlight the item. The search itself changes nothing.</summary>
    public void OpenSearchResult(SearchSeed seed)
    {
        pendingHighlight = seed.TargetId;
        if (selected.Key == seed.PageKey) HighlightRequested?.Invoke();
        else Navigate(seed.PageKey);
    }

    public DestinationInfo Selected => selected;
    private string CurrentKey => (object?)selected is null ? "" : selected.Key;
    public PageViewModel CurrentPage => current;
    public string Theme => Settings.Theme;
    public bool IsBusy => Model.IsBusy || SettingsModel.IsBusy;
    public bool ShowProgress => Model.IsBusy || SettingsModel.ShowProgress;

    private readonly Stack<string> back = [];
    // Pages left with Back, newest on top. A new navigation clears it, like a browser.
    private readonly Stack<string> forward = [];
    private const int MaxBackDepth = 32;

    /// <summary>Raised after the user moved to another page (also by Back). The window moves keyboard focus to the new page heading.</summary>
    public event Action<string>? Navigated;
    public bool CanGoBack => back.Count > 0;
    public bool CanGoForward => forward.Count > 0;

    public void Navigate(string key)
    {
        var previous = selected.Key;
        if (!Move(key)) return;
        Push(back, previous);
        forward.Clear();
        OnHistoryChanged();
        Navigated?.Invoke(key);
    }

    private static void Push(Stack<string> stack, string key)
    {
        stack.Push(key);
        if (stack.Count > MaxBackDepth) { var kept = stack.Take(MaxBackDepth).Reverse().ToArray(); stack.Clear(); foreach (var k in kept) stack.Push(k); }
    }

    private void OnHistoryChanged() { OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoForward)); }

    /// <summary>Opens the Scheduled Tasks page with this task selected (used by the Startup page for task rows).</summary>
    public void OpenScheduledTask(string taskPath)
    {
        if (pages.TryGetValue("ScheduledTasks", out var page) && page is ScheduledTasksViewModel tasks) tasks.SelectTask(taskPath);
        Navigate("ScheduledTasks");
    }

    /// <summary>Alt+Left: returns to the page the user came from; the page left goes on the forward stack. False when there is nowhere to go back to.</summary>
    public bool GoBack() => Step(back, forward);

    /// <summary>Alt+Right: opens the page the user left with Back. False when there is nothing to go forward to.</summary>
    public bool GoForward() => Step(forward, back);

    private bool Step(Stack<string> from, Stack<string> to)
    {
        var leaving = selected.Key;
        while (from.Count > 0)
        {
            var key = from.Pop();
            if (Move(key)) { Push(to, leaving); OnHistoryChanged(); Navigated?.Invoke(key); return true; }
        }
        OnHistoryChanged();
        return false;
    }

    private bool Move(string key)
    {
        var destination = Destinations.FirstOrDefault(d => d.Key == key);
        if (destination is null || destination == selected) return false;
        // Leaving a page cancels its scan; arriving at a page lets it resume or start one (read-only).
        current.Deactivate();
        selected = destination; current = pages[key];
        if (!IsSectionExpanded(destination.Section)) { expanded[destination.Section] = true; SectionExpansionChanged?.Invoke(destination.Section); }
        current.Activate();
        ActionCenter.PageOpened(key);
        OnPropertyChanged(nameof(Selected)); OnPropertyChanged(nameof(CurrentPage)); OnPropertyChanged(nameof(ShowSettingsNotice)); OnPropertyChanged(nameof(SettingsNoticeVisible));
        return true;
    }

    /// <summary>Starts the page that is showing when the window opens (the first page is never entered through <see cref="Navigate"/>).</summary>
    public void ActivateCurrentPage() => current.Activate();

    /// <summary>F5: runs the current page's own read-only refresh. False when the page has none or it is busy.</summary>
    public bool RefreshCurrentPage()
    {
        if (current.ShortcutRefresh() is not { } command || !command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
    }

    /// <summary>Errors and warnings interrupt (assertive); everything else is announced politely.</summary>
    public bool OutcomeIsUrgent => OutcomeSeverity is BannerSeverity.Error or BannerSeverity.Warning;

    public bool OutcomeOpen { get => Model.Outcome is not null; set { if (!value && Model.Outcome is not null) Model.DismissOutcome(); } }
    public string OutcomeTitle => Model.Outcome switch
    {
        null => "",
        { Error: { } e } => Text[e.Severity is ErrorSeverity.Warning or ErrorSeverity.Info ? "ErrorTitleWarning" : "ErrorTitleFailed"],
        { State: { } s } => Text["State" + s],
        _ => Text["ActionFailedTitle"]
    };
    public string OutcomeMessage
    {
        get
        {
            if (Model.Outcome is not { } o) return "";
            if (o.Error is { } e) return $"{errors.Message(e)} {errors.Reference(e)}";
            var key = "Code_" + o.Code.Replace('.', '_');
            return Text.Has(key) ? Text[key] : Text["UnknownResult"];
        }
    }
    public BannerSeverity OutcomeSeverity => Model.Outcome switch
    {
        null => BannerSeverity.Informational,
        { Error: { Severity: ErrorSeverity.Warning } } => BannerSeverity.Warning,
        { Error: { Severity: ErrorSeverity.Info } } => BannerSeverity.Informational,
        { Error: not null } => BannerSeverity.Error,
        { State: OperationState.Failed or OperationState.Unsupported } => BannerSeverity.Error,
        { IsError: true } => BannerSeverity.Warning,
        { State: OperationState.AlreadyConfigured } => BannerSeverity.Informational,
        _ => BannerSeverity.Success
    };
    /// <summary>Transient failures offer to repeat the action; a stale review offers to review again. Other outcomes only explain.</summary>
    public string? OutcomeActionLabel => Model.Outcome switch
    {
        { Retry: not null } => Text["TryAgain"],
                _ => null
    };
    public bool OutcomeHasAction => OutcomeActionLabel is not null;
    public async Task RunOutcomeActionAsync()
    {
        if (Model.Outcome is not { } o) return;
        try
        {
            if (o.Retry is { } retry) { Model.DismissOutcome(); await retry(); }
            
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Model.Fail(ex, "Outcome action"); }
    }
    public void DismissOutcome() => Model.DismissOutcome();
    private void NotifyOutcome()
    {
        foreach (var name in new[] { nameof(OutcomeTitle), nameof(OutcomeMessage), nameof(OutcomeSeverity), nameof(OutcomeIsUrgent), nameof(OutcomeActionLabel), nameof(OutcomeHasAction) }) OnPropertyChanged(name);
    }
}