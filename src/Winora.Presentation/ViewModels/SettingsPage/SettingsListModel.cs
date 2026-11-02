// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop;

/// <summary>Where a page keeps small per-user choices such as which groups are folded. Null storage keeps them for the session only.</summary>
public interface IPagePreferences
{
    string? Read(string key);
    void Write(string key, string value);
}

/// <summary>Stores the small choices of a settings page in the user's preferences.</summary>
public sealed class PagePreferences(SettingsViewModel settings) : IPagePreferences
{
    public string? Read(string key) => settings.ReadPreference(key);
    public void Write(string key, string value) => settings.WritePreference(key, value);
}

public enum StateFilter { All, Pending, ChangedByWinora, DiffersFromDefault, Managed, NotAvailable }
public enum SortMode { Name, Risk }

/// <summary>A collection that replaces all items with one change notification, so a filter swaps the whole list at once.</summary>
public sealed class BulkCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>One entry of the flat, virtualized list: a group header or a setting row.</summary>
public abstract class SettingsListEntry : ObservableObject
{
    public abstract string AutomationName { get; }
    /// <summary>False for an entry that cannot take keyboard focus (the title of a tab).</summary>
    public virtual bool IsFoldable => true;
}

public sealed class SettingsRowEntry(ISettingRow row, bool isFirst = false, bool isLast = false) : SettingsListEntry
{
    public ISettingRow Row { get; } = row;
    /// <summary>The rows of a group are one card: the first row has the top corners and edge, the last one the bottom corners and edge.</summary>
    public bool IsFirst { get; } = isFirst;
    public bool IsLast { get; } = isLast;
    public override string AutomationName => Row.AutomationName;
}

/// <summary>A collapsible group header. Level 2 heading; Enter or Space folds and unfolds it.</summary>
public sealed class SettingsGroupEntry : SettingsListEntry
{
    private readonly Func<string> title, count, intro;
    private readonly LocalizationService text;

    public SettingsGroupEntry(string key, Func<string> title, Func<string> count, Func<string> intro, bool collapsed, bool isAdvanced, LocalizationService text, Action<string> toggle, bool isFoldable = true)
    {
        Key = key; this.title = title; this.count = count; this.intro = intro; IsCollapsed = collapsed; IsAdvanced = isAdvanced; this.text = text; IsFoldable = isFoldable;
        ToggleCommand = new RelayCommand(() => toggle(key));
    }

    public string Key { get; }
    public bool IsCollapsed { get; internal set; }
    public bool IsAdvanced { get; }
    /// <summary>False for the title of a tab: it only names the card under it and cannot be folded or focused.</summary>
    public override bool IsFoldable { get; }
    public bool IsOpen => !IsCollapsed;
    public string Title => title();
    public string CountText => count();
    public string Intro => intro();
    public bool HasIntro => IsAdvanced && Intro.Length > 0;
    public RelayCommand ToggleCommand { get; }
    public string StateText => text[IsCollapsed ? "GroupCollapsed" : "GroupExpanded"];
    public string ToggleHelp => text["GroupToggleHelp"];
    public override string AutomationName => IsFoldable ? $"{Title}, {CountText}, {StateText}" : Title;
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A category of the rail (or the category box on a narrow page) with the number of settings that pass the other filters.</summary>
public sealed class CategoryItem(string? key, Func<string> label, int order = 0) : ObservableObject
{
    private int count;
    public string? Key { get; } = key;
    public int Order { get; } = order;
    public string Label => label();
    public int Count { get => count; internal set { if (SetProperty(ref count, value)) { OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(AutomationName)); } } }
    public string CountText => count.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string AutomationName => $"{Label}, {count}";
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A filter chip in the filter bar. A toggle button, named "Risk: Low, on".</summary>
public sealed class FilterToggle : ObservableObject
{
    private readonly Func<bool, string> name;
    private bool isOn;
    private readonly Action changed;

