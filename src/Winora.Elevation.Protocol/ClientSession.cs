// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>
/// Options of a client session. <paramref name="BeforeCommit"/> is called after a Prepared with Ready and willChange and before Commit is
/// sent: the caller records the worker-observed before-state durably (technical-docs/protocol.md 12.3 step 5) and returns true to proceed; false
/// sends Abort and ends the session without a mutation.
/// </summary>
public sealed record ClientSessionOptions(
    ElevationFlavor Flavor, Bytes16 Rendezvous, ClientIdentity Self, AttestationRecord Attestation, Bytes32 DefinitionSetHash, BatchBegin Batch,
    IHandshakeEntropy Entropy, TimeProvider Time, Func<BatchItem, Prepared, CancellationToken, ValueTask<bool>>? BeforeCommit = null);

/// <summary>One operation as the client saw it: the worker's Prepared and, after a Commit, the Outcome.</summary>
public sealed record ClientOperationResult(BatchItem Item, Prepared Prepared, Outcome? Outcome, bool CommitSent);

public sealed class ClientSessionResult
{
    public SessionEnd End { get; init; }
    public ProtocolFailure? Failure { get; init; }
    /// <summary>The Result of a worker Abort that passed the allow-list; see <see cref="PeerAbortAuthenticated"/>.</summary>
    public ElevationResult? PeerAbort { get; init; }
    public bool PeerAbortAuthenticated { get; init; }
    public IReadOnlyList<ClientOperationResult> Operations { get; init; } = [];
    public BatchAccepted? Accepted { get; init; }
    /// <summary>True once a Commit frame was written: from then on the worker may have written and the client must verify by observation.</summary>
    public bool CommitSent { get; init; }
    /// <summary>True when the caller's BeforeCommit hook declined, so the client sent Abort.</summary>
    public bool DeclinedByCaller { get; init; }
    public bool ByeReceived { get; init; }
    public Bytes32 Transcript { get; init; }
    public bool IsCompleted => End == SessionEnd.Completed;
}

