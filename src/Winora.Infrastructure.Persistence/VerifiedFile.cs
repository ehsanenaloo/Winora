// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace Winora.Infrastructure.Persistence;

/// <summary>
/// Opens a file in a folder that other programs of the user can change, for an app that may run with administrator rights. No folder above the file and no file that exists may be a link.
/// The folder is opened first, without sharing delete or rename, and its own path is checked; while that handle is held no folder on the path can be replaced, so the file is created
/// (or opened) only in the folder that was checked. After the open the path of the file handle is checked too.
/// </summary>
public static class VerifiedFile
{
    /// <summary>Test seam: runs after the folder is pinned and before the file is opened.</summary>
    internal static Action<string>? AfterFolderPinned { get; set; }

    public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
    {
        var full = Path.GetFullPath(path);
        RefuseLinks(full);
        using var pin = OperatingSystem.IsWindows() ? PinFolder(Path.GetDirectoryName(full)) : null;
        AfterFolderPinned?.Invoke(full);
        var stream = OperatingSystem.IsWindows() ? OpenLeaf(full, mode, access, share) : new FileStream(full, mode, access, share);
        try
        {
            if (OperatingSystem.IsWindows() && !string.Equals(FinalPath(stream.SafeFileHandle), LongForm(full), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The file is not at the requested place.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>
    /// Opens the file itself, never what a link at that place points to. A link that appears at the place after the check is opened as a link and refused, so nothing is created or
    /// changed at its target. Truncation happens only after the opened object is known to be an ordinary file.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FileStream OpenLeaf(string full, FileMode mode, FileAccess access, FileShare share)
    {
        const uint ReadAccess = 0x80000000, WriteAccess = 0x40000000, OpenReparsePoint = 0x00200000, Normal = 0x80;
        uint desired = (access.HasFlag(FileAccess.Read) ? ReadAccess : 0) | (access.HasFlag(FileAccess.Write) ? WriteAccess : 0);
        uint disposition = mode switch { FileMode.CreateNew => 1, FileMode.Create or FileMode.OpenOrCreate or FileMode.Append => 4, FileMode.Open => 3, FileMode.Truncate => 3, _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
        if (mode is FileMode.Truncate or FileMode.Create && !access.HasFlag(FileAccess.Write)) throw new ArgumentException("This mode needs write access.", nameof(access));
        var handle = CreateFileW(full, desired, (uint)share & 0x7, IntPtr.Zero, disposition, Normal | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Marshal.GetExceptionForHR(unchecked((int)0x80070000) | error) ?? new IOException("The file cannot be opened (" + error + ").", error);
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("The file could not be checked.");
            if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("A folder or file on the path is a link.");
            var stream = new FileStream(handle, access, 4096, false);
            try
            {
                if (mode is FileMode.Create or FileMode.Truncate) stream.SetLength(0);
                if (mode == FileMode.Append) stream.Seek(0, SeekOrigin.End);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    /// <summary>Holds a folder so that it and the folders above it cannot be renamed or replaced until the result is disposed. Use it around a write followed by a move in the same folder.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="chosenByUser">True for a folder the person picked (it may sit behind a link such as a synced known folder): the folder is then held as it resolves now, whatever its path, and only a swap after this call is prevented.</param>
    public static IDisposable Pin(string folder, bool chosenByUser = false)
    {
        var full = Path.GetFullPath(folder);
        if (!chosenByUser) RefuseLinks(full);
        return OperatingSystem.IsWindows() ? PinFolder(full, chosenByUser) ?? (IDisposable)new NoPin() : new NoPin();
    }

    private sealed class NoPin : IDisposable { public void Dispose() { } }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle? PinFolder(string? folder, bool chosenByUser = false)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        // List access, shared for read and write only: the folder and the folders above it cannot be renamed or replaced while this handle is open.
        var handle = CreateFileW(folder, 0x1, 0x3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw error switch
            {
                2 or 3 => new DirectoryNotFoundException("The folder does not exist."),
                5 => new UnauthorizedAccessException("The folder cannot be opened."),
                _ => new IOException("The folder cannot be opened (" + error + ").", error),
            };
        }
        try
        {
            if (!chosenByUser && !string.Equals(FinalPath(handle), LongForm(folder), StringComparison.OrdinalIgnoreCase)) throw new IOException("The folder is not at the requested place.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Every folder from the drive root down to the file, and the file when it exists, must not be a link.</summary>
    public static void RefuseLinks(string fullPath)
    {
        for (var p = fullPath; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(p); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A folder or file on the path is a link.");
        }
    }

    /// <summary>The path with 8.3 short names written out (the temp folder is often given in short form); the handle reports the long form.</summary>
    [SupportedOSPlatform("windows")]
    private static string LongForm(string path)
    {
        var buffer = new StringBuilder(1024);
        var n = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        return n == 0 || n >= buffer.Capacity ? path : buffer.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (n == 0 || n >= buffer.Capacity) throw new IOException("The path of the file could not be read.");
        var text = buffer.ToString();
        return text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + text[8..] : text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string path, StringBuilder longPath, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
