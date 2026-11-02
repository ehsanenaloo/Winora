// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>How a protocol exchange ended, as seen by the local side.</summary>
public enum SessionEnd { Completed, Failed, TimedOut, PeerClosed, PeerAborted, Cancelled }

/// <summary>Result of a handshake. On success it owns the <see cref="FrameCodec"/>; dispose it to zero the keys.</summary>
public sealed class HandshakeResult : IDisposable
{
    public SessionEnd End { get; init; }
    public ProtocolFailure? Failure { get; init; }
    /// <summary>The Result carried by an Abort of the peer, if one arrived and decoded.</summary>
    public ElevationResult? PeerAbort { get; init; }
    /// <summary>False for a plaintext Abort (before key derivation): advisory only, never persisted as an outcome.</summary>
    public bool PeerAbortAuthenticated { get; init; }
    public FrameCodec? Codec { get; init; }
    public Bytes32 Transcript { get; init; }
    public byte Proto { get; init; }
    public ClientHello? ClientHello { get; init; }
    public ServerHello? ServerHello { get; init; }
    public bool IsSuccess => End == SessionEnd.Completed && Codec is not null;
    public void Dispose() => Codec?.Dispose();

    internal static HandshakeResult Fail(SessionEnd end, ProtocolFailure failure) => new() { End = end, Failure = failure };
    internal static HandshakeResult Cancelled() => new() { End = SessionEnd.Cancelled };
}

/// <summary>Inputs of the client side of the handshake. The attestation has already been accepted (see <see cref="AttestationWaiter"/>).</summary>
public sealed record ClientHandshakeOptions(
    ElevationFlavor Flavor, Bytes16 Rendezvous, ClientIdentity Self, AttestationRecord Attestation, Bytes32 DefinitionSetHash,
    Bytes32 BatchDigest, byte Operations, IHandshakeEntropy Entropy, TimeProvider Time);

/// <summary>Inputs of the worker side. The worker generates its key pair when the object is created, because the attestation names it.</summary>
public sealed record WorkerHandshakeOptions(
    ElevationFlavor Flavor, Bytes16 Rendezvous, Bytes32 DefinitionSetHash, ServerIdentity Self, IHandshakeEntropy Entropy, TimeProvider Time);

/// <summary>
/// The handshake of section 8 for both roles: ephemeral ECDH P-256, transcript T binding both identities, flavor, version
/// lists, the definition-set hash and the batch digest, HKDF-SHA256 key schedule, and key confirmation by <c>Finished</c> in each direction.
/// </summary>
public static class HandshakeEngine
{
    private const byte OwnMin = ElevationLimits.ProtocolVersion;
    private const byte OwnMax = ElevationLimits.ProtocolVersion;
    private const byte Floor = ElevationLimits.ProtocolVersion;

    public static WorkerHandshake CreateWorker(WorkerHandshakeOptions options) => new(options);

    internal static MessageTypeSet PlainHelloOrAbort(MessageType hello) => MessageTypeSet.Of(hello, MessageType.Abort);

    /// <summary>Selects <c>max(common)</c> of two inclusive ranges and applies the floor; null when there is none (protocol.version).</summary>
    public static byte? NegotiateVersion(byte peerMin, byte peerMax)
    {
        var low = Math.Max(OwnMin, peerMin);
        var high = Math.Min(OwnMax, peerMax);
        return high >= low && high >= Floor ? (byte)high : null;
    }

    internal static HandshakeResult FromRead(FrameRead read, string timeoutDetail)
    {
        switch (read.Status)
        {
            case FrameReadStatus.TimedOut: return HandshakeResult.Fail(SessionEnd.TimedOut, ProtocolFailure.Timeout(timeoutDetail));
            case FrameReadStatus.Closed: return HandshakeResult.Fail(SessionEnd.PeerClosed, read.Failure ?? ProtocolFailure.Channel(ProtocolDetail.Framing));
            default: return HandshakeResult.Fail(SessionEnd.Failed, read.Failure ?? ProtocolFailure.Channel(ProtocolDetail.Framing));
        }
    }

