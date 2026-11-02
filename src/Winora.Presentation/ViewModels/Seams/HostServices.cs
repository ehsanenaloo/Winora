// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>The UI thread, as far as the view models need it. The window project supplies the real one; a host without a window has none.</summary>
public interface IUiThread
{
    /// <summary>False when no window exists (a test host without one): <see cref="PostBackground"/> then does nothing.</summary>
    bool IsAvailable { get; }
    bool CheckAccess();
    /// <summary>Runs the action on the UI thread and waits for it.</summary>
    void Invoke(Action action);
    /// <summary>Queues the action on the UI thread at background priority and returns.</summary>
    void PostBackground(Action action);
}

/// <summary>
/// The shell services that a view model uses when its constructor was not given one. The window project replaces them when the assembly loads; the defaults do nothing and touch no system.
/// </summary>
public static class HostServices
{
    public static IUiThread UiThread { get; set; } = new NoUiThread();
    public static IClipboardService Clipboard { get; set; } = new NoClipboard();
    /// <summary>The accent color of Windows, for the preview of the "Windows" choice of the accent setting.</summary>
    public static Func<Rgb> SystemAccent { get; set; } = static () => Rgb.Gray;

    private sealed class NoUiThread : IUiThread
    {
        public bool IsAvailable => false;
        public bool CheckAccess() => true;
        public void Invoke(Action action) => action();
        public void PostBackground(Action action) { }
    }

    private sealed class NoClipboard : IClipboardService
    {
        public bool TrySetText(string text) => false;
    }
}
