// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>What a handler read from its target: the canonical snapshot and the key revision (0 when the target has none).</summary>
public sealed record HandlerObservation(MachineSnapshot Snapshot, ulong KeyRevision);

/// <summary>A handler's decision after Prepare: Ready (willChange true or false), AlreadyConfigured, Failed or Refused, with its Result.</summary>
public sealed record HandlerPlan(ElevationStatus Status, ElevationResult Result, bool WillChange);

/// <summary>A handler's result after Commit: the Outcome status, Result, after-snapshot and key revision.</summary>
public sealed record HandlerApplied(ElevationStatus Status, ElevationResult Result, MachineSnapshot After, ulong KeyRevision);

/// <summary>
/// One typed capability of the worker (technical-docs/protocol.md 11): it owns exactly one definition id and version. A handler never receives a
/// target, path or command from the wire; it resolves everything from its own compiled definition. Handlers are synchronous and pure with
/// respect to the protocol: the session performs every comparison, digest and state transition.
/// </summary>
public interface IWorkerHandler
{
    string DefinitionId { get; }
    uint Version { get; }
    /// <summary>Restore mode (mode 1) is available only where the definition supports it; M4a has no restore.</summary>
    bool SupportsRestore { get; }
    bool AcceptsChoice(string choiceId);
    /// <summary>A catalog code when the worker knows this definition but cannot execute its kind; the session then ends the batch before any Prepare. Null for a handler that executes.</summary>
    string? UnsupportedCode => null;
    HandlerObservation Observe(BatchItem item);
    HandlerPlan Decide(BatchItem item, HandlerObservation observed);
    HandlerApplied Apply(BatchItem item, HandlerObservation prepared);
}

/// <summary>The set of handlers a worker registers. The production set is empty, so every BatchBegin is answered with worker.unimplemented.</summary>
public sealed class WorkerHandlerSet
{
    private readonly IWorkerHandler[] handlers;
    public WorkerHandlerSet(IEnumerable<IWorkerHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = handlers.ToArray();
        if (this.handlers.Any(h => h is null || !ElevationIdentifier.IsValid(h.DefinitionId))) throw new ArgumentException("invalid handler", nameof(handlers));
        if (this.handlers.Select(h => h.DefinitionId).Distinct().Count() != this.handlers.Length) throw new ArgumentException("duplicate definition id", nameof(handlers));
    }
    public static WorkerHandlerSet Empty { get; } = new([]);
    public int Count => handlers.Length;
    public bool IsEmpty => handlers.Length == 0;
    public IReadOnlyList<IWorkerHandler> Handlers => handlers;
    public IWorkerHandler? Find(string definitionId) => handlers.FirstOrDefault(h => h.DefinitionId == definitionId);
}

/// <summary>The M4a definition-set bytes and their hash H_d (technical-docs/protocol.md A.9). There is no signature layer in M4a.</summary>
public static class M4aDefinitionSet
{
    public static ReadOnlySpan<byte> Bytes(ElevationFlavor flavor) => flavor == ElevationFlavor.VmTrust ? SelftestNoop.VmTrustSetBytes : SelftestNoop.EmptySetBytes;
    public static Bytes32 Hash(ElevationFlavor flavor) => new(SHA256.HashData(Bytes(flavor)));
}

/// <summary>
/// The harmless typed no-op of milestone M4a (technical-docs/protocol.md 19.1): definition <c>winora.selftest.noop</c> version 1 with the choices
/// <c>ping</c> and <c>echo.&lt;p&gt;</c>. It touches no registry, service or file; the only state is a one-byte flag in this object.
/// It exists in the VmTrust flavor only: a release worker registers no handler.
/// </summary>
public sealed class SelftestNoopHandler : IWorkerHandler
{
    private bool pinged;
    public string DefinitionId => SelftestNoop.DefinitionId;
    public uint Version => SelftestNoop.DefinitionVersion;
    public bool SupportsRestore => false;
    public bool AcceptsChoice(string choiceId) => SelftestChoice.TryParse(choiceId, out _);

    public HandlerObservation Observe(BatchItem item) =>
        SelftestChoice.TryParse(item.ChoiceId, out var choice)
            ? new(choice.Kind == SelftestChoiceKind.Ping ? MachineSnapshot.Selftest(pinged ? SelftestNoop.PongPayload : "") : MachineSnapshot.Selftest(choice.ChoiceId), 0)
            : new(MachineSnapshot.SelftestEmpty, 0);

    public HandlerPlan Decide(BatchItem item, HandlerObservation observed) =>
        SelftestChoice.TryParse(item.ChoiceId, out var choice) && choice.Kind == SelftestChoiceKind.Ping
            ? new(ElevationStatus.Ready, ElevationResult.From(OutcomeCodes.Prepared), true)
            : new(ElevationStatus.AlreadyConfigured, ElevationResult.From(OutcomeCodes.AlreadyConfigured), false);

    public HandlerApplied Apply(BatchItem item, HandlerObservation prepared)
    {
        pinged = true;
        return new(ElevationStatus.Applied, ElevationResult.From(OutcomeCodes.Applied), MachineSnapshot.Selftest(SelftestNoop.PongPayload), 0);
    }
}
