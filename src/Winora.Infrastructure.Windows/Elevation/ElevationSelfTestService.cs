// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;
using EW = Winora.Elevation.Windows;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>Source of the per-launch random values. The system implementation uses the platform CSPRNG; tests inject fixed values.</summary>
public interface IElevationRandom
{
    Bytes16 NewRendezvous();
    Guid NewOperationId();
    /// <summary>A payload for the echo choice: <c>[a-z0-9]{1,32}</c>.</summary>
    string NewEchoPayload();
}

public sealed class SystemElevationRandom : IElevationRandom
{
    public static SystemElevationRandom Instance { get; } = new();
    public Bytes16 NewRendezvous() => new(RandomNumberGenerator.GetBytes(ElevationLimits.RendezvousBytes));
    public Guid NewOperationId()
    {
        Guid id;
        do { id = new Guid(RandomNumberGenerator.GetBytes(16)); } while (id == Guid.Empty);
        return id;
    }
    public string NewEchoPayload() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
}

/// <summary>
/// Client orchestration of the M4a channel self-test (technical-docs/protocol.md 19): gates, confirmation, gates again, launch, attestation wait, pipe
/// connect, secondary server checks, then <see cref="ClientSession"/> with a batch of one harmless typed no-op (<c>echo.&lt;random&gt;</c>:
/// the worker answers AlreadyConfigured with the payload it parsed; nothing is committed and nothing on the machine changes).
/// It has no journal, store, registry-writer or shell-refresh dependency: the self-test records nothing, anywhere. Every refusal before the
/// launch leaves <see cref="ElevationSelfTestResult.LaunchAttempted"/> false.
/// </summary>
public sealed class ElevationSelfTestService : IElevationSelfTest, IDisposable
{
    private readonly ElevationFlavor flavor;
    private readonly IInstallProbe install;
    private readonly IElevationEnvironmentProbe environment;
    private readonly WorkerLauncher launcher;
    private readonly IAttestationSource attestations;
    private readonly IPipeConnector pipes;
    private readonly IServerProcessInspector inspector;
    private readonly IElevationConfirmation confirmation;
    private readonly TimeProvider time;
    private readonly IHandshakeEntropy entropy;
    private readonly IElevationRandom random;
    private readonly ElevationFlavor? buildFlavor;

    public ElevationSelfTestService(
        ElevationFlavor flavor, IInstallProbe install, IElevationEnvironmentProbe environment, WorkerLauncher launcher, IAttestationSource attestations,
        IPipeConnector pipes, IServerProcessInspector inspector, IElevationConfirmation confirmation, TimeProvider? time = null,
        IHandshakeEntropy? entropy = null, IElevationRandom? random = null, ElevationFlavor? buildFlavor = null)
    {
        this.flavor = flavor;
        this.install = install ?? throw new ArgumentNullException(nameof(install));
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.attestations = attestations ?? throw new ArgumentNullException(nameof(attestations));
        this.pipes = pipes ?? throw new ArgumentNullException(nameof(pipes));
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        this.time = time ?? TimeProvider.System;
        this.entropy = entropy ?? SystemHandshakeEntropy.Instance;
        this.random = random ?? SystemElevationRandom.Instance;
        this.buildFlavor = buildFlavor;
    }

    public void Dispose() => (install as IDisposable)?.Dispose();

