// SPDX-License-Identifier: GPL-3.0-only
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Winora.Elevation.Windows;

/// <summary>The fixed WinVerifyTrust flag set of A.10. A test asserts these constants.</summary>
public static class AuthenticodeFlags
{
    public const uint UiNone = 2;                      // WTD_UI_NONE
    public const uint RevokeNone = 0;                  // WTD_REVOKE_NONE
    public const uint ChoiceFile = 1;                  // WTD_CHOICE_FILE
    public const uint StateActionVerify = 1;           // WTD_STATEACTION_VERIFY
    public const uint StateActionClose = 2;            // WTD_STATEACTION_CLOSE
    public const uint RevocationCheckNone = 0x10;      // WTD_REVOCATION_CHECK_NONE
    public const uint CacheOnlyUrlRetrieval = 0x1000;  // WTD_CACHE_ONLY_URL_RETRIEVAL
    public const uint DisableMd2Md4 = 0x2000;          // WTD_DISABLE_MD2_MD4
    public const uint SaferFlag = 0x100;               // WTD_SAFER_FLAG: must NOT be set
    public const uint ProvFlags = RevocationCheckNone | CacheOnlyUrlRetrieval | DisableMd2Md4;
    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2.</summary>
    public static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
}

public enum AuthenticodeStatus { Valid, NotSigned, Untrusted, ChainOffline, Timeout, Unsupported }

/// <summary>One certificate of the chain: subject, issuer and SHA-256 hashes.</summary>
public sealed record SignerCertificate(string Subject, string Issuer, string SpkiSha256Hex, string CertSha256Hex);

/// <summary>Plain-data Authenticode evidence. <c>Chain</c> is leaf first; <c>TrustResult</c> is the raw HRESULT returned by WinVerifyTrust.</summary>
public sealed record SignatureEvidence(AuthenticodeStatus Status, int TrustResult, IReadOnlyList<SignerCertificate> Chain)
{
    public SignerCertificate? Leaf => Chain.Count > 0 ? Chain[0] : null;
    /// <summary>The issuing intermediate whose SPKI hash pins; null for a self-issued leaf or a single-certificate chain.</summary>
    public SignerCertificate? Issuer => Chain.Count > 1 ? Chain[1] : null;
    public SignerCertificate? Root => Chain.Count > 0 ? Chain[^1] : null;
    public bool IsValid => Status == AuthenticodeStatus.Valid;
}

