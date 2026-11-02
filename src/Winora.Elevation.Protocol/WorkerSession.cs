// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>
/// Options of a worker session. The production configuration is the release flavor with an empty handler set. <paramref name="DefinitionSetHash"/> is H_d of the verified
/// embedded set. <paramref name="BeforeBatchAccepted"/> runs after the batch passed every check and before BatchAccepted is sent: it
/// advances the anti-rollback floor; when it returns false the session aborts with <c>trust.store_insecure</c> and nothing was written.
/// </summary>
public sealed record WorkerSessionOptions(
    ElevationFlavor Flavor, Bytes16 Rendezvous, ServerIdentity Self, BinarySid TokenUserSid, WorkerHandlerSet Handlers,
    IHandshakeEntropy Entropy, TimeProvider Time,
    bool CommitSupported = false, int MaxOperations = ElevationLimits.M4aMaxBatchOperations,
    Bytes32? DefinitionSetHash = null, Func<bool>? BeforeBatchAccepted = null);

/// <summary>Result of a worker session: how it ended, the typed failure if any, and whether a write may have started.</summary>
public sealed class WorkerSessionResult
{
    public SessionEnd End { get; init; }
    public ProtocolFailure? Failure { get; init; }
    public ElevationResult? PeerAbort { get; init; }
    public bool PeerAbortAuthenticated { get; init; }
    /// <summary>True once a Commit was accepted: from then on a mutation may have happened and the client must verify by observation.</summary>
    public bool CommitAccepted { get; init; }
    public int OperationsCompleted { get; init; }
    public bool ByeReceived { get; init; }
    public Bytes32 Transcript { get; init; }
    public WorkerExitCode ExitCode { get; init; }
}

/// <summary>
/// Worker side of the session: runs the handshake, accepts one batch, answers Prepare and Commit through the
/// registered handlers and ends with Bye. The state machine is table driven (<see cref="SessionRules"/>); every wait has the timer of
/// section 10.2, measured by the injected <see cref="TimeProvider"/>. A worker with an empty handler set (the production worker) answers
/// every BatchBegin with Abort(worker.unimplemented) before any definition lookup. The worker rejects Commit unless
/// <see cref="WorkerSessionOptions.CommitSupported"/> is set (only tests and the VM harness enable it).
/// </summary>
public sealed class WorkerSession : IDisposable
{
    private readonly WorkerSessionOptions options;
    private readonly WorkerHandshake handshake;
    private int served;

