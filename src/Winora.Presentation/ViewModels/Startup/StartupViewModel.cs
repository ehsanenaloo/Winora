// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>One line of the change history with its undo button.</summary>
public sealed class StartupHistoryRow(DateTimeOffset at, string description, string stateText, string when, bool canRevert, RelayCommand<StartupHistoryRow> revert, Func<Task<(bool Ok, string Code)>> undo, Guid id = default, bool ok = true)
{
    public Guid Id { get; } = id;
    public bool Ok { get; } = ok;
    public DateTimeOffset At { get; } = at;
    /// <summary>Undoes the change through the service that made it (the startup service or the scheduled task service).</summary>
    public Func<Task<(bool Ok, string Code)>> Undo { get; } = undo;
    public string Description { get; } = description;
    public string StateText { get; } = stateText;
    public string When { get; } = when;
    public bool CanRevert { get; } = canRevert;
    public RelayCommand<StartupHistoryRow> RevertCommand { get; } = revert;
    public string AutomationName => $"{Description}, {StateText}, {When}";
}

/// <summary>Notices above the list: the administrator hint, the short explanation and the result of the last change.</summary>
public sealed class StartupBannerPanel(StartupViewModel page) : ObservableObject
{
    public LocalizationService Text => page.Text;
    public string InfoTitle => page.Text["V2StInfoTitle"];
    public string InfoText => page.Text["V2StInfoText"];
    public string ImpactHeadline => page.ImpactHeadline;
    public string ImpactNote => page.Text["V2StImpactNote"];
    public bool HasImpact => page.ImpactCounts().Total > 0;
    public double HighCount => page.ImpactCounts().High;
    public double MediumCount => page.ImpactCounts().Medium;
    public double LowCount => page.ImpactCounts().Low;
    public string HighLabel => page.Text.Format("V2StImpactHighCount", page.ImpactCounts().High);
    public string MediumLabel => page.Text.Format("V2StImpactMediumCount", page.ImpactCounts().Medium);
    public string LowLabel => page.Text.Format("V2StImpactLowCount", page.ImpactCounts().Low);
    public string BarAutomationName => $"{page.ImpactHeadline} {HighLabel}, {MediumLabel}, {LowLabel}";
    public string ToggleAllLabel => page.ToggleAllLabel;
    public string ToggleAllHelp => page.Text["V2StAllHelp"];
    public System.Windows.Input.ICommand ToggleAllCommand => page.ToggleAllCommand;
    public bool HasGroups => page.ListItems.OfType<StartupCategoryRow>().Any();
    /// <summary>"24 items, 5 enabled, 2 need attention": counts only, no impact claim.</summary>
    public string SummaryText => page.SummaryText;
    public bool HasSummary => page.SummaryText.Length > 0;
    public bool ShowNeedsAdministrator => page.ShowNeedsAdministrator;
    public string NeedsAdministratorTitle => page.NeedsAdministratorTitle;
    public string NeedsAdministratorText => page.NeedsAdministratorText;
    public bool ShowAdminHowTo => page.ShowAdminHowTo;
    /// <summary>Only a failure is a banner; a success is a toast.</summary>
    public bool HasNotice => page.HasNotice && page.NoticeIsError;
    public string NoticeTitle => page.Text["StartupResult"];
    public string? Notice => page.Notice;
    public BannerSeverity NoticeSeverity => page.NoticeIsError ? BannerSeverity.Warning : BannerSeverity.Success;
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>The change history block below the list.</summary>
public sealed class StartupHistoryPanel(StartupViewModel page) : ObservableObject
{
    public PageHistoryModel List => page.HistoryList;
    public void Refresh() { OnPropertyChanged(string.Empty); page.HistoryList.Refresh(); }
}

/// <summary>
/// The Startup page: every program that starts with Windows (Run keys and Startup folders, plus scheduled tasks that start at logon or boot as read-only rows), with search, filters, facts about the file,
/// a signature check that runs in the background, and a button to turn an entry on or off. Turning an entry
/// off sets the Task Manager flag in the StartupApproved key; the Run value and the shortcut are never removed. Every change shows what will change, asks once, is recorded before it is made,
/// is read back afterwards and can be undone while nothing else changed it (decision DEC-IW-027).
/// </summary>
public sealed class StartupViewModel : DiscoveryPageViewModel<StartupItem>
{
    private const int HistoryLimit = 30;
    private readonly StartupService service;
    private readonly IFileLocationOpener opener;
    private readonly IReviewPrompt prompt;
    private readonly IElevationState elevation;
    private readonly ScheduledTaskService? taskService;
    private readonly SignatureService? signatures;
    private readonly Action<string>? openTask;
    private readonly StartupInputs inputs;
    private ConcurrentDictionary<string, ScheduledTaskInfo> taskInfos => inputs.Infos;
    private readonly ConcurrentDictionary<string, StartupEntry> entries = new(StringComparer.Ordinal);
    private readonly StartupBannerPanel banner;
    private readonly StartupHistoryPanel footer;
    private Task work = Task.CompletedTask;
    private Func<string>? noticeText;
    private bool noticeIsError;
    private CancellationTokenSource? signatureRun;
    private SignatureBatch? lastBatch;
    private bool busy;
    private readonly IAppIconSource? icons;
    private readonly Dictionary<string, bool> stagedStates = new(StringComparer.Ordinal);
    private ChoiceFilter statusFilter = null!;
    private ToggleFilter highFilter = null!;
    private readonly IPagePreferences? prefs;
    private readonly HashSet<StartupCategory> collapsed = [];
    private readonly Dictionary<StartupCategory, StartupCategoryRow> headers = [];
    private readonly ObservableCollection<object> listItems = [];
    private const string CollapsedKey = "startup.collapsed";

    public StartupViewModel(LocalizationService text, SimulationModel model, ErrorPresenter errors, InventoryModel inventory, StartupService service, IFileLocationOpener opener, IReviewPrompt prompt, IElevationState elevation,
        ScheduledTaskService? taskService = null, SignatureService? signatures = null, Action<string>? openTask = null, IAppIconSource? icons = null, IPagePreferences? preferences = null)
        : this(text, model, errors, inventory, service, opener, prompt, elevation, taskService, signatures, openTask, new StartupInputs(inventory, taskService is not null), icons, preferences) { }

