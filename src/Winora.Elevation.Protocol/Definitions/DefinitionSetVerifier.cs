// SPDX-License-Identifier: GPL-3.0-only
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Cryptography.Cose;
using System.Text;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol.Definitions;

/// <summary>
/// A pinned definition-signing public key: an ECDSA P-256 SubjectPublicKeyInfo and its <c>kid</c>, the first 8 bytes of SHA-256(SPKI).
/// The kid is recomputed from the key, never trusted from a caller.
/// </summary>
public sealed class PinnedKey
{
    public const int KidBytes = 8;
    private readonly byte[] kid;
    private readonly byte[] spki;

    public PinnedKey(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        spki = subjectPublicKeyInfo.ToArray();
        using var probe = ECDsa.Create();
        probe.ImportSubjectPublicKeyInfo(spki, out var read);
        if (read != spki.Length) throw new ArgumentException("trailing bytes after the public key", nameof(subjectPublicKeyInfo));
        var p = probe.ExportParameters(false);
        if (p.Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new ArgumentException("the key is not on curve P-256", nameof(subjectPublicKeyInfo));
        kid = SHA256.HashData(spki)[..KidBytes];
    }

    public ReadOnlySpan<byte> Kid => kid;
    public ECDsa CreateVerifier()
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return key;
    }
}

/// <summary>Why a set was not accepted. <see cref="Code"/> is always an error code of the catalog.</summary>
public sealed record DefinitionVerifyResult(DefinitionSet? Set, Bytes32 SetHash, string? Code, string? Problem)
{
    public bool Ok => Set is not null;
}

/// <summary>
/// The definition-signature layer. The detached COSE_Sign1 is verified over the raw bytes BEFORE anything is parsed; then the strict profile,
/// the schema and the admission invariants run; the floor is checked by <see cref="RollbackPolicy"/> by the caller. Every failure of the first three steps
/// is <c>definition.signature_invalid</c>: a set that is signed but unusable is unusable.
/// </summary>
public sealed class DefinitionSetVerifier(IReadOnlyList<PinnedKey> pinnedKeys)
{
    /// <summary>COSE external additional authenticated data: the domain-separation label of the set.</summary>
    public static readonly byte[] ExternalAad = Encoding.ASCII.GetBytes("winora/defset/v1");
    public const int MaxSignatureBytes = 512;

    public DefinitionVerifyResult Verify(ReadOnlySpan<byte> setBytes, ReadOnlySpan<byte> coseSign1)
    {
        if (setBytes.Length is 0 or > DefinitionLimits.MaxBytes) return Fail("set size out of range");
        if (coseSign1.Length is 0 or > MaxSignatureBytes) return Fail("signature size out of range");
        if (pinnedKeys.Count == 0) return Fail("no pinned definition key");

        CoseSign1Message message;
        try { message = CoseMessage.DecodeSign1(coseSign1); }
        catch (Exception ex) when (ex is CryptographicException or CborContentException or InvalidOperationException or ArgumentException) { return Fail("not a COSE_Sign1 message"); }

        var headerProblem = CheckHeaders(message, out var kid);
        if (headerProblem is not null) return Fail(headerProblem);
        if (message.Content is not null) return Fail("the payload must be detached");

        var key = pinnedKeys.FirstOrDefault(k => k.Kid.SequenceEqual(kid));
        if (key is null) return Fail("kid is not pinned");
        bool valid;
        try
        {
            using var ecdsa = key.CreateVerifier();
            valid = message.VerifyDetached(ecdsa, setBytes, ExternalAad);
        }
        catch (CryptographicException) { valid = false; }
        if (!valid) return Fail("signature does not verify");

        var parsed = DefinitionSetParser.Parse(setBytes);
        if (!parsed.Ok) return Fail(parsed.Problem!);
        var problems = DefinitionInvariants.Validate(parsed.Set!);
        if (problems.Count > 0) return Fail(problems[0]);
        return new DefinitionVerifyResult(parsed.Set, DefinitionSetHash.Compute(setBytes), null, null);
    }

    /// <summary>Protected header exactly <c>{ alg: -7, kid: 8 bytes }</c>, no unprotected header, no critical parameter, no other label.</summary>
    private static string? CheckHeaders(CoseSign1Message message, out byte[] kid)
    {
        kid = [];
        if (message.UnprotectedHeaders.Count != 0) return "unprotected headers are not allowed";
        var protectedHeaders = message.ProtectedHeaders;
        if (protectedHeaders.Count != 2) return "protected header must hold exactly alg and kid";
        if (!protectedHeaders.TryGetValue(CoseHeaderLabel.Algorithm, out var alg) || alg.GetValueAsInt32() != -7) return "algorithm must be ES256";
        if (!protectedHeaders.TryGetValue(CoseHeaderLabel.KeyIdentifier, out var kidValue)) return "kid is missing";
        kid = kidValue.GetValueAsBytes().ToArray();
        return kid.Length == PinnedKey.KidBytes ? null : "kid must be 8 bytes";
    }

    private static DefinitionVerifyResult Fail(string problem) =>
        new(null, default, ErrorCode.DefinitionSignatureInvalid, problem);
}
