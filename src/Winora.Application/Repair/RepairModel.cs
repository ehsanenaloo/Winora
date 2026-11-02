// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;

namespace Winora.Application;

/// <summary>
/// The closed list of programs Winora may start for repairs and optional features (decision DEC-IW-041). Nothing outside this list can be started: the runner builds the command from the
/// enum value alone.
/// </summary>
public enum RepairTool
{
    SystemFileCheck,
    DiskCheck,
    WinsockReset,
    IpReset,
    Ipv6Reset,
    WinHttpProxyReset,
    StoreCacheReset,
    ComponentCheckHealth,
    ComponentScanHealth,
    ComponentRestoreHealth,
    FeatureList,
    FeatureEnable,
    FeatureDisable,
}

/// <summary>
/// One run. <see cref="Argument"/> is used by exactly three tools and is never free text: a fixed drive letter for the disk check, and a feature name from the list that was just read for the two
/// feature actions. Every other tool takes no argument.
/// </summary>
public sealed record RepairRequest(RepairTool Tool, string? Argument = null);

/// <summary>How a run ended. <see cref="StillRunning"/>: the wait reached its limit while the program still ran; Winora stopped waiting and did not end it, so the result is not known.
/// <see cref="TimedOut"/> is only found in older journal lines.</summary>
public enum RepairOutcome { Succeeded, FoundProblems, Failed, TimedOut, Cancelled, NotStarted, StillRunning }

/// <summary>What the runner saw. <see cref="Output"/> is the captured text up to the size cap, <see cref="Summary"/> the last meaningful lines of it.</summary>
public sealed record RepairRun(RepairOutcome Outcome, int? ExitCode, string Code, string Summary, string Output, bool OutputTruncated, bool RestartNeeded);

/// <summary>Progress of a running tool. <see cref="Percent"/> is null when the tool prints none.</summary>
public sealed record RepairProgress(int? Percent, string Line);

/// <summary>Starts the tools of <see cref="RepairTool"/>. The only implementation lives in one reviewed Windows file.</summary>
public interface IRepairRunner
{
    /// <summary>The exact command that would run, as plain text. Null when the request is not valid.</summary>
    string? Preview(RepairRequest request);
    /// <summary>The fixed drives, as "C:". The only values the disk check accepts.</summary>
    IReadOnlyList<string> FixedVolumes();
    Task<RepairRun> RunAsync(RepairRequest request, IProgress<RepairProgress>? progress, CancellationToken cancellationToken);
}

public enum RepairRisk { Low, Medium }

/// <summary>The facts the page shows for each repair tool. The command itself is built by the runner.</summary>
public sealed record RepairToolInfo(RepairTool Tool, string Id, RepairRisk Risk, bool RestartLikely, bool ChangesSystem, int MinutesLow, int MinutesHigh);

public static class RepairTools
{
    public static IReadOnlyList<RepairToolInfo> All { get; } =
    [
        new(RepairTool.SystemFileCheck, "sfc", RepairRisk.Low, false, true, 5, 30),
        new(RepairTool.DiskCheck, "chkdsk", RepairRisk.Low, false, false, 2, 60),
        new(RepairTool.ComponentCheckHealth, "dism_check", RepairRisk.Low, false, false, 1, 2),
        new(RepairTool.ComponentScanHealth, "dism_scan", RepairRisk.Low, false, false, 5, 30),
        new(RepairTool.ComponentRestoreHealth, "dism_restore", RepairRisk.Medium, true, true, 10, 60),
        new(RepairTool.StoreCacheReset, "wsreset", RepairRisk.Low, false, true, 1, 3),
        new(RepairTool.WinsockReset, "winsock", RepairRisk.Medium, true, true, 1, 1),
        new(RepairTool.IpReset, "ip", RepairRisk.Medium, true, true, 1, 1),
        new(RepairTool.Ipv6Reset, "ipv6", RepairRisk.Medium, true, true, 1, 1),
        new(RepairTool.WinHttpProxyReset, "winhttp", RepairRisk.Low, false, true, 1, 1),
    ];

    public static RepairToolInfo? Find(RepairTool tool) => All.FirstOrDefault(i => i.Tool == tool);
}

