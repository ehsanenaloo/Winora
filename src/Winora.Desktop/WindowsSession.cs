// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
namespace Winora.Desktop;

public sealed class WindowsSession : IWindowsSession
{
    private const uint ExitWindowsLogoff = 0; // EWX_LOGOFF without EWX_FORCE: apps may still ask to save work.

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    public Task<bool> SignOutAsync() => Task.FromResult(ExitWindowsEx(ExitWindowsLogoff, 0));
}
