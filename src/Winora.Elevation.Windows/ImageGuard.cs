// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Winora.Domain;

namespace Winora.Elevation.Windows;

public enum ImageGuardStage { Path, Volume, Walk, Open, FinalPath, Acl, Hash, Signature }

/// <summary>One failed check. <c>Code</c> and <c>Detail</c> are Domain tokens; <c>Subject</c> is a diagnostic path or principal, never shown to users.</summary>
public sealed record ImageGuardFinding(ImageGuardStage Stage, string Code, string Detail, string Subject, int Win32Error = 0);

public sealed class ImageGuardOptions
{
    /// <summary>
    /// The install root. Components strictly above it are judged with <see cref="WriteRightsTier.ReplaceOnly"/> (a default C:\ root lets
    /// Authenticated Users create folders, which cannot replace Program Files); the root and everything below use <see cref="WriteRightsTier.Full"/>.
    /// Null means every component is judged with the full rule.
    /// </summary>
    public string? TrustBoundaryDirectory { get; init; }
    /// <summary>Expected SHA-256 of the file (32 bytes). Null skips the comparison; the hash is still computed and returned.</summary>
    public byte[]? ExpectedSha256 { get; init; }
    public bool RequireSignature { get; init; } = true;
    public bool RequireFixedNtfsVolume { get; init; } = true;
    public IAclReader? AclReader { get; init; }
    public TimeSpan? SignatureTimeout { get; init; }
}

/// <summary>Volume serial and 128-bit file id from FileIdInfo (section 4.3 step 6).</summary>
public sealed record FileIdentity(ulong VolumeSerial, string FileIdHex, uint LinkCount);

/// <summary>
/// Result of the guard. When trusted, <see cref="HeldHandle"/> is the open-once handle (read, share-read only) that the caller must keep until
/// the key exchange completed and then dispose. The handle is also kept for untrusted verdicts so the caller can inspect it; Dispose closes it.
/// </summary>
public sealed class ImageGuardResult : IDisposable
{
    internal ImageGuardResult(IReadOnlyList<ImageGuardFinding> findings, string? finalPath, FileIdentity? identity, byte[]? sha256, SignatureEvidence? signature, SafeFileHandle? held)
    { Findings = findings; FinalPath = finalPath; Identity = identity; Sha256 = sha256; Signature = signature; HeldHandle = held; }

    public IReadOnlyList<ImageGuardFinding> Findings { get; }
    public bool Trusted => Findings.Count == 0;
    /// <summary>Domain code of the verdict (<c>elevation.install_untrusted</c>) or null when trusted.</summary>
    public string? Code => Findings.Count == 0 ? null : Findings[0].Code;
    /// <summary>Detail token of the first finding (acl, reparse, location, volume, chain_offline, hash, signer).</summary>
    public string? Detail => Findings.Count == 0 ? null : Findings[0].Detail;
    public string? FinalPath { get; }
    public FileIdentity? Identity { get; }
    public byte[]? Sha256 { get; }
    public SignatureEvidence? Signature { get; }
    public SafeFileHandle? HeldHandle { get; }
    public void Dispose() => HeldHandle?.Dispose();
}

/// <summary>
/// Pre-launch image guard: open once with share-read only, walk from the volume root rejecting reparse points,
/// evaluate effective write ACLs, hash and verify Authenticode on the SAME handle. It only reads files.
/// </summary>
public static class ImageGuard
{
    private const uint GenericRead = 0x80000000, ReadControl = 0x20000, FileReadAttributes = 0x80;
    private const uint ShareRead = 1, ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000, FileFlagOpenReparsePoint = 0x00200000;
    private const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400;