/// <summary>Stable codes of repair runs and feature changes.</summary>
public static class RepairCode
{
    public const string Ok = "ok";
    public const string NeedsAdmin = "repair.needs_admin";
    public const string Busy = "repair.busy";
    public const string InvalidRequest = "repair.invalid_request";
    public const string JournalFailed = "repair.journal_failed";
    public const string ToolMissing = "repair.tool_missing";
    public const string StartFailed = "repair.start_failed";
    public const string TimedOut = "repair.timed_out";
    public const string StillRunning = "repair.still_running";
    /// <summary>A program that Winora stopped waiting for still runs; nothing new starts until it ends.</summary>
    public const string PreviousRunning = "repair.previous_running";
    public const string Cancelled = "repair.cancelled";
    public const string ExitNonZero = "repair.exit_nonzero";
    public const string Completed = "repair.completed";
    public const string RestartNeeded = "repair.restart_needed";
    public const string SfcClean = "repair.sfc.clean";
    public const string SfcRepaired = "repair.sfc.repaired";
    public const string SfcUnrepaired = "repair.sfc.unrepaired";
    public const string SfcCouldNotRun = "repair.sfc.could_not_run";
    public const string SfcRebootPending = "repair.sfc.reboot_pending";
    /// <summary>sfc ended with exit 0 but printed no result line Winora can read (another language, or cut output). Not proof of a clean or repaired system.</summary>
    public const string SfcNoVerdict = "repair.sfc.no_verdict";
    public const string DiskClean = "repair.chkdsk.clean";
    public const string DiskFixed = "repair.chkdsk.fixed";
    public const string DiskProblems = "repair.chkdsk.problems";
    public const string ComponentHealthy = "repair.dism.healthy";
    public const string ComponentRepairable = "repair.dism.repairable";
    public const string ComponentNotRepairable = "repair.dism.not_repairable";
    public const string ComponentSourceMissing = "repair.dism.source_missing";
    public const string ComponentSourceBlocked = "repair.dism.source_blocked";
    public const string FeaturesUnreadable = "features.unreadable";
    public const string UnknownFeature = "features.unknown";
    public const string ProtectedFeature = "features.protected";
    public const string RequiredBy = "features.required_by";
    public const string AlreadySet = "features.already_set";
    public const string Stale = "features.stale";
    public const string CannotRevert = "features.cannot_revert";
    public const string VerifyFailed = "features.verify_failed";
}

public static class RepairRules
{
    public static readonly Regex FeatureName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex Volume = new(@"^[A-Za-z]:$", RegexOptions.CultureInvariant);

    public static string NormalizeVolume(string? text)
    {
        var s = (text ?? "").Trim().TrimEnd('\\', '/');
        return Volume.IsMatch(s) ? s.ToUpperInvariant() : "";
    }

    /// <summary>Null when the request is acceptable. Free text never passes: the disk check needs a fixed drive from the list, the feature actions a plain feature name, every other tool no argument.</summary>
    public static string? Validate(RepairRequest request, IReadOnlyList<string> fixedVolumes)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Tool)) return RepairCode.InvalidRequest;
        switch (request.Tool)
        {
            case RepairTool.DiskCheck:
                var volume = NormalizeVolume(request.Argument);
                return volume.Length == 2 && fixedVolumes.Contains(volume, StringComparer.OrdinalIgnoreCase) ? null : RepairCode.InvalidRequest;
            case RepairTool.FeatureEnable:
            case RepairTool.FeatureDisable:
                return request.Argument is not null && FeatureName.IsMatch(request.Argument) ? null : RepairCode.InvalidRequest;
            default:
                return request.Argument is null ? null : RepairCode.InvalidRequest;
        }
    }
}

/// <summary>Append-only record of repair runs and feature changes. <see cref="Begin"/> must be durable before it returns.</summary>
public interface IRepairJournal
{
    void Begin(RepairBegin begin, DateTimeOffset atUtc);
    void Complete(Guid id, RepairEnd end, DateTimeOffset atUtc);
    /// <summary>The newest runs first.</summary>
    IReadOnlyList<RepairRecord> History(int max);
}

/// <summary>Written before the program starts. <see cref="PreviousState"/> is the feature state before a feature action; it is what an undo restores.</summary>
public sealed record RepairBegin(Guid Id, RepairTool Tool, string? Argument, string Command, string? PreviousState, Guid? RevertOf);

public sealed record RepairEnd(RepairOutcome Outcome, int? ExitCode, string Code, bool RestartNeeded, string Summary);

public sealed record RepairRecord(RepairBegin Begin, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, RepairEnd? End)
{
    public bool Interrupted => End is null;
}
