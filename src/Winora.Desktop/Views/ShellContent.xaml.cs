// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
namespace Winora.Desktop;
public partial class ShellContent : UserControl
{
    public ShellContent()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ShellViewModel old) old.PropertyChanged -= OnShellChanged;
            if (e.NewValue is ShellViewModel next) next.PropertyChanged += OnShellChanged;
        };
    }

    /// <summary>A new page fades in and moves up 8 px. Motion does nothing when Windows animations are off.</summary>
    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.CurrentPage)) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            Motion.FadeIn(PageHost);
            Motion.SlideIn(PageHost, (double)FindResource("WinoraPageEntranceOffset"));
        });
    }

    /// <summary>The status strip, for tests.</summary>
    public Controls.ActionCenterStrip StatusStrip => Strip;

    private async void OnOutcomeAction(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell) await shell.RunOutcomeActionAsync();
    }
    private async void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell) await shell.SettingsModel.RunNoticeActionAsync(secondary: false);
    }
    private async void OnNoticeSecondary(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell) await shell.SettingsModel.RunNoticeActionAsync(secondary: true);
    }
}
