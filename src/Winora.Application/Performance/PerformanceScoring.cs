// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;

namespace Winora.Application;

/// <summary>The raw values of one measurement. A value that could not be read is null.</summary>
public sealed record PerformanceReadings(double? BootSeconds, int? StartupEnabled, double? MemoryUsedPercent, double? SystemDriveFreePercent, double? CpuPercent);

/// <summary>
/// The fixed rules that turn readings into statuses and one score. Each value is Good, Fair or Poor against a documented limit; Good counts 100, Fair 60 and Poor 20,
/// and the score is the rounded mean of the values that could be read. With fewer than <see cref="MinimumMetrics"/> values there is no score. The limits are indicators of how this PC
/// is set up and loaded at the moment of the check. They are not a benchmark and are never compared with other PCs.
/// </summary>
public static class PerformanceScoring
{
    public const int MinimumMetrics = 3;
    public const int GoodPoints = 100, FairPoints = 60, PoorPoints = 20;

    /// <summary>Boot time in seconds: up to 30 is Good, up to 60 is Fair.</summary>
    public static MetricStatus Boot(double? seconds) => seconds is null ? MetricStatus.Unavailable : seconds <= 30 ? MetricStatus.Good : seconds <= 60 ? MetricStatus.Fair : MetricStatus.Poor;
    /// <summary>Enabled startup items: up to 8 is Good, up to 15 is Fair.</summary>
    public static MetricStatus Startup(int? enabled) => enabled is null ? MetricStatus.Unavailable : enabled <= 8 ? MetricStatus.Good : enabled <= 15 ? MetricStatus.Fair : MetricStatus.Poor;
    /// <summary>Memory in use, percent: up to 60 is Good, up to 80 is Fair.</summary>
    public static MetricStatus Memory(double? usedPercent) => usedPercent is null ? MetricStatus.Unavailable : usedPercent <= 60 ? MetricStatus.Good : usedPercent <= 80 ? MetricStatus.Fair : MetricStatus.Poor;
    /// <summary>Free space on the system drive, percent: 20 or more is Good, 10 or more is Fair.</summary>
    public static MetricStatus Disk(double? freePercent) => freePercent is null ? MetricStatus.Unavailable : freePercent >= 20 ? MetricStatus.Good : freePercent >= 10 ? MetricStatus.Fair : MetricStatus.Poor;
    /// <summary>Processor load, percent, averaged over a few seconds: up to 10 is Good, up to 30 is Fair.</summary>
    public static MetricStatus Cpu(double? percent) => percent is null ? MetricStatus.Unavailable : percent <= 10 ? MetricStatus.Good : percent <= 30 ? MetricStatus.Fair : MetricStatus.Poor;

    public static int Points(MetricStatus status) => status switch { MetricStatus.Good => GoodPoints, MetricStatus.Fair => FairPoints, MetricStatus.Poor => PoorPoints, _ => 0 };

    public static PerformanceReport Evaluate(PerformanceReadings r, DateTimeOffset at)
    {
        var metrics = new List<PerformanceMetric>
        {
            new(MetricIds.Boot, r.BootSeconds, Boot(r.BootSeconds)),
            new(MetricIds.Startup, r.StartupEnabled, Startup(r.StartupEnabled)),
            new(MetricIds.Memory, r.MemoryUsedPercent, Memory(r.MemoryUsedPercent)),
            new(MetricIds.Disk, r.SystemDriveFreePercent, Disk(r.SystemDriveFreePercent)),
            new(MetricIds.Cpu, r.CpuPercent, Cpu(r.CpuPercent)),
        };
        var read = metrics.Where(m => m.Status != MetricStatus.Unavailable).ToList();
        int? score = read.Count >= MinimumMetrics ? (int)Math.Round(read.Average(m => Points(m.Status)), MidpointRounding.AwayFromZero) : null;
        return new PerformanceReport(at, metrics, score);
    }
}
