// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;


namespace Winora.Desktop.Controls;

/// <summary>The rail's view of one tab of the <see cref="TabRail.ContentTabs"/> control: its header and name are read live from the tab.</summary>
public sealed class TabRailEntry(TabItem tab) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Never raised. It lets WPF bindings listen through a weak event; on a plain object they hold the object, and with it the window, for the life of the process.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public TabItem Tab { get; } = tab;
}

/// <summary>
/// The category tab rail of a page: one row of tabs that never wraps. A row wider than the page scrolls sideways with a previous and a next button that
/// exist only while there is something to scroll to; the selected tab has an accent underline and is scrolled into view; Left and Right (reading direction
/// aware), Home and End move between the tabs. It is a TabControl without a content area, so it is announced as a tab list. The rail either shows its own
/// items (<see cref="ItemsControl.ItemsSource"/>) or mirrors the tabs of another TabControl (<see cref="ContentTabs"/>), whose content area then sits in the
/// page body while the rail stays in the fixed header.
/// </summary>
public sealed class TabRail : TabControl
{
    static TabRail()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TabRail), new FrameworkPropertyMetadata(typeof(TabRail)));
    }

    public static readonly DependencyProperty ContentTabsProperty = DependencyProperty.Register(nameof(ContentTabs), typeof(TabControl), typeof(TabRail),
        new FrameworkPropertyMetadata(null, (d, e) => ((TabRail)d).OnContentTabs((TabControl?)e.OldValue, (TabControl?)e.NewValue)));
    /// <summary>
    /// The item that is shown as selected, for a rail that lists its own items. Bind it two-way to the page's selection. A TabControl selects its first tab whenever its
    /// items are replaced; that is not a choice of the person, so it never reaches this property: only a click, a key or an automation client changes it, and the rail takes
    /// the page's choice back after any other selection.
    /// </summary>
    public static readonly DependencyProperty ActiveItemProperty = DependencyProperty.Register(nameof(ActiveItem), typeof(object), typeof(TabRail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((TabRail)d).OnActiveItem(e.NewValue)));
    public static readonly DependencyProperty PreviousTextProperty = DependencyProperty.Register(nameof(PreviousText), typeof(string), typeof(TabRail), new FrameworkPropertyMetadata("Previous tabs"));
    public static readonly DependencyProperty NextTextProperty = DependencyProperty.Register(nameof(NextText), typeof(string), typeof(TabRail), new FrameworkPropertyMetadata("Next tabs"));
    private static readonly DependencyPropertyKey HasOverflowPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasOverflow), typeof(bool), typeof(TabRail), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasOverflowProperty = HasOverflowPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey CanScrollPreviousPropertyKey = DependencyProperty.RegisterReadOnly(nameof(CanScrollPrevious), typeof(bool), typeof(TabRail), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty CanScrollPreviousProperty = CanScrollPreviousPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey CanScrollNextPropertyKey = DependencyProperty.RegisterReadOnly(nameof(CanScrollNext), typeof(bool), typeof(TabRail), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty CanScrollNextProperty = CanScrollNextPropertyKey.DependencyProperty;

    /// <summary>Below this much hidden width the arrows stay hidden (sub-pixel rounding of the row).</summary>
    private const double Slack = 0.75;

    private readonly ObservableCollection<TabRailEntry> entries = [];
    private bool syncing, user;
    private ScrollViewer? scroll;
    private ButtonBase? previous, next;

    public TabRail()
    {
        Loaded += (_, _) => { LinkTexts(); UpdateArrows(); BringSelectedIntoView(); };
        Unloaded += (_, _) => UnlinkTexts();
        SizeChanged += (_, _) => { UpdateArrows(); BringSelectedIntoView(); };
        PreviewMouseWheel += OnWheel;
    }

    public object? ActiveItem { get => GetValue(ActiveItemProperty); set => SetValue(ActiveItemProperty, value); }
    /// <summary>The TabControl whose tabs this rail shows; its own header strip is not drawn.</summary>
    public TabControl? ContentTabs { get => (TabControl?)GetValue(ContentTabsProperty); set => SetValue(ContentTabsProperty, value); }
    /// <summary>Accessible name of the previous button.</summary>
    public string PreviousText { get => (string)GetValue(PreviousTextProperty); set => SetValue(PreviousTextProperty, value); }
    /// <summary>Accessible name of the next button.</summary>
    public string NextText { get => (string)GetValue(NextTextProperty); set => SetValue(NextTextProperty, value); }
    /// <summary>The tabs are wider than the rail: the previous and next buttons are shown.</summary>
    public bool HasOverflow => (bool)GetValue(HasOverflowProperty);
    public bool CanScrollPrevious => (bool)GetValue(CanScrollPreviousProperty);
    public bool CanScrollNext => (bool)GetValue(CanScrollNextProperty);
    public ScrollViewer? Scroller => scroll;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (scroll is not null) scroll.ScrollChanged -= OnScrollChanged;
        if (previous is not null) previous.Click -= OnPrevious;
        if (next is not null) next.Click -= OnNext;
        scroll = GetTemplateChild("PART_Scroll") as ScrollViewer;
        previous = GetTemplateChild("PART_Previous") as ButtonBase;
        next = GetTemplateChild("PART_Next") as ButtonBase;
        if (scroll is not null) scroll.ScrollChanged += OnScrollChanged;
        if (previous is not null) previous.Click += OnPrevious;
        if (next is not null) next.Click += OnNext;
    }

    // ---- Names of the two buttons, in the language of the window ----

    private LocalizationService? texts;

    private void LinkTexts()
    {
        UnlinkTexts();
        if (Window.GetWindow(this)?.DataContext is not ShellViewModel shell) return;
        texts = shell.Text;
        texts.PropertyChanged += OnTextsChanged;
        ApplyTexts();
    }

    private void UnlinkTexts()
    {
        if (texts is not null) texts.PropertyChanged -= OnTextsChanged;
        texts = null;
    }

    private void OnTextsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyTexts();

    private void ApplyTexts()
    {
        if (texts is null) return;
        SetCurrentValue(PreviousTextProperty, texts["TabRailPrevious"]);
        SetCurrentValue(NextTextProperty, texts["TabRailNext"]);
    }

    // ---- Mirroring another TabControl ----

    private void OnContentTabs(TabControl? old, TabControl? tabs)
    {
        if (old is not null)
        {
            ((INotifyCollectionChanged)old.Items).CollectionChanged -= OnContentItemsChanged;
            old.SelectionChanged -= OnContentSelectionChanged;
        }
        if (tabs is null) { ItemsSource = null; return; }
        ((INotifyCollectionChanged)tabs.Items).CollectionChanged += OnContentItemsChanged;
        tabs.SelectionChanged += OnContentSelectionChanged;
        ItemsSource = entries;
        Rebuild();
    }

    private void OnContentItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        if (ContentTabs is not { } tabs) return;
        syncing = true;
        try
        {
            entries.Clear();
            foreach (var tab in tabs.Items.OfType<TabItem>()) entries.Add(new TabRailEntry(tab));
            SelectedIndex = tabs.SelectedIndex < entries.Count ? tabs.SelectedIndex : -1;
        }
        finally { syncing = false; }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { UpdateArrows(); BringSelectedIntoView(); });
    }

    private void OnContentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ContentTabs) || syncing || ContentTabs is not { } tabs) return;
        if (tabs.SelectedIndex != SelectedIndex && tabs.SelectedIndex < entries.Count) SelectedIndex = tabs.SelectedIndex;
    }

    private void OnActiveItem(object? item)
    {
        if (ContentTabs is not null || item is null || ReferenceEquals(SelectedItem, item) || !Items.Contains(item)) return;
        syncing = true;
        try { SelectedItem = item; }
        finally { syncing = false; }
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        if (ContentTabs is null) OnActiveItem(ActiveItem);
    }

    // The selection of a choice of the person: the mouse, the keys and the automation clients mark it; everything else is the control selecting a tab by itself.
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        user = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => user = false);
        base.OnPreviewMouseLeftButtonDown(e);
    }

    private void Choose(Action select)
    {
        user = true;
        try { select(); }
        finally { user = false; }
    }

    internal void SelectFromAutomation(object? item) => Choose(() => SelectedItem = item);
    internal bool IsItemSelected(object? item) => Equals(SelectedItem, item);

    protected override AutomationPeer OnCreateAutomationPeer() => new TabRailPeer(this);

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (!ReferenceEquals(e.OriginalSource, this)) return;
        if (ContentTabs is null && !syncing)
        {
            if (user && SelectedItem is not null) { if (!Equals(ActiveItem, SelectedItem)) SetCurrentValue(ActiveItemProperty, SelectedItem); }
            else if (ActiveItem is not null && !Equals(SelectedItem, ActiveItem) && Items.Contains(ActiveItem)) OnActiveItem(ActiveItem);
        }
        if (!syncing && ContentTabs is { } tabs && SelectedIndex >= 0 && tabs.SelectedIndex != SelectedIndex)
        {
            syncing = true;
            try { tabs.SelectedIndex = SelectedIndex; }
            finally { syncing = false; }
        }
        BringSelectedIntoView();
    }

    // ---- Scrolling ----

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateArrows();

    private void UpdateArrows()
    {
        if (scroll is null) return;
        var overflow = scroll.ExtentWidth > ActualWidth + Slack;
        SetValue(HasOverflowPropertyKey, overflow);
        SetValue(CanScrollPreviousPropertyKey, overflow && scroll.HorizontalOffset > Slack);
        SetValue(CanScrollNextPropertyKey, overflow && scroll.HorizontalOffset < scroll.ScrollableWidth - Slack);
    }

    // The scroll offset counts from the start of the row, so these work the same in both reading directions.
    private void OnPrevious(object sender, RoutedEventArgs e) => ScrollBy(-1);
    private void OnNext(object sender, RoutedEventArgs e) => ScrollBy(1);

    private void ScrollBy(int direction)
    {
        if (scroll is null) return;
        scroll.ScrollToHorizontalOffset(Math.Clamp(scroll.HorizontalOffset + direction * Math.Max(80, scroll.ViewportWidth * 0.75), 0, scroll.ScrollableWidth));
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (scroll is null || !HasOverflow || e.Delta == 0) return;
        var direction = e.Delta < 0 ? 1 : -1;
        if (direction > 0 ? !CanScrollNext : !CanScrollPrevious) return;
        ScrollBy(direction);
        e.Handled = true;
    }

    /// <summary>Scrolls the row so that the selected tab is fully visible.</summary>
    public void BringSelectedIntoView()
    {
        if (!IsLoaded || SelectedIndex < 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (SelectedIndex >= 0 && ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is FrameworkElement tab) tab.BringIntoView();
        });
    }

    // ---- Keys ----

    // The keys are taken on the way down: the scroll viewer around the tabs would use the arrow keys to scroll the row first.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var rtl = FlowDirection == FlowDirection.RightToLeft;
        var step = e.Key switch { Key.Right => rtl ? -1 : 1, Key.Left => rtl ? 1 : -1, _ => 0 };
        if (step == 0 && e.Key is not (Key.Home or Key.End)) { base.OnPreviewKeyDown(e); return; }
        if (Keyboard.Modifiers != ModifierKeys.None) { base.OnPreviewKeyDown(e); return; }
        var count = Items.Count;
        if (count == 0) return;
        var from = SelectedIndex < 0 ? (step < 0 ? count : -1) : SelectedIndex;
        var target = e.Key switch
        {
            Key.Home => Seek(-1, 1),
            Key.End => Seek(count, -1),
            _ => Seek(from, step),
        };
        e.Handled = true;
        if (target < 0 || target == SelectedIndex) return;
        Choose(() => SelectedIndex = target);
        if (ItemContainerGenerator.ContainerFromIndex(target) is TabItem container) { container.Focus(); container.BringIntoView(); }
    }

    // The next tab from this index in this direction that can be used (shown and enabled); -1 when there is none.
    private int Seek(int from, int direction)
    {
        for (var i = from + direction; i >= 0 && i < Items.Count; i += direction)
            if (ItemContainerGenerator.ContainerFromIndex(i) is UIElement { IsEnabled: true, Visibility: Visibility.Visible }) return i;
        return -1;
    }
}

/// <summary>The tab list of a rail: a tab control whose tabs tell the rail when an automation client selects one, so a screen reader user can choose a tab of the page.</summary>
internal sealed class TabRailPeer(TabRail owner) : TabControlAutomationPeer(owner)
{
    protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => new TabRailItemPeer(item, this);
}

internal sealed class TabRailItemPeer(object item, TabRailPeer parent) : TabItemAutomationPeer(item, parent), ISelectionItemProvider
{
    private TabRail Rail => (TabRail)parent.Owner;

    void ISelectionItemProvider.Select() => Rail.SelectFromAutomation(Item);
    void ISelectionItemProvider.AddToSelection() => Rail.SelectFromAutomation(Item);
    void ISelectionItemProvider.RemoveFromSelection() { }
    bool ISelectionItemProvider.IsSelected => Rail.IsItemSelected(Item);
    IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => ProviderFromPeer(parent);
}
