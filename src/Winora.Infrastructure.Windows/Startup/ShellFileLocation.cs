// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Winora.Application;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// The only code that opens a File Explorer window for a file (a source scan test enforces it): the documented shell function that opens the folder of a file and selects it. No command line is
/// built and no process is started by Winora. Only an existing file or folder on a local drive is accepted; network and device paths are refused.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellFileLocation : IFileLocationOpener
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count, IntPtr[]? items, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr memory);

    /// <summary>True for a full path on a lettered drive, without network, device or relative parts.</summary>
    internal static bool IsLocalFilePath(string? path)
    {
        if (path is not { Length: >= 3 and <= 1024 } || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Any(char.IsControl)) return false;
        try { return Path.GetFullPath(path) == path; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public bool Reveal(string path)
    {
        if (!IsLocalFilePath(path)) return false;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;
            try { return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0; }
            finally { CoTaskMemFree(pidl); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
    }
}
