// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public sealed record DiskScanOptions
{
    /// <summary>Files kept one by one in each folder (the largest). The rest of the folder's files become one aggregate node with exact totals. This bounds memory.</summary>
    public int MaxFilesPerFolder { get; init; } = 256;
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);
    /// <summary>Minimum time between two progress reports.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(150);
}

public sealed record DiskScanProgress(long Files, long Folders, long Bytes, long Inaccessible, string CurrentFolder);

public enum DiskScanIssueKind { AccessDenied, Unreadable, ReparsePointSkipped, TooDeep }

public sealed record DiskScanIssue(DiskScanIssueKind Kind, string Path);

/// <summary>The outcome of a scan. <see cref="Issues"/> keeps the first entries only; the counters are exact.</summary>
public sealed record DiskScanResult(DiskNode Root, long Files, long Folders, long Bytes, long Inaccessible, long ReparsePointsSkipped, IReadOnlyList<DiskScanIssue> Issues, TimeSpan Duration);

/// <summary>
/// Read-only size scan of a folder tree. It never follows reparse points (junctions, symbolic links, mount points), counts folders it cannot open instead of failing, and stops
/// promptly when the token is cancelled (then it throws <see cref="OperationCanceledException"/>).
/// </summary>
public interface IDiskScanner
{
    Task<DiskScanResult> ScanAsync(string root, DiskScanOptions options, IProgress<DiskScanProgress>? progress, CancellationToken cancellationToken);
}
