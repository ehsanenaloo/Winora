// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>One accepted pipe connection: its byte channel and the injected source of facts about the peer. <see cref="Disconnect"/> frees the single pipe instance for the next client.</summary>
public interface IWorkerConnection
{
    IFrameChannel Channel { get; }
    IPeerIdentity Peer { get; }
    void Disconnect();
}

/// <summary>The listening side of the single pipe instance; implemented by the Windows package, faked in tests.</summary>
public interface IConnectionAcceptor
{
    ValueTask<IWorkerConnection> AcceptAsync(CancellationToken cancellationToken);
}

public sealed class WorkerHostResult
{
    public WorkerSessionResult? Session { get; init; }
    public SessionEnd End { get; init; }
    public ProtocolFailure? Failure { get; init; }
    public int RejectedConnections { get; init; }
    public WorkerExitCode ExitCode { get; init; }
}

/// <summary>
/// The worker's connect loop (technical-docs/protocol.md 5, 7.2): waits up to 30 s for an authentic client, tolerates 3 identity-failed connections
/// and ends on the 4th with <c>budget_exhausted</c>, serves the first connection that passes and never accepts again. The 5-minute worker
/// lifetime caps everything. All timers use the injected clock.
/// </summary>
public sealed class WorkerHost(WorkerSession session, ExpectedClient expected, TimeProvider time, TimeSpan? lifetimeCap = null)
{
    private readonly TimeSpan cap = lifetimeCap ?? TimeSpan.FromMilliseconds(ElevationLimits.WorkerLifetimeMs);

    public async Task<WorkerHostResult> RunAsync(IConnectionAcceptor acceptor, CancellationToken ct)
    {
        using var lifetime = new DeadlineScope(cap, time, ct);
        var rejected = 0;
        try
        {
            using var connect = new DeadlineScope(TimeSpan.FromMilliseconds(ElevationLimits.ConnectWaitMs), time, lifetime.Token);
            while (true)
            {
                IWorkerConnection connection;
                try { connection = await acceptor.AcceptAsync(connect.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.Expired) { return Timeout("lifetime", rejected); }
                catch (OperationCanceledException) when (connect.Expired) { return Timeout("connect", rejected); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return Failed(ProtocolFailure.Channel(ProtocolDetail.Framing), rejected); }

                var facts = connection.Peer.Read();
                var refusal = PeerVerifier.VerifyClient(facts, expected);
                if (refusal is not null)
                {
                    connection.Disconnect();
                    rejected++;
                    if (rejected > ElevationLimits.RejectedConnectBudget) return Failed(ProtocolFailure.Channel(ProtocolDetail.BudgetExhausted), rejected);
                    continue;
                }

                var served = await session.ServeAsync(connection.Channel, facts!.ToClientIdentity(), lifetime.Token).ConfigureAwait(false);
                if (served.End == SessionEnd.Cancelled && lifetime.Expired)
                    return new() { Session = served, End = SessionEnd.TimedOut, Failure = ProtocolFailure.Timeout("lifetime"), RejectedConnections = rejected, ExitCode = WorkerExitCode.Timeout };
                return new() { Session = served, End = served.End, Failure = served.Failure, RejectedConnections = rejected, ExitCode = served.ExitCode };
            }
        }
        catch (OperationCanceledException) when (lifetime.Expired)
        {
            return Timeout("lifetime", rejected);
        }
        catch (OperationCanceledException)
        {
            return new() { End = SessionEnd.Cancelled, RejectedConnections = rejected, ExitCode = WorkerExitCode.Timeout };
        }
    }

    private static WorkerHostResult Timeout(string detail, int rejected) =>
        new() { End = SessionEnd.TimedOut, Failure = ProtocolFailure.Timeout(detail), RejectedConnections = rejected, ExitCode = WorkerExitCode.Timeout };

    private static WorkerHostResult Failed(ProtocolFailure failure, int rejected) =>
        new() { End = SessionEnd.Failed, Failure = failure, RejectedConnections = rejected, ExitCode = WorkerSession.ExitFor(SessionEnd.Failed, failure) };
}