    public FilterToggle(string id, IconKey symbol, Func<string> label, Func<bool, string> name, Action changed)
    {
        Id = id; Symbol = symbol; this.name = name; this.changed = changed; LabelFunc = label;
    }

    public string Id { get; }
    public IconKey Symbol { get; }
    private Func<string> LabelFunc { get; }
    public string Label => LabelFunc();
    public bool IsOn { get => isOn; set { if (SetProperty(ref isOn, value)) { OnPropertyChanged(nameof(AutomationName)); changed(); } } }
    public string AutomationName => name(isOn);
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class StateOption(StateFilter id, Func<string> label) : ObservableObject
{
    public StateFilter Id { get; } = id;
    public string Label => label();
    public void Refresh() => OnPropertyChanged(nameof(Label));
}

public sealed class SortOption(SortMode id, Func<string> label) : ObservableObject
{
    public SortMode Id { get; } = id;
    public string Label => label();
    public void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// Everything the settings page shows beyond the rows themselves: search and filters (risk, source, state, category), the category rail with counts, collapsible groups, the Advanced
/// section, the "Changed by Winora" view and the details pane. It holds no Windows state; the page gives it rows and calls <see cref="Rebuild"/> when a row changes in a way
/// the filters can see. The flat entry list is replaced in one swap for a new filter and edited in place when a group folds, so the scroll position survives folding.
/// </summary>
public sealed class SettingsListModel : ObservableObject
{
    public const string AdvancedKey = "advanced";
    /// <summary>Shows the settings as tiles in tabs: an All tab and one tab per category, no group headers and no separate Advanced group (an advanced setting stays in its tab).</summary>
    public bool Tiles
    {
        get => tiles;
        set { if (tiles == value) return; tiles = value; OnPropertyChanged(); selectedCategory = allCategory; openFirstTab = value; Rebuild(force: true); }
    }
    private bool tiles;
    // Tabs open on the first category, not on All, so the page starts with one card.
    private bool openFirstTab;
    private readonly LocalizationService text;
    private readonly string pageKey;
    private readonly IPagePreferences? prefs;
    private readonly HashSet<string> toggled = [];
    private IReadOnlyList<ISettingRow> rows = [];
    private string search = "";
    private CategoryItem allCategory;
    private CategoryItem selectedCategory;
    private StateOption selectedState;
    private SortOption selectedSort;
    private ISettingRow? selected;
    private bool detailsOpen;
    private int shown;
    private List<(CategoryItem Category, List<ISettingRow> Rows)> lastGroups = [];

