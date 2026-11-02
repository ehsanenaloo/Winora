// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Controls;
using System.Windows.Threading;

namespace Winora.Desktop.Controls;

/// <summary>
/// The right-hand pane of the window: staged changes of every page, the runs and the notifications. It only shows <see cref="ActionCenterViewModel"/>. The clock of the elapsed times runs only
/// while the pane is on screen and a run exists.
/// </summary>
public partial class ActionCenterPane : UserControl
{
    private DispatcherTimer? clock;

    public ActionCenterPane()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { UpdateClock(); RefreshKeys(); };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ActionCenterViewModel old) { old.Runs.CollectionChanged -= OnRunsChanged; old.PropertyChanged -= OnModelChanged; }
            if (e.NewValue is ActionCenterViewModel next) { next.Runs.CollectionChanged += OnRunsChanged; next.PropertyChanged += OnModelChanged; }
            UpdateClock(); RefreshKeys();
        };
        Loaded += (_, _) => RefreshKeys();
        // Discarding the last change hides the footer under the keyboard focus: hand the focus to the Changes tab instead of losing it.
        ChangesFooter.IsVisibleChanged += (_, e) => { if (e.NewValue is false && ChangesFooter.IsKeyboardFocusWithin) ChangesTab.Focus(); };
        Unloaded += (_, _) => StopClock();
    }

    private ActionCenterViewModel? Model => DataContext as ActionCenterViewModel;

    public TextBlock HeadingBlock => Heading;
    public TabControl TabsControl => Tabs;
    public System.Windows.Controls.Primitives.ButtonBase ReviewButtonControl => ReviewButton;

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActionCenterViewModel.ReviewText) or nameof(ActionCenterViewModel.DiscardText) or null or "") RefreshKeys();
    }

    /// <summary>Alt+letter for Review and Discard: each button gets the first Latin letter of its text that the other does not use. Right-to-left pages have none.</summary>
    private void RefreshKeys()
    {
        if (Model is null) return;
        AccessKeys.Assign(AccessKeys.Offered(this), [(ReviewButton, Model.ReviewText), (DiscardButton, Model.DiscardText)]);
    }

    private void OnRunsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateClock();

    private void UpdateClock()
    {
        if (Model is { Runs.Count: > 0 } && IsVisible) StartClock(); else StopClock();
    }

    private void StartClock()
    {
        if (clock is { IsEnabled: true }) return;
        Model?.Tick();
        clock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => Model?.Tick();
        clock.Start();
    }

    private void StopClock() { clock?.Stop(); clock = null; }

    /// <summary>True while the one-second clock of the elapsed times runs; for tests.</summary>
    public bool ClockRunning => clock is { IsEnabled: true };
}