    internal static HandshakeResult PeerAbort(byte[] payload, bool authenticated)
    {
        var decoded = WireCodec.DecodeAbort(payload);
        return new HandshakeResult
        {
            End = SessionEnd.PeerAborted,
            Failure = decoded.IsSuccess ? null : decoded.Failure,
            PeerAbort = decoded.IsSuccess ? decoded.Value!.Result : null,
            PeerAbortAuthenticated = authenticated,
        };
    }

    /// <summary>Client role: sends ClientHello, verifies ServerHello against the attestation, derives keys, exchanges Finished.</summary>
    public static async Task<HandshakeResult> RunClientAsync(IFrameChannel channel, ClientHandshakeOptions o, CancellationToken ct)
    {
        try
        {
            using var ours = o.Entropy.NewKeyPair();
            var nc = o.Entropy.NewNonce();
            var hello = new ClientHello(OwnMin, OwnMax, o.Flavor, o.Rendezvous, nc, ours.PublicKey, o.DefinitionSetHash, o.BatchDigest, o.Operations);
            var sent = await FrameIo.WriteAsync(channel, FrameRules.EncodePlain(MessageType.ClientHello, WireCodec.Encode(hello)), ct).ConfigureAwait(false);
            if (sent is not null) return HandshakeResult.Fail(SessionEnd.PeerClosed, sent);

            var read = await FrameIo.ReadAsync(channel, null, ElevationLimits.ProtocolVersion, PlainHelloOrAbort(MessageType.ServerHello),
                TimeSpan.FromMilliseconds(ElevationLimits.ServerHelloWaitMs), o.Time, ct).ConfigureAwait(false);
            if (read.Status != FrameReadStatus.Frame) return FromRead(read, "frame");
            if (read.Type == MessageType.Abort) return PeerAbort(read.Payload, authenticated: false);
            var decoded = WireCodec.DecodeServerHello(read.Payload);
            if (!decoded.TryGet(out var server, out var failure)) return HandshakeResult.Fail(SessionEnd.Failed, failure);

            // 7.1 step 4: the hello must equal the attestation, and the key must hash to the attested value.
            var att = o.Attestation;
            if (server.Flavor != o.Flavor) return HandshakeResult.Fail(SessionEnd.Failed, ProtocolFailure.Channel(ProtocolDetail.Flavor));
            if (server.Rendezvous != o.Rendezvous || server.Rendezvous != att.Rendezvous || server.Flavor != att.Flavor || server.ProtoMin != att.ProtoMin || server.ProtoMax != att.ProtoMax
                || server.DefinitionSetHash != att.DefinitionSetHash
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(server.ServerPublicKey.AsSpan()), att.ServerKeyHash.ToArray()))
                return HandshakeResult.Fail(SessionEnd.Failed, ProtocolFailure.ServerUntrusted());
            if (server.DefinitionSetHash != o.DefinitionSetHash) return HandshakeResult.Fail(SessionEnd.Failed, new ProtocolFailure(ErrorCode.DefinitionSetMismatch));
            var proto = NegotiateVersion(server.ProtoMin, server.ProtoMax);
            if (proto is null) return HandshakeResult.Fail(SessionEnd.Failed, new ProtocolFailure(ErrorCode.ProtocolVersion));
            if (server.Chosen != proto) return HandshakeResult.Fail(SessionEnd.Failed, ProtocolFailure.Channel(ProtocolDetail.Version));

            if (!ours.TryDeriveSecret(server.ServerPublicKey, out var z)) return HandshakeResult.Fail(SessionEnd.Failed, ProtocolFailure.Channel(ProtocolDetail.Handshake));
            var t = KeySchedule.Transcript(new TranscriptInputs(proto.Value, o.Flavor, OwnMin, OwnMax, server.ProtoMin, server.ProtoMax, o.Rendezvous, nc, server.ServerNonce,
                ours.PublicKey, server.ServerPublicKey, o.Self, att.ServerIdentity, o.DefinitionSetHash, o.BatchDigest, o.Operations));
            FrameCodec codec;
            using (var keys = KeySchedule.Derive(z, t))
            {
                CryptographicOperations.ZeroMemory(z);
                codec = new FrameCodec(FrameDirection.ClientToWorker, proto.Value, keys.ClientToWorker, keys.WorkerToClient);
            }
            return await ConfirmAsync(channel, codec, t, proto.Value, sendFirst: true, SessionRules.ClientAllowed(ClientState.AwaitFinished), o.Time, ct, serverHello: server, clientHello: null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return HandshakeResult.Cancelled(); }
    }

