// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Enumeration;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Cleanup;

/// <summary>
/// Read-only scan of a cleanup category. It lists files, never opens them, never follows a link (a link inside a location is counted and skipped) and keeps only files that match
/// the category patterns and are older than its minimum age. The age is taken from the newest of the last write, last access and creation times.
/// </summary>
public sealed class CleanupScanner(TimeProvider? clock = null, IRecycleBin? recycleBin = null) : ICleanupScanner
{
    public const int MaxFiles = 200_000;
    public const int MaxFolders = 100_000;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    private readonly record struct Entry(string Name, bool IsDirectory, bool IsReparsePoint, long Length, long WriteTicks, long AccessTicks, long CreationTicks);

    public Task<CleanupScan> ScanAsync(CleanupCategory category, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        return Task.Run(() => Scan(category, progress, cancellationToken), cancellationToken);
    }

    private CleanupScan Scan(CleanupCategory category, IProgress<long>? progress, CancellationToken ct)
    {
        if (category.Kind == CleanupCategoryKind.RecycleBin)
        {
            var info = recycleBin?.Query() ?? new RecycleBinInfo(0, 0);
            return new(category.Id, [], [], info.Bytes, info.Items, 0, 0, false);
        }
        var cutoff = category.MinAgeDays > 0 ? clock.GetUtcNow().UtcTicks - TimeSpan.FromDays(category.MinAgeDays).Ticks : long.MaxValue;
        var files = new List<CleanupCandidate>();
        var folders = new List<string>();
        long bytes = 0, recent = 0, recentBytes = 0, links = 0;
        var truncated = false;

        foreach (var location in category.Locations)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPlainFolder(location.Root)) continue;
            var pending = new Stack<string>();
            pending.Push(Path.GetFullPath(location.Root));
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var folder = pending.Pop();
                IEnumerable<Entry> entries;
                try
                {
                    entries = new FileSystemEnumerable<Entry>(folder, static (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0,
                        e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcTicks, e.LastAccessTimeUtc.UtcTicks, e.CreationTimeUtc.UtcTicks),
                        new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = 64 * 1024 });
                    foreach (var entry in entries)
                    {
                        if (entry.IsReparsePoint) { links++; continue; }
                        var full = Path.Combine(folder, entry.Name);
                        if (entry.IsDirectory)
                        {
                            if (!location.Recurse) continue;
                            if (folders.Count < MaxFolders) folders.Add(full); else truncated = true;
                            pending.Push(full);
                            continue;
                        }
                        if (!location.Patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, entry.Name, ignoreCase: true))) continue;
                        if (Math.Max(entry.WriteTicks, Math.Max(entry.AccessTicks, entry.CreationTicks)) >= cutoff) { recent++; recentBytes += entry.Length; continue; }
                        if (files.Count >= MaxFiles) { truncated = true; continue; }
                        files.Add(new(full, entry.Length, entry.WriteTicks));
                        bytes += entry.Length;
                    }
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { /* a folder that cannot be listed has nothing the scan may offer */ }
                progress?.Report(files.Count);
            }
        }
        return new(category.Id, files, folders, bytes, files.Count, recent, links, truncated, recentBytes);
    }

    private static bool IsPlainFolder(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or ArgumentException) { return false; }
    }
}
