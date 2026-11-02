// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
namespace Winora.Desktop;

/// <summary>Picks the dropdown or checkbox template for a filter item.</summary>
public sealed class FilterTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ChoiceTemplate { get; set; }
    public DataTemplate? ToggleTemplate { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is ToggleFilter ? ToggleTemplate : ChoiceTemplate;
}

/// <summary>True for the card that heads a group of the Startup list and for the footer item of the list. Such an item takes no focus and is not selected.</summary>
public sealed class IsCategoryRowConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is StartupCategoryRow or Controls.ListExtra;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>View wiring only: responsive layout, Ctrl+F and keyboard hand-off between search, list and details.</summary>
public partial class DiscoveryView : UserControl
{

    private Controls.ExtendedItems? items;

    public DiscoveryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is System.ComponentModel.INotifyPropertyChanged old) old.PropertyChanged -= OnPageChanged;
            if (!IsLoaded) return;
            if (e.NewValue is System.ComponentModel.INotifyPropertyChanged now) now.PropertyChanged += OnPageChanged;
            UpdateItems();
        };
    }

    public void FocusSearch() => Toolbar.FocusSearch();
    public Wpf.Ui.Controls.TextBox SearchBox => Toolbar.SearchBoxControl;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout(ActualWidth);
        UpdateItems();
        if (DataContext is System.ComponentModel.INotifyPropertyChanged page) { page.PropertyChanged -= OnPageChanged; page.PropertyChanged += OnPageChanged; }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is System.ComponentModel.INotifyPropertyChanged page) page.PropertyChanged -= OnPageChanged;
        items?.Dispose(); items = null;
    }

    private void OnPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IDiscoveryLayout.ListItems) or nameof(IDiscoveryLayout.ShowList) or nameof(IDiscoveryLayout.FooterPanel) or null or "") UpdateItems();
    }

    /// <summary>The list shows the rows when there are rows to show, plus the history of changes (when the page has one) as its last item.</summary>
    private void UpdateItems()
    {
        if (DataContext is not IDiscoveryLayout page) return;
        var tail = page.FooterPanel is { } footer ? new Controls.ListExtra(footer, null) { IsFooter = true } : null;
        items?.Dispose();
        var head = Controls.ListExtras.GetHead(List) is { } banners ? new Controls.ListExtra(banners, DataContext) : null;
        items = new Controls.ExtendedItems(page.ShowList ? page.ListItems : null, head, tail);
        List.ItemsSource = items;
    }

    // A row that was selected from code (for example after a jump from another page) is brought into view. The row a page selects by itself when it opens is among the first items, behind the
    // notices at the top of the list: it stays where it is, so a short window shows the top of the page and not the middle of the list.
    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not { } item) return;
        var index = List.Items.IndexOf(item);
        if (index <= 3 && List.Items.Count > 0 && List.Items[0] is Controls.ListExtra && FindScroller(List) is { VerticalOffset: 0 }) return;
        List.ScrollIntoView(item);
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e) { if (SearchBox.IsVisible) FocusSearch(); }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width);

    // Narrow windows fold the filters behind a button. The list always has the full width; the details of a row open in a dialog.
    private void ApplyResponsiveLayout(double viewWidth)
    {
        var compact = viewWidth < Controls.FilterBar.FoldWidth;
        if (DataContext is IDiscoveryLayout layout) layout.IsCompactLayout = compact;
        // A page whose rows carry their own controls is walked with Tab; the others are walked with the arrow keys and Enter opens the details.
        KeyboardNavigation.SetTabNavigation(List, (DataContext as IDiscoveryLayout)?.RowsHaveControls == true ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.Once);
    }

    private DiscoveryRow? RowOf(DependencyObject? source)
    {
        for (var d = source; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListViewItem { DataContext: DiscoveryRow row }) return row;
        return null;
    }

    private static bool IsControl(DependencyObject? source)
    {
        for (var d = source; d is not null && d is not ListViewItem; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase or TextBoxBase or MenuItem or Wpf.Ui.Controls.ToggleSwitch) return true;
        return false;
    }

    private void OpenDetails(DiscoveryRow row)
    {
        if (DataContext is IDiscoveryLayout page && page.ShowDetailsCommand.CanExecute(row)) page.ShowDetailsCommand.Execute(row);
    }

    // Enter on a list row opens its details; a double click on the row does the same, unless the click was on a control of the row.
    // Space on a row flips its switch or tick box (Startup, Scheduled tasks, Apps): those controls are not tab stops, so this is their keyboard path.
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || Keyboard.Modifiers != ModifierKeys.None || !ReferenceEquals(e.OriginalSource, e.Source) && IsControl(e.OriginalSource as DependencyObject)) return;
        if (RowOf(e.OriginalSource as DependencyObject) is not { } row) return;
        if (e.Key == Key.Space)
        {
            if (DataContext is IDiscoveryLayout page && page.ToggleRow(row)) e.Handled = true;
            return;
        }
        e.Handled = true;
        OpenDetails(row);
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || IsControl(e.OriginalSource as DependencyObject) || RowOf(e.OriginalSource as DependencyObject) is not { } row) return;
        e.Handled = true;
        OpenDetails(row);
    }

    // Down in the search box moves to the list.
    private void OnSearchKeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && List.IsVisible) { List.Focus(); e.Handled = true; }
    }
}
