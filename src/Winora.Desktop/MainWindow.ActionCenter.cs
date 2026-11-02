// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Winora.Application.Activity;
using Wpf.Ui.Controls;

namespace Winora.Desktop;

/// <summary>The view side of the Action center: where the pane sits (a column or a drawer), the rail, the marks in the navigation and the keyboard path. The state is in <see cref="ActionCenterViewModel"/>.</summary>
public partial class MainWindow
{
    private ActionCenterViewModel ActionCenterModel => model.ActionCenter;

    /// <summary>The pane control, for tests.</summary>
    public Controls.ActionCenterPane ActionCenterPaneControl => ActionCenter;

    /// <summary>The rail of the closed pane, for tests.</summary>
    public Controls.ActionCenterRail ActionCenterRailControl => ActionCenterRail;

    private void InitActionCenter()
    {
        var vm = ActionCenterModel;
        vm.PropertyChanged += OnActionCenterChanged;
        vm.Opened += OnActionCenterOpened;
        vm.HintRequested += OnActionCenterHint;
        vm.MarksChanged += OnMarksChanged;
        ActionCenterBackdrop.MouseLeftButtonDown += (_, _) => vm.Close();
        // The view model lives as long as the shell; a closed window must not stay reachable from it.
        Closed += (_, _) => { vm.PropertyChanged -= OnActionCenterChanged; vm.Opened -= OnActionCenterOpened; vm.HintRequested -= OnActionCenterHint; vm.MarksChanged -= OnMarksChanged; };
        ApplyActionCenterLayout();
    }

    private void OnActionCenterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActionCenterViewModel.IsOpen) or nameof(ActionCenterViewModel.IsDrawer) or nameof(ActionCenterViewModel.ShowBackdrop) or null or "") ApplyActionCenterLayout();
        if (e.PropertyName is nameof(ActionCenterViewModel.IndicatorIsRunning) or null or "") Dispatcher.BeginInvoke(SpinRailIcon);
    }

    /// <summary>Docked: the right column. Drawer: over the content at the inline end, with a backdrop, and never together with the open navigation overlay.</summary>
    private void ApplyActionCenterLayout()
    {
        var vm = ActionCenterModel;
        var drawer = vm.IsDrawer;
        System.Windows.Controls.Grid.SetColumn(ActionCenter, drawer ? 0 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(ActionCenter, drawer ? 2 : 1);
        System.Windows.Controls.Panel.SetZIndex(ActionCenter, drawer ? 20 : 0);
        System.Windows.Controls.Panel.SetZIndex(ActionCenterBackdrop, drawer ? 19 : 0);
        ActionCenter.Visibility = vm.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        ActionCenterBackdrop.Visibility = vm.ShowBackdrop ? Visibility.Visible : Visibility.Collapsed;
        if (drawer && vm.IsOpen && RootNavigation.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftMinimal && RootNavigation.IsPaneOpen) RootNavigation.IsPaneOpen = false;
    }

    /// <summary>The rail's spinner turns only while a run exists and Windows animations are on; otherwise it stands still or is gone.</summary>
    private void SpinRailIcon()
    {
        var spinner = ActionCenterRail.SpinnerControl;
        if (ActionCenterModel.IndicatorIsRunning && Motion.Enabled)
        {
            var rotation = new RotateTransform();
            spinner.RenderTransformOrigin = new Point(0.5, 0.5);
            spinner.RenderTransform = rotation;
            rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            (spinner.RenderTransform as RotateTransform)?.BeginAnimation(RotateTransform.AngleProperty, null);
            spinner.RenderTransform = null;
        }
    }

    private void OnActionCenterOpened(object? sender, EventArgs e)
    {
        ApplyActionCenterLayout();
        if (ActionCenterModel.IsOpen) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (ActionCenterModel.IsOpen && !IsModalOpen) ActionCenter.HeadingBlock.Focus(); });
        else Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var logical = FocusManager.GetFocusedElement(this) as DependencyObject;
            // The pane folds into the rail, so the keyboard lands there.
            if ((logical is null || IsInsideActionCenter(logical)) && ActionCenterRail.IsVisible) ActionCenterRail.Button.Focus();
        });
    }

    private bool IsInsideActionCenter(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ActionCenter)) return true;
        return false;
    }

    /// <summary>The first time something is staged while the pane is closed the count badge of the rail pulses twice.</summary>
    private void OnActionCenterHint(object? sender, EventArgs e)
    {
        if (!Motion.Enabled) return;
        var dot = ActionCenterRail.BadgeControl;
        var scale = new ScaleTransform(1, 1);
        dot.RenderTransformOrigin = new Point(0.5, 0.5);
        dot.RenderTransform = scale;
        var pulse = new DoubleAnimation(1, 1.6, TimeSpan.FromMilliseconds(380)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) };
        pulse.Completed += (_, _) => dot.RenderTransform = Transform.Identity;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    /// <summary>The window tells the view model how wide the content really is (text scaling counts), so the pane picks docked or drawer.</summary>
    private void UpdateActionCenterWidth() => ActionCenterModel.SetWidth(ActualWidth / contentScale);

    // ---- marks in the navigation ----

    private void OnMarksChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { if (IsLoaded) foreach (var d in model.Destinations) ApplyPending(d.Key); });

    /// <summary>A small spinner while the page has a run, a warning mark after a failure until the page is opened, otherwise the dot with the count of staged changes.</summary>
    private void ApplyNavigationMark(string key, NavigationViewItem item)
    {
        var mark = ActionCenterModel.MarkFor(key);
        var count = model.PendingFor(key);
        var name = NavigationRules.MarkedName(model.Text, key, mark, count);
        if (mark == NavMark.Running)
        {
            var icon = new SymbolIcon(SymbolRegular.ArrowSync12) { FontSize = Controls.IconSizes.Badge };
            if (Motion.Enabled)
            {
                var rotation = new RotateTransform();
                icon.RenderTransformOrigin = new Point(0.5, 0.5);
                icon.RenderTransform = rotation;
                rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            item.InfoBadge = new InfoBadge { Severity = InfoBadgeSeverity.Informational, Icon = icon };
            item.SetValue(AutomationProperties.NameProperty, name);
        }
        else if (mark == NavMark.Failed)
        {
            item.InfoBadge = new InfoBadge { Severity = InfoBadgeSeverity.Caution, Icon = new SymbolIcon(SymbolRegular.Warning12) { FontSize = Controls.IconSizes.Badge } };
            item.SetValue(AutomationProperties.NameProperty, name);
        }
        else if (count > 0)
        {
            item.InfoBadge = new InfoBadge { Severity = InfoBadgeSeverity.Attention, Value = model.Text.FormatNumber(count) };
            item.SetValue(AutomationProperties.NameProperty, name);
        }
        else
        {
            item.InfoBadge = null;
            item.SetBinding(AutomationProperties.NameProperty, new System.Windows.Data.Binding($"[{key}]") { Source = model.Text });
        }
    }
}
