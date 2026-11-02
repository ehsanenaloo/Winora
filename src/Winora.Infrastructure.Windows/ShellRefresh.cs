// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Winora.Application;
namespace Winora.Infrastructure.Windows;

/// <summary>OS calls used by the shell refresh. Internal so tests substitute a fake and never touch the real shell.</summary>
internal interface IShellOs
{
    /// <summary>Broadcasts WM_SETTINGCHANGE with the given fixed lParam string (null for none). True when the broadcast was delivered.</summary>
    bool BroadcastSettingChange(string? parameter, int timeoutMs);
    /// <summary>Process ids of explorer.exe from %SystemRoot% owned by the current user in the current session. Never other sessions or users.</summary>
    IReadOnlyList<int> FindShellProcesses();
    /// <summary>Posts the clean exit-Explorer message to the taskbar window. False when there is no taskbar window.</summary>
    bool PostExitExplorer();
    /// <summary>Waits for the process to exit; true if it has exited.</summary>
    bool WaitForExit(int processId, int timeoutMs);
    /// <summary>Last resort, graceful exit timed out: terminate one matched shell process.</summary>
    bool KillMatchedShell(int processId);
    /// <summary>Starts %SystemRoot%\explorer.exe with no arguments.</summary>
    bool StartExplorer();
    bool TaskbarExists();
    /// <summary>Applies one fixed group of user setting values that the registry already holds. True when every call succeeded.</summary>
    bool ApplyUserSetting(UserSettingApply action);
}

/// <summary>The fixed groups of user settings that can be applied without a sign-out. Owner approval pending.</summary>
internal enum UserSettingApply { Mouse, KeyboardRepeat, AccessibilityKeys }

internal sealed record ShellTimings(int BroadcastTimeoutMs = 2000, int ExitWaitMs = 10_000, int KillWaitMs = 5000, int RelaunchGraceMs = 4000,
    int TaskbarWaitMs = 15_000, int PollMs = 250);

/// <summary>
/// The only code allowed to message windows, call SystemParametersInfo or start/stop a process (see the source scan). A closed list of fixed actions; nothing here accepts a
/// path, command, process id, message or number from callers. The SystemParametersInfo actions read the values from the user profile and apply them as they are. Cancellation is the only exception thrown; all other failures return a code.
/// </summary>
public sealed class WindowsShellRefresh : IShellRefresh
{
    public const string BroadcastDone = "operation.shell_refreshed";
    public const string BroadcastFailed = "shell.broadcast_failed";
    public const string RestartDone = "operation.shell_refreshed";
    public const string RestartFailed = "shell.restart_failed";

    // Closed list: ImmersiveColorSet is the documented notification for light/dark app and system theme changes; null re-reads general per-user settings.
    // The order matters. Measured on Windows 11 build 26200: with the general broadcast first, the taskbar ignored the following ImmersiveColorSet and showed
    // the previous colour state one change late (a manual restart of Explorer fixed it). With ImmersiveColorSet first the taskbar followed at once.
    private static readonly string?[] BroadcastParameters = ["ImmersiveColorSet", null];

    private readonly IShellOs os;
    private readonly ShellTimings timings;
    private readonly bool elevated;

    [SupportedOSPlatform("windows")]
    public WindowsShellRefresh() : this(new WindowsShellOs(), new ShellTimings(), ProcessElevationState.TokenIsElevated) { }

    /// <param name="elevated">When this process is elevated it never starts Explorer itself: a shell started from an elevated process would run with administrator rights. It waits for Windows to relaunch the shell and reports a failure when that does not happen.</param>
    internal WindowsShellRefresh(IShellOs os, ShellTimings timings, bool elevated = false) { this.os = os; this.timings = timings; this.elevated = elevated; }

    public Task<ShellRefreshResult> RefreshAsync(ShellRefreshKind kind, CancellationToken cancellationToken) => kind switch
    {
        ShellRefreshKind.BroadcastSettingChange => Task.Run(() => Broadcast(cancellationToken), cancellationToken),
        ShellRefreshKind.RestartExplorer => RestartAsync(cancellationToken),
        ShellRefreshKind.BroadcastIntl => Task.Run(() => BroadcastOne("intl", cancellationToken), cancellationToken),
        ShellRefreshKind.BroadcastEnvironment => Task.Run(() => BroadcastOne("Environment", cancellationToken), cancellationToken),
        ShellRefreshKind.ApplyMouse => Task.Run(() => Apply(UserSettingApply.Mouse, cancellationToken), cancellationToken),
        ShellRefreshKind.ApplyKeyboardRepeat => Task.Run(() => Apply(UserSettingApply.KeyboardRepeat, cancellationToken), cancellationToken),
        ShellRefreshKind.ApplyAccessibilityKeys => Task.Run(() => Apply(UserSettingApply.AccessibilityKeys, cancellationToken), cancellationToken),
        _ => Task.FromResult(new ShellRefreshResult(false, RestartFailed)),
    };

