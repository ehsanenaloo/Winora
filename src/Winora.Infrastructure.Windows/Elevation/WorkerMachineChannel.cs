// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;
using EW = Winora.Elevation.Windows;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>
/// The client side of one machine batch (technical-docs/protocol.md 3 to 12): gates, launch, attestation, pipe connect, secondary server checks and the session.
/// The host has already shown the batch digest and obtained consent (<see cref="ElevationConfirmation"/>), and the executor has journaled the first row; this class
/// persists nothing and never asks the user anything. It implements <see cref="IMachineChannel"/> for the executor. The definition-set hash it commits to is the one of
/// the verified set the executor planned from, so a worker built with another set is refused in the handshake (<c>definition.set_mismatch</c>).
/// </summary>
public sealed class WorkerMachineChannel : IMachineChannel
{
    private readonly ElevationFlavor flavor;
    private readonly IInstallProbe install;
    private readonly IElevationEnvironmentProbe environment;
    private readonly WorkerLauncher launcher;
    private readonly IAttestationSource attestations;
    private readonly IPipeConnector pipes;
    private readonly IServerProcessInspector inspector;
    private readonly Func<Bytes32> setHash;
    private readonly TimeProvider time;
    private readonly IHandshakeEntropy entropy;
    private readonly IElevationRandom random;
    private readonly ElevationFlavor? buildFlavor;

    public WorkerMachineChannel(
        ElevationFlavor flavor, IInstallProbe install, IElevationEnvironmentProbe environment, WorkerLauncher launcher, IAttestationSource attestations,
        IPipeConnector pipes, IServerProcessInspector inspector, Func<Bytes32> setHash, TimeProvider? time = null, IHandshakeEntropy? entropy = null,
        IElevationRandom? random = null, ElevationFlavor? buildFlavor = null)
    {
        this.flavor = flavor;
        this.install = install ?? throw new ArgumentNullException(nameof(install));
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.attestations = attestations ?? throw new ArgumentNullException(nameof(attestations));
        this.pipes = pipes ?? throw new ArgumentNullException(nameof(pipes));
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        this.setHash = setHash ?? throw new ArgumentNullException(nameof(setHash));
        this.time = time ?? TimeProvider.System;
        this.entropy = entropy ?? SystemHandshakeEntropy.Instance;
        this.random = random ?? SystemElevationRandom.Instance;
        this.buildFlavor = buildFlavor;
    }

    /// <summary>The gate result a host evaluates before it shows the consent window (also tells whether Windows elevates without a prompt).</summary>
    public GateEvaluation Preflight()
    {
        var gates = ElevationGates.Evaluate(flavor, install.Read(), environment.Read(), buildFlavor);
        return new(gates.FirstFailure is null, gates.FirstFailure?.Code, gates.FirstFailure?.Detail ?? "", gates.NoConsentPrompt);
    }

