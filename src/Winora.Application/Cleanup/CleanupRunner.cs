// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>A category together with the scan the user reviewed.</summary>
public sealed record CleanupSelection(CleanupCategory Category, CleanupScan Scan);

/// <summary>
/// Removes the files of reviewed scans. The order is fixed: record the intent and make it durable, then remove file by file (each one is re-checked by the remover), then remove folders
/// that became empty, then record the result. Every path is checked to be inside a root of its category before it reaches the remover. Cancelling stops at the next file and still records a result.
/// The problem lines name the file with the user's own folder replaced by a token (<see cref="PersonalPaths"/>), because they go into the journal.
/// </summary>
public sealed class CleanupRunner(IFileRemover remover, ICleanupJournal journal, TimeProvider? clock = null, IRecycleBin? recycleBin = null, string? profileFolder = null)
{
    private const int MaxProblemsPerCategory = 20;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly string? profile = profileFolder ?? PersonalPaths.CurrentProfile();

    public Task<CleanupRunResult> RunAsync(IReadOnlyList<CleanupSelection> selections, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(selections, progress, cancellationToken), CancellationToken.None);

    private CleanupRunResult Run(IReadOnlyList<CleanupSelection> selections, IProgress<CleanupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var runId = Guid.NewGuid();
        var started = clock.GetUtcNow();
        journal.Begin(new(runId, started, selections.Select(s => new CleanupCategoryIntent(s.Category.Id, s.Scan.Count, s.Scan.Bytes)).ToList()));

        var reports = new List<CleanupCategoryReport>();
        var state = CleanupRunState.Completed;
        try
        {
            foreach (var selection in selections)
            {
                if (ct.IsCancellationRequested) { state = CleanupRunState.Cancelled; break; }
                var report = RunCategory(selection, started, progress, ct, out var cancelled);
                reports.Add(report);
                if (cancelled) { state = CleanupRunState.Cancelled; break; }
            }
        }
        finally
        {
            journal.Complete(new(runId, clock.GetUtcNow(), state, reports));
        }
        return new(runId, clock.GetUtcNow(), state, reports);
    }

    private CleanupCategoryReport RunCategory(CleanupSelection selection, DateTimeOffset started, IProgress<CleanupProgress>? progress, CancellationToken ct, out bool cancelled)
    {
        cancelled = false;
        var category = selection.Category;
        if (category.Kind == CleanupCategoryKind.RecycleBin) return RunRecycleBin(selection, progress, ct, out cancelled);
        var roots = category.Locations.Select(l => CleanupRootGuard.Normalize(l.Root)).ToList();
        var cutoff = category.MinAgeDays > 0 ? started.UtcTicks - TimeSpan.FromDays(category.MinAgeDays).Ticks : 0;
        long removedFiles = 0, removedBytes = 0, removedFolders = 0, handled = 0;
        var notRemoved = new Dictionary<string, long>(StringComparer.Ordinal);
        var problems = new List<string>();

        void NotRemoved(string reason, string path, bool problem)
        {
            notRemoved[reason] = notRemoved.GetValueOrDefault(reason) + 1;
            if (problem && problems.Count < MaxProblemsPerCategory) problems.Add(reason + ": " + PersonalPaths.Redact(path, profile));
        }

        foreach (var file in selection.Scan.Files)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            handled++;
            if (!IsInside(file.Path, roots)) { NotRemoved(RemovalReason.OutsideRoot, file.Path, true); continue; }
            RemovalResult result;
            try { result = remover.RemoveFile(file, cutoff, ct); }
            catch (OperationCanceledException) { cancelled = true; break; }
            switch (result.Status)
            {
                case RemovalStatus.Removed: removedFiles++; removedBytes += file.Size; break;
                case RemovalStatus.Skipped: NotRemoved(result.Reason, file.Path, false); break;
                default: NotRemoved(result.Reason, file.Path, true); break;
            }
            if ((handled & 0x3F) == 0) progress?.Report(new(category.Id, removedFiles, removedBytes, selection.Scan.Count));
        }

        if (!cancelled && category.RemoveEmptyFolders)
        {
            // Deepest folders first, so a folder that only held empty folders is empty by the time it is tried. A location root is never removed.
            foreach (var folder in selection.Scan.Folders.OrderByDescending(f => f.Length))
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                if (!IsInside(folder, roots) || roots.Any(r => string.Equals(r, CleanupRootGuard.Normalize(folder), StringComparison.OrdinalIgnoreCase))) continue;
                RemovalResult result;
                try { result = remover.RemoveEmptyFolder(folder, cutoff, ct); }
                catch (OperationCanceledException) { cancelled = true; break; }
                if (result.Status == RemovalStatus.Removed) removedFolders++;
                else if (result.Status == RemovalStatus.Failed) NotRemoved(result.Reason, folder, true);
            }
        }

        progress?.Report(new(category.Id, removedFiles, removedBytes, selection.Scan.Count));
        return new(category.Id, removedFiles, removedBytes, removedFolders, notRemoved, problems);
    }

    private CleanupCategoryReport RunRecycleBin(CleanupSelection selection, IProgress<CleanupProgress>? progress, CancellationToken ct, out bool cancelled)
    {
        cancelled = ct.IsCancellationRequested;
        var id = selection.Category.Id;
        var notRemoved = new Dictionary<string, long>(StringComparer.Ordinal);
        if (cancelled) return new(id, 0, 0, 0, notRemoved, []);
        var result = recycleBin is null ? RemovalResult.Fail(RemovalReason.Unexpected) : recycleBin.Empty(ct);
        if (result.Status == RemovalStatus.Removed)
        {
            progress?.Report(new(id, selection.Scan.Count, selection.Scan.Bytes, selection.Scan.Count));
            return new(id, selection.Scan.Count, selection.Scan.Bytes, 0, notRemoved, []);
        }
        notRemoved[result.Reason] = Math.Max(1, selection.Scan.Count);
        return new(id, 0, 0, 0, notRemoved, [result.Reason + ": " + id]);
    }

    private static bool IsInside(string path, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split('\\', '/').Any(s => s is ".." or ".")) return false;
        var full = CleanupRootGuard.Normalize(path);
        return roots.Any(r => full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
    }
}
