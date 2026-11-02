// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>A category heading with the cards that currently pass the search and filter.</summary>
public sealed class SettingGroupViewModel(SettingCategory category, IReadOnlyList<SettingCardViewModel> cards, SettingFormat format, LocalizationService text) : ObservableObject
{
    public SettingCategory Category { get; } = category;
    public IReadOnlyList<SettingCardViewModel> Cards { get; } = cards;
    public string Title => format.Category(Category);
    public string CountText => text.Format("SetGroupCountFormat", Cards.Count);
    public string AutomationName => $"{Title}, {CountText}";
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class CategoryOption(SettingCategory? category, Func<string> label) : ObservableObject
{
    public SettingCategory? Category { get; } = category;
    public string Label => label();
    public void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// The Customize page: the real settings catalog on the settings page (filters, category rail, folding groups, details pane, pending bar). All real actions end in the review
/// dialog; there is no auto-apply. The cards stay the unit of work; <see cref="List"/> only decides which of them are shown and how.
/// </summary>
public sealed class CustomizeViewModel : PageViewModel, IPendingSelectionSource, ISettingsPage
{
    private readonly SettingsModel settings;
    private readonly List<SettingCardViewModel> cards = [];
    private readonly Dictionary<string, CustomizeRow> rows = [];
    private Task loadTask = Task.CompletedTask;
    private IReadOnlyList<ChangeRequest> lastRequests = [];
    private ChangeKind lastKind;
    private int groupVersion = -1;

    public CustomizeViewModel(LocalizationService text, SimulationModel model, SettingsModel settings, IPagePreferences? preferences = null, Func<string, bool>? copy = null) : base("Customize", text, model)
    {
        this.settings = settings;
        List = new SettingsListModel(text, "Customize", preferences) { Tiles = true, FeedbackSource = () => Feedback, UndoAllChanged = UndoAllChangedAsync };
        var clipboard = copy ?? HostServices.Clipboard.TrySetText;
        List.ShowDetails = row => settings.ShowDetailsAsync(SettingDetails.Build(Text, clipboard, row));
        CategoryOptions =
        [
            new(null, () => Text["SetCategoryAll"]),
            .. SettingCategories.PerUser.Select(c => new CategoryOption(c, () => settings.Format.Category(c))),
        ];
        RefreshCommand = new(() => settings.RefreshAsync(), () => !IsBusy);
        ReviewAllCommand = new(ReviewAllAsync, () => PendingCount > 0 && !IsBusy);
        CancelCommand = new(settings.Cancel, () => settings.ShowProgress);
        ClearFiltersCommand = new(() => List.ClearFilters());
        DiscardPendingCommand = new(DiscardAll, () => PendingCount > 0 && !settings.IsBusy);
        List.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsListModel.CountText)) OnListRebuilt(); };
        HistoryList = new(text, HistoryArea.Settings, () => OpenHistory);
        RevertHistoryCommand = new(id => settings.ReviewAndRevertAsync(id), _ => !IsBusy);
        settings.Changed += (_, _) => OnSettingsChanged();
        settings.ReviewAgainRequested += OnReviewAgain;
        OnSettingsChanged();
    }

    /// <summary>The page's change history block: the same rows as the Settings area of the History page (same reader), undone through the page's review.</summary>
    public PageHistoryModel HistoryList { get; }
    public AsyncRelayCommand<Guid> RevertHistoryCommand { get; }

    private void RebuildHistory()
    {
        var entries = new SettingsHistorySource(settings, Text).Read().OrderByDescending(e => e.AtUtc).ToList();
        HistoryList.Set(entries.Select(e => HistoryList.Row(e.Id, e.AtUtc, e.Result, e.Title, e.CanUndo, RevertHistoryCommand, e.Id, new(null, Text["PowerUndo"], Text["OptimizeUndoHelp"]))).ToList());
    }

    /// <summary>Search, filters, groups, the rail and the details pane.</summary>
    public SettingsListModel List { get; }
    public SettingsModel Settings => settings;
    public SettingsNotice Notice => settings.Notice;
    public override bool ShowsSimulationBanner => false;
    public override bool IsBusy => settings.IsBusy || Model.IsBusy;

    public IReadOnlyList<CategoryOption> CategoryOptions { get; }
    /// <summary>The visible settings by category, all groups open. The list on screen is <see cref="List"/>; this keeps the plain grouping for code that wants it.</summary>
    public ObservableCollection<SettingGroupViewModel> Groups { get; } = [];
    public IReadOnlyList<SettingCardViewModel> Cards => cards;
    public AsyncRelayCommand RefreshCommand { get; }
    public override System.Windows.Input.ICommand? ShortcutRefresh() => RefreshCommand;
    public AsyncRelayCommand ReviewAllCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand DiscardPendingCommand { get; }
    public AsyncRelayCommand ReviewPendingCommand => ReviewAllCommand;

    public string SearchText
    {
        get => List.SearchText;
        set { if (List.SearchText != (value ?? "")) { List.SearchText = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasActiveFilter)); } }
    }
    public CategoryOption SelectedCategory
    {
        get => CategoryOptions.FirstOrDefault(o => o.Category?.ToString() == List.SelectedCategory.Key) ?? CategoryOptions[0];
        set
        {
            if (value is null) return;
            var item = List.Categories.FirstOrDefault(c => c.Key == value.Category?.ToString()) ?? List.AllCategory;
            if (ReferenceEquals(item, List.SelectedCategory)) return;
            List.SelectedCategory = item;
            OnPropertyChanged(); OnPropertyChanged(nameof(HasActiveFilter));
        }
    }
    public bool HasActiveFilter => List.HasActiveFilter;

    public override int PendingCount => cards.Count(c => c.IsChangeStaged);
    public IReadOnlyList<ProfileSelection> PendingSelection() => cards.Where(c => c.IsChangeStaged).Select(c => new ProfileSelection(ProfileEntryKind.User, c.Id, c.StagedChoiceId!)).ToList();
    public string ReviewAllLabel => Text.Format("SetReviewAllFormat", PendingCount);
    public string PendingSummary => PendingCount == 0 ? Text["SetNoPending"] : Text.Format("SetPendingSummaryFormat", PendingCount);
    public string ActionBarSummary => PendingCount == 1 ? Text["ABPendingOne"] : Text.Format("ABPendingFormat", PendingCount);
    public IReadOnlyList<string> PendingLines => cards.Where(c => c.IsChangeStaged).Select(c => $"{c.Title}: {c.CurrentLabel} {Text["ArrowGlyph"]} {settings.Format.Choice(c.Definition, c.StagedChoiceId)}").ToList();
    public override StagingView? Staging => PendingCount == 0 ? null : new(Key,
        cards.Where(c => c.IsChangeStaged).Select(c => new StagedChange(c.Id, c.Title, c.CurrentLabel, settings.Format.Choice(c.Definition, c.StagedChoiceId), c.Definition.Risk, () => { if (!settings.IsBusy) c.StagedChoiceId = null; })).ToList(),
        ActionBarSummary, Text["ABReview"], Text["ABReviewHelp"], Text["Discard"], Text["DiscardHelp"],
        () => ReviewAllCommand.CanExecute(null), () => ReviewAllCommand.Execute(null), () => DiscardPendingCommand.CanExecute(null), () => DiscardPendingCommand.Execute(null));
    public string CountText => List.CountText;

    // ---- Page states: exactly one of progress, failure, empty, no matches or the list is shown ----
    public bool ShowProgressState => !settings.Loaded && settings.IsBusy;
    public bool ShowFailure => !settings.Loaded && !settings.IsBusy && settings.LoadError is not null;
    public bool ShowNotLoaded => !settings.Loaded && !settings.IsBusy && settings.LoadError is null;
    public bool ShowEmptyCatalog => settings.Loaded && cards.Count == 0;
    public bool ShowNoMatches => settings.Loaded && cards.Count > 0 && Groups.Count == 0;
    public bool ShowList => settings.Loaded && cards.Count > 0;
    public bool ShowToolbar => settings.Loaded && cards.Count > 0;
    public string FailureMessage => settings.LoadError is { } e ? errorsText(e) : "";
    public string FailureDetails => settings.LoadError is { } e ? ErrorPresenter.Diagnostics(e) : "";
    private string errorsText(WinoraError error) => Text.Has(error.UserMessageKey) ? Text[error.UserMessageKey] : Text[ErrorCatalog.ResourceKey(ErrorCode.Unexpected)];

    public override void Activate()
    {
        if (!settings.Loaded && !settings.IsBusy) loadTask = settings.LoadAsync();
    }
    public override Task WhenIdleAsync() => loadTask;

    /// <summary>One review dialog for every staged change. Applied one after another, stopping at the first failure.</summary>
    private Task ReviewAllAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        var staged = cards.Where(c => c.IsChangeStaged).Select(c => new ChangeRequest(c.Id, c.StagedChoiceId!)).ToList();
        return RunReview(staged, ChangeKind.Bulk);
    }

    private void DiscardAll() { foreach (var card in cards.Where(c => c.IsChangeStaged).ToList()) card.DiscardCommand.Execute(null); }

    private Task ReviewOneAsync(SettingCardViewModel card) => !card.CanReview ? Task.CompletedTask : RunReview([new(card.Id, card.StagedChoiceId!)], ChangeKind.Apply);

    private Task RunReview(IReadOnlyList<ChangeRequest> requests, ChangeKind kind)
    {
        lastRequests = requests; lastKind = kind;
        return settings.ReviewAndApplyAsync(requests, kind);
    }

    private void OnReviewAgain(string settingId)
    {
        // A conflict or stale review: repeat the same request so the user sees the new before value, then decides again.
        if (lastRequests.Any(r => r.SettingId == settingId)) _ = RunReview(lastRequests, lastKind);
    }

    /// <summary>"Undo all" of the Changed by Winora view: one review with every reversed value, then each undo in turn.</summary>
    private Task UndoAllChangedAsync()
    {
        var ids = cards.Select(c => c.RevertTarget).Where(o => o is not null && rows.TryGetValue(o.SettingId, out var r) && r.Origin == ChangeOrigin.ByWinora).Select(o => o!.OperationId).Distinct().ToList();
        return settings.ReviewAndRevertManyAsync(ids);
    }

    private void OnSettingsChanged()
    {
        SyncCards();
        List.SetRows(cards.Select(c => rows[c.Id]).ToList());
        foreach (var name in new[] { nameof(IsBusy), nameof(ShowProgressState), nameof(ShowFailure), nameof(ShowNotLoaded), nameof(FailureMessage), nameof(FailureDetails), nameof(ShowEmptyCatalog), nameof(ShowList), nameof(ShowToolbar) })
            OnPropertyChanged(name);
        RefreshCommand.NotifyCanExecuteChanged(); ReviewAllCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); DiscardPendingCommand.NotifyCanExecuteChanged();
        RevertHistoryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowProgress));
        RebuildHistory();
    }
    public bool ShowProgress => settings.ShowProgress;

    private void SyncCards()
    {
        foreach (var state in settings.States)
        {
            var existing = cards.FirstOrDefault(c => c.Id == state.Definition.Id);
            if (existing is null)
            {
                var card = new SettingCardViewModel(state, settings, Text, ReviewOneAsync);
                card.StagedChanged += (_, _) => OnStagedChanged();
                cards.Add(card);
                rows[card.Id] = new CustomizeRow(card, settings, Text);
            }
            else existing.Update(state);
        }
        foreach (var gone in cards.Where(c => settings.States.All(s => s.Definition.Id != c.Id)).ToList()) { cards.Remove(gone); rows.Remove(gone.Id); }
    }

    private void OnStagedChanged()
    {
        OnPropertyChanged(nameof(PendingCount)); OnPropertyChanged(nameof(ReviewAllLabel)); OnPropertyChanged(nameof(PendingSummary)); OnPropertyChanged(nameof(ActionBarSummary)); OnPropertyChanged(nameof(PendingLines));
        ReviewAllCommand.NotifyCanExecuteChanged(); DiscardPendingCommand.NotifyCanExecuteChanged();
        // A pending filter depends on it.
        if (List.SelectedState.Id == StateFilter.Pending) List.Rebuild();
    }

    /// <summary>The list was rebuilt (a filter, the rows or the language changed): the plain grouping follows, all groups open.</summary>
    private void OnListRebuilt()
    {
        var visible = List.VisibleRows.OfType<CustomizeRow>().Select(r => r.Card).ToList();
        var groups = visible.GroupBy(c => c.Definition.Category).OrderBy(g => g.Key).ToList();
        var signature = string.Join('|', groups.Select(g => g.Key + ":" + string.Join(',', g.Select(c => c.Id))));
        var version = signature.GetHashCode();
        if (version != groupVersion)
        {
            groupVersion = version;
            Groups.Clear();
            foreach (var g in groups) Groups.Add(new(g.Key, g.ToList(), settings.Format, Text));
        }
        else foreach (var g in Groups) g.Refresh();
        foreach (var n in new[] { nameof(ShowNoMatches), nameof(CountText), nameof(HasActiveFilter), nameof(PendingCount), nameof(ReviewAllLabel), nameof(PendingSummary), nameof(SearchText), nameof(SelectedCategory) }) OnPropertyChanged(n);
        ReviewAllCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        foreach (var option in CategoryOptions) option.Refresh();
        foreach (var card in cards) card.Refresh();
        groupVersion = -1;
        List.Refresh();
        base.OnLanguageChanged();
    }

    protected override void OnModelChanged() { base.OnModelChanged(); RefreshCommand.NotifyCanExecuteChanged(); ReviewAllCommand.NotifyCanExecuteChanged(); }
}
