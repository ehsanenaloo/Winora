// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

/// <summary>What to do after the registry write so the change really takes effect. Default: broadcast and restart Explorer when the definition needs it.</summary>
public sealed record SettingApplyOptions(bool RefreshShell, bool RestartExplorer)
{
    /// <summary>Marks the journal row as one change of a group (a profile) so the group can be listed and undone together.</summary>
    public Guid? Group { get; init; }
    public string? Label { get; init; }
    public static SettingApplyOptions Default { get; } = new(true, true);
    public static SettingApplyOptions WriteOnly { get; } = new(false, false);
}

/// <summary>Read-only access to the trusted catalog. Implementations return authored data only.</summary>
public interface ISettingCatalog
{
    IReadOnlyList<SettingDefinition> All { get; }
    SettingDefinition? Find(string id);
}

/// <summary>
/// User-scope registry port. EVERY method validates the target against the catalog allowlist and throws
/// WinoraException(target.not_allowed) otherwise; there is no generic write. Writes are by (definition, choice), never raw paths.
/// </summary>
public interface IRegistrySettingsAccess
{
    RegistrySnapshot Read(SettingDefinition definition);
    /// <summary>Key last-write time as a change counter proxy (null if the key is missing). Best-effort ABA signal.</summary>
    long? ReadRevision(SettingDefinition definition);
    /// <summary>Writes the value only if the live snapshot still equals expected; returns false (no write) otherwise.</summary>
    bool CompareAndSet(SettingDefinition definition, RegistrySnapshot expected, RegistryValueSpec desired);
}

/// <summary>The one entry point Desktop and CLI use. Failures surface as WinoraException with catalog codes; per-item problems as SettingState.Issue.</summary>
public interface ISettingsService
{
    Task<IReadOnlyList<SettingState>> ReadAllAsync(CancellationToken cancellationToken);
    Task<SettingState> ReadAsync(string settingId, CancellationToken cancellationToken);
    Task<SettingPlan> PlanAsync(string settingId, string choiceId, CancellationToken cancellationToken);
    Task<SettingOutcome> ApplyAsync(SettingPlan reviewedPlan, CancellationToken cancellationToken);
    /// <summary>Applies and then, per options and the definition's needs, makes the change take effect (broadcast, Explorer restart). The outcome is Applied only when the refresh verifiably succeeded; otherwise PendingRestart with the refresh failure code.</summary>
    Task<SettingOutcome> ApplyAsync(SettingPlan reviewedPlan, SettingApplyOptions options, CancellationToken cancellationToken);
    /// <summary>Runs the shell refresh for an operation left PendingRestart (the user chose 'Apply now' later). Journaled; becomes Applied only on verified success.</summary>
    Task<SettingOutcome> CompleteAsync(Guid operationId, CancellationToken cancellationToken);
    Task<SettingOutcome> RevertAsync(Guid operationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SettingOutcome>> RecoverAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SettingOutcome>> HistoryAsync(CancellationToken cancellationToken);
}
