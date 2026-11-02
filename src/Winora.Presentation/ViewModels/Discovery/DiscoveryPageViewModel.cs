// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>Layout state the view reports: on narrow windows the filters fold behind a button.</summary>
public interface IDiscoveryLayout
{
    bool IsCompactLayout { get; set; }
    /// <summary>True for a page whose rows carry buttons and switches of their own: Tab walks through them. The other pages are walked with the arrow keys.</summary>
    bool RowsHaveControls { get; }
    /// <summary>Opens the details dialog of a row (the parameter is the row).</summary>
    System.Windows.Input.ICommand ShowDetailsCommand { get; }
    /// <summary>Space on a focused row: flips its switch or tick box. False when the row has none (or it is locked).</summary>
    bool ToggleRow(DiscoveryRow row);
    /// <summary>What the list shows: the rows, or the rows with their group headers.</summary>
    System.Collections.IEnumerable ListItems { get; }
    /// <summary>The list has rows to show (not a state such as scanning, failed or empty).</summary>
    bool ShowList { get; }
    /// <summary>The history of changes below the list, when the page keeps one.</summary>
    object? FooterPanel { get; }
}

/// <summary>
/// One inventory page: scan, partial results, search, sort, filters and the details dialog of a row. The base class exposes no command that
/// changes Windows; a page that changes something supplies its own row action and panels. Scans run off the UI thread, one at a time,
/// and are cancelled when the page is left or a new scan starts.
/// </summary>
public abstract class DiscoveryPageViewModel<T> : PageViewModel, IDiscoveryLayout
{
    private readonly record struct Entry(T Item, DiscoveryRow Row, string Haystack);

    private readonly ErrorPresenter errors;
    private readonly InventorySlot<T> slot;
    private readonly List<ChoiceFilter> choiceFilters = [];
    private readonly List<ToggleFilter> toggleFilters = [];
    private readonly List<ToggleFilter> hiddenToggles = [];
    private readonly List<SortChoice> sortOptions = [];
    private readonly HashSet<Task> pending = [];
    private Entry[] entries = [];
    private Winora.Domain.DiscoveryResult<T>? result;
    private WinoraError? failure;
    private DateTimeOffset? failedAt;
    private CancellationTokenSource? scan;
    private CancellationTokenSource? debounce;
    private CultureInfo builtCulture;
    private int buildVersion;
    private bool isScanning;
    private string search = "";
    private bool sortDescending;
    private SortChoice? selectedSort;
    private DiscoveryRow? selectedRow;
    private IReadOnlyList<DiscoveryRow> rows = [];
    private bool detailsOpen;
    private Task detailsTask = Task.CompletedTask;

    protected DiscoveryPageViewModel(string key, IconKey icon, LocalizationService text, SimulationModel model, ErrorPresenter errors, InventorySlot<T> slot)
        : base(key, text, model)
    {
        this.errors = errors; this.slot = slot; Icon = icon; builtCulture = text.Culture;
        RefreshCommand = new(ScanAsync, () => !IsScanning);
        CancelCommand = new(CancelScan, () => IsScanning);
        ShowDetailsCommand = new(ShowDetailsAsync, row => row is not null && DetailsHost is not null && !detailsOpen);
        ClearFiltersCommand = new(ClearFilters);
        ToggleSortDirectionCommand = new(() => SortDescending = !SortDescending);
        ToggleFiltersCommand = new(() => FiltersOpen = !FiltersOpen);
        TryAgainSlowCommand = new(() => Start(ScanAsync), () => IsScanning);
    }

    // ---- A scan that takes too long ----
    /// <summary>After this long a running scan shows "This is taking too long" with Try again and Cancel. The wait exists only while a scan runs.</summary>
    public TimeSpan SlowAfter { get; set; } = TimeSpan.FromSeconds(15);
    private bool slow;
    public bool IsTakingTooLong { get => slow; private set { if (SetProperty(ref slow, value)) { OnPropertyChanged(nameof(ShowSlow)); OnPropertyChanged(nameof(ShowProgress)); } } }
    public bool ShowSlow => slow && IsScanning;
    public RelayCommand TryAgainSlowCommand { get; }

