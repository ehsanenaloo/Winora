// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

public enum AttestationPollKind { NotYet, Attestation, EarlyFailure, Rejected }

/// <summary>
/// One look at the run directory by the platform adapter: nothing yet, the raw bytes of <c>&lt;R&gt;.att</c>, the raw bytes of <c>&lt;R&gt;.fail</c>,
/// or a rejection decided by the file-system checks only the adapter can make (reparse point, link count, weak ACL, sharing).
/// </summary>
public readonly record struct AttestationPollResult(AttestationPollKind Kind, byte[]? Bytes = null, ProtocolFailure? Rejection = null)
{
    public static AttestationPollResult NotYet => new(AttestationPollKind.NotYet);
    public static AttestationPollResult Attestation(byte[] bytes) => new(AttestationPollKind.Attestation, bytes);
    public static AttestationPollResult EarlyFailure(byte[] bytes) => new(AttestationPollKind.EarlyFailure, bytes);
    public static AttestationPollResult Rejected(ProtocolFailure failure) => new(AttestationPollKind.Rejected, null, failure);
}

/// <summary>Injected reader of the admin-written records of technical-docs/protocol.md section 6. The Windows implementation lives in another package.</summary>
public interface IAttestationSource
{
    AttestationPollResult Poll(Bytes16 rendezvous);
}

/// <summary>What the client requires of an attestation (technical-docs/protocol.md 6.2).</summary>
public sealed record AttestationExpectation(ElevationFlavor Flavor, Bytes16 Rendezvous, Bytes32 DefinitionSetHash, uint SessionId);

public sealed record AttestationAcceptance(AttestationRecord? Record, ProtocolFailure? Failure)
{
    public bool IsAccepted => Record is not null;
}

/// <summary>The closed reasons of the early-failure record (6.4) and what the client surfaces for each.</summary>
public static class EarlyFailureMapping
{
    public static ProtocolFailure ToFailure(EarlyFailureReason reason) => reason switch
    {
        EarlyFailureReason.Busy => new(ErrorCode.WorkerBusy),
        EarlyFailureReason.Hardening => new(ErrorCode.WorkerHardeningFailed),
        EarlyFailureReason.Environment => new(ErrorCode.ElevationUnavailable, "environment"),
        EarlyFailureReason.Image => new(ErrorCode.ElevationInstallUntrusted, "hash"),
        EarlyFailureReason.RunDir => new(ErrorCode.ElevationInstallUntrusted, "run_dir"),
        EarlyFailureReason.Definition => new("definition.signature_invalid"),
        EarlyFailureReason.Rollback => new("definition.rollback"),
        EarlyFailureReason.FloorStore => new("trust.store_insecure"),
        EarlyFailureReason.SetMismatch => new(ErrorCode.DefinitionSetMismatch),
        EarlyFailureReason.Squatted => ProtocolFailure.Channel(ProtocolDetail.Squatted),
        EarlyFailureReason.Arguments => new(ErrorCode.ProtocolVersion),
        _ => new(ErrorCode.Unexpected),
    };

    /// <summary>Only <c>busy</c> is retryable; every other reason is shown as permanent (6.4).</summary>
    public static bool IsRetryable(EarlyFailureReason reason) => reason == EarlyFailureReason.Busy;

    /// <summary>Worker exit code of a startup failure (13.2): for humans and logs only.</summary>
    public static WorkerExitCode ExitCodeFor(EarlyFailureReason reason) => reason switch
    {
        EarlyFailureReason.Busy => WorkerExitCode.Busy,
        EarlyFailureReason.Arguments => WorkerExitCode.BadArguments,
        EarlyFailureReason.Definition or EarlyFailureReason.Rollback or EarlyFailureReason.FloorStore or EarlyFailureReason.SetMismatch => WorkerExitCode.DefinitionRefusal,
        EarlyFailureReason.Squatted => WorkerExitCode.ChannelFailure,
        EarlyFailureReason.Internal => WorkerExitCode.ChannelFailure,
        _ => WorkerExitCode.TrustFailure,
    };
}

/// <summary>
/// The client's wait for the attestation (technical-docs/protocol.md 4.5, 6.2): polls <c>.att</c> and <c>.fail</c> every 250 ms for at most 30 s of the
/// injected clock. The record is accepted only when it parses exactly and equals the client's expectations; the primary check, that the key
/// of ServerHello hashes to the attested value, happens in the handshake.
/// </summary>
public static class AttestationWaiter
{
    public static async Task<AttestationAcceptance> WaitAsync(IAttestationSource source, AttestationExpectation expected, TimeProvider time, CancellationToken ct)
    {
        using var deadline = new DeadlineScope(TimeSpan.FromMilliseconds(ElevationLimits.AttestationWaitMs), time, ct);
        try
        {
            while (true)
            {
                var poll = source.Poll(expected.Rendezvous);
                switch (poll.Kind)
                {
                    case AttestationPollKind.Rejected:
                        return new(null, poll.Rejection ?? ProtocolFailure.ServerUntrusted());
                    case AttestationPollKind.EarlyFailure:
                        {
                            var fail = WireCodec.DecodeEarlyFailure(poll.Bytes ?? []);
                            // A record that does not parse or does not belong to this rendezvous is not trusted (6.2: only records seen created for R).
                            if (!fail.IsSuccess || !fail.Value!.Matches(expected.Rendezvous)) return new(null, ProtocolFailure.ServerUntrusted());
                            return new(null, EarlyFailureMapping.ToFailure(fail.Value.Reason));
                        }
                    case AttestationPollKind.Attestation:
                        return Validate(WireCodec.DecodeAttestation(poll.Bytes ?? []), expected);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(ElevationLimits.RecordPollMs), time, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.Expired)
        {
            return new(null, ProtocolFailure.Timeout("attest"));
        }
    }

    private static AttestationAcceptance Validate(DecodeResult<AttestationRecord> decoded, AttestationExpectation e)
    {
        if (!decoded.TryGet(out var r, out _)) return new(null, ProtocolFailure.ServerUntrusted());
        if (r.Flavor != e.Flavor) return new(null, ProtocolFailure.Channel(ProtocolDetail.Flavor));
        if (r.Rendezvous != e.Rendezvous || r.SessionId != e.SessionId || HandshakeEngine.NegotiateVersion(r.ProtoMin, r.ProtoMax) is null) return new(null, ProtocolFailure.ServerUntrusted());
        if (r.DefinitionSetHash != e.DefinitionSetHash) return new(null, new ProtocolFailure(ErrorCode.DefinitionSetMismatch));
        return new(r, null);
    }
}
