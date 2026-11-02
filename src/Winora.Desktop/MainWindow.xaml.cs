// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Windows;
using Winora.Desktop.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Threading;
using Winora.Application;
using Winora.Domain;
using Wpf.Ui;
using Wpf.Ui.Extensions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Winora.Desktop;

public partial class MainWindow : FluentWindow, IReviewPrompt, IMachineConsentPrompt, IRestorePointPrompt
{
    static MainWindow() { Controls.IconSizes.Register(); Controls.SmoothScroll.Register(); Controls.LiveRegions.Register(); }

    private readonly ShellViewModel model;
    private readonly ErrorPresenter errors;
    private readonly ITextScaleSource textScale;
    private double contentScale = 1;
    private bool blockingShown;
    private readonly ContentDialogService dialogs = new();
    private readonly Dictionary<string, NavigationViewItem> items = [];
    private readonly Dictionary<NavSection, NavigationViewItemHeader> headers = [];
    private readonly Dictionary<NavSection, System.Windows.Controls.Primitives.ToggleButton> sectionToggles = [];
    private readonly Dictionary<NavSection, SymbolIcon> sectionChevrons = [];
    private readonly Dictionary<NavSection, FrameworkElement> sectionDividers = [];
    private readonly ThemeController theme;
    private ModalDialogs modals = null!;
    private bool syncingSelection;

    private readonly RestorePointService? restorePoints;
    private readonly IRestorePointGate? restoreGate;

