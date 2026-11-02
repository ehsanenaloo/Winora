// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;

namespace Winora.Application;

/// <summary>Reads the values of the performance check that come from Windows. Read-only: no write, no child process.</summary>
public interface IPerformanceProbe
{
    /// <summary>Reads boot time, memory, system drive space and processor load. <paramref name="stage"/> receives the id of the value being read now.</summary>
    Task<PerformanceReadings> ReadAsync(IProgress<string>? stage, CancellationToken token);
}

/// <summary>Runs one measurement and applies the fixed rules. The number of enabled startup items comes from the page that knows it.</summary>
public sealed class PerformanceService(IPerformanceProbe probe, TimeProvider? clock = null)
{
    public async Task<PerformanceReport> MeasureAsync(int? startupEnabled, IProgress<string>? stage, CancellationToken token)
    {
        var read = await probe.ReadAsync(stage, token);
        return PerformanceScoring.Evaluate(read with { StartupEnabled = startupEnabled }, (clock ?? TimeProvider.System).GetUtcNow());
    }
}