    private ShellRefreshResult Broadcast(CancellationToken ct)
    {
        var ok = false;
        try
        {
            foreach (var p in BroadcastParameters) { ct.ThrowIfCancellationRequested(); ok |= os.BroadcastSettingChange(p, timings.BroadcastTimeoutMs); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, BroadcastFailed); }
        return ok ? new(true, BroadcastDone) : new(false, BroadcastFailed);
    }

    // Closed strings only: the two values are the documented lParam strings for regional format and environment variable changes.
    private ShellRefreshResult BroadcastOne(string parameter, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return os.BroadcastSettingChange(parameter, timings.BroadcastTimeoutMs) ? new(true, BroadcastDone) : new(false, BroadcastFailed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, BroadcastFailed); }
    }

    private ShellRefreshResult Apply(UserSettingApply action, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return os.ApplyUserSetting(action) ? new(true, BroadcastDone) : new(false, BroadcastFailed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, BroadcastFailed); }
    }

    internal const uint HotkeyActive = 0x4;

    /// <summary>The new shortcut flags: only the hotkey bit comes from the stored value; every other bit keeps its live state.</summary>
    internal static uint MergeHotkeyFlag(uint live, uint stored) => (live & ~HotkeyActive) | (stored & HotkeyActive);

    private async Task<ShellRefreshResult> RestartAsync(CancellationToken ct)
    {
        // An elevated process cannot bring the shell back without starting an elevated one, and a shell that exits is not always relaunched by Windows: so it does not close the shell at all.
        if (elevated) return new(false, RestartFailed);
        try
        {
            var before = os.FindShellProcesses();
            if (before.Count > 0)
            {
                if (!os.PostExitExplorer()) return new(false, RestartFailed);
                foreach (var pid in before)
                {
                    ct.ThrowIfCancellationRequested();
                    if (os.WaitForExit(pid, timings.ExitWaitMs)) continue;
                    // Last resort, only after the graceful exit timed out, and only for a process that is still a matched shell process.
                    if (!os.FindShellProcesses().Contains(pid) || !os.KillMatchedShell(pid) || !os.WaitForExit(pid, timings.KillWaitMs))
                        return new(false, RestartFailed);
                }
            }
            // Windows normally relaunches the shell itself; give it a moment before starting one.
            if (await WaitUntil(() => os.FindShellProcesses().Count > 0 && os.TaskbarExists(), timings.RelaunchGraceMs, ct)) return new(true, RestartDone);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (os.FindShellProcesses().Count == 0 && !os.StartExplorer()) continue; // never start a second shell if one appeared
                if (await WaitUntil(os.TaskbarExists, timings.TaskbarWaitMs, ct)) return new(true, RestartDone);
            }
            return new(false, RestartFailed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, RestartFailed); }
    }

    private async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs, CancellationToken ct)
    {
        var waited = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (condition()) return true;
            if (waited >= timeoutMs) return false;
            await Task.Delay(timings.PollMs, ct);
            waited += timings.PollMs;
        }
    }
}

