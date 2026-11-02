// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Domain;

public enum ErrorSeverity { Info, Warning, Error, Critical }
public enum ErrorCategory { Validation, Conflict, Unsupported, Denied, Transient, Corruption, Cancelled, Internal }

/// <summary>
/// Stable error and outcome codes. Strings are persisted (OperationRecord.Code, ledger) and shown by hosts; never rename or translate them.
/// Persisted codes that predate this catalog keep their exact spelling.
/// </summary>
public static class ErrorCode
{
    // Operations (persisted outcome codes use the same spelling as before the catalog existed)
    public const string RecoveryRequired = "operation.recovery_required";
    public const string NotReversible = "operation.not_reversible";
    public const string Superseded = "operation.superseded";
    public const string RevisionUnknown = "operation.revision_unknown";
    public const string ResourceChanged = "operation.resource_changed";
    public const string ExternalConflict = "operation.external_conflict";
    public const string VerificationFailed = "operation.verification_failed";
    public const string ReconciledPartial = "operation.reconciled_partial";
    public const string RollbackUncertain = "operation.rollback_uncertain";
    public const string RollbackFailed = "operation.rollback_failed";
    public const string Cancelled = "operation.cancelled";
    // Planning
    public const string PlanStale = "plan.stale";
    public const string PlanDuplicateFeature = "plan.duplicate_feature";
    public const string PlanMissingDependency = "plan.missing_dependency";
    public const string PlanResourceConflict = "plan.resource_conflict";
    public const string PlanDependencyCycle = "plan.dependency_cycle";
    // Journal
    public const string RevisionConflict = "journal.revision_conflict";
    public const string OperationMissing = "journal.operation_missing";
    public const string InvalidTransition = "journal.invalid_transition";
    public const string JournalInvalid = "journal.invalid";
    public const string SchemaUnsupported = "journal.schema_unsupported";
    public const string SchemaCorrupt = "journal.schema_corrupt";
    public const string MigrationConflict = "journal.migration_conflict";
    public const string UntrustedDefinition = "journal.untrusted_definition";
    // Store
    public const string StoreBusy = "store.busy";
    public const string StoreUnavailable = "store.unavailable";
    public const string StoreFailure = "store.failure";
    // Worker protocol and profiles
    public const string ProtocolSize = "protocol.size";
    public const string ProtocolVersion = "protocol.version";
    // LEGACY: produced only by the retired foundation validator stub. Kept readable and localizable because no proof exists that
    // no persisted data carries them (see ErrorCatalog.LegacyCodes); nothing raises them.
    public const string ProtocolOperation = "protocol.operation";
    public const string ProtocolCommand = "protocol.command";
    public const string ProtocolFeature = "protocol.feature";
    public const string WorkerUnimplemented = "worker.unimplemented";
    public const string ProfileSize = "profile.size";
    public const string ProfileInvalid = "profile.invalid";
    public const string ProfileDuplicateProperty = "profile.duplicate_property";
    // Elevation channel, milestone; only the codes that paths raise
    public const string ElevationUnavailable = "elevation.unavailable";
    public const string ElevationUnsignedBuild = "elevation.unsigned_build";
    public const string ElevationInstallUntrusted = "elevation.install_untrusted";
    public const string ElevationUacDisabled = "elevation.uac_disabled";
    public const string ElevationClientElevated = "elevation.client_elevated";
    public const string ElevationDeclined = "elevation.declined";
    public const string ElevationLaunchFailed = "elevation.launch_failed";
    public const string ElevationBlocked = "elevation.blocked";
    public const string ElevationTimeout = "elevation.timeout";
    public const string ElevationServerUntrusted = "elevation.server_untrusted";
    public const string ElevationClientUntrusted = "elevation.client_untrusted";
    public const string ElevationChannelFailed = "elevation.channel_failed";
    public const string ElevationConfirmationRequired = "elevation.confirmation_required";
    public const string ElevationPlatformUnsupported = "elevation.platform_unsupported";
    public const string ElevationPlatformUnverified = "elevation.platform_unverified";
    public const string DefinitionUnknown = "definition.unknown";
    public const string DefinitionSetMismatch = "definition.set_mismatch";
    public const string DefinitionRevoked = "definition.revoked";
    public const string DefinitionSignatureInvalid = "definition.signature_invalid";
    public const string DefinitionRollback = "definition.rollback";
    public const string TrustStoreInsecure = "trust.store_insecure";
    public const string WorkerBusy = "worker.busy";
    public const string WorkerHardeningFailed = "worker.hardening_failed";
    // Discovery
    public const string DiscoveryUnavailable = "discovery.unavailable";
    public const string DiscoveryItemUnreadable = "discovery.item_unreadable";
    // Settings
    public const string SettingUnknown = "setting.unknown";
    public const string SettingChoiceInvalid = "setting.choice_invalid";
    public const string SettingUnsupportedBuild = "setting.unsupported_build";
    public const string SettingStateUnrecognized = "setting.state_unrecognized";
    public const string SettingManagedByPolicy = "setting.managed_by_policy";
    public const string ShellRefreshFailed = "shell.refresh_failed";
    public const string ShellRestartFailed = "shell.restart_failed";
    public const string TargetNotAllowed = "target.not_allowed";
    public const string TargetKindUnsupported = "target.kind_unsupported";
    // Diagnostics export
    public const string DiagnosticsDestinationInvalid = "diagnostics.destination_invalid";
    public const string DiagnosticsExportFailed = "diagnostics.export_failed";
    // General
    public const string AccessDenied = "access.denied";
    public const string Unexpected = "error.unexpected";
}