    private async Task WatchSlowAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SlowAfter, token).ConfigureAwait(true);
            if (IsScanning && !token.IsCancellationRequested) IsTakingTooLong = true;
        }
        catch (OperationCanceledException) { }
    }

    // ---- Page-specific definition (called from derived constructors and overrides) ----
    /// <summary>False when the page is shown inside another page that already has the heading.</summary>
    public virtual bool ShowTitle => true;
    protected abstract DiscoveryRow BuildRow(T item);

    private DiscoveryRow Named(DiscoveryRow row) { row.MoreName = Text.Format("RowMoreName", row.Title); return row; }
    protected abstract IEnumerable<string?> SearchFields(T item);

    protected ChoiceFilter AddChoice<TEnum>(string labelKey, Func<T, TEnum> facet) where TEnum : struct, Enum
    {
        var choices = new List<FilterChoice> { new(null, () => Text["DiscFilterAll"]) };
        foreach (var v in Enum.GetValues<TEnum>()) { var value = v; choices.Add(new(value.ToString(), () => Text.Enum(value))); }
        var filter = new ChoiceFilter(labelKey, choices, o => facet((T)o).ToString(), Text);
        AddFilter(filter);
        return filter;
    }
    protected ChoiceFilter AddYesNoChoice(string labelKey, Func<T, bool> facet)
    {
        List<FilterChoice> choices = [new(null, () => Text["DiscFilterAll"]), new("yes", () => Text["ValueYes"]), new("no", () => Text["ValueNo"])];
        var filter = new ChoiceFilter(labelKey, choices, o => facet((T)o) ? "yes" : "no", Text);
        AddFilter(filter);
        return filter;
    }
    protected ToggleFilter AddToggle(string labelKey, Func<T, bool> hides)
    {
        var filter = new ToggleFilter(labelKey, o => hides((T)o), Text);
        filter.Changed += (_, _) => ApplyView();
        toggleFilters.Add(filter);
        return filter;
    }
    /// <summary>Sets a dropdown filter to the choice with this value; null is "All".</summary>
    protected static void SetChoice(ChoiceFilter filter, string? value) => filter.Selected = filter.Choices.First(c => c.Value == value);
    /// <summary>True while the dropdown filter shows the choice with this value; null is "All".</summary>
    protected static bool IsChoice(ChoiceFilter filter, string? value) => filter.Selected.Value == value;
    /// <summary>A filter the page drives itself (a chip): it narrows and clears like the others but has no checkbox in the filter row.</summary>
    protected ToggleFilter AddHiddenToggle(string labelKey, Func<T, bool> hides)
    {
        var filter = new ToggleFilter(labelKey, o => hides((T)o), Text);
        filter.Changed += (_, _) => ApplyView();
        hiddenToggles.Add(filter);
        return filter;
    }
    protected void AddSort(string labelKey, Func<T, IComparable?> key)
    {
        var choice = new SortChoice(labelKey, o => key((T)o), Text);
        sortOptions.Add(choice); selectedSort ??= choice;
    }
    private void AddFilter(ChoiceFilter filter) { filter.Changed += (_, _) => ApplyView(); choiceFilters.Add(filter); }

    // ---- Quick chips and the summary strip (a page fills them; the others show neither) ----
    private readonly List<QuickChip> quickChips = [];
    public IReadOnlyList<QuickChip> QuickChips => quickChips;
    public bool ShowQuickChips => quickChips.Count > 0 && (ShowList || ShowNoMatches);
    public string QuickChipsName => Text["V2MaChipsName"];
    public QuickChip? SelectedQuickChip { get => quickChips.FirstOrDefault(c => c.IsSelected); set => value?.SelectCommand.Execute(null); }
    protected void AddQuickChip(string key, string labelKey, Action select, Func<bool> isSelected) => quickChips.Add(new(key, () => Text[labelKey], select, isSelected));

    /// <summary>A chip that shows one value of a dropdown filter (null clears it). The other dropdowns the chips manage are cleared first, so the chips act as one choice.</summary>
    protected void AddChoiceChip(string key, string labelKey, IReadOnlyList<ChoiceFilter> managed, ChoiceFilter? filter, string? value) =>
        AddQuickChip(key, labelKey,
            () => { foreach (var other in managed) if (!ReferenceEquals(other, filter) && other.IsActive) other.Reset(); if (filter is not null) SetChoice(filter, value); },
            () => managed.All(m => ReferenceEquals(m, filter) ? IsChoice(m, value) : !m.IsActive));

    private IReadOnlyList<SummaryStat> summary = [];
    public IReadOnlyList<SummaryStat> Summary => summary;
    public bool HasSummary => summary.Count > 0;
    public string SummaryName => Text["V2MaSummaryName"];
    /// <summary>The figures of the strip for the items of the last scan. An empty list hides the strip.</summary>
    protected virtual IEnumerable<SummaryStat> BuildSummary(IReadOnlyList<T> items) => [];
    private void RefreshSummary()
    {
        summary = result is null || failure is not null || result.Items.Count == 0 ? [] : [.. BuildSummary(result.Items)];
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(HasSummary));
    }
    private void RefreshChips()
    {
        foreach (var chip in quickChips) chip.Refresh();
        OnPropertyChanged(nameof(SelectedQuickChip)); OnPropertyChanged(nameof(ShowQuickChips));
    }

    // ---- Helpers for derived pages ----
    protected string Or(string? value) => string.IsNullOrWhiteSpace(value) ? Text["ValueNotRecorded"] : value;
    protected string YesNo(bool value) => Text[value ? "ValueYes" : "ValueNo"];
    protected string When(DateTimeOffset? value) => value is { } v ? Text.When(v.ToLocalTime(), "g") : Text["ValueNotRecorded"];
    protected static BadgeModel Neutral(string label) => new(label, Tone.Neutral);

    // ---- Extension points for a page that does more than list ----
    /// <summary>True for a page that only reads: it shows the "view only" notice.</summary>
    public virtual bool ShowReadOnlyNotice => true;
    /// <summary>Runs the button of a row (<see cref="RowAction"/>); the parameter is the row key.</summary>
    public virtual System.Windows.Input.ICommand? RowActionCommand => null;
    /// <summary>Content shown above the toolbar and below the list. Null shows nothing.</summary>
    public virtual object? BannerPanel => null;
    /// <summary>The text about the page. The header shows its first sentence and keeps the rest behind "About this page".</summary>
    public virtual string Intro => "";
    public virtual object? FooterPanel => null;

    // ---- Bindable state ----
    public IconKey Icon { get; }
    public override bool ShowsSimulationBanner => false;
    public IReadOnlyList<ChoiceFilter> ChoiceFilters => choiceFilters;
    public IReadOnlyList<ToggleFilter> ToggleFilters => toggleFilters;
    public IReadOnlyList<SortChoice> SortOptions => sortOptions;
    private IReadOnlyList<object>? filterItems;
    /// <summary>Dropdown and checkbox filters in one list so the view can lay them out in a single wrapping row.</summary>
    public IReadOnlyList<object> FilterItems => filterItems ??= [.. choiceFilters, .. toggleFilters];
    public AsyncRelayCommand RefreshCommand { get; }
    public override System.Windows.Input.ICommand? ShortcutRefresh() => RefreshCommand;
    // The pending bar under the list. A page that stages changes fills these; the others leave PendingCount at 0 and the bar stays hidden.
    public virtual string PendingSummaryText => "";
    public virtual System.Windows.Input.ICommand? PendingReviewCommand => null;
    public virtual System.Windows.Input.ICommand? PendingDiscardCommand => null;
    public virtual IReadOnlyList<string> PendingNames => [];
    public virtual string PendingReviewText => Text["V2StReview"];
    public virtual string PendingReviewHelp => Text["V2StReviewHelp"];
    public virtual string PendingDiscardText => Text["V2StDiscard"];
    public virtual string PendingDiscardHelp => Text["V2StDiscardHelp"];
    /// <summary>The staged items for the Action center. A page with values per item overrides this; the default lists the names.</summary>
    protected virtual IReadOnlyList<StagedChange> StagedItems => PendingNames.Select(n => new StagedChange(n, n, "", "", null, null)).ToList();
    public override StagingView? Staging => PendingCount == 0 || PendingReviewCommand is not { } review ? null : new(Key, StagedItems, PendingSummaryText, PendingReviewText, PendingReviewHelp, PendingDiscardText, PendingDiscardHelp,
        () => review.CanExecute(null), () => review.Execute(null), () => PendingDiscardCommand?.CanExecute(null) == true, () => PendingDiscardCommand?.Execute(null));
    public RelayCommand CancelCommand { get; }
    /// <summary>Opens the details dialog of a row. The same command serves the row menu, Enter and a double click.</summary>
    public AsyncRelayCommand<DiscoveryRow> ShowDetailsCommand { get; }
    System.Windows.Input.ICommand IDiscoveryLayout.ShowDetailsCommand => ShowDetailsCommand;
    public virtual bool RowsHaveControls => false;
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand ToggleSortDirectionCommand { get; }
    public RelayCommand ToggleFiltersCommand { get; }

    // The filters of a page sit behind the "Filters" button of the toolbar at every width.
    private bool filtersExpanded;
    public bool FiltersOpen { get => filtersExpanded; set { if (SetProperty(ref filtersExpanded, value)) RaiseLayout(); } }
    private bool HasFilterItems => choiceFilters.Count + toggleFilters.Count > 0;
    public bool ShowFilterToggle => HasFilterItems;
    public bool ShowFilterRow => HasFilterItems && filtersExpanded;
    /// <summary>A search or a filter hides rows: the toolbar offers "Clear filters" and shows the count line.</summary>
    public bool HasActiveFilter => search.Length > 0 || choiceFilters.Any(f => f.IsActive) || toggleFilters.Any(t => t.IsOn) || hiddenToggles.Any(t => t.IsOn);
    public bool ShowSortDirectionText => !compactLayout;
    public string FiltersToggleLabel
    {
        get
        {
            var active = choiceFilters.Count(f => f.IsActive) + toggleFilters.Count(t => t.IsOn);
            return active > 0 ? Text.Format("DiscFiltersActive", active) : Text["DiscFilters"];
        }
    }

    private bool compactLayout;
    public bool IsCompactLayout { get => compactLayout; set { if (SetProperty(ref compactLayout, value)) RaiseLayout(); } }
    private void RaiseLayout()
    {
        foreach (var name in new[] { nameof(ShowFilterToggle), nameof(ShowFilterRow), nameof(ShowSortDirectionText), nameof(FiltersToggleLabel), nameof(FiltersOpen), nameof(HasActiveFilter) }) OnPropertyChanged(name);
    }

    // ---- Details dialog ----
    /// <summary>Where the details dialog is shown. A page without a host has no Details command.</summary>
    protected virtual IReviewPrompt? DetailsHost => null;
    /// <summary>True while the details dialog of a row is open.</summary>
    public bool IsDetailsOpen => detailsOpen;
    /// <summary>Completes when the details dialog that is open now has closed. For tests.</summary>
    public Task WhenDetailsClosed => detailsTask;

    /// <summary>The one details dialog of a row: its name, tags, labelled values in cards and, when the page offers any, action buttons.</summary>
    protected virtual DetailsDialog BuildDetails(DiscoveryRow row) =>
        DetailsDialogBuilder.Build(Text, CopyText, row.Title, row.BuildFields(), row.Badges, actions: DetailActions(row));

    /// <summary>The buttons the details dialog of a row offers (the actions that used to sit in the details pane).</summary>
    protected virtual IReadOnlyList<DetailsAction> DetailActions(DiscoveryRow row) => [];

    /// <summary>Space on a focused row. Pages whose rows have a switch or a tick box flip it; the others do nothing.</summary>
    public virtual bool ToggleRow(DiscoveryRow row) => false;

    protected bool CopyText(string text)
    {
        try { CopyToClipboard(text); return true; }
        catch (ExternalException ex)
        {
            System.Diagnostics.Trace.TraceWarning("Details could not be copied: {0}", ex.GetType().Name);
            return false;
        }
    }

    private Task ShowDetailsAsync(DiscoveryRow? row) => detailsTask = ShowDetailsCoreAsync(row);

    private async Task ShowDetailsCoreAsync(DiscoveryRow? row)
    {
        if (row is null || DetailsHost is not { } host || detailsOpen) return;
        SelectedRow = row;
        detailsOpen = true;
        ShowDetailsCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(IsDetailsOpen));
        try { await host.ShowDetailsAsync(BuildDetails(row)).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("The details dialog failed: {0}", e.GetType().Name); }
        finally { detailsOpen = false; ShowDetailsCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(IsDetailsOpen)); }
    }

    /// <summary>Copy sink; replaced in tests. The default writes plain text to the system clipboard.</summary>
    public Action<string> CopyToClipboard { get; set; } = static text => { if (!HostServices.Clipboard.TrySetText(text)) throw new ExternalException("The clipboard is held by another process."); };
    public TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public IReadOnlyList<DiscoveryRow> Rows { get => rows; private set { if (SetProperty(ref rows, value)) OnPropertyChanged(nameof(ListItems)); } }
    /// <summary>What the list shows. A page with group headers returns its own collection; the others show the rows.</summary>
    public virtual System.Collections.IEnumerable ListItems => Rows;
    /// <summary>The row that is selected when nothing was selected before: the first one shown.</summary>
    protected virtual DiscoveryRow? DefaultSelection(IReadOnlyList<DiscoveryRow> shown) => shown.FirstOrDefault();
    /// <summary>Called after the rows of the view changed (filter, search, sort or a new scan), so a page can arrange its list.</summary>
    protected virtual void OnRowsApplied() { }
    public int TotalCount => entries.Length;
    public bool IsScanning
    {
        get => isScanning;
        private set
        {
            if (!SetProperty(ref isScanning, value)) return;
            RefreshCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); RaiseState();
        }
    }

    public string SearchText
    {
        get => search;
        set { if (SetProperty(ref search, value ?? "")) { OnPropertyChanged(nameof(HasActiveFilter)); ScheduleSearch(); } }
    }
    public SortChoice? SelectedSort { get => selectedSort; set { if (value is not null && SetProperty(ref selectedSort, value)) ApplyView(); } }
    public bool SortDescending
    {
        get => sortDescending;
        set { if (SetProperty(ref sortDescending, value)) { OnPropertyChanged(nameof(SortDirectionLabel)); OnPropertyChanged(nameof(SortDirectionSymbol)); ApplyView(); } }
    }
    public string SortDirectionLabel => Text[SortDescending ? "DiscSortDescending" : "DiscSortAscending"];
    public IconKey SortDirectionSymbol => SortDescending ? IconKey.ArrowSortDown24 : IconKey.ArrowSortUp24;

    public DiscoveryRow? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!SetProperty(ref selectedRow, value)) return;
            OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(HasNoSelection));
        }
    }
    public bool HasSelection => selectedRow is not null;
    public bool HasNoSelection => selectedRow is null && Rows.Count > 0;

    // Header
    public string ReadOnlyTitle => Text["DiscReadOnlyTitle"];
    public string ReadOnlyMessage => Text["DiscReadOnlyMessage"];
    public string ReadOnlyBannerText => Text["DiscReadOnlyTitle"] + " " + Text["DiscReadOnlyMessage"];
    public string SearchPlaceholder => Text["DiscSearch_" + Key];
    public string CountText => result is null || failure is not null ? "" :
        Rows.Count != TotalCount ? Text.Format("DiscCountFiltered", Rows.Count, TotalCount) :
        TotalCount == 1 ? Text["DiscCountOne"] : Text.Format("DiscCountFormat", TotalCount);
    public string LastScannedText => failure is not null ? Text.Format("DiscScanFailedAt", Text.When((failedAt ?? result?.CollectedAtUtc ?? DateTimeOffset.UtcNow).ToLocalTime(), "g")) :
        result is null ? Text["DiscNeverScanned"] :
        Text.Format("DiscLastScanned", Text.When(result.CollectedAtUtc.ToLocalTime(), "g"));
    /// <summary>The header says only when the list was read; the count lives in one place, the line under the search box.</summary>
    public string HeaderStatusText => IsScanning ? Text["DiscScanning"] : LastScannedText;
    /// <summary>True when the page has its own summary line that already gives the item count (Startup, quarantine): the count line then shows only while a filter hides rows.</summary>
    protected virtual bool SummaryHasCount => false;
    public bool ShowCountLine => CountText.Length > 0 && (HasActiveFilter || !SummaryHasCount);

    // Page states (exactly one of progress / failure / not-scanned / empty / no-matches / list is shown)
    public bool ShowProgress => IsScanning && (result is null || failure is not null) && !slow;
    public bool ShowFailure => failure is not null && !IsScanning;
    public bool ShowNotScanned => result is null && failure is null && !IsScanning;
    public bool ShowEmpty => result is not null && failure is null && TotalCount == 0;
    public bool ShowNoMatches => failure is null && TotalCount > 0 && Rows.Count == 0;
    public bool ShowList => failure is null && Rows.Count > 0;
    /// <summary>A switch the page shows in the toolbar beside the search box (a filter the page wants in plain sight). Null shows none.</summary>
    public virtual ToggleFilter? InlineToggle => null;
    public bool HasInlineToggle => InlineToggle is not null;
    public bool ShowToolbar => ShowList || ShowNoMatches;
    public string EmptyTitle => Text["DiscEmptyTitle_" + Key];
    public string EmptyBody => Text["DiscEmptyBody_" + Key];
    public string FailureMessage => failure is null ? "" : errors.Message(failure);
    /// <summary>The code and the reference of the failure, for the details under the error state.</summary>
    public string FailureDetails => failure is null ? "" : ErrorPresenter.Diagnostics(failure);
    public bool CanRetry => failure?.Retryable == true;
    /// <summary>Label of the retry action; empty when the failure cannot be retried, which hides the button.</summary>
    public string RetryText => CanRetry ? Text["TryAgain"] : "";

    // Partial results
    public bool HasPartial => result is not null && failure is null && result.Issues.Count > 0;
    public string PartialTitle => Text["DiscPartialTitle"];
    public string PartialMessage
    {
        get
        {
            if (result is null || result.Issues.Count == 0) return "";
            var parts = new List<string> { Text.Format("DiscPartialCount", Text.Number(result.Issues.Count)) };
            foreach (var group in result.Issues.GroupBy(i => i.Error.Code).OrderByDescending(g => g.Count()))
            {
                if (group.Key == ErrorCode.DiscoveryItemUnreadable) continue; // already covered by the count sentence
                parts.Add(group.Key == ErrorCode.AccessDenied ? Text["DiscAccessDeniedHint"] : errors.Message(group.First().Error));
            }
            return string.Join(" ", parts);
        }
    }

    // ---- Lifecycle ----
    public override void Activate()
    {
        if (slot.Last is { } cached && !ReferenceEquals(cached, result)) Start(() => ApplyResultAsync(cached));
        else if (result is null && failure is null && !IsScanning) Start(ScanAsync);
    }
    public override void Deactivate() { CancelScan(); debounce?.Cancel(); }

    public void CancelScan() => scan?.Cancel();

    /// <summary>Completes when no scan, rebuild or debounced filter is outstanding. Used by tests and shutdown.</summary>
    public override async Task WhenIdleAsync()
    {
        // A finished task leaves the set through a continuation on the UI thread. Looking only at unfinished tasks keeps this loop from spinning on a finished one before that continuation has run.
        while (true)
        {
            var running = pending.Where(t => !t.IsCompleted || t.IsFaulted).ToArray();
            if (running.Length == 0) return;
            await Task.WhenAll(running);
        }
    }

    private void Start(Func<Task> work)
    {
        var task = work();
        if (task.IsCompleted) return;
        pending.Add(task);
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // in production nobody awaits it
        _ = task.ContinueWith(t => pending.Remove(t), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Starts a scan, cancelling one that is already running. Never throws for scan failures; they become the failure state.</summary>
    public async Task ScanAsync()
    {
        scan?.Cancel();
        var cts = scan = new CancellationTokenSource();
        IsTakingTooLong = false;
        IsScanning = true;
        _ = WatchSlowAsync(cts.Token);
        var title = Text.Format("ACRunRead", Text[Key]);
        using var run = Runs.Begin(title, false, () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
        try
        {
            var scanned = await slot.ScanAsync(cts.Token);
            if (cts.IsCancellationRequested) { run.Finish(RunResult.Stopped); return; }
            await ApplyResultAsync(scanned);
            run.Finish(RunResult.Ok, quiet: true);
        }
        catch (OperationCanceledException) { run.Finish(RunResult.Stopped); }
        catch (Exception ex)
        {
            failure = errors.Report(ex, "Discovery " + Key) ?? WinoraError.From(ErrorCode.Unexpected);
            buildVersion++; result = null; entries = []; failedAt = DateTimeOffset.UtcNow; ApplyView();
            run.Finish(RunResult.Error, Text.Format("ACRunFailedText", title));
        }
        finally
        {
            if (ReferenceEquals(scan, cts)) { scan = null; IsTakingTooLong = false; IsScanning = false; }
            cts.Cancel(); // stops the slow-scan watcher of this scan, so it cannot flag a newer scan
            cts.Dispose();
        }
    }

    private async Task ApplyResultAsync(Winora.Domain.DiscoveryResult<T> value)
    {
        result = value; builtCulture = Text.Culture;
        failure = value.Items.Count == 0 && value.Issues.Count > 0 ? value.Issues[0].Error : null;
        failedAt = failure is null ? null : value.CollectedAtUtc;
        await RebuildAsync();
        OnResultApplied(value);
    }

    /// <summary>The result that is shown now, or null.</summary>
    protected Winora.Domain.DiscoveryResult<T>? CurrentResult => result;

    /// <summary>Called after a result is shown. A page can start background work for its rows here.</summary>
    protected virtual void OnResultApplied(Winora.Domain.DiscoveryResult<T> value) { }

    /// <summary>Selects the row with this key. Filters and search that hide it are cleared first. False when no row has the key.</summary>
    protected bool SelectRowByKey(string key)
    {
        if (Rows.All(r => r.Key != key)) ClearFilters();
        var row = Rows.FirstOrDefault(r => r.Key == key);
        if (row is null) return false;
        SelectedRow = row;
        return true;
    }

    /// <summary>Builds the rows again from the last result (for facts that arrive after the scan).</summary>
    protected void RebuildRows() { if (result is not null) Start(RebuildAsync); }

    /// <summary>Rebuilds display rows from the last result on a worker thread (language change, new scan).</summary>
    private async Task RebuildAsync()
    {
        var version = ++buildVersion;
        var items = result?.Items ?? [];
        var built = await Task.Run(() => items.Select(i => new Entry(i, Named(BuildRow(i)), string.Join('\n', SearchFields(i).Where(s => !string.IsNullOrEmpty(s))))).ToArray());
        if (version != buildVersion) return;
        entries = built;
        ApplyView();
    }

    protected override void OnLanguageChanged()
    {
        foreach (var f in choiceFilters) f.RefreshLabels();
        foreach (var f in toggleFilters) f.RefreshLabels();
        foreach (var f in hiddenToggles) f.RefreshLabels();
        foreach (var s in sortOptions) s.Refresh();
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(QuickChipsName)); OnPropertyChanged(nameof(SummaryName));
        if (!ReferenceEquals(builtCulture, Text.Culture) && result is not null) { builtCulture = Text.Culture; Start(RebuildAsync); }
    }

    // ---- Search, filter, sort ----
    private void ScheduleSearch()
    {
        debounce?.Cancel();
        var cts = debounce = new CancellationTokenSource();
        Start(async () =>
        {
            try { await Task.Delay(SearchDelay, cts.Token); }
            catch (OperationCanceledException) { return; }
            ApplyView();
        });
    }

    /// <summary>Applies the pending search text immediately instead of waiting for the debounce.</summary>
    public void ApplyViewNow() { debounce?.Cancel(); ApplyView(); }

    private void ClearFilters()
    {
        foreach (var f in choiceFilters) if (f.IsActive) f.Reset();
        foreach (var t in toggleFilters) t.Reset();
        foreach (var t in hiddenToggles) t.Reset();
        if (search.Length > 0) { search = ""; OnPropertyChanged(nameof(SearchText)); }
        ApplyViewNow();
    }

    private void ApplyView()
    {
        var keepKey = selectedRow?.Key;
        IEnumerable<Entry> query = entries;
        foreach (var f in choiceFilters) if (f.IsActive) query = query.Where(e => f.Matches(e.Item!));
        foreach (var t in toggleFilters) if (t.IsOn) query = query.Where(e => t.Matches(e.Item!));
        foreach (var t in hiddenToggles) if (t.IsOn) query = query.Where(e => t.Matches(e.Item!));
        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 0) query = query.Where(e => terms.All(term => e.Haystack.Contains(term, StringComparison.OrdinalIgnoreCase)));
        var sorted = Sort(query.ToList());
        Rows = sorted.Select(e => e.Row).ToArray();
        SelectedRow = Rows.FirstOrDefault(r => r.Key == keepKey) ?? DefaultSelection(Rows);
        OnRowsApplied();
        RefreshSummary(); RefreshChips();
        RaiseState();
    }

    private List<Entry> Sort(List<Entry> list)
    {
        if (selectedSort is null) return list;
        var culture = Text.Culture; var key = selectedSort.Key; var descending = sortDescending;
        var keyed = list.Select(e => (Entry: e, Key: key(e.Item!))).ToList();
        var present = keyed.Where(x => x.Key is not null).ToList();
        var missing = keyed.Where(x => x.Key is null).Select(x => x.Entry).ToList();
        int ByTitle(Entry a, Entry b)
        {
            var c = string.Compare(a.Row.Title, b.Row.Title, culture, CompareOptions.IgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Row.Key, b.Row.Key);
        }
        present.Sort((a, b) =>
        {
            var c = a.Key is string sa && b.Key is string sb ? string.Compare(sa, sb, culture, CompareOptions.IgnoreCase) : a.Key!.CompareTo(b.Key);
            if (descending) c = -c;
            return c != 0 ? c : ByTitle(a.Entry, b.Entry);
        });
        missing.Sort(ByTitle);
        return present.Select(x => x.Entry).Concat(missing).ToList();
    }

    private void RaiseState()
    {
        foreach (var name in new[]
        {
            nameof(TotalCount), nameof(CountText), nameof(HasActiveFilter), nameof(ShowCountLine), nameof(LastScannedText), nameof(HeaderStatusText), nameof(ShowProgress), nameof(ShowFailure),
            nameof(ShowNotScanned), nameof(ShowEmpty), nameof(ShowNoMatches), nameof(ShowList), nameof(ShowToolbar), nameof(FailureMessage), nameof(FailureDetails), nameof(CanRetry), nameof(RetryText),
            nameof(HasPartial), nameof(PartialMessage), nameof(HasNoSelection), nameof(IsScanning), nameof(FiltersToggleLabel),
        }) OnPropertyChanged(name);
    }
}
