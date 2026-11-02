// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Infrastructure.Windows;

/// <summary>What a journal file looked like when it was read: its length and last write time. The file only grows, so an equal stamp means the same content.</summary>
internal readonly record struct JournalStamp(string Path, long Length, DateTime WriteUtc)
{
    public static JournalStamp Of(string path)
    {
        var info = new FileInfo(path);
        return new(info.FullName, info.Length, info.LastWriteTimeUtc);
    }
}