    /// <summary>Key confirmation (8.2): the client sends Finished(T) first, the worker verifies and answers with its own.</summary>
    internal static async Task<HandshakeResult> ConfirmAsync(IFrameChannel channel, FrameCodec codec, Bytes32 t, byte proto, bool sendFirst, MessageTypeSet allowed,
        TimeProvider time, CancellationToken ct, ServerHello? serverHello, ClientHello? clientHello)
    {
        var failed = true;
        try
        {
            if (sendFirst)
            {
                var sentFailure = await SendFinishedAsync(channel, codec, t, ct).ConfigureAwait(false);
                if (sentFailure is not null) return HandshakeResult.Fail(SessionEnd.PeerClosed, sentFailure);
            }
            var read = await FrameIo.ReadAsync(channel, codec, proto, allowed,
                TimeSpan.FromMilliseconds(ElevationLimits.AwaitFinishedMs), time, ct).ConfigureAwait(false);
            if (read.Status != FrameReadStatus.Frame)
            {
                var r = FromRead(read, "frame");
                // 8.2: an AEAD failure at key confirmation is reported as a handshake failure.
                return r.Failure is { Detail: ProtocolDetail.Tag } ? HandshakeResult.Fail(SessionEnd.Failed, ProtocolFailure.Channel(ProtocolDetail.Handshake)) : r;
            }
            if (read.Type == MessageType.Abort) return PeerAbort(read.Payload, authenticated: true);
            var peer = WireCodec.DecodeFinished(read.Payload);
            if (!peer.IsSuccess || !CryptographicOperations.FixedTimeEquals(peer.Value!.Transcript.ToArray(), t.ToArray()))
                return HandshakeResult.Fail(SessionEnd.Failed, ProtocolFailure.Channel(ProtocolDetail.Handshake));
            if (!sendFirst)
            {
                var sentFailure = await SendFinishedAsync(channel, codec, t, ct).ConfigureAwait(false);
                if (sentFailure is not null) return HandshakeResult.Fail(SessionEnd.PeerClosed, sentFailure);
            }
            failed = false;
            return new HandshakeResult { End = SessionEnd.Completed, Codec = codec, Transcript = t, Proto = proto, ServerHello = serverHello, ClientHello = clientHello };
        }
        finally
        {
            if (failed) codec.Dispose();
        }
    }

    private static async ValueTask<ProtocolFailure?> SendFinishedAsync(IFrameChannel channel, FrameCodec codec, Bytes32 t, CancellationToken ct)
    {
        var sealedFrame = codec.Seal(MessageType.Finished, WireCodec.Encode(new Finished(t)));
        return sealedFrame.IsSuccess ? await FrameIo.WriteAsync(channel, sealedFrame.Frame!, ct).ConfigureAwait(false) : sealedFrame.Failure;
    }
}

/// <summary>Worker role of the handshake. Create it before the pipe: <see cref="Attestation"/> is the record the worker must publish.</summary>
public sealed class WorkerHandshake : IDisposable
{
    private const byte OwnMin = ElevationLimits.ProtocolVersion;
    private const byte OwnMax = ElevationLimits.ProtocolVersion;
    private readonly WorkerHandshakeOptions options;
    private readonly EphemeralKey key;
    private readonly Bytes32 serverNonce;

    internal WorkerHandshake(WorkerHandshakeOptions options)
    {
        this.options = options;
        key = options.Entropy.NewKeyPair();
        serverNonce = options.Entropy.NewNonce();
        Attestation = new AttestationRecord(options.Flavor, OwnMin, OwnMax, options.Rendezvous, options.Self.ProcessId, options.Self.StartFileTime, options.Self.SessionId,
            new Bytes32(SHA256.HashData(key.PublicKey.AsSpan())), options.DefinitionSetHash);
    }

    /// <summary>Content of the attestation record (A.8.1); encode with <see cref="WireCodec"/> and write it before accepting connections.</summary>
    public AttestationRecord Attestation { get; }

