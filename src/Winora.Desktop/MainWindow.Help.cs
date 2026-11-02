// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using Winora.Desktop.Controls;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui;
using Wpf.Ui.Extensions;
using Wpf.Ui.Controls;

namespace Winora.Desktop;

/// <summary>Title bar actions (Back, Help menu, Support) and the rich tooltips of the navigation and the title bar buttons.</summary>
public partial class MainWindow
{
    private void OnBackClick(object sender, RoutedEventArgs e) => model.GoBack();
    private void OnForwardClick(object sender, RoutedEventArgs e) => model.GoForward();
    // The sidebar button opens and closes the navigation pane (wide: labels or icons only; narrow: the overlay pane).
    private void OnSidebarClick(object sender, RoutedEventArgs e)
    {
        // The drawer and the navigation overlay never cover each other.
        if (model.ActionCenter is { IsDrawer: true, IsOpen: true } && !RootNavigation.IsPaneOpen) model.ActionCenter.Close();
        RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        BuildHelpMenu();
        HelpButton.ContextMenu.PlacementTarget = HelpButton;
        HelpButton.ContextMenu.Placement = PlacementMode.Bottom;
        HelpButton.ContextMenu.IsOpen = true;
    }

    private void OnHeartClick(object sender, RoutedEventArgs e) => _ = ShowSupportAsync();

    private void BuildHelpMenu()
    {
        var menu = new ContextMenu { FlowDirection = model.Text.Direction.ToFlow() };
        var about = new System.Windows.Controls.MenuItem { Header = model.Text["AboutMenuItem"], Icon = new SymbolIcon(SymbolRegular.Info24) };
        about.Click += (_, _) => _ = ShowAboutAsync();
        var support = new System.Windows.Controls.MenuItem { Header = model.Text["SupportMenuItem"], Icon = new SymbolIcon(SymbolRegular.Heart24) };
        support.Click += (_, _) => _ = ShowSupportAsync();
        menu.Items.Add(about); menu.Items.Add(support); menu.Items.Add(new Separator());
        menu.Items.Add(CopyItem("LinkGitHub", SymbolRegular.Code24, ProjectLinks.GitHub));
        menu.Items.Add(CopyItem("LinkWebsite", SymbolRegular.Globe24, ProjectLinks.Website));
        AutomationProperties.SetName(menu, model.Text["HelpMenu"]);
        HelpButton.ContextMenu = menu;
    }

    private System.Windows.Controls.MenuItem CopyItem(string labelKey, SymbolRegular icon, string address)
    {
        var item = new System.Windows.Controls.MenuItem { Header = model.Text[labelKey], Icon = new SymbolIcon(icon) };
        AutomationProperties.SetHelpText(item, model.Text["LinkCopyHelp"]);
        item.Click += (_, _) => CopyAddress(address);
        return item;
    }

    private void CopyAddress(string address)
    {
        if (DiagnosticsWriter(address)) model.Feedback.Info(model.Text.Format("LinkCopied", address));
        else model.Feedback.Warning(model.Text["PathCopyFailed"]);
    }

