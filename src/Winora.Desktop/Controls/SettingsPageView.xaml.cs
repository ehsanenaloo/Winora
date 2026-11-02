// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Winora.Desktop.Controls;

/// <summary>
/// The page that lists settings: the category chips and filters, the recommended bar, one virtualized list of group headers and cards, the details dialog and the pending-changes bar. The header and the notices
/// above the list come from the page that hosts it. Only the cards that are on screen exist, so a page of 170 settings costs the same as a page of 20. The page is a
/// <see cref="PageWorkspace"/>: the header, the category tab rail and the toolbar stay at the top and the list is the one scroll area under them; the history below the
/// list is the last item of the list, and the notices above it are the first.
/// </summary>
public partial class SettingsPageView : UserControl
{
    /// <summary>Width from which the page uses its roomy padding.</summary>
    public const double WideWidth = 1100;

    private SettingsListModel? model;
    private ISettingsPage? page;
    private ExtendedItems? items;

    public SettingsPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (model is not null) { model.PropertyChanged -= OnModelChanged; model.Entries.CollectionChanged -= OnEntriesChanged; }
            if (page is System.ComponentModel.INotifyPropertyChanged oldPage) oldPage.PropertyChanged -= OnPageChanged;
            page = e.NewValue as ISettingsPage;
            model = page?.List;
            ApplyMode();
            if (model is not null) { model.PropertyChanged += OnModelChanged; model.Entries.CollectionChanged += OnEntriesChanged; }
            if (page is System.ComponentModel.INotifyPropertyChanged newPage) newPage.PropertyChanged += OnPageChanged;
            UpdateItems();
        };
        Workspace.SizeChanged += (_, _) => ApplyWidth();
        Loaded += (_, _) => { ApplyWidth(); UpdateItems(); };
        Unloaded += (_, _) => { items?.Dispose(); items = null; };
    }

    private void OnPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ISettingsPage.ShowList) or null or "") UpdateItems();
    }

    /// <summary>The list shows the entries when the page has a list to show, plus the footer as its last item.</summary>
    private void UpdateItems()
    {
        var tail = Footer is null ? null : new ListExtra(Footer, FooterContext) { IsFooter = true };
        var head = ListExtras.GetHead(SettingsList) is { } notices ? new ListExtra(notices, DataContext) : null;
        var source = page?.ShowList == true && model is not null ? model.Entries : null;
        items?.Dispose();
        items = new ExtendedItems(source, head, tail);
        SettingsList.ItemsSource = items;
    }

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(object), typeof(SettingsPageView), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty BannersProperty = DependencyProperty.Register(nameof(Banners), typeof(object), typeof(SettingsPageView), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty StatesProperty = DependencyProperty.Register(nameof(States), typeof(object), typeof(SettingsPageView), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(nameof(Footer), typeof(object), typeof(SettingsPageView), new FrameworkPropertyMetadata(null, (d, _) => ((SettingsPageView)d).UpdateItems()));
    public static readonly DependencyProperty FooterContextProperty = DependencyProperty.Register(nameof(FooterContext), typeof(object), typeof(SettingsPageView), new FrameworkPropertyMetadata(null, (d, _) => ((SettingsPageView)d).UpdateItems()));
    /// <summary>The data context of the <see cref="Footer"/> (the history of changes of the page).</summary>
    public object? FooterContext { get => GetValue(FooterContextProperty); set => SetValue(FooterContextProperty, value); }
    /// <summary>Content below the list, for example the history of changes.</summary>
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    /// <summary>The page header (title, subtitle, actions).</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    /// <summary>Notices under the header.</summary>
    public object? Banners { get => GetValue(BannersProperty); set => SetValue(BannersProperty, value); }
    /// <summary>Loading, failure and empty states shown instead of the list.</summary>
    public object? States { get => GetValue(StatesProperty); set => SetValue(StatesProperty, value); }

    public ListBox List => SettingsList;
    public FilterBar FilterBarControl => Filters;
    public PageWorkspace Frame => Workspace;
    public bool IsWide => Workspace.ActualWidth >= WideWidth;

    /// <summary>The Optimize page shows one tab of its settings at a time under tab headers; the other pages keep the list with group headers. Both use the same row.</summary>
    private void ApplyMode()
    {
    }

    public void FocusSearch() => Filters.FocusSearch();

    private void ApplyWidth()
    {
        var width = Workspace.ActualWidth;
        if (width <= 0) return;
        Filters.IsNarrow = width < FilterBar.FoldWidth;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsListModel.Tiles)) { ApplyMode(); return; }
        if (e.PropertyName == nameof(SettingsListModel.DetailsOpen) && model?.DetailsOpen == false) FocusSelectedItem();
    }

    /// <summary>A new filter replaces the whole list: it starts at the top. Folding a group edits the list in place and keeps the position.</summary>
    private void OnEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (FindDescendant<ScrollViewer>(SettingsList) is { } scroll) scroll.ScrollToTop(); });
    }

    // ---- Keyboard and mouse ----

    private ListBoxItem? ContainerOf(object entry) => SettingsList.ItemContainerGenerator.ContainerFromItem(entry) as ListBoxItem;

    private void FocusSelectedItem()
    {
        if (model?.Selected is not { } row) return;
        var entry = model.Entries.OfType<SettingsRowEntry>().FirstOrDefault(x => x.Row.Id == row.Id);
        if (entry is null) return;
        SettingsList.SelectedItem = entry;
        SettingsList.ScrollIntoView(entry);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ContainerOf(entry)?.Focus());
    }

    /// <summary>Scrolls the setting into view and puts keyboard focus on its card. Used by search results later and by tests.</summary>
    public bool Reveal(ISettingRow row)
    {
        if (model?.Reveal(row) is not { } entry) return false;
        SettingsList.SelectedItem = entry;
        SettingsList.ScrollIntoView(entry);
        SettingsList.UpdateLayout();
        var focused = ContainerOf(entry)?.Focus() == true;
        // Revealing can rebuild the list, and a rebuilt list scrolls to the top once it is loaded: scroll to the row again after that.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { SettingsList.ScrollIntoView(entry); ContainerOf(entry)?.BringIntoView(); });
        return focused;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        var inside = e.OriginalSource as DependencyObject;
        var container = inside is null ? null : FindItem(inside);
        if (container is null) return;
        var onItem = ReferenceEquals(e.OriginalSource, container);
        if (!onItem)
        {
            // Inside a card: Escape goes back to the card.
            if (e.Key == Key.Escape && (e.OriginalSource as DependencyObject) is not null && !IsPopupOpen(e.OriginalSource as DependencyObject)) { container.Focus(); e.Handled = true; }
            return;
        }
        var entry = container.Content;
        var expandKey = FlowDirection == FlowDirection.RightToLeft ? Key.Left : Key.Right;
        var collapseKey = FlowDirection == FlowDirection.RightToLeft ? Key.Right : Key.Left;
        switch (entry)
        {
            case SettingsGroupEntry group when e.Key is Key.Enter or Key.Space:
                group.ToggleCommand.Execute(null); e.Handled = true; RefocusKey(group.Key); break;
            case SettingsGroupEntry group when e.Key == expandKey && group.IsCollapsed:
                group.ToggleCommand.Execute(null); e.Handled = true; RefocusKey(group.Key); break;
            case SettingsGroupEntry group when e.Key == collapseKey && !group.IsCollapsed:
                group.ToggleCommand.Execute(null); e.Handled = true; RefocusKey(group.Key); break;
            // The context menu key (or Shift+F10) opens the details dialog, which holds the row menu actions (Reset, Revert last, Copy details).
            case SettingsRowEntry row when e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) || (e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift):
                e.Handled = true;
                model?.OpenDetails(row.Row);
                break;
            case SettingsRowEntry row when e.Key is Key.Enter or Key.Space:
                e.Handled = true;
                if (row.Row.IsReviewOnly) model?.OpenDetails(row.Row);
                else if (FindDescendant<ChoiceRow>(container) is { } choice) choice.FocusControl();
                break;
        }
    }

    private static bool IsPopupOpen(DependencyObject? element)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is ComboBox { IsDropDownOpen: true }) return true;
        return false;
    }

    private void RefocusKey(string key) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var header = model?.Entries.OfType<SettingsGroupEntry>().FirstOrDefault(g => g.Key == key);
            if (header is null) return;
            SettingsList.SelectedItem = header;
            ContainerOf(header)?.Focus();
        });

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindItem(source) is not { } container) return;
        for (var d = source; d is not null && !ReferenceEquals(d, container); d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase or ComboBox or ListBoxItem or MenuItem or TextBoxBase) return;
        switch (container.Content)
        {
            case SettingsGroupEntry group: group.ToggleCommand.Execute(null); RefocusKey(group.Key); e.Handled = true; break;
        }
    }

    /// <summary>A double click on the body of a card opens its details. A click on a control of the card does not.</summary>
    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || e.OriginalSource is not DependencyObject source || FindItem(source) is not { } container || container.Content is not SettingsRowEntry row) return;
        for (var d = source; d is not null && !ReferenceEquals(d, container); d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase or ComboBox or ListBoxItem or MenuItem or TextBoxBase or ChoiceRow) return;
        e.Handled = true;
        model?.OpenDetails(row.Row);
    }

    private ListBoxItem? FindItem(DependencyObject source)
    {
        for (var d = source; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) == SettingsList) return item;
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
