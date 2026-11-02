// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>Collects the system snapshot. The sections are read in parallel, off the caller's thread; a section that fails or is unavailable is Unknown and never hides the others. Cancelling stops the wait at once.</summary>
public sealed class DashboardService(ISystemInfoSource source, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>Only the drives, read on the caller's thread (a few device queries). Null when they cannot be read.</summary>
    public IReadOnlyList<DriveEntry>? ReadDrives()
    {
        try { return source.ReadDrives(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    public async Task<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var os = Run(source.ReadOs, cancellationToken);
        var cpu = Run(source.ReadCpu, cancellationToken);
        var memory = Run(source.ReadMemory, cancellationToken);
        var gpus = Run(source.ReadGpus, cancellationToken);
        var drives = Run(source.ReadDrives, cancellationToken);
        var battery = Run(source.ReadBattery, cancellationToken);
        var device = Run(source.ReadDevice, cancellationToken);
        var firmware = Run(source.ReadFirmware, cancellationToken);
        var tpm = Run(source.ReadTpm, cancellationToken);
        var plan = Run(source.ReadActivePowerPlan, cancellationToken);
        var isolation = Run(source.ReadIsolation, cancellationToken);
        await Task.WhenAll(os, cpu, memory, gpus, drives, battery, device, firmware, tpm, plan, isolation).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(os.Result, cpu.Result, memory.Result, gpus.Result, drives.Result, battery.Result, device.Result, firmware.Result, tpm.Result, plan.Result?.Name, clock.GetUtcNow(), isolation.Result,
            plan.Result is { Id: var id } && id != Guid.Empty ? id : null);
    }

    private static Task<T?> Run<T>(Func<T?> read, CancellationToken ct) where T : class => Task.Run(() =>
    {
        if (ct.IsCancellationRequested) return null;
        try { return read(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }, CancellationToken.None);
}