    public MainWindow(ShellViewModel model, ReviewPromptHost prompt, ErrorPresenter errors, ITextScaleSource textScale, RestorePointService? restorePoints = null, IRestorePointGate? restoreGate = null)
    {
        this.model = model; this.errors = errors; this.textScale = textScale; this.restorePoints = restorePoints; this.restoreGate = restoreGate;
        DensityResources.Apply(model.Settings.Density);
        theme = new ThemeController(this, model);
        theme.Apply();
        Controls.A11y.NameUnnamedProgress(model.Text);
        InitializeComponent();
        var work = SystemParameters.WorkArea;
        var fit = WindowSizing.FitToWorkArea(Width, Height, MinWidth, MinHeight, work.Width, work.Height);
        MinWidth = fit.MinWidth; MinHeight = fit.MinHeight; Width = fit.Width; Height = fit.Height;
        DataContext = model;
        prompt.Inner = this;
        dialogs.SetDialogHost(DialogHost);
        modals = new ModalDialogs(this, DialogHost, () => contentScale, () => model.Text.Direction, FocusPageHeading);
        BuildNavigation();
        RootNavigation.ApplyTemplate();
        RootNavigation.ReplaceContent(new ShellContent { DataContext = model });
        ApplyTips();
        InitActionCenter();
        model.Settings.ThemeChanged += OnThemeChanged; model.Settings.AccentChanged += OnThemeChanged;
        model.Settings.DensityChanged += OnDensityChanged;
        model.PropertyChanged += OnShellChanged;
        model.Text.PropertyChanged += OnLanguageChanged;
        textScale.Changed += OnTextScaleChanged;
        model.Navigated += OnNavigated; model.HighlightRequested += OnHighlightRequested; model.Settings.ShortcutsRequested += OnShortcutsRequested; model.Settings.WelcomeRequested += OnWelcomeRequested; model.FirstRun.PropertyChanged += OnFirstRunChanged;
        SystemParameters.StaticPropertyChanged += OnSystemChanged;
        ApplicationThemeManager.Changed += theme.OnApplicationThemeChanged;
        errors.BlockingRequested += OnBlockingRequested;
        SizeChanged += (_, _) => { UpdatePaneMode(); UpdateActionCenterWidth(); UpdateTitleDetails(); };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => { ApplyContentScale(textScale.Factor); theme.Apply(); ApplyMotion(MotionPolicy.AnimationsEnabled); ApplyLanguage(); UpdatePaneMode(); UpdateActionCenterWidth(); UpdateTitleDetails(); SelectItem(model.Selected.Key); };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            model.Settings.ThemeChanged -= OnThemeChanged; model.Settings.AccentChanged -= OnThemeChanged; model.Settings.DensityChanged -= OnDensityChanged; model.PropertyChanged -= OnShellChanged;
            textScale.Changed -= OnTextScaleChanged; model.PendingChanged -= OnPendingChanged;
            model.Navigated -= OnNavigated; model.HighlightRequested -= OnHighlightRequested; model.SectionExpansionChanged -= OnSectionExpansionChanged; model.Settings.ShortcutsRequested -= OnShortcutsRequested; model.Settings.WelcomeRequested -= OnWelcomeRequested; model.FirstRun.PropertyChanged -= OnFirstRunChanged;
            ApplicationThemeManager.Changed -= theme.OnApplicationThemeChanged; model.Text.PropertyChanged -= OnLanguageChanged; SystemParameters.StaticPropertyChanged -= OnSystemChanged; errors.BlockingRequested -= OnBlockingRequested;
        };
        ApplyLanguage();
    }

    public NavigationViewItem NavigationItem(string key) => items[key];

    /// <summary>The application sets this; the window then asks before it closes with staged changes (the only close prompt). Tests leave it off.</summary>
    public bool AskBeforeClosingWithPending { get; set; }
    private bool closeConfirmed;

    /// <summary>Why the window hides or closes, for the log. Plain English, never shown.</summary>
    public event EventHandler<string>? ExitReason;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // With the setting on, closing only hides the window, so staged changes stay and nothing is asked.
        var hide = ClosingHidesToTray();
        if (hide && model.Settings.KeepInTray) { e.Cancel = true; ExitReason?.Invoke(this, "hidden to the notification area (setting on)"); HideToTray(); return; }
        if (DialogRules.AskBeforeClose(AskBeforeClosingWithPending, closeConfirmed, IsModalOpen, model.TotalPending))
        {
            e.Cancel = true;
            _ = CloseAfterConfirmAsync();
            return;
        }
        // A long run or a system change goes on in the notification area; the app ends when it is over.
        if (hide) { e.Cancel = true; ExitReason?.Invoke(this, "hidden to the notification area during a run"); HideToTray(); return; }
        ExitReason?.Invoke(this, exitForReal ? "exit from the notification area or after the run it waited for" : "the window was closed");
        theme.Stop();
    }

    private async Task CloseAfterConfirmAsync()
    {
        if (!await ConfirmDiscardPendingAsync()) { exitForReal = false; trayModel?.CancelExit(); return; }
        closeConfirmed = true;
        Close();
    }

    /// <summary>"Discard 3 pending changes?" Nothing was applied, so closing only drops what was staged.</summary>
    public Task<bool> ConfirmDiscardPendingAsync() => OnUi(() =>
        ShowConfirmAsync(ConfirmDialogModel.Simple(model.Text.Format("ClosePendingTitle", model.TotalPending), model.Text["ClosePendingBody"], model.Text["ClosePendingPrimary"], model.Text)));

    // ---- Keyboard shortcuts (the documented set lives in Shortcuts.All) ----
    /// <summary>True while any dialog is open; global shortcuts are ignored then so the page behind a dialog cannot be driven.</summary>
    public bool IsModalOpen => modals?.IsOpen == true;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
#if DEBUG
        if (e.Key == Key.G && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) { Gallery.GalleryWindow.Open(model.Text); e.Handled = true; return; }
