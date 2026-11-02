// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>
/// An ephemeral ECDH P-256 key pair (technical-docs/protocol.md 8.1). The private half never leaves this object. Disposing releases the key.
/// </summary>
public sealed class EphemeralKey : IDisposable
{
    private readonly ECDiffieHellman key;
    public PublicPoint65 PublicKey { get; }

    private EphemeralKey(ECDiffieHellman key)
    {
        this.key = key;
        var q = key.ExportParameters(false).Q;
        var raw = new byte[ElevationLimits.PublicKeyBytes];
        raw[0] = 0x04;
        q.X!.CopyTo(raw, 1 + 32 - q.X.Length);
        q.Y!.CopyTo(raw, 33 + 32 - q.Y.Length);
        PublicKey = new PublicPoint65(raw);
    }

    public static EphemeralKey Generate() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>
    /// Imports a fixed private scalar with its public point. Deterministic tests and published test vectors only (Appendix B); the system
    /// entropy source never calls this. Throws <see cref="CryptographicException"/> when the pair is inconsistent (programmer error).
    /// </summary>
    public static EphemeralKey FromPrivateScalar(ReadOnlySpan<byte> scalar32, PublicPoint65 publicPoint)
    {
        var q = publicPoint.AsSpan();
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = scalar32.ToArray(),
            Q = new ECPoint { X = q.Slice(1, 32).ToArray(), Y = q.Slice(33, 32).ToArray() },
        };
        return new(ECDiffieHellman.Create(parameters));
    }

    /// <summary>
    /// The raw shared secret (32-byte big-endian x coordinate) with a peer's public point. False when the point is not on the curve or the
    /// platform refuses it; hostile input never throws.
    /// </summary>
    public bool TryDeriveSecret(PublicPoint65 peer, out byte[] secret)
    {
        secret = [];
        try
        {
            var p = peer.AsSpan();
            using var other = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = p.Slice(1, 32).ToArray(), Y = p.Slice(33, 32).ToArray() },
            });
            secret = key.DeriveRawSecretAgreement(other.PublicKey);
            return secret.Length == 32;
        }
        // Windows CNG reports an off-curve point as PlatformNotSupportedException wrapping CryptographicException; other platforms use the latter directly.
        catch (CryptographicException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public void Dispose() => key.Dispose();
}

/// <summary>The platform CSPRNG and a fresh key pair per call.</summary>
public sealed class SystemHandshakeEntropy : IHandshakeEntropy
{
    public static SystemHandshakeEntropy Instance { get; } = new();
    public EphemeralKey NewKeyPair() => EphemeralKey.Generate();
    public Bytes32 NewNonce() => new(RandomNumberGenerator.GetBytes(32));
}

/// <summary>Everything the transcript T binds (technical-docs/protocol.md A.6).</summary>
public sealed record TranscriptInputs(
    byte Proto, ElevationFlavor Flavor, byte ClientProtoMin, byte ClientProtoMax, byte ServerProtoMin, byte ServerProtoMax,
    Bytes16 Rendezvous, Bytes32 ClientNonce, Bytes32 ServerNonce, PublicPoint65 ClientPublicKey, PublicPoint65 ServerPublicKey,
    ClientIdentity Client, ServerIdentity Server, Bytes32 DefinitionSetHash, Bytes32 BatchDigest, byte Operations);

/// <summary>The two directional AES-256-GCM keys of one session. Dispose zeroes them.</summary>
public sealed class SessionKeys : IDisposable
{
    public byte[] ClientToWorker { get; }
    public byte[] WorkerToClient { get; }
    internal SessionKeys(byte[] c2s, byte[] s2c) { ClientToWorker = c2s; WorkerToClient = s2c; }
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(ClientToWorker);
        CryptographicOperations.ZeroMemory(WorkerToClient);
    }
}

/// <summary>Transcript hash and key derivation of technical-docs/protocol.md 8.2 and A.6. HKDF-SHA256 with the exact info strings and salt T.</summary>
public static class KeySchedule
{
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("winora/transcript/v1\0");
    private static readonly byte[] InfoC2S = Encoding.ASCII.GetBytes("winora/v1/c2s");
    private static readonly byte[] InfoS2C = Encoding.ASCII.GetBytes("winora/v1/s2c");
    private static readonly byte[] Suite = Encoding.ASCII.GetBytes("P256-AESGCM-HKDF-SHA256");

    /// <summary>The 417-byte transcript input of A.6 (for the fixed vectors); <see cref="Transcript"/> is its SHA-256.</summary>
    public static byte[] TranscriptInput(TranscriptInputs t) => new WireWriter().Bytes(Label).U8(t.Proto).U8((byte)t.Flavor)
        .U8(t.ClientProtoMin).U8(t.ClientProtoMax).U8(t.ServerProtoMin).U8(t.ServerProtoMax)
        .B16(t.Rendezvous).B32(t.ClientNonce).B32(t.ServerNonce).Point(t.ClientPublicKey).Point(t.ServerPublicKey)
        .Bytes(WireCodec.Encode(t.Client)).Bytes(WireCodec.Encode(t.Server))
        .B32(t.DefinitionSetHash).B32(t.BatchDigest).U8(t.Operations).Blob(Suite).ToArray();

    public static Bytes32 Transcript(TranscriptInputs t) => new(SHA256.HashData(TranscriptInput(t)));

    /// <summary>PRK = HKDF-Extract(salt = T, ikm = Z); each key is HKDF-Expand(PRK, info, 32).</summary>
    public static SessionKeys Derive(ReadOnlySpan<byte> sharedSecret, Bytes32 transcript, out byte[] prk)
    {
        prk = new byte[32];
        HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, transcript.ToArray(), prk);
        var c2s = new byte[32];
        var s2c = new byte[32];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, c2s, InfoC2S);
        HKDF.Expand(HashAlgorithmName.SHA256, prk, s2c, InfoS2C);
        return new(c2s, s2c);
    }

    public static SessionKeys Derive(ReadOnlySpan<byte> sharedSecret, Bytes32 transcript)
    {
        var keys = Derive(sharedSecret, transcript, out var prk);
        CryptographicOperations.ZeroMemory(prk);
        return keys;
    }
}
