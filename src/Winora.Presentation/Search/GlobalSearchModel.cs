// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop.Search;

/// <summary>Adapter from the app text to the index.</summary>
public sealed class LocalizationSearchText(LocalizationService text) : ISearchText
{
    public string Current(string key) => text[key];
    public string? In(string key, string language) => text.Lookup(key, language);
}

/// <summary>One line of the result list. The automation name carries the group, the title, the path and the risk, so a screen reader reads one sentence per row.</summary>
public sealed class SearchRow : ObservableObject
{
    private bool selected;

    public SearchRow(SearchHit hit, LocalizationService text, IReadOnlyList<string> queryWords, string groupTitle)
    {
        Hit = hit;
        Parts = SearchText.Highlight(hit.Title, queryWords);
        if (hit.Seed.Risk is { } risk) { Risk = risk; RiskText = text.Enum(risk); }
        if (hit.Seed.Source is { } source) { Source = source; SourceText = text["OptimizeSourceClass_" + source]; }
        AutomationName = $"{groupTitle}: {hit.Title}" + (hit.Breadcrumb.Length > 0 ? ", " + hit.Breadcrumb : "") + (RiskText is null ? "" : ", " + RiskText) + (SourceText is null ? "" : ", " + SourceText);
    }

    public SearchHit Hit { get; }
    public string Title => Hit.Title;
    public string Breadcrumb => Hit.Breadcrumb;
    public IconKey Icon => Hit.Seed.Icon;
    public IReadOnlyList<(string Text, bool Match)> Parts { get; }
    public RiskLevel? Risk { get; }
    public string? RiskText { get; }
    public bool HasRisk => Risk is not null;
    public SourceClass? Source { get; }
    public string? SourceText { get; }
    public bool HasSource => Source is not null;
    public string AutomationName { get; }
    public bool IsSelected { get => selected; set => SetProperty(ref selected, value); }
}

public sealed class SearchGroupModel : ObservableObject
{
    private readonly IReadOnlyList<SearchRow> all;
    private bool expanded;

    public SearchGroupModel(SearchGroup group, string title, IReadOnlyList<SearchRow> rows, string showAllFormat, Action changed)
    {
        Group = group; Title = title; all = rows; ShowAllText = string.Format(showAllFormat, rows.Count);
        ShowAllCommand = new(() => { expanded = true; OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(CanShowAll)); changed(); });
    }

    public const int Limit = 6;
    public SearchGroup Group { get; }
    public string Title { get; }
    public int Total => all.Count;
    public IReadOnlyList<SearchRow> Rows => expanded ? all : all.Take(Limit).ToList();
    public bool CanShowAll => !expanded && all.Count > Limit;
    public string ShowAllText { get; }
    public RelayCommand ShowAllCommand { get; }
}

/// <summary>
/// The model of the title-bar search box. It reads nothing from the PC and changes nothing: choosing a result only asks the shell to open a page and highlight an item.
/// The index is built on the first query and again after the language changed.
/// </summary>
public sealed class GlobalSearchModel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly Func<IEnumerable<SearchSeed>> seeds;
    private readonly Action<SearchSeed> open;
    private SearchIndex? index;
    private string query = "";
    private bool isOpen;
    private int selectedIndex = -1;
    private IReadOnlyList<SearchRow> rows = [];

    public GlobalSearchModel(LocalizationService text, Func<IEnumerable<SearchSeed>> seeds, Action<SearchSeed> open)
    {
        this.text = text; this.seeds = seeds; this.open = open;
        ClearCommand = new(Clear);
        ActivateCommand = new(row => { if (row is not null) Activate(row); });
        text.PropertyChanged += (_, e) => { if (e.PropertyName is "Item[]" or nameof(LocalizationService.Culture)) { index = null; RunAfterLanguageChange(); OnPropertyChanged(string.Empty); } };
    }

    public ObservableCollection<SearchGroupModel> Groups { get; } = [];
    public LocalizationService Text => text;
    public RelayCommand ClearCommand { get; }
    public RelayCommand<SearchRow> ActivateCommand { get; }

    /// <summary>The rows in the order they are shown (the arrow keys move through them).</summary>
    public IReadOnlyList<SearchRow> Rows => rows;
    public int Count => index?.Count ?? 0;

    public string Query
    {
        get => query;
        set { if (SetProperty(ref query, value ?? "")) { Run(); OnPropertyChanged(nameof(HasQuery)); } }
    }

    public bool HasQuery => query.Trim().Length > 0;
    public bool IsOpen { get => isOpen; set => SetProperty(ref isOpen, value); }
    public bool NoResults => HasQuery && rows.Count == 0;
    public string NoResultsText => text.Format("SearchNoMatch", query.Trim());
    public string StatusText => !HasQuery ? "" : rows.Count == 0 ? text.Format("SearchNoMatch", query.Trim()) : text.Format("SearchResultCount", rows.Count);

    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            var next = Math.Clamp(value, -1, rows.Count - 1);
            if (next == selectedIndex) return;
            if (selectedIndex >= 0 && selectedIndex < rows.Count) rows[selectedIndex].IsSelected = false;
            selectedIndex = next;
            if (next >= 0) rows[next].IsSelected = true;
            OnPropertyChanged();
        }
    }

    public SearchRow? Selected => selectedIndex >= 0 && selectedIndex < rows.Count ? rows[selectedIndex] : null;

    public void Move(int step)
    {
        if (rows.Count == 0) return;
        SelectedIndex = selectedIndex < 0 ? (step > 0 ? 0 : rows.Count - 1) : (selectedIndex + step + rows.Count) % rows.Count;
        OnPropertyChanged(nameof(Selected));
    }

    /// <summary>Enter in the box: opens the selected row, or the first one.</summary>
    public bool ActivateSelectedOrFirst()
    {
        var row = Selected ?? rows.FirstOrDefault();
        if (row is null) return false;
        Activate(row);
        return true;
    }

    public void Activate(SearchRow row)
    {
        var seed = row.Hit.Seed;
        Clear();
        open(seed);
    }

    public void Clear()
    {
        Query = "";
        IsOpen = false;
    }

    private void RunAfterLanguageChange()
    {
        try { Run(); }
        catch (ObjectDisposedException) { index = null; }
    }

    private void Run()
    {
        if (!HasQuery && Groups.Count == 0) { OnPropertyChanged(nameof(NoResults)); OnPropertyChanged(nameof(StatusText)); return; }
        if (HasQuery) index ??= new SearchIndex(seeds(), new LocalizationSearchText(text));
        var words = SearchText.Words(query);
        var results = HasQuery ? index!.Search(query) : [];
        var all = new List<SearchRow>();
        Groups.Clear();
        foreach (var group in results)
        {
            var title = text["SearchGroup" + group.Group];
            var groupRows = group.Hits.Select(h => new SearchRow(h, text, words, title)).ToList();
            var model = new SearchGroupModel(group.Group, title, groupRows, text["SearchShowAll"], Rebuild);
            Groups.Add(model);
        }
        Rebuild();
        IsOpen = HasQuery;
        OnPropertyChanged(nameof(NoResults)); OnPropertyChanged(nameof(NoResultsText)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(Count));
    }

    private void Rebuild()
    {
        rows = [.. Groups.SelectMany(g => g.Rows)];
        selectedIndex = -1;
        foreach (var r in rows) r.IsSelected = false;
        OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(SelectedIndex)); OnPropertyChanged(nameof(Selected)); OnPropertyChanged(nameof(StatusText));
    }
}
