// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Cleanup;

/// <summary>
/// The Recycle Bin through the documented shell functions: SHQueryRecycleBin for the size and item count, SHEmptyRecycleBin to empty it without the shell's own dialog, progress window and sound.
/// The shell empties the bin of the signed-in user on all drives; items cannot be listed or restored afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBin : IRecycleBin
{
    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;
    private const int ErrorAbort = unchecked((int)0x80004004);

    // SHQUERYRBINFO has the natural layout of the platform (24 bytes on x64); a packed layout makes the shell refuse the call with E_INVALIDARG.
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo { public uint Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref QueryInfo info);

    [DllImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr owner, string? rootPath, uint flags);

    public RecycleBinInfo Query() => TryQuery(out var info) == 0 ? info : new(0, 0);

    /// <summary>The raw result of the shell call (0 is success). Tests use it so that a wrong structure layout cannot pass as an empty bin.</summary>
    internal static int TryQuery(out RecycleBinInfo info)
    {
        var raw = new QueryInfo { Size = (uint)Marshal.SizeOf<QueryInfo>() };
        var hr = SHQueryRecycleBin(null, ref raw);
        info = hr == 0 ? new(Math.Max(0, raw.Bytes), Math.Max(0, raw.Items)) : new(0, 0);
        return hr;
    }

    public RemovalResult Empty(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        return hr == 0 ? RemovalResult.Done : hr == ErrorAbort ? RemovalResult.Skip(RemovalReason.InUse) : RemovalResult.Fail(RemovalReason.Unexpected);
    }
}
