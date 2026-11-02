// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop;

/// <summary>One choice of a setting. ToString is the label because screen readers read the item text from it.</summary>
public sealed record OptimizeChoiceItem(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One machine setting of the catalog with its current choice and the choice the user picked. A setting that cannot be offered shows why instead of a list.</summary>
public sealed class OptimizeRow : ObservableObject
{
    private OptimizeChoiceItem? selected;
    private readonly Action changed;

    public OptimizeRow(OptimizeItem item, string title, string description, string categoryText, string riskText, string? restartText, string? problemText, IReadOnlyList<OptimizeChoiceItem> choices, string source, Action changed, string sourceClassText = "", string? targetText = null)
    {
        TargetText = targetText;
        Item = item; Title = title; Description = description; CategoryText = categoryText; RiskText = riskText; RestartText = restartText; ProblemText = problemText; Choices = choices; Source = source;
        SourceClassText = sourceClassText;
        this.changed = changed;
        selected = choices.FirstOrDefault(c => c.Id == item.CurrentChoiceId);
    }

    public OptimizeItem Item { get; }
    public MachineSettingDefinition Definition => Item.Definition;
    public string Title { get; }
    /// <summary>For a display driver setting: the name of the graphics card this row changes. Each card is its own row.</summary>
    public string? TargetText { get; }
    public bool HasTarget => TargetText is not null;
    /// <summary>The title with the graphics card name, for dialogs, the history and screen readers.</summary>
    public string DisplayTitle => TargetText is null ? Title : $"{Title} ({TargetText})";
    public string Description { get; }
    public string CategoryText { get; }
    public string RiskText { get; }
    public RiskLevel Risk => Definition.Risk;
    public SourceClass SourceClassValue => Definition.SourceClass;
    public string? RestartText { get; }
    public bool HasRestart => RestartText is not null;
    public string? ProblemText { get; }
    public bool HasProblem => ProblemText is not null;
    public bool IsAvailable => Item.IsAvailable;
    public IReadOnlyList<OptimizeChoiceItem> Choices { get; }
    public string Source { get; }
    /// <summary>Who documents the setting, in plain words: Microsoft documentation, vendor or expert material, or community material.</summary>
    public string SourceClassText { get; }
    /// <summary>High-risk settings are listed in the advanced section and need a second confirmation.</summary>
    public bool IsAdvanced => Definition.Risk == RiskLevel.High;
    public OptimizeChoiceItem? Current => Choices.FirstOrDefault(c => c.Id == Item.CurrentChoiceId);
    public OptimizeChoiceItem? SelectedChoice
    {
        get => selected;
        set { if (SetProperty(ref selected, value)) { OnPropertyChanged(nameof(IsDirty)); changed(); } }
    }
    public bool IsDirty => IsAvailable && selected is not null && selected.Id != Item.CurrentChoiceId;
    public void Discard() => SelectedChoice = Current;
    public string AutomationName => HasProblem ? $"{DisplayTitle}, {ProblemText}" : $"{DisplayTitle}, {Current?.Label}, {RiskText}";
}

public sealed class OptimizeHistoryRow(OptimizeRecord record, string description, string stateText, string when, bool canRevert, RelayCommand<OptimizeHistoryRow> revert)
{
    public OptimizeRecord Record { get; } = record;
    public string Description { get; } = description;
    public string StateText { get; } = stateText;
    public string When { get; } = when;
    public bool CanRevert { get; } = canRevert;
    public RelayCommand<OptimizeHistoryRow> RevertCommand { get; } = revert;
    public string AutomationName => $"{Description}, {StateText}, {When}";
}

/// <summary>
/// The Optimize page: the machine settings of the shipped catalog (each one documented by Microsoft) with their current state, a choice per setting, apply with a preview and one
/// confirmation, a journal, a read-back and an undo while nothing else has changed the value (decision DEC-IW-023). A setting that Group Policy or device management controls,
/// that does not apply to this Windows build or whose state is not one the catalog knows is shown with the reason and is never changed.
/// </summary>
public sealed class OptimizeViewModel : PageViewModel, IPendingSelectionSource, INoticePage, ISettingsPage
{
    private const int HistoryLimit = 30;
    private readonly OptimizeService service;
    private readonly IReviewPrompt prompt;
    private readonly IElevationState elevation;
    private readonly IRestorePointGate? restorePoint;
    private string? restoreNote;
    private Task work = Task.CompletedTask;
    private bool busy;
    private string? notice;
    private bool noticeIsError;
    private bool loaded;

    public OptimizeViewModel(LocalizationService text, SimulationModel model, OptimizeService service, IReviewPrompt prompt, IElevationState elevation, IRestorePointGate? restorePoint = null, IPagePreferences? preferences = null, Func<string, bool>? copy = null) : base("Optimize", text, model)
    {
        this.service = service; this.prompt = prompt; this.elevation = elevation; this.restorePoint = restorePoint;
        HistoryList = new(text, HistoryArea.Optimize, () => OpenHistory);
        List = new SettingsListModel(text, "Optimize", preferences) { Tiles = true, FeedbackSource = () => Feedback, UndoAllChanged = UndoAllChangedAsync };
        var clipboard = copy ?? HostServices.Clipboard.TrySetText;
        List.ShowDetails = row => prompt.ShowDetailsAsync(SettingDetails.Build(Text, clipboard, row));
        RefreshCommand = new(() => work = ReloadAsync(), () => !Busy);
        ApplyCommand = new(() => work = ApplyAsync(), () => CanApply);
        DiscardCommand = new(Discard, () => HasPending && !Busy);
        RevertHistory = new(row => { if (row is not null && !Busy) work = RevertAsync(row); }, _ => !Busy);
    }

    /// <summary>Search, filters, groups (with the Advanced section), the rail and the details pane.</summary>
    public SettingsListModel List { get; }
    public bool ShowList => loaded && HasRows;
    public string ActionBarSummary => PendingCount == 1 ? Text["ABPendingOne"] : Text.Format("ABPendingFormat", PendingCount);
    public System.Windows.Input.ICommand ReviewPendingCommand => ApplyCommand;
    public System.Windows.Input.ICommand DiscardPendingCommand => DiscardCommand;
    public IReadOnlyList<string> PendingLines => AllRows.Where(r => r.IsDirty).Select(r => $"{r.DisplayTitle}: {r.Current?.Label} {Text["ArrowGlyph"]} {r.SelectedChoice!.Label}").ToList();
    public override StagingView? Staging => PendingCount == 0 ? null : new(Key,
        AllRows.Where(r => r.IsDirty).Select(r => new StagedChange(r.Definition.Id, r.DisplayTitle, r.Current?.Label ?? "", r.SelectedChoice!.Label, r.Risk, () => { if (!Busy) r.Discard(); })).ToList(),
        ActionBarSummary, Text["ABReview"], Text["ABReviewHelp"], Text["Discard"], Text["DiscardHelp"],
        () => ApplyCommand.CanExecute(null), () => ApplyCommand.Execute(null), () => DiscardCommand.CanExecute(null), () => DiscardCommand.Execute(null));
    public ObservableCollection<OptimizeRow> Rows { get; } = [];
    /// <summary>The High-risk settings: a separate section below the others.</summary>
    public ObservableCollection<OptimizeRow> AdvancedRows { get; } = [];
    private IEnumerable<OptimizeRow> AllRows => Rows.Concat(AdvancedRows);
    public ObservableCollection<OptimizeHistoryRow> History { get; } = [];
    public PageHistoryModel HistoryList { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand<OptimizeHistoryRow> RevertHistory { get; }

    public override bool IsBusy => Busy;
    private bool Busy { get => busy; set { if (SetProperty(ref busy, value)) { OnPropertyChanged(nameof(IsBusy)); Notify(); } } }
    public bool IsElevated => elevation.IsElevated;
    public bool ShowNeedsAdministrator => !elevation.IsElevated;
    public string NeedsAdministratorTitle => elevation.UnsafeLocation ? Text["AppLocationUnsafeTitle"] : Text["AdminNoticeTitle"];
    public string NeedsAdministratorText => elevation.UnsafeLocation ? Text["AppLocationUnsafe"] : Text["AdminNoticeMessage"];
    public bool ShowAdminHowTo => !elevation.UnsafeLocation;
    public bool HasRows => Rows.Count > 0 || AdvancedRows.Count > 0;
    public bool HasNormalRows => Rows.Count > 0;
    public bool HasAdvanced => AdvancedRows.Count > 0;
    public bool ShowEmpty => loaded && !HasRows && !Busy;
    public bool HasHistory => History.Count > 0;
    public string? Notice { get => notice; private set { if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool NoticeIsError { get => noticeIsError; private set { if (SetProperty(ref noticeIsError, value)) OnPropertyChanged(nameof(NoticeSeverity)); } }
    public BannerSeverity NoticeSeverity => noticeIsError ? BannerSeverity.Warning : BannerSeverity.Success;
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool HasPending => AllRows.Any(r => r.IsDirty);
    public override int PendingCount => AllRows.Count(r => r.IsDirty);
    public IReadOnlyList<ProfileSelection> PendingSelection() => AllRows.Where(r => r.IsDirty).Select(r => new ProfileSelection(ProfileEntryKind.Machine, r.Definition.Id, r.SelectedChoice!.Id)).ToList();
    public bool CanApply => IsElevated && !Busy && HasPending;
    public string PendingText => AllRows.Count(r => r.IsDirty) is var n and > 0 ? Text.Format("OptimizePending", n) : Text["OptimizeNoPending"];
    public string AdvancedHeading => Text["OptimizeAdvancedHeading"];
    public string AdvancedIntro => Text["OptimizeAdvancedIntro"];
    public string UndoLabel => Text["PowerUndo"];
    public string UndoHelp => Text["OptimizeUndoHelp"];
    public string ChoiceLabel => Text["OptimizeChoiceLabel"];
    public string ChoiceHelp => Text["OptimizeChoiceHelp"];
    public string SourceLabel => Text["OptimizeSource"];
    public string AdapterLabel => Text["OptimizeAdapterLabel"];

    private void Notify()
    {
        RefreshCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged(); RevertHistory.NotifyCanExecuteChanged();
        foreach (var n in new[] { nameof(CanApply), nameof(HasPending), nameof(PendingText), nameof(ShowEmpty), nameof(HasRows), nameof(HasNormalRows), nameof(HasAdvanced), nameof(AdvancedHeading), nameof(AdvancedIntro), nameof(PendingCount), nameof(ActionBarSummary), nameof(PendingLines), nameof(ShowList) }) OnPropertyChanged(n);
        foreach (var row in settingRows.Values) row.Refresh();
        if (List.SelectedState.Id == StateFilter.Pending) List.Rebuild();
    }

    public override void Activate() { if ((!loaded || Journal.IsStale) && !Busy) work = ReloadAsync(); }
    public override Task WhenIdleAsync() => work;
    public override System.Windows.Input.ICommand? ShortcutRefresh() => RefreshCommand;
    protected override void OnLanguageChanged() { base.OnLanguageChanged(); if (loaded) Rebuild(items); List.Refresh(); }

    // ---- loading ----

    private IReadOnlyList<OptimizeItem> items = [];

    private async Task ReloadAsync()
    {
        Journal.MarkRead();
        Busy = true;
        try
        {
            items = await Task.Run(() => service.List()).ConfigureAwait(true);
            loaded = true;
            Rebuild(items);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError));
        }
        finally { Busy = false; OnPropertyChanged(nameof(HasRows)); Notify(); }
        RefreshHistory();
        foreach (var row in settingRows.Values) row.Refresh();
        List.Rebuild();
    }

    private void Rebuild(IReadOnlyList<OptimizeItem> source)
    {
        Rows.Clear(); AdvancedRows.Clear();
        foreach (var item in source.OrderBy(i => Text.Enum(i.Definition.Category), StringComparer.CurrentCultureIgnoreCase).ThenBy(i => SettingTitle(i.Definition), StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Adapter?.FriendlyName ?? "", StringComparer.CurrentCultureIgnoreCase))
        {
            var row = MakeRow(item);
            (row.IsAdvanced ? AdvancedRows : Rows).Add(row);
        }
        settingRows.Clear();
        foreach (var row in AllRows) settingRows[row] = new OptimizeSettingRow(row, this, Text);
        List.SetRows(settingRows.Values.ToList());
        OnPropertyChanged(nameof(HasRows)); Notify();
    }

    private readonly Dictionary<OptimizeRow, OptimizeSettingRow> settingRows = [];

    /// <summary>The newest applied change of this setting (and graphics card) that was not itself an undo.</summary>
    private OptimizeHistoryRow? LatestChange(OptimizeRow row) =>
        History.FirstOrDefault(h => h.Record.Change.DefinitionId == row.Definition.Id && h.Record.Change.TargetInstanceId == row.Item.Adapter?.InstanceId && h.Record.State == OptimizeState.Applied);

    /// <summary>Winora set the value that is in effect now, or set one once and something else changed it after.</summary>
    public ChangeOrigin OriginOf(OptimizeRow row)
    {
        if (LatestChange(row) is not { } latest || latest.Record.Change.RevertOf is not null || !row.IsAvailable) return ChangeOrigin.None;
        return latest.Record.Change.ChoiceId == row.Item.CurrentChoiceId ? ChangeOrigin.ByWinora : ChangeOrigin.Since;
    }

    public bool CanUndoLatest(OptimizeRow row) => !Busy && LatestChange(row) is { CanRevert: true, Record.Change.RevertOf: null };

    public void UndoLatest(OptimizeRow row)
    {
        if (CanUndoLatest(row) && LatestChange(row) is { } latest) work = RevertAsync(latest);
    }

    private Task UndoAllChangedAsync()
    {
        var targets = AllRows.Where(r => OriginOf(r) == ChangeOrigin.ByWinora && CanUndoLatest(r)).Select(LatestChange).OfType<OptimizeHistoryRow>().DistinctBy(h => h.Record.Change.Id).ToList();
        return work = RevertManyAsync(targets);
    }

    private async Task RevertManyAsync(IReadOnlyList<OptimizeHistoryRow> targets)
    {
        if (targets.Count == 0) return;
        var confirmation = new ActionConfirmation(Text.Plural("SetReviewTitleRevertBulk", targets.Count, targets.Count), Text["OptimizeRevertBody"], targets.Select(t => t.Description).ToList(), Text["PowerUndoNote"], Text.Format("SetRevertAllButton", targets.Count))
        {
            Changes = targets.Select(t => Reversed(t.Record.Change)).ToList(),
            Risk = targets.Max(t => Definition(t.Record.Change.DefinitionId)?.Risk ?? RiskLevel.Low), Undo = ConfirmUndo.WhileUnchanged,
            Restart = targets.Any(t => Definition(t.Record.Change.DefinitionId)?.RequiresRestart == true), UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        await RunAsync(targets.Select(t => (Func<Task<OptimizeApplyResult>>)(() => service.RevertAsync(t.Record.Change.Id)))).ConfigureAwait(true);
    }

    private string SettingTitle(MachineSettingDefinition d) => Text.Has(d.TitleKey) ? Text[d.TitleKey] : d.Id;
    private string LabelOf(MachineChoice c) => Text.Has(c.LabelKey) ? Text[c.LabelKey] : c.Id;

    private OptimizeRow MakeRow(OptimizeItem item)
    {
        var d = item.Definition;
        var choices = d.Choices.Select(c => new OptimizeChoiceItem(c.Id, LabelOf(c))).ToList();
        var source = d.SourceRefs.FirstOrDefault() ?? d.CommunitySources.FirstOrDefault() ?? "";
        return new(item, SettingTitle(d), Text.Has(d.DescriptionKey) ? Text[d.DescriptionKey] : "", Text.Enum(d.Category), Text.Enum(d.Risk), d.RequiresRestart ? Text["BadgeRestart"] : null,
            item.Problem is { } p ? ProblemText(p) : null, choices, source, Notify, Text["OptimizeSourceClass_" + d.SourceClass], item.Adapter?.FriendlyName);
    }

    public string ProblemText(string code)
    {
        var key = "OptimizeCode_" + code.Replace('.', '_');
        return Text.Has(key) ? Text[key] : Text["OptimizeCode_optimize_failed"];
    }

    // ---- actions ----

    private void Discard() { foreach (var row in AllRows) row.Discard(); Notice = null; Notify(); }

    private async Task ApplyAsync()
    {
        var dirty = AllRows.Where(r => r.IsDirty).ToList();
        if (dirty.Count == 0) return;
        var lines = dirty.Select(r => Text.Format("OptimizeChangeLine", r.DisplayTitle, r.Current?.Label ?? "", r.SelectedChoice!.Label)).ToList();
        var restart = dirty.Any(r => r.Definition.RequiresRestart);
        var high = dirty.Where(r => r.IsAdvanced).ToList();
        var confirmation = new ActionConfirmation(Text[high.Count > 0 ? "OptimizeHighTitle" : "OptimizeApplyTitle"], Text[high.Count > 0 ? "OptimizeHighBody" : "OptimizeApplyBody"], lines,
            Text[restart ? "OptimizeRestartNote" : high.Count > 0 ? "OptimizeHighNote" : "PowerUndoNote"], Text["OptimizeApplyPrimary"])
        {
            Changes = dirty.Select(r => new ConfirmChange(r.DisplayTitle, r.Current?.Label ?? "", r.SelectedChoice!.Label)).ToList(),
            Risk = dirty.Max(r => r.Risk), Undo = ConfirmUndo.WhileUnchanged, Restart = restart, UsesRestorePoint = restorePoint is not null,
            HighRiskNames = high.Select(r => r.DisplayTitle).ToList(),
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        var requests = dirty.Select(r => new OptimizeRequest(r.Definition.Id, r.SelectedChoice!.Id, r.Item.Current is { } c ? StoredValue.From(c) : null, r.Item.Adapter?.InstanceId)).ToList();
        await RunAsync(requests.Select(r => (Func<Task<OptimizeApplyResult>>)(() => service.ApplyAsync(r)))).ConfigureAwait(true);
    }

    private async Task RevertAsync(OptimizeHistoryRow row)
    {
        if (Busy) return;
        var confirmation = new ActionConfirmation(Text["OptimizeRevertTitle"], Text["OptimizeRevertBody"], [row.Description], Text["PowerUndoNote"], Text["PowerRevertPrimary"])
        {
            Changes = [Reversed(row.Record.Change)], Risk = Definition(row.Record.Change.DefinitionId)?.Risk ?? RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged,
            Restart = Definition(row.Record.Change.DefinitionId)?.RequiresRestart, UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true) || Busy) return;
        await RunAsync([() => service.RevertAsync(row.Record.Change.Id)]).ConfigureAwait(true);
    }

    private async Task RunAsync(IEnumerable<Func<Task<OptimizeApplyResult>>> actions)
    {
        if (Busy) return; // one write at a time
        using var run = Runs.Begin(Text.Format("ACRunApply", Title), true);
        if (!run.Started) return;
        restoreNote = null;
        if (restorePoint is not null)
        {
            var gate = await restorePoint.BeforeChangeAsync("Optimize").ConfigureAwait(true);
            if (!gate.Proceed || Busy) return;
            restoreNote = RestorePointText.Notice(Text, gate);
        }
        Busy = true; Notice = null;
        var results = new List<OptimizeApplyResult>();
        try
        {
            foreach (var action in actions) results.Add(await action().ConfigureAwait(true));
            Report(results);
            run.Finish(NoticeIsError ? RunResult.Warning : RunResult.Ok, Notice);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError));
            run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title));
        }
        finally { Busy = false; }
        Journal.Wrote(HistoryArea.Optimize);
        await ReloadAsync().ConfigureAwait(true);
    }

    private void Report(IReadOnlyList<OptimizeApplyResult> results)
    {
        var applied = results.Count(r => r.Ok);
        var problems = results.Where(r => !r.Ok).Select(r => r.Code).Distinct().Select(ProblemText).ToList();
        var restart = results.Where(r => r.Ok && r.Change is { } c && Definition(c.DefinitionId)?.RequiresRestart == true).Any();
        if (problems.Count == 0)
        {
            Notice = (applied == 1 ? Text["OptimizeAppliedOne"] : Text.Format("OptimizeApplied", applied)) + (restart ? " " + Text["OptimizeRestartNeeded"] : "");
            NoticeIsError = false;
        }
        else
        {
            Notice = applied > 0 ? Text.Format("OptimizePartial", applied, string.Join(" ", problems)) : string.Join(" ", problems);
            NoticeIsError = true;
        }
        if (results.Any(r => r.RecordLost)) Notice += " " + Text["RecordLostNote"];
        if (restoreNote is not null) Notice += " " + restoreNote;
        OnPropertyChanged(nameof(NoticeIsError));
        if (results.Count > 1)
        {
            var failures = results.Where(r => !r.Ok).Select(r => new ResultFailure(Definition(r.Change?.DefinitionId ?? "") is { } d ? SettingTitle(d) : r.Code, ProblemText(r.Code), Text["ReportFailedNext"])).ToList();
            Feedback.ShowReport(Text.Format("ReportHeadlineFormat", applied, results.Count), applied, 0, failures);
        }
    }

    private MachineSettingDefinition? Definition(string id) => items.FirstOrDefault(i => i.Definition.Id == id)?.Definition;

    // ---- history ----

    private void RefreshHistory()
    {
        History.Clear();
        var shown = new List<HistoryRowViewModel>();
        IReadOnlyList<OptimizeRecord> records;
        try { records = service.History(HistoryLimit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { records = []; }
        var revertable = IsElevated ? service.CanRevertAll(records) : [];
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            History.Add(new(r, Describe(r.Change), r.State is { } s ? Text.Enum(s) : Text["PowerInterrupted"], Text.When(r.BeganUtc.ToLocalTime(), "g"), IsElevated && revertable[i], RevertHistory));
            var h = History[^1];
            shown.Add(HistoryList.Row(r.Change.Id, r.BeganUtc, r.State == OptimizeState.Applied ? (records.Any(o => o.Change.RevertOf == r.Change.Id && o.State == OptimizeState.Applied) ? HistoryResult.Undone : HistoryResult.Applied) : r.State == OptimizeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed, h.Description, h.CanRevert, h.RevertCommand, h, new(UndoLabel, UndoHelp)));
        }
        HistoryList.Set(shown);
        OnPropertyChanged(nameof(HasHistory));
    }

    private string ChoiceLabelOf(MachineSettingDefinition? d, StoredValue v)
    {
        var value = v.ToValue();
        var choice = d?.Choices.FirstOrDefault(x => x.EffectiveValue is { } ev && value is not null && ev.Equals(value));
        return choice is null ? Text["OptimizeOtherValue"] : LabelOf(choice);
    }

    private string TitleOf(OptimizeChange c)
    {
        var d = Definition(c.DefinitionId);
        return (d is null ? c.DefinitionId : SettingTitle(d)) + (c.TargetName is { } card ? $" ({card})" : "");
    }

    /// <summary>The undo review: the value now (what the change left) and the value it goes back to.</summary>
    private ConfirmChange Reversed(OptimizeChange c)
    {
        var d = Definition(c.DefinitionId);
        return new(TitleOf(c), ChoiceLabelOf(d, c.After), ChoiceLabelOf(d, c.Before));
    }

    private string Describe(OptimizeChange c)
    {
        var d = Definition(c.DefinitionId);
        return Text.Format("OptimizeHistoryLine", TitleOf(c), ChoiceLabelOf(d, c.Before), ChoiceLabelOf(d, c.After)) + (c.RevertOf is null ? "" : " " + Text["PowerUndoSuffix"]);
    }
}