    public static ImageGuardResult Verify(string path, ImageGuardOptions? options = null)
    {
        options ??= new ImageGuardOptions();
        var findings = new List<ImageGuardFinding>();
        if (!OperatingSystem.IsWindows()) return Done(findings, "location", ImageGuardStage.Path, null, null, null, null, null);
        if (!TrySplit(path, out var root, out var components)) return Done(findings, "location", ImageGuardStage.Path, path, null, null, null, null);
        var aclReader = options.AclReader ?? new WindowsAclReader();
        string? boundary = options.TrustBoundaryDirectory;
        if (boundary is not null && !TrySplit(boundary, out _, out _)) return Done(findings, "location", ImageGuardStage.Path, boundary, null, null, null, null);

        // Volume rule: fixed local NTFS drive letter.
        if (options.RequireFixedNtfsVolume && !IsFixedNtfs(root)) findings.Add(Fail(ImageGuardStage.Volume, "volume", root));

        // Walk from the volume root, one directory handle at a time, rejecting reparse points and judging ACLs.
        string current = root;
        for (int i = -1; i < components.Length - 1; i++)
        {
            if (i >= 0) current = current.TrimEnd('\\') + "\\" + components[i];
            using var dir = OpenNoFollow(current, ReadControl | FileReadAttributes, ShareAll, out int openError);
            if (dir is null) { findings.Add(Fail(ImageGuardStage.Walk, "location", current, openError)); return Done(findings, null, null, null, null, null, null, null); }
            if (!GetInfo(dir, out var info)) { findings.Add(Fail(ImageGuardStage.Walk, "location", current, Marshal.GetLastPInvokeError())); return Done(findings, null, null, null, null, null, null, null); }
            if ((info.FileAttributes & AttributeReparsePoint) != 0) { findings.Add(Fail(ImageGuardStage.Walk, "reparse", current)); return Done(findings, null, null, null, null, null, null, null); }
            if ((info.FileAttributes & AttributeDirectory) == 0) { findings.Add(Fail(ImageGuardStage.Walk, "location", current)); return Done(findings, null, null, null, null, null, null, null); }
            var tier = boundary is not null && IsStrictAncestor(current, boundary) ? WriteRightsTier.ReplaceOnly : WriteRightsTier.Full;
            JudgeAcl(findings, aclReader, dir, current, tier);
        }

        // Open the file ONCE: read access, share read only (no write or delete sharing). No reparse following.
        string full = components.Length == 0 ? root : current.TrimEnd('\\') + "\\" + components[^1];
        var held = OpenNoFollow(full, GenericRead, ShareRead, out int err);
        if (held is null) { findings.Add(Fail(ImageGuardStage.Open, "location", full, err)); return Done(findings, null, null, null, null, null, null, null); }
        bool keep = false;
        try
        {
            if (!GetInfo(held, out var fileInfo)) { findings.Add(Fail(ImageGuardStage.Open, "location", full, Marshal.GetLastPInvokeError())); return Done(findings, null, null, null, null, null, null, null); }
            if ((fileInfo.FileAttributes & AttributeReparsePoint) != 0) { findings.Add(Fail(ImageGuardStage.Walk, "reparse", full)); return Done(findings, null, null, null, null, null, null, null); }
            if ((fileInfo.FileAttributes & AttributeDirectory) != 0) { findings.Add(Fail(ImageGuardStage.Open, "location", full)); return Done(findings, null, null, null, null, null, null, null); }

            string? finalPath = FinalPath(held);
            if (finalPath is null || !string.Equals(finalPath, full, StringComparison.OrdinalIgnoreCase))
                findings.Add(Fail(ImageGuardStage.FinalPath, "location", finalPath ?? full));

            JudgeAcl(findings, aclReader, held, full, WriteRightsTier.Full);

            FileIdentity? identity = ReadIdentity(held, fileInfo);

            byte[]? sha = HashHandle(held);
            if (sha is null) findings.Add(Fail(ImageGuardStage.Hash, "hash", full, Marshal.GetLastPInvokeError()));
            else if (options.ExpectedSha256 is not null && !CryptographicOperations.FixedTimeEquals(sha, options.ExpectedSha256)) findings.Add(Fail(ImageGuardStage.Hash, "hash", full));

            SignatureEvidence? signature = AuthenticodeVerifier.Verify(held, options.SignatureTimeout);
            if (options.RequireSignature && !signature.IsValid)
                findings.Add(Fail(ImageGuardStage.Signature, signature.Status == AuthenticodeStatus.ChainOffline || signature.Status == AuthenticodeStatus.Timeout ? "chain_offline" : "signer", full, signature.TrustResult));

            keep = true;
            return new ImageGuardResult(findings, finalPath, identity, sha, signature, held);
        }
        finally { if (!keep) held.Dispose(); }
    }

    /// <summary>
    /// Read-only audit of everything below a directory (section 4.3 step 4: sub-directories and files): reparse points and the full
    /// write rule on every entry. Returns the findings; empty means every entry passed. Never opens a file for write or follows a link.
    /// </summary>
    public static IReadOnlyList<ImageGuardFinding> AuditSubtree(string directory, IAclReader? aclReader = null, int maxEntries = 4096)
    {
        var findings = new List<ImageGuardFinding>();
        if (!OperatingSystem.IsWindows() || !TrySplit(directory, out _, out _)) { findings.Add(Fail(ImageGuardStage.Path, "location", directory)); return findings; }
        aclReader ??= new WindowsAclReader();
        var pending = new Stack<string>();
        pending.Push(directory.TrimEnd('\\'));
        int seen = 0;
        while (pending.Count > 0 && findings.Count < 64)
        {
            string dir = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { findings.Add(Fail(ImageGuardStage.Walk, "location", dir)); continue; }
            foreach (var entry in entries)
            {
                if (++seen > maxEntries) { findings.Add(Fail(ImageGuardStage.Walk, "location", directory)); return findings; }
                using var handle = OpenNoFollow(entry, ReadControl | FileReadAttributes, ShareAll, out int err);
                if (handle is null || !GetInfo(handle, out var info)) { findings.Add(Fail(ImageGuardStage.Walk, "location", entry, err)); continue; }
                if ((info.FileAttributes & AttributeReparsePoint) != 0) { findings.Add(Fail(ImageGuardStage.Walk, "reparse", entry)); continue; }
                JudgeAcl(findings, aclReader, handle, entry, WriteRightsTier.Full);
                if ((info.FileAttributes & AttributeDirectory) != 0) pending.Push(entry);
            }
        }
        return findings;
    }