    public SettingsListModel(LocalizationService text, string pageKey, IPagePreferences? prefs = null)
    {
        this.text = text; this.pageKey = pageKey; this.prefs = prefs;
        allCategory = new CategoryItem(null, () => text["SetCategoryAll"], -1);
        selectedCategory = allCategory;
        RiskFilters =
        [
            new("Low", IconKey.CheckmarkCircle16, () => text.Enum(RiskLevel.Low), on => text.Format("FbToggleName", text["FbRiskGroup"], text.Enum(RiskLevel.Low), text[on ? "FbOn" : "FbOff"]), OnFilterChanged),
            new("Medium", IconKey.Warning16, () => text.Enum(RiskLevel.Medium), on => text.Format("FbToggleName", text["FbRiskGroup"], text.Enum(RiskLevel.Medium), text[on ? "FbOn" : "FbOff"]), OnFilterChanged),
            new("High", IconKey.ErrorCircle16, () => text.Enum(RiskLevel.High), on => text.Format("FbToggleName", text["FbRiskGroup"], text.Enum(RiskLevel.High), text[on ? "FbOn" : "FbOff"]), OnFilterChanged),
        ];
        SourceFilters =
        [
            new("A", IconKey.ShieldCheckmark16, () => text["FbSourceA"], on => text.Format("FbToggleName", text["FbSourceGroup"], text["FbSourceA"], text[on ? "FbOn" : "FbOff"]), OnFilterChanged),
            new("B", IconKey.PersonStar16, () => text["FbSourceB"], on => text.Format("FbToggleName", text["FbSourceGroup"], text["FbSourceB"], text[on ? "FbOn" : "FbOff"]), OnFilterChanged),
            new("C", IconKey.People16, () => text["FbSourceC"], on => text.Format("FbToggleName", text["FbSourceGroup"], text["FbSourceC"], text[on ? "FbOn" : "FbOff"]), OnFilterChanged),
        ];
        States =
        [
            new(StateFilter.All, () => text["FbStateAll"]), new(StateFilter.Pending, () => text["FbStatePending"]), new(StateFilter.ChangedByWinora, () => text["FbStateChanged"]),
            new(StateFilter.DiffersFromDefault, () => text["FbStateDiffers"]), new(StateFilter.Managed, () => text["FbStateManaged"]), new(StateFilter.NotAvailable, () => text["FbStateUnavailable"]),
        ];
        Sorts = [new(SortMode.Name, () => text["FbSortName"]), new(SortMode.Risk, () => text["FbSortRisk"])];
        selectedState = States[0]; selectedSort = Sorts[0];
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ToggleSortDirectionCommand = new RelayCommand(() => SortDescending = !SortDescending);
        CollapseAllCommand = new RelayCommand(() => SetAll(collapsed: true));
        ExpandAllCommand = new RelayCommand(() => SetAll(collapsed: false));
        OpenDetailsCommand = new RelayCommand<ISettingRow>(r => { if (r is not null) OpenDetails(r); });
        CopyNameCommand = new RelayCommand<ISettingRow>(r => { if (r is not null) FeedbackSource?.Invoke()?.CopyText(r.Title, text["RowNameCopied"]); });
        LoadToggled();
    }