/// <summary>Real OS calls. Never constructed by tests.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsShellOs : IShellOs
{
    private const uint WmSettingChange = 0x001A, SmtoAbortIfHung = 0x0002, ExitExplorerMessage = 0x5B4;
    private const int ProcessQueryLimited = 0x1000, TokenQuery = 0x0008;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string? lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, int access, out IntPtr token);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, char[] name, ref int size);

    private static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public bool BroadcastSettingChange(string? parameter, int timeoutMs)
        => SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, parameter, SmtoAbortIfHung, (uint)timeoutMs, out _) != IntPtr.Zero;

    public bool TaskbarExists() => FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;

    private const uint SpiSetMouse = 0x0004, SpiSetKeyboardSpeed = 0x000B, SpiSetKeyboardDelay = 0x0017, SpifSendChange = 0x0002;
    private const uint SpiGetFilterKeys = 0x0032, SpiSetFilterKeys = 0x0033, SpiGetToggleKeys = 0x0034, SpiSetToggleKeys = 0x0035, SpiGetStickyKeys = 0x003A, SpiSetStickyKeys = 0x003B;

    [StructLayout(LayoutKind.Sequential)] private struct StickyKeys { public uint Size, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ToggleKeys { public uint Size, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct FilterKeys { public uint Size, Flags, WaitMs, DelayMs, RepeatMs, BounceMs; }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref StickyKeys value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref ToggleKeys value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref FilterKeys value, uint winIni);

    // The values come from the user profile, which Winora has already written and verified. A string that is not a plain number in range fails the action.
    private static bool TryStored(string subKey, string name, int min, int max, out int value)
    {
        value = 0;
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key?.GetValue(name) is string text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }

    public bool ApplyUserSetting(UserSettingApply action)
    {
        switch (action)
        {
            case UserSettingApply.Mouse:
                const string mouse = @"Control Panel\Mouse";
                if (!TryStored(mouse, "MouseSpeed", 0, 2, out var speed) || !TryStored(mouse, "MouseThreshold1", 0, 100, out var t1) || !TryStored(mouse, "MouseThreshold2", 0, 100, out var t2)) return false;
                return SystemParametersInfo(SpiSetMouse, 0, [t1, t2, speed], SpifSendChange);
            case UserSettingApply.KeyboardRepeat:
                const string keyboard = @"Control Panel\Keyboard";
                if (!TryStored(keyboard, "KeyboardDelay", 0, 3, out var delay) || !TryStored(keyboard, "KeyboardSpeed", 0, 31, out var rate)) return false;
                return SystemParametersInfo(SpiSetKeyboardDelay, (uint)delay, IntPtr.Zero, SpifSendChange) && SystemParametersInfo(SpiSetKeyboardSpeed, (uint)rate, IntPtr.Zero, SpifSendChange);
            case UserSettingApply.AccessibilityKeys:
                const string accessibility = @"Control Panel\Accessibility";
                if (!TryStored(accessibility + @"\StickyKeys", "Flags", 0, int.MaxValue, out var sticky) ||
                    !TryStored(accessibility + @"\Keyboard Response", "Flags", 0, int.MaxValue, out var filter) ||
                    !TryStored(accessibility + @"\ToggleKeys", "Flags", 0, int.MaxValue, out var toggle)) return false;
                var s = new StickyKeys { Size = (uint)Marshal.SizeOf<StickyKeys>() };
                var f = new FilterKeys { Size = (uint)Marshal.SizeOf<FilterKeys>() };
                var t = new ToggleKeys { Size = (uint)Marshal.SizeOf<ToggleKeys>() };
                if (!SystemParametersInfo(SpiGetStickyKeys, s.Size, ref s, 0) || !SystemParametersInfo(SpiGetFilterKeys, f.Size, ref f, 0) || !SystemParametersInfo(SpiGetToggleKeys, t.Size, ref t, 0)) return false;
                s.Flags = WindowsShellRefresh.MergeHotkeyFlag(s.Flags, (uint)sticky);
                f.Flags = WindowsShellRefresh.MergeHotkeyFlag(f.Flags, (uint)filter);
                t.Flags = WindowsShellRefresh.MergeHotkeyFlag(t.Flags, (uint)toggle);
                return SystemParametersInfo(SpiSetStickyKeys, s.Size, ref s, SpifSendChange) && SystemParametersInfo(SpiSetFilterKeys, f.Size, ref f, SpifSendChange) &&
                    SystemParametersInfo(SpiSetToggleKeys, t.Size, ref t, SpifSendChange);
            default:
                return false;
        }
    }

    public bool PostExitExplorer()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        return tray != IntPtr.Zero && PostMessage(tray, ExitExplorerMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public IReadOnlyList<int> FindShellProcesses()
    {
        var result = new List<int>();
        var mySession = Process.GetCurrentProcess().SessionId;
        var me = WindowsIdentity.GetCurrent().User;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != mySession) continue;
                    using var h = OpenProcess(ProcessQueryLimited, false, p.Id);
                    if (h.IsInvalid) continue;
                    var buffer = new char[1024]; var size = buffer.Length;
                    if (!QueryFullProcessImageName(h, 0, buffer, ref size)) continue;
                    if (!string.Equals(new string(buffer, 0, size), ExplorerPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!OpenProcessToken(h, TokenQuery, out var token)) continue;
                    try { using var id = new WindowsIdentity(token); if (id.User != me) continue; }
                    finally { CloseHandle(token); }
                    result.Add(p.Id);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
            }
        }
        return result;
    }

    public bool WaitForExit(int processId, int timeoutMs)
    {
        try { using var p = Process.GetProcessById(processId); return p.WaitForExit(timeoutMs); }
        catch (ArgumentException) { return true; } // already gone
    }

    public bool KillMatchedShell(int processId)
    {
        try { using var p = Process.GetProcessById(processId); p.Kill(); return true; }
        catch (Exception e) when (e is ArgumentException or Win32Exception or InvalidOperationException) { return false; }
    }

    public bool StartExplorer()
    {
        var path = ExplorerPath;
        if (!File.Exists(path)) return false;
        using var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
        return started is not null;
    }
}