    /// <summary>
    /// Runs the handshake on a connection whose client already passed the identity checks (<see cref="PeerVerifier.VerifyClient"/>).
    /// <paramref name="observedClient"/> is the identity the worker observed; it is bound into the transcript.
    /// </summary>
    public async Task<HandshakeResult> RunAsync(IFrameChannel channel, ClientIdentity observedClient, CancellationToken ct)
    {
        try
        {
            var read = await FrameIo.ReadAsync(channel, null, ElevationLimits.ProtocolVersion, HandshakeEngine.PlainHelloOrAbort(MessageType.ClientHello),
                TimeSpan.FromMilliseconds(ElevationLimits.AwaitHelloMs), options.Time, ct).ConfigureAwait(false);
            if (read.Status != FrameReadStatus.Frame) return HandshakeEngine.FromRead(read, "frame");
            if (read.Type == MessageType.Abort) return HandshakeEngine.PeerAbort(read.Payload, authenticated: false);
            var decoded = WireCodec.DecodeClientHello(read.Payload);
            if (!decoded.TryGet(out var hello, out var failure)) return await RefuseAsync(channel, failure, ct).ConfigureAwait(false);

            if (hello.Flavor != options.Flavor) return await RefuseAsync(channel, ProtocolFailure.Channel(ProtocolDetail.Flavor), ct).ConfigureAwait(false);
            if (hello.Rendezvous != options.Rendezvous) return await RefuseAsync(channel, ProtocolFailure.Channel(ProtocolDetail.Handshake), ct).ConfigureAwait(false);
            if (hello.DefinitionSetHash != options.DefinitionSetHash) return await RefuseAsync(channel, new ProtocolFailure(ErrorCode.DefinitionSetMismatch), ct).ConfigureAwait(false);
            var proto = HandshakeEngine.NegotiateVersion(hello.ProtoMin, hello.ProtoMax);
            if (proto is null) return await RefuseAsync(channel, new ProtocolFailure(ErrorCode.ProtocolVersion), ct).ConfigureAwait(false);
            if (!key.TryDeriveSecret(hello.ClientPublicKey, out var z)) return await RefuseAsync(channel, ProtocolFailure.Channel(ProtocolDetail.Handshake), ct).ConfigureAwait(false);

            var server = new ServerHello(OwnMin, OwnMax, options.Flavor, options.Rendezvous, serverNonce, key.PublicKey, options.DefinitionSetHash, proto.Value);
            var sent = await FrameIo.WriteAsync(channel, FrameRules.EncodePlain(MessageType.ServerHello, WireCodec.Encode(server)), ct).ConfigureAwait(false);
            if (sent is not null) return HandshakeResult.Fail(SessionEnd.PeerClosed, sent);

            var t = KeySchedule.Transcript(new TranscriptInputs(proto.Value, options.Flavor, hello.ProtoMin, hello.ProtoMax, OwnMin, OwnMax, options.Rendezvous, hello.ClientNonce,
                serverNonce, hello.ClientPublicKey, key.PublicKey, observedClient, options.Self, options.DefinitionSetHash, hello.BatchDigest, hello.Operations));
            FrameCodec codec;
            using (var keys = KeySchedule.Derive(z, t))
            {
                CryptographicOperations.ZeroMemory(z);
                codec = new FrameCodec(FrameDirection.WorkerToClient, proto.Value, keys.WorkerToClient, keys.ClientToWorker);
            }
            return await HandshakeEngine.ConfirmAsync(channel, codec, t, proto.Value, sendFirst: false, SessionRules.WorkerAllowed(SessionState.AwaitFinished), options.Time, ct, serverHello: null, clientHello: hello).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return HandshakeResult.Cancelled(); }
    }

    /// <summary>Best-effort plaintext Abort (advisory, 9) followed by the failure result; the connection is not usable afterwards.</summary>
    private async Task<HandshakeResult> RefuseAsync(IFrameChannel channel, ProtocolFailure failure, CancellationToken ct)
    {
        var abort = new Abort(failure.ToResultOrUnexpected());
        await FrameIo.WriteAsync(channel, FrameRules.EncodePlain(MessageType.Abort, WireCodec.Encode(abort)), ct).ConfigureAwait(false);
        return HandshakeResult.Fail(SessionEnd.Failed, failure);
    }

    public void Dispose() => key.Dispose();
}