    public async Task<ElevationSelfTestResult> RunNoopAsync(CancellationToken cancellationToken)
    {
        var launched = false;
        try
        {
            // 1. Gates, before any UI or launch (protocol.md 3).
            var installFacts = install.Read();
            var env = environment.Read();
            var gates = ElevationGates.Evaluate(flavor, installFacts, env, buildFlavor);
            if (gates.FirstFailure is { } failedGate) return Refusal(failedGate, ElevationSelfTestStage.Gates, launched: false);

            // 2. The batch: one operation, the echo choice with a random payload. Its digest is shown and is on the visible command line.
            var rendezvous = random.NewRendezvous();
            var choice = SelftestChoice.Echo(random.NewEchoPayload());
            var expected = MachineSnapshot.Selftest(choice.ChoiceId);
            var item = new BatchItem(random.NewOperationId(), SelftestNoop.DefinitionId, SelftestNoop.DefinitionVersion, PrepareMode.ApplyChoice, choice.ChoiceId,
                MachineValue.Absent, Digests.PlanTokenApply(SelftestNoop.DefinitionId, SelftestNoop.DefinitionVersion, choice.ChoiceId, expected, 0));
            var batch = new BatchBegin([item]);
            var batchDigest = Digests.BatchDigest(batch);
            var setHash = M4aDefinitionSet.Hash(flavor);

            // 3. Confirmation by the host; a non-interactive host never elevates (D-12).
            var answer = await confirmation.ConfirmAsync(new ElevationConfirmationRequest(batchDigest.ShortHex(), setHash.ShortHex(), gates.NoConsentPrompt), cancellationToken).ConfigureAwait(false);
            if (answer == ElevationConfirmationAnswer.NonInteractive) return Failure(ErrorCode.ElevationConfirmationRequired, "", ElevationSelfTestStage.Confirmation);
            if (answer != ElevationConfirmationAnswer.Confirmed) return Failure(ErrorCode.ElevationDeclined, "", ElevationSelfTestStage.Confirmation);

            // 4. The gates again immediately before the launch (protocol.md 3, 4.5).
            installFacts = install.Read();
            env = environment.Read();
            gates = ElevationGates.Evaluate(flavor, installFacts, env, buildFlavor);
            if (gates.FirstFailure is { } failedAgain) return Refusal(failedAgain, ElevationSelfTestStage.Gates, launched: false);

            // 5. Launch. The worker path comes from the install facts only.
            var plan = new WorkerLaunchPlan(rendezvous, env.ClientProcessId, env.ClientStartFileTime, env.ClientUserSid!, env.ClientLogonSid!, batchDigest, batch.Operations,
                gates.NoConsentPrompt, SecondConfirmationGiven: gates.NoConsentPrompt && answer == ElevationConfirmationAnswer.Confirmed);
            launched = true;
            var launch = launcher.Launch(plan, installFacts);
            if (!launch.Started) return new(false, launch.Code ?? ErrorCode.ElevationLaunchFailed, launch.Detail, ElevationSelfTestStage.Launch, LaunchAttempted: true, ChannelEstablished: false);

            // 6. Wait for the administrator-written attestation (protocol.md 6).
            var userSid = BinarySidOf(env.ClientUserSid!);
            var logonSid = BinarySidOf(env.ClientLogonSid!);
            var expectation = new AttestationExpectation(flavor, rendezvous, setHash, env.SessionId);
            var attested = await AttestationWaiter.WaitAsync(attestations, expectation, time, cancellationToken).ConfigureAwait(false);
            if (attested.Record is not { } attestation)
                return FromProtocol(attested.Failure ?? ProtocolFailure.ServerUntrusted(), ElevationSelfTestStage.Attestation, launched, channel: false);

            // Post-launch file identity (4.5): the worker path re-read now must be the file the guard verified.
            if (installFacts.WorkerFileId is not null && installFacts.WorkerImagePath is not null && install.ReadWorkerFileId(installFacts.WorkerImagePath) != installFacts.WorkerFileId)
                return Failure(ErrorCode.ElevationInstallUntrusted, "hash", ElevationSelfTestStage.Attestation, launched);

            // 7. Connect and verify the server BEFORE any byte is sent (7.1 steps 2 and 3).
            PipeConnection connection;
            try { connection = await pipes.ConnectAsync(rendezvous, flavor, TimeSpan.FromMilliseconds(ElevationLimits.ConnectWaitMs), cancellationToken).ConfigureAwait(false); }
            catch (PipeConnectException e) { return Failure(e.Code, e.Detail, ElevationSelfTestStage.Connect, launched); }
            var refusal = VerifyServer(connection, attestation, installFacts);
            if (refusal is not null)
            {
                connection.Channel.Close();
                return FromProtocol(refusal, ElevationSelfTestStage.Connect, launched, channel: false);
            }

            // 8. The session. The BeforeCommit hook is a marker only: M4a has no journal and the echo choice never commits.
            var self = new ClientIdentity(env.ClientProcessId, env.ClientStartFileTime, userSid, logonSid, env.SessionId);
            var options = new ClientSessionOptions(flavor, rendezvous, self, attestation, setHash, batch, entropy, time, BeforeCommit: static (_, _, _) => ValueTask.FromResult(true));
            var session = await ClientSession.RunAsync(connection.Channel, options, cancellationToken).ConfigureAwait(false);
            return Map(session, item, expected, launched);
        }
        catch (OperationCanceledException)
        {
            return new(false, ErrorCode.Cancelled, "", ElevationSelfTestStage.Session, launched, false);
        }
    }

