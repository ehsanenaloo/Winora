// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>Client identity as the worker observed it and as the client knows it about itself (technical-docs/protocol.md A.6 clientId).</summary>
public sealed record ClientIdentity(uint ProcessId, ulong StartFileTime, BinarySid UserSid, BinarySid LogonSid, uint SessionId);

/// <summary>Server identity as attested (A.6 serverId).</summary>
public sealed record ServerIdentity(uint ProcessId, ulong StartFileTime, uint SessionId);

/// <summary>Integrity level of a token, as plain data supplied by the platform layer.</summary>
public enum IntegrityLevel { Untrusted, Low, Medium, High, System }

/// <summary>
/// Facts about the process at the other end of a connection, as plain data. This project never reads the operating system: a platform
/// adapter fills the record from the kernel (token of the pipe peer, image path and signature of the process that created the peer end).
/// </summary>
public sealed record PeerFacts(
    uint ProcessId, ulong StartFileTime, BinarySid UserSid, BinarySid LogonSid, uint SessionId, bool IsElevated, IntegrityLevel Integrity,
    string? ImagePath, bool SignerMatches,
    BinarySid? ProcessTokenUserSid = null, BinarySid? ProcessTokenLogonSid = null)
{
    public ClientIdentity ToClientIdentity() => new(ProcessId, StartFileTime, UserSid, LogonSid, SessionId);
}

/// <summary>Injected provider of <see cref="PeerFacts"/> for the peer of one connection; null means the facts could not be determined (always a refusal).</summary>
public interface IPeerIdentity
{
    PeerFacts? Read();
}

/// <summary>What the worker expects of its client: the public command-line values and its own session (technical-docs/protocol.md 4.4, 7.2).</summary>
public sealed record ExpectedClient(
    BinarySid InitiatorSid, BinarySid InitiatorLogonSid, uint OwnSessionId, uint ClientProcessId, ulong ClientStartFileTime, IReadOnlyList<string> AllowedImagePaths);

/// <summary>Pure decisions over <see cref="PeerFacts"/> (technical-docs/protocol.md 7.1, 7.2).</summary>
public static class PeerVerifier
{
    /// <summary>
    /// Worker side, steps 1 to 3 of 7.2: token, image, tripwire. Returns null when the client may proceed, else the refusal
    /// (elevation.client_untrusted with token, integrity, session, image, signature or pid). PID and start time can only add refusals.
    /// </summary>
    public static ProtocolFailure? VerifyClient(PeerFacts? facts, ExpectedClient expected)
    {
        if (facts is null) return ProtocolFailure.ClientUntrusted("token");
        if (facts.UserSid != expected.InitiatorSid || facts.LogonSid != expected.InitiatorLogonSid || facts.IsElevated) return ProtocolFailure.ClientUntrusted("token");
        if (facts.SessionId != expected.OwnSessionId) return ProtocolFailure.ClientUntrusted("session");
        if (facts.Integrity != IntegrityLevel.Medium) return ProtocolFailure.ClientUntrusted("integrity");
        if (facts.ImagePath is null || !expected.AllowedImagePaths.Any(p => string.Equals(p, facts.ImagePath, StringComparison.OrdinalIgnoreCase))) return ProtocolFailure.ClientUntrusted("image");
        if (!facts.SignerMatches) return ProtocolFailure.ClientUntrusted("signature");
        if (facts.ProcessTokenUserSid is { } u && u != facts.UserSid) return ProtocolFailure.ClientUntrusted("token");
        if (facts.ProcessTokenLogonSid is { } l && l != facts.LogonSid) return ProtocolFailure.ClientUntrusted("token");
        if (facts.ProcessId != expected.ClientProcessId || facts.StartFileTime != expected.ClientStartFileTime) return ProtocolFailure.ClientUntrusted("pid");
        return null;
    }

    /// <summary>
    /// Client side, secondary checks of 6.2 and 7.1 step 3: when the facts could be read, pid, start time and image of the pipe server must equal
    /// the attested values and the verified worker path. Unavailable facts (null) accept nothing extra and refuse nothing; a mismatch refuses.
    /// </summary>
    public static ProtocolFailure? VerifyServerSecondary(PeerFacts? facts, AttestationRecord attestation, string expectedWorkerImage)
    {
        if (facts is null) return null;
        if (facts.ProcessId != attestation.WorkerProcessId || facts.StartFileTime != attestation.WorkerStartFileTime) return ProtocolFailure.ServerUntrusted();
        if (facts.ImagePath is not null && !string.Equals(facts.ImagePath, expectedWorkerImage, StringComparison.OrdinalIgnoreCase)) return ProtocolFailure.ServerUntrusted();
        return null;
    }
}
