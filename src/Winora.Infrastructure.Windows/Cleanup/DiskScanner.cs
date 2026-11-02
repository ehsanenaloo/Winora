// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Cleanup;

/// <summary>
/// Read-only size scan with a bounded number of workers. Folders are listed with one enumeration each (name, attributes and length come with the listing, so no file is opened).
/// Reparse points (junctions, symbolic links, mount points, cloud placeholders) are counted and skipped, never followed. A folder that cannot be listed becomes an
/// <see cref="DiskNodeKind.Unreadable"/> node and a counter, never an exception. Memory is bounded: each folder keeps its largest files one by one and the rest as one aggregate
/// with exact totals. Sizes are logical (what Explorer shows as Size), not allocated clusters; a hard-linked file is counted at each name.
/// </summary>
public sealed class DiskScanner : IDiskScanner
{
    private const int MaxIssues = 200;
    private const int MaxDepth = 4000;

    private readonly record struct Entry(string Name, bool IsDirectory, bool IsReparsePoint, long Length);

    private sealed class Counters
    {
        public long Files, Folders, Bytes, Inaccessible, ReparseSkipped;
        public long LastReport;
        public readonly List<DiskScanIssue> Issues = [];
        public void Issue(DiskScanIssueKind kind, string path) { lock (Issues) if (Issues.Count < MaxIssues) Issues.Add(new(kind, path)); }
    }

    public Task<DiskScanResult> ScanAsync(string root, DiskScanOptions options, IProgress<DiskScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new ArgumentException("The folder must be a full path.", nameof(root));
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The folder does not exist.");
        if (options.MaxFilesPerFolder < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxFilesPerFolder must be at least 1.");
        return Task.Run(() => Scan(full, options, progress, cancellationToken), cancellationToken);
    }

    private static DiskScanResult Scan(string root, DiskScanOptions options, IProgress<DiskScanProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var counters = new Counters();
        var rootNode = new DiskNode(root, DiskNodeKind.Directory);
        var queue = Channel.CreateUnbounded<(DiskNode Node, string Path, int Depth)>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
        var pending = 1;
        queue.Writer.TryWrite((rootNode, root, 0));
        Exception? failure = null;

        void Worker()
        {
            try
            {
                while (queue.Reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
                {
                    while (queue.Reader.TryRead(out var item))
                    {
                        try { ScanFolder(item.Node, item.Path, item.Depth, options, counters, queue.Writer, ref pending, progress, watch, ct); }
                        finally { if (Interlocked.Decrement(ref pending) == 0) queue.Writer.TryComplete(); }
                    }
                }
            }
            catch (OperationCanceledException) { queue.Writer.TryComplete(); }
            catch (Exception e) { Interlocked.CompareExchange(ref failure, e, null); queue.Writer.TryComplete(); }
        }

        var workers = Enumerable.Range(0, Math.Max(1, options.MaxParallelism)).Select(_ => Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(workers);
        ct.ThrowIfCancellationRequested();
        if (failure is not null) throw new IOException("The scan stopped because of an unexpected error.", failure);

        DiskTree.Finish(rootNode);
        progress?.Report(new(Volatile.Read(ref counters.Files), Volatile.Read(ref counters.Folders), Volatile.Read(ref counters.Bytes), Volatile.Read(ref counters.Inaccessible), root));
        return new(rootNode, counters.Files, counters.Folders, counters.Bytes, counters.Inaccessible, counters.ReparseSkipped, counters.Issues, watch.Elapsed);
    }

    private static void ScanFolder(DiskNode node, string path, int depth, DiskScanOptions options, Counters counters, ChannelWriter<(DiskNode, string, int)> queue, ref int pending,
        IProgress<DiskScanProgress>? progress, Stopwatch watch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref counters.Folders);
        if (depth >= MaxDepth) { counters.Issue(DiskScanIssueKind.TooDeep, path); return; }

        var keep = new PriorityQueue<Entry, long>(); // smallest kept file first, so it is the one evicted
        var directories = new List<Entry>();
        long restBytes = 0, restFiles = 0, seen = 0;
        Exception? error = null;

        try
        {
            var enumerable = new FileSystemEnumerable<Entry>(path, static (ref FileSystemEntry e) =>
                new Entry(e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0, e.IsDirectory ? 0 : e.Length),
                new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = 64 * 1024 });
            foreach (var entry in enumerable)
            {
                if ((++seen & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                if (entry.IsReparsePoint && entry.IsDirectory)
                {
                    Interlocked.Increment(ref counters.ReparseSkipped);
                    counters.Issue(DiskScanIssueKind.ReparsePointSkipped, Path.Combine(path, entry.Name));
                    continue;
                }
                if (entry.IsDirectory) { directories.Add(entry); continue; }
                Interlocked.Increment(ref counters.Files);
                Interlocked.Add(ref counters.Bytes, entry.Length);
                if (keep.Count < options.MaxFilesPerFolder) keep.Enqueue(entry, entry.Length);
                else if (entry.Length > keep.Peek().Length)
                {
                    var evicted = keep.Dequeue();
                    restBytes += evicted.Length; restFiles++;
                    keep.Enqueue(entry, entry.Length);
                }
                else { restBytes += entry.Length; restFiles++; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { error = e; }

        if (error is not null)
        {
            Interlocked.Increment(ref counters.Inaccessible);
            counters.Issue(error is UnauthorizedAccessException or System.Security.SecurityException ? DiskScanIssueKind.AccessDenied : DiskScanIssueKind.Unreadable, path);
        }

        while (keep.TryDequeue(out var file, out _)) node.Add(new DiskNode(file.Name, DiskNodeKind.File, file.Length));
        if (restFiles > 0) node.Add(new DiskNode("", DiskNodeKind.Aggregate, restBytes, restFiles));
        if (error is not null && node.Children.Count == 0 && directories.Count == 0) node.MarkUnreadable();
        foreach (var directory in directories)
        {
            var childPath = Path.Combine(path, directory.Name);
            var child = new DiskNode(directory.Name, DiskNodeKind.Directory);
            node.Add(child);
            Interlocked.Increment(ref pending);
            if (!queue.TryWrite((child, childPath, depth + 1))) Interlocked.Decrement(ref pending);
        }

        Report(counters, progress, watch, path, options);
    }

    private static void Report(Counters counters, IProgress<DiskScanProgress>? progress, Stopwatch watch, string current, DiskScanOptions options)
    {
        if (progress is null) return;
        var now = watch.ElapsedTicks;
        var last = Volatile.Read(ref counters.LastReport);
        if (now - last < options.ProgressInterval.TotalSeconds * Stopwatch.Frequency) return;
        if (Interlocked.CompareExchange(ref counters.LastReport, now, last) != last) return;
        progress.Report(new(Volatile.Read(ref counters.Files), Volatile.Read(ref counters.Folders), Volatile.Read(ref counters.Bytes), Volatile.Read(ref counters.Inaccessible), current));
    }
}
