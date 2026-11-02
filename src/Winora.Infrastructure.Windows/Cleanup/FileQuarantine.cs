// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Cleanup;

/// <summary>
/// File quarantine on a local volume (uninstall.md). Every step works on an open handle, never on a path that could change under it:
/// the file is opened without following reparse points, its final path must equal the requested canonical path (so no parent is a junction or a symbolic link), it must be a
/// regular file with one link, and its volume, file index, size and SHA-256 must equal the evidence. The move is a rename by handle into the quarantine folder, which is
/// opened the same way. Limits: 512 MiB per file, 2 GiB in total for single files. A leftover folder moves whole, by a rename of its handle (see <see cref="MoveFolder"/>); it is never
/// copied, so it has no size limit, only a limit on its entries. Nothing here deletes on its own; the purge after 30 days is a caller of <see cref="Delete"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileQuarantine : IQuarantine, IQuarantineDelete, IFolderQuarantine
{
    public const long MaxFileBytes = 512L * 1024 * 1024;
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    private const string ContentExtension = ".q";
    private const string ManifestExtension = ".json";
    private const string FolderExtension = ".d";

    private readonly Func<string> rootProvider;
    private readonly TimeProvider clock;
    private readonly long maxFileBytes, maxTotalBytes;

    /// <summary>The quarantine folder, asked for on every use. The app passes a folder only administrators can change (<see cref="AdminOnlyFolder"/>); a folder that fails that check throws <see cref="IOException"/>, and the call then does nothing.</summary>
    private string root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootProvider()));

    public FileQuarantine(string root, TimeProvider? clock = null) : this(root, MaxFileBytes, MaxTotalBytes, clock) { }

    public FileQuarantine(Func<string> rootProvider, TimeProvider? clock = null)
    {
        this.rootProvider = rootProvider;
        this.clock = clock ?? TimeProvider.System;
        maxFileBytes = MaxFileBytes;
        maxTotalBytes = MaxTotalBytes;
    }

    /// <summary>The limits are parameters so tests can reach them without writing gigabytes; production uses the constants.</summary>
    internal FileQuarantine(string root, long maxFileBytes, long maxTotalBytes, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        rootProvider = () => full;
        this.clock = clock ?? TimeProvider.System;
        this.maxFileBytes = maxFileBytes;
        this.maxTotalBytes = maxTotalBytes;
    }

    public (QuarantineResult Result, FileEvidence? Evidence) Inspect(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var file = OpenChecked(path, out var info, out var reason, out var canonical);
            if (file is null) return (Refuse(reason), null);
            if (info.Size > maxFileBytes) return (Refuse(QuarantineReason.TooLarge), null);
            var hash = Hash(file, cancellationToken);
            return (new(QuarantineStatus.Moved, QuarantineReason.None, null), new FileEvidence(canonical!, info, hash));
        }
        catch (IOException) { return (new(QuarantineStatus.Failed, QuarantineReason.Locked, null), null); }
        catch (UnauthorizedAccessException) { return (new(QuarantineStatus.Failed, QuarantineReason.AccessDenied, null), null); }
    }

    public QuarantineResult Move(FileEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        try
        {
            if (evidence.Fingerprint.Size > maxFileBytes) return Refuse(QuarantineReason.TooLarge);
            if (Total() + evidence.Fingerprint.Size > maxTotalBytes) return Refuse(QuarantineReason.QuotaExceeded);
            using var file = OpenChecked(evidence.CanonicalPath, out var info, out var reason, out _);
            if (file is null) return Refuse(reason);
            if (!info.Equals(evidence.Fingerprint)) return Refuse(QuarantineReason.IdentityChanged);
            if (!string.Equals(Hash(file, cancellationToken), evidence.Sha256, StringComparison.OrdinalIgnoreCase)) return Refuse(QuarantineReason.ContentChanged);

            Directory.CreateDirectory(root);
            using var folder = OpenDirectory(root, out var folderReason);
            if (folder is null) return Refuse(folderReason);
            if (VolumeOf(folder) != info.VolumeSerial) return Refuse(QuarantineReason.DifferentVolume);

            var id = Guid.NewGuid();
            var entry = new QuarantineEntry(id, evidence.CanonicalPath, info, evidence.Sha256, clock.GetUtcNow());
            // The manifest is written and flushed to disk first: a crash between the two steps leaves a manifest without content, never content without a way back.
            WriteManifest(id, entry);
            var moved = RenameInto(file, root, id.ToString("N") + ContentExtension, out var error);
            if (!moved)
            {
                File.Delete(ManifestPath(id));
                return Failed(error);
            }
            return new(QuarantineStatus.Moved, QuarantineReason.None, entry);
        }
        catch (IOException) { return new(QuarantineStatus.Failed, QuarantineReason.Locked, null); }
        catch (UnauthorizedAccessException) { return new(QuarantineStatus.Failed, QuarantineReason.AccessDenied, null); }
    }

    public QuarantineResult Restore(Guid id, CancellationToken cancellationToken) => Restore(id, static _ => true, cancellationToken);

    public QuarantineResult Restore(Guid id, Func<string, bool> allowPlace, CancellationToken cancellationToken)
    {
        try
        {
            var entry = ReadManifest(id);
            if (entry is null) return Refuse(QuarantineReason.ManifestInvalid);
            if (!allowPlace(entry.OriginalPath)) return Refuse(QuarantineReason.PlaceNotAllowed);
            if (entry.IsFolder) return RestoreFolder(entry);
            var contentPath = ContentPath(id);
            if (!File.Exists(contentPath)) return Refuse(QuarantineReason.NotFound);
            using var file = OpenChecked(contentPath, out var info, out var reason, out _);
            if (file is null) return Refuse(reason);
            if (!info.Equals(entry.Fingerprint)) return Refuse(QuarantineReason.IdentityChanged);
            if (!string.Equals(Hash(file, cancellationToken), entry.Sha256, StringComparison.OrdinalIgnoreCase)) return Refuse(QuarantineReason.ContentChanged);

            var directory = Path.GetDirectoryName(entry.OriginalPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return Refuse(QuarantineReason.NotFound);
            using var target = OpenDirectory(directory, out var targetReason);
            if (target is null) return Refuse(targetReason);
            if (VolumeOf(target) != info.VolumeSerial) return Refuse(QuarantineReason.DifferentVolume);

            // ReplaceIfExists is false: a newer file at the original place is never overwritten.
            if (!RenameInto(file, directory, Path.GetFileName(entry.OriginalPath), out var error))
                return error == ErrorAlreadyExists || error == ErrorFileExists ? Refuse(QuarantineReason.DestinationExists) : Failed(error);
            File.Delete(ManifestPath(id));
            return new(QuarantineStatus.Restored, QuarantineReason.None, entry);
        }
        catch (IOException) { return new(QuarantineStatus.Failed, QuarantineReason.Locked, null); }
        catch (UnauthorizedAccessException) { return new(QuarantineStatus.Failed, QuarantineReason.AccessDenied, null); }
    }

    /// <summary>
    /// Deletes the content and the manifest of one entry. Both paths are built from the id inside the quarantine folder; nothing from a manifest is used. The quarantine folder can be changed
    /// by the user, so the delete works like the cleanup remover: the folder is held open (it and its parents cannot be renamed or deleted meanwhile) and must be the folder itself, and each
    /// file is opened without following links and deleted through that handle only when it is a regular file whose final path is the expected one. A link or a path that leads elsewhere
    /// is refused and left as it is; a hard link loses only its name in this folder.
    /// </summary>
    public QuarantineResult Delete(Guid id)
    {
        try
        {
            var entry = ReadManifest(id);
            if (!Directory.Exists(root)) return Refuse(QuarantineReason.NotFound);
            using var folder = OpenDirectory(root, out var folderReason);
            if (folder is null) return Refuse(folderReason);
            if (Directory.Exists(FolderPath(id)))
            {
                var tree = RemoveTree(FolderPath(id), 0);
                if (tree != QuarantineReason.None) return Refuse(tree);
                var folderManifest = RemoveChecked(ManifestPath(id));
                if (folderManifest is not (QuarantineReason.None or QuarantineReason.NotFound)) return Refuse(folderManifest);
                return new(QuarantineStatus.Deleted, QuarantineReason.None, entry);
            }
            var content = RemoveChecked(ContentPath(id));
            if (content is not (QuarantineReason.None or QuarantineReason.NotFound)) return Refuse(content);
            var manifest = RemoveChecked(ManifestPath(id));
            if (manifest is not (QuarantineReason.None or QuarantineReason.NotFound)) return Refuse(manifest);
            if (content == QuarantineReason.NotFound && manifest == QuarantineReason.NotFound) return Refuse(QuarantineReason.NotFound);
            return new(QuarantineStatus.Deleted, QuarantineReason.None, entry);
        }
        catch (IOException) { return new(QuarantineStatus.Failed, QuarantineReason.Locked, null); }
        catch (UnauthorizedAccessException) { return new(QuarantineStatus.Failed, QuarantineReason.AccessDenied, null); }
    }

    public IReadOnlyList<QuarantineEntry> List()
    {
        if (!Directory.Exists(root)) return [];
        var result = new List<QuarantineEntry>();
        foreach (var file in Directory.EnumerateFiles(root, "*" + ManifestExtension))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) && ReadManifest(id) is { } entry)
                result.Add(entry with { ContentMissing = entry.IsFolder ? !Directory.Exists(FolderPath(id)) : !File.Exists(ContentPath(id)) });
        // Content whose manifest is gone (a crash, or someone deleted it): listed without an origin so it can be deleted or purged; it cannot be restored.
        var known = result.Select(e => e.Id).ToHashSet();
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var extension = Path.GetExtension(path);
            if (!string.Equals(extension, ContentExtension, StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, FolderExtension, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || known.Contains(id)) continue;
            DateTime created;
            try { created = File.GetCreationTimeUtc(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { created = DateTime.UtcNow; }
            result.Add(new(id, "", new FileFingerprint(0, 0, 0), "", new DateTimeOffset(created, TimeSpan.Zero), null, string.Equals(extension, FolderExtension, StringComparison.OrdinalIgnoreCase)));
        }
        return result.OrderBy(e => e.QuarantinedAtUtc).ToList();
    }

    // ---- folders -------------------------------------------------------------------------------

    /// <summary>
    /// Moves a whole folder into quarantine. The folder is opened without following a reparse point and held open, so it cannot be renamed or swapped meanwhile; its final path must be
    /// the requested one (no folder above it is a link). Everything in it is walked again: one link or junction anywhere inside, or too many entries, and nothing moves. The manifest is
    /// written first; then the folder is renamed by its handle into the quarantine folder on the same volume, and the new place is checked through the same handle.
    /// </summary>
    public QuarantineResult MoveFolder(string path, string program, CancellationToken cancellationToken, Guid? itemId = null)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return Refuse(QuarantineReason.PathIndirection);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (Path.GetDirectoryName(full) is not { } parent || string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), StringComparison.OrdinalIgnoreCase))
                return Refuse(QuarantineReason.PlaceNotAllowed);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || Inside(full, root) || Inside(root, full)) return Refuse(QuarantineReason.PlaceNotAllowed);
            using var folder = OpenFolderForRename(full, out var info, out var reason);
            if (folder is null) return Refuse(reason);
            var walk = Walk(full, cancellationToken);
            if (walk.Reason != QuarantineReason.None) return Refuse(walk.Reason);

            Directory.CreateDirectory(root);
            using var quarantineFolder = OpenDirectory(root, out var folderReason);
            if (quarantineFolder is null) return Refuse(folderReason);
            if (VolumeOf(quarantineFolder) != info.VolumeSerialNumber) return Refuse(QuarantineReason.DifferentVolume);

            var id = itemId ?? Guid.NewGuid();
            var fingerprint = new FileFingerprint(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, walk.Bytes);
            var entry = new QuarantineEntry(id, full, fingerprint, "", clock.GetUtcNow(), string.IsNullOrWhiteSpace(program) ? null : program.Trim(), IsFolder: true, Files: walk.Files);
            // The manifest is written and flushed to disk first: a crash between the two steps leaves a manifest without content, never content without a way back.
            WriteManifest(id, entry);
            if (!RenameInto(folder, root, id.ToString("N") + FolderExtension, out var error))
            {
                File.Delete(ManifestPath(id));
                return Failed(error);
            }
            return new(QuarantineStatus.Moved, QuarantineReason.None, entry);
        }
        catch (IOException) { return new(QuarantineStatus.Failed, QuarantineReason.Locked, null); }
        catch (UnauthorizedAccessException) { return new(QuarantineStatus.Failed, QuarantineReason.AccessDenied, null); }
    }

    /// <summary>Puts a folder back under its original name. A file or folder now at that place is never replaced, and a missing parent folder is not created.</summary>
    private QuarantineResult RestoreFolder(QuarantineEntry entry)
    {
        var content = FolderPath(entry.Id);
        if (!Directory.Exists(content)) return Refuse(QuarantineReason.NotFound);
        using var folder = OpenFolderForRename(content, out var info, out var reason);
        if (folder is null) return Refuse(reason);
        if (info.VolumeSerialNumber != entry.Fingerprint.VolumeSerial || (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) != entry.Fingerprint.FileIndex) return Refuse(QuarantineReason.IdentityChanged);

        var original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.OriginalPath));
        if (File.Exists(original) || Directory.Exists(original)) return Refuse(QuarantineReason.DestinationExists);
        var parent = Path.GetDirectoryName(original);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return Refuse(QuarantineReason.PlaceMissing);
        using var target = OpenDirectory(parent, out var targetReason);
        if (target is null) return Refuse(targetReason);
        if (VolumeOf(target) != info.VolumeSerialNumber) return Refuse(QuarantineReason.DifferentVolume);
        // ReplaceIfExists is false: something that appeared at the original place meanwhile is never overwritten.
        if (!RenameInto(folder, parent, Path.GetFileName(original), out var error))
            return error == ErrorAlreadyExists || error == ErrorFileExists ? Refuse(QuarantineReason.DestinationExists) : Failed(error);
        File.Delete(ManifestPath(entry.Id));
        return new(QuarantineStatus.Restored, QuarantineReason.None, entry);
    }

    private readonly record struct WalkResult(string Reason, long Bytes, int Files);

    private static readonly EnumerationOptions OneLevel = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };

    /// <summary>Counts the files and bytes of a folder without entering any link. The first link or junction, or too many entries or levels, stops the walk with a reason.</summary>
    private static WalkResult Walk(string folder, CancellationToken cancellationToken)
    {
        long bytes = 0; int files = 0, entries = 0;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((folder, 0));
        while (pending.TryPop(out var current))
        {
            foreach (var item in new DirectoryInfo(current.Path).EnumerateFileSystemInfos("*", OneLevel))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > LeftoverFolderScanner.MaxEntries) return new(QuarantineReason.TooManyEntries, bytes, files);
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new(QuarantineReason.LinkInside, bytes, files);
                if (item is DirectoryInfo)
                {
                    if (current.Depth + 1 >= LeftoverFolderScanner.MaxDepth) return new(QuarantineReason.TooManyEntries, bytes, files);
                    pending.Push((item.FullName, current.Depth + 1));
                }
                else if (item is FileInfo file) { files++; bytes += file.Length; }
            }
        }
        return new(QuarantineReason.None, bytes, files);
    }

    /// <summary>Opens a folder with the right to rename it, without following a reparse point; others cannot rename or delete it while the handle is open. Its final path must be the requested one.</summary>
    private static SafeFileHandle? OpenFolderForRename(string path, out ByHandleFileInformation info, out string reason)
    {
        info = default; reason = QuarantineReason.None;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var handle = CreateFile(@"\\?\" + full, DeleteAccess | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            reason = error is ErrorFileNotFound or ErrorPathNotFound ? QuarantineReason.NotFound : error is ErrorSharingViolation or ErrorLockViolation ? QuarantineReason.Locked
                : error == ErrorAccessDenied ? QuarantineReason.AccessDenied : QuarantineReason.Unexpected;
            handle.Dispose();
            return null;
        }
        if (!GetFileInformationByHandle(handle, out info)) { handle.Dispose(); reason = QuarantineReason.Unexpected; return null; }
        if ((info.FileAttributes & AttributeReparsePoint) != 0) { handle.Dispose(); reason = QuarantineReason.ReparsePoint; return null; }
        if ((info.FileAttributes & AttributeDirectory) == 0) { handle.Dispose(); reason = QuarantineReason.NotARegularFile; return null; }
        var final = FinalPath(handle);
        if (final is null || !string.Equals(Path.TrimEndingDirectorySeparator(final), full, StringComparison.OrdinalIgnoreCase)) { handle.Dispose(); reason = QuarantineReason.PathIndirection; return null; }
        return handle;
    }

    /// <summary>
    /// Deletes a quarantined folder for good, depth first, each entry through its own handle opened without following links. A link or junction found inside is removed as a link: its
    /// target is never entered or touched. Each folder stays open (it cannot be renamed) while its entries are deleted, and each entry's final path must be the expected one.
    /// A missing entry counts as done.
    /// </summary>
    private static string RemoveTree(string path, int depth)
    {
        if (depth > LeftoverFolderScanner.MaxDepth) return QuarantineReason.TooManyEntries;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        using var handle = CreateFile(@"\\?\" + full, DeleteAccess | FileReadAttributes | FileWriteAttributes | Synchronize, FileShareRead | FileShareWrite, 0, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error is ErrorFileNotFound or ErrorPathNotFound ? QuarantineReason.None : error is ErrorSharingViolation or ErrorLockViolation ? QuarantineReason.Locked
                : error == ErrorAccessDenied ? QuarantineReason.AccessDenied : QuarantineReason.Unexpected;
        }
        if (!GetFileInformationByHandle(handle, out var info)) return QuarantineReason.Unexpected;
        if (FinalPath(handle) is not { } final || !string.Equals(Path.TrimEndingDirectorySeparator(final), full, StringComparison.OrdinalIgnoreCase)) return QuarantineReason.PathIndirection;
        if ((info.FileAttributes & AttributeReparsePoint) == 0 && (info.FileAttributes & AttributeDirectory) != 0)
        {
            var count = 0;
            foreach (var child in Directory.EnumerateFileSystemEntries(full, "*", OneLevel).ToList())
            {
                if (++count > LeftoverFolderScanner.MaxEntries) return QuarantineReason.TooManyEntries;
                var reason = RemoveTree(child, depth + 1);
                if (reason != QuarantineReason.None) return reason;
            }
        }
        if ((info.FileAttributes & AttributeReadOnly) != 0 && !ClearReadOnly(handle, info.FileAttributes)) return QuarantineReason.AccessDenied;
        return MarkDeleted(handle);
    }

    /// <summary>A read-only entry cannot be marked for deletion; its attribute is cleared through the same handle first (FILE_BASIC_INFO, zero times stay unchanged).</summary>
    private static bool ClearReadOnly(SafeFileHandle handle, uint attributes)
    {
        const int size = 40;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            var value = attributes & ~AttributeReadOnly;
            Marshal.WriteInt32(buffer, 32, (int)(value == 0 ? AttributeNormal : value));
            return SetFileInformationByHandle(handle, FileBasicInfoClass, buffer, size);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool Inside(string path, string folder) =>
        path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] == '\\';

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>The bytes of the single files in quarantine. A folder is renamed, never copied, so it does not count against the limit of single files.</summary>
    private long Total()
    {
        long total = 0;
        foreach (var e in List().Where(e => !e.IsFolder)) total += e.Fingerprint.Size;
        return total;
    }

    private const int MaxManifestBytes = 64 * 1024;

    /// <summary>The manifest folder is writable by the user, so a manifest is read through a bounded stream: a huge file cannot exhaust memory of this process.</summary>
    private static string ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxManifestBytes) throw new IOException("The manifest is too large.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Writes the manifest through a stream and forces it to disk, so a rename that survives a power loss never outlives the manifest that names the original place.</summary>
    private void WriteManifest(Guid id, QuarantineEntry entry)
    {
        using var stream = new FileStream(ManifestPath(id), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry))).ToArray());
        stream.Flush(flushToDisk: true);
    }

    private string ManifestPath(Guid id) => Path.Combine(root, id.ToString("N") + ManifestExtension);
    private string ContentPath(Guid id) => Path.Combine(root, id.ToString("N") + ContentExtension);
    private string FolderPath(Guid id) => Path.Combine(root, id.ToString("N") + FolderExtension);

    private QuarantineEntry? ReadManifest(Guid id)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<QuarantineEntry>(ReadBounded(ManifestPath(id)));
            return entry is not null && entry.Id == id && Path.IsPathFullyQualified(entry.OriginalPath) ? entry : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static QuarantineResult Refuse(string reason) =>
        new(reason is QuarantineReason.Locked or QuarantineReason.AccessDenied or QuarantineReason.Unexpected ? QuarantineStatus.Failed : QuarantineStatus.Refused, reason, null);
    private static QuarantineResult Failed(int error) =>
        new(QuarantineStatus.Failed, error is ErrorSharingViolation or ErrorLockViolation ? QuarantineReason.Locked : error == ErrorAccessDenied ? QuarantineReason.AccessDenied : error == ErrorPathIndirection ? QuarantineReason.PathIndirection : QuarantineReason.Unexpected, null);

    private static string Hash(SafeFileHandle file, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long offset = 0;
        int read;
        while ((read = RandomAccess.Read(file, buffer, offset)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
            offset += read;
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Opens a regular file without following reparse points and returns null with a reason when any rule fails. The handle can be renamed and read.</summary>
    private static SafeFileHandle? OpenChecked(string path, out FileFingerprint fingerprint, out string reason, out string? canonical)
    {
        fingerprint = new(0, 0, 0); canonical = null; reason = QuarantineReason.None;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) { reason = QuarantineReason.PathIndirection; return null; }
        var full = Path.GetFullPath(path);
        var handle = CreateFile(@"\\?\" + full, GenericRead | DeleteAccess | FileReadAttributes, FileShareRead, 0, OpenExisting, FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            reason = error is ErrorFileNotFound or ErrorPathNotFound ? QuarantineReason.NotFound
                : error is ErrorSharingViolation or ErrorLockViolation ? QuarantineReason.Locked
                : error == ErrorAccessDenied ? (Directory.Exists(full) ? QuarantineReason.NotARegularFile : QuarantineReason.AccessDenied) : QuarantineReason.Unexpected;
            handle.Dispose();
            return null;
        }
        if (!GetFileInformationByHandle(handle, out var info)) { handle.Dispose(); reason = QuarantineReason.Unexpected; return null; }
        if ((info.FileAttributes & AttributeReparsePoint) != 0) { handle.Dispose(); reason = QuarantineReason.ReparsePoint; return null; }
        if ((info.FileAttributes & AttributeDirectory) != 0) { handle.Dispose(); reason = QuarantineReason.NotARegularFile; return null; }
        if (info.NumberOfLinks != 1) { handle.Dispose(); reason = QuarantineReason.HardLinked; return null; }
        var final = FinalPath(handle);
        if (final is null || !string.Equals(final, full, StringComparison.OrdinalIgnoreCase)) { handle.Dispose(); reason = QuarantineReason.PathIndirection; return null; }
        fingerprint = new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, ((long)info.FileSizeHigh << 32) | info.FileSizeLow);
        canonical = final;
        return handle;
    }

    /// <summary>Opens a directory without following a reparse point; its final path must equal the requested one. Deleting or renaming the folder (or a parent of it) is denied while the handle is open, so it cannot be swapped for a link before the move.</summary>
    private static SafeFileHandle? OpenDirectory(string path, out string reason)
    {
        reason = QuarantineReason.None;
        var full = Path.GetFullPath(path);
        var handle = CreateFile(@"\\?\" + full, FileAddFile | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, 0, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid) { reason = Marshal.GetLastWin32Error() is ErrorFileNotFound or ErrorPathNotFound ? QuarantineReason.NotFound : QuarantineReason.AccessDenied; handle.Dispose(); return null; }
        if (!GetFileInformationByHandle(handle, out var info) || (info.FileAttributes & AttributeDirectory) == 0) { handle.Dispose(); reason = QuarantineReason.NotARegularFile; return null; }
        if ((info.FileAttributes & AttributeReparsePoint) != 0) { handle.Dispose(); reason = QuarantineReason.ReparsePoint; return null; }
        var final = FinalPath(handle);
        if (final is null || !string.Equals(final.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { handle.Dispose(); reason = QuarantineReason.PathIndirection; return null; }
        return handle;
    }

    /// <summary>Deletes one file of the quarantine folder through its own handle after the checks of <see cref="Delete"/>; returns the reason it was not deleted, or None.</summary>
    private static string RemoveChecked(string path)
    {
        var full = Path.GetFullPath(path);
        using var handle = CreateFile(@"\\?\" + full, DeleteAccess | FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, 0, OpenExisting, FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error is ErrorFileNotFound or ErrorPathNotFound ? QuarantineReason.NotFound
                : error is ErrorSharingViolation or ErrorLockViolation ? QuarantineReason.Locked
                : error == ErrorAccessDenied ? QuarantineReason.AccessDenied : QuarantineReason.Unexpected;
        }
        if (!GetFileInformationByHandle(handle, out var info)) return QuarantineReason.Unexpected;
        if ((info.FileAttributes & AttributeReparsePoint) != 0) return QuarantineReason.ReparsePoint;
        if ((info.FileAttributes & AttributeDirectory) != 0) return QuarantineReason.NotARegularFile;
        // A hard link is removed as a name of this folder: its other names and its content stay.
        if (!string.Equals(FinalPath(handle), full, StringComparison.OrdinalIgnoreCase)) return QuarantineReason.PathIndirection;
        return MarkDeleted(handle);
    }

    private static string MarkDeleted(SafeFileHandle handle)
    {
        var flag = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(flag, 1); // the delete flag of FILE_DISPOSITION_INFO
            if (SetFileInformationByHandle(handle, FileDispositionInfoClass, flag, 4)) return QuarantineReason.None;
            var error = Marshal.GetLastWin32Error();
            return error is ErrorSharingViolation or ErrorLockViolation ? QuarantineReason.Locked : error == ErrorAccessDenied ? QuarantineReason.AccessDenied : QuarantineReason.Unexpected;
        }
        finally { Marshal.FreeHGlobal(flag); }
    }

    private static uint VolumeOf(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var info) ? info.VolumeSerialNumber : 0;

    private static string? FinalPath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
        if (length == 0 || length >= builder.Capacity) return null;
        var text = builder.ToString();
        return text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? null : text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
    }

    /// <summary>
    /// Renames the open file to <paramref name="folderPath"/>\<paramref name="name"/> (FILE_RENAME_INFO, the destination as a full path) and then checks through the file's own
    /// handle that it really ended up there. An existing target is never replaced. The folder handle stays open meanwhile, so the folder cannot be swapped for a link
    /// in a way that this check would miss.
    /// </summary>
    private static bool RenameInto(SafeFileHandle file, string folderPath, string name, out int error)
    {
        var destination = Path.Combine(Path.GetFullPath(folderPath), name);
        var nameBytes = Encoding.Unicode.GetBytes(@"\\?\" + destination);
        var size = 20 + nameBytes.Length + 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, 0);                                          // ReplaceIfExists = false
            Marshal.WriteInt32(buffer, 16, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, buffer + 20, nameBytes.Length);
            var ok = SetFileInformationByHandle(file, FileRenameInfoClass, buffer, (uint)size);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok && !string.Equals(FinalPath(file), destination, StringComparison.OrdinalIgnoreCase)) { error = ErrorPathIndirection; return false; }
            return ok;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ---- Win32 -------------------------------------------------------------------------------------

    private const uint GenericRead = 0x80000000, DeleteAccess = 0x00010000, FileReadAttributes = 0x0080, FileWriteAttributes = 0x0100, FileAddFile = 0x0002, Synchronize = 0x00100000;
    private const uint FileShareRead = 1, FileShareWrite = 2, FileShareDelete = 4, OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000, FileFlagBackupSemantics = 0x02000000;
    private const uint AttributeReadOnly = 0x1, AttributeDirectory = 0x10, AttributeNormal = 0x80, AttributeReparsePoint = 0x400;
    private const int FileBasicInfoClass = 0, FileRenameInfoClass = 3, FileDispositionInfoClass = 4;
    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5, ErrorSharingViolation = 32, ErrorLockViolation = 33, ErrorFileExists = 80, ErrorAlreadyExists = 183, ErrorPathIndirection = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, nint information, uint size);
}
