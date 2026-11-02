// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Domain.Elevation;

/// <summary>
/// Closed detail tokens of. A detail is a persisted, validated subcode, never free text; codes not listed here
/// accept no detail.
/// </summary>
public static class ElevationDetail
{
    private static readonly IReadOnlyList<string> None = Array.AsReadOnly(Array.Empty<string>());
    private static readonly IReadOnlyList<string> Unavailable = Array.AsReadOnly(new[] { "environment", "logon_sid", "session", "testhook", "uac_state" });
    private static readonly IReadOnlyList<string> InstallUntrusted = Array.AsReadOnly(new[] { "acl", "reparse", "location", "volume", "run_dir", "chain_offline", "hash", "signer" });
    private static readonly IReadOnlyList<string> ClientUntrusted = Array.AsReadOnly(new[] { "token", "integrity", "session", "image", "signature", "pid" });
    private static readonly IReadOnlyList<string> ChannelFailed = Array.AsReadOnly(new[] { "handshake", "squatted", "framing", "size", "counter", "tag", "state", "code", "flavor", "version", "budget_exhausted" });
    private static readonly IReadOnlyList<string> Timeout = Array.AsReadOnly(new[] { "attest", "connect", "frame", "idle", "lifetime", "commit" });

    public static IReadOnlyList<string> AllowedFor(string code) => code switch
    {
        ErrorCode.ElevationUnavailable => Unavailable,
        ErrorCode.ElevationInstallUntrusted => InstallUntrusted,
        ErrorCode.ElevationClientUntrusted => ClientUntrusted,
        ErrorCode.ElevationChannelFailed => ChannelFailed,
        ErrorCode.ElevationTimeout => Timeout,
        _ => None,
    };

    /// <summary>The empty detail is always allowed; otherwise the token must be in the closed list for the code.</summary>
    public static bool IsAllowed(string code, string detail) => detail.Length == 0 || AllowedFor(code).Contains(detail);
}

/// <summary>
/// Wire status block (A.3.3): code, severity, retryable and a closed detail token. The values are advisory copies of the receiver's own
/// catalog; <see cref="IsConsistentWithCatalog"/> is the check a receiver applies. Immutable.
/// </summary>
public sealed record ElevationResult
{
    public string Code { get; }
    public ErrorSeverity Severity { get; }
    public bool Retryable { get; }
    public string Detail { get; }

    public ElevationResult(string code, ErrorSeverity severity, bool retryable, string detail = "")
    {
        if (!ElevationIdentifier.IsValid(code)) throw new ArgumentException("result code is out of grammar", nameof(code));
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        if (detail is null || (detail.Length != 0 && !ElevationIdentifier.IsValidToken(detail))) throw new ArgumentException("detail is out of grammar", nameof(detail));
        Code = code; Severity = severity; Retryable = retryable; Detail = detail;
    }

    /// <summary>Builds the block for a catalog code or a persisted informational code, copying severity and retryability from the catalog.</summary>
    public static ElevationResult From(string code, string detail = "")
    {
        if (OutcomeCodes.IsInformational(code)) return new(code, ErrorSeverity.Info, false, detail);
        if (!ErrorCatalog.TryGet(code, out var info)) throw new ArgumentException("code is not in the catalog", nameof(code));
        return new(info.Code, info.Severity, info.Retryable, detail);
    }

    /// <summary>True when code, severity and retryable equal the local catalog and the detail is allowed for the code.</summary>
    public bool IsConsistentWithCatalog()
    {
        if (!ElevationDetail.IsAllowed(Code, Detail)) return false;
        if (OutcomeCodes.IsInformational(Code)) return Severity == ErrorSeverity.Info && !Retryable;
        return ErrorCatalog.TryGet(Code, out var info) && info.Severity == Severity && info.Retryable == Retryable;
    }
}

// ---- Message DTOs. Immutable; construction enforces the bounds of the grammar so an out-of-range
// ---- message cannot exist. Encoding and decoding belong to the protocol library. No property accepts a path, command or free text.

