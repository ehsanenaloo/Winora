// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using Winora.Domain;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.Windows;

namespace Winora.Desktop;

/// <summary>Where the app keeps its operation journal, or the text key of the reason it cannot start. <see cref="Notes"/> are written to the log once the log exists.</summary>
public sealed record StartupJournal(string? DatabasePath, string? RefusalKey, IReadOnlyList<string> Notes);

/// <summary>
/// The first step of the start, before anything reads a record. An elevated app prepares the admin-only root, then opens the
/// operation journal in the admin-only journals folder; the first time, it copies the checked rows of the old user-writable journal. It never falls back to the old file:
/// when the protected folder is refused, the start stops with a plain reason. A process that is not elevated keeps the old file, because it acts with the user's own rights.
/// </summary>
public static class JournalStartup
{
    public static StartupJournal Prepare(bool elevated, string legacyPath, Func<RootPreparation> prepareRoot, Func<string> protectedPath, Func<string, bool> knownSetting, DateTimeOffset now)
    {
        if (!elevated) return new(legacyPath, null, []);
        var notes = new List<string>();
        var root = prepareRoot();
        if (root.Outcome == RootOutcome.Replaced)
            notes.Add($"The folder {root.Root} was not admin-only ({root.Problem}). It was renamed to {Path.GetFileName(root.MovedTo)} and a new admin-only folder was made.");
        if (!root.Usable)
        {
            notes.Add($"The folder {root.Root} is refused ({root.Problem}). Nothing was read or written.");
            return new(null, root.ParentProblem ? "RecordsFolderParentUnsafe" : "RecordsFolderUnsafe", notes);
        }
        string path;
        try { path = protectedPath(); }
        catch (AdminOnlyFolderException e)
        {
            notes.Add($"The journals folder is refused ({e.Problem}). Nothing was read or written.");
            return new(null, e.Problem is AdminOnlyProblem.ParentLink or AdminOnlyProblem.ParentOwner or AdminOnlyProblem.ParentRights ? "RecordsFolderParentUnsafe" : "RecordsFolderUnsafe", notes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            notes.Add("The journals folder could not be opened. Nothing was read or written.");
            return new(null, "RecordsFolderUnsafe", notes);
        }
        JournalImportResult import;
        try { import = SqliteOperationStore.MoveLegacy(legacyPath, path, knownSetting, now); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or StoreException)
        {
            notes.Add("The operation journal could not be moved to the admin-only folder. Nothing was read.");
            return new(null, "StartupFailed", notes);
        }
        if (import.Outcome != JournalImportOutcome.AlreadyMoved)
            notes.Add($"The operation journal moved to the admin-only folder ({import.Outcome}): {import.Imported} rows copied, {import.Skipped} left out. Old file renamed: {(import.LegacyMovedTo is null ? "no" : Path.GetFileName(import.LegacyMovedTo))}.");
        return new(path, null, notes);
    }
}
