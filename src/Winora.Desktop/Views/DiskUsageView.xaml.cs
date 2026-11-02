// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Winora.Application;

namespace Winora.Desktop;

public partial class DiskUsageView : UserControl
{
    /// <summary>The scroll viewer of the page body.</summary>
    public ScrollViewer PageScroll => Workspace.BodyScroller ?? new ScrollViewer();

    public DiskUsageView()
    {
        InitializeComponent();
        Map.NodeInvoked += node => (DataContext as DiskUsageViewModel)?.Invoke(node);
        Map.UpRequested += () => (DataContext as DiskUsageViewModel)?.UpCommand.Execute(null);
        Map.ContextMenu = new ContextMenu();
        Map.ContextMenuOpening += OnMapContextMenuOpening;
        Loaded += (_, _) => LayoutUpdated += OnLayoutUpdated;
        Unloaded += (_, _) => LayoutUpdated -= OnLayoutUpdated;
    }

    /// <summary>The map and the list never get smaller than this; a short window scrolls the page instead of squeezing the map away.</summary>
    private const double MinResultHeight = 480;
    private double lastResultHeight = -1;

    /// <summary>Gives the map and the list the room that is left under the disks and the controls, but never less than <see cref="MinResultHeight"/>.</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsLoaded || PageScroll.ViewportHeight <= 0 || ResultGrid.Visibility != Visibility.Visible) return;
        double top;
        try { top = ResultGrid.TransformToAncestor(Root).Transform(new Point(0, 0)).Y; }
        catch (InvalidOperationException) { return; } // not in the visual tree for a moment
        var height = Math.Max(MinResultHeight, Math.Floor(PageScroll.ViewportHeight - top - 120));
        if (Math.Abs(height - lastResultHeight) < 1) return;
        lastResultHeight = height;
        ResultGrid.Height = height;
    }

    /// <summary>A cell chosen on the map selects its row; the list scrolls to it so the two always agree.</summary>
    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemList.SelectedItem is { } item) ItemList.ScrollIntoView(item);
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DiskUsageViewModel vm || sender is not ListViewItem { DataContext: DiskItemRow row }) return;
        vm.Invoke(row.Node);
        e.Handled = true;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not DiskUsageViewModel vm) return;
        if (e.Key == Key.Enter && vm.OpenCommand.CanExecute(null)) { vm.OpenCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Back && vm.UpCommand.CanExecute(null)) { vm.UpCommand.Execute(null); e.Handled = true; }
    }

    private void OnMapContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not DiskUsageViewModel vm || Map.ContextMenu is not { } menu || Map.SelectedNode is not { } node) { e.Handled = true; return; }
        Fill(menu, vm, node);
    }

    private void OnItemContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not DiskUsageViewModel vm || sender is not ListViewItem { DataContext: DiskItemRow row } item || item.ContextMenu is not { } menu) { e.Handled = true; return; }
        vm.SelectedNode = row.Node;
        Fill(menu, vm, row.Node);
    }

    /// <summary>Builds the menu of one node. Entries come from the view model, so the map and the list always offer the same actions.</summary>
    internal static void Fill(ContextMenu menu, DiskUsageViewModel vm, DiskNode node)
    {
        menu.Items.Clear();
        AutomationProperties.SetName(menu, vm.Text["DiskMenuLabel"]);
        foreach (var entry in vm.MenuFor(node))
        {
            if (entry.IsSeparator) { menu.Items.Add(new Separator()); continue; }
            var item = new MenuItem { Header = entry.Label, IsEnabled = entry.IsEnabled, Command = entry.Command };
            AutomationProperties.SetName(item, entry.Label);
            menu.Items.Add(item);
        }
    }
}
