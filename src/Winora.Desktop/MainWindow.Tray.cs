// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Winora.Desktop.Shell;

namespace Winora.Desktop;

/// <summary>
/// The window side of taskbar progress, the taskbar badge, the finish notice and the notification area icon. The state is in <see cref="TaskbarTrayModel"/>, which reads only the
/// activity service. The application turns this on; a window made by a test has none of it unless the test passes a fake surface.
/// </summary>
public partial class MainWindow
{
    private TaskbarTrayModel? trayModel;
    private IShellSurface? surface;
    private DispatcherTimer? longRunTimer;
    private bool exitForReal;
    private string? noticePage;

    public TaskbarTrayModel? TaskbarTray => trayModel;

    /// <summary>True only while a run is going that has not yet counted as long. Never true while Winora is idle.</summary>
    public bool LongRunTimerRunning => longRunTimer?.IsEnabled == true;

    /// <summary>Asks before Exit while a run goes: (title, text) to yes. Null uses a Windows message box (the window may be hidden). Tests replace it.</summary>
    public Func<string, string, bool>? AskExitDuringRun { get; set; }

    public void EnableShellIntegration(IShellSurface? custom = null)
    {
        if (trayModel is not null) return;
        surface = custom ?? new NativeShellSurface(this, () => model.Text.Direction == TextDirection.RightToLeft);
        var tray = trayModel = new TaskbarTrayModel(model.Text, model.Activity, () => model.Settings.KeepInTray, () => model.Settings.NotifyOnCompletion);
        surface.Menu = () => tray.Menu;
        tray.Changed += OnTrayChanged;
        tray.NoticeRequested += OnNoticeRequested;
        tray.ExitRequested += OnExitRequested;
        surface.OpenRequested += OnTrayOpen;
        surface.CommandChosen += OnTrayCommand;
        surface.NoticeClosed += OnNoticeClosed;
        Activated += OnPresenceChanged; Deactivated += OnPresenceChanged; StateChanged += OnPresenceChanged; IsVisibleChanged += OnVisibilityChanged;
        model.Settings.ThemeChanged += OnTrayLook; model.Settings.AccentChanged += OnTrayLook; SystemParameters.StaticPropertyChanged += OnTraySystemChanged;
        Closed += (_, _) => DisableShellIntegration();
        tray.SetPresence(CurrentPresence());
    }

    private void DisableShellIntegration()
    {
        longRunTimer?.Stop();
        Activated -= OnPresenceChanged; Deactivated -= OnPresenceChanged; StateChanged -= OnPresenceChanged; IsVisibleChanged -= OnVisibilityChanged;
        model.Settings.ThemeChanged -= OnTrayLook; model.Settings.AccentChanged -= OnTrayLook; SystemParameters.StaticPropertyChanged -= OnTraySystemChanged;
        if (trayModel is { } tray)
        {
            tray.Changed -= OnTrayChanged; tray.NoticeRequested -= OnNoticeRequested; tray.ExitRequested -= OnExitRequested;
            tray.Dispose();
        }
        if (surface is { } s)
        {
            s.OpenRequested -= OnTrayOpen; s.CommandChosen -= OnTrayCommand; s.NoticeClosed -= OnNoticeClosed;
            s.Dispose();
        }
        trayModel = null; surface = null;
    }

    private WindowPresence CurrentPresence() =>
        !IsVisible ? WindowPresence.Hidden : WindowState == WindowState.Minimized ? WindowPresence.Minimized : IsActive ? WindowPresence.Front : WindowPresence.Behind;

    private void OnPresenceChanged(object? sender, EventArgs e) => trayModel?.SetPresence(CurrentPresence());

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Back from the notification area: staged changes are asked about again at the next real close.
        if (IsVisible && !exitForReal) closeConfirmed = false;
        trayModel?.SetPresence(CurrentPresence());
    }

    private void OnTrayLook(object? sender, EventArgs e) => surface?.RefreshLook();

    private void OnTraySystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(() => surface?.RefreshLook());
    }

    private void OnTrayChanged(object? sender, EventArgs e)
    {
        if (trayModel is not { } tray || surface is null) return;
        surface.SetProgress(tray.ProgressKind, tray.ProgressValue);
        surface.SetBadge(tray.Badge, tray.BadgeDescription);
        surface.SetTray(tray.TrayVisible, tray.TrayToolTip);
        // One check when the next run turns long; no timer at all while nothing runs.
        if (tray.NextCheck is { } wait)
        {
            if (longRunTimer is null) { longRunTimer = new DispatcherTimer(DispatcherPriority.Background); longRunTimer.Tick += OnLongRunTick; }
            longRunTimer.Stop();
            longRunTimer.Interval = wait + TimeSpan.FromMilliseconds(50);
            longRunTimer.Start();
        }
        else longRunTimer?.Stop();
    }

    private void OnLongRunTick(object? sender, EventArgs e)
    {
        longRunTimer?.Stop();
        trayModel?.Refresh();
    }

    /// <summary>Hides the window to the notification area. The window stays loaded; nothing on the pages changes.</summary>
    private void HideToTray() => Hide();

    /// <summary>The window closes for real only when Exit was chosen, the setting is off and no run must go on, or the run it waited for is over.</summary>
    private bool ClosingHidesToTray() => trayModel is { } tray && !exitForReal && !IsModalOpen && tray.WindowClosing() == CloseOutcome.HideToTray;

    private void OnTrayOpen(object? sender, EventArgs e) => OpenFromTray(null);

    private void OnTrayCommand(object? sender, TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open: OpenFromTray(null); break;
            case TrayCommand.Stop: trayModel?.StopShown(); break;
            case TrayCommand.Exit: ExitFromTray(); break;
        }
    }

    /// <summary>Exit on the tray menu. While a run goes it asks first; then every run is asked to stop and the app ends when none is left.</summary>
    public void ExitFromTray()
    {
        if (trayModel is not { } tray || tray.ExitPending) return;
        if (tray.ExitNeedsConfirmation)
        {
            var name = tray.ShownRun?.Title ?? model.Activity.Runs.FirstOrDefault()?.Title ?? "";
            var title = model.Text["TrayExitAskTitle"];
            var body = model.Text.Format("TrayExitAskBody", name);
            if (!(AskExitDuringRun ?? AskWithMessageBox)(title, body)) return;
        }
        tray.ConfirmExit();
    }

    private bool AskWithMessageBox(string title, string body)
    {
        var options = model.Text.Direction == TextDirection.RightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
        return MessageBox.Show(body, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, options) == MessageBoxResult.Yes;
    }

    /// <summary>Open on the tray menu, a click on the icon or on the notice. A pending exit is called off: the person wants the window.</summary>
    public void OpenFromTray(string? pageKey)
    {
        exitForReal = false;
        trayModel?.CancelExit();
        ShowFromHandOver(pageKey is not null && model.Destinations.Any(d => d.Key == pageKey) ? pageKey : null);
    }

    private void OnNoticeRequested(object? sender, FinishNotice notice)
    {
        noticePage = notice.PageKey;
        surface?.ShowNotice(notice);
    }

    private void OnNoticeClosed(object? sender, bool clicked)
    {
        if (clicked) OpenFromTray(noticePage);
        noticePage = null;
        trayModel?.NoticeClosed(clicked);
    }

    private void OnExitRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ExitNow);

    private void ExitNow()
    {
        exitForReal = true;
        // Staged changes are asked about in the window, so it comes back for that question.
        if (AskBeforeClosingWithPending && !closeConfirmed && model.TotalPending > 0 && !IsVisible) { Show(); Activate(); }
        Close();
    }
}
