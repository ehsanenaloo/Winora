// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>Client-side mirror of the session states (technical-docs/protocol.md 10.2 "the client mirrors it").</summary>
public enum ClientState { AwaitServerHello, AwaitFinished, AwaitBatchAccepted, AwaitPrepared, AwaitOutcome, AwaitBye }

/// <summary>
/// The allowed-message tables of the session state machines. They are data, so the state-machine exhaustion test can enumerate every
/// state against every message type.
/// </summary>
public static class SessionRules
{
    /// <summary>Messages the worker accepts in a state (10.2). A pre-key Abort is plaintext and advisory; after key derivation it is an ordinary frame.</summary>
    public static MessageTypeSet WorkerAllowed(SessionState state) => state switch
    {
        SessionState.AwaitHello => MessageTypeSet.Of(MessageType.ClientHello, MessageType.Abort),
        SessionState.AwaitFinished => MessageTypeSet.Of(MessageType.Finished),
        SessionState.AwaitBatchBegin => MessageTypeSet.Of(MessageType.BatchBegin),
        SessionState.AwaitPrepare => MessageTypeSet.Of(MessageType.Prepare, MessageType.Abort),
        SessionState.AwaitCommit => MessageTypeSet.Of(MessageType.Commit, MessageType.Abort),
        SessionState.AwaitBye => MessageTypeSet.Of(MessageType.Bye),
        _ => MessageTypeSet.None,
    };

    /// <summary>Messages the client accepts: the worker may send Abort at any point after key derivation.</summary>
    public static MessageTypeSet ClientAllowed(ClientState state) => state switch
    {
        ClientState.AwaitServerHello => MessageTypeSet.Of(MessageType.ServerHello, MessageType.Abort),
        ClientState.AwaitFinished => MessageTypeSet.Of(MessageType.Finished, MessageType.Abort),
        ClientState.AwaitBatchAccepted => MessageTypeSet.Of(MessageType.BatchAccepted, MessageType.Abort),
        ClientState.AwaitPrepared => MessageTypeSet.Of(MessageType.Prepared, MessageType.Abort),
        ClientState.AwaitOutcome => MessageTypeSet.Of(MessageType.Outcome, MessageType.Abort),
        ClientState.AwaitBye => MessageTypeSet.Of(MessageType.Bye, MessageType.Abort),
        _ => MessageTypeSet.None,
    };

    /// <summary>The detail token of the <c>elevation.timeout</c> raised when a worker state's timer expires (10.2).</summary>
    public static string TimeoutDetail(SessionState state) => state switch
    {
        SessionState.AwaitConnect => "connect",
        SessionState.AwaitPrepare => "idle",
        SessionState.AwaitCommit => "commit",
        _ => "frame",
    };

    /// <summary>True for the statuses after which the worker ends the batch without starting the remaining operations (10.2).</summary>
    public static bool EndsBatch(MessageType type, ElevationStatus status) => type switch
    {
        MessageType.Prepared => status is ElevationStatus.Failed or ElevationStatus.Conflict or ElevationStatus.Refused,
        MessageType.Outcome => status is ElevationStatus.Failed or ElevationStatus.Conflict or ElevationStatus.Partial,
        _ => false,
    };

    /// <summary>
    /// Client reply deadline for BatchAccepted, Prepared and Outcome. The design fixes worker-side timers only; the client mirrors the
    /// longest worker window (Prepared to Commit, 30 s) because a write plus RegFlushKey may take that long (implementation clarification).
    /// </summary>
    public static readonly TimeSpan ClientReplyWait = TimeSpan.FromMilliseconds(ElevationLimits.AwaitCommitMs);
}

/// <summary>
/// The per-message allow-list of Result codes (technical-docs/protocol.md 10.3). A receiver applies it to every Prepared, Outcome and Abort and
/// aborts the session (<c>elevation.channel_failed</c>, <c>code</c>) on a violation. Severity, retryability and the detail token are
/// compared with the local catalog, never trusted from the wire.
/// </summary>
public static class ResultPolicy
{
    private static readonly string[] Refused =
    [
        ErrorCode.SettingStateUnrecognized, ErrorCode.NotReversible, ErrorCode.DefinitionUnknown, "definition.revoked",
        "definition.restore_incompatible", "definition.restore_revoked",
    ];
    private static readonly string[] AbortExact =
    [
        ErrorCode.WorkerUnimplemented, ErrorCode.WorkerBusy, ErrorCode.WorkerHardeningFailed, "trust.store_insecure", ErrorCode.ProtocolSize,
        ErrorCode.ProtocolVersion, ErrorCode.ElevationClientUntrusted, ErrorCode.ElevationTimeout, ErrorCode.ElevationChannelFailed, ErrorCode.Unexpected,
        ErrorCode.Cancelled,   // the client's own Abort when the user cancels before Commit (12.4); see the protocol.md clarifications
        ErrorCode.TargetKindUnsupported,   // a definition of a kind the worker does not execute (display driver values)
    ];

    /// <summary>True when the code is on the allow-list of (<paramref name="type"/>, <paramref name="status"/>) and the block matches the local catalog.</summary>
    public static bool IsAllowed(MessageType type, ElevationStatus status, ElevationResult result)
    {
        if (!result.IsConsistentWithCatalog()) return false;
        var code = result.Code;
        return type switch
        {
            MessageType.Prepared => status switch
            {
                ElevationStatus.Ready => code == OutcomeCodes.Prepared,
                ElevationStatus.AlreadyConfigured => code == OutcomeCodes.AlreadyConfigured,
                ElevationStatus.Failed => FailedCode(code),
                ElevationStatus.Conflict => ConflictCode(code),
                ElevationStatus.Refused => Array.IndexOf(Refused, code) >= 0,
                _ => false,
            },
            MessageType.Outcome => status switch
            {
                ElevationStatus.Applied => code == OutcomeCodes.Applied,
                ElevationStatus.AlreadyConfigured => code == OutcomeCodes.AlreadyConfigured,
                ElevationStatus.Failed => FailedCode(code),
                ElevationStatus.Conflict => ConflictCode(code),
                ElevationStatus.Partial => code == ErrorCode.VerificationFailed,
                _ => false,
            },
            _ => false,
        };
    }

    /// <summary>The allow-list for <c>Abort</c> (10.3): the worker and protocol codes, all definition.* codes and error.unexpected.</summary>
    public static bool IsAllowedAbort(ElevationResult result) =>
        result.IsConsistentWithCatalog()
        && (Array.IndexOf(AbortExact, result.Code) >= 0 || result.Code.StartsWith("definition.", StringComparison.Ordinal));

    private static bool FailedCode(string code) => code is ErrorCode.AccessDenied or ErrorCode.Unexpected;
    private static bool ConflictCode(string code) => code is ErrorCode.PlanStale or ErrorCode.ExternalConflict;
}