    public LocalizationService Text => text;
    public IReadOnlyList<FilterToggle> RiskFilters { get; }
    public IReadOnlyList<FilterToggle> SourceFilters { get; }
    public IReadOnlyList<StateOption> States { get; }
    public IReadOnlyList<SortOption> Sorts { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand ToggleSortDirectionCommand { get; }
    private bool sortDescending;
    /// <summary>Reverses the sort order (Z to A, or the highest risk first).</summary>
    public bool SortDescending
    {
        get => sortDescending;
        set { if (SetProperty(ref sortDescending, value)) { OnPropertyChanged(nameof(SortDirectionLabel)); OnPropertyChanged(nameof(SortDirectionSymbol)); OnFilterChanged(); } }
    }
    public string SortDirectionLabel => text[sortDescending ? "DiscSortDescending" : "DiscSortAscending"];
    public IconKey SortDirectionSymbol => sortDescending ? IconKey.ArrowSortDown24 : IconKey.ArrowSortUp24;
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand ExpandAllCommand { get; }
    public RelayCommand<ISettingRow> OpenDetailsCommand { get; }
    public RelayCommand<ISettingRow> CopyNameCommand { get; }
    /// <summary>Where "Copy setting name" gets the clipboard and the toast from. Set by the page.</summary>
    public Func<FeedbackCenter?>? FeedbackSource { get; set; }
    public BulkCollection<SettingsListEntry> Entries { get; } = [];
    /// <summary>All categories first. The collection object stays the same so the rail keeps its selection; items change only when the set of categories does.</summary>
    public BulkCollection<CategoryItem> Categories { get; } = [];
    public IReadOnlyList<ISettingRow> Rows => rows;
    /// <summary>The rows that pass every filter, in display order (group order, then the sort).</summary>
    public IReadOnlyList<ISettingRow> VisibleRows { get; private set; } = [];

    /// <summary>The visible rows by category, ignoring folding. The page view model builds its group list from this.</summary>
    public IReadOnlyList<(CategoryItem Category, IReadOnlyList<ISettingRow> Rows)> VisibleGroups => lastGroups.Select(g => (g.Category, (IReadOnlyList<ISettingRow>)g.Rows)).ToList();

    public string SearchText { get => search; set { if (SetProperty(ref search, value ?? "")) OnFilterChanged(); } }
    public CategoryItem SelectedCategory { get => selectedCategory; set { if (value is not null && SetProperty(ref selectedCategory, value)) OnFilterChanged(); } }
    public StateOption SelectedState { get => selectedState; set { if (value is not null && SetProperty(ref selectedState, value)) { OnFilterChanged(); OnPropertyChanged(nameof(ShowChangedView)); } } }
    public SortOption SelectedSort { get => selectedSort; set { if (value is not null && SetProperty(ref selectedSort, value)) OnFilterChanged(); } }
    public CategoryItem AllCategory => allCategory;

    public int ShownCount => shown;
    public int TotalCount => rows.Count;
    public string CountText => text.Format("SetResultCountFormat", shown, rows.Count);
    public bool HasActiveFilter => search.Trim().Length > 0 || selectedCategory.Key is not null || ActiveFilterCount > 0;
    /// <summary>Filters other than search: risk, source, state and category. Shown on the "Filters (n)" button of a narrow page.</summary>
    public int ActiveFilterCount => RiskFilters.Count(f => f.IsOn) + SourceFilters.Count(f => f.IsOn) + (selectedState.Id != StateFilter.All ? 1 : 0) + (selectedCategory.Key is not null ? 1 : 0);
    /// <summary>Filters behind the "Filters" button: the category is a chip beside it and does not count.</summary>
    public int FlyoutFilterCount => RiskFilters.Count(f => f.IsOn) + SourceFilters.Count(f => f.IsOn) + (selectedState.Id != StateFilter.All ? 1 : 0);
    public string FiltersButtonText => FlyoutFilterCount == 0 ? text["FbFilters"] : text.Format("FbFiltersCount", FlyoutFilterCount);
    public bool HasAnyFilterToClear => HasActiveFilter;
    public bool ShowNoMatches => rows.Count > 0 && shown == 0;
    public bool HasRows => rows.Count > 0;

    // ---- Recommended ----
    private readonly List<ISettingRow> watched = [];
    /// <summary>Some setting on the page names a recommended choice that can be staged here.</summary>
    public bool HasRecommendations => rows.Any(r => r.RecommendedChoiceId is not null && r.IsInlineChoice);
    /// <summary>Settings whose recommended choice is not in place and not staged yet.</summary>
    public int RecommendedCount => rows.Count(RecommendedRule.Gap);
    /// <summary>
    /// "Select recommended" stages every row of the page, also rows the search or filters hide and rows on other category tabs; the text says how many of them
    /// are out of view and why, so the count matches what is seen.
    /// </summary>
    public string RecommendedText
    {
        get
        {
            var gaps = rows.Where(RecommendedRule.Gap).ToList();
            var count = gaps.Count;
            var filtered = gaps.Count(r => !Matches(r, Tiles));
            var otherTabs = Tiles ? gaps.Count(r => Matches(r, true) && !Matches(r, false)) : 0;
            if (count > 0 && filtered > 0 && otherTabs > 0) return text.Format("V2PaRecHiddenBoth", count, otherTabs, filtered);
            if (count > 0 && otherTabs > 0) return text.Plural("V2PaRecOtherTabs", count, count, otherTabs);
            if (count > 0 && filtered > 0) return text.Plural("V2PaRecHidden", count, count, filtered);
            return count switch { 0 => text["V2PaRecNone"], 1 => text["V2PaRecOne"], var n => text.Format("V2PaRecMany", n) };
        }
    }
    /// <summary>The button names the count when it stages more than one row, so it is clear it also takes rows out of view.</summary>
    public string SelectRecommendedLabel => RecommendedCount > 1 ? text.Format("V2PaSelectRecCount", RecommendedCount) : text["V2PaSelectRec"];
    public bool ShowSelectRecommended => RecommendedCount > 0;
    public IRelayCommand SelectRecommendedCommand => selectRecommended ??= new RelayCommand(() => SelectRecommended(), () => RecommendedCount > 0);
    private RelayCommand? selectRecommended;

    /// <summary>Stages the recommended choice of every setting that is not following it. Nothing is applied: the review stays the way to apply.</summary>
    public int SelectRecommended()
    {
        var staged = 0;
        foreach (var row in rows.Where(RecommendedRule.Gap).ToList())
            if (row.Choices.FirstOrDefault(c => c.Id == row.RecommendedChoiceId) is { } choice) { row.SelectedChoice = choice; staged++; }
        RaiseRecommended();
        return staged;
    }

    private void RaiseRecommended()
    {
        OnPropertyChanged(nameof(HasRecommendations)); OnPropertyChanged(nameof(RecommendedCount)); OnPropertyChanged(nameof(RecommendedText)); OnPropertyChanged(nameof(SelectRecommendedLabel)); OnPropertyChanged(nameof(ShowSelectRecommended));
        selectRecommended?.NotifyCanExecuteChanged();
    }

    private void OnWatchedRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ISettingRow.SelectedChoice) or nameof(ISettingRow.IsPending) or nameof(ISettingRow.IsAvailable) or nameof(ISettingRow.CanChoose) or null or "") RaiseRecommended();
    }

