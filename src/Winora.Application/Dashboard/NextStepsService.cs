// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>The rules of the "Recommended next steps" list, in the order they are checked.</summary>
public enum NextStepRule { RestorePoint, SystemProtectionOff, AdministratorMissing, DiskLow, SettingsReset, RestartNeeded, StartupAttention, NoProfile, Scan }

/// <summary>Where the button of a step goes. <see cref="None"/> is a step that only informs.</summary>
public enum NextStepAction { None, OpenCleanup, OpenHistory, OpenStartup, OpenProfiles, Scan, CreateRestorePoint, OpenSystemProtection, ShowAdminHelp }

/// <summary>
/// What the rules need, all of it read from the PC or from the journals of Winora. A value that could not be read is null and its rule stays quiet: a missing fact is never guessed.
/// </summary>
/// <param name="ProtectionOn">System Protection of the system drive: false when Windows says it is off, otherwise not known to be off (Windows gives no reliable "on" answer).</param>
/// <param name="LastRestorePointUtc">The newest restore point Winora created, or null when there is none.</param>
/// <param name="SystemDriveFreeBytes">Free and total bytes of the system drive.</param>
/// <param name="SettingsReset">Settings Winora changed that read differently now.</param>
/// <param name="ChangesWaitingForRestart">Machine changes made since the last start of Windows that need a restart.</param>
/// <param name="StartupAttention">Enabled startup items whose file is missing or whose signature is invalid.</param>
/// <param name="AnyChangeApplied">True when a change was ever applied; null when the journals could not be read.</param>
/// <param name="LastScanUtc">When the inventory was last scanned in this session, or null.</param>
public sealed record NextStepsInput(DateTimeOffset Now, bool? Elevated, bool? ProtectionOn, DateTimeOffset? LastRestorePointUtc, long? SystemDriveFreeBytes, long? SystemDriveTotalBytes,
    string? SystemDrive, int? SettingsReset, int? ChangesWaitingForRestart, int? StartupAttention, bool? AnyChangeApplied, DateTimeOffset? LastScanUtc);

/// <summary>One row. <see cref="Numbers"/> are the real values the sentence quotes, in the order of the text; <see cref="Variant"/> picks between two sentences of one rule.</summary>
public sealed record NextStep(NextStepRule Rule, NextStepAction Action, IReadOnlyList<long> Numbers, int Variant = 0, string? Drive = null);

/// <summary>
/// The fixed rule table behind the Overview list. Each rule is a plain condition on measured facts: there is no score, no weight and no estimate. At most four rows are returned, in the order of
/// <see cref="NextStepRule"/>. The thresholds are public so the text that explains a row can quote them.
/// </summary>
public static class NextStepsService
{
    public const int MaxRows = 4;
    public const int RestorePointMaxAgeDays = 30;
    public const int LowFreePercent = 10;
    public const long LowFreeBytes = 15L * 1024 * 1024 * 1024;
    public const int ScanMaxAgeDays = 14;

    public static IReadOnlyList<NextStep> Evaluate(NextStepsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var steps = new List<NextStep>();

        if (input.ProtectionOn != false)
        {
            if (input.LastRestorePointUtc is not { } last) steps.Add(new(NextStepRule.RestorePoint, NextStepAction.CreateRestorePoint, [], 1));
            else if (input.Now - last > TimeSpan.FromDays(RestorePointMaxAgeDays)) steps.Add(new(NextStepRule.RestorePoint, NextStepAction.CreateRestorePoint, [Days(input.Now - last)]));
        }
        if (input.ProtectionOn == false) steps.Add(new(NextStepRule.SystemProtectionOff, NextStepAction.OpenSystemProtection, []));
        if (input.Elevated == false) steps.Add(new(NextStepRule.AdministratorMissing, NextStepAction.ShowAdminHelp, []));
        if (input.SystemDriveFreeBytes is { } free && input.SystemDriveTotalBytes is { } total && total > 0 && (free * 100 < total * LowFreePercent || free < LowFreeBytes))
            steps.Add(new(NextStepRule.DiskLow, NextStepAction.OpenCleanup, [free / (1024 * 1024 * 1024), (long)Math.Round(free * 100.0 / total, MidpointRounding.AwayFromZero)], 0, input.SystemDrive));
        if (input.SettingsReset is > 0) steps.Add(new(NextStepRule.SettingsReset, NextStepAction.OpenHistory, [input.SettingsReset.Value]));
        if (input.ChangesWaitingForRestart is > 0) steps.Add(new(NextStepRule.RestartNeeded, NextStepAction.None, [input.ChangesWaitingForRestart.Value]));
        if (input.StartupAttention is > 0) steps.Add(new(NextStepRule.StartupAttention, NextStepAction.OpenStartup, [input.StartupAttention.Value]));
        if (input.AnyChangeApplied == false) steps.Add(new(NextStepRule.NoProfile, NextStepAction.OpenProfiles, []));
        if (input.LastScanUtc is null) steps.Add(new(NextStepRule.Scan, NextStepAction.Scan, [], 1));
        else if (input.Now - input.LastScanUtc.Value > TimeSpan.FromDays(ScanMaxAgeDays)) steps.Add(new(NextStepRule.Scan, NextStepAction.Scan, [Days(input.Now - input.LastScanUtc.Value)]));

        return steps.Take(MaxRows).ToList();
    }

    private static long Days(TimeSpan span) => (long)Math.Floor(span.TotalDays);
}
