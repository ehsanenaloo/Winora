// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

public enum ProfileEntryKind { User, Machine }

/// <summary>One setting of a profile: an existing catalog id with the choice to set. Nothing else, so a profile can never carry a path, a value or a command.</summary>
/// <param name="ReasonKey">Resource key of the one plain line that says why the entry is in a built-in profile. Null for custom profiles.</param>
public sealed record ProfileEntry(ProfileEntryKind Kind, string Id, string ChoiceId, string? ReasonKey = null);

/// <summary>A named list of entries. Built-in profiles have resource keys for name and description; a custom profile has a plain name.</summary>
public sealed record Profile(string Id, string? NameKey, string? DescriptionKey, string? Name, IReadOnlyList<ProfileEntry> Entries)
{
    public bool IsBuiltIn => NameKey is not null;
}

/// <summary>What the catalogs say about one id; the parser uses it to refuse unknown ids, unknown choices and High risk entries.</summary>
public sealed record ProfileReference(RiskLevel Risk, IReadOnlySet<string> ChoiceIds);

public interface IProfileReferences
{
    ProfileReference? Find(ProfileEntryKind kind, string id);
}

public enum ProfileEntryStatus { WillChange, AlreadySet, Skipped }

/// <summary>An entry as it stands on this PC.</summary>
public sealed record ProfilePlanEntry(ProfileEntry Entry, ProfileEntryStatus Status, string? SkipCode, string TitleKey, SettingCategory Category, RiskLevel Risk,
    string? BeforeChoiceId, string? BeforeLabelKey, string AfterLabelKey)
{
    public SettingPlan? UserPlan { get; init; }
    public OptimizeRequest? MachineRequest { get; init; }
    public bool NeedsRestart { get; init; }
}

public sealed record ProfilePlan(Profile Profile, IReadOnlyList<ProfilePlanEntry> Entries)
{
    public int ChangeCount => Entries.Count(e => e.Status == ProfileEntryStatus.WillChange);
    public int AlreadySetCount => Entries.Count(e => e.Status == ProfileEntryStatus.AlreadySet);
    public int SkippedCount => Entries.Count(e => e.Status == ProfileEntryStatus.Skipped);
    public int MachineChangeCount => Entries.Count(e => e.Status == ProfileEntryStatus.WillChange && e.Entry.Kind == ProfileEntryKind.Machine);
    public int LowRiskChangeCount => Entries.Count(e => e.Status == ProfileEntryStatus.WillChange && e.Risk == RiskLevel.Low);
    public int MediumRiskChangeCount => Entries.Count(e => e.Status == ProfileEntryStatus.WillChange && e.Risk == RiskLevel.Medium);
}

public enum ProfileEntryOutcome { Applied, AlreadySet, Skipped, Failed, NotRun }

public sealed record ProfileEntryResult(ProfileEntry Entry, ProfileEntryOutcome Outcome, string Code, bool NeedsRestart);

/// <param name="Cancelled">The user cancelled, or the restore point question was cancelled; entries after that point are NotRun.</param>
public sealed record ProfileApplyResult(Guid Group, string ProfileId, IReadOnlyList<ProfileEntryResult> Entries, bool Cancelled, RestorePointGateResult? RestorePoint)
{
    public int Applied => Entries.Count(e => e.Outcome == ProfileEntryOutcome.Applied);
    public int Failed => Entries.Count(e => e.Outcome == ProfileEntryOutcome.Failed);
    public int Skipped => Entries.Count(e => e.Outcome is ProfileEntryOutcome.Skipped or ProfileEntryOutcome.AlreadySet);
    public int NotRun => Entries.Count(e => e.Outcome == ProfileEntryOutcome.NotRun);
}

public sealed record ProfileProgress(int Done, int Total, ProfileEntry? Current);

public enum ProfileUndoOutcome { Reverted, Changed, Failed }

public sealed record ProfileUndoEntry(ProfileEntryKind Kind, string Id, ProfileUndoOutcome Outcome, string Code);

public sealed record ProfileUndoResult(Guid Group, IReadOnlyList<ProfileUndoEntry> Entries, bool Cancelled)
{
    public int Reverted => Entries.Count(e => e.Outcome == ProfileUndoOutcome.Reverted);
    public int Left => Entries.Count(e => e.Outcome != ProfileUndoOutcome.Reverted);
}

/// <summary>One applied profile as the journals show it.</summary>
/// <param name="Label">The profile id (built-in id or "custom.name") written into the journal rows.</param>
/// <param name="Changed">How many settings the run changed.</param>
/// <param name="StillApplied">How many of them were not undone yet.</param>
public sealed record ProfileHistoryEntry(Guid Group, string Label, DateTimeOffset AtUtc, int Changed, int StillApplied);

/// <summary>One setting the user picked, for saving as a profile.</summary>
public sealed record ProfileSelection(ProfileEntryKind Kind, string Id, string ChoiceId);

/// <summary>The entries a custom profile can take from a selection or from the changes Winora made, and how many were left out.</summary>
public sealed record ProfileDraft(IReadOnlyList<ProfileEntry> Entries, int RefusedHighRisk, int RefusedUnknown);

/// <summary>The built-in and custom profiles that loaded, and the ones that did not.</summary>
public sealed record ProfileCatalogResult(IReadOnlyList<Profile> BuiltIn, IReadOnlyList<Profile> Custom, IReadOnlyList<ProfileLoadError> Errors);

/// <param name="Source">The built-in id or the custom file name; never a path.</param>
public sealed record ProfileLoadError(string Source, string Code, string Detail);

/// <summary>Stable codes of the profile engine.</summary>
public static class ProfileCode
{
    public const string Ok = "ok";
    public const string AlreadySet = "profile.already_set";
    public const string UnknownEntry = "profile.unknown_entry";
    public const string HighRisk = "profile.high_risk";
    public const string NeedsAdministrator = "profile.needs_administrator";
    public const string UnsupportedBuild = "profile.unsupported_build";
    public const string UnsupportedEdition = "profile.unsupported_edition";
    public const string ManagedByPolicy = "profile.managed_by_policy";
    public const string StateUnrecognized = "profile.state_unrecognized";
    public const string Unreadable = "profile.unreadable";
    public const string Stale = "profile.stale";
    public const string Conflict = "profile.conflict";
    public const string Failed = "profile.failed";
    public const string Cancelled = "profile.cancelled";
    public const string Superseded = "profile.superseded";
    public const string NoCatalog = "profile.no_catalog";
    public const string EmptyName = "profile.empty_name";
    public const string NothingToSave = "profile.nothing_to_save";
    public const string TooMany = "profile.too_many";
    public const string Invalid = "profile.invalid";
    public const string TooLarge = "profile.too_large";
    public const string StoreFailed = "profile.store_failed";
}

/// <summary>Where custom profiles live: one JSON file per profile in the application data folder. The store never trusts what it reads.</summary>
public interface IProfileStore
{
    /// <summary>The names of the stored profiles (file stems) in a stable order.</summary>
    IReadOnlyList<string> List();
    /// <summary>The text of one profile file, or null when it does not exist, is too large or cannot be read.</summary>
    string? Read(string id);
    /// <summary>Writes the file for the id (replacing it). Throws IOException when it cannot be written.</summary>
    void Write(string id, string json);
    void Delete(string id);
}