    public WorkerSession(WorkerSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxOperations is < 1 or > ElevationLimits.MaxBatchOperations) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Flavor == ElevationFlavor.Release && options.Handlers.Handlers.Any(h => h is SelftestNoopHandler))
            throw new ArgumentException("the self-test handler exists only in the VmTrust flavor", nameof(options));
        this.options = options;
        handshake = HandshakeEngine.CreateWorker(new WorkerHandshakeOptions(options.Flavor, options.Rendezvous, options.DefinitionSetHash ?? M4aDefinitionSet.Hash(options.Flavor), options.Self, options.Entropy, options.Time));
    }

    /// <summary>The record to publish before accepting connections.</summary>
    public AttestationRecord Attestation => handshake.Attestation;

    /// <summary>Serves one connection that already passed the identity checks. A session serves at most one connection.</summary>
    public async Task<WorkerSessionResult> ServeAsync(IFrameChannel channel, ClientIdentity observedClient, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref served, 1) != 0) throw new InvalidOperationException("a worker session serves one connection");
        try { return await RunAsync(channel, observedClient, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new() { End = SessionEnd.Cancelled, ExitCode = WorkerExitCode.Timeout }; }
        finally { channel.Close(); }   // every end of a session closes the channel
    }

    private async Task<WorkerSessionResult> RunAsync(IFrameChannel channel, ClientIdentity observedClient, CancellationToken ct)
    {
        using var hs = await handshake.RunAsync(channel, observedClient, ct).ConfigureAwait(false);
        if (!hs.IsSuccess)
            return new()
            {
                End = hs.End, Failure = hs.Failure, PeerAbort = hs.PeerAbort, PeerAbortAuthenticated = hs.PeerAbortAuthenticated, ExitCode = ExitFor(hs.End, hs.Failure),
            };
        var codec = hs.Codec!;
        var hello = hs.ClientHello!;
        var t = hs.Transcript;
        var proto = hs.Proto;
        var state = SessionState.AwaitBatchBegin;
        BatchBegin? batch = null;
        var index = 0;
        var commitAccepted = false;
        (BatchItem Item, Bytes32 Digest, HandlerObservation Observed)? pending = null;

        WorkerSessionResult End(SessionEnd end, ProtocolFailure? failure = null, ElevationResult? peer = null, bool byeReceived = false) => new()
        {
            End = end, Failure = failure, PeerAbort = peer, PeerAbortAuthenticated = peer is not null, CommitAccepted = commitAccepted, OperationsCompleted = index,
            ByeReceived = byeReceived, Transcript = t, ExitCode = ExitFor(end, failure),
        };

        async Task<WorkerSessionResult> AbortWith(ProtocolFailure failure, SessionEnd end = SessionEnd.Failed)
        {
            var result = failure.ToResultOrUnexpected();
            if (!ResultPolicy.IsAllowedAbort(result)) result = ElevationResult.From(ErrorCode.Unexpected);
            var sealedAbort = codec.Seal(MessageType.Abort, WireCodec.Encode(new Abort(result)));
            if (sealedAbort.IsSuccess) await FrameIo.WriteAsync(channel, sealedAbort.Frame!, ct).ConfigureAwait(false);
            return End(end, failure);
        }

        async ValueTask<ProtocolFailure?> Send(MessageType type, byte[] payload)
        {
            var s = codec.Seal(type, payload);
            return s.IsSuccess ? await FrameIo.WriteAsync(channel, s.Frame!, ct).ConfigureAwait(false) : s.Failure;
        }

        while (true)
        {
            var wait = TimeSpan.FromMilliseconds(ElevationLimits.DeadlineMs(state) ?? ElevationLimits.IdleMs);
            var read = await FrameIo.ReadAsync(channel, codec, proto, SessionRules.WorkerAllowed(state), wait, options.Time, ct).ConfigureAwait(false);
            switch (read.Status)
            {
                case FrameReadStatus.TimedOut:
                    if (state == SessionState.AwaitBye) return End(SessionEnd.Completed);   // 10.2: exit normally
                    return await AbortWith(ProtocolFailure.Timeout(SessionRules.TimeoutDetail(state)), SessionEnd.TimedOut).ConfigureAwait(false);
                case FrameReadStatus.Closed:
                    return End(SessionEnd.PeerClosed, read.Failure);
                case FrameReadStatus.Failed:
                    return await AbortWith(read.Failure!).ConfigureAwait(false);
            }

            if (read.Type == MessageType.Abort)
            {
                var abort = WireCodec.DecodeAbort(read.Payload);
                if (!abort.IsSuccess) return await AbortWith(abort.Failure!).ConfigureAwait(false);
                return End(SessionEnd.PeerAborted, null, abort.Value!.Result);
            }

            switch (state)
            {
                case SessionState.AwaitBatchBegin:
                {
                    var decoded = WireCodec.DecodeBatchBegin(read.Payload);
                    if (!decoded.TryGet(out var begin, out var decodeFailure)) return await AbortWith(decodeFailure).ConfigureAwait(false);
                    // Empty handler set: reject before any definition lookup.
                    if (options.Handlers.IsEmpty) return await AbortWith(new ProtocolFailure(ErrorCode.WorkerUnimplemented)).ConfigureAwait(false);
                    if (begin.Operations != hello.Operations || Digests.BatchDigest(begin) != hello.BatchDigest)
                        return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.Handshake)).ConfigureAwait(false);
                    if (begin.Operations > options.MaxOperations) return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.Size)).ConfigureAwait(false);
                    foreach (var item in begin.Items)
                    {
                        var handler = options.Handlers.Find(item.DefinitionId);
                        if (handler?.UnsupportedCode is { } unsupported) return await AbortWith(new ProtocolFailure(unsupported)).ConfigureAwait(false);
                        if (handler is null || item.DefinitionVersion != handler.Version || (item.Mode == PrepareMode.ApplyChoice && !handler.AcceptsChoice(item.ChoiceId)))
                            return await AbortWith(new ProtocolFailure(ErrorCode.DefinitionUnknown)).ConfigureAwait(false);
                    }
                    if (options.BeforeBatchAccepted is { } advance && !advance())
                        return await AbortWith(new ProtocolFailure(ErrorCode.TrustStoreInsecure)).ConfigureAwait(false);
                    batch = begin;
                    var sent = await Send(MessageType.BatchAccepted, WireCodec.Encode(new BatchAccepted(begin.Operations, options.TokenUserSid))).ConfigureAwait(false);
                    if (sent is not null) return End(SessionEnd.PeerClosed, sent);
                    state = SessionState.AwaitPrepare;
                    break;
                }
                case SessionState.AwaitPrepare:
                {
                    var decoded = WireCodec.DecodePrepare(read.Payload);
                    if (!decoded.TryGet(out var prepare, out var decodeFailure)) return await AbortWith(decodeFailure).ConfigureAwait(false);
                    var item = batch!.Items[index];
                    if (prepare.OperationId != item.OperationId) return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.State)).ConfigureAwait(false);
                    Prepared reply;
                    HandlerObservation observed;
                    try { (reply, observed) = BuildPrepared(item, t); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { return await AbortWith(new ProtocolFailure(ErrorCode.Unexpected)).ConfigureAwait(false); }
                    var sent = await Send(MessageType.Prepared, WireCodec.Encode(reply)).ConfigureAwait(false);
                    if (sent is not null) return End(SessionEnd.PeerClosed, sent);
                    if (reply.Status == ElevationStatus.Ready && reply.WillChange)
                    {
                        pending = (item, reply.PreparedDigest, observed);
                        state = SessionState.AwaitCommit;
                    }
                    else
                    {
                        state = SessionRules.EndsBatch(MessageType.Prepared, reply.Status) ? SessionState.AwaitBye : NextAfter(batch!, ref index);
                    }
                    break;
                }
                case SessionState.AwaitCommit:
                {
                    var decoded = WireCodec.DecodeCommit(read.Payload);
                    if (!decoded.TryGet(out var commit, out var decodeFailure)) return await AbortWith(decodeFailure).ConfigureAwait(false);
                    var p = pending!.Value;
                    if (commit.OperationId != p.Item.OperationId || !CryptographicOperations.FixedTimeEquals(commit.PreparedDigest.ToArray(), p.Digest.ToArray()))
                        return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.State)).ConfigureAwait(false);
                    if (!options.CommitSupported) return await AbortWith(new ProtocolFailure(ErrorCode.WorkerUnimplemented)).ConfigureAwait(false);
                    commitAccepted = true;
                    Outcome outcome;
                    try { outcome = BuildOutcome(p.Item, p.Observed); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { return await AbortWith(new ProtocolFailure(ErrorCode.Unexpected)).ConfigureAwait(false); }
                    var sent = await Send(MessageType.Outcome, WireCodec.Encode(outcome)).ConfigureAwait(false);
                    if (sent is not null) return End(SessionEnd.PeerClosed, sent);
                    pending = null;
                    state = SessionRules.EndsBatch(MessageType.Outcome, outcome.Status) ? SessionState.AwaitBye : NextAfter(batch!, ref index);
                    break;
                }
                case SessionState.AwaitBye:
                {
                    if (!WireCodec.DecodeBye(read.Payload).IsSuccess) return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.Framing)).ConfigureAwait(false);
                    var sent = await Send(MessageType.Bye, []).ConfigureAwait(false);
                    return sent is not null ? End(SessionEnd.PeerClosed, sent) : End(SessionEnd.Completed, byeReceived: true);
                }
                default:
                    return await AbortWith(ProtocolFailure.Channel(ProtocolDetail.State)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Counts the finished operation and returns the next state: AwaitPrepare, or AwaitBye after the last operation.</summary>
    private static SessionState NextAfter(BatchBegin batch, ref int index)
    {
        index++;
        return index >= batch.Operations ? SessionState.AwaitBye : SessionState.AwaitPrepare;
    }

    private (Prepared Reply, HandlerObservation Observed) BuildPrepared(BatchItem item, Bytes32 transcript)
    {
        var handler = options.Handlers.Find(item.DefinitionId)!;
        var observed = handler.Observe(item);
        ElevationStatus status;
        ElevationResult result;
        var willChange = false;
        if (item.Mode == PrepareMode.Restore && !handler.SupportsRestore)
        {
            status = ElevationStatus.Refused; result = ElevationResult.From(ErrorCode.NotReversible);
        }
        else
        {
            var expected = item.Mode == PrepareMode.ApplyChoice
                ? Digests.PlanTokenApply(item.DefinitionId, item.DefinitionVersion, item.ChoiceId, observed.Snapshot, observed.KeyRevision)
                : Digests.PlanTokenRestore(item.DefinitionId, item.DefinitionVersion, observed.Snapshot, observed.KeyRevision);
            if (expected != item.PlanTokenDigest)
            {
                status = ElevationStatus.Conflict;
                result = ElevationResult.From(item.Mode == PrepareMode.ApplyChoice ? ErrorCode.PlanStale : ErrorCode.ExternalConflict);
            }
            else
            {
                var plan = handler.Decide(item, observed);
                status = plan.Status; result = plan.Result; willChange = plan.WillChange && plan.Status == ElevationStatus.Ready;
            }
        }
        if (status == ElevationStatus.Applied || status == ElevationStatus.Partial || !ResultPolicy.IsAllowed(MessageType.Prepared, status, result))
        {
            status = ElevationStatus.Failed; result = ElevationResult.From(ErrorCode.Unexpected); willChange = false;
        }
        var digest = Digests.PreparedDigest(transcript, item.OperationId, item.PlanTokenDigest, observed.Snapshot, observed.KeyRevision, willChange);
        return (new Prepared(item.OperationId, status, result, observed.Snapshot, observed.KeyRevision, willChange, digest), observed);
    }

    private Outcome BuildOutcome(BatchItem item, HandlerObservation prepared)
    {
        var applied = options.Handlers.Find(item.DefinitionId)!.Apply(item, prepared);
        if (applied.Status is ElevationStatus.Ready or ElevationStatus.Refused || !ResultPolicy.IsAllowed(MessageType.Outcome, applied.Status, applied.Result))
            return new Outcome(item.OperationId, ElevationStatus.Failed, ElevationResult.From(ErrorCode.Unexpected), applied.After, applied.KeyRevision);
        return new Outcome(item.OperationId, applied.Status, applied.Result, applied.After, applied.KeyRevision);
    }

    /// <summary>Exit code of 13.2 for logs only: the client never relies on it.</summary>
    public static WorkerExitCode ExitFor(SessionEnd end, ProtocolFailure? failure)
    {
        if (end == SessionEnd.Completed) return WorkerExitCode.Finished;
        if (end == SessionEnd.TimedOut || end == SessionEnd.Cancelled) return WorkerExitCode.Timeout;
        return failure?.Code switch
        {
            ErrorCode.ElevationTimeout => WorkerExitCode.Timeout,
            ErrorCode.ElevationClientUntrusted => WorkerExitCode.TrustFailure,
            ErrorCode.WorkerUnimplemented or ErrorCode.DefinitionUnknown or ErrorCode.DefinitionSetMismatch or ErrorCode.TargetKindUnsupported => WorkerExitCode.DefinitionRefusal,
            _ => WorkerExitCode.ChannelFailure,
        };
    }

    public void Dispose() => handshake.Dispose();
}