/// <summary>
/// Client side of the session (technical-docs/protocol.md 10, 12.3): handshake, BatchBegin, then for each operation Prepare, an optional Commit and
/// the replies, then Bye. Every reply is checked against the allow-list of 10.3, the operation order and the recomputed prepared digest
/// before it is returned; the client never persists a worker-supplied string that failed these checks.
/// </summary>
public static class ClientSession
{
    public static async Task<ClientSessionResult> RunAsync(IFrameChannel channel, ClientSessionOptions o, CancellationToken ct)
    {
        try { return await RunCoreAsync(channel, o, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new() { End = SessionEnd.Cancelled }; }
        finally { channel.Close(); }   // every end of a session closes the channel (technical-docs/protocol.md 0, Abort)
    }

    private static async Task<ClientSessionResult> RunCoreAsync(IFrameChannel channel, ClientSessionOptions o, CancellationToken ct)
    {
        var batch = o.Batch;
        using var hs = await HandshakeEngine.RunClientAsync(channel, new ClientHandshakeOptions(o.Flavor, o.Rendezvous, o.Self, o.Attestation, o.DefinitionSetHash,
            Digests.BatchDigest(batch), batch.Operations, o.Entropy, o.Time), ct).ConfigureAwait(false);
        if (!hs.IsSuccess)
            return new() { End = hs.End, Failure = hs.Failure, PeerAbort = hs.PeerAbort, PeerAbortAuthenticated = hs.PeerAbortAuthenticated };
        var codec = hs.Codec!;
        var t = hs.Transcript;
        var proto = hs.Proto;
        var operations = new List<ClientOperationResult>();
        BatchAccepted? accepted = null;
        var commitSent = false;

        ClientSessionResult End(SessionEnd end, ProtocolFailure? failure = null, ElevationResult? peer = null, bool declined = false, bool bye = false) => new()
        {
            End = end, Failure = failure, PeerAbort = peer, PeerAbortAuthenticated = peer is not null, Operations = operations.ToArray(), Accepted = accepted,
            CommitSent = commitSent, DeclinedByCaller = declined, ByeReceived = bye, Transcript = t,
        };

        async ValueTask<ProtocolFailure?> Send(MessageType type, byte[] payload)
        {
            var s = codec.Seal(type, payload);
            return s.IsSuccess ? await FrameIo.WriteAsync(channel, s.Frame!, ct).ConfigureAwait(false) : s.Failure;
        }

        async Task<ClientSessionResult> AbortWith(ProtocolFailure failure, SessionEnd end = SessionEnd.Failed)
        {
            var result = failure.ToResultOrUnexpected();
            var abort = codec.Seal(MessageType.Abort, WireCodec.Encode(new Abort(result)));
            if (abort.IsSuccess) await FrameIo.WriteAsync(channel, abort.Frame!, ct).ConfigureAwait(false);
            return End(end, failure);
        }

        // Reads one reply; a null reply means the session already ended with the returned result.
        async Task<(FrameRead Read, ClientSessionResult? Ended)> Receive(ClientState state, string timeoutDetail, TimeSpan wait)
        {
            var read = await FrameIo.ReadAsync(channel, codec, proto, SessionRules.ClientAllowed(state), wait, o.Time, ct).ConfigureAwait(false);
            switch (read.Status)
            {
                case FrameReadStatus.TimedOut:
                    return (read, await AbortWith(ProtocolFailure.Timeout(timeoutDetail), SessionEnd.TimedOut).ConfigureAwait(false));
                case FrameReadStatus.Closed:
                    return (read, End(SessionEnd.PeerClosed, read.Failure));
                case FrameReadStatus.Failed:
                    return (read, await AbortWith(read.Failure!).ConfigureAwait(false));
            }
            if (read.Type == MessageType.Abort)
            {
                var abort = WireCodec.DecodeAbort(read.Payload);
                if (!abort.IsSuccess) return (read, await AbortWith(abort.Failure!).ConfigureAwait(false));
                // 10.3: a worker Abort must also pass the allow-list, or the session is a channel failure; the string is never kept otherwise.
                if (!ResultPolicy.IsAllowedAbort(abort.Value!.Result)) return (read, await AbortWith(ProtocolFailure.Channel(ProtocolDetail.Code)).ConfigureAwait(false));
                return (read, End(SessionEnd.PeerAborted, null, abort.Value!.Result));
            }
            return (read, null);
        }

        var sent = await Send(MessageType.BatchBegin, WireCodec.Encode(batch)).ConfigureAwait(false);
        if (sent is not null) return End(SessionEnd.PeerClosed, sent);
        var (accRead, accEnded) = await Receive(ClientState.AwaitBatchAccepted, "frame", SessionRules.ClientReplyWait).ConfigureAwait(false);
        if (accEnded is not null) return accEnded;
        var accDecoded = WireCodec.DecodeBatchAccepted(accRead.Payload);
        if (!accDecoded.TryGet(out var acc, out var accFailure)) return await AbortWith(accFailure).ConfigureAwait(false);
        if (acc.Operations != batch.Operations) return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.State)).ConfigureAwait(false);
        accepted = acc;

