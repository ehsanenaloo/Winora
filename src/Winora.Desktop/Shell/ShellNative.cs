// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace Winora.Desktop.Shell;

/// <summary>
/// Every Windows call of the taskbar button and the notification area icon, in one reviewed file. The calls only draw: progress, a badge, an icon, a menu and a notice.
/// Nothing here starts a process, reads or writes the system, or opens a channel to another process. Explorer runs without administrator rights, so the window messages it
/// sends back (icon events, "taskbar created", "taskbar button created") are let through to the elevated window one by one with ChangeWindowMessageFilterEx.
/// </summary>
internal static class ShellNative
{
    // ---- ITaskbarList3 (shobjidl_core.h). The order of the methods is the order of the vtable. ----

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr tab, IntPtr main);
        void UnregisterTab(IntPtr tab);
        void SetTabOrder(IntPtr tab, IntPtr insertBefore);
        void SetTabActive(IntPtr tab, IntPtr main, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass { }

    internal static ITaskbarList3 CreateTaskbarList()
    {
        var list = (ITaskbarList3)new TaskbarListClass();
        list.HrInit();
        return list;
    }

    internal const int TbpfNoProgress = 0, TbpfIndeterminate = 1, TbpfNormal = 2, TbpfError = 4, TbpfPaused = 8;

    // ---- Shell_NotifyIcon ----

    internal const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    internal const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    internal const uint NiifInfo = 0x1, NiifWarning = 0x2, NiifError = 0x3, NiifRespectQuietTime = 0x80;
    internal const uint NotifyIconVersion4 = 4;
    internal const int NinSelect = 0x400, NinKeySelect = 0x401, NinBalloonHide = 0x403, NinBalloonTimeout = 0x404, NinBalloonUserClick = 0x405;
    internal const int WmContextMenu = 0x7B, WmApp = 0x8000, WmNull = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    // ---- window messages ----

    internal const uint MsgfltAllow = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    internal static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int message, uint action, IntPtr changeInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    internal const int SmCxSmIcon = 49;

    // ---- the tray menu ----

    internal const uint MfString = 0x0, MfGrayed = 0x1, MfSeparator = 0x800;
    internal const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100, TpmLayoutRtl = 0x8000, TpmRightAlign = 0x8, TpmBottomAlign = 0x20;

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr menu);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    // ---- icons from pixels ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[]? bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr icon);

    /// <summary>An icon handle from 32-bit BGRA pixels with straight alpha. The caller destroys it with <see cref="DestroyIcon"/>. Zero when Windows refuses.</summary>
    internal static IntPtr IconFromPixels(int size, byte[] bgra)
    {
        var mask = CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);
        var color = CreateBitmap(size, size, 1, 32, bgra);
        try
        {
            if (mask == IntPtr.Zero || color == IntPtr.Zero) return IntPtr.Zero;
            var info = new IconInfo { fIcon = true, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            if (mask != IntPtr.Zero) DeleteObject(mask);
            if (color != IntPtr.Zero) DeleteObject(color);
        }
    }
}
