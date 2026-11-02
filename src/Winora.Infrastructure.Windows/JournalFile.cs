// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Infrastructure.Windows;

/// <summary>
/// The one way the journals of the app add a line: the folder is created when it is missing, a torn last line (a power loss in the middle of a write) is closed with a newline so the next
/// line is not glued to it, then the line is appended and flushed to disk. Nothing is ever rewritten or removed.
/// </summary>
internal static class JournalFile
{
    public static void Append(string path, byte[] line)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var length = stream.Length;
        if (length > 0)
        {
            stream.Seek(length - 1, SeekOrigin.Begin);
            if (stream.ReadByte() != '\n') { stream.Seek(0, SeekOrigin.End); stream.WriteByte((byte)'\n'); }
        }
        stream.Seek(0, SeekOrigin.End);
        stream.Write(line);
        stream.Flush(flushToDisk: true);
    }
}
