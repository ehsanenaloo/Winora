// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;

namespace Winora.Infrastructure.Windows.Cleanup;

/// <summary>Read-only folder facts for the leftover scan. It reads names, attributes and sizes; it never enters a link or junction and changes nothing.</summary>
public sealed class LeftoverFolderReader : ILeftoverFolderReader
{
    private static readonly EnumerationOptions OneLevel = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };

    public FolderKind Probe(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var info = new DirectoryInfo(full);
            if (!info.Exists) return File.Exists(full) ? FolderKind.Link : FolderKind.Missing;
            for (var folder = info; folder is not null && folder.Parent is not null; folder = folder.Parent)
                if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return FolderKind.Link;
            return FolderKind.Plain;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return FolderKind.Unreadable; }
    }

    public FolderFacts Inspect(string path, int maxEntries, CancellationToken cancellationToken)
    {
        long bytes = 0; int files = 0, entries = 0;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((path, 0));
        try
        {
            while (pending.TryPop(out var current))
            {
                foreach (var item in new DirectoryInfo(current.Path).EnumerateFileSystemInfos("*", OneLevel))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++entries > maxEntries) return new(bytes, files, entries, false, true, false);
                    if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new(bytes, files, entries, true, false, false);
                    if (item is DirectoryInfo)
                    {
                        if (current.Depth + 1 >= LeftoverFolderScanner.MaxDepth) return new(bytes, files, entries, false, true, false);
                        pending.Push((item.FullName, current.Depth + 1));
                    }
                    else if (item is FileInfo file) { files++; bytes += file.Length; }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(bytes, files, entries, false, false, true); }
        return new(bytes, files, entries, false, false, false);
    }
}