/// <summary>Catalog facts for one code. UserMessageKey is a Strings.resx key, never display text.</summary>
public sealed record ErrorInfo(string Code, ErrorCategory Category, ErrorSeverity Severity, bool Retryable)
{
    public string UserMessageKey => ErrorCatalog.ResourceKey(Code);
}

public static class ErrorCatalog
{
    public const string ResourceKeyPrefix = "Code_";
    /// <summary>Strings.resx key for a code: "Code_" plus the code with dots as underscores (e.g. plan.stale -> Code_plan_stale).</summary>
    public static string ResourceKey(string code) => ResourceKeyPrefix + code.Replace('.', '_');
    private static ErrorInfo I(string code, ErrorCategory category, ErrorSeverity severity, bool retryable = false) => new(code, category, severity, retryable);

    public static IReadOnlyList<ErrorInfo> All { get; } =
    [
        I(ErrorCode.RecoveryRequired, ErrorCategory.Conflict, ErrorSeverity.Warning),
        I(ErrorCode.NotReversible, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.Superseded, ErrorCategory.Conflict, ErrorSeverity.Warning),
        I(ErrorCode.RevisionUnknown, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.ResourceChanged, ErrorCategory.Conflict, ErrorSeverity.Warning, true),
        I(ErrorCode.ExternalConflict, ErrorCategory.Conflict, ErrorSeverity.Warning),
        I(ErrorCode.VerificationFailed, ErrorCategory.Conflict, ErrorSeverity.Error),
        I(ErrorCode.ReconciledPartial, ErrorCategory.Conflict, ErrorSeverity.Error),
        I(ErrorCode.RollbackUncertain, ErrorCategory.Conflict, ErrorSeverity.Error),
        I(ErrorCode.RollbackFailed, ErrorCategory.Conflict, ErrorSeverity.Error),
        I(ErrorCode.Cancelled, ErrorCategory.Cancelled, ErrorSeverity.Info),
        I(ErrorCode.PlanStale, ErrorCategory.Conflict, ErrorSeverity.Warning, true),
        I(ErrorCode.PlanDuplicateFeature, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.PlanMissingDependency, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.PlanResourceConflict, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.PlanDependencyCycle, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.RevisionConflict, ErrorCategory.Conflict, ErrorSeverity.Warning, true),
        I(ErrorCode.OperationMissing, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.InvalidTransition, ErrorCategory.Corruption, ErrorSeverity.Error),
        I(ErrorCode.JournalInvalid, ErrorCategory.Corruption, ErrorSeverity.Critical),
        I(ErrorCode.SchemaUnsupported, ErrorCategory.Unsupported, ErrorSeverity.Critical),
        I(ErrorCode.SchemaCorrupt, ErrorCategory.Corruption, ErrorSeverity.Critical),
        I(ErrorCode.MigrationConflict, ErrorCategory.Corruption, ErrorSeverity.Critical),
        I(ErrorCode.UntrustedDefinition, ErrorCategory.Corruption, ErrorSeverity.Critical),
        I(ErrorCode.StoreBusy, ErrorCategory.Transient, ErrorSeverity.Warning, true),
        I(ErrorCode.StoreUnavailable, ErrorCategory.Transient, ErrorSeverity.Error, true),
        I(ErrorCode.StoreFailure, ErrorCategory.Internal, ErrorSeverity.Error),
        I(ErrorCode.ProtocolSize, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.ProtocolVersion, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.ProtocolOperation, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.ProtocolCommand, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.ProtocolFeature, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.WorkerUnimplemented, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.ElevationUnavailable, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.ElevationUnsignedBuild, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.ElevationInstallUntrusted, ErrorCategory.Denied, ErrorSeverity.Error),
        I(ErrorCode.ElevationUacDisabled, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.ElevationClientElevated, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.ElevationDeclined, ErrorCategory.Cancelled, ErrorSeverity.Info),
        I(ErrorCode.ElevationLaunchFailed, ErrorCategory.Transient, ErrorSeverity.Error, true),
        I(ErrorCode.ElevationBlocked, ErrorCategory.Denied, ErrorSeverity.Error),
        I(ErrorCode.ElevationTimeout, ErrorCategory.Transient, ErrorSeverity.Warning, true),
        I(ErrorCode.ElevationServerUntrusted, ErrorCategory.Denied, ErrorSeverity.Critical),
        I(ErrorCode.ElevationClientUntrusted, ErrorCategory.Denied, ErrorSeverity.Error),
        I(ErrorCode.ElevationChannelFailed, ErrorCategory.Internal, ErrorSeverity.Error),
        I(ErrorCode.ElevationConfirmationRequired, ErrorCategory.Denied, ErrorSeverity.Warning),
        I(ErrorCode.ElevationPlatformUnsupported, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.ElevationPlatformUnverified, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.DefinitionUnknown, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.DefinitionSetMismatch, ErrorCategory.Corruption, ErrorSeverity.Critical),
        I(ErrorCode.DefinitionRevoked, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.DefinitionSignatureInvalid, ErrorCategory.Corruption, ErrorSeverity.Critical),
        I(ErrorCode.DefinitionRollback, ErrorCategory.Denied, ErrorSeverity.Error),
        I(ErrorCode.TrustStoreInsecure, ErrorCategory.Corruption, ErrorSeverity.Critical),
        I(ErrorCode.SettingManagedByPolicy, ErrorCategory.Denied, ErrorSeverity.Warning),
        I(ErrorCode.WorkerBusy, ErrorCategory.Transient, ErrorSeverity.Warning, true),
        I(ErrorCode.WorkerHardeningFailed, ErrorCategory.Internal, ErrorSeverity.Error),
        I(ErrorCode.ProfileSize, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.ProfileInvalid, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.ProfileDuplicateProperty, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.DiscoveryUnavailable, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.DiscoveryItemUnreadable, ErrorCategory.Validation, ErrorSeverity.Info),
        I(ErrorCode.SettingUnknown, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.SettingChoiceInvalid, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.SettingUnsupportedBuild, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.SettingStateUnrecognized, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.ShellRefreshFailed, ErrorCategory.Transient, ErrorSeverity.Warning, true),
        I(ErrorCode.ShellRestartFailed, ErrorCategory.Transient, ErrorSeverity.Warning, true),
        I(ErrorCode.TargetNotAllowed, ErrorCategory.Denied, ErrorSeverity.Critical),
        I(ErrorCode.TargetKindUnsupported, ErrorCategory.Unsupported, ErrorSeverity.Warning),
        I(ErrorCode.DiagnosticsDestinationInvalid, ErrorCategory.Validation, ErrorSeverity.Error),
        I(ErrorCode.DiagnosticsExportFailed, ErrorCategory.Transient, ErrorSeverity.Error, true),
        I(ErrorCode.AccessDenied, ErrorCategory.Denied, ErrorSeverity.Error),
        I(ErrorCode.Unexpected, ErrorCategory.Internal, ErrorSeverity.Critical),
    ];