    // ---- Changed by Winora ----
    public int ChangedByWinoraCount => rows.Count(r => r.Origin != ChangeOrigin.None);
    public bool ShowChangedView => selectedState.Id == StateFilter.ChangedByWinora;
    public string ChangedViewText => text.Format("FbChangedHeader", ChangedByWinoraCount);
    /// <summary>Set by the page: opens the one confirmation for undoing every row that Winora changed and that can still be undone.</summary>
    public Func<Task>? UndoAllChanged { get; set; }
    public IAsyncRelayCommand UndoAllChangedCommand => undoAll ??= new AsyncRelayCommand(() => UndoAllChanged?.Invoke() ?? Task.CompletedTask, () => UndoAllChanged is not null && rows.Any(r => r.CanUndo));
    private IAsyncRelayCommand? undoAll;
    public void RefreshUndoAll() => undoAll?.NotifyCanExecuteChanged();

    // ---- Details dialog ----
    /// <summary>The row whose details were opened last; the card gets focus back when the dialog closes.</summary>
    public ISettingRow? Selected { get => selected; private set => SetProperty(ref selected, value); }
    /// <summary>True while the details dialog of <see cref="Selected"/> is open.</summary>
    public bool DetailsOpen { get => detailsOpen; private set => SetProperty(ref detailsOpen, value); }
    /// <summary>Set by the page: shows the one details dialog for a row and completes when it closes.</summary>
    public Func<ISettingRow, Task>? ShowDetails { get; set; }
    private Task detailsTask = Task.CompletedTask;
    /// <summary>Completes when the details dialog that is open now has closed. For tests.</summary>
    public Task WhenDetailsClosed => detailsTask;

    public void OpenDetails(ISettingRow row)
    {
        if (DetailsOpen) return;
        Selected = row;
        detailsTask = ShowDetailsCoreAsync(row);
    }

    private async Task ShowDetailsCoreAsync(ISettingRow row)
    {
        DetailsOpen = true;
        try { if (ShowDetails is { } show) await show(row).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("The details dialog failed: {0}", e.GetType().Name); }
        finally { DetailsOpen = false; }
    }

    public void SetRows(IReadOnlyList<ISettingRow> value)
    {
        foreach (var old in watched) old.PropertyChanged -= OnWatchedRowChanged;
        watched.Clear();
        rows = value;
        foreach (var row in rows) { row.PropertyChanged += OnWatchedRowChanged; watched.Add(row); }
        if (selected is not null && value.All(r => r.Id != selected.Id)) Selected = null;
        Rebuild();
    }

    public void ClearFilters() => ClearFilters(null);

