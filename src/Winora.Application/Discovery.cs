// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

/// <summary>
/// Read-only async discovery port. Implementations must not mutate Windows, must honor cancellation, must return partial
/// results with DiscoveryIssue entries instead of throwing for per-item failures, and must throw only for cancellation.
/// </summary>
public interface IDiscoverySource<T>
{
    Task<DiscoveryResult<T>> DiscoverAsync(CancellationToken cancellationToken);
}

/// <summary>Aggregates the discovery sources behind one injectable service for hosts.</summary>
public sealed class DiscoveryService(IDiscoverySource<StartupItem> startup, IDiscoverySource<ScheduledTaskInfo> tasks,
    IDiscoverySource<ServiceInfo> services, IDiscoverySource<InstalledApplication> applications, IDiscoverySource<PowerPlanInfo>? power = null, IDiscoverySource<Winora.Domain.Elevation.MachineSettingDefinition>? machine = null, IDiscoverySource<QuarantineEntry>? quarantine = null)
{
    public Task<DiscoveryResult<StartupItem>> StartupAsync(CancellationToken ct) => Run(startup, ct);
    public Task<DiscoveryResult<ScheduledTaskInfo>> TasksAsync(CancellationToken ct) => Run(tasks, ct);
    public Task<DiscoveryResult<ServiceInfo>> ServicesAsync(CancellationToken ct) => Run(services, ct);
    public Task<DiscoveryResult<InstalledApplication>> ApplicationsAsync(CancellationToken ct) => Run(applications, ct);
    /// <summary>Hosts that register no quarantine source get one DiscoveryUnavailable issue.</summary>
    public Task<DiscoveryResult<QuarantineEntry>> QuarantineAsync(CancellationToken ct) => quarantine is null
        ? Task.FromResult(new DiscoveryResult<QuarantineEntry>([], [new DiscoveryIssue(nameof(QuarantineEntry), WinoraError.From(ErrorCode.DiscoveryUnavailable))], DateTimeOffset.UtcNow))
        : Run(quarantine, ct);
    /// <summary>Hosts that register no machine definition source get one DiscoveryUnavailable issue.</summary>
    public Task<DiscoveryResult<Winora.Domain.Elevation.MachineSettingDefinition>> MachineDefinitionsAsync(CancellationToken ct) => machine is null
        ? Task.FromResult(new DiscoveryResult<Winora.Domain.Elevation.MachineSettingDefinition>([], [new DiscoveryIssue(nameof(Winora.Domain.Elevation.MachineSettingDefinition), WinoraError.From(ErrorCode.DiscoveryUnavailable))], DateTimeOffset.UtcNow))
        : Run(machine, ct);
    /// <summary>Hosts that register no power source get one DiscoveryUnavailable issue, like any other source that cannot run.</summary>
    public Task<DiscoveryResult<PowerPlanInfo>> PowerAsync(CancellationToken ct) => power is null
        ? Task.FromResult(new DiscoveryResult<PowerPlanInfo>([], [new DiscoveryIssue(nameof(PowerPlanInfo), WinoraError.From(ErrorCode.DiscoveryUnavailable))], DateTimeOffset.UtcNow))
        : Run(power, ct);

    // A source that throws (other than cancellation) becomes one classified issue, so one broken source never blanks a page.
    private static async Task<DiscoveryResult<T>> Run<T>(IDiscoverySource<T> source, CancellationToken ct)
    {
        try { return await source.DiscoverAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            var error = ErrorMapper.Classify(e) ?? WinoraError.From(ErrorCode.Unexpected);
            return new([], [new DiscoveryIssue(typeof(T).Name, error)], DateTimeOffset.UtcNow);
        }
    }
}
