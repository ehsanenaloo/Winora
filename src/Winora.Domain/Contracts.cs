// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Domain;

public enum OperationState { Planned, Prepared, Executing, Applied, AlreadyConfigured, Unsupported, Failed, Partial, PendingRestart, Reverted }
public enum RecoveryKind { Reversible, Conditional, Irreversible }
public enum RegistryViewKind { Registry32, Registry64 }
public enum RegistryDataKind { String, ExpandString, Binary, DWord, MultiString, QWord, None }
public sealed record RegistrySnapshot(bool KeyExists, bool ValueExists, RegistryViewKind View, RegistryDataKind? Kind, byte[]? RawData)
{
    /// <summary>Snapshots of the further members of a grouped setting, in member order. Null for a setting with one value.</summary>
    public IReadOnlyList<RegistrySnapshot>? More { get; init; }
}
public sealed record BooleanSnapshot(bool Present, bool Value);
public sealed record ResourceIdentity(string Scheme, string Scope, string Path)
{
    public bool Overlaps(ResourceIdentity other) => Scheme == other.Scheme && Scope == other.Scope &&
        (Path == other.Path || Path.StartsWith(other.Path.TrimEnd('/') + "/", StringComparison.Ordinal) ||
         other.Path.StartsWith(Path.TrimEnd('/') + "/", StringComparison.Ordinal));
}
public sealed record FeatureDefinition(string Id, int DefinitionVersion, ResourceIdentity Resource,
    IReadOnlyList<string> Dependencies, RecoveryKind Recovery, string RiskCode, bool RequiresRestart, bool RequiresExplorerRefresh);
public sealed record OperationRecord(Guid Id, string FeatureId, OperationState State, bool Before, bool Desired,
    bool? After, string Code, string ResourceId,
    [property: System.Text.Json.Serialization.JsonRequired] int SchemaVersion = 1,
    [property: System.Text.Json.Serialization.JsonRequired] int DefinitionVersion = 1,
    [property: System.Text.Json.Serialization.JsonRequired] bool CompensationPrepared = false)
{
    [System.Text.Json.Serialization.JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Journal row revision (column, not payload). Save expects the stored value and the store advances it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long Revision { get; init; }
    /// <summary>Simulated resource revision observed when the operation was prepared (null for migrated v1 rows).</summary>
    public long? SourceRevision { get; init; }
    /// <summary>Resource revision produced by this operation's mutation; rollback is bound to it (null when unknown).</summary>
    public long? AppliedRevision { get; init; }
}
public sealed record SimulationPlan(bool Current, bool Desired, bool WillChange)
{
    /// <summary>Pass to SimulationEngine.Apply to fail with plan.stale if the resource changed since this preview.</summary>
    public PlanToken? Token { get; init; }
}
public sealed record DesiredSetting(string FeatureId, bool Desired);
public sealed record DesiredProfile(int SchemaVersion, IReadOnlyList<DesiredSetting> Settings);
/// <summary>Largest profile file Winora imports, in UTF-8 bytes (formerly shared with the retired worker validator stub).</summary>
public static class ProfileLimits
{
    public const int MaximumBytes = 65536;
}
public enum InstallationKind { Msi, Win32, Msix }
public enum InstallationScope { User, Machine }
public enum OwnershipConfidence { Unknown, Low, Confirmed }
public enum OwnershipBasis { Unknown, NameSimilarity, PublisherMatch, InstallationManifest, InstallationLog, InstallerComponent, ExactPackageIdentity }
public sealed record InstallationIdentity(InstallationKind Kind, string StableIdentity, InstallationScope Scope,
    RegistryViewKind? RegistryView, string? Publisher);
public sealed record LeftoverCandidate(InstallationIdentity Installation, ResourceIdentity Resource,
    OwnershipConfidence Confidence, string Provenance, bool Shared, bool UserData, bool ReparsePoint, OwnershipBasis Basis = OwnershipBasis.Unknown)
{
    public bool EligibleForDefaultSelection => Confidence == OwnershipConfidence.Confirmed
        && Basis is OwnershipBasis.InstallationManifest or OwnershipBasis.InstallationLog or OwnershipBasis.InstallerComponent or OwnershipBasis.ExactPackageIdentity
        && !string.IsNullOrWhiteSpace(Provenance) && !Shared && !UserData && !ReparsePoint;
}
public sealed record InstalledApplication(InstallationIdentity Identity, string DisplayName, string? InstallLocation, string RegistrationProvenance,
    string? Version = null, DateOnly? InstalledOn = null, long? EstimatedSizeKb = null, bool IsSystemComponent = false, string? DisplayIcon = null);
public enum UninstallOutcomeState { Removed, Cancelled, Failed, PendingRestart, Unknown }
public sealed record UninstallOutcome(InstallationIdentity Identity, UninstallOutcomeState State, string Code, bool RequiresRestart);
public sealed record CleanupReview(IReadOnlyList<LeftoverCandidate> Selected, DateTimeOffset ReviewedAtUtc, string EvidenceFingerprint);
