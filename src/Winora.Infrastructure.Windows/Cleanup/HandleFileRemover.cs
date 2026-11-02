// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Cleanup;

/// <summary>
/// Permanent removal by open handle. The target is opened without following links and the final path of the handle must equal the requested
/// path, so no parent folder can be a junction or a symbolic link. A file must be a regular file with one link, with the size and last write time the scan saw and, when an age limit
/// applies, with all its times older than the limit. A file that another program has open without allowing deletion cannot be opened for deletion and is skipped; a file whose users allow deletion (browsers keep their caches that way) is removed, as Explorer would, and its last user keeps a handle until it closes it. The delete itself is a disposition set on the
/// handle (POSIX semantics, and a check that the file is not mapped as a program image); no path-based delete call is used anywhere in this class.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HandleFileRemover : IFileRemover
{
    public RemovalResult RemoveFile(CleanupCandidate candidate, long newestAllowedUtcTicks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(candidate.Path) || candidate.Path.StartsWith(@"\\", StringComparison.Ordinal)) return RemovalResult.Skip(RemovalReason.PathIndirection);
        var full = Path.GetFullPath(candidate.Path);
        using var handle = CreateFile(@"\\?\" + full, Delete | FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, 0, OpenExisting, FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid) return OpenFailure(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info)) return RemovalResult.Fail(RemovalReason.Unexpected);
        if ((info.FileAttributes & AttributeReparsePoint) != 0) return RemovalResult.Skip(RemovalReason.Reparse);
        if ((info.FileAttributes & AttributeDirectory) != 0) return RemovalResult.Skip(RemovalReason.NotRegular);
        if (info.NumberOfLinks != 1) return RemovalResult.Skip(RemovalReason.HardLinked);
        if (!string.Equals(FinalPath(handle), full, StringComparison.OrdinalIgnoreCase)) return RemovalResult.Skip(RemovalReason.PathIndirection);

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var write = Ticks(info.WriteHigh, info.WriteLow);
        if (size != candidate.Size || write != candidate.LastWriteUtcTicks) return RemovalResult.Skip(RemovalReason.Changed);
        if (newestAllowedUtcTicks > 0 && Math.Max(write, Math.Max(Ticks(info.AccessHigh, info.AccessLow), Ticks(info.CreationHigh, info.CreationLow))) >= newestAllowedUtcTicks)
            return RemovalResult.Skip(RemovalReason.TooRecent);
        return Dispose(handle);
    }

    public RemovalResult RemoveEmptyFolder(string folder, long newestAllowedUtcTicks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal)) return RemovalResult.Skip(RemovalReason.PathIndirection);
        var full = Path.GetFullPath(folder).TrimEnd('\\');
        using var handle = CreateFile(@"\\?\" + full, Delete | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, 0, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid) return OpenFailure(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info)) return RemovalResult.Fail(RemovalReason.Unexpected);
        if ((info.FileAttributes & AttributeReparsePoint) != 0) return RemovalResult.Skip(RemovalReason.Reparse);
        if ((info.FileAttributes & AttributeDirectory) == 0) return RemovalResult.Skip(RemovalReason.NotRegular);
        if (!string.Equals(FinalPath(handle)?.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)) return RemovalResult.Skip(RemovalReason.PathIndirection);
        if (newestAllowedUtcTicks > 0 && Ticks(info.CreationHigh, info.CreationLow) >= newestAllowedUtcTicks) return RemovalResult.Skip(RemovalReason.TooRecent);
        // Only the creation time counts: removing the old files of a folder changes its write time to now.
        // A folder that still has anything in it makes the disposition fail with "directory not empty": the folder is left as it is.
        return Dispose(handle);
    }

    private static RemovalResult OpenFailure(int error) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound => RemovalResult.Skip(RemovalReason.Gone),
        ErrorSharingViolation or ErrorLockViolation => RemovalResult.Skip(RemovalReason.InUse),
        ErrorAccessDenied => RemovalResult.Fail(RemovalReason.AccessDenied),
        _ => RemovalResult.Fail(RemovalReason.Unexpected),
    };

    private static RemovalResult Dispose(SafeFileHandle handle)
    {
        var error = SetDisposition(handle);
        if (error == 0) return RemovalResult.Done;
        return error switch
        {
            ErrorDirNotEmpty => RemovalResult.Skip(RemovalReason.NotEmpty),
            ErrorSharingViolation or ErrorLockViolation or ErrorCannotDelete => RemovalResult.Skip(RemovalReason.InUse),
            ErrorAccessDenied => RemovalResult.Fail(RemovalReason.AccessDenied),
            _ => RemovalResult.Fail(RemovalReason.Unexpected),
        };
    }

    /// <summary>Marks the open file or folder for deletion. The first choice is the extended disposition; a file system that does not know it gets the classic one.</summary>
    private static int SetDisposition(SafeFileHandle handle)
    {
        var flags = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(flags, DispositionDelete | DispositionPosixSemantics | DispositionForceImageSectionCheck | DispositionIgnoreReadOnly);
            if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, flags, 4)) return 0;
            var error = Marshal.GetLastWin32Error();
            if (error is not (ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction)) return error;
            Marshal.WriteInt32(flags, 1);
            return SetFileInformationByHandle(handle, FileDispositionInfoClass, flags, 4) ? 0 : Marshal.GetLastWin32Error();
        }
        finally { Marshal.FreeHGlobal(flags); }
    }

    private static long Ticks(uint high, uint low) => FileTimeTicks(((long)high << 32) | low);

    /// <summary>A time stamp outside the range DateTime can hold (a damaged value) counts as the newest possible time, so the age check keeps the file.</summary>
    private static long FileTimeTicks(long fileTime) => fileTime is >= 0 and <= 2650467743999999999 ? DateTime.FromFileTimeUtc(fileTime).Ticks : long.MaxValue;

    private static string? FinalPath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
        if (length == 0 || length >= builder.Capacity) return null;
        var text = builder.ToString();
        return text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? null : text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
    }

    // ---- Win32 -------------------------------------------------------------------------------------

    private const uint Delete = 0x00010000, FileReadAttributes = 0x0080, Synchronize = 0x00100000;
    private const uint FileShareRead = 1, FileShareWrite = 2, FileShareDelete = 4, OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000, FileFlagBackupSemantics = 0x02000000;
    private const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400;
    private const int FileDispositionInfoClass = 4, FileDispositionInfoExClass = 21;
    private const int DispositionDelete = 0x1, DispositionPosixSemantics = 0x2, DispositionForceImageSectionCheck = 0x4, DispositionIgnoreReadOnly = 0x10;
    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5, ErrorInvalidFunction = 1, ErrorSharingViolation = 32, ErrorLockViolation = 33, ErrorNotSupported = 50,
        ErrorInvalidParameter = 87, ErrorDirNotEmpty = 145, ErrorCannotDelete = 1235;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, nint information, uint size);
}