    // ---- helpers (internal where tests reach them) ----

    internal static void JudgeAcl(List<ImageGuardFinding> findings, IAclReader reader, SafeFileHandle handle, string path, WriteRightsTier tier)
    {
        var read = reader.Read(handle, path);
        if (read.Status != AclReadStatus.Ok || read.Descriptor is null) { findings.Add(Fail(ImageGuardStage.Acl, "acl", path, read.Win32Error)); return; }
        foreach (var f in AclEvaluator.Evaluate(read.Descriptor, tier)) findings.Add(Fail(ImageGuardStage.Acl, "acl", path + "|" + f.Reason + "|" + f.Principal));
    }

    private static ImageGuardFinding Fail(ImageGuardStage stage, string detail, string subject, int error = 0) =>
        new(stage, ErrorCode.ElevationInstallUntrusted, detail, subject, error);

    private static ImageGuardResult Done(List<ImageGuardFinding> findings, string? detailIfAny, ImageGuardStage? stage, string? subject, string? finalPath, FileIdentity? identity, byte[]? sha, SignatureEvidence? sig)
    {
        if (detailIfAny is not null && stage is not null) findings.Add(Fail(stage.Value, detailIfAny, subject ?? ""));
        return new ImageGuardResult(findings, finalPath, identity, sha, sig, null);
    }

    /// <summary>Accepts only <c>X:\a\b</c>: ASCII drive letter, backslashes, no empty, dot or dot-dot component, no device or stream syntax.</summary>
    internal static bool TrySplit(string? path, out string root, out string[] components)
    {
        root = ""; components = Array.Empty<string>();
        if (path is null || path.Length < 3 || path.Length > 1000) return false;
        char d = path[0];
        if (!(d is >= 'A' and <= 'Z' or >= 'a' and <= 'z') || path[1] != ':' || path[2] != '\\') return false;
        if (path.Length == 3) { root = path; return true; }
        var parts = path[3..].TrimEnd('\\').Split('\\');
        foreach (var p in parts)
            if (p.Length == 0 || p is "." or ".." || p.IndexOfAny(new[] { '/', ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0 || p.EndsWith(' ') || p.EndsWith('.')) return false;
        root = path[..3]; components = parts;
        return true;
    }

    internal static bool IsStrictAncestor(string candidate, string boundary)
    {
        string c = candidate.TrimEnd('\\') + "\\", b = boundary.TrimEnd('\\') + "\\";
        return b.Length > c.Length && b.StartsWith(c, StringComparison.OrdinalIgnoreCase);
    }

    private static SafeFileHandle? OpenNoFollow(string path, uint access, uint share, out int error)
    {
        var h = Native.CreateFileW(@"\\?\" + path, access, share, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        error = Marshal.GetLastPInvokeError();
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }

    private static bool GetInfo(SafeFileHandle h, out ByHandleFileInformation info) => Native.GetFileInformationByHandle(h, out info);

    private static unsafe string? FinalPath(SafeFileHandle h)
    {
        char[] buf = new char[1024];
        fixed (char* p = buf)
        {
            uint n = Native.GetFinalPathNameByHandleW(h, p, (uint)buf.Length, 0); // VOLUME_NAME_DOS, normalized
            if (n == 0 || n >= buf.Length) return null;
            string s = new(p, 0, (int)n);
            return s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? null : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
        }
    }

    private static unsafe FileIdentity? ReadIdentity(SafeFileHandle h, ByHandleFileInformation info)
    {
        FileIdInfo id = default;
        if (!Native.GetFileInformationByHandleEx(h, 18, &id, (uint)sizeof(FileIdInfo))) return null;
        return new FileIdentity(id.VolumeSerialNumber, Convert.ToHexStringLower(new ReadOnlySpan<byte>(id.FileId, 16)), info.NumberOfLinks);
    }

    private static byte[]? HashHandle(SafeFileHandle h)
    {
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long offset = 0;
            int n;
            while ((n = RandomAccess.Read(h, buffer, offset)) > 0) { sha.AppendData(buffer, 0, n); offset += n; }
            return sha.GetHashAndReset();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    private static unsafe bool IsFixedNtfs(string root)
    {
        if (Native.GetDriveTypeW(root) != 3) return false; // DRIVE_FIXED
        char* fs = stackalloc char[32];
        return Native.GetVolumeInformationW(root, null, 0, null, null, null, fs, 32) && new string(fs) == "NTFS";
    }
}