    /// <summary>Clears search and filters and selects <paramref name="tab"/>, or All.</summary>
    private void ClearFilters(CategoryItem? tab)
    {
        suppress = true;
        search = ""; selectedCategory = tab ?? allCategory; selectedState = States[0];
        foreach (var f in RiskFilters) f.IsOn = false;
        foreach (var f in SourceFilters) f.IsOn = false;
        suppress = false;
        OnPropertyChanged(nameof(SearchText)); OnPropertyChanged(nameof(SelectedCategory)); OnPropertyChanged(nameof(SelectedState)); OnPropertyChanged(nameof(ShowChangedView));
        Rebuild();
    }

    private bool suppress;
    private void OnFilterChanged() { if (!suppress) Rebuild(); }

    // ---- Matching ----

    private bool MatchesSearch(ISettingRow row)
    {
        var query = search.Trim();
        return query.Length == 0 || text.Culture.CompareInfo.IndexOf(row.SearchHaystack, query, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreSymbols) >= 0;
    }

    private bool MatchesToggles(ISettingRow row)
    {
        if (RiskFilters.Any(f => f.IsOn) && !RiskFilters.Any(f => f.IsOn && f.Id == row.Risk.ToString())) return false;
        if (SourceFilters.Any(f => f.IsOn) && (row.Source is null || !SourceFilters.Any(f => f.IsOn && f.Id == row.Source.ToString()))) return false;
        return selectedState.Id switch
        {
            StateFilter.Pending => row.IsPending,
            StateFilter.ChangedByWinora => row.Origin != ChangeOrigin.None,
            StateFilter.DiffersFromDefault => row.DiffersFromDefault,
            StateFilter.Managed => row.IsManaged,
            StateFilter.NotAvailable => !row.IsAvailable && !row.IsManaged,
            _ => true,
        };
    }

    private bool Matches(ISettingRow row, bool ignoreCategory) =>
        MatchesSearch(row) && MatchesToggles(row) && (ignoreCategory || (Tiles && search.Trim().Length > 0) || selectedCategory.Key is null || selectedCategory.Key == row.CategoryKey);

    // ---- Build ----

    private bool GroupCollapsed(string key, bool filtering)
    {
        if (filtering) return false;   // a search or filter shows its matches: a folded group that holds one opens
        var defaultCollapsed = key == AdvancedKey;
        return toggled.Contains(key) ? !defaultCollapsed : defaultCollapsed;
    }

    /// <summary>Recomputes counts, groups and the flat list. Called when rows, language or a filter change.</summary>
    public void Rebuild() => Rebuild(force: false);

    private string signature = "";
    private static string SignatureOf(IEnumerable<SettingsListEntry> entries) =>
        string.Join('|', entries.Select(e => e is SettingsGroupEntry g ? "g:" + g.Key + g.IsCollapsed : "r:" + ((SettingsRowEntry)e).Row.Id + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(((SettingsRowEntry)e).Row)));

