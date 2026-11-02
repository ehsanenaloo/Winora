// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Serialization;
namespace Winora.Application;

/// <summary>The identity of one file as the operating system sees it: volume, file index and size. A path alone never identifies a cleanup target.</summary>
public sealed record FileFingerprint(uint VolumeSerial, ulong FileIndex, long Size);

/// <summary>What a scan recorded about a file and what the cleanup must still find: the fingerprint and the SHA-256 of the content.</summary>
public sealed record FileEvidence(string CanonicalPath, FileFingerprint Fingerprint, string Sha256);

public enum QuarantineStatus { Moved, Restored, Refused, Failed, Deleted }

/// <summary>Stable reasons of a refusal or failure (uninstall.md: revalidate before cleanup; never follow reparse points).</summary>
public static class QuarantineReason
{
    public const string None = "";
    public const string NotFound = "not_found";
    public const string ReparsePoint = "reparse_point";
    public const string NotARegularFile = "not_a_regular_file";
    public const string HardLinked = "hard_linked";
    public const string PathIndirection = "path_indirection";
    public const string IdentityChanged = "identity_changed";
    public const string ContentChanged = "content_changed";
    public const string TooLarge = "too_large";
    public const string QuotaExceeded = "quota_exceeded";
    public const string Locked = "locked";
    public const string AccessDenied = "access_denied";
    public const string DifferentVolume = "different_volume";
    public const string DestinationExists = "destination_exists";
    public const string ManifestInvalid = "manifest_invalid";
    public const string PlaceNotAllowed = "place_not_allowed";
    /// <summary>The folder that held the item is gone, so a restore has nowhere to go. Winora does not create it.</summary>
    public const string PlaceMissing = "place_missing";
    /// <summary>The folder was gone before the move started (an uninstaller often removes its own folder a moment after it exits). Nothing was moved or journaled.</summary>
    public const string AlreadyGone = "already_gone";
    /// <summary>The quarantined copy of an item is missing, so it cannot be restored. Its record can still be deleted.</summary>
    public const string ContentMissing = "content_missing";
    /// <summary>A folder holds a link or junction, too many entries or folders nested too deep.</summary>
    public const string LinkInside = "link_inside";
    public const string TooManyEntries = "too_many_entries";
    public const string Unexpected = "unexpected";
}

/// <summary>
/// One quarantined file or folder. The manifest is stored beside the content; restoring needs nothing else. A folder entry (<see cref="IsFolder"/>) is a leftover folder of a removed
/// program: its fingerprint holds the volume and file index of the folder and the total size of its files, <see cref="Sha256"/> is empty, and <see cref="Program"/> names the program.
/// </summary>
public sealed record QuarantineEntry(Guid Id, string OriginalPath, FileFingerprint Fingerprint, string Sha256, DateTimeOffset QuarantinedAtUtc,
    string? Program = null, bool IsFolder = false, int Files = 0, [property: JsonIgnore] bool ContentMissing = false);

public sealed record QuarantineResult(QuarantineStatus Status, string Reason, QuarantineEntry? Entry)
{
    public bool Ok => Status is QuarantineStatus.Moved or QuarantineStatus.Restored or QuarantineStatus.Deleted;
}

/// <summary>
/// Reversible removal of single files (uninstall.md). Nothing is deleted: a file is moved to a quarantine folder on the same volume through its open handle, after the
/// path, the type, the link count, the identity and the content were revalidated against the evidence of the scan. Reparse points, directories and hard-linked files are refused.
/// </summary>
public interface IQuarantine
{
    /// <summary>Reads the evidence of a file for a later <see cref="Move"/>. Applies the same refusals as <see cref="Move"/>.</summary>
    (QuarantineResult Result, FileEvidence? Evidence) Inspect(string path, CancellationToken cancellationToken);

    QuarantineResult Move(FileEvidence evidence, CancellationToken cancellationToken);

    QuarantineResult Restore(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Restores only when <paramref name="allowPlace"/> accepts the original path of the manifest the restore itself reads. The check and the use share one read, so a manifest that is
    /// replaced between a caller's own check and the restore cannot send the file somewhere else.
    /// </summary>
    QuarantineResult Restore(Guid id, Func<string, bool> allowPlace, CancellationToken cancellationToken) => Restore(id, cancellationToken);

    IReadOnlyList<QuarantineEntry> List();
}

/// <summary>
/// Reversible removal of a whole leftover folder: the folder is renamed by its handle into the quarantine folder on the same volume, after the folder and everything in it were checked
/// again (a plain folder, no link or junction anywhere inside, the path is the expected one). Restore and delete of the entry go through <see cref="IQuarantine.Restore(Guid, Func{string, bool}, CancellationToken)"/>
/// and <see cref="IQuarantineDelete.Delete"/>.
/// </summary>
public interface IFolderQuarantine
{
    /// <param name="itemId">The id the item gets, chosen by the caller so the journal's begin line can name it before anything moves.</param>
    QuarantineResult MoveFolder(string path, string program, CancellationToken cancellationToken, Guid? itemId = null);
}
