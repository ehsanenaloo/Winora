// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Winora.Desktop;

/// <summary>The UI thread of the WPF application, found each time it is asked for (a test host may create the application later than the first view model).</summary>
public sealed class WpfUiThread : IUiThread
{
    private static Dispatcher? Dispatcher => System.Windows.Application.Current?.Dispatcher;

    public bool IsAvailable => Dispatcher is not null;
    public bool CheckAccess() => Dispatcher?.CheckAccess() ?? true;
    public void Invoke(Action action)
    {
        if (Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.Invoke(action); else action();
    }
    public void PostBackground(Action action) => Dispatcher?.BeginInvoke(DispatcherPriority.Background, action);
}

/// <summary>Connects the view model layer to WPF when this assembly loads: the shell services the view models fall back to, and the effects of a language change.</summary>
internal static class WpfHost
{
    [ModuleInitializer]
    internal static void Connect()
    {
        HostServices.UiThread = new WpfUiThread();
        HostServices.Clipboard = new SystemClipboardService();
        HostServices.SystemAccent = static () => Controls.ColorLook.ToRgb(System.Windows.SystemParameters.WindowGlassColor);
        LocalizationService.LanguageApplied += Typography.Apply;
        SettingsViewModel.SmoothScrollingApplied += static enabled => Controls.SmoothScroll.Enabled = enabled;
    }
}
