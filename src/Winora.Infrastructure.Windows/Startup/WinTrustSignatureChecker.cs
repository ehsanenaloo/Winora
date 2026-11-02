// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using Winora.Application;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// The only code that calls the Windows trust functions (a source scan test enforces it). It checks the digital signature of a file with WinVerifyTrust and the action
/// WINTRUST_ACTION_GENERIC_VERIFY_V2, first for a signature inside the file and, when there is none, for a signature in a Windows catalog (most Windows files are signed that way). The check uses no
/// network: revocation checking is off and certificate and revocation data come from the local cache only. It shows no window. It reads the file and never runs or loads it. The signer name is taken
/// from the leaf certificate of the chain that Windows verified. Results of the trust call map to four states: Valid, Invalid (a signature exists but the trust check failed; the class of the failure
/// is kept), Unsigned, and Unknown (no answer).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinTrustSignatureChecker : IFileSignatureChecker
{
    internal const long MaxFile = 512L * 1024 * 1024;
    internal const int MaxCertificateBytes = 65536;

    // WINTRUST_DATA values. The set is fixed: no user interface, no revocation check, cache-only retrieval of certificate data.
    internal const uint UiNone = 2;                       // WTD_UI_NONE
    internal const uint RevokeNone = 0;                   // WTD_REVOKE_NONE
    internal const uint ChoiceFile = 1;                   // WTD_CHOICE_FILE
    internal const uint ChoiceCatalog = 2;                // WTD_CHOICE_CATALOG
    internal const uint StateVerify = 1;                  // WTD_STATEACTION_VERIFY
    internal const uint StateClose = 2;                   // WTD_STATEACTION_CLOSE
    internal const uint RevocationCheckNone = 0x10;       // WTD_REVOCATION_CHECK_NONE
    internal const uint CacheOnlyUrlRetrieval = 0x1000;   // WTD_CACHE_ONLY_URL_RETRIEVAL
    internal const uint ProvFlags = RevocationCheckNone | CacheOnlyUrlRetrieval;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2

    // Results that mean "there is no signature of this kind here"; the catalog is tried next.
    internal const uint TrustNoSignature = 0x800B0100, TrustProviderUnknown = 0x800B0001, TrustSubjectFormUnknown = 0x800B0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustCatalogInfo
    {
        public uint cbStruct; public uint dwCatalogVersion; public IntPtr pcwszCatalogFilePath; public IntPtr pcwszMemberTag; public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile; public IntPtr pbCalculatedFileHash; public uint cbCalculatedFileHash; public IntPtr pcCatalogContext; public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice;
        public IntPtr pUnion; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo { public uint cbStruct; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr catAdmin, IntPtr subsystem, string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr catAdmin, SafeFileHandle file, ref uint hashSize, IntPtr hash, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, IntPtr hash, uint hashSize, uint flags, ref IntPtr previous);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CatalogInfo info, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);

    public SignatureInfo Check(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!ShellFileLocation.IsLocalFilePath(path)) return SignatureInfo.Unknown(SignatureProblem.CheckFailed);
            if (IsNetworkDrive(path)) return SignatureInfo.Unknown(SignatureProblem.CheckFailed);
            var info = new FileInfo(path);
            if (!info.Exists) return SignatureInfo.Unknown(SignatureProblem.FileMissing);
            if (info.Length > MaxFile) return SignatureInfo.Unknown(SignatureProblem.TooLarge);

            int hr;
            string? signer;
            using (var file = OpenForRead(path)) hr = VerifyFile(path, file, out signer);
            if (hr == 0) return SignatureInfo.Valid(signer, false);
            if (!IsNoSignature(hr)) return SignatureInfo.Invalid(Classify(hr), hr, false);

            cancellationToken.ThrowIfCancellationRequested();
            return VerifyByCatalog(path, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or DllNotFoundException or EntryPointNotFoundException or CryptographicException
            or ArgumentException or NotSupportedException or InvalidOperationException or OverflowException or System.Security.SecurityException or MarshalDirectiveException or SEHException)
        {
            return SignatureInfo.Unknown(SignatureProblem.CheckFailed);
        }
    }

    /// <summary>True for a drive letter that maps to a network share. Such a file is not read for the check, so the check stays a local one.</summary>
    internal static bool IsNetworkDrive(string path)
    {
        try { return path.Length >= 2 && char.IsAsciiLetter(path[0]) && new DriveInfo(path[..1]).DriveType == DriveType.Network; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static SafeFileHandle OpenForRead(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal static bool IsNoSignature(int hr) => unchecked((uint)hr) is TrustNoSignature or TrustProviderUnknown or TrustSubjectFormUnknown;

    /// <summary>The class of a failed trust result, for the plain-language text.</summary>
    internal static SignatureProblem Classify(int hr) => unchecked((uint)hr) switch
    {
        0x80096010 => SignatureProblem.Changed,                                  // TRUST_E_BAD_DIGEST
        0x800B0109 or 0x800B0112 or 0x800B010D => SignatureProblem.UntrustedPublisher, // CERT_E_UNTRUSTEDROOT, CERT_E_UNTRUSTEDCA, CERT_E_UNTRUSTEDTESTROOT
        0x800B0101 => SignatureProblem.Expired,                                  // CERT_E_EXPIRED
        0x800B010A => SignatureProblem.ChainIncomplete,                          // CERT_E_CHAINING
        0x800B0111 or 0x800B010C or 0x80092026 => SignatureProblem.Blocked,      // TRUST_E_EXPLICIT_DISTRUST, CERT_E_REVOKED, CRYPT_E_SECURITY_SETTINGS
        _ => SignatureProblem.Other,
    };

    // ---- signature inside the file ----

    private static int VerifyFile(string path, SafeFileHandle file, out string? signer)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var added = false;
        try
        {
            file.DangerousAddRef(ref added);
            Marshal.StructureToPtr(new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = pathPtr, hFile = file.DangerousGetHandle(), pgKnownSubject = IntPtr.Zero }, infoPtr, false);
            return Trust(ChoiceFile, infoPtr, out signer);
        }
        finally
        {
            if (added) file.DangerousRelease();
            Marshal.FreeHGlobal(infoPtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    // ---- signature in a catalog ----

    private static SignatureInfo VerifyByCatalog(string path, CancellationToken cancellationToken)
    {
        // Catalogs of current Windows files use SHA-256; older ones use SHA-1 (the default of the catalog admin context).
        foreach (var algorithm in new string?[] { "SHA256", null })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0) || admin == IntPtr.Zero) continue;
            try
            {
                var found = TryCatalog(admin, path);
                if (found is not null) return found;
            }
            finally { CryptCATAdminReleaseContext(admin, 0); }
        }
        return SignatureInfo.Unsigned;
    }

    private static SignatureInfo? TryCatalog(IntPtr admin, string path)
    {
        using var file = OpenForRead(path);
        var size = 0u;
        CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, IntPtr.Zero, 0);
        if (size is 0 or > 128) return null;
        var hashPtr = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hashPtr, 0)) return null;
            var hash = new byte[size];
            Marshal.Copy(hashPtr, hash, 0, (int)size);
            var previous = IntPtr.Zero;
            var catalog = CryptCATAdminEnumCatalogFromHash(admin, hashPtr, size, 0, ref previous);
            if (catalog == IntPtr.Zero) return null;
            try
            {
                var catalogInfo = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>(), wszCatalogFile = "" };
                if (!CryptCATCatalogInfoFromContext(catalog, ref catalogInfo, 0) || string.IsNullOrEmpty(catalogInfo.wszCatalogFile)) return null;
                var hr = VerifyMember(admin, catalogInfo.wszCatalogFile, path, hash, hashPtr, out var signer);
                return hr == 0 ? SignatureInfo.Valid(signer, true) : SignatureInfo.Invalid(Classify(hr), hr, true);
            }
            finally { CryptCATAdminReleaseCatalogContext(admin, catalog, 0); }
        }
        finally { Marshal.FreeHGlobal(hashPtr); }
    }

    private static int VerifyMember(IntPtr admin, string catalogFile, string path, byte[] hash, IntPtr hashPtr, out string? signer)
    {
        var catalogPtr = Marshal.StringToHGlobalUni(catalogFile);
        var tagPtr = Marshal.StringToHGlobalUni(Convert.ToHexString(hash));
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustCatalogInfo>());
        try
        {
            Marshal.StructureToPtr(new WinTrustCatalogInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(), dwCatalogVersion = 0, pcwszCatalogFilePath = catalogPtr, pcwszMemberTag = tagPtr, pcwszMemberFilePath = pathPtr,
                hMemberFile = IntPtr.Zero, pbCalculatedFileHash = hashPtr, cbCalculatedFileHash = (uint)hash.Length, pcCatalogContext = IntPtr.Zero, hCatAdmin = admin,
            }, infoPtr, false);
            return Trust(ChoiceCatalog, infoPtr, out signer);
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
            Marshal.FreeHGlobal(pathPtr);
            Marshal.FreeHGlobal(tagPtr);
            Marshal.FreeHGlobal(catalogPtr);
        }
    }

    // ---- the trust call ----

    // One verify call with the fixed flag set, the name of the signer when it succeeded, and the close call that frees the state Windows keeps.
    private static int Trust(uint choice, IntPtr unionPtr, out string? signer)
    {
        signer = null;
        var action = GenericVerifyV2;
        var dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        var state = IntPtr.Zero;
        var data = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(), dwUIChoice = UiNone, fdwRevocationChecks = RevokeNone, dwUnionChoice = choice, pUnion = unionPtr,
            dwStateAction = StateVerify, dwProvFlags = ProvFlags,
        };
        try
        {
            Marshal.StructureToPtr(data, dataPtr, false);
            var hr = WinVerifyTrust(new IntPtr(-1), ref action, dataPtr);
            data = Marshal.PtrToStructure<WinTrustData>(dataPtr);
            state = data.hWVTStateData;
            if (hr == 0 && state != IntPtr.Zero) signer = LeafName(state);
            return hr;
        }
        finally
        {
            if (state != IntPtr.Zero)
            {
                data.dwStateAction = StateClose;
                Marshal.StructureToPtr(data, dataPtr, false);
                WinVerifyTrust(new IntPtr(-1), ref action, dataPtr);
            }
            Marshal.FreeHGlobal(dataPtr);
        }
    }

    // The simple name of the leaf certificate of the chain Windows just verified (the certificate that signed the file or the catalog).
    private static string? LeafName(IntPtr state)
    {
        try
        {
            var provider = WTHelperProvDataFromStateData(state);
            if (provider == IntPtr.Zero) return null;
            var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return null;
            var chainCert = WTHelperGetProvCertFromChain(signer, 0);
            if (chainCert == IntPtr.Zero) return null;
            var context = Marshal.ReadIntPtr(chainCert, IntPtr.Size);        // CRYPT_PROVIDER_CERT.pCert
            if (context == IntPtr.Zero) return null;
            var encoded = Marshal.ReadIntPtr(context, IntPtr.Size);          // CERT_CONTEXT.pbCertEncoded
            var length = Marshal.ReadInt32(context, 2 * IntPtr.Size);        // CERT_CONTEXT.cbCertEncoded
            if (encoded == IntPtr.Zero || length <= 0 || length > MaxCertificateBytes) return null;
            var bytes = new byte[length];
            Marshal.Copy(encoded, bytes, 0, length);
            using var certificate = X509CertificateLoader.LoadCertificate(bytes);
            return Clean(certificate.GetNameInfo(X509NameType.SimpleName, false));
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or InvalidOperationException) { return null; }
    }

    internal static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = new string(name.Where(c => !char.IsControl(c)).Take(256).ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }
}
