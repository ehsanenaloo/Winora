// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// The worker's capability for ONE machine registry definition (technical-docs/trust.md 7, 11; protocol.md 11). It resolves its target, its choices and its restore domain
/// from the verified definition it was built from, never from the wire. Apply is a compare-and-set against the state the session prepared: if the key or the value changed
/// since Prepare nothing is written. Every write is flushed to disk and read back before the answer is built. Restore mode writes only a value of the restore domain.
/// </summary>
public sealed class RegistryValueHandler : IWorkerHandler
{
    private readonly MachineSettingDefinition definition;
    private readonly MachineRegistryTarget target;
    private readonly IMachineRegistry registry;

    public RegistryValueHandler(MachineSettingDefinition definition, IMachineRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);
        if (definition.TargetKind != MachineTargetKind.RegistryValue || definition.Registry is null) throw new ArgumentException("not a registry definition", nameof(definition));
        this.definition = definition;
        target = definition.Registry;
        this.registry = registry;
    }

    public string DefinitionId => definition.Id;
    public uint Version => (uint)definition.Version;
    public bool SupportsRestore => true;
    public bool AcceptsChoice(string choiceId) => definition.Choices.Any(c => c.Id == choiceId && c.Value is not null);

    public HandlerObservation Observe(BatchItem item)
    {
        var state = registry.Read(target.SubKey, target.ValueName, target.View);
        return new(state.Snapshot, state.KeyRevision);
    }

    public HandlerPlan Decide(BatchItem item, HandlerObservation observed)
    {
        if (!definition.TryReadComparable(observed.Snapshot, out var current) || !definition.RestoreDomain.Contains(current))
            return new(ElevationStatus.Refused, ElevationResult.From(ErrorCode.SettingStateUnrecognized), false);
        var desired = Desired(item);
        if (desired is null) return new(ElevationStatus.Refused, ElevationResult.From(ErrorCode.NotReversible), false);
        return current.Equals(desired)
            ? new(ElevationStatus.AlreadyConfigured, ElevationResult.From(OutcomeCodes.AlreadyConfigured), false)
            : new(ElevationStatus.Ready, ElevationResult.From(OutcomeCodes.Prepared), true);
    }

    public HandlerApplied Apply(BatchItem item, HandlerObservation prepared)
    {
        var desired = Desired(item) ?? throw new InvalidOperationException("the session only applies a decided item");
        MachineRegistryState now;
        try { now = registry.Read(target.SubKey, target.ValueName, target.View); }
        catch (MachineRegistryException e) { return Failed(e.Code, prepared); }
        // Compare-and-set: a change since Prepare (value or key revision) means someone else moved the target; nothing is written.
        if (!now.Snapshot.Equals(prepared.Snapshot) || now.KeyRevision != prepared.KeyRevision)
            return new(ElevationStatus.Conflict, ElevationResult.From(ErrorCode.ExternalConflict), now.Snapshot, now.KeyRevision);
        var toWrite = desired;
        if (definition.IsBitField)
        {
            // Read-modify-write: only the owned bits change, every other bit comes from the value just compared.
            if (!now.Snapshot.TryReadRegistry(out _, out _, out var raw) || definition.WithBits(raw, (uint)desired.Number!.Value) is not { } merged)
                return new(ElevationStatus.Failed, ElevationResult.From(ErrorCode.SettingStateUnrecognized), now.Snapshot, now.KeyRevision);
            toWrite = merged;
        }
        try { registry.Write(target.SubKey, target.ValueName, target.View, toWrite); }
        catch (MachineRegistryException e)
        {
            // A failed write may still have changed the value; report what is there now.
            return Failed(e.Code, ReadOr(prepared));
        }
        MachineRegistryState after;
        try { after = registry.Read(target.SubKey, target.ValueName, target.View); }
        catch (MachineRegistryException e) { return Failed(e.Code, prepared); }
        return definition.TryReadComparable(after.Snapshot, out var written) && written.Equals(desired) && OtherBitsKept(now.Snapshot, after.Snapshot)
            ? new(ElevationStatus.Applied, ElevationResult.From(OutcomeCodes.Applied), after.Snapshot, after.KeyRevision)
            : new(ElevationStatus.Partial, ElevationResult.From(ErrorCode.VerificationFailed), after.Snapshot, after.KeyRevision);
    }

    /// <summary>The value this item writes: the choice value (apply) or the restore value, which must lie in the restore domain (restore).</summary>
    private MachineValue? Desired(BatchItem item)
    {
        if (item.Mode == PrepareMode.ApplyChoice) return definition.Choices.FirstOrDefault(c => c.Id == item.ChoiceId)?.Value;
        return definition.RestoreDomain.Contains(item.RestoreValue) ? item.RestoreValue : null;
    }

    /// <summary>For a bit field: every bit outside the mask is the same before and after the write. Always true for a plain value.</summary>
    private bool OtherBitsKept(MachineSnapshot before, MachineSnapshot after)
    {
        if (target.Bits is not { } bits) return true;
        if (!before.TryReadRegistry(out _, out _, out var b) || !after.TryReadRegistry(out _, out _, out var a)) return false;
        return MachineSettingDefinition.TryReadBits(b, bits, out var nb) && MachineSettingDefinition.TryReadBits(a, bits, out var na) && (nb & ~bits.Mask) == (na & ~bits.Mask);
    }

    private HandlerObservation ReadOr(HandlerObservation fallback)
    {
        try
        {
            var state = registry.Read(target.SubKey, target.ValueName, target.View);
            return new(state.Snapshot, state.KeyRevision);
        }
        catch (MachineRegistryException) { return fallback; }
    }

    private static HandlerApplied Failed(string code, HandlerObservation observation) =>
        new(ElevationStatus.Failed, ElevationResult.From(code == ErrorCode.AccessDenied ? ErrorCode.AccessDenied : ErrorCode.Unexpected), observation.Snapshot, observation.KeyRevision);
}

/// <summary>
/// Builds the worker's handler set from a verified definition set: one <see cref="RegistryValueHandler"/> per registry definition, one <see cref="ServiceStartHandler"/> per service
/// definition and one <see cref="UnsupportedKindHandler"/> per display driver definition, which the worker refuses.
/// </summary>
public static class RegistryHandlers
{
    public static WorkerHandlerSet For(IEnumerable<MachineSettingDefinition> definitions, IMachineRegistry registry, IMachineServices? services = null)
    {
        var list = definitions.ToList();
        var handlers = list.Where(d => d.TargetKind == MachineTargetKind.RegistryValue).Select(d => (IWorkerHandler)new RegistryValueHandler(d, registry)).ToList();
        if (services is not null) handlers.AddRange(list.Where(d => d.TargetKind == MachineTargetKind.ServiceStartType).Select(d => new ServiceStartHandler(d, services)));
        handlers.AddRange(list.Where(d => d.TargetKind == MachineTargetKind.DisplayDriverValue).Select(d => new UnsupportedKindHandler(d)));
        return new(handlers);
    }
}