    private static ElevationSelfTestResult Map(ClientSessionResult session, BatchItem item, MachineSnapshot expected, bool launched)
    {
        var established = session.Transcript != default;
        if (session.End == SessionEnd.PeerAborted && session.PeerAbort is { } abort)
            return new(false, abort.Code, abort.Detail, ElevationSelfTestStage.Session, launched, established);
        if (!session.IsCompleted)
        {
            var failure = session.Failure ?? (session.End == SessionEnd.TimedOut ? ProtocolFailure.Timeout("frame") : ProtocolFailure.Channel(ProtocolDetail.Framing));
            return FromProtocol(failure, ElevationSelfTestStage.Session, launched, established);
        }
        // The only acceptable answer to the echo no-op: AlreadyConfigured, nothing to commit, and the very payload that was sent.
        var ok = session.Operations.Count == 1 && !session.CommitSent && session.Operations[0] is { Outcome: null } op && op.Item.OperationId == item.OperationId
            && op.Prepared.Status == ElevationStatus.AlreadyConfigured && !op.Prepared.WillChange && op.Prepared.Observed.Equals(expected);
        return ok
            ? new(true, OutcomeCodes.AlreadyConfigured, "", ElevationSelfTestStage.Completed, launched, true)
            : new(false, ErrorCode.ElevationChannelFailed, ProtocolDetail.Framing, ElevationSelfTestStage.Session, launched, established);
    }

    /// <summary>Secondary evidence about the server (protocol.md 6.2, 7.1 step 3): it can only add refusals; the attestation key match stays the primary check.</summary>
    private ProtocolFailure? VerifyServer(PipeConnection connection, AttestationRecord attestation, InstallFacts installFacts)
    {
        if (connection.ServerProcessId is { } reported && reported != attestation.WorkerProcessId) return ProtocolFailure.ServerUntrusted();
        var inspection = inspector.Inspect(connection.ServerProcessId);
        if (inspection.TokenElevated == false) return ProtocolFailure.ServerUntrusted();   // the real worker is elevated; an unelevated copy is not
        var workerPath = WorkerLauncher.FixedWorkerPath(installFacts) ?? "";
        return PeerVerifier.VerifyServerSecondary(inspection.Facts, attestation, workerPath);
    }

    private static BinarySid BinarySidOf(string text) => new(EW.SidText.ToBinary(text) ?? throw new InvalidOperationException("sid was validated by the gates"));

    private static ElevationSelfTestResult Refusal(GateResult gate, ElevationSelfTestStage stage, bool launched) =>
        new(false, gate.Code ?? ErrorCode.ElevationUnavailable, gate.Detail, stage, launched, false, gate.Id);

    private static ElevationSelfTestResult Failure(string code, string detail, ElevationSelfTestStage stage, bool launched = false) =>
        new(false, code, detail, stage, launched, false);

    private static ElevationSelfTestResult FromProtocol(ProtocolFailure failure, ElevationSelfTestStage stage, bool launched, bool channel)
    {
        var known = failure.TryToResult(out var result) ? result : ElevationResult.From(ErrorCode.Unexpected);
        return new(false, known.Code, known.Detail, stage, launched, channel);
    }
}
