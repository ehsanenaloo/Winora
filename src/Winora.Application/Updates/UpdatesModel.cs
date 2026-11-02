// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;
namespace Winora.Application;

/// <summary>The named ways to set Windows Update. Each is a full set of values of the machine catalog, applied together as one group.</summary>
public enum UpdateModeId { WindowsDefault, NoDrivers, Security, Pin, Notify, AutomaticOff }

/// <summary>
/// The choices behind a mode. Delay steps are fixed lists because a definition holds fixed values only. <see cref="ExcludeDrivers"/> applies to the modes that offer it.
/// </summary>
public sealed record UpdateModeOptions(int FeatureDays = 365, int QualityDays = 4, bool ExcludeDrivers = false)
{
    public static IReadOnlyList<int> FeatureDaysSteps { get; } = [30, 90, 180, 365];
    public static IReadOnlyList<int> QualityDaysSteps { get; } = [4, 7, 14, 30];
}

public static class UpdateModes
{
    public const string LabelPrefix = "updates:";
    public static IReadOnlyList<UpdateModeId> Normal { get; } = [UpdateModeId.WindowsDefault, UpdateModeId.NoDrivers, UpdateModeId.Security, UpdateModeId.Pin, UpdateModeId.Notify];
    public static IReadOnlyList<UpdateModeId> Advanced { get; } = [UpdateModeId.AutomaticOff];
    public static bool IsAdvanced(UpdateModeId id) => id == UpdateModeId.AutomaticOff;
    public static bool OffersDriverOption(UpdateModeId id) => id is UpdateModeId.Security or UpdateModeId.Pin or UpdateModeId.Notify;
    public static bool OffersDaysOptions(UpdateModeId id) => id == UpdateModeId.Security;
    public static string Label(UpdateModeId id) => LabelPrefix + id;

    public static UpdateModeId? FromLabel(string? label) =>
        label is not null && label.StartsWith(LabelPrefix, StringComparison.Ordinal) && Enum.TryParse<UpdateModeId>(label[LabelPrefix.Length..], out var id) && Enum.IsDefined(id) ? id : null;
}

/// <summary>Ids of the catalog definitions that the update modes use.</summary>
public static class UpdateIds
{
    public const string ExcludeDrivers = "machine.update.exclude_drivers";
    public const string DeferFeature = "machine.update.defer_feature";
    public const string DeferFeatureDays = "machine.update.defer_feature_days";
    public const string DeferQuality = "machine.update.defer_quality";
    public const string DeferQualityDays = "machine.update.defer_quality_days";
    public const string PinRelease = "machine.update.target_release";
    public const string PinReleaseName = "machine.update.target_release_version";
    public const string PinProduct = "machine.update.target_release_product";
    public const string Notify = "machine.update.au_notify";
    public const string AutomaticOff = "machine.update.auto_updates_off";

    /// <summary>In the order the values are written. Every mode names a choice for each of these, so switching modes never leaves a value of the previous mode behind.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ExcludeDrivers, DeferFeatureDays, DeferFeature, DeferQualityDays, DeferQuality, PinReleaseName, PinProduct, PinRelease, Notify, AutomaticOff];
}

/// <summary>Stable codes of the update modes (the codes of <see cref="OptimizeCode"/> also apply).</summary>
public static class UpdateCode
{
    public const string InvalidOption = "updates.invalid_option";
    public const string ReleaseUnknown = "updates.release_unknown";
    public const string MissingDefinition = "updates.missing_definition";
}

/// <summary>
/// One value of a mode as the review shows it. <see cref="Before"/> is null when the value could not be read. <see cref="RefusalCode"/> is set when this value cannot be changed (managed by
/// policy, unknown state, unsupported Windows build or edition).
/// </summary>
public sealed record UpdateLine(MachineSettingDefinition Definition, string? BeforeChoiceId, StoredValue? Before, string AfterChoiceId, bool WillChange, string? RefusalCode);

public sealed record UpdatePreview(UpdateModeId Mode, UpdateModeOptions Options, IReadOnlyList<UpdateLine> Lines, string? RefusalCode, string? RefusalDefinitionId)
{
    public bool IsRefused => RefusalCode is not null;
    public bool WillChange => Lines.Any(l => l.WillChange);
}

/// <param name="Applied">How many values were written.</param>
/// <param name="Group">The group id of the journal lines, for the undo; null when nothing was written.</param>
public sealed record UpdateApplyResult(bool Ok, string Code, Guid? Group, int Applied);

public sealed record UpdateRevertResult(bool Ok, string Code, int Reverted, int Skipped);

/// <summary>A mode change in the journal, or the undo of one.</summary>
public sealed record UpdateChangeGroup(Guid Group, UpdateModeId? Mode, DateTimeOffset AtUtc, int Changes, bool IsUndo, bool CanRevert);

public sealed record UpdateModeState(UpdateModeId Id, bool Available, string? UnavailableCode, bool IsActive);

public enum UpdateValueSource { Winora, Organization, DeviceManagement }

/// <summary>A value that controls updates on this PC. <see cref="Source"/> says who most likely set it: Winora (the value is the one Winora wrote), Group Policy or another tool, or device management.</summary>
public sealed record UpdatePolicyRow(string KeyPath, string Name, string Data, UpdateValueSource Source);

/// <summary>Until when updates are paused in Windows Settings. A date in the past is not a pause.</summary>
public sealed record UpdatePause(DateTimeOffset? SecurityUntilUtc, DateTimeOffset? FeatureUntilUtc)
{
    public bool IsPaused => SecurityUntilUtc is not null || FeatureUntilUtc is not null;
}

public sealed record UpdatesState(
    bool CatalogAvailable,
    IReadOnlyList<UpdateModeState> Modes,
    UpdateModeId? ActiveMode,
    UpdateModeOptions? ActiveOptions,
    bool IsCustom,
    bool ManagedByOrganization,
    IReadOnlyList<UpdatePolicyRow> PolicyRows,
    UpdatePause Pause,
    string? Build,
    string? DisplayVersion,
    int? UpdateRevision,
    UpdateFacts Facts);

// ---- read-only facts ----

/// <param name="Display">The data as text; long text is cut. Never used as an instruction.</param>
public sealed record UpdatePolicyValue(string KeyPath, string Name, string Display);

/// <summary>What Windows holds about updates on this PC, read without administrator rights. Null means the value is not there or could not be read.</summary>
public sealed record UpdateFacts(
    IReadOnlyList<UpdatePolicyValue> PolicyValues,
    IReadOnlyList<UpdatePolicyValue> DeviceManagementValues,
    DateTimeOffset? LastCheckUtc,
    DateTimeOffset? LastInstallUtc,
    IReadOnlyList<string> PendingRestartSources,
    DateTimeOffset? QualityPauseUntilUtc,
    DateTimeOffset? FeaturePauseUntilUtc,
    bool? GetUpdatesAsSoonAsAvailable)
{
    public static UpdateFacts Empty { get; } = new([], [], null, null, [], null, null, null);
}

/// <summary>Reads the update facts. The Windows adapter reads the registry only.</summary>
public interface IUpdateFactsSource
{
    UpdateFacts Read();
}