    /// <summary>
    /// Codes that nothing raises any more but that stay in the catalog so persisted data and old logs remain readable and localizable.
    /// The retired foundation validator stub was their only producer.
    /// </summary>
    public static IReadOnlyList<string> LegacyCodes { get; } = [ErrorCode.ProtocolOperation, ErrorCode.ProtocolCommand, ErrorCode.ProtocolFeature];
    public static bool IsLegacy(string code) => LegacyCodes.Contains(code);

    private static readonly Dictionary<string, ErrorInfo> ByCode = All.ToDictionary(i => i.Code, StringComparer.Ordinal);

    public static bool TryGet(string code, out ErrorInfo info) => ByCode.TryGetValue(code, out info!);
    public static bool IsKnown(string code) => ByCode.ContainsKey(code);
    /// <summary>Unknown codes degrade to the Internal entry instead of throwing.</summary>
    public static ErrorInfo Describe(string code) => ByCode.TryGetValue(code, out var info) ? info : ByCode[ErrorCode.Unexpected];

    /// <summary>Process exit code for CLI hosts: 0 success is the caller's; failures map by category.</summary>
    public static int ExitCode(ErrorCategory category) => category switch
    {
        ErrorCategory.Validation => 2, ErrorCategory.Conflict => 3, ErrorCategory.Unsupported => 4, ErrorCategory.Denied => 5,
        ErrorCategory.Transient => 6, ErrorCategory.Corruption => 7, ErrorCategory.Cancelled => 130, _ => 1,
    };
}