    /// <summary>The list object changes only when what is shown changes (or the language does), so a plain value change keeps the scroll position.</summary>
    private void Rebuild(bool force)
    {
        var rail = rows.Where(r => Matches(r, ignoreCategory: true)).ToList();
        var categories = rows.GroupBy(r => r.CategoryKey).Select(g => (Key: g.Key, First: g.First())).OrderBy(g => g.First.CategoryOrder).ThenBy(g => g.First.CategoryLabel, StringComparer.Create(text.Culture, true)).ToList();
        var items = categories.Select(c => Categories.FirstOrDefault(x => x.Key == c.Key) ?? new CategoryItem(c.Key, () => rows.FirstOrDefault(r => r.CategoryKey == c.Key)?.CategoryLabel ?? c.Key, c.First.CategoryOrder)).ToList();
        foreach (var item in items) item.Count = rail.Count(r => r.CategoryKey == item.Key);
        allCategory.Count = rail.Count;
        var wanted = new List<CategoryItem>();
        wanted.Add(allCategory);
        wanted.AddRange(items);
        if (!Categories.SequenceEqual(wanted) || (openFirstTab && items.Count > 0))
        {
            var keep = selectedCategory;
            Categories.ReplaceAll(wanted);
            // The tabs start on the first category and go back to All when the selected tab is gone.
            selectedCategory = openFirstTab && items.Count > 0 ? items[0] : keep.Key is not null && wanted.Any(c => c.Key == keep.Key) ? wanted.First(c => c.Key == keep.Key) : allCategory;
            if (items.Count > 0) openFirstTab = false;
            OnPropertyChanged(nameof(SelectedCategory));
        }

        var visible = rows.Where(r => Matches(r, ignoreCategory: false)).ToList();
        shown = visible.Count;
        var filtering = search.Trim().Length > 0 || RiskFilters.Any(f => f.IsOn) || SourceFilters.Any(f => f.IsOn) || selectedState.Id != StateFilter.All;
        lastGroups = [];
        var entries = new List<SettingsListEntry>();
        var order = new List<ISettingRow>();
        var comparer = StringComparer.Create(text.Culture, true);
        IEnumerable<ISettingRow> Sorted(IEnumerable<ISettingRow> source) => selectedSort.Id == SortMode.Risk
            ? sortDescending ? source.OrderByDescending(r => r.Risk).ThenBy(r => r.Title, comparer) : source.OrderBy(r => r.Risk).ThenBy(r => r.Title, comparer)
            : sortDescending ? source.OrderByDescending(r => r.Title, comparer).ThenBy(r => r.Id, StringComparer.Ordinal) : source.OrderBy(r => r.Title, comparer).ThenBy(r => r.Id, StringComparer.Ordinal);
        foreach (var category in Categories.Where(c => c.Key is not null).ToList())
        {
            var members = Sorted(visible.Where(r => (Tiles || !r.IsAdvanced) && r.CategoryKey == category.Key)).ToList();
            if (members.Count == 0) continue;
            lastGroups.Add((category, members));
            if (Tiles)
            {
                // A tab is one card: its title is the header and the rows sit under it.
                entries.Add(new SettingsGroupEntry("tab:" + category.Key, () => selectedCategory.Key is null ? category.Label : selectedCategory.Label, () => "", () => "", collapsed: false, isAdvanced: false, text, _ => { }, isFoldable: false));
                entries.AddRange(RowEntries(members, underHeader: true)); order.AddRange(members);
            }
            else AddGroup(entries, order, category.Key!, () => category.Label, members, advanced: false, filtering);
        }
        var advancedRows = Tiles ? [] : Sorted(visible.Where(r => r.IsAdvanced)).ToList();
        if (advancedRows.Count > 0)
        {
            lastGroups.Add((new CategoryItem(AdvancedKey, () => text["OptimizeAdvancedHeading"], int.MaxValue) { Count = advancedRows.Count }, advancedRows));
            AddGroup(entries, order, AdvancedKey, () => text["OptimizeAdvancedHeading"], advancedRows, advanced: true, filtering);
        }
        VisibleRows = visible.Count == 0 ? [] : lastGroups.SelectMany(g => g.Rows).ToList();
        var next = SignatureOf(entries);
        if (force || next != signature) { Entries.ReplaceAll(entries); signature = next; }
        foreach (var n in new[] { nameof(ShownCount), nameof(TotalCount), nameof(CountText), nameof(HasActiveFilter), nameof(ActiveFilterCount), nameof(FiltersButtonText), nameof(HasAnyFilterToClear),
            nameof(ShowNoMatches), nameof(HasRows), nameof(FlyoutFilterCount), nameof(ChangedByWinoraCount), nameof(ChangedViewText), nameof(VisibleRows), nameof(VisibleGroups), nameof(ShowChangedView) })
            OnPropertyChanged(n);
        RefreshUndoAll();
        RaiseRecommended();
    }