    public async Task<MachineBatchResult> RunBatchAsync(BatchBegin batch, MachineBeforeCommit beforeCommit, bool secondConfirmationGiven, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(beforeCommit);
        var launched = false;
        {
            var installFacts = install.Read();
            var env = environment.Read();
            var gates = ElevationGates.Evaluate(flavor, installFacts, env, buildFlavor);
            if (gates.FirstFailure is { } failed) return Fail(failed.Code ?? ErrorCode.ElevationUnavailable, launched);
            if (gates.NoConsentPrompt && !secondConfirmationGiven) return Fail(ErrorCode.ElevationConfirmationRequired, launched);

            var rendezvous = random.NewRendezvous();
            var batchDigest = Digests.BatchDigest(batch);
            var hash = setHash();
            var plan = new WorkerLaunchPlan(rendezvous, env.ClientProcessId, env.ClientStartFileTime, env.ClientUserSid!, env.ClientLogonSid!, batchDigest, batch.Operations,
                gates.NoConsentPrompt, SecondConfirmationGiven: gates.NoConsentPrompt && secondConfirmationGiven);
            launched = true;
            var launch = launcher.Launch(plan, installFacts);
            if (!launch.Started) return Fail(launch.Code ?? ErrorCode.ElevationLaunchFailed, launched);

            var userSid = new BinarySid(EW.SidText.ToBinary(env.ClientUserSid!) ?? throw new InvalidOperationException("sid was validated by the gates"));
            var logonSid = new BinarySid(EW.SidText.ToBinary(env.ClientLogonSid!) ?? throw new InvalidOperationException("sid was validated by the gates"));
            var expectation = new AttestationExpectation(flavor, rendezvous, hash, env.SessionId);
            var attested = await AttestationWaiter.WaitAsync(attestations, expectation, time, cancellationToken).ConfigureAwait(false);
            if (attested.Record is not { } attestation) return FailProtocol(attested.Failure ?? ProtocolFailure.ServerUntrusted(), launched);

            if (installFacts.WorkerFileId is not null && installFacts.WorkerImagePath is not null && install.ReadWorkerFileId(installFacts.WorkerImagePath) != installFacts.WorkerFileId)
                return Fail(ErrorCode.ElevationInstallUntrusted, launched);

            PipeConnection connection;
            try { connection = await pipes.ConnectAsync(rendezvous, flavor, TimeSpan.FromMilliseconds(ElevationLimits.ConnectWaitMs), cancellationToken).ConfigureAwait(false); }
            catch (PipeConnectException e) { return Fail(e.Code, launched); }
            var refusal = VerifyServer(connection, attestation, installFacts);
            if (refusal is not null)
            {
                connection.Channel.Close();
                return FailProtocol(refusal, launched);
            }

            var self = new ClientIdentity(env.ClientProcessId, env.ClientStartFileTime, userSid, logonSid, env.SessionId);
            var options = new ClientSessionOptions(flavor, rendezvous, self, attestation, hash, batch, entropy, time,
                BeforeCommit: (item, prepared, _) => beforeCommit(item, prepared));
            var session = await ClientSession.RunAsync(connection.Channel, options, cancellationToken).ConfigureAwait(false);
            return Map(session, launched);
        }
    }

    private static MachineBatchResult Map(ClientSessionResult session, bool launched)
    {
        var operations = session.Operations.Select(o => new MachineBatchOperation(o.Item, o.Prepared, o.Outcome, o.CommitSent)).ToList();
        string? code = null;
        if (!session.IsCompleted && !session.DeclinedByCaller)
        {
            if (session.End == SessionEnd.PeerAborted && session.PeerAbort is { } abort) code = abort.Code;
            else
            {
                var failure = session.Failure ?? (session.End == SessionEnd.TimedOut ? ProtocolFailure.Timeout("frame") : ProtocolFailure.Channel(ProtocolDetail.Framing));
                code = failure.TryToResult(out var result) ? result.Code : ErrorCode.Unexpected;
            }
        }
        return new(launched, code, session.DeclinedByCaller, session.CommitSent, operations);
    }

    /// <summary>Secondary evidence about the server (protocol.md 6.2, 7.1 step 3): it can only add refusals; the attestation key match stays the primary check.</summary>
    private ProtocolFailure? VerifyServer(PipeConnection connection, AttestationRecord attestation, InstallFacts installFacts)
    {
        if (connection.ServerProcessId is { } reported && reported != attestation.WorkerProcessId) return ProtocolFailure.ServerUntrusted();
        var inspection = inspector.Inspect(connection.ServerProcessId);
        if (inspection.TokenElevated == false) return ProtocolFailure.ServerUntrusted();
        var workerPath = WorkerLauncher.FixedWorkerPath(installFacts) ?? "";
        return PeerVerifier.VerifyServerSecondary(inspection.Facts, attestation, workerPath);
    }

    private static MachineBatchResult Fail(string code, bool launched) => new(launched, code, false, false, []);

    private static MachineBatchResult FailProtocol(ProtocolFailure failure, bool launched) =>
        Fail(failure.TryToResult(out var result) ? result.Code : ErrorCode.Unexpected, launched);
}

/// <summary>What the host learns before it shows the consent window.</summary>
public sealed record GateEvaluation(bool Passed, string? Code, string Detail, bool NoConsentPrompt);