public sealed record ClientHello
{
    public byte ProtoMin { get; }
    public byte ProtoMax { get; }
    public ElevationFlavor Flavor { get; }
    public Bytes16 Rendezvous { get; }
    public Bytes32 ClientNonce { get; }
    public PublicPoint65 ClientPublicKey { get; }
    public Bytes32 DefinitionSetHash { get; }
    public Bytes32 BatchDigest { get; }
    public byte Operations { get; }
    public ClientHello(byte protoMin, byte protoMax, ElevationFlavor flavor, Bytes16 rendezvous, Bytes32 clientNonce, PublicPoint65 clientPublicKey,
        Bytes32 definitionSetHash, Bytes32 batchDigest, byte operations)
    {
        Guard.ProtocolRange(protoMin, protoMax); Guard.Flavor(flavor); Guard.Operations(operations);
        ProtoMin = protoMin; ProtoMax = protoMax; Flavor = flavor; Rendezvous = rendezvous; ClientNonce = clientNonce; ClientPublicKey = clientPublicKey;
        DefinitionSetHash = definitionSetHash; BatchDigest = batchDigest; Operations = operations;
    }
}

public sealed record ServerHello
{
    public byte ProtoMin { get; }
    public byte ProtoMax { get; }
    public ElevationFlavor Flavor { get; }
    public Bytes16 Rendezvous { get; }
    public Bytes32 ServerNonce { get; }
    public PublicPoint65 ServerPublicKey { get; }
    public Bytes32 DefinitionSetHash { get; }
    public byte Chosen { get; }
    public ServerHello(byte protoMin, byte protoMax, ElevationFlavor flavor, Bytes16 rendezvous, Bytes32 serverNonce, PublicPoint65 serverPublicKey,
        Bytes32 definitionSetHash, byte chosen)
    {
        Guard.ProtocolRange(protoMin, protoMax); Guard.Flavor(flavor);
        if (chosen < protoMin || chosen > protoMax) throw new ArgumentOutOfRangeException(nameof(chosen));
        ProtoMin = protoMin; ProtoMax = protoMax; Flavor = flavor; Rendezvous = rendezvous; ServerNonce = serverNonce; ServerPublicKey = serverPublicKey;
        DefinitionSetHash = definitionSetHash; Chosen = chosen;
    }
}

/// <summary>Key confirmation (type 0x10): the plaintext is the transcript hash T.</summary>
public sealed record Finished(Bytes32 Transcript);

/// <summary>One operation of a batch (A.3.5). In mode 1 the choice id is empty and the restore value is the value to write; in mode 0 the restore value is Absent.</summary>
public sealed record BatchItem
{
    public Guid OperationId { get; }
    public string DefinitionId { get; }
    public uint DefinitionVersion { get; }
    public PrepareMode Mode { get; }
    public string ChoiceId { get; }
    public MachineValue RestoreValue { get; }
    public Bytes32 PlanTokenDigest { get; }
    public BatchItem(Guid operationId, string definitionId, uint definitionVersion, PrepareMode mode, string choiceId, MachineValue restoreValue, Bytes32 planTokenDigest)
    {
        Guard.OperationId(operationId);
        if (!ElevationIdentifier.IsValid(definitionId)) throw new ArgumentException("definition id is out of grammar", nameof(definitionId));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ArgumentNullException.ThrowIfNull(restoreValue);
        if (mode == PrepareMode.ApplyChoice)
        {
            if (!ElevationIdentifier.IsValid(choiceId)) throw new ArgumentException("choice id is out of grammar", nameof(choiceId));
            if (restoreValue.Kind != MachineValueKind.Absent) throw new ArgumentException("apply mode carries no restore value", nameof(restoreValue));
        }
        else if (choiceId is null || choiceId.Length != 0) throw new ArgumentException("restore mode carries no choice id", nameof(choiceId));
        OperationId = operationId; DefinitionId = definitionId; DefinitionVersion = definitionVersion; Mode = mode; ChoiceId = choiceId;
        RestoreValue = restoreValue; PlanTokenDigest = planTokenDigest;
    }
}

/// <summary>The whole batch (type 0x20): 1 to 8 distinct operations in execution order. The session enforces the cap of one.</summary>
public sealed record BatchBegin
{
    private readonly BatchItem[] items;
    public IReadOnlyList<BatchItem> Items => items;
    public byte Operations => (byte)items.Length;
    public BatchBegin(IEnumerable<BatchItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copy = items.ToArray();
        if (copy.Length is < 1 or > ElevationLimits.MaxBatchOperations) throw new ArgumentException("a batch holds 1 to 8 operations", nameof(items));
        if (copy.Any(i => i is null)) throw new ArgumentException("batch item is null", nameof(items));
        if (copy.Select(i => i.OperationId).Distinct().Count() != copy.Length) throw new ArgumentException("operation ids must be distinct", nameof(items));
        this.items = copy;
    }
    public bool Equals(BatchBegin? other) => other is not null && items.AsSpan().SequenceEqual(other.items);
    public override int GetHashCode() { var h = new HashCode(); foreach (var i in items) h.Add(i); return h.ToHashCode(); }
}

