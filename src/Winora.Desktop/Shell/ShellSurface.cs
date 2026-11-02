// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Winora.Application.Activity;

namespace Winora.Desktop.Shell;

/// <summary>What the window shows outside itself: the taskbar button and the notification area. Tests use a fake; the app uses <see cref="NativeShellSurface"/>.</summary>
public interface IShellSurface : IDisposable
{
    void SetProgress(TaskbarProgressKind kind, double value);
    void SetBadge(TaskbarBadge badge, string description);
    void SetTray(bool visible, string toolTip);
    /// <summary>Shows the notice from the notification area icon (the icon is added for it when needed).</summary>
    void ShowNotice(FinishNotice notice);
    /// <summary>The badge colors follow the theme; the window calls this when the theme or high contrast changes.</summary>
    void RefreshLook();
    Func<IReadOnlyList<TrayMenuEntry>>? Menu { get; set; }
    event EventHandler? OpenRequested;
    event EventHandler<TrayCommand>? CommandChosen;
    /// <summary>True when the person clicked the notice, false when it timed out or was closed.</summary>
    event EventHandler<bool>? NoticeClosed;
}

/// <summary>
/// The real taskbar button (progress and badge through ITaskbarList3, which takes a screen reader description for the badge) and the real notification area icon
/// (Shell_NotifyIcon, version 4, a native menu and the notice as a balloon). All Windows calls are in <see cref="ShellNative"/>.
/// </summary>
public sealed class NativeShellSurface : IShellSurface
{
    private const uint IconId = 1;
    private static readonly TimeSpan NoticeFallback = TimeSpan.FromSeconds(15);

    private readonly Window window;
    private readonly Func<bool> rightToLeft;
    private readonly int callbackMessage = ShellNative.WmApp + 1;
    private readonly int taskbarCreated = ShellNative.RegisterWindowMessage("TaskbarCreated");
    private readonly int buttonCreated = ShellNative.RegisterWindowMessage("TaskbarButtonCreated");

    private ShellNative.ITaskbarList3? taskbar;
    private IntPtr windowHandle;
    private TaskbarProgressKind kind;
    private double value;
    private TaskbarBadge badge;
    private string badgeDescription = "";
    private IntPtr badgeIcon;

    private HwndSource? trayWindow;
    private IntPtr trayIcon;
    private bool trayAdded, trayWanted;
    private string toolTip = "";
    private bool noticeOpen;
    private DispatcherTimer? noticeTimer;

    public NativeShellSurface(Window window, Func<bool> rightToLeft)
    {
        this.window = window; this.rightToLeft = rightToLeft;
        if (new WindowInteropHelper(window).Handle is var handle && handle != IntPtr.Zero) Attach(handle);
        else window.SourceInitialized += (_, _) => Attach(new WindowInteropHelper(window).Handle);
    }

    public Func<IReadOnlyList<TrayMenuEntry>>? Menu { get; set; }
    public event EventHandler? OpenRequested;
    public event EventHandler<TrayCommand>? CommandChosen;
    public event EventHandler<bool>? NoticeClosed;

    private void Attach(IntPtr handle)
    {
        windowHandle = handle;
        ShellNative.ChangeWindowMessageFilterEx(handle, buttonCreated, ShellNative.MsgfltAllow, IntPtr.Zero);
        HwndSource.FromHwnd(handle)?.AddHook(WindowHook);
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Explorer made the button again (first show, or after Explorer restarted): progress and badge must be sent again.
        if (msg == buttonCreated) { ApplyProgress(); ApplyBadge(); }
        return IntPtr.Zero;
    }

    private ShellNative.ITaskbarList3? Taskbar()
    {
        if (taskbar is not null || windowHandle == IntPtr.Zero) return taskbar;
        try { taskbar = ShellNative.CreateTaskbarList(); }
        catch (Exception e) when (e is COMException or InvalidCastException) { taskbar = null; }
        return taskbar;
    }

    public void SetProgress(TaskbarProgressKind kind, double value)
    {
        if (this.kind == kind && Math.Abs(this.value - value) < 0.001) return;
        this.kind = kind; this.value = value;
        ApplyProgress();
    }

