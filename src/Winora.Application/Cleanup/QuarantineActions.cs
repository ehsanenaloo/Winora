// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>Removes one quarantined file for good (content and manifest). It works only on the quarantine folder; no path of a manifest is used.</summary>
public interface IQuarantineDelete
{
    QuarantineResult Delete(Guid id);
}

/// <summary>What the Cleanup page may do with the files Winora set aside.</summary>
public interface IQuarantineActions
{
    /// <summary>Puts the file back where it came from, when the place is one Winora allows (see <see cref="QuarantineActions.IsRestorePlaceAllowed"/>).</summary>
    QuarantineResult Restore(Guid id, CancellationToken cancellationToken);

    /// <summary>Deletes the file for good. This cannot be undone.</summary>
    QuarantineResult Delete(Guid id);
}

public static class QuarantineActionReason
{
    /// <summary>The original place is outside the folders Winora restores to.</summary>
    public const string PlaceNotAllowed = "place_not_allowed";
    public const string NoSuchEntry = "no_such_entry";
}

/// <summary>
/// Restore, delete and empty for the quarantine list. A restore goes only to a place under one of the allowed roots (the profile of the current user): the manifest of an entry sits in a folder
/// the user can write, so its path alone is not trusted by a process that has more rights than the user. Delete never reads a path from a manifest.
/// </summary>
public sealed class QuarantineActions(IQuarantine quarantine, IQuarantineDelete deleter, Func<IReadOnlyList<string>> allowedRoots) : IQuarantineActions
{
    public QuarantineResult Restore(Guid id, CancellationToken cancellationToken)
    {
        var entry = quarantine.List().FirstOrDefault(e => e.Id == id);
        if (entry is null) return new(QuarantineStatus.Refused, QuarantineActionReason.NoSuchEntry, null);
        if (!IsRestorePlaceAllowed(entry.OriginalPath, allowedRoots())) return new(QuarantineStatus.Refused, QuarantineActionReason.PlaceNotAllowed, entry);
        var roots = allowedRoots();
        var result = quarantine.Restore(id, path => IsRestorePlaceAllowed(path, roots), cancellationToken);
        return result.Reason == QuarantineReason.PlaceNotAllowed ? new(QuarantineStatus.Refused, QuarantineActionReason.PlaceNotAllowed, entry) : result;
    }

    public QuarantineResult Delete(Guid id)
    {
        if (quarantine.List().All(e => e.Id != id)) return new(QuarantineStatus.Refused, QuarantineActionReason.NoSuchEntry, null);
        return deleter.Delete(id);
    }

    /// <summary>True when the full path is under one of the roots. Compared by whole path segments, ignoring case, after removing "." and ".." parts.</summary>
    public static bool IsRestorePlaceAllowed(string originalPath, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathFullyQualified(originalPath) || originalPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var full = Path.GetFullPath(originalPath);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) continue;
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