    // The rows of a group sit under the group's header, which is the top of the card; rows of a tab have no header and the first row is the top.
    private static IEnumerable<SettingsRowEntry> RowEntries(IReadOnlyList<ISettingRow> members, bool underHeader) =>
        members.Select((row, i) => new SettingsRowEntry(row, isFirst: i == 0 && !underHeader, isLast: i == members.Count - 1));

    private void AddGroup(List<SettingsListEntry> entries, List<ISettingRow> order, string key, Func<string> title, List<ISettingRow> members, bool advanced, bool filtering)
    {
        var collapsed = GroupCollapsed(key, filtering);
        entries.Add(new SettingsGroupEntry(key, title, () => text.Format("SetGroupCountFormat", members.Count), () => text["OptimizeAdvancedIntro"], collapsed, advanced, text, Toggle));
        if (!collapsed) entries.AddRange(RowEntries(members, underHeader: true));
        order.AddRange(members);
    }

    // ---- Folding ----

    private void Toggle(string key)
    {
        if (!toggled.Remove(key)) toggled.Add(key);
        SaveToggled();
        var index = Entries.ToList().FindIndex(e => e is SettingsGroupEntry g && g.Key == key);
        if (index < 0) { Rebuild(); return; }
        var header = (SettingsGroupEntry)Entries[index];
        var group = lastGroups.FirstOrDefault(g => g.Category.Key == key);
        var collapsed = !header.IsCollapsed;
        header.IsCollapsed = collapsed;
        header.Refresh();
        if (collapsed)
        {
            while (index + 1 < Entries.Count && Entries[index + 1] is SettingsRowEntry) Entries.RemoveAt(index + 1);
        }
        else
        {
            var at = index + 1;
            foreach (var entry in RowEntries(group.Rows ?? [], underHeader: true)) Entries.Insert(at++, entry);
        }
        signature = SignatureOf(Entries);
    }

    private void SetAll(bool collapsed)
    {
        toggled.Clear();
        foreach (var g in lastGroups.Select(x => x.Category.Key!))
        {
            var defaultCollapsed = g == AdvancedKey;
            if (collapsed != defaultCollapsed) toggled.Add(g);
        }
        SaveToggled();
        Rebuild();
    }

    private string PrefKey => "settingspage.toggled." + pageKey;
    private void LoadToggled()
    {
        if (prefs?.Read(PrefKey) is not { Length: > 0 } stored) return;
        foreach (var key in stored.Split(',', StringSplitOptions.RemoveEmptyEntries)) toggled.Add(key);
    }
    private void SaveToggled() => prefs?.Write(PrefKey, string.Join(',', toggled.Order()));

    /// <summary>Opens the group that holds the row, selects its category when needed and returns the row's entry, so the page can scroll to it and focus it.</summary>
    public SettingsRowEntry? Reveal(ISettingRow row)
    {
        var key = Tiles ? row.CategoryKey : row.IsAdvanced ? AdvancedKey : row.CategoryKey;
        var defaultCollapsed = key == AdvancedKey;
        var collapsed = !Tiles && (toggled.Contains(key) ? !defaultCollapsed : defaultCollapsed);
        if (collapsed) { if (!toggled.Remove(key)) toggled.Add(key); SaveToggled(); }
        if (!Matches(row, ignoreCategory: false)) ClearFilters(Categories.FirstOrDefault(c => c.Key == row.CategoryKey)); else Rebuild();
        return Entries.OfType<SettingsRowEntry>().FirstOrDefault(e => e.Row.Id == row.Id);
    }

    /// <summary>Language changed: labels, counts and sort order follow.</summary>
    public void Refresh()
    {
        foreach (var c in Categories) c.Refresh();
        foreach (var t in RiskFilters.Concat(SourceFilters)) t.Refresh();
        foreach (var s in States) s.Refresh();
        foreach (var s in Sorts) s.Refresh();
        Rebuild(force: true);
        OnPropertyChanged(string.Empty);
    }
}