/// <summary>
/// A classified failure ready for UI and logs. UserMessageKey is a resource key; Detail is safe technical text
/// (codes, ids, revisions only; no user paths, secrets or raw exception messages).
/// </summary>
public sealed record WinoraError(string Code, ErrorCategory Category, ErrorSeverity Severity, bool Retryable, string UserMessageKey,
    string? Detail = null, string? CorrelationId = null, Guid? OperationId = null)
{
    public static WinoraError From(string code, string? detail = null, Guid? operationId = null, string? correlationId = null)
    {
        var info = ErrorCatalog.Describe(code); // unknown codes degrade to internal.unexpected
        return new(info.Code, info.Category, info.Severity, info.Retryable, info.UserMessageKey, detail, correlationId, operationId);
    }
    public int ExitCode => ErrorCatalog.ExitCode(Category);
}

/// <summary>For throw sites that cannot return a result.</summary>
public sealed class WinoraException(WinoraError error, Exception? inner = null) : Exception(error.Code, inner)
{
    public WinoraError Error { get; } = error;
}

/// <summary>Expected failures as values. Never carry a success flag with an error, or vice versa.</summary>
public readonly record struct Result<T>
{
    private readonly T? value;
    public WinoraError? Error { get; }
    public bool IsSuccess => Error is null;
    public T Value => Error is null ? value! : throw new InvalidOperationException("result.failed");
    private Result(T? value, WinoraError? error) { this.value = value; Error = error; }
    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(WinoraError error) => new(default, error);
}

/// <summary>Result of an action with no value.</summary>
public readonly record struct OperationOutcome(WinoraError? Error)
{
    public bool IsSuccess => Error is null;
    public static OperationOutcome Ok { get; } = new(null);
    public static OperationOutcome Fail(WinoraError error) => new(error);
}
