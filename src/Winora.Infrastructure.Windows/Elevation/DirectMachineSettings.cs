// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;
using Winora.Elevation.Protocol.Definitions;
using Winora.Elevation.Windows.Worker;

namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>
/// Machine settings written by the elevated Desktop app itself (decision DEC-IW-023). It uses the same reviewed handlers as the worker: the target, the choices and the restore domain come
/// from the definition; a write is a compare-and-set against what was observed, is flushed to disk and is read back. Nothing here takes a path, a value or a name from the caller.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DirectMachineSettings(IMachineRegistry registry, IMachineServices services, IElevationState elevation) : IMachineSettingsBackend
{
    public bool CanWrite => elevation.IsElevated;

    private IWorkerHandler? Handler(MachineSettingDefinition d, DisplayAdapter? adapter = null) => d.TargetKind switch
    {
        MachineTargetKind.RegistryValue => new RegistryValueHandler(d, registry),
        MachineTargetKind.ServiceStartType => new ServiceStartHandler(d, services),
        MachineTargetKind.DisplayDriverValue => DisplayHandler(d, adapter),
        _ => null,
    };

    /// <summary>
    /// A display driver value is handled as a registry value on the driver key that was resolved for the adapter. Only the vendor of the definition, only a key of the exact shape
    /// below the display class key and only the value name of the definition can reach the registry layer; the layer itself then checks the final key name.
    /// </summary>
    private RegistryValueHandler? DisplayHandler(MachineSettingDefinition d, DisplayAdapter? adapter)
    {
        if (d.DisplayDriver is not { } target || adapter is null) return null;
        if (!string.Equals(adapter.VendorId, target.VendorId, StringComparison.OrdinalIgnoreCase) || !DisplayDriverKeys.IsResolvedKeyPath(adapter.KeyPath)) return null;
        var bound = d with { TargetKind = MachineTargetKind.RegistryValue, Registry = new MachineRegistryTarget(adapter.KeyPath, target.ValueName, RegistryViewKind.Registry64), DisplayDriver = null };
        return new RegistryValueHandler(bound, registry);
    }

    private static BatchItem Item(MachineSettingDefinition d, PrepareMode mode, string choiceId, MachineValue restore) =>
        new(Guid.NewGuid(), d.Id, (uint)d.Version, mode, choiceId, restore, default);

    public MachineObserveResult Observe(MachineSettingDefinition definition, DisplayAdapter? adapter = null)
    {
        if (Handler(definition, adapter) is not { } handler) return new(null, OptimizeCode.Unreadable);
        try
        {
            var observed = handler.Observe(Item(definition, PrepareMode.ApplyChoice, definition.DefaultChoiceId, MachineValue.Absent));
            return new(new MachineObservation(observed.Snapshot, observed.KeyRevision), null);
        }
        catch (Exception e) when (e is MachineRegistryException or MachineServiceException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            return new(null, e is UnauthorizedAccessException ? OptimizeCode.AccessDenied : OptimizeCode.Unreadable);
        }
    }

    public MachineWriteResult Apply(MachineSettingDefinition definition, string choiceId, MachineObservation observed, DisplayAdapter? adapter = null) =>
        Write(definition, Item(definition, PrepareMode.ApplyChoice, choiceId, MachineValue.Absent), observed, adapter);

    public MachineWriteResult Restore(MachineSettingDefinition definition, MachineValue value, MachineObservation observed, DisplayAdapter? adapter = null) =>
        Write(definition, Item(definition, PrepareMode.Restore, "", value), observed, adapter);

    private MachineWriteResult Write(MachineSettingDefinition d, BatchItem item, MachineObservation observed, DisplayAdapter? adapter)
    {
        if (!CanWrite) return new(false, OptimizeCode.NeedsAdministrator, null, 0);
        if (Handler(d, adapter) is not { } handler) return new(false, OptimizeCode.Failed, null, 0);
        // The driver key must exist now: a write never creates a numbered driver key.
        if (d.TargetKind == MachineTargetKind.DisplayDriverValue && !(observed.Snapshot.TryReadRegistry(out var keyExists, out _, out _) && keyExists)) return new(false, OptimizeCode.Failed, null, 0);
        try
        {
            var plan = handler.Decide(item, new(observed.Snapshot, observed.KeyRevision));
            if (plan.Status is ElevationStatus.Refused or ElevationStatus.Failed) return new(false, MapRefusal(d, plan.Result.Code), null, 0);
            if (plan.Status == ElevationStatus.AlreadyConfigured) return new(false, OptimizeCode.AlreadySet, null, 0);
            var applied = handler.Apply(item, new(observed.Snapshot, observed.KeyRevision));
            return applied.Status switch
            {
                ElevationStatus.Applied => new(true, OptimizeCode.Ok, applied.After, applied.KeyRevision),
                ElevationStatus.Conflict => new(false, OptimizeCode.Conflict, applied.After, applied.KeyRevision),
                _ => new(false, MapRefusal(d, applied.Result.Code), applied.After, applied.KeyRevision),
            };
        }
        catch (Exception e) when (e is MachineRegistryException or MachineServiceException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            return new(false, e is UnauthorizedAccessException ? OptimizeCode.AccessDenied : OptimizeCode.Failed, null, 0);
        }
    }

    /// <summary>A service refused by the service rules gets its own plain-language code: another service needs it, or its program is not a Microsoft file in the Windows folder.</summary>
    private string MapRefusal(MachineSettingDefinition d, string code)
    {
        if (code != ErrorCode.TargetNotAllowed || d.Service is not { } service) return Map(code);
        try { return services.Inspect(service.ServiceName).ImageTrusted ? OptimizeCode.ServiceInUse : OptimizeCode.ServiceNotTrusted; }
        catch (MachineServiceException) { return OptimizeCode.AccessDenied; }
    }

    private static string Map(string code) => code switch
    {
        ErrorCode.AccessDenied => OptimizeCode.AccessDenied,
        ErrorCode.ExternalConflict => OptimizeCode.Conflict,
        ErrorCode.SettingStateUnrecognized => OptimizeCode.StateUnrecognized,
        ErrorCode.NotReversible => OptimizeCode.NotReversible,
        ErrorCode.VerificationFailed => OptimizeCode.VerifyFailed,
        _ => OptimizeCode.Failed,
    };
}