    /// <summary>An address as text (always left to right) with a Copy button.</summary>
    private Grid LinkRow(string labelKey, string address)
    {
        var grid = new Grid { Margin = this.Space("T12") };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Text[labelKey], Style = (Style)FindResource("WinoraBodyStrong") });
        text.Children.Add(new System.Windows.Controls.TextBlock { Text = address, Style = (Style)FindResource("WinoraMono"), HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        grid.Children.Add(text);
        var copy = new Wpf.Ui.Controls.Button { Content = model.Text["CopyPath"], Icon = new SymbolIcon(SymbolRegular.Copy24), Margin = this.Space("L12"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(copy, model.Text["CopyPath"] + ": " + model.Text[labelKey]);
        AutomationProperties.SetHelpText(copy, model.Text["LinkCopyHelp"]);
        copy.Click += (_, _) => CopyAddress(address);
        Grid.SetColumn(copy, 1); grid.Children.Add(copy);
        return grid;
    }

    public Task ShowAboutAsync() => OnUi(() =>
    {
        var panel = new StackPanel { MinWidth = 400, MaxWidth = 460 };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Text["AppName"], Style = (Style)FindResource("WinoraSubtitle") });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Settings.VersionText, Style = (Style)FindResource("WinoraCaption") });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Text["AboutBody"], Style = (Style)FindResource("WinoraBody"), Margin = this.Space("T12") });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Text["AboutCopyright"], Style = (Style)FindResource("WinoraBodyStrong"), Margin = this.Space("T12") });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Text["AboutLocalNote"], Style = (Style)FindResource("WinoraCaption"), Margin = this.Space("T12") });
        panel.Children.Add(LinkRow("LinkGitHub", ProjectLinks.GitHub)); panel.Children.Add(LinkRow("LinkWebsite", ProjectLinks.Website));
        AutomationProperties.SetName(panel, model.Text["AboutDialogTitle"]);
        return ShowDialogAsync(new SimpleContentDialogCreateOptions { Title = model.Text["AboutDialogTitle"], Content = panel, CloseButtonText = model.Text["Close"] });
    });

    public Task ShowSupportAsync() => OnUi(() =>
    {
        var panel = new StackPanel { MinWidth = 400, MaxWidth = 460 };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Text["SupportBody"], Style = (Style)FindResource("WinoraBody") });
        panel.Children.Add(LinkRow("LinkDonate", ProjectLinks.Support)); panel.Children.Add(LinkRow("LinkGitHub", ProjectLinks.GitHub)); panel.Children.Add(LinkRow("LinkWebsite", ProjectLinks.Website));
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Text["LinkNote"], Style = (Style)FindResource("WinoraCaption"), Margin = this.Space("T12") });
        AutomationProperties.SetName(panel, model.Text["SupportDialogTitle"]);
        return ShowDialogAsync(new SimpleContentDialogCreateOptions { Title = model.Text["SupportDialogTitle"], Content = panel, CloseButtonText = model.Text["Close"] });
    });

    /// <summary>A tooltip card: a title, an optional shortcut, and one sentence of description.</summary>
    private ToolTip RichTip(string title, string description, string? shortcut)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new System.Windows.Controls.TextBlock { Text = title, Style = (Style)FindResource("WinoraBodyStrong") };
        grid.Children.Add(head);
        if (shortcut is not null)
        {
            var key = new System.Windows.Controls.TextBlock { Text = shortcut, Style = (Style)FindResource("WinoraCaption"), FlowDirection = FlowDirection.LeftToRight, Margin = this.Space("L16"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(key, 1); grid.Children.Add(key);
        }
        var body = new System.Windows.Controls.TextBlock { Text = description, Style = (Style)FindResource("WinoraCaption"), Margin = this.Space("T4") };
        Grid.SetRow(body, 1); Grid.SetColumnSpan(body, 2); grid.Children.Add(body);
        return new ToolTip { Content = grid, Style = (Style)FindResource("WinoraRichToolTip") };
    }

    private void SetTip(FrameworkElement element, string titleKey, string descriptionKey, string? shortcut, PlacementMode placement)
    {
        // A tip open during a language change would keep the old text on screen until the mouse leaves.
        if (element.ToolTip is ToolTip { IsOpen: true } open) open.IsOpen = false;
        element.ToolTip = RichTip(model.Text[titleKey], model.Text[descriptionKey], shortcut);
        ToolTipService.SetPlacement(element, placement);
        ToolTipService.SetInitialShowDelay(element, 400);
        AutomationProperties.SetHelpText(element, model.Text[descriptionKey]);
    }

    /// <summary>
    /// Rebuilds every tooltip in the current language. Navigation items show their card only while the pane shows icons alone (with labels open the name
    /// is already visible). The title bar buttons always have one.
    /// </summary>
    private void ApplyTips()
    {
        var rtl = model.Text.Direction == TextDirection.RightToLeft;
        var iconsOnly = !(RootNavigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Left && RootNavigation.IsPaneOpen);
        foreach (var d in model.Destinations)
        {
            var item = items[d.Key];
            if (item.ToolTip is ToolTip { IsOpen: true } open) open.IsOpen = false;
            item.ToolTip = RichTip(model.Text[d.Key], model.Text["NavDesc_" + d.Key], null);
            ToolTipService.SetPlacement(item, rtl ? PlacementMode.Left : PlacementMode.Right);
            ToolTipService.SetInitialShowDelay(item, 400);
            ToolTipService.SetIsEnabled(item, iconsOnly);
            if (d.IsAvailable) AutomationProperties.SetHelpText(item, model.Text["NavDesc_" + d.Key]);
        }
        string Keys(string id) => Shortcuts.All.Single(s => s.Id == id).DisplayFor(model.Text.Direction);
        SetTip(HelpButton, "AppMenu", "AppMenuHelp", null, PlacementMode.Bottom);
        SetTip(SidebarButton, "NavigationToggle", "NavigationToggleHelp", null, PlacementMode.Bottom);
        SetTip(BackButton, "BackButton", "BackButtonHelp", Keys(Shortcuts.Back), PlacementMode.Bottom);
        SetTip(ForwardButton, "ForwardButton", "ForwardButtonHelp", Keys(Shortcuts.Forward), PlacementMode.Bottom);
        SetTip(HeartButton, "HeartButton", "HeartButtonHelp", null, PlacementMode.Bottom);
        // Arrows and the panel icon point the way the page reads.
        BackButton.Content = new SymbolIcon(rtl ? SymbolRegular.ArrowRight20 : SymbolRegular.ArrowLeft20) { FontSize = Controls.IconSizes.Button };
        ForwardButton.Content = new SymbolIcon(rtl ? SymbolRegular.ArrowLeft20 : SymbolRegular.ArrowRight20) { FontSize = Controls.IconSizes.Button };
        SidebarButton.Content = new SymbolIcon(rtl ? SymbolRegular.PanelRight20 : SymbolRegular.PanelLeft20) { FontSize = Controls.IconSizes.Button };
        ActionCenterRail.IconControl.Symbol = rtl ? SymbolRegular.PanelLeft20 : SymbolRegular.PanelRight20;
        AutomationProperties.SetName(BackButton, model.Text["BackButton"]);
        AutomationProperties.SetName(ForwardButton, model.Text["ForwardButton"]);
        AutomationProperties.SetName(SidebarButton, model.Text["NavigationToggle"]);
        AutomationProperties.SetName(HelpButton, model.Text["AppMenu"]);
        AutomationProperties.SetName(HeartButton, model.Text["HeartButton"]);
    }
}