/// <summary>Type 0x21. Carries the worker's token user SID for diagnostics only.</summary>
public sealed record BatchAccepted
{
    public byte Operations { get; }
    public BinarySid WorkerTokenUserSid { get; }
    public BatchAccepted(byte operations, BinarySid workerTokenUserSid) { Guard.Operations(operations); Operations = operations; WorkerTokenUserSid = workerTokenUserSid; }
}

/// <summary>Type 0x30. Everything else was committed by BatchBegin and the batch digest.</summary>
public sealed record Prepare
{
    public Guid OperationId { get; }
    public Prepare(Guid operationId) { Guard.OperationId(operationId); OperationId = operationId; }
}

/// <summary>Type 0x31. Status is one of Ready, AlreadyConfigured, Failed, Conflict, Refused; WillChange is true only with Ready (A.4).</summary>
public sealed record Prepared
{
    public Guid OperationId { get; }
    public ElevationStatus Status { get; }
    public ElevationResult Result { get; }
    public MachineSnapshot Observed { get; }
    public ulong KeyRevision { get; }
    public bool WillChange { get; }
    public Bytes32 PreparedDigest { get; }
    public Prepared(Guid operationId, ElevationStatus status, ElevationResult result, MachineSnapshot observed, ulong keyRevision, bool willChange, Bytes32 preparedDigest)
    {
        Guard.OperationId(operationId);
        if (status is not (ElevationStatus.Ready or ElevationStatus.AlreadyConfigured or ElevationStatus.Failed or ElevationStatus.Conflict or ElevationStatus.Refused))
            throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(observed);
        if (willChange && status != ElevationStatus.Ready) throw new ArgumentException("willChange is allowed only with status Ready", nameof(willChange));
        OperationId = operationId; Status = status; Result = result; Observed = observed; KeyRevision = keyRevision; WillChange = willChange; PreparedDigest = preparedDigest;
    }
}

/// <summary>Type 0x32.</summary>
public sealed record Commit
{
    public Guid OperationId { get; }
    public Bytes32 PreparedDigest { get; }
    public Commit(Guid operationId, Bytes32 preparedDigest) { Guard.OperationId(operationId); OperationId = operationId; PreparedDigest = preparedDigest; }
}

/// <summary>Type 0x33. Status is one of Applied, AlreadyConfigured, Failed, Conflict, Partial.</summary>
public sealed record Outcome
{
    public Guid OperationId { get; }
    public ElevationStatus Status { get; }
    public ElevationResult Result { get; }
    public MachineSnapshot After { get; }
    public ulong KeyRevision { get; }
    public Outcome(Guid operationId, ElevationStatus status, ElevationResult result, MachineSnapshot after, ulong keyRevision)
    {
        Guard.OperationId(operationId);
        if (status is not (ElevationStatus.Applied or ElevationStatus.AlreadyConfigured or ElevationStatus.Failed or ElevationStatus.Conflict or ElevationStatus.Partial))
            throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(after);
        OperationId = operationId; Status = status; Result = result; After = after; KeyRevision = keyRevision;
    }
}

/// <summary>Type 0x40. Before key derivation it is plaintext and advisory; afterwards it is an ordinary encrypted frame.</summary>
public sealed record Abort
{
    public ElevationResult Result { get; }
    public Abort(ElevationResult result) { ArgumentNullException.ThrowIfNull(result); Result = result; }
}

/// <summary>Type 0x41. Empty payload.</summary>
public sealed record Bye;

internal static class Guard
{
    public static void ProtocolRange(byte min, byte max)
    {
        if (min < ElevationLimits.ProtocolVersion || max < min) throw new ArgumentException("invalid protocol version range");
    }
    public static void Flavor(ElevationFlavor flavor) { if (!ElevationFlavors.IsDefined((byte)flavor)) throw new ArgumentOutOfRangeException(nameof(flavor)); }
    public static void Operations(byte operations)
    {
        if (operations is < 1 or > ElevationLimits.MaxBatchOperations) throw new ArgumentOutOfRangeException(nameof(operations));
    }
    public static void OperationId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("operation id must not be empty", nameof(id)); }
}