/// <summary>
/// WinVerifyTrust on an already open handle (so the verified bytes are the held bytes), with no revocation, cache-only retrieval and no UI.
/// The call runs on a worker thread and is bounded by a timeout so an offline machine cannot hang the caller.
/// </summary>
public static class AuthenticodeVerifier
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public static SignatureEvidence Verify(SafeFileHandle heldFile, TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows()) return new SignatureEvidence(AuthenticodeStatus.Unsupported, 0, Array.Empty<SignerCertificate>());
        SignatureEvidence? result = null;
        var worker = new Thread(() => result = VerifyCore(heldFile)) { IsBackground = true, Name = "Winora.AuthenticodeVerify" };
        worker.Start();
        if (!worker.Join(timeout ?? DefaultTimeout)) return new SignatureEvidence(AuthenticodeStatus.Timeout, 0, Array.Empty<SignerCertificate>());
        return result ?? new SignatureEvidence(AuthenticodeStatus.Untrusted, unchecked((int)0x80004005), Array.Empty<SignerCertificate>());
    }

    /// <summary>Maps the HRESULT of WinVerifyTrust: 0 valid; TRUST_E_NOSIGNATURE and friends not signed; chain or revocation retrieval problems offline; else untrusted.</summary>
    public static AuthenticodeStatus MapResult(int hresult) => unchecked((uint)hresult) switch
    {
        0 => AuthenticodeStatus.Valid,
        0x800B0100 or 0x800B0001 or 0x800B0003 => AuthenticodeStatus.NotSigned, // TRUST_E_NOSIGNATURE, PROVIDER_UNKNOWN, SUBJECT_FORM_UNKNOWN
        0x800B010A or 0x80092013 or 0x80092012 => AuthenticodeStatus.ChainOffline, // CERT_E_CHAINING (needs retrieval), REVOCATION_OFFLINE, NO_REVOCATION_CHECK
        _ => AuthenticodeStatus.Untrusted,
    };

    private static unsafe SignatureEvidence VerifyCore(SafeFileHandle heldFile)
    {
        var action = AuthenticodeFlags.GenericVerifyV2;
        bool added = false;
        heldFile.DangerousAddRef(ref added);
        WintrustData data = default;
        try
        {
            WintrustFileInfo file = new() { cbStruct = (uint)sizeof(WintrustFileInfo), hFile = heldFile.DangerousGetHandle() };
            data = new WintrustData
            {
                cbStruct = (uint)sizeof(WintrustData),
                dwUIChoice = AuthenticodeFlags.UiNone,
                fdwRevocationChecks = AuthenticodeFlags.RevokeNone,
                dwUnionChoice = AuthenticodeFlags.ChoiceFile,
                pFile = &file,
                dwStateAction = AuthenticodeFlags.StateActionVerify,
                dwProvFlags = AuthenticodeFlags.ProvFlags,
            };
            int hr = Native.WinVerifyTrust(-1, &action, &data);
            var chain = new List<SignerCertificate>();
            try { if (data.hWVTStateData != 0) ExtractChain(data.hWVTStateData, chain); }
            catch (Exception ex) when (ex is CryptographicException or AsnContentException or ArgumentException) { chain.Clear(); }
            return new SignatureEvidence(MapResult(hr), hr, chain);
        }
        finally
        {
            if (data.hWVTStateData != 0)
            {
                WintrustFileInfo closeFile = new() { cbStruct = (uint)sizeof(WintrustFileInfo), hFile = heldFile.DangerousGetHandle() };
                data.pFile = &closeFile;
                data.dwStateAction = AuthenticodeFlags.StateActionClose;
                Native.WinVerifyTrust(-1, &action, &data);
            }
            if (added) heldFile.DangerousRelease();
        }
    }

    private static unsafe void ExtractChain(nint state, List<SignerCertificate> chain)
    {
        void* prov = Native.WTHelperProvDataFromStateData(state);
        if (prov is null) return;
        var signer = Native.WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (signer is null) return;
        uint count = Math.Min(signer->csCertChain, 16u);
        for (uint i = 0; i < count; i++)
        {
            var cert = Native.WTHelperGetProvCertFromChain(signer, i);
            if (cert is null || cert->pCert is null) break;
            var ctx = (CertContextHead*)cert->pCert;
            if (ctx->pbCertEncoded is null || ctx->cbCertEncoded == 0 || ctx->cbCertEncoded > 65536) break;
            chain.Add(Describe(new ReadOnlySpan<byte>(ctx->pbCertEncoded, (int)ctx->cbCertEncoded)));
        }
    }

    /// <summary>Subject, issuer and hashes of a DER certificate. The SPKI hash is SHA-256 of the full DER SubjectPublicKeyInfo.</summary>
    public static SignerCertificate Describe(ReadOnlySpan<byte> derCertificate)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(derCertificate);
        return new SignerCertificate(certificate.Subject, certificate.Issuer,
            Convert.ToHexStringLower(SHA256.HashData(SubjectPublicKeyInfo(derCertificate))), Convert.ToHexStringLower(SHA256.HashData(derCertificate)));
    }

    /// <summary>Extracts the DER SubjectPublicKeyInfo from a DER X.509 certificate.</summary>
    public static byte[] SubjectPublicKeyInfo(ReadOnlySpan<byte> derCertificate)
    {
        var certificate = new AsnReader(derCertificate.ToArray(), AsnEncodingRules.DER).ReadSequence();
        var tbs = certificate.ReadSequence();
        if (tbs.PeekTag().TagClass == TagClass.ContextSpecific) tbs.ReadEncodedValue(); // [0] version
        tbs.ReadEncodedValue(); // serial number
        tbs.ReadEncodedValue(); // signature algorithm
        tbs.ReadEncodedValue(); // issuer
        tbs.ReadEncodedValue(); // validity
        tbs.ReadEncodedValue(); // subject
        return tbs.ReadEncodedValue().ToArray();
    }
}
