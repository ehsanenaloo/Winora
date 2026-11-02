// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>
/// Reads the facts of the next-steps rules. Everything is read from the PC or from the journals of Winora, off the UI thread; a fact that cannot be read is null and its rule stays quiet.
/// The reads touch no setting: they list the catalogs and the journals and compare them with the current values.
/// </summary>
public sealed class NextStepsFacts(IElevationState elevation, RestorePointService? restorePoints, SettingsModel settings, OptimizeService optimize, InventoryModel inventory, StartupService startup,
    SignatureService? signatures, DashboardViewModel? dashboard, HistoryViewModel history, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public NextStepsInput Collect()
    {
        var now = clock.GetUtcNow();
        bool? protection = null; DateTimeOffset? lastPoint = null;
        try
        {
            if (restorePoints?.Status() is { } status) { protection = status.Protection == RestoreProtection.Disabled ? false : null; lastPoint = status.LastCreatedUtc; }
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        var drive = dashboard?.SystemDrive;
        var (reset, restart) = Drift(now);
        var scans = new[] { inventory.Startup.Last?.CollectedAtUtc, inventory.Tasks.Last?.CollectedAtUtc, inventory.Services.Last?.CollectedAtUtc, inventory.Applications.Last?.CollectedAtUtc }.Where(d => d is not null).ToList();
        return new(now, elevation.IsElevated, protection, lastPoint, drive is null ? null : (long)drive.FreeBytes, drive is null ? null : (long)drive.TotalBytes, drive?.Letter, reset, restart, StartupAttention(),
            history.IsLoaded ? history.Snapshot.AnyApplied : null, scans.Count == 0 ? null : scans.Max());
    }

    /// <summary>Settings Winora set that now read differently, and machine changes made since Windows started that wait for a restart.</summary>
    private (int? Reset, int? Restart) Drift(DateTimeOffset now)
    {
        int? reset = null, restart = null;
        try
        {
            var count = 0;
            var history = settings.History;
            foreach (var state in settings.States)
            {
                if (state.Issue is not null || state.Compatibility != SettingCompatibility.Supported) continue;
                var last = history.FirstOrDefault(o => o.SettingId == state.Definition.Id && o.State is OperationState.Applied or OperationState.PendingRestart or OperationState.Reverted);
                if (last is { State: OperationState.Applied or OperationState.PendingRestart } && state.MatchedChoiceId != (last.AfterChoiceId ?? last.ChoiceId)) count++;
            }
            if (settings.Loaded) reset = count;
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        try
        {
            var records = optimize.History(200);
            var items = optimize.List();
            var boot = dashboard?.OsUptime is { } uptime ? now - uptime : (DateTimeOffset?)null;
            var drifted = 0; var waiting = 0;
            foreach (var item in items)
            {
                if (item.Problem is not null) continue;
                var last = records.FirstOrDefault(r => r.State == OptimizeState.Applied && r.Change.DefinitionId == item.Definition.Id && r.Change.TargetInstanceId == item.Adapter?.InstanceId);
                if (last is null || last.Change.RevertOf is not null) continue;
                if (item.CurrentChoiceId != last.Change.ChoiceId) drifted++;
                else if (item.Definition.RequiresRestart && boot is { } at && last.BeganUtc > at) waiting++;
            }
            reset = (reset ?? 0) + drifted;
            if (boot is not null) restart = waiting;
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return (reset, restart);
    }

    private int? StartupAttention()
    {
        if (inventory.Startup.Last is not { } found) return null;
        try
        {
            var count = 0;
            foreach (var item in found.Items.Where(i => i.Approval == StartupApproval.Enabled))
            {
                var entry = startup.Describe(item);
                if (entry.FileMissing || signatures?.Peek(entry.File) is { State: SignatureState.Invalid }) count++;
            }
            return count;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }
}

/// <summary>One row of "Recommended next steps": an icon, one sentence with the real number, at most one button and a reason that quotes the rule.</summary>
public sealed class NextStepRow : ObservableObject
{
    private readonly LocalizationService text;
    private bool showWhy;

    public NextStepRow(NextStep step, LocalizationService text, Action<NextStepAction> run)
    {
        Step = step; this.text = text;
        ActionCommand = new(() => run(step.Action));
        ToggleWhyCommand = new(() => ShowWhy = !ShowWhy);
    }

    public NextStep Step { get; }
    public RelayCommand ActionCommand { get; }
    public RelayCommand ToggleWhyCommand { get; }
    public IconKey Icon => Step.Rule switch
    {
        NextStepRule.DiskLow => IconKey.HardDrive20,
        NextStepRule.StartupAttention => IconKey.Rocket24,
        NextStepRule.SettingsReset => IconKey.History24,
        NextStepRule.RestartNeeded => IconKey.ArrowClockwise24,
        NextStepRule.NoProfile => IconKey.Sparkle24,
        NextStepRule.Scan => IconKey.Search24,
        NextStepRule.AdministratorMissing => IconKey.ShieldLock24,
        _ => IconKey.Shield24,
    };

    private string Rule => Step.Rule.ToString();
    private object[] Args => Step.Rule == NextStepRule.DiskLow
        ? [Step.Drive ?? "", text.Number(Step.Numbers[0]), text.Number(Step.Numbers[1])]
        : [.. Step.Numbers.Select(n => (object)text.Number(n))];
    public string Text => text.Format($"NextStep_{Rule}_{Step.Variant}", Args);
    public string WhyText => text.Format($"NextStepWhy_{Rule}", NextStepsService.RestorePointMaxAgeDays, NextStepsService.LowFreePercent, NextStepsService.LowFreeBytes / (1024 * 1024 * 1024), NextStepsService.ScanMaxAgeDays);
    public IconKey Chevron => text.Direction == TextDirection.RightToLeft ? IconKey.ChevronLeft20 : IconKey.ChevronRight20;
    public bool HasAction => Step.Action != NextStepAction.None;
    public string ActionText => HasAction ? text["NextStepButton_" + Step.Action] : "";
    public string ActionHelp => HasAction ? text["NextStepButtonHelp_" + Step.Action] : "";
    public string WhyLabel => text[showWhy ? "NextStepsWhyHide" : "NextStepsWhyLink"];
    public string WhyHelp => text["NextStepsWhyHelp"];
    public bool ShowWhy { get => showWhy; set { if (SetProperty(ref showWhy, value)) OnPropertyChanged(nameof(WhyLabel)); } }
    /// <summary>The row text without its full stop, for names that join it with a comma (a screen reader would say ".,").</summary>
    public string ItemName => Text.TrimEnd('.');
    public string AutomationName => ItemName + (HasAction ? ", " + ActionText : "");
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>The "Recommended next steps" list. It is recalculated when the Overview facts, the inventory or the history change, never on a timer.</summary>
public sealed class NextStepsViewModel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly NextStepsFacts facts;
    private readonly Action<NextStepAction> run;
    private readonly Func<DateTimeOffset?> readAt;
    private bool computing, dirty, ready;
    private Task work = Task.CompletedTask;

    public NextStepsViewModel(LocalizationService text, NextStepsFacts facts, Action<NextStepAction> run, Func<DateTimeOffset?> readAt)
    {
        this.text = text; this.facts = facts; this.run = run; this.readAt = readAt;
    }

    public ObservableCollection<NextStepRow> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public bool ShowEmpty => ready && Rows.Count == 0;
    public string EmptyTitle => text["NextStepsNoneTitle"];
    public string EmptyBody => readAt() is { } at ? text.Format("NextStepsNoneRead", text.When(at.ToLocalTime(), "t")) : text["NextStepsNoneBody"];
    public Task WhenIdleAsync() => work;

    public void Request()
    {
        if (computing) { dirty = true; return; }
        work = ComputeAsync();
    }

    /// <summary>Runs the rule table once on the current facts and waits for it (tests, and the first read).</summary>
    public async Task ComputeAsync()
    {
        computing = true;
        try
        {
            do
            {
                dirty = false;
                var input = await Task.Run(facts.Collect).ConfigureAwait(true);
                Show(NextStepsService.Evaluate(input));
            }
            while (dirty);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Show([]); }
        finally { computing = false; }
    }

    public void Show(IReadOnlyList<NextStep> steps)
    {
        // Request can be called from a worker thread (a page that reports a change from a task); the rows are bound to the UI.
        if (HostServices.UiThread is { IsAvailable: true } ui && !ui.CheckAccess()) { ui.Invoke(() => Show(steps)); return; }
        Rows.Clear();
        foreach (var step in steps) Rows.Add(new(step, text, run));
        ready = true;
        OnPropertyChanged(nameof(HasRows)); OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(EmptyBody));
    }

    public void LanguageChanged()
    {
        foreach (var row in Rows) row.Refresh();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// The latest changes on the Overview, with an undo per entry. The list comes from the History page, so both always agree. It has the members the shared
/// "Change history" block (<see cref="Controls.PageHistory"/>) binds to, so the Overview shows the same block as every other page.
/// </summary>
public sealed class RecentChangesViewModel : ObservableObject
{
    private readonly HistoryViewModel history;
    private readonly LocalizationService text;
    private readonly Action<string> navigate;

    public RecentChangesViewModel(HistoryViewModel history, LocalizationService text, Action<string> navigate)
    {
        this.history = history; this.text = text; this.navigate = navigate;
        SeeAllCommand = new(() => navigate("History"));
        OpenHistoryCommand = new(() => navigate("History"));
        DismissFailureCommand = new(() => { dismissedKey = failureKey; OnPropertyChanged(nameof(ShowFailure)); });
        UndoCommand = new(r => r is null ? Task.CompletedTask : history.UndoAsync(r.Item, null), r => r is { CanUndo: true } && !history.IsBusy);
        ToggleExpandCommand = new(r => { if (r is not null) r.IsExpanded = !r.IsExpanded; });
        history.SnapshotChanged += (_, _) => Rebuild();
        history.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HistoryViewModel.IsBusy)) UndoCommand.NotifyCanExecuteChanged(); };
    }

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];
    // ---- the members of the shared change history block ----
    public LocalizationService Text => text;
    public HistoryRowViewModel? Selected { get => selected; set => SetProperty(ref selected, value); }
    private HistoryRowViewModel? selected;
    public AsyncRelayCommand<HistoryRowViewModel> UndoRowCommand => UndoCommand;
    public RelayCommand<HistoryRowViewModel> ToggleExpandCommand { get; }
    public string Heading => text.Format("PageHistoryHeading", history.Snapshot.Items.Count);
    public string? Note => null;
    public bool HasNote => false;
    public string ShowAllText => text["PageHistoryShowAll"];
    public string ShowAllName => text["OverviewSeeAll"];
    public string ShowAllHelp => text["OverviewSeeAllHelp"];
    public RelayCommand ShowAllCommand => SeeAllCommand;
    // The Recent changes tab shows nothing else: the block is always open there and lists the newest few, with Show all in History for the rest.
    public bool CanCollapse => false;
    public bool ShowPlainHeading => true;
    public bool IsExpanded { get => true; set { } }
    public bool ShowBody => true;
    public bool HasPages => false;
    /// <summary>Reads the journals again when the Overview opens, so the block is filled even before the History page was visited.</summary>
    public void Activate() { if (!history.IsBusy) history.RefreshHistoryCommand.Execute(null); }
    public RelayCommand SeeAllCommand { get; }
    public RelayCommand OpenHistoryCommand { get; }
    public RelayCommand DismissFailureCommand { get; }
    public AsyncRelayCommand<HistoryRowViewModel> UndoCommand { get; }

    private string? failureKey, dismissedKey;
    private int failed, attempted;
    /// <summary>True when the newest change did not apply, or a batch only applied in part. The bar stays until the user closes it or a newer entry replaces it.</summary>
    public bool ShowFailure { get => failureKey is not null && failureKey != dismissedKey; set { if (!value) DismissFailureCommand.Execute(null); } }
    public string FailureTitle => text["V2OvFailTitle"];
    public string FailureMessage => attempted > 1 ? text.Format("V2OvFailMessage", failed, attempted) : text["V2OvFailMessageOne"];
    public string FailureAction => text["V2OvFailOpen"];
    public string FailureActionHelp => text["V2OvFailOpenHelp"];
    public bool HasRows => Rows.Count > 0;
    public bool ShowEmpty => history.IsLoaded && Rows.Count == 0;
    public string EmptyTitle => text["OverviewRecentEmptyTitle"];
    public string EmptyBody => text["OverviewRecentEmptyBody"];

    public void Rebuild()
    {
        Rows.Clear();
        foreach (var item in history.Snapshot.Items.Take(PageHistoryModel.VisibleRows)) Rows.Add(new(item, text, history.UndoMemberCommand));
        FindFailure();
        OnPropertyChanged(nameof(HasRows)); OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(Heading));
        foreach (var name in new[] { nameof(ShowFailure), nameof(FailureMessage), nameof(FailureTitle), nameof(FailureAction) }) OnPropertyChanged(name);
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void FindFailure()
    {
        failureKey = null; failed = 0; attempted = 0;
        if (history.Snapshot.Items.FirstOrDefault() is not { } newest) return;
        if (newest.IsBatch)
        {
            attempted = newest.Members.Count; failed = newest.Members.Count(m => m.Result == HistoryResult.Failed);
        }
        else { attempted = 1; failed = newest.Entry.Result == HistoryResult.Failed ? 1 : 0; }
        if (failed > 0) failureKey = newest.Entry.Key;
    }
}