#endif
        // WPF-UI dialogs do not close on Escape by themselves. Escape is "Back": it dismisses the dialog without choosing the primary action.
        if (e.Key == Key.Escape && IsModalOpen && FindVisible<ContentDialog>(DialogHost) is { } open) { open.Hide(ContentDialogResult.None); e.Handled = true; return; }
        if (e.Key == Key.Escape && !IsModalOpen && ActionCenterModel is { IsDrawer: true, IsOpen: true } && !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)) { ActionCenterModel.Close(); e.Handled = true; return; }
        if (RunShortcut(ShortcutKeys.Match(e.Key, e.SystemKey, Keyboard.Modifiers, model.Text.Direction))) e.Handled = true;
    }

    /// <summary>Runs a global shortcut by id. Returns true when the key was consumed.</summary>
    public bool RunShortcut(string? id)
    {
        if (id is null || IsModalOpen) return false;
        switch (id)
        {
            case Shortcuts.Find: return FocusSearch();
            case Shortcuts.Help: _ = ShowShortcutsAsync(); return true;
            case Shortcuts.Regions: return CycleRegions(1);
            case Shortcuts.RegionsBack: return CycleRegions(-1);
            case Shortcuts.Search: return FocusGlobalSearch();
            case Shortcuts.Undo: return RunUndoShortcut();
            default: return model.RunShortcut(id);
        }
    }

    /// <summary>
    /// Every prompt can be called from a worker thread (the restore point gate and the engines ask from one). The dialog and its elements belong to the UI thread, so each
    /// prompt method runs its body through this helper. Calling from the UI thread runs the body at once.
    /// </summary>
    private Task<T> OnUi<T>(Func<Task<T>> body) => Dispatcher.CheckAccess() ? body() : Dispatcher.InvokeAsync(body).Task.Unwrap();

    private Task OnUi(Func<Task> body) => Dispatcher.CheckAccess() ? body() : Dispatcher.InvokeAsync(body).Task.Unwrap();

    public Task ShowShortcutsAsync() => OnUi(() =>
        ShowDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = model.Text["ShortcutsTitle"], Content = new ShortcutsContent { DataContext = new ShortcutsModel(model.Text) },
            CloseButtonText = model.Text["Close"],
        }));

    /// <summary>Opens the first-run guide (at start on a new profile, and from Settings). It is a page of the window, not a dialog; the preference is written when the guide is finished or skipped.</summary>
    public Task ShowWelcomeAsync() => OnUi(() => { if (!IsModalOpen) model.FirstRun.Begin(); return Task.CompletedTask; });

    private Task<ContentDialogResult> ShowDialogAsync(SimpleContentDialogCreateOptions options) => modals.ShowAsync(options);

    private Task<ContentDialogResult> ShowDialogAsync(Func<ContentDialog> create) => modals.ShowAsync(create);

    /// <summary>The one confirmation dialog. High risk and irreversible choices get a red primary button and Cancel as the default answer; High risk also needs the box inside the dialog ticked.</summary>
    private async Task<bool> ShowConfirmAsync(ConfirmDialogModel vm)
    {
        var result = await ShowDialogAsync(() =>
        {
            var dialog = new ContentDialog(DialogHost)
            {
                Title = vm.Title, Content = new Controls.ConfirmDialogContent { DataContext = vm },
                PrimaryButtonText = vm.PrimaryText, CloseButtonText = vm.CancelText,
                PrimaryButtonAppearance = vm.PrimaryIsDanger ? ControlAppearance.Danger : ControlAppearance.Primary,
                DefaultButton = vm.DefaultIsCancel ? ContentDialogButton.Close : ContentDialogButton.Primary,
            };
            // The primary button stays visible; pressing it before the box is ticked keeps the dialog open and says why.
            dialog.Closing += (_, e) =>
            {
                if (e.Result != ContentDialogResult.Primary || vm.CanConfirm) return;
                e.Cancel = true; vm.MarkMissing();
                if ((dialog.Content as Controls.ConfirmDialogContent)?.FindName("Acknowledge") is System.Windows.Controls.CheckBox box) box.Focus();
            };
            _ = LoadRestorePointLineAsync(vm);
            return dialog;
        });
        return result == ContentDialogResult.Primary && vm.CanConfirm;
    }

    /// <summary>The restore point status line of the confirmation. It reads Windows off the UI thread and fills in when ready; the dialog never waits for it.</summary>
    private async Task LoadRestorePointLineAsync(ConfirmDialogModel vm)
    {
        if (!vm.UsesRestorePoint) return;
        if (await RestorePointText.LoadStatusLineAsync(model.Text, restorePoints, restoreGate) is { } line) vm.RestorePointLine = line;
    }

    private UIElement? CurrentFocus() => modals.CurrentFocus();

    // After navigation the keyboard focus moves to the new page's heading so a screen reader announces where the user is.
    private void OnNavigated(string key) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { FocusPageHeading(); ApplyHighlight(); });
    private void OnHighlightRequested() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyHighlight());

    public bool FocusPageHeading()
    {
        var heading = FindPageHeading(this);
        return heading is not null && heading.Focus();
    }

    public static System.Windows.Controls.TextBlock? FindPageHeading(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.TextBlock { IsVisible: true, Focusable: true } text && AutomationProperties.GetHeadingLevel(text) == AutomationHeadingLevel.Level1) return text;
            if (FindPageHeading(child) is { } nested) return nested;
        }
        return null;
    }
    /// <summary>The areas F6 moves between, in order: navigation, page, notification (while a toast shows), Action center (while it is open).</summary>
    public List<string> Regions() => RegionRules.Regions(FindVisible<Controls.ToastHost>(this) is { Toast: not null }, ActionCenterModel.IsOpen);

    /// <summary>The area that holds the keyboard focus now.</summary>
    public string CurrentRegion()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        for (var d = focused; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is Controls.ToastHost) return RegionRules.Toast;
            if (d is Controls.ActionCenterPane) return RegionRules.ActionCenter;
            if (d is ShellContent) return RegionRules.Content;
        }
        return focused is null ? RegionRules.Content : RegionRules.Navigation;
    }

    public bool CycleRegions(int step) => FocusRegion(RegionRules.Next(Regions(), CurrentRegion(), step));

    public bool FocusRegion(string region)
    {
        switch (region)
        {
            case RegionRules.Navigation:
                return items.TryGetValue(model.Selected.Key, out var item) && item.Focus();
            case RegionRules.Toast:
                return FindVisible<Controls.ToastHost>(this)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)) == true;
            case RegionRules.ActionCenter:
                return ActionCenter.IsVisible && ActionCenter.HeadingBlock.Focus();
            default:
                return FocusPageHeading();
        }
    }

    /// <summary>
    /// Ctrl+Z works on the History page only, and not while a text box has focus (there it keeps its text-edit meaning). It selects the newest entry that can be undone and opens its undo review;
    /// it never undoes anything without that review.
    /// </summary>
    public bool RunUndoShortcut()
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return false;
        return model.TryUndoNewest();
    }

    /// <summary>Ctrl+K: focuses the search box of the title bar. Escape there gives focus back to the control that had it.</summary>
    public bool FocusGlobalSearch() => GlobalSearch.FocusBox(CurrentFocus());

    /// <summary>The search asked the open page to highlight an item. A page that cannot do it stays as it is, with the heading focused.</summary>
    private bool ApplyHighlight()
    {
        if (model.TakePendingHighlight() is not { } target) return false;
        return FindHighlightTarget(RootNavigation) is { } view && view.Highlight(target);
    }

    private static Search.IHighlightTarget? FindHighlightTarget(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Search.IHighlightTarget { } target && child is UIElement { IsVisible: true }) return target;
            if (FindHighlightTarget(child) is { } nested) return nested;
        }
        return null;
    }

    public bool FocusSearch()
    {
        var view = FindVisible<DiscoveryView>(this);
        if (view is not null) { view.FocusSearch(); return true; }
        var settings = FindVisible<CustomizeView>(this);
        if (settings is not null) { settings.FocusSearch(); return true; }
        var optimize = FindVisible<OptimizeView>(this);
        if (optimize is null) return false;
        optimize.FocusSearch();
        return true;
    }
    private static T? FindVisible<T>(DependencyObject root) where T : UIElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.IsVisible) return match;
            if (FindVisible<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>The section header above a group of navigation items (not focusable; named for assistive technology).</summary>
    public NavigationViewItemHeader NavigationHeader(NavSection section) => headers[section];

    private void BuildNavigation()
    {
        NavSection? last = null;
        foreach (var d in model.Destinations)
        {
            if (d.SectionKey is { } sectionKey && last != d.Section)
            {
                var header = new NavigationViewItemHeader { Focusable = false, IsTabStop = false, Template = (System.Windows.Controls.ControlTemplate)FindResource("WinoraNavSectionHeaderTemplate") };
                header.SetBinding(NavigationViewItemHeader.TextProperty, new Binding($"[{sectionKey}]") { Source = model.Text });
                header.SetBinding(AutomationProperties.NameProperty, new Binding($"[{sectionKey}]") { Source = model.Text });
                headers[d.Section] = header;
                WireSectionToggle(d.Section, header, sectionKey);
                RootNavigation.MenuItems.Add(header);
                last = d.Section;
            }
            var item = new NavigationViewItem { Icon = new SymbolIcon(Controls.IconKeys.SymbolOf(d.Icon)) { FontSize = Controls.IconSizes.Lead }, Tag = d.Key, MinHeight = 40 };
            item.SetBinding(ContentControlContent, new Binding($"[{d.Key}]") { Source = model.Text });
            item.SetBinding(AutomationProperties.NameProperty, new Binding($"[{d.Key}]") { Source = model.Text });
            if (!d.IsAvailable) item.SetBinding(AutomationProperties.HelpTextProperty, new Binding("[StatusPlanned]") { Source = model.Text });
            item.Click += (_, _) => { if (!syncingSelection) model.Navigate(d.Key); };
            items[d.Key] = item;
            (d.Section == NavSection.Footer ? RootNavigation.FooterMenuItems : RootNavigation.MenuItems).Add(item);
        }
        model.PendingChanged += OnPendingChanged;
        model.SectionExpansionChanged += OnSectionExpansionChanged;
        RootNavigation.PaneOpened += (_, _) => ApplySections();
        RootNavigation.PaneClosed += (_, _) => ApplySections();
        foreach (var d in model.Destinations) ApplyPending(d.Key);
        ApplySections();
    }

    private void WireSectionToggle(NavSection section, NavigationViewItemHeader header, string sectionKey)
    {
        header.ApplyTemplate();
        if (header.Template.FindName("PART_Toggle", header) is not System.Windows.Controls.Primitives.ToggleButton toggle) return;
        toggle.SetBinding(AutomationProperties.NameProperty, new Binding($"[{sectionKey}]") { Source = model.Text });
        toggle.SetBinding(AutomationProperties.HelpTextProperty, new Binding("[NavSectionToggleHelp]") { Source = model.Text });
        toggle.Click += (_, _) => model.SetSectionExpanded(section, toggle.IsChecked == true);
        sectionToggles[section] = toggle;
        sectionChevrons[section] = (SymbolIcon)header.Template.FindName("Chevron", header);
        if (header.Template.FindName("PART_Divider", header) is FrameworkElement divider) sectionDividers[section] = divider;
    }

    /// <summary>The toggle of a section header (opens or closes the section).</summary>
    public System.Windows.Controls.Primitives.ToggleButton NavigationSectionToggle(NavSection section) => sectionToggles[section];

    private void OnSectionExpansionChanged(NavSection section) => Dispatcher.BeginInvoke(ApplySections);

    /// <summary>Shows the items of open sections. A closed pane shows icons only and has no section toggles, so every item stays reachable there.</summary>
    private void ApplySections()
    {
        var collapsible = NavigationRules.SectionsCollapsible(RootNavigation.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftMinimal, RootNavigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Left && RootNavigation.IsPaneOpen);
        foreach (var d in model.Destinations.Where(d => d.Section != NavSection.Footer))
        {
            var open = model.IsSectionExpanded(d.Section);
            items[d.Key].Visibility = NavigationRules.ItemVisible(open, collapsible) ? Visibility.Visible : Visibility.Collapsed;
            if (sectionToggles.TryGetValue(d.Section, out var toggle)) { toggle.IsChecked = open; toggle.Visibility = collapsible ? Visibility.Visible : Visibility.Collapsed; }
            if (sectionDividers.TryGetValue(d.Section, out var line)) line.Visibility = NavigationRules.DividerVisible(collapsible, d.Section) ? Visibility.Visible : Visibility.Collapsed;
            if (sectionChevrons.TryGetValue(d.Section, out var chevron))
                chevron.Symbol = Controls.IconKeys.SymbolOf(NavigationRules.ChevronFor(open, model.Text.Direction));
        }
        if (HelpButton is not null) ApplyTips();
    }

    private void OnPendingChanged(string key, int count) => Dispatcher.BeginInvoke(() => ApplyPending(key));

    /// <summary>The mark on the navigation item: a spinner while the page runs, a warning after a failure, otherwise the dot with the count of staged changes.</summary>
    private void ApplyPending(string key)
    {
        if (!items.TryGetValue(key, out var item)) return;
        ApplyNavigationMark(key, item);
    }
    private static readonly DependencyProperty ContentControlContent = System.Windows.Controls.ContentControl.ContentProperty;

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Selected)) SelectItem(model.Selected.Key);
    }
    private void SelectItem(string key)
    {
        syncingSelection = true;
        try
        {
            foreach (var (k, item) in items) item.IsActive = k == key;
        }
        finally { syncingSelection = false; }
    }

    /// <summary>Below this window width the title bar drops the version and the word in the Administrator badge (the shield stays), so the search box never covers them.</summary>
    public const double TitleDetailsMinWidth = 1000;
    private void UpdateTitleDetails()
    {
        var details = ActualWidth >= TitleDetailsMinWidth ? Visibility.Visible : Visibility.Collapsed;
        TitleVersion.Visibility = details;
        AdminBadgeText.Visibility = details;
    }

    // Wide: open pane with labels. Narrower: compact icon-only pane. Below the minimum width: collapsed behind the toggle.
    private void UpdatePaneMode()
    {
        // Text scaling shrinks the room in DIPs: judge the pane by the width the scaled content really has.
        var pane = NavigationRules.PaneFor(NavigationRules.EffectiveWidth(ActualWidth, contentScale));
        RootNavigation.PaneDisplayMode = pane.Minimal ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.Left;
        if (pane.Open is { } open) RootNavigation.IsPaneOpen = open;
        ApplySections();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AttachNavigationFade);
    }

    /// <summary>The scroll viewer around the navigation items (its scroll bar shows only under the mouse); null before the template is applied.</summary>
    public System.Windows.Controls.ScrollViewer? NavigationScroll =>
        RootNavigation.Template?.FindName("PART_MenuItemsItemsControl", RootNavigation) is DependencyObject items ? Ancestor<System.Windows.Controls.ScrollViewer>(items) : null;

    private void AttachNavigationFade()
    {
        if (NavigationScroll is { } scroll) ScrollEdgeFade.Attach(scroll);
    }

    private static T? Ancestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var d = VisualTreeHelper.GetParent(start); d is not null; d = VisualTreeHelper.GetParent(d)) if (d is T found) return found;
        return null;
    }

    /// <summary>The current content scale (1.0 to 2.25).</summary>
    public double ContentScale => contentScale;

    /// <summary>
    /// Scales the whole content area (navigation and pages) so text follows the Windows "Text size" setting. It is a layout zoom, not a font-only
    /// change: everything stays proportional and reflows into the room that is left.
    /// </summary>
    public void ApplyContentScale(double factor)
    {
        contentScale = Math.Clamp(factor, TextScale.Minimum, TextScale.Maximum);
        RootNavigation.LayoutTransform = contentScale == 1 ? Transform.Identity : new ScaleTransform(contentScale, contentScale);
        FirstRunHost.LayoutTransform = contentScale == 1 ? Transform.Identity : new ScaleTransform(contentScale, contentScale);
        ActionCenter.LayoutTransform = contentScale == 1 ? Transform.Identity : new ScaleTransform(contentScale, contentScale);
        ActionCenterRail.LayoutTransform = ActionCenter.LayoutTransform;
        UpdatePaneMode();
        UpdateActionCenterWidth();
    }
    private void OnTextScaleChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => ApplyContentScale(textScale.Factor));
    // Digits, shaping and culture-aware formatting follow the selected language.
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => ApplyLanguage();
    private void ApplyLanguage()
    {
        Language = XmlLanguage.GetLanguage(model.Text.Culture.IetfLanguageTag);
        foreach (var d in model.Destinations) if (model.PendingFor(d.Key) > 0) ApplyPending(d.Key);
        ApplySections();
        if (items.Count > 0 && HelpButton is not null) ApplyTips();
    }

    public Task<bool> ConfirmAsync(ReviewSummary summary) => OnUi(() => ShowConfirmAsync(ConfirmDialogModel.From(summary, model.Text)));

    public Task<bool> ConfirmChangesAsync(ChangeReview review) => OnUi(() => ShowConfirmAsync(ConfirmDialogModel.From(review, model.Text)));

    /// <summary>
    /// The consent window for an administrator-level change. The default answer is Cancel, there is no timer, and Continue stays disabled until the second confirmation
    /// is ticked when Windows will not prompt. Nothing in the app calls this yet: the release set holds no machine definition.
    /// </summary>
    public Task<Winora.Application.ElevationConfirmation?> ConfirmMachineChangeAsync(MachineConsentRequest request) => OnUi(async () =>
    {
        var vm = new MachineConsentModel(request, model.Text);
        var result = await ShowDialogAsync(() =>
        {
            var dialog = new ContentDialog(DialogHost) { Content = new MachineConsentContent { DataContext = vm }, Title = vm.Title, PrimaryButtonText = vm.PrimaryText, CloseButtonText = vm.CancelText };
            dialog.DefaultButton = ContentDialogButton.Close;
            dialog.IsPrimaryButtonEnabled = vm.CanConfirm;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MachineConsentModel.CanConfirm)) dialog.IsPrimaryButtonEnabled = vm.CanConfirm; };
            return dialog;
        });
        return result == ContentDialogResult.Primary && vm.CanConfirm ? vm.ToConfirmation() : null;
    });

    private Task<bool> ConfirmSimpleAsync(string title, string body, string primary) =>
        OnUi(() => ShowConfirmAsync(ConfirmDialogModel.Simple(title, body, primary, model.Text)));

    public Task<bool> ConfirmExplorerRestartAsync() =>
        ConfirmSimpleAsync(model.Text["SetRestartDialogTitle"], model.Text["SetRestartWarning"], model.Text["SetRestartNowButton"]);

    public Task<bool> ConfirmSignOutAsync() =>
        ConfirmSimpleAsync(model.Text["SetSignOutDialogTitle"], model.Text["SetSignOutDialogBody"], model.Text["SetSignOutNow"]);

    /// <summary>The default answer is Back. The dialog lists what will change, the risk, whether it can be undone and whether a restart is needed.</summary>
    public Task<bool> ConfirmActionAsync(ActionConfirmation confirmation) => OnUi(() => ShowConfirmAsync(ConfirmDialogModel.From(confirmation, model.Text)));

    /// <summary>The one details dialog: the item name as title, cards inside that scroll when long, and one OK button across the bottom.</summary>
    public Task ShowDetailsAsync(DetailsDialog details) => OnUi(() =>
    {
        var vm = new DetailsDialogModel(details);
        var content = new Controls.DetailsDialogContent { DataContext = vm };
        return ShowDialogAsync(() =>
        {
            var dialog = new ContentDialog(DialogHost) { Title = details.Title, Content = content, CloseButtonText = details.CloseLabel, DefaultButton = ContentDialogButton.Close };
            vm.RequestClose = () => dialog.Hide(ContentDialogResult.None);
            return dialog;
        });
    });

    /// <summary>
    /// The restore point question: create one and continue, continue without, or cancel. The default answer is Cancel. When a restore point cannot be made now the first button is left out and the
    /// text says why.
    /// </summary>
    public Task<RestorePointChoice> AskAsync(RestorePointAsk ask, CancellationToken cancellationToken) => OnUi(() => AskCoreAsync(ask));

    private async Task<RestorePointChoice> AskCoreAsync(RestorePointAsk ask)
    {
        var dialog = RestorePointText.Dialog(model.Text, ask);
        var panel = new System.Windows.Controls.StackPanel { MinWidth = 520, MaxWidth = 560 };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = dialog.Body, Style = (Style)FindResource("WinoraBody"), TextWrapping = TextWrapping.Wrap });
        foreach (var note in dialog.Notes)
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = note, Style = (Style)FindResource("WinoraCaption"), TextWrapping = TextWrapping.Wrap, Margin = this.Space("T8") });
        AutomationProperties.SetName(panel, dialog.Title);
        // Enter answers Cancel, as the summary says: nothing changes on a stray key press.
        var result = await ShowDialogAsync(() => new ContentDialog(DialogHost)
        {
            Title = dialog.Title, Content = panel, PrimaryButtonText = dialog.Primary ?? "", SecondaryButtonText = dialog.Secondary, CloseButtonText = dialog.Cancel,
            DefaultButton = ContentDialogButton.Close,
        });
        return result switch { ContentDialogResult.Primary => RestorePointChoice.CreateAndContinue, ContentDialogResult.Secondary => RestorePointChoice.ContinueWithout, _ => RestorePointChoice.Cancel };
    }

    private void OnBlockingRequested(WinoraError error) => Dispatcher.BeginInvoke(async () => await ShowBlockingAsync(error));

    /// <summary>Blocking dialog for critical failures. Only the stable code, reference id and version can be copied.</summary>
    public Task<bool> ShowBlockingAsync(WinoraError error) => OnUi(() => ShowBlockingCoreAsync(error));

    private async Task<bool> ShowBlockingCoreAsync(WinoraError error)
    {
        if (blockingShown) return false;
        blockingShown = true;
        try
        {
            var panel = new System.Windows.Controls.StackPanel { MinWidth = 360 };
            var body = new System.Windows.Controls.TextBlock { Text = errors.Message(error), Style = (Style)FindResource("WinoraBody") };
            var reference = new System.Windows.Controls.TextBlock { Text = errors.Reference(error), Style = (Style)FindResource("WinoraCaption"), Margin = this.Space("T12"), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetLiveSetting(panel, AutomationLiveSetting.Assertive);
            AutomationProperties.SetName(panel, model.Text["CriticalErrorTitle"]);
            panel.Children.Add(body); panel.Children.Add(reference);
            var result = await ShowDialogAsync(new SimpleContentDialogCreateOptions
            {
                Title = model.Text["CriticalErrorTitle"], Content = panel,
                PrimaryButtonText = model.Text["CopyDiagnostics"], CloseButtonText = model.Text["Close"],
            });
            if (result != ContentDialogResult.Primary) return false;
            return DiagnosticsWriter(ErrorPresenter.Diagnostics(error));
        }
        finally { blockingShown = false; }
    }

    /// <summary>Writes diagnostics text to the clipboard; false when the OS clipboard is held by another process. Replaceable so tests do not depend on the shared OS clipboard.</summary>
    public Func<string, bool> DiagnosticsWriter { get; set; } = static text => HostServices.Clipboard.TrySetText(text);

    /// <summary>Test seam: forces the high-contrast path without changing the real Windows setting. Null follows Windows.</summary>
    public bool? HighContrastOverride { get => theme.HighContrastOverride; set => theme.HighContrastOverride = value; }
    public void ReapplyTheme() => theme.Apply();

    private void OnShortcutsRequested(object? sender, EventArgs e) => _ = ShowShortcutsAsync();
    private void OnWelcomeRequested(object? sender, EventArgs e) => _ = ShowWelcomeAsync();

    /// <summary>When the guide closes the page behind it comes back and its heading takes the keyboard focus.</summary>
    private void OnFirstRunChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FirstRunViewModel.IsActive) && !model.FirstRun.IsActive) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusPageHeading());
    }

    /// <summary>Page transitions run only when Windows animations are on. The state is always conveyed by text too, never by motion alone.</summary>
    public void ApplyMotion(bool enabled) => RootNavigation.Transition = enabled ? Wpf.Ui.Animations.Transition.FadeInWithSlide : Wpf.Ui.Animations.Transition.None;

    private void OnThemeChanged(object? sender, EventArgs e) => theme.Apply();
    private void OnDensityChanged(object? sender, EventArgs e) => DensityResources.Apply(model.Settings.Density);
    private void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.WindowGlassColor) or null) Dispatcher.BeginInvoke(theme.Apply);
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or null) Dispatcher.BeginInvoke(() => ApplyMotion(MotionPolicy.AnimationsEnabled));
    }
}
