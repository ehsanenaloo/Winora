// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;
using Winora.Elevation.Protocol.Definitions;

namespace Winora.Elevation.Windows.Worker;

/// <summary>Where a worker (or a client) finds the signed definition set: the raw bytes, the detached signature and the pinned public keys (trust.md 6).</summary>
public interface IDefinitionMaterial
{
    IReadOnlyList<PinnedKey> Keys { get; }
    /// <summary>The set and its signature, or null when none exists (an empty release set).</summary>
    (byte[] Set, byte[] Signature)? Load();
}

/// <summary>
/// The release material: the set and its signature are resources embedded in the assembly (<c>MachineDefinitions.v1.json</c> and <c>MachineDefinitions.v1.cose</c>), the keys are
/// compiled in. No set is embedded and no key is pinned yet (trust.md 7.5, DEC-IW-016), so <see cref="Load"/> returns null and the worker registers no handler.
/// </summary>
public sealed class EmbeddedDefinitionMaterial(Assembly assembly, IReadOnlyList<PinnedKey>? keys = null) : IDefinitionMaterial
{
    public const string SetResource = "MachineDefinitions.v1.json";
    public const string SignatureResource = "MachineDefinitions.v1.cose";

    public IReadOnlyList<PinnedKey> Keys { get; } = keys ?? [];

    public (byte[] Set, byte[] Signature)? Load()
    {
        var set = Read(SetResource);
        var signature = Read(SignatureResource);
        return set is not null && signature is not null ? (set, signature) : null;
    }

    private byte[]? Read(string suffix)
    {
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
        if (name is null) return null;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

/// <summary>What the worker runs with after it resolved its definitions (trust.md 6.3). <see cref="Failure"/> is the early-failure reason when the set cannot be used.</summary>
public sealed record WorkerDefinitionState(
    WorkerHandlerSet Handlers, Bytes32? SetHash, Func<bool>? BeforeBatchAccepted, EarlyFailureReason? Failure, bool CommitSupported, int MaxOperations)
{
    /// <summary>The built-in M4a state: the flavor's own handlers and set hash, no signature layer.</summary>
    public static WorkerDefinitionState BuiltIn(WorkerHandlerSet handlers, bool commitSupported) =>
        new(handlers, null, null, null, commitSupported, ElevationLimits.M4aMaxBatchOperations);

    public static WorkerDefinitionState Refused(EarlyFailureReason reason) => new(WorkerHandlerSet.Empty, null, null, reason, false, ElevationLimits.M4aMaxBatchOperations);
}

/// <summary>
/// Resolution of the worker's definitions at startup (trust.md 6.3 and 6.4): verify the signature before anything is parsed, then the schema and invariants, then the floor,
/// compute H_d and register one handler per registry definition. The floor is advanced only after the batch was accepted. Any failure is a closed early-failure reason.
/// </summary>
public static class WorkerDefinitions
{
    public static WorkerDefinitionState Resolve(
        WorkerHandlerSet builtIn, bool builtInCommit, IDefinitionMaterial? material, IAntiRollbackStore floor, IMachineRegistry registry, int osBuild, bool vmFlavor, IMachineServices? services = null)
    {
        var loaded = material?.Load();
        if (loaded is null) return WorkerDefinitionState.BuiltIn(builtIn, builtInCommit);

        var verified = new DefinitionSetVerifier(material!.Keys).Verify(loaded.Value.Set, loaded.Value.Signature);
        if (verified.Set is not { } set) return WorkerDefinitionState.Refused(EarlyFailureReason.Definition);
        if (set.MinProtocol > ElevationLimits.ProtocolVersion || set.MinWorkerBuild > osBuild) return WorkerDefinitionState.Refused(EarlyFailureReason.Definition);

        var code = RollbackPolicy.Evaluate(floor, (ulong)set.Serial, verified.SetHash, out var current);
        if (code is not null)
            return WorkerDefinitionState.Refused(code switch
            {
                ErrorCode.DefinitionRollback => EarlyFailureReason.Rollback,
                ErrorCode.DefinitionSetMismatch => EarlyFailureReason.SetMismatch,
                _ => EarlyFailureReason.FloorStore,
            });

        var handlers = new List<IWorkerHandler>(RegistryHandlers.For(set.Definitions, registry, services).Handlers);
        if (vmFlavor) handlers.AddRange(builtIn.Handlers);        // the VM build keeps its self-test next to the verified definitions
        var hash = verified.SetHash;
        var serial = (ulong)set.Serial;
        return new(new WorkerHandlerSet(handlers), hash, () => RollbackPolicy.Advance(floor, current, serial, hash), null, true, ElevationLimits.MaxBatchOperations);
    }
}
