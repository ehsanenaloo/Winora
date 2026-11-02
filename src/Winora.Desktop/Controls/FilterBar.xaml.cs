// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// The one toolbar of the list and settings pages, always in this order: chips (they only filter), then search (caption above the box, clear button, Ctrl+F, Esc clears), sort (caption, drop-down,
/// an Ascending/Descending button that relabels itself) and the Filters button ("Filters (n)"), then the filter panel the button opens, then the count line. What belongs to one page is passed in:
/// <see cref="PanelContent"/> is the page's own filters (inside the one panel, which also holds "Clear filters") and <see cref="ExtraContent"/> is a control that sits at the end of the row.
/// Every part is optional: a part without data is collapsed. The count line is a polite live region.
/// </summary>
public partial class FilterBar : UserControl
{
    /// <summary>Below this width the sort direction button shows only its icon.</summary>
    public const double FoldWidth = 820;

    public FilterBar()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyParts();
        // The search box keeps its width for the longest placeholder but never runs past the row (large text on a small window).
        Tools.SizeChanged += (_, e) => SearchBlock.MaxWidth = Math.Max(0, e.NewSize.Width - SearchBlock.Margin.Left - SearchBlock.Margin.Right);
    }

    private static DependencyProperty Prop<T>(string name, T value = default!, PropertyChangedCallback? changed = null, FrameworkPropertyMetadataOptions options = FrameworkPropertyMetadataOptions.None) =>
        DependencyProperty.Register(name, typeof(T), typeof(FilterBar), new FrameworkPropertyMetadata(value, options, changed ?? ((d, _) => ((FilterBar)d).ApplyParts())));

    public static readonly DependencyProperty IsNarrowProperty = Prop(nameof(IsNarrow), false);
    public static readonly DependencyProperty AsTabsProperty = Prop(nameof(AsTabs), false, (d, _) => ((FilterBar)d).ApplyTabs());
    public static readonly DependencyProperty ChipsSourceProperty = Prop<IEnumerable?>(nameof(ChipsSource), null, (d, e) => ((FilterBar)d).OnChipsSource((IEnumerable?)e.OldValue, (IEnumerable?)e.NewValue));
    public static readonly DependencyProperty SelectedChipProperty = Prop<object?>(nameof(SelectedChip), null, null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault);
    public static readonly DependencyProperty ChipsNameProperty = Prop(nameof(ChipsName), "");
    public static readonly DependencyProperty ChipsHelpProperty = Prop(nameof(ChipsHelp), "");
    public static readonly DependencyProperty SearchTextProperty = Prop(nameof(SearchText), "", null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault);
    public static readonly DependencyProperty SearchLabelProperty = Prop(nameof(SearchLabel), "");
    public static readonly DependencyProperty SearchPlaceholderProperty = Prop(nameof(SearchPlaceholder), "");
    public static readonly DependencyProperty SearchHelpProperty = Prop(nameof(SearchHelp), "");
    public static readonly DependencyProperty SortSourceProperty = Prop<IEnumerable?>(nameof(SortSource));
    public static readonly DependencyProperty SelectedSortProperty = Prop<object?>(nameof(SelectedSort), null, null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault);
    public static readonly DependencyProperty SortLabelProperty = Prop(nameof(SortLabel), "");
    public static readonly DependencyProperty SortHelpProperty = Prop(nameof(SortHelp), "");
    public static readonly DependencyProperty SortDirectionCommandProperty = Prop<ICommand?>(nameof(SortDirectionCommand));
    public static readonly DependencyProperty SortDirectionTextProperty = Prop(nameof(SortDirectionText), "");
    public static readonly DependencyProperty SortDirectionSymbolProperty = Prop(nameof(SortDirectionSymbol), SymbolRegular.ArrowSortUp24);
    public static readonly DependencyProperty SortDirectionHelpProperty = Prop(nameof(SortDirectionHelp), "");
    public static readonly DependencyProperty FiltersTextProperty = Prop(nameof(FiltersText), "");
    public static readonly DependencyProperty FiltersHelpProperty = Prop(nameof(FiltersHelp), "");
    public static readonly DependencyProperty HasFiltersProperty = Prop(nameof(HasFilters), false);
    public static readonly DependencyProperty FiltersOpenProperty = Prop(nameof(FiltersOpen), false, (d, e) => { var bar = (FilterBar)d; bar.FiltersToggle.IsChecked = (bool)e.NewValue; bar.ApplyParts(); }, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault);
    public static readonly DependencyProperty PanelContentProperty = Prop<object?>(nameof(PanelContent));
    public static readonly DependencyProperty ExtraContentProperty = Prop<object?>(nameof(ExtraContent));
    public static readonly DependencyProperty ClearCommandProperty = Prop<ICommand?>(nameof(ClearCommand));
    public static readonly DependencyProperty ClearTextProperty = Prop(nameof(ClearText), "");
    public static readonly DependencyProperty ClearHelpProperty = Prop(nameof(ClearHelp), "");
    public static readonly DependencyProperty ShowClearProperty = Prop(nameof(ShowClear), false);
    public static readonly DependencyProperty CountTextProperty = Prop(nameof(CountText), "");
    public static readonly DependencyProperty ShowCountProperty = Prop(nameof(ShowCount), false);
    public static readonly DependencyProperty GroupNameProperty = Prop(nameof(GroupName), "");

    /// <summary>True when the page is narrower than <see cref="FoldWidth"/>: the sort direction button shows only its icon.</summary>
    public bool IsNarrow { get => (bool)GetValue(IsNarrowProperty); set => SetValue(IsNarrowProperty, value); }
    /// <summary>The chips are drawn as a tab strip (text with an underline) instead of pills.</summary>
    public bool AsTabs { get => (bool)GetValue(AsTabsProperty); set => SetValue(AsTabsProperty, value); }
    public IEnumerable? ChipsSource { get => (IEnumerable?)GetValue(ChipsSourceProperty); set => SetValue(ChipsSourceProperty, value); }
    public object? SelectedChip { get => GetValue(SelectedChipProperty); set => SetValue(SelectedChipProperty, value); }
    public string ChipsName { get => (string)GetValue(ChipsNameProperty); set => SetValue(ChipsNameProperty, value); }
    public string ChipsHelp { get => (string)GetValue(ChipsHelpProperty); set => SetValue(ChipsHelpProperty, value); }
    public string SearchText { get => (string)GetValue(SearchTextProperty); set => SetValue(SearchTextProperty, value); }
    public string SearchLabel { get => (string)GetValue(SearchLabelProperty); set => SetValue(SearchLabelProperty, value); }
    public string SearchPlaceholder { get => (string)GetValue(SearchPlaceholderProperty); set => SetValue(SearchPlaceholderProperty, value); }
    public string SearchHelp { get => (string)GetValue(SearchHelpProperty); set => SetValue(SearchHelpProperty, value); }
    public IEnumerable? SortSource { get => (IEnumerable?)GetValue(SortSourceProperty); set => SetValue(SortSourceProperty, value); }
    public object? SelectedSort { get => GetValue(SelectedSortProperty); set => SetValue(SelectedSortProperty, value); }
    public string SortLabel { get => (string)GetValue(SortLabelProperty); set => SetValue(SortLabelProperty, value); }
    public string SortHelp { get => (string)GetValue(SortHelpProperty); set => SetValue(SortHelpProperty, value); }
    /// <summary>The Ascending/Descending button; without a command it is not shown.</summary>
    public ICommand? SortDirectionCommand { get => (ICommand?)GetValue(SortDirectionCommandProperty); set => SetValue(SortDirectionCommandProperty, value); }
    public string SortDirectionText { get => (string)GetValue(SortDirectionTextProperty); set => SetValue(SortDirectionTextProperty, value); }
    public SymbolRegular SortDirectionSymbol { get => (SymbolRegular)GetValue(SortDirectionSymbolProperty); set => SetValue(SortDirectionSymbolProperty, value); }
    public string SortDirectionHelp { get => (string)GetValue(SortDirectionHelpProperty); set => SetValue(SortDirectionHelpProperty, value); }
    /// <summary>The Filters button: "Filters" or "Filters (n)".</summary>
    public string FiltersText { get => (string)GetValue(FiltersTextProperty); set => SetValue(FiltersTextProperty, value); }
    public string FiltersHelp { get => (string)GetValue(FiltersHelpProperty); set => SetValue(FiltersHelpProperty, value); }
    /// <summary>The page has filters behind the Filters button.</summary>
    public bool HasFilters { get => (bool)GetValue(HasFiltersProperty); set => SetValue(HasFiltersProperty, value); }
    public bool FiltersOpen { get => (bool)GetValue(FiltersOpenProperty); set => SetValue(FiltersOpenProperty, value); }
    public object? PanelContent { get => GetValue(PanelContentProperty); set => SetValue(PanelContentProperty, value); }
    public object? ExtraContent { get => GetValue(ExtraContentProperty); set => SetValue(ExtraContentProperty, value); }
    public ICommand? ClearCommand { get => (ICommand?)GetValue(ClearCommandProperty); set => SetValue(ClearCommandProperty, value); }
    public string ClearText { get => (string)GetValue(ClearTextProperty); set => SetValue(ClearTextProperty, value); }
    public string ClearHelp { get => (string)GetValue(ClearHelpProperty); set => SetValue(ClearHelpProperty, value); }
    /// <summary>A search or a filter is active: "Clear filters" and the count line are shown.</summary>
    public bool ShowClear { get => (bool)GetValue(ShowClearProperty); set => SetValue(ShowClearProperty, value); }
    public string CountText { get => (string)GetValue(CountTextProperty); set => SetValue(CountTextProperty, value); }
    public bool ShowCount { get => (bool)GetValue(ShowCountProperty); set => SetValue(ShowCountProperty, value); }
    /// <summary>Accessible name of the whole toolbar.</summary>
    public string GroupName { get => (string)GetValue(GroupNameProperty); set => SetValue(GroupNameProperty, value); }

    private void OnChipsSource(IEnumerable? old, IEnumerable? source)
    {
        if (old is System.Collections.Specialized.INotifyCollectionChanged was) was.CollectionChanged -= OnChipsChanged;
        if (source is System.Collections.Specialized.INotifyCollectionChanged now) now.CollectionChanged += OnChipsChanged;
        ApplyParts();
    }

    private void OnChipsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ApplyParts();

    private void ApplyTabs()
    {
        Chips.ItemContainerStyle = (Style)FindResource(AsTabs ? "TabChipItem" : "CategoryChipItem");
        ApplyParts();
    }

    private void ApplyParts()
    {
        if (!IsInitialized) return;
        FilterPanel.Visibility = FiltersOpen && HasFilters ? Visibility.Visible : Visibility.Collapsed;
        var hasChips = ChipsSource is not null && ChipsSource.Cast<object>().Any();
        Chips.Visibility = hasChips ? Visibility.Visible : Visibility.Collapsed;
        SortBlock.Visibility = SortSource is null ? Visibility.Collapsed : Visibility.Visible;
        SortDirection.Visibility = SortDirectionCommand is null ? Visibility.Collapsed : Visibility.Visible;
        SortDirectionLabel.Visibility = IsNarrow ? Visibility.Collapsed : Visibility.Visible;
        FiltersToggle.Visibility = HasFilters ? Visibility.Visible : Visibility.Collapsed;
        Tools.Margin = this.Space(hasChips && AsTabs ? "T4B8" : "B8");
        if (FiltersToggle.IsChecked != FiltersOpen) FiltersToggle.IsChecked = FiltersOpen;
    }

    private void OnFiltersToggled(object sender, RoutedEventArgs e) => SetCurrentValue(FiltersOpenProperty, FiltersToggle.IsChecked == true);

    public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }
    public Wpf.Ui.Controls.TextBox SearchBoxControl => SearchBox;

    /// <summary>A key pressed in the search box that the bar did not use (the page may move focus to its list on Down).</summary>
    public event KeyEventHandler? SearchKeyPressed;

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; return; }
        SearchKeyPressed?.Invoke(this, e);
    }
}
