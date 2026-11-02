// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>The cache groups the free-up-space flow can clean. Each one lives in the user's own profile, so no administrator rights are needed.</summary>
public enum CleanupCategoryId
{
    UserTemp, ThumbnailCache, InternetCache, ShaderCache, ErrorReports, CrashDumps, BrowserCaches, RecycleBin,
    /// <summary>The groups below are in system folders and are offered only when Winora runs as administrator.</summary>
    WindowsTemp, UpdateCache, DeliveryOptimization, SystemDumps, SystemErrorReports,
}

public enum CleanupCategoryKind { Files, RecycleBin }

/// <summary>A folder to look in. <see cref="Patterns"/> are simple wildcard patterns for file names ("*", "thumbcache_*.db").</summary>
public sealed record CleanupLocation(string Root, bool Recurse, IReadOnlyList<string> Patterns);

/// <summary>
/// One cleanable group. <see cref="MinAgeDays"/> keeps files that were written, read or created more recently than that. <see cref="RemoveEmptyFolders"/> removes folders that are empty
/// after the files are gone (never the location root itself). <see cref="Basis"/> is where the choice of folder comes from.
/// </summary>
public sealed record CleanupCategory(CleanupCategoryId Id, IReadOnlyList<CleanupLocation> Locations, int MinAgeDays, bool RemoveEmptyFolders, string Basis, CleanupCategoryKind Kind = CleanupCategoryKind.Files, bool RequiresAdministrator = false);

/// <summary>One file found by a scan. The size and last write time are re-checked right before the file is removed.</summary>
public sealed record CleanupCandidate(string Path, long Size, long LastWriteUtcTicks);

/// <summary>The result of scanning one category. Nothing was changed. <see cref="Truncated"/> is true when more files exist than the scan keeps. <see cref="SkippedRecentBytes"/> is the size of the files kept for their age.</summary>
public sealed record CleanupScan(CleanupCategoryId Category, IReadOnlyList<CleanupCandidate> Files, IReadOnlyList<string> Folders, long Bytes, long Count, long SkippedRecent, long SkippedLinks, bool Truncated, long SkippedRecentBytes = 0);

public sealed record RecycleBinInfo(long Bytes, long Items);

/// <summary>The Recycle Bin of the signed-in user across all drives. Emptying it removes its items for good.</summary>
public interface IRecycleBin
{
    RecycleBinInfo Query();
    RemovalResult Empty(CancellationToken cancellationToken);
}

public interface ICleanupCatalog
{
    IReadOnlyList<CleanupCategory> Categories { get; }
}

public interface ICleanupScanner
{
    /// <summary>Lists the files of the category that may be removed. Read-only. Throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    Task<CleanupScan> ScanAsync(CleanupCategory category, IProgress<long>? progress, CancellationToken cancellationToken);
}

public enum RemovalStatus { Removed, Skipped, Failed }

/// <summary>Stable reasons a file or folder was not removed.</summary>
public static class RemovalReason
{
    public const string None = "";
    public const string InUse = "in_use";
    public const string Changed = "changed";
    public const string TooRecent = "too_recent";
    public const string Reparse = "reparse_point";
    public const string NotRegular = "not_a_regular_file";
    public const string HardLinked = "hard_linked";
    public const string PathIndirection = "path_indirection";
    public const string Gone = "gone";
    public const string NotEmpty = "not_empty";
    public const string AccessDenied = "access_denied";
    public const string OutsideRoot = "outside_root";
    public const string Unexpected = "unexpected";
}

public sealed record RemovalResult(RemovalStatus Status, string Reason)
{
    public static RemovalResult Done { get; } = new(RemovalStatus.Removed, RemovalReason.None);
    public static RemovalResult Skip(string reason) => new(RemovalStatus.Skipped, reason);
    public static RemovalResult Fail(string reason) => new(RemovalStatus.Failed, reason);
}

/// <summary>
/// Permanent removal of single files and empty folders, by open handle. Before a file is removed it is opened without following links, and its path, type, link count, size, last write
/// time and age are checked against what the scan saw. A file that another program has open is skipped, never forced.
/// </summary>
public interface IFileRemover
{
    /// <param name="candidate">What the scan saw.</param>
    /// <param name="newestAllowedUtcTicks">The file is removed only if its last write, last access and creation times are all older than this. Zero turns the age check off.</param>
    RemovalResult RemoveFile(CleanupCandidate candidate, long newestAllowedUtcTicks, CancellationToken cancellationToken);

    /// <summary>Removes the folder only if it is empty and, when an age limit applies, only if its creation time is older than <paramref name="newestAllowedUtcTicks"/>. A folder with anything in it, a young folder or a link is left alone.</summary>
    RemovalResult RemoveEmptyFolder(string folder, long newestAllowedUtcTicks, CancellationToken cancellationToken);
}

public sealed record CleanupCategoryIntent(CleanupCategoryId Id, long Files, long Bytes);

/// <summary>Written and flushed before the first removal of a run, so an interrupted run is visible afterwards.</summary>
public sealed record CleanupRunIntent(Guid RunId, DateTimeOffset StartedUtc, IReadOnlyList<CleanupCategoryIntent> Categories);

public enum CleanupRunState { Completed, Cancelled }

public sealed record CleanupCategoryReport(CleanupCategoryId Id, long RemovedFiles, long RemovedBytes, long RemovedFolders, IReadOnlyDictionary<string, long> NotRemoved, IReadOnlyList<string> FirstProblems);

public sealed record CleanupRunResult(Guid RunId, DateTimeOffset EndedUtc, CleanupRunState State, IReadOnlyList<CleanupCategoryReport> Categories)
{
    public long RemovedFiles => Categories.Sum(c => c.RemovedFiles);
    public long RemovedBytes => Categories.Sum(c => c.RemovedBytes);
    public long RemovedFolders => Categories.Sum(c => c.RemovedFolders);
    public long NotRemoved => Categories.Sum(c => c.NotRemoved.Values.Sum());
}

public sealed record CleanupRunRecord(CleanupRunIntent Intent, CleanupRunResult? Result);

/// <summary>Append-only record of cleanup runs. <see cref="Begin"/> must be durable before it returns.</summary>
public interface ICleanupJournal
{
    void Begin(CleanupRunIntent intent);
    void Complete(CleanupRunResult result);
    /// <summary>The newest runs first. A run without a result was interrupted.</summary>
    IReadOnlyList<CleanupRunRecord> History(int max);
}

public sealed record CleanupProgress(CleanupCategoryId Category, long RemovedFiles, long RemovedBytes, long Total);
