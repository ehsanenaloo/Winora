// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Domain;

/// <summary>How one measured value compares with the fixed limits of the performance check. Unavailable means the value could not be read on this PC.</summary>
public enum MetricStatus { Good, Fair, Poor, Unavailable }

/// <summary>Stable identifiers of the five measured values.</summary>
public static class MetricIds
{
    public const string Boot = "boot", Startup = "startup", Memory = "memory", Disk = "disk", Cpu = "cpu";
    public static readonly IReadOnlyList<string> All = [Boot, Startup, Memory, Disk, Cpu];
}

/// <summary>One measured value. The unit follows the id: seconds, a count, or a percent.</summary>
public sealed record PerformanceMetric(string Id, double? Value, MetricStatus Status);

/// <summary>The result of one measurement. Score is null when fewer than three values could be read.</summary>
public sealed record PerformanceReport(DateTimeOffset At, IReadOnlyList<PerformanceMetric> Metrics, int? Score);