        foreach (var item in batch.Items)
        {
            sent = await Send(MessageType.Prepare, WireCodec.Encode(new Prepare(item.OperationId))).ConfigureAwait(false);
            if (sent is not null) return End(SessionEnd.PeerClosed, sent);
            var (pRead, pEnded) = await Receive(ClientState.AwaitPrepared, "frame", SessionRules.ClientReplyWait).ConfigureAwait(false);
            if (pEnded is not null) return pEnded;
            var pDecoded = WireCodec.DecodePrepared(pRead.Payload);
            if (!pDecoded.TryGet(out var prepared, out var pFailure)) return await AbortWith(pFailure).ConfigureAwait(false);
            if (prepared.OperationId != item.OperationId) return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.State)).ConfigureAwait(false);
            if (!ResultPolicy.IsAllowed(MessageType.Prepared, prepared.Status, prepared.Result)) return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.Code)).ConfigureAwait(false);
            var expectedDigest = Digests.PreparedDigest(t, item.OperationId, item.PlanTokenDigest, prepared.Observed, prepared.KeyRevision, prepared.WillChange);
            if (!CryptographicOperations.FixedTimeEquals(expectedDigest.ToArray(), prepared.PreparedDigest.ToArray()))
                return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.Framing)).ConfigureAwait(false);

            if (prepared.Status != ElevationStatus.Ready || !prepared.WillChange)
            {
                operations.Add(new(item, prepared, null, false));
                if (SessionRules.EndsBatch(MessageType.Prepared, prepared.Status)) break;
                continue;
            }

            if (o.BeforeCommit is not null && !await o.BeforeCommit(item, prepared, ct).ConfigureAwait(false))
            {
                operations.Add(new(item, prepared, null, false));
                var cancel = codec.Seal(MessageType.Abort, WireCodec.Encode(new Abort(ElevationResult.From(ErrorCode.Cancelled))));
                if (cancel.IsSuccess) await FrameIo.WriteAsync(channel, cancel.Frame!, ct).ConfigureAwait(false);
                return End(SessionEnd.Cancelled, null, null, declined: true);
            }
            // From here on the worker may have written: the entry is visible to the caller even if no Outcome ever arrives.
            commitSent = true;
            operations.Add(new(item, prepared, null, true));
            sent = await Send(MessageType.Commit, WireCodec.Encode(new Commit(item.OperationId, prepared.PreparedDigest))).ConfigureAwait(false);
            if (sent is not null) return End(SessionEnd.PeerClosed, sent);
            var (oRead, oEnded) = await Receive(ClientState.AwaitOutcome, "commit", SessionRules.ClientReplyWait).ConfigureAwait(false);
            if (oEnded is not null) return oEnded;
            var oDecoded = WireCodec.DecodeOutcome(oRead.Payload);
            if (!oDecoded.TryGet(out var outcome, out var oFailure)) return await AbortWith(oFailure).ConfigureAwait(false);
            if (outcome.OperationId != item.OperationId || !ResultPolicy.IsAllowed(MessageType.Outcome, outcome.Status, outcome.Result))
                return await AbortWith(ProtocolFailure.Channel(outcome.OperationId != item.OperationId ? ProtocolDetail.State : ProtocolDetail.Code)).ConfigureAwait(false);
            operations[^1] = new(item, prepared, outcome, true);
            if (SessionRules.EndsBatch(MessageType.Outcome, outcome.Status)) break;
        }

        sent = await Send(MessageType.Bye, []).ConfigureAwait(false);
        if (sent is not null) return End(SessionEnd.PeerClosed, sent);
        // Every reply so far was authentic and complete: a missing Bye (timeout or close) does not change the operation results.
        var byeRead = await FrameIo.ReadAsync(channel, codec, proto, SessionRules.ClientAllowed(ClientState.AwaitBye),
            TimeSpan.FromMilliseconds(ElevationLimits.AwaitByeMs), o.Time, ct).ConfigureAwait(false);
        switch (byeRead.Status)
        {
            case FrameReadStatus.TimedOut:
            case FrameReadStatus.Closed:
                return End(SessionEnd.Completed);
            case FrameReadStatus.Failed:
                return await AbortWith(byeRead.Failure!).ConfigureAwait(false);
        }
        if (byeRead.Type == MessageType.Abort)
        {
            var abort = WireCodec.DecodeAbort(byeRead.Payload);
            return abort.IsSuccess && ResultPolicy.IsAllowedAbort(abort.Value!.Result)
                ? End(SessionEnd.PeerAborted, null, abort.Value.Result)
                : await AbortWith(ProtocolFailure.Channel(ProtocolDetail.Code)).ConfigureAwait(false);
        }
        return WireCodec.DecodeBye(byeRead.Payload).IsSuccess ? End(SessionEnd.Completed, bye: true) : await AbortWith(ProtocolFailure.Channel(ProtocolDetail.Framing)).ConfigureAwait(false);
    }
}