    private StartupViewModel(LocalizationService text, SimulationModel model, ErrorPresenter errors, InventoryModel inventory, StartupService service, IFileLocationOpener opener, IReviewPrompt prompt, IElevationState elevation,
        ScheduledTaskService? taskService, SignatureService? signatures, Action<string>? openTask, StartupInputs inputs, IAppIconSource? icons, IPagePreferences? preferences)
        : base("Startup", IconKey.Rocket24, text, model, errors, inputs.Slot)
    {
        this.icons = icons; prefs = preferences;
        LoadCollapsed();
        this.service = service; this.opener = opener; this.prompt = prompt; this.elevation = elevation;
        this.taskService = taskService; this.signatures = signatures; this.openTask = openTask; this.inputs = inputs;
        HistoryList = new(text, HistoryArea.Startup, () => OpenHistory);
        banner = new(this); footer = new(this);
        AddChoice("FilterSource", i => i.Source);
        AddChoice("FilterScope", i => i.Scope);
        AddChoice("FilterStatus", i => i.Approval);
        AddYesNoChoice("FilterFileMissing", i => entries.TryGetValue(i.StableId, out var e) && e.FileMissing);
        AddChoice("FilterSignature", i => SignatureOf(i)?.State ?? SignatureState.Unknown);
        statusFilter = ChoiceFilters[2];
        highFilter = AddHiddenToggle("V2StChipHigh", i => ImpactOf(i) != StartupImpact.High);
        // The chips sit in the toolbar band like the chips of Apps and Services; "All" clears the filters they manage.
        AddQuickChip("all", "V2StChipAll", () => { statusFilter.Reset(); highFilter.Reset(); }, () => !statusFilter.IsActive && !highFilter.IsOn);
        AddQuickChip("high", "V2StChipHigh", () => { statusFilter.Reset(); highFilter.IsOn = true; }, () => highFilter.IsOn && !statusFilter.IsActive);
        AddQuickChip("on", "V2StChipOn", () => { highFilter.Reset(); SetChoice(statusFilter, nameof(StartupApproval.Enabled)); }, () => !highFilter.IsOn && IsChoice(statusFilter, nameof(StartupApproval.Enabled)));
        AddQuickChip("off", "V2StChipOff", () => { highFilter.Reset(); SetChoice(statusFilter, nameof(StartupApproval.Disabled)); }, () => !highFilter.IsOn && IsChoice(statusFilter, nameof(StartupApproval.Disabled)));
        ReviewStagedCommand = new(() => Track(ReviewStagedAsync()), () => stagedStates.Count > 0 && !Busy && !base.IsBusy);
        DiscardStagedCommand = new(DiscardStaged, () => stagedStates.Count > 0);
        AddSort("SortName", i => i.Name);
        AddSort("SortSource", i => i.Source);
        AddSort("SortStatus", i => i.Approval);
        ToggleAllCommand = new(ToggleAll, () => listItems.OfType<StartupCategoryRow>().Any());
        OpenLocationCommand = new(OpenLocation, () => SelectedEntry is { File: { Exists: true, TargetPath: not null } });
        RevertHistory = new(row => { if (row is not null && !Busy) Track(RevertAsync(row)); }, _ => !Busy);
        ManageTaskCommand = new(ManageSelectedTask, () => SelectedIsTask);
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(SelectedRow)) RefreshDetail(); else if (e.PropertyName is nameof(Rows)) banner.Refresh(); };
    }

    // ---- staged changes: the switch on a row stages; one review applies them all ----

    public RelayCommand ReviewStagedCommand { get; }
    public RelayCommand DiscardStagedCommand { get; }
    public override int PendingCount => stagedStates.Count;
    public override System.Windows.Input.ICommand? PendingReviewCommand => ReviewStagedCommand;
    public override System.Windows.Input.ICommand? PendingDiscardCommand => DiscardStagedCommand;
    public override IReadOnlyList<string> PendingNames => stagedStates.Keys.Select(k => entries.TryGetValue(k, out var e) ? e.Item.Name : k).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    public override string PendingSummaryText => Text.Format(stagedStates.Count == 1 ? "V2StPendingOne" : "V2StPending", stagedStates.Count);
    protected override IReadOnlyList<StagedChange> StagedItems => stagedStates
        .Select(p => (p.Key, Name: entries.TryGetValue(p.Key, out var e) ? e.Item.Name : p.Key, On: p.Value))
        .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(x => new StagedChange(x.Key, x.Name, EnabledLabel(!x.On), EnabledLabel(x.On), null, () => { if (Rows.OfType<StartupRow>().FirstOrDefault(r => r.Key == x.Key) is { } row) { row.Reset(); stagedStates.Remove(x.Key); RaiseStaged(); } })).ToList();
    public string ReviewStagedLabel => Text["V2StReview"];
    public string DiscardStagedLabel => Text["V2StDiscard"];

    private StartupImpact ImpactOf(StartupItem i) => entries.TryGetValue(i.StableId, out var e) ? StartupImpactEstimator.Estimate(e.File) : StartupImpact.Low;

    /// <summary>Counts of the entries that start with Windows now, by estimated impact. Same rule as the enabled count of <see cref="SummaryText"/>, so the two numbers agree.</summary>
    public (int High, int Medium, int Low, int Total) ImpactCounts()
    {
        int high = 0, medium = 0, low = 0;
        foreach (var item in CurrentResult?.Items ?? [])
        {
            if (item.Approval != StartupApproval.Enabled) continue;
            switch (ImpactOf(item)) { case StartupImpact.High: high++; break; case StartupImpact.Medium: medium++; break; default: low++; break; }
        }
        return (high, medium, low, high + medium + low);
    }

    public string ImpactHeadline { get { var c = ImpactCounts(); return c.Total == 0 ? "" : Text.Format("V2StHeadline", c.Total, c.High); } }

    private void OnStaged(StartupRow row)
    {
        if (row.IsStaged) stagedStates[row.Key] = row.IsOn; else stagedStates.Remove(row.Key);
        RaiseStaged();
    }

    private void RaiseStaged()
    {
        foreach (var n in new[] { nameof(PendingCount), nameof(PendingNames), nameof(PendingSummaryText) }) OnPropertyChanged(n);
        ReviewStagedCommand.NotifyCanExecuteChanged(); DiscardStagedCommand.NotifyCanExecuteChanged();
    }

    private void DiscardStaged()
    {
        stagedStates.Clear();
        foreach (var row in Rows.OfType<StartupRow>()) row.Reset();
        RaiseStaged();
    }

    // One preview per entry, one confirmation for all of them, then the changes in order. An entry that fails is reported; the others stay applied and each can be undone from the history.
    // A startup entry changes through the startup service (the StartupApproved flag); a scheduled task changes through the scheduled task service. Both record the change first, write with a
    // compare-and-set and read the result back.
    private sealed record PlannedChange(StartupEntry Entry, string Before, string After, bool Enable, Func<Task<(bool Ok, string Code, bool RecordLost)>> Apply);

    private async Task ReviewStagedAsync()
    {
        var wanted = stagedStates.Where(p => entries.ContainsKey(p.Key)).Select(p => (Entry: entries[p.Key], Enable: p.Value))
            .Where(w => w.Entry.Block == StartupBlock.ScheduledTask ? taskService is not null && !w.Entry.NeedsAdministrator : w.Entry.Allows(w.Enable ? StartupAction.Enable : StartupAction.Disable)).ToList();
        if (wanted.Count == 0) { stagedStates.Clear(); RaiseStaged(); return; }
        var run = ActivityRun.None;
        Busy = true; SetNotice(null);
        try
        {
            var plan = new List<PlannedChange>();
            string? unchanged = null;
            foreach (var (entry, enable) in wanted)
            {
                var (change, refusal) = await PlanAsync(entry, enable).ConfigureAwait(true);
                if (refusal is not null) { SetNotice(() => CodeText(refusal), true); Busy = false; return; }
                if (change is not null) plan.Add(change); else unchanged ??= entry.Block == StartupBlock.ScheduledTask ? ScheduledTaskCode.AlreadySet : StartupCode.AlreadySet;
            }
            if (plan.Count == 0) { stagedStates.Clear(); RaiseStaged(); Report(unchanged ?? StartupCode.AlreadySet, false); Busy = false; return; }
            if (!await prompt.ConfirmActionAsync(Confirmation(plan)).ConfigureAwait(true)) { Busy = false; return; }
            run = Runs.Begin(Text.Format("ACRunApply", Title), true);
            if (!run.Started) { Busy = false; return; }
            var done = 0; string? failure = null; var recordLost = false;
            foreach (var change in plan)
            {
                var (ok, code, lost) = await change.Apply().ConfigureAwait(true);
                recordLost |= lost;
                if (ok) done++; else failure ??= code;
                run.Report((double)(done + (ok ? 0 : 1)) / plan.Count);
            }
            stagedStates.Clear(); RaiseStaged();
            if (failure is null) SetNotice(() => (plan.Count == 1 ? Text["StartupApplied"] : Text.Format("V2StApplied", done)) + (recordLost ? " " + Text["RecordLostNote"] : ""), recordLost);
            else SetNotice(() => plan.Count == 1 ? CodeText(failure) : Text.Format("V2StAppliedSome", done, plan.Count, CodeText(failure)), true);
            run.Finish(failure is null ? RunResult.Ok : RunResult.Warning, plan.Count == 1 ? Text["StartupApplied"] : Text.Format("V2StApplied", done));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetNotice(() => Text["ActionFailedUnexpected"], true);
            run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title));
        }
        finally { run.Dispose(); }
        // Busy until the list and the history are read again: the old rows (and their Undo buttons) stay on screen while the page scans.
        try { await AfterChangeAsync().ConfigureAwait(true); }
        finally { Busy = false; }
    }

    private async Task<(PlannedChange? Change, string? Refusal)> PlanAsync(StartupEntry entry, bool enable)
    {
        if (entry.Block == StartupBlock.ScheduledTask)
        {
            var path = StartupTaskRows.PathOf(entry.Item);
            var request = new ScheduledTaskRequest(enable ? ScheduledTaskChangeKind.Enable : ScheduledTaskChangeKind.Disable, path);
            var preview = await Task.Run(() => taskService!.Preview(request)).ConfigureAwait(true);
            if (preview.IsRefused) return (null, preview.RefusalCode);
            if (!preview.WillChange) return (null, null);
            return (new(entry, Text.Enum(preview.BeforeEnabled == true ? StartupApproval.Enabled : StartupApproval.Disabled), Text.Enum(enable ? StartupApproval.Enabled : StartupApproval.Disabled), enable,
                async () => { var r = await taskService!.ApplyAsync(request with { ExpectedFingerprint = preview.Fingerprint }).ConfigureAwait(true); return (r.Ok, r.Code, r.RecordLost); }), null);
        }
        var startupRequest = new StartupRequest(entry.Item.StableId, enable ? StartupAction.Enable : StartupAction.Disable);
        var startupPreview = await service.PreviewAsync(startupRequest).ConfigureAwait(true);
        if (startupPreview.IsRefused) return (null, startupPreview.RefusalCode);
        if (!startupPreview.WillChange) return (null, null);
        return (new(entry, Text.Enum(startupPreview.Before), Text.Enum(startupPreview.After), enable,
            async () => { var r = await service.ApplyAsync(startupRequest with { ExpectedBeforeHex = startupPreview.BeforeHex, CheckExpected = true }).ConfigureAwait(true); return (r.Ok, r.Code, r.RecordLost); }), null);
    }

    private ActionConfirmation Confirmation(IReadOnlyList<PlannedChange> plan)
    {
        var changes = plan.Select(p => new ConfirmChange(p.Entry.Item.Name, p.Before, p.After, p.Entry.Item.Command)).ToList();
        if (plan.Count == 1)
        {
            var p = plan[0]; var item = p.Entry.Item;
            var lines = new List<string>
            {
                Text.Format("StartupLineEntry", item.Name),
                Text.Format("StartupLineCommand", item.Command),
                Text.Format("StartupLineChange", p.Before, p.After),
                Text.Format("StartupLineWhere", Text.Enum(item.Source), Text.Enum(item.Scope)),
            };
            return new ActionConfirmation(Text[p.Enable ? "StartupEnableTitle" : "StartupDisableTitle"], Text.Format(p.Enable ? "StartupEnableBody" : "StartupDisableBody", item.Name), lines,
                Text["StartupUndoNote"], Text[p.Enable ? "StartupEnablePrimary" : "StartupDisablePrimary"])
            { Changes = changes, Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, Restart = false };
        }
        return new ActionConfirmation(Text.Format("V2StApplyTitle", plan.Count), Text.Format("V2StApplyBody", plan.Count), plan.Select(p => Text.Format("StartupLineChange", p.Before, p.After) + ": " + p.Entry.Item.Name).ToList(),
            Text["StartupUndoNote"], Text.Format("V2StApplyPrimary", plan.Count))
        { Changes = changes, Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, Restart = false };
    }
    public RelayCommand ToggleAllCommand { get; }
    public RelayCommand ManageTaskCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand<StartupHistoryRow> RevertHistory { get; }
    public ObservableCollection<StartupHistoryRow> History { get; } = [];
    public PageHistoryModel HistoryList { get; }

    public override bool ShowReadOnlyNotice => false;
    protected override bool SummaryHasCount => true;
    public override object? BannerPanel => banner;
    public override string Intro => Text["StartupIntro"];
    protected override IReviewPrompt? DetailsHost => prompt;

    protected override DetailsDialog BuildDetails(DiscoveryRow row)
    {
        var fields = row.BuildFields().ToList();
        if (entries.TryGetValue(row.Key, out var e) && Rules(e) is { Length: > 0 } rules) fields.Add(new(Text["StartupRules"], rules));
        return DetailsDialogBuilder.Build(Text, CopyText, row.Title, fields, row.Badges, actions: DetailActions(row));
    }

    /// <summary>The buttons that used to sit in the details pane: manage a scheduled task on its own page, show the file in Explorer.</summary>
    protected override IReadOnlyList<DetailsAction> DetailActions(DiscoveryRow row)
    {
        if (!entries.TryGetValue(row.Key, out var e)) return [];
        List<DetailsAction> list = [];
        if (e.Block == StartupBlock.ScheduledTask && openTask is not null)
            list.Add(new(Text["StartupManageTask"], $"{Text["StartupManageTask"]}, {e.Item.Name}", Text["StartupManageTaskHelp"], () => { OpenTask(e); return Task.CompletedTask; }, IsPrimary: true));
        if (e.File is { Exists: true, TargetPath: not null })
            list.Add(new(Text["StartupOpenLocation"], Text.Format("V2StOpenLocationName", e.Item.Name), Text["StartupOpenLocationHelp"], () => { OpenLocation(row.Key); return Task.CompletedTask; }));
        return list;
    }
    public override bool ToggleRow(DiscoveryRow row)
    {
        if (row is not StartupRow { CanStage: true } startup) return false;
        startup.IsOn = !startup.IsOn;
        return true;
    }
    public override object? FooterPanel => footer;
    public override bool IsBusy => base.IsBusy || busy;

    private bool Busy { get => busy; set { if (SetProperty(ref busy, value)) { OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); ReviewStagedCommand.NotifyCanExecuteChanged(); RevertHistory.NotifyCanExecuteChanged(); } } }

    /// <summary>Counts of the scanned entries. "Need attention" are enabled entries whose file is missing or whose signature is not valid (read from the checks that already ran).</summary>
    public string SummaryText
    {
        get
        {
            var all = CurrentResult?.Items;
            if (all is null || all.Count == 0) return "";
            var enabled = 0; var attention = 0;
            foreach (var item in all)
            {
                if (item.Approval != StartupApproval.Enabled) continue;
                enabled++;
                if (!entries.TryGetValue(item.StableId, out var e)) continue;
                if (e.FileMissing || signatures?.Peek(e.File) is { State: SignatureState.Invalid }) attention++;
            }
            return Text.Format("StartupSummary", all.Count, enabled, attention);
        }
    }

    public bool ShowNeedsAdministrator => !elevation.IsElevated;
    public string NeedsAdministratorTitle => elevation.UnsafeLocation ? Text["AppLocationUnsafeTitle"] : Text["StartupNeedsAdminTitle"];
    public string NeedsAdministratorText => elevation.UnsafeLocation ? Text["AppLocationUnsafe"] : Text["StartupNeedsAdminBody"];
    public bool ShowAdminHowTo => !elevation.UnsafeLocation;
    // The notice is kept as a function of the language, so it is shown in the language that is active now.
    public string? Notice => noticeText?.Invoke();
    private void SetNotice(Func<string>? text, bool isError = false)
    {
        noticeText = text; noticeIsError = isError;
        if (text is not null && !isError) Feedback.Success(text());
        OnPropertyChanged(nameof(Notice)); OnPropertyChanged(nameof(HasNotice)); OnPropertyChanged(nameof(NoticeIsError)); banner.Refresh();
    }
    public bool NoticeIsError => noticeIsError;
    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public StartupEntry? SelectedEntry => SelectedRow is { } row && entries.TryGetValue(row.Key, out var e) ? e : null;
    public bool SelectedIsTask => SelectedEntry is { Block: StartupBlock.ScheduledTask } && openTask is not null;

    private void Track(Task task)
    {
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // in production nobody awaits the page work
        work = Task.WhenAll(work, task);
    }

    private void ManageSelectedTask() { if (SelectedEntry is { Block: StartupBlock.ScheduledTask } e) OpenTask(e); }

    private void OpenTask(StartupEntry e) => openTask?.Invoke(StartupTaskRows.PathOf(e.Item));

    public string SelectedRulesText => SelectedEntry is { } e ? Rules(e) : "";

    private string Rules(StartupEntry e) => e.Block switch
    {
        StartupBlock.ScheduledTask => Text["StartupRuleTask"],
        StartupBlock.RunOnce => Text["StartupRuleRunOnce"],
        StartupBlock.Unmanaged => Text["StartupRuleUnmanaged"],
        StartupBlock.Protected => e.Item.Approval == StartupApproval.Disabled ? Text["StartupRuleProtectedDisabled"] : Text["StartupRuleProtected"],
        _ => e.NeedsAdministrator ? Text["StartupRuleNeedsAdmin"] : e.FileMissing ? Text["StartupRuleMissing"] : "",
    };

    private void RefreshDetail()
    {
        OnPropertyChanged(nameof(SelectedEntry));
        OpenLocationCommand.NotifyCanExecuteChanged();
        ManageTaskCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCommands() => OpenLocationCommand.NotifyCanExecuteChanged();

    protected override IEnumerable<string?> SearchFields(StartupItem i) =>
        [i.Name, i.Command, i.Publisher, SignatureOf(i) is { State: SignatureState.Valid } sig ? sig.Signer : null];

    protected override DiscoveryRow BuildRow(StartupItem i)
    {
        var plain = BuildPlainRow(i);
        var e = entries[i.StableId];
        var original = i.Approval != StartupApproval.Disabled;
        var canStage = CanStage(e, original);
        var desired = canStage && stagedStates.TryGetValue(i.StableId, out var wanted) ? wanted : original;
        var lockText = canStage ? "" : LockReason(e);
        var impact = StartupImpactEstimator.Estimate(e.File);
        var impactText = Text[impact switch { StartupImpact.High => "V2StImpactHigh", StartupImpact.Medium => "V2StImpactMedium", _ => "V2StImpactLow" }];
        var canOpen = e.File is { Exists: true, TargetPath: not null };
        var subLine = string.Join(" · ", new[] { plain.Secondary, Text.Enum(i.Source) }.Where(part => part.Length > 0));
        var key = i.StableId;
        return new StartupRow(plain, e, impact, impactText, subLine, original, desired, canStage, lockText, Text["V2StOn"], Text["V2StOff"], Text["V2StWillChange"], $"{i.Name}, {Text["V2StSwitchName"]}", OnStaged,
            new AppIconLoader(icons, e.File is { TargetPath: { Length: > 0 } target, Exists: not false } ? new AppIconKey(AppIconKind.File, target) : null))
        {
            SourceChips = [new(Text.Enum(i.Source), Tone.Info), Neutral(Text.Enum(i.Scope))],
            PublisherText = plain.Secondary.Length > 0 ? Text.Format("V2StPublisherLine", Text.Name(plain.Secondary)) : Text["V2StPublisherUnknown"],
            PathText = e.File.TargetPath ?? i.Command,
            SwitchHelp = Rules(e) is { Length: > 0 } rules ? rules : Text["StartupToggleHelp"],
            CanOpenLocation = canOpen,
            OpenLocationCommand = new RelayCommand(() => OpenLocation(key), () => canOpen),
            OpenLocationText = Text["StartupOpenLocation"],
            OpenLocationName = Text.Format("V2StOpenLocationName", i.Name),
        };
    }

    // The switch is available when Winora has the change for the entry and the app has the rights for it. A scheduled task also needs the task service and the safety policy to allow turning it off.
    private bool CanStage(StartupEntry e, bool original)
    {
        if (e.Block == StartupBlock.ScheduledTask) return taskService is not null && !e.NeedsAdministrator && (!original || TaskPolicyAllows(e, ScheduledTaskChangeKind.Disable));
        return e.Allows(original ? StartupAction.Disable : StartupAction.Enable);
    }

    private bool TaskPolicyAllows(StartupEntry e, ScheduledTaskChangeKind kind) =>
        taskService is not null && ScheduledTaskPolicy.Refusal(kind, taskService.Policy.Classify(StartupTaskRows.PathOf(e.Item), e.Item.Publisher)) is null;

    private string LockReason(StartupEntry e)
    {
        if (e.Block == StartupBlock.ScheduledTask)
        {
            if (taskService is null) return Text["BadgeViewOnly"];
            if (e.NeedsAdministrator) return Text["BadgeNeedsAdmin"];
            return taskService.Policy.Classify(StartupTaskRows.PathOf(e.Item), e.Item.Publisher).Class == TaskPolicyClass.Protected ? Text["BadgeTaskProtected"] : Text["BadgeTaskLocked"];
        }
        return e.Block switch
        {
            StartupBlock.Protected => Text["BadgeProtected"],
            StartupBlock.RunOnce or StartupBlock.Unmanaged => Text["BadgeViewOnly"],
            _ => e.NeedsAdministrator ? Text["BadgeNeedsAdmin"] : Text["BadgeViewOnly"],
        };
    }

    private DiscoveryRow BuildPlainRow(StartupItem i)
    {
        var e = service.Describe(i);
        entries[i.StableId] = e;
        var sig = signatures?.Peek(e.File);
        if (i.Source == StartupSource.ScheduledTask) return BuildTaskRow(i, e, sig);
        var badges = new List<BadgeModel> { new(Text.Enum(i.Source), Tone.Info), Neutral(Text.Enum(i.Scope)), new(Text.Enum(i.Approval), Approval(i.Approval)) };
        if (e.FileMissing) badges.Add(new(Text["BadgeFileMissing"], Tone.Danger));
        if (SignatureBadge(sig) is { } sigBadge) badges.Add(sigBadge);
        if (e.Block == StartupBlock.Protected) badges.Add(new(Text["BadgeProtected"], Tone.Info));
        else if (e.Block is StartupBlock.RunOnce or StartupBlock.Unmanaged) badges.Add(Neutral(Text["BadgeViewOnly"]));
        else if (e.NeedsAdministrator) badges.Add(new(Text["BadgeNeedsAdmin"], Tone.Caution));

        return new(i.StableId, i.Name, i.Command, true, i.Publisher ?? VerifiedSigner(sig) ?? "", badges,
            () =>
            [
                new(Text["FieldName"], i.Name),
                new(Text["FieldCommand"], i.Command, true),
                new(Text["FieldPublisher"], Or(i.Publisher)),
                new(Text["FieldSource"], Text.Enum(i.Source)),
                new(Text["FieldScope"], Text.Enum(i.Scope)),
                new(Text["FieldStatus"], Text.Enum(i.Approval)),
                new(Text["FieldFileExists"], e.File.Exists is { } x ? YesNo(x) : Text["ValueNotRecorded"]),
                new(Text["FieldFileSize"], e.File.Size is { } size ? Size(size) : Text["ValueNotRecorded"]),
                new(Text["FieldFile"], Or(e.File.TargetPath), true),
                .. FileFields(e),
                new(Text["FieldLocation"], $"{i.Resource.Scheme}:{i.Resource.Scope}:{i.Resource.Path}", true),
                new(Text["FieldIdentifier"], i.StableId, true),
            ]);
    }

    // A scheduled task that starts at logon or boot. The switch turns it on or off through the scheduled task service; the Scheduled Tasks page shows the rest.
    private DiscoveryRow BuildTaskRow(StartupItem i, StartupEntry e, SignatureInfo? sig)
    {
        var path = StartupTaskRows.PathOf(i);
        taskInfos.TryGetValue(i.StableId, out var info);
        var state = info?.State ?? TaskRunState.Unknown;
        var policy = taskService?.Policy.Classify(path, i.Publisher);
        var badges = new List<BadgeModel> { new(Text.Enum(i.Source), Tone.Info), new(Text.Enum(state), TaskState(state)) };
        if (policy?.Class == TaskPolicyClass.Protected) badges.Add(new(Text["BadgeTaskProtected"], Tone.Success));
        else if (policy?.Class == TaskPolicyClass.WindowsUnlisted) badges.Add(Neutral(Text["BadgeTaskLocked"]));
        if (e.FileMissing) badges.Add(new(Text["BadgeFileMissing"], Tone.Danger));
        if (SignatureBadge(sig) is { } sigBadge) badges.Add(sigBadge);
        return new(i.StableId, i.Name, i.Command, true, i.Publisher ?? VerifiedSigner(sig) ?? "", badges,
            () =>
            [
                new(Text["FieldName"], i.Name),
                new(Text["FieldPath"], path, true),
                new(Text["FieldCommand"], Or(i.Command), true),
                new(Text["FieldAuthor"], Or(i.Publisher)),
                new(Text["FieldSource"], Text.Enum(i.Source)),
                new(Text["FieldState"], Text.Enum(state)),
                new(Text["FieldSafety"], policy is null ? Text["ValueNotRecorded"] : PolicyText(policy)),
                new(Text["FieldLastRun"], When(info?.LastRunUtc)),
                new(Text["FieldNextRun"], When(info?.NextRunUtc)),
                new(Text["FieldFileExists"], e.File.Exists is { } x ? YesNo(x) : Text["ValueNotRecorded"]),
                new(Text["FieldFileSize"], e.File.Size is { } size ? Size(size) : Text["ValueNotRecorded"]),
                new(Text["FieldFile"], Or(e.File.TargetPath), true),
                .. FileFields(e),
                new(Text["FieldIdentifier"], i.StableId, true),
            ]);
    }

    private string PolicyText(TaskPolicyInfo policy) => policy.Class switch
    {
        TaskPolicyClass.KnownOptional => Text["TaskPolicy_KnownOptional_" + (policy.Risk == TaskRisk.ReducesDiagnostics ? "ReducesDiagnostics" : "Recommended")],
        _ => Text["TaskPolicy_" + policy.Class],
    };

    private static Tone TaskState(TaskRunState s) => s switch
    {
        TaskRunState.Running => Tone.Success,
        TaskRunState.Ready => Tone.Info,
        TaskRunState.Disabled => Tone.Neutral,
        _ => Tone.Caution,
    };

    // ---- signature ----

    private SignatureInfo? SignatureOf(StartupItem i) => entries.TryGetValue(i.StableId, out var e) ? signatures?.Peek(e.File) : null;

    private static string? VerifiedSigner(SignatureInfo? sig) => sig is { State: SignatureState.Valid } ? sig.Signer : null;

    private BadgeModel? SignatureBadge(SignatureInfo? sig) => sig?.State switch
    {
        SignatureState.Valid => new(Text["BadgeSignatureValid"], Tone.Success),
        SignatureState.Invalid => new(Text["BadgeSignatureInvalid"], Tone.Danger),
        SignatureState.Unsigned => new(Text["BadgeSignatureUnsigned"], Tone.Caution),
        SignatureState.Unknown => Neutral(Text["BadgeSignatureUnknown"]),
        _ => null,
    };

    // The signature part of the details: the result, the verified signer, and a plain-words note. The note says what the result means and, for a result that is not yet known, why.
    private IEnumerable<DetailField> FileFields(StartupEntry e)
    {
        var sig = signatures?.Peek(e.File);
        yield return new(Text["FieldSignature"], SignatureText(e, sig));
        yield return new(Text["FieldSigner"], Or(VerifiedSigner(sig)));
        if (SignatureNote(e, sig) is { Length: > 0 } note) yield return new(Text["FieldSignatureNote"], note);
        if (AlsoStartsThrough(e) is { Length: > 0 } also) yield return new(Text["FieldAlsoStarts"], also);
    }

    private string SignatureText(StartupEntry e, SignatureInfo? sig) => sig switch
    {
        null => e.File.Exists == true && signatures is not null ? Text[checking ? "SigStateChecking" : "SigStateNotChecked"] : Text["ValueNotRecorded"],
        { State: SignatureState.Valid, ViaCatalog: true } => Text["SigStateValidCatalog"],
        _ => Text.Enum(sig.State),
    };

    private string SignatureNote(StartupEntry e, SignatureInfo? sig)
    {
        if (sig is null) return e.File.Exists == true && signatures is not null ? Text[checking ? "SigNoteChecking" : "SigNoteNotChecked"] : "";
        return sig.State switch
        {
            SignatureState.Valid => Text["SigNoteValid"],
            SignatureState.Unsigned => Text["SigNoteUnsigned"],
            SignatureState.Invalid => Text.Format("SigNoteInvalid", Text["SigProblem_" + sig.Problem], "0x" + ((uint)sig.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
            _ => Text[Text.Has("SigProblem_" + sig.Problem) ? "SigProblem_" + sig.Problem : "SigProblem_CheckFailed"],
        };
    }

    // Other startup entries that start the same file (compared by full path). Short and exact: the names and where each one lives.
    private string AlsoStartsThrough(StartupEntry e)
    {
        if (e.File.TargetPath is not { } target) return "";
        var others = entries.Values.Where(o => !ReferenceEquals(o, e) && o.Item.StableId != e.Item.StableId && o.File.TargetPath is { } p && string.Equals(p, target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Item.StableId, StringComparer.Ordinal).ToList();
        if (others.Count == 0) return "";
        return string.Join(Text["ListSeparatorStrong"], others.Take(3).Select(o => Text.Format("StartupAlsoItem", o.Item.Name, Text.Enum(o.Item.Source)))) + (others.Count > 3 ? " " + Text.Format("StartupAlsoMore", others.Count - 3) : "");
    }

    private volatile bool checking;

    // The checks run on a worker thread, after the rows are shown, one batch per scan (at most the cap in SignatureService). Rows are built again as results arrive.
    protected override void OnResultApplied(DiscoveryResult<StartupItem> value) => StartSignatureChecks(value);

    private void StartSignatureChecks(DiscoveryResult<StartupItem> value)
    {
        if (signatures is null) return;
        signatureRun?.Cancel();
        var cts = signatureRun = new CancellationTokenSource();
        var files = value.Items.Select(i => entries.TryGetValue(i.StableId, out var e) ? e.File : StartupFileFacts.Unknown).ToList();
        checking = true; lastBatch = null;
        Track(RunSignatureChecksAsync(files, cts));
    }

    private async Task RunSignatureChecksAsync(IReadOnlyList<StartupFileFacts> files, CancellationTokenSource cts)
    {
        var progress = new Progress<int>(n => { if (n % 8 == 0 && !cts.IsCancellationRequested) RebuildRows(); });
        try { lastBatch = await signatures!.CheckAsync(files, progress, cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is not OutOfMemoryException) { /* the checks are an extra; the list stays usable */ }
        finally
        {
            if (ReferenceEquals(signatureRun, cts)) { signatureRun = null; checking = false; }
            cts.Dispose();
        }
        RebuildRows();
    }

    private string Size(long bytes) => Text.Size(bytes);

    private static Tone Approval(StartupApproval a) => a switch
    {
        StartupApproval.Enabled => Tone.Success,
        StartupApproval.Disabled => Tone.Neutral,
        _ => Tone.Caution,
    };

    public override void Activate()
    {
        inputs.SeedFromInventory();
        base.Activate();
        RefreshHistory();
        // Coming back to a page whose checks were cut short continues them.
        if (signatures is not null && signatureRun is null && CurrentResult is { } shown && (lastBatch is null || lastBatch.Deferred > 0)) StartSignatureChecks(shown);
    }

    public override void Deactivate() { base.Deactivate(); signatureRun?.Cancel(); }
    public override async Task WhenIdleAsync()
    {
        Task seen;
        do { seen = work; await base.WhenIdleAsync(); await seen; } while (!ReferenceEquals(seen, work));
        await base.WhenIdleAsync();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        banner.Refresh(); footer.Refresh();
        if (History.Count > 0) RefreshHistory();
    }

    // ---- actions ----

    private async Task RevertAsync(StartupHistoryRow row)
    {
        if (Busy) return;
        var confirmation = new ActionConfirmation(Text["StartupRevertTitle"], Text["StartupRevertBody"], [row.Description], Text["StartupUndoNote"], Text["StartupRevertPrimary"])
        {
            Changes = [new ConfirmChange(row.Description)], Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, Restart = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true) || Busy) return;
        Busy = true; SetNotice(null);
        var run = ActivityRun.None;
        try
        {
            run = Runs.Begin(Text.Format("ACRunRevert", Title), true);
            if (!run.Started) { Busy = false; return; }
            var (ok, code) = await row.Undo().ConfigureAwait(true);
            Report(code, ok);
            run.Finish(ok ? RunResult.Ok : RunResult.Warning, null, quiet: ok);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetNotice(() => Text["ActionFailedUnexpected"], true);
            run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title));
        }
        finally { run.Dispose(); }
        // Busy until the list and the history are read again: the old rows (and their Undo buttons) stay on screen while the page scans.
        try { await AfterChangeAsync().ConfigureAwait(true); }
        finally { Busy = false; }
    }

    // A change is shown by reading the list again, which also keeps the Overview counts and the other pages in step.
    private async Task AfterChangeAsync()
    {
        await ScanAsync().ConfigureAwait(true);
        RefreshHistory();
        RefreshDetail();
    }

    private void OpenLocation() => OpenPath(SelectedEntry?.File.TargetPath);

    private void OpenLocation(string key) => OpenPath(entries.TryGetValue(key, out var e) ? e.File.TargetPath : null);

    private void OpenPath(string? path)
    {
        if (path is null) return;
        SetNotice(null);
        if (!opener.Reveal(path)) SetNotice(() => Text["StartupOpenFailed"], true);
    }

    private void Report(string code, bool ok)
    {
        SetNotice(ok ? () => Text["StartupApplied"] : () => CodeText(code), !ok);
    }

    public string CodeText(string code)
    {
        var prefix = code.StartsWith("task.", StringComparison.Ordinal) ? "TaskCode_" : "StartupCode_";
        var key = prefix + code.Replace('.', '_');
        return Text.Has(key) ? Text[key] : Text["StartupCode_startup_failed"];
    }

    // ---- groups ----

    private static string CategoryKey(StartupCategory c) => c switch { StartupCategory.Registry => "Registry", StartupCategory.StartupFolder => "Folder", _ => "Task" };

    private static IconKey CategoryIcon(StartupCategory c) => c switch { StartupCategory.Registry => IconKey.Key24, StartupCategory.StartupFolder => IconKey.Folder24, _ => IconKey.CalendarClock24 };

    private void LoadCollapsed()
    {
        if (prefs?.Read(CollapsedKey) is not { Length: > 0 } stored) return;
        foreach (var part in stored.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<StartupCategory>(part, out var c)) collapsed.Add(c);
    }

    public override System.Collections.IEnumerable ListItems => listItems;

    /// <summary>True while a search is typed: every group shows its matches then, whatever the user closed.</summary>
    private bool Searching => SearchText.Trim().Length > 0;

    private bool ShowsOpen(StartupCategory c) => Searching || !collapsed.Contains(c);

    private StartupCategoryRow HeaderFor(StartupCategory c)
    {
        if (!headers.TryGetValue(c, out var header)) headers[c] = header = new(c, SetExpanded) { Icon = CategoryIcon(c) };
        return header;
    }

    private void SetExpanded(StartupCategory c, bool expanded)
    {
        if (Searching) { SyncListItems(); return; }
        if (expanded) collapsed.Remove(c); else collapsed.Add(c);
        prefs?.Write(CollapsedKey, string.Join(',', collapsed.Order()));
        SyncListItems();
    }

    private static string GroupStateKey(bool open) => open ? "GroupExpanded" : "GroupCollapsed";

    /// <summary>Opens every group, or closes every group when at least one is open.</summary>
    private void ToggleAll()
    {
        var present = listItems.OfType<StartupCategoryRow>().Select(h => h.Category).ToList();
        if (present.Count == 0) return;
        if (present.Any(c => !collapsed.Contains(c))) foreach (var c in present) collapsed.Add(c);
        else foreach (var c in present) collapsed.Remove(c);
        prefs?.Write(CollapsedKey, string.Join(',', collapsed.Order()));
        SyncListItems();
    }

    public string ToggleAllLabel => Text[listItems.OfType<StartupCategoryRow>().Any(h => h.IsExpanded) ? "V2StCollapseAll" : "V2StExpandAll"];

    protected override void OnRowsApplied() => SyncListItems();

    // The list shows the groups in their own order, so the first entry shown is the first entry of the first group.
    protected override DiscoveryRow? DefaultSelection(IReadOnlyList<DiscoveryRow> shown) =>
        StartupCategories.All.SelectMany(c => shown.OfType<StartupRow>().Where(r => StartupCategories.Of(r.Entry.Item) == c)).FirstOrDefault();

    /// <summary>Builds the list: one header per group that has entries, then the entries of the open groups. The collection is edited in place so the scroll position stays.</summary>
    private void SyncListItems()
    {
        var rows = Rows.OfType<StartupRow>().ToList();
        var desired = new List<object>();
        foreach (var c in StartupCategories.All)
        {
            var members = rows.Where(r => StartupCategories.Of(r.Entry.Item) == c).ToList();
            if (members.Count == 0) continue;
            var open = ShowsOpen(c);
            var key = CategoryKey(c);
            HeaderFor(c).Update(Text["V2StCat" + key], Text["V2StCat" + key + "Text"], Text.Format(members.Count == 1 ? "V2StCatCountOne" : "V2StCatCount", Text.Number(members.Count)), Text[GroupStateKey(open)], Text["V2StGroupHelp"], open, members.Count);
            desired.Add(headers[c]);
            if (open) desired.AddRange(members);
            for (var i = 0; i < members.Count; i++) members[i].SetLastInGroup(i == members.Count - 1);
        }
        var keep = SelectedRow;
        var wanted = new HashSet<object>(desired);
        for (var i = listItems.Count - 1; i >= 0; i--) if (!wanted.Contains(listItems[i])) listItems.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < listItems.Count && ReferenceEquals(listItems[i], desired[i])) continue;
            var at = listItems.IndexOf(desired[i]);
            if (at >= 0) listItems.Move(at, i); else listItems.Insert(i, desired[i]);
        }
        // The list drops the selection of an item that leaves it; the row that is still shown stays selected.
        if (keep is not null && !ReferenceEquals(SelectedRow, keep) && listItems.Contains(keep)) SelectedRow = keep;
        else if (SelectedRow is null || !listItems.Contains(SelectedRow)) SelectedRow = listItems.OfType<StartupRow>().FirstOrDefault();
        ToggleAllCommand.NotifyCanExecuteChanged();
        banner.Refresh();
    }

    // ---- history ----

    private void RefreshHistory()
    {
        History.Clear();
        var rows = new List<StartupHistoryRow>();
        IReadOnlyList<StartupChangeRecord> records;
        try { records = service.History(HistoryLimit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { records = []; }
        IReadOnlyList<bool> revertable;
        try { revertable = service.CanRevertAll(records); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { revertable = records.Select(_ => false).ToList(); }
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var id = r.Change.Id;
            rows.Add(new(r.BeganUtc, Describe(r.Change), r.State is { } s ? Text.Enum(s) : Text["StartupInterrupted"], Text.When(r.BeganUtc.ToLocalTime(), "g"), revertable[i], RevertHistory,
                async () => { var result = await service.RevertAsync(id).ConfigureAwait(true); return (result.Ok, result.Code); }, id, r.State == StartupChangeState.Applied));
        }
        if (taskService is not null) rows.AddRange(TaskHistoryRows());
        foreach (var row in rows.OrderByDescending(r => r.At).Take(HistoryLimit)) History.Add(row);
        HistoryList.Set([.. History.Select(h => HistoryList.Row(h.Id, h.At, h.Ok ? HistoryResult.Applied : HistoryResult.Failed, h.Description, h.CanRevert, h.RevertCommand, h, new(Text["StartupUndo"], Text["StartupUndoHelp"])))]);
        OnPropertyChanged(nameof(HasHistory));
        footer.Refresh();
    }

    // The on and off changes that were made to scheduled tasks (here or on the Scheduled Tasks page), with the same undo.
    private IEnumerable<StartupHistoryRow> TaskHistoryRows()
    {
        IReadOnlyList<ScheduledTaskChangeRecord> records;
        try { records = taskService!.History(HistoryLimit).Where(r => r.Change.Kind is ScheduledTaskChangeKind.Enable or ScheduledTaskChangeKind.Disable).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        IReadOnlyList<bool> revertable;
        try { revertable = taskService.CanRevertAll(records); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { revertable = records.Select(_ => false).ToList(); }
        var rows = new List<StartupHistoryRow>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var id = r.Change.Id;
            var undo = r.Change.RevertOf is null ? "" : " " + Text["StartupUndoSuffix"];
            var text = Text.Format(r.Change.Kind == ScheduledTaskChangeKind.Enable ? "StartupHistoryEnable" : "StartupHistoryDisable", r.Change.Name, Text.Enum(InstallationScope.Machine)) + undo;
            rows.Add(new(r.BeganUtc, text, r.State is { } s ? Text.Enum(s) : Text["StartupInterrupted"], Text.When(r.BeganUtc.ToLocalTime(), "g"), revertable[i], RevertHistory,
                async () => { var result = await taskService.RevertAsync(id).ConfigureAwait(true); return (result.Ok, result.Code); }, id, r.State == ScheduledTaskChangeState.Applied));
        }
        return rows;
    }

    public bool HasHistory => History.Count > 0;

    // The Startup page reads the Run keys and the startup folders, and (when a task service is given) the scheduled tasks that start at logon or boot. Both reads go through the shared inventory slots,
    // so the Overview counts and the Scheduled Tasks inventory stay in step, and opening the page uses what the Overview already read.
    private sealed class StartupInputs
    {
        private readonly InventoryModel inventory;
        private readonly bool withTasks;
        private object? startupRead, tasksRead;

        public StartupInputs(InventoryModel inventory, bool withTasks)
        {
            this.inventory = inventory; this.withTasks = withTasks;
            Slot = withTasks ? new InventorySlot<StartupItem>(ScanAsync) : inventory.Startup;
        }

        public ConcurrentDictionary<string, ScheduledTaskInfo> Infos { get; } = new(StringComparer.Ordinal);
        public InventorySlot<StartupItem> Slot { get; }

        private async Task<DiscoveryResult<StartupItem>> ScanAsync(CancellationToken ct)
        {
            var startup = await inventory.Startup.ScanAsync(ct).ConfigureAwait(false);
            var tasks = await inventory.Tasks.ScanAsync(ct).ConfigureAwait(false);
            return Merge(startup, tasks);
        }

        private DiscoveryResult<StartupItem> Merge(DiscoveryResult<StartupItem> startup, DiscoveryResult<ScheduledTaskInfo> tasks)
        {
            startupRead = startup; tasksRead = tasks;
            Infos.Clear();
            var rows = new List<StartupItem>();
            foreach (var task in tasks.Items.Where(t => t.RunsAtLogonOrBoot)) { var row = StartupTaskRows.From(task); Infos[row.StableId] = task; rows.Add(row); }
            return new DiscoveryResult<StartupItem>([.. startup.Items, .. rows], [.. startup.Issues, .. tasks.Issues], startup.CollectedAtUtc > tasks.CollectedAtUtc ? startup.CollectedAtUtc : tasks.CollectedAtUtc);
        }

        /// <summary>Takes what the Overview or another page read since the page last looked, so opening the page does not read again.</summary>
        public void SeedFromInventory()
        {
            if (!withTasks) return;
            if (inventory.Startup.Last is { } startup && inventory.Tasks.Last is { } tasks && (!ReferenceEquals(startup, startupRead) || !ReferenceEquals(tasks, tasksRead)))
                Slot.Seed(Merge(startup, tasks));
        }
    }

    private string Describe(StartupChange c)
    {
        var undo = c.RevertOf is null ? "" : " " + Text["StartupUndoSuffix"];
        return Text.Format(c.Action == StartupAction.Enable ? "StartupHistoryEnable" : "StartupHistoryDisable", Text.Name(c.Name), Text.Enum(c.Scope)) + undo;
    }
}
