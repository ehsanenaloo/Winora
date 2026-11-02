// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;
namespace Winora.Application;

/// <summary>
/// Read-only list of the administrator-level changes that the verified definition set of this build offers. It reads the set through the same source the executor uses; it does not
/// start the worker, touch the target values or change anything. A set that cannot be used (signature, rollback floor, floor store) becomes one issue with the catalog code.
/// The release set is empty, so on a release build the list is empty and complete.
/// </summary>
public sealed class MachineDefinitionDiscovery(IMachineDefinitionSource source) : IDiscoverySource<MachineSettingDefinition>
{
    public Task<DiscoveryResult<MachineSettingDefinition>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                var result = source.Get();
                if (result.Set is { } set) return new DiscoveryResult<MachineSettingDefinition>(set.Definitions, [], now);
                return new DiscoveryResult<MachineSettingDefinition>([], [new(nameof(MachineSettingDefinition), WinoraError.From(result.Code ?? ErrorCode.DefinitionSignatureInvalid))], now);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var error = ErrorMapper.Classify(e) ?? WinoraError.From(ErrorCode.Unexpected);
                return new DiscoveryResult<MachineSettingDefinition>([], [new(nameof(MachineSettingDefinition), error)], now);
            }
        }, cancellationToken);
    }
}