    private void ApplyProgress()
    {
        if (Taskbar() is not { } list) return;
        try
        {
            list.SetProgressState(windowHandle, kind switch
            {
                TaskbarProgressKind.Normal => ShellNative.TbpfNormal,
                TaskbarProgressKind.Paused => ShellNative.TbpfPaused,
                TaskbarProgressKind.Error => ShellNative.TbpfError,
                TaskbarProgressKind.Indeterminate => ShellNative.TbpfIndeterminate,
                _ => ShellNative.TbpfNoProgress,
            });
            if (kind is TaskbarProgressKind.Normal or TaskbarProgressKind.Paused or TaskbarProgressKind.Error)
                list.SetProgressValue(windowHandle, (ulong)Math.Round(Math.Clamp(value, 0, 1) * 1000), 1000);
        }
        catch (COMException) { } // the button does not exist yet; TaskbarButtonCreated sends it again
    }

    public void SetBadge(TaskbarBadge badge, string description)
    {
        if (this.badge == badge && badgeDescription == description) return;
        this.badge = badge; badgeDescription = description;
        ReplaceBadgeIcon();
        ApplyBadge();
    }

    public void RefreshLook()
    {
        if (badge == TaskbarBadge.None) return;
        ReplaceBadgeIcon();
        ApplyBadge();
    }

    private void ReplaceBadgeIcon()
    {
        if (badgeIcon != IntPtr.Zero) { ShellNative.DestroyIcon(badgeIcon); badgeIcon = IntPtr.Zero; }
        if (badge != TaskbarBadge.None) badgeIcon = ShellIcons.Badge(window, badge, SmallIconSize());
    }

    private void ApplyBadge()
    {
        if (Taskbar() is not { } list) return;
        try { list.SetOverlayIcon(windowHandle, badgeIcon, badge == TaskbarBadge.None ? null : badgeDescription); }
        catch (COMException) { }
    }

    private static int SmallIconSize() => ShellNative.GetSystemMetrics(ShellNative.SmCxSmIcon) is var s && s > 0 ? s : 16;

    // ---- notification area ----

    public void SetTray(bool visible, string toolTip)
    {
        trayWanted = visible;
        var changedTip = this.toolTip != toolTip;
        this.toolTip = toolTip;
        if (visible && !trayAdded) AddIcon();
        else if (visible && changedTip) Modify(ShellNative.NifTip | ShellNative.NifShowTip);
        else if (!visible && trayAdded && !noticeOpen) RemoveIcon();
    }

    private ShellNative.NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<ShellNative.NotifyIconData>(),
        hWnd = trayWindow?.Handle ?? IntPtr.Zero,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = (uint)callbackMessage,
        hIcon = trayIcon,
        szTip = Clip(toolTip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private void AddIcon()
    {
        if (trayWindow is null)
        {
            trayWindow = new HwndSource(new HwndSourceParameters("WinoraNotificationArea") { Width = 0, Height = 0, WindowStyle = 0 });
            trayWindow.AddHook(TrayHook);
            ShellNative.ChangeWindowMessageFilterEx(trayWindow.Handle, callbackMessage, ShellNative.MsgfltAllow, IntPtr.Zero);
            ShellNative.ChangeWindowMessageFilterEx(trayWindow.Handle, taskbarCreated, ShellNative.MsgfltAllow, IntPtr.Zero);
        }
        if (trayIcon == IntPtr.Zero) trayIcon = ShellIcons.App(SmallIconSize());
        var data = Data(ShellNative.NifMessage | ShellNative.NifIcon | ShellNative.NifTip | ShellNative.NifShowTip);
        trayAdded = ShellNative.ShellNotifyIcon(ShellNative.NimAdd, ref data);
        if (!trayAdded) return;
        data.uVersion = ShellNative.NotifyIconVersion4;
        ShellNative.ShellNotifyIcon(ShellNative.NimSetVersion, ref data);
    }

    private void Modify(uint flags)
    {
        if (!trayAdded) return;
        var data = Data(flags);
        ShellNative.ShellNotifyIcon(ShellNative.NimModify, ref data);
    }

    private void RemoveIcon()
    {
        if (!trayAdded) return;
        var data = Data(0);
        ShellNative.ShellNotifyIcon(ShellNative.NimDelete, ref data);
        trayAdded = false;
    }

    public void ShowNotice(FinishNotice notice)
    {
        if (!trayAdded) AddIcon();
        if (!trayAdded) { NoticeClosed?.Invoke(this, false); return; }
        var data = Data(ShellNative.NifInfo);
        data.szInfoTitle = Clip(notice.Title, 63);
        data.szInfo = Clip(notice.Text, 255);
        data.dwInfoFlags = (notice.Result switch
        {
            RunResult.Error => ShellNative.NiifError,
            RunResult.Warning => ShellNative.NiifWarning,
            _ => ShellNative.NiifInfo,
        }) | ShellNative.NiifRespectQuietTime;
        noticeOpen = ShellNative.ShellNotifyIcon(ShellNative.NimModify, ref data);
        if (!noticeOpen) { NoticeClosed?.Invoke(this, false); return; }
        // Windows sends a timeout or a click; this one-shot timer covers a notice that Windows dropped without a word (quiet hours, notifications off).
        noticeTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = NoticeFallback };
        noticeTimer.Tick -= OnNoticeTimer; noticeTimer.Tick += OnNoticeTimer;
        noticeTimer.Start();
    }

