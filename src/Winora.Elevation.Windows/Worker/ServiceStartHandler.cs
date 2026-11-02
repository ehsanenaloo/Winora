// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// The worker's capability for ONE service start-type definition (trust.md 7.1 item 3, 11). The service, its choices and the admissible start types come from the verified
/// definition, never from the wire. Apply is a compare-and-set against the state the session prepared. The start type is read back after the change. A service has no
/// key revision, so the revision is always 0. Restore mode writes only automatic, manual or disabled.
/// </summary>
public sealed class ServiceStartHandler : IWorkerHandler
{
    private readonly MachineSettingDefinition definition;
    private readonly string serviceName;
    private readonly IMachineServices services;

    public ServiceStartHandler(MachineSettingDefinition definition, IMachineServices services)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(services);
        if (definition.TargetKind != MachineTargetKind.ServiceStartType || definition.Service is null) throw new ArgumentException("not a service definition", nameof(definition));
        this.definition = definition;
        serviceName = definition.Service.ServiceName;
        this.services = services;
    }

    public string DefinitionId => definition.Id;
    public uint Version => (uint)definition.Version;
    public bool SupportsRestore => true;
    public bool AcceptsChoice(string choiceId) => definition.Choices.Any(c => c.Id == choiceId && c.Start is not null);

    public HandlerObservation Observe(BatchItem item) => new(ReadOrOutOfDomain(), 0);

    public HandlerPlan Decide(BatchItem item, HandlerObservation observed)
    {
        if (!observed.Snapshot.TryReadValue(out var current) || !definition.EffectiveRestoreDomain.Contains(current))
            return new(ElevationStatus.Refused, ElevationResult.From(ErrorCode.SettingStateUnrecognized), false);
        var desired = Desired(item);
        if (desired is null) return new(ElevationStatus.Refused, ElevationResult.From(ErrorCode.NotReversible), false);
        if (current.Equals(desired)) return new(ElevationStatus.AlreadyConfigured, ElevationResult.From(OutcomeCodes.AlreadyConfigured), false);
        var refusal = RefusalCode(desired);
        return refusal is not null ? new(ElevationStatus.Refused, ElevationResult.From(refusal), false) : new(ElevationStatus.Ready, ElevationResult.From(OutcomeCodes.Prepared), true);
    }

    /// <summary>The service rules (DEC-IW-033): null when the change is allowed, otherwise the catalog code that refuses it. Read-only.</summary>
    private string? RefusalCode(MachineValue desired)
    {
        try
        {
            return ServicePolicy.Check(services, serviceName, (byte)desired.Number!.Value) switch
            {
                ServiceRefusal.None => null,
                ServiceRefusal.MissingService => ErrorCode.SettingStateUnrecognized,
                _ => ErrorCode.TargetNotAllowed,
            };
        }
        catch (MachineServiceException e) { return e.Code; }
    }

    public HandlerApplied Apply(BatchItem item, HandlerObservation prepared)
    {
        var desired = Desired(item) ?? throw new InvalidOperationException("the session only applies a decided item");
        MachineSnapshot now;
        try { now = services.Read(serviceName); }
        catch (MachineServiceException e) { return Failed(e.Code, prepared.Snapshot); }
        if (!now.Equals(prepared.Snapshot))
            return new(ElevationStatus.Conflict, ElevationResult.From(ErrorCode.ExternalConflict), now, 0);
        if (RefusalCode(desired) is { } refusal) return new(ElevationStatus.Failed, ElevationResult.From(refusal), now, 0);
        try { services.SetStart(serviceName, (byte)desired.Number!.Value); }
        catch (MachineServiceException e) { return Failed(e.Code, ReadOr(prepared.Snapshot)); }
        MachineSnapshot after;
        try { after = services.Read(serviceName); }
        catch (MachineServiceException e) { return Failed(e.Code, prepared.Snapshot); }
        return after.TryReadValue(out var written) && written.Equals(desired)
            ? new(ElevationStatus.Applied, ElevationResult.From(OutcomeCodes.Applied), after, 0)
            : new(ElevationStatus.Partial, ElevationResult.From(ErrorCode.VerificationFailed), after, 0);
    }

    /// <summary>The wire start kind this item writes as a DWORD: the choice, or the restore value when it lies in the three admissible start types.</summary>
    private MachineValue? Desired(BatchItem item)
    {
        if (item.Mode == PrepareMode.ApplyChoice) return definition.Choices.FirstOrDefault(c => c.Id == item.ChoiceId)?.EffectiveValue;
        return definition.EffectiveRestoreDomain.Contains(item.RestoreValue) ? item.RestoreValue : null;
    }

    /// <summary>A driver or an unreadable configuration is shown as a start kind outside the domain so that Decide refuses it.</summary>
    private MachineSnapshot ReadOrOutOfDomain()
    {
        try { return services.Read(serviceName); }
        catch (MachineServiceException e) when (e.Code == ErrorCode.SettingStateUnrecognized) { return MachineSnapshot.Service(true, 1); }
    }

    private MachineSnapshot ReadOr(MachineSnapshot fallback)
    {
        try { return services.Read(serviceName); }
        catch (MachineServiceException) { return fallback; }
    }

    private static HandlerApplied Failed(string code, MachineSnapshot snapshot) =>
        new(ElevationStatus.Failed, ElevationResult.From(code == ErrorCode.AccessDenied ? ErrorCode.AccessDenied : ErrorCode.Unexpected), snapshot, 0);
}
