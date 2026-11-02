// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.Win32.SafeHandles;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// The image rule for services (BACKLOG 4.4, DEC-IW-033): a service may be changed only when its executable is a file inside the Windows folder that carries a valid Microsoft
/// signature, embedded or through a Microsoft-signed catalog. Nothing is launched and nothing is written; the file is opened for reading only.
/// </summary>
public static unsafe class ServiceImage
{
    private const int CatalogInfoSize = 4 + 260 * 2;

    /// <summary>The Windows folder, read from the system (for example <c>C:\Windows</c>), without a trailing separator.</summary>
    public static string WindowsDirectory()
    {
        var buffer = stackalloc char[260];
        var length = Native.GetSystemWindowsDirectoryW(buffer, 260);
        return length is 0 or >= 260 ? "" : new string(buffer, 0, (int)length).TrimEnd('\\');
    }

    /// <summary>
    /// The executable named by a service binary path: the quoted part, or the text up to the first <c>.exe</c>; <c>\SystemRoot\</c>, <c>%SystemRoot%\</c> and <c>%windir%\</c> are replaced
    /// by <paramref name="windowsDirectory"/>. Null when the text names no absolute drive path.
    /// </summary>
    public static string? ExtractExecutable(string binaryPath, string windowsDirectory)
    {
        ArgumentNullException.ThrowIfNull(binaryPath);
        var text = binaryPath.Trim();
        if (text.Length is 0 or > 1024) return null;
        string candidate;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 2) return null;
            candidate = text[1..end];
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0) return null;
            candidate = text[..(exe + 4)];
        }
        foreach (var prefix in new[] { @"\SystemRoot\", @"%SystemRoot%\", @"%windir%\" })
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && windowsDirectory.Length > 0)
            {
                candidate = windowsDirectory + "\\" + candidate[prefix.Length..];
                break;
            }
        if (candidate.Length < 4 || !char.IsAsciiLetter(candidate[0]) || candidate[1] != ':' || candidate[2] != '\\') return null;
        if (candidate.Contains("..", StringComparison.Ordinal) || candidate.Contains('%') || !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return candidate;
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="windowsDirectory"/> (a real subpath, case-insensitive).</summary>
    public static bool IsUnder(string path, string windowsDirectory) =>
        windowsDirectory.Length > 0 && path.StartsWith(windowsDirectory + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the leaf certificate is issued to Microsoft Corporation and the chain ends at a Microsoft root.</summary>
    public static bool IsMicrosoftChain(SignatureEvidence evidence) =>
        evidence.IsValid && evidence.Chain.Count >= 2 && evidence.Leaf is { } leaf && evidence.Root is { } root
        && leaf.Subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) && root.Subject.Contains("CN=Microsoft Root", StringComparison.OrdinalIgnoreCase);

    /// <summary>The image rule for the binary path of a service.</summary>
    public static bool IsMicrosoftSignedInWindows(string binaryPath)
    {
        var windows = WindowsDirectory();
        var path = ExtractExecutable(binaryPath, windows);
        if (path is null || !IsUnder(path, windows)) return false;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (FinalPath(handle) is not { } final || !IsUnder(final, windows)) return false;
            var embedded = AuthenticodeVerifier.Verify(handle);
            if (embedded.IsValid) return IsMicrosoftChain(embedded);
            return IsInMicrosoftCatalog(handle, windows);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = stackalloc char[1024];
        var length = Native.GetFinalPathNameByHandleW(handle, buffer, 1024, 0);
        if (length is 0 or >= 1024) return null;
        var text = new string(buffer, 0, (int)length);
        return text.StartsWith(@"\\?\", StringComparison.Ordinal) && !text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? text[4..] : null;
    }

    /// <summary>The file hash is listed in an installed catalog, and that catalog (inside the Windows folder) has a valid Microsoft signature.</summary>
    private static bool IsInMicrosoftCatalog(SafeFileHandle file, string windows)
    {
        if (!Native.CryptCATAdminAcquireContext2(out var admin, 0, "SHA256", 0, 0)) return false;
        nint current = 0;
        try
        {
            uint size = 0;
            Native.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
            if (size is 0 or > 128) return false;
            var hash = stackalloc byte[(int)size];
            if (!Native.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0)) return false;
            var info = stackalloc byte[CatalogInfoSize];
            for (var i = 0; i < 8; i++)
            {
                var previous = current;
                current = Native.CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, &previous);
                if (current == 0) return false;
                new Span<byte>(info, CatalogInfoSize).Clear();
                *(int*)info = CatalogInfoSize;
                if (!Native.CryptCATCatalogInfoFromContext(current, info, 0)) continue;
                var catalog = new string((char*)(info + 4)).TrimEnd('\0');
                if (!IsUnder(catalog, windows)) continue;
                using var catalogHandle = File.OpenHandle(catalog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (IsMicrosoftChain(AuthenticodeVerifier.Verify(catalogHandle))) return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            if (current != 0) Native.CryptCATAdminReleaseCatalogContext(admin, current, 0);
            Native.CryptCATAdminReleaseContext(admin, 0);
        }
    }
}