    private void OnNoticeTimer(object? sender, EventArgs e) => EndNotice(false);

    private void EndNotice(bool clicked)
    {
        noticeTimer?.Stop();
        if (!noticeOpen) return;
        noticeOpen = false;
        if (!trayWanted) RemoveIcon();
        NoticeClosed?.Invoke(this, clicked);
    }

    private IntPtr TrayHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == taskbarCreated)
        {
            // Explorer restarted: the icon is gone and must be added again.
            trayAdded = false;
            if (trayWanted) AddIcon();
            return IntPtr.Zero;
        }
        if (msg != callbackMessage) return IntPtr.Zero;
        handled = true;
        switch ((int)((long)lParam & 0xFFFF))
        {
            case ShellNative.NinSelect or ShellNative.NinKeySelect: OpenRequested?.Invoke(this, EventArgs.Empty); break;
            case ShellNative.WmContextMenu: ShowMenu((short)((long)wParam & 0xFFFF), (short)(((long)wParam >> 16) & 0xFFFF)); break;
            case ShellNative.NinBalloonUserClick: EndNotice(true); break;
            case ShellNative.NinBalloonTimeout or ShellNative.NinBalloonHide: EndNotice(false); break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        if (trayWindow is null || Menu?.Invoke() is not { Count: > 0 } entries) return;
        var menu = ShellNative.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            foreach (var entry in entries)
            {
                if (entry.Command == TrayCommand.Separator) { ShellNative.AppendMenu(menu, ShellNative.MfSeparator, UIntPtr.Zero, null); continue; }
                // A run title is plain text: an ampersand in it must not become a keyboard mnemonic.
                var label = entry.Command == TrayCommand.RunInfo ? entry.Text.Replace("&", "&&", StringComparison.Ordinal) : entry.Text;
                ShellNative.AppendMenu(menu, ShellNative.MfString | (entry.Enabled ? 0 : ShellNative.MfGrayed), (UIntPtr)((uint)entry.Command + 1), label);
            }
            // The menu closes when the person clicks elsewhere only if its owner window is in front.
            ShellNative.SetForegroundWindow(trayWindow.Handle);
            var flags = ShellNative.TpmReturnCmd | ShellNative.TpmRightButton | ShellNative.TpmBottomAlign | (rightToLeft() ? ShellNative.TpmLayoutRtl | ShellNative.TpmRightAlign : 0);
            var chosen = ShellNative.TrackPopupMenuEx(menu, flags, x, y, trayWindow.Handle, IntPtr.Zero);
            ShellNative.PostMessage(trayWindow.Handle, ShellNative.WmNull, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0) CommandChosen?.Invoke(this, (TrayCommand)(chosen - 1));
        }
        finally { ShellNative.DestroyMenu(menu); }
    }

    public void Dispose()
    {
        noticeTimer?.Stop();
        noticeOpen = false;
        RemoveIcon();
        trayWindow?.Dispose(); trayWindow = null;
        if (trayIcon != IntPtr.Zero) { ShellNative.DestroyIcon(trayIcon); trayIcon = IntPtr.Zero; }
        if (windowHandle != IntPtr.Zero && taskbar is not null)
        {
            try { taskbar.SetProgressState(windowHandle, ShellNative.TbpfNoProgress); taskbar.SetOverlayIcon(windowHandle, IntPtr.Zero, null); }
            catch (COMException) { }
        }
        if (badgeIcon != IntPtr.Zero) { ShellNative.DestroyIcon(badgeIcon); badgeIcon = IntPtr.Zero; }
        if (taskbar is not null) { Marshal.ReleaseComObject(taskbar); taskbar = null; }
    }
}

