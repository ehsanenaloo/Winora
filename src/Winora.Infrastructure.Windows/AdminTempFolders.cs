// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Winora.Application;
using Winora.Infrastructure.Windows.Cleanup;

namespace Winora.Infrastructure.Windows;

/// <summary>What one sweep of the temp folders did.</summary>
public sealed record TempSweep(bool Checked, int Removed, int Kept);

/// <summary>
/// Removes the old <c>%ProgramData%\Winora\temp\&lt;id&gt;</c> folders that repair tools and uninstallers got as their TEMP (<see cref="ChildEnvironment"/>). Such a folder cannot be removed
/// right after a run, because some uninstallers copy themselves there and keep running. At app start every folder whose name is an id and that was created more than
/// <see cref="MaxAge"/> ago is removed. The sweep runs only when the temp folder is admin-only and every folder above it is protected. It never follows a link: a link is left where it is,
/// with what it points to, and the folder that holds it stays. Every file and folder is removed through the reviewed handle remover, which opens the entry without following links and
/// checks its final path, so a swapped parent is not followed either.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class AdminTempFolders
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunId();

    public static string DefaultRoot() => Path.Combine(AdminOnlyFolder.AppRoot(), "temp");

    /// <param name="root">The temp folder. Production passes none (<see cref="DefaultRoot"/>).</param>
    /// <param name="extraTrusted">Principals treated like Administrators. Production passes none; tests pass the test user.</param>
    public static TempSweep Sweep(DateTimeOffset now, string? root = null, IFileRemover? remover = null, IEnumerable<SecurityIdentifier>? extraTrusted = null, CancellationToken cancellationToken = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? DefaultRoot()));
        if (!Directory.Exists(full) || !AdminOnlyFolder.IsProtected(full, extraTrusted)) return new(false, 0, 0);
        remover ??= new HandleFileRemover();
        var cutoff = (now - MaxAge).UtcTicks;
        int removed = 0, kept = 0;
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(full).EnumerateFileSystemInfos().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(true, 0, 0); }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RunId().IsMatch(entry.Name) || entry is not DirectoryInfo folder || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            try
            {
                if (folder.CreationTimeUtc.Ticks >= cutoff) continue;
                RemoveContents(folder, remover, cancellationToken);
                // The age of the id folder is checked again on the open handle; only the creation time counts.
                if (remover.RemoveEmptyFolder(folder.FullName, cutoff, cancellationToken).Status == RemovalStatus.Removed) removed++;
                else kept++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { kept++; }
        }
        return new(true, removed, kept);
    }

    private static void RemoveContents(DirectoryInfo folder, IFileRemover remover, CancellationToken cancellationToken)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos().ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (entry is DirectoryInfo child)
            {
                RemoveContents(child, remover, cancellationToken);
                remover.RemoveEmptyFolder(child.FullName, 0, cancellationToken);
            }
            else if (entry is FileInfo file) remover.RemoveFile(new CleanupCandidate(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks), 0, cancellationToken);
        }
    }
}