/// <summary>The pictures of the badges and of the notification area icon, drawn for the current theme. Light and dark use the status colors; high contrast uses the system highlight colors.</summary>
public static class ShellIcons
{
    private static readonly Uri AppIconUri = new("pack://application:,,,/Winora.Desktop;component/Assets/Winora.ico", UriKind.Absolute);

    /// <summary>The badge as BGRA pixels with straight alpha, <paramref name="size"/> by <paramref name="size"/>.</summary>
    public static byte[] BadgePixels(FrameworkElement theme, TaskbarBadge badge, int size)
    {
        var (fill, glyph) = BadgeColors(theme, badge);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 16.0, size / 16.0));
            dc.DrawEllipse(fill, new Pen(glyph, 1), new Point(8, 8), 7.5, 7.5);
            var pen = new Pen(glyph, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (badge == TaskbarBadge.Failed)
            {
                dc.DrawLine(pen, new Point(8, 4.5), new Point(8, 9));
                dc.DrawEllipse(glyph, null, new Point(8, 11.75), 1.25, 1.25);
            }
            else
            {
                // A circular arrow: three quarters of a circle and a short head at its start.
                var arc = new PathFigure { StartPoint = new Point(10.8, 5.2) };
                arc.Segments.Add(new ArcSegment(new Point(5.2, 5.2), new Size(4, 4), 0, true, SweepDirection.Clockwise, true));
                dc.DrawGeometry(null, new Pen(glyph, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new PathGeometry([arc]));
                dc.DrawLine(pen, new Point(10.8, 5.2), new Point(11.2, 2.8));
                dc.DrawLine(pen, new Point(10.8, 5.2), new Point(8.4, 5.4));
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Straight(bitmap, size);
    }

    internal static IntPtr Badge(FrameworkElement theme, TaskbarBadge badge, int size) => ShellNative.IconFromPixels(size, BadgePixels(theme, badge, size));

    /// <summary>The Winora icon at the size closest to <paramref name="size"/>, as BGRA pixels.</summary>
    public static byte[] AppPixels(int size)
    {
        var decoder = BitmapDecoder.Create(AppIconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).ThenByDescending(f => f.Format.BitsPerPixel).First();
        if (frame.PixelWidth != size) frame = new TransformedBitmap(frame, new ScaleTransform((double)size / frame.PixelWidth, (double)size / frame.PixelHeight));
        return Straight(frame, size);
    }

    internal static IntPtr App(int size) => ShellNative.IconFromPixels(size, AppPixels(size));

    private static byte[] Straight(BitmapSource source, int size)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        converted.CopyPixels(new Int32Rect(0, 0, Math.Min(size, converted.PixelWidth), Math.Min(size, converted.PixelHeight)), pixels, size * 4, 0);
        return pixels;
    }

    private static (Brush Fill, Brush Glyph) BadgeColors(FrameworkElement theme, TaskbarBadge badge)
    {
        if (SystemParameters.HighContrast) return (SystemColors.HighlightBrush, SystemColors.HighlightTextBrush);
        var fill = theme.TryFindResource(badge == TaskbarBadge.Failed ? "WinoraStatusErrorForeground" : "WinoraStatusInfoForeground") as Brush ?? SystemColors.HighlightBrush;
        var glyph = theme.TryFindResource("WinoraSurfaceCardBrush") as Brush ?? SystemColors.WindowBrush;
        return (fill, glyph);
    }
}
