// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using Winora.Domain;
namespace Winora.Application;

/// <summary>Who owns an in-flight operation. Recovery never touches a row whose owner process is still alive.</summary>
public interface IProcessLiveness
{
    (int ProcessId, long StartTicks) Current { get; }
    bool IsAlive(int processId, long startTicks);
}

public sealed class SystemProcessLiveness : IProcessLiveness
{
    public static SystemProcessLiveness Instance { get; } = new();
    public (int ProcessId, long StartTicks) Current { get; } = (Environment.ProcessId, StartOf(Process.GetCurrentProcess()));

    public bool IsAlive(int processId, long startTicks)
    {
        if (processId == Environment.ProcessId) return startTicks == Current.StartTicks || startTicks == 0;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            return startTicks == 0 || StartOf(process) == startTicks;
        }
        catch (ArgumentException) { return false; } // no such process
        catch (InvalidOperationException) { return false; } // exited meanwhile
        catch { return true; } // cannot tell (access denied): assume alive, never recover under a live writer
    }

    private static long StartOf(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; } catch { return 0; }
    }
}

/// <summary>
/// Typed registry-setting engine: the M0 invariants generalized to catalog settings. The write target is ALWAYS taken from the trusted
/// catalog; the journal supplies only ids and before/after snapshots. Blocking store and registry calls run on the thread pool.
/// Failures surface as <see cref="WinoraException"/> with catalog codes; cancellation as <see cref="OperationCanceledException"/>,
/// and only at safe boundaries (before prepare and between steps, never during a mutation).
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private const string CodePrepared = "operation.prepared";
    private const string CodeExecuting = "operation.executing";
    private const string CodeApplied = "operation.applied";
    private const string CodeAlready = "operation.already_configured";
    private const string CodeReverted = "operation.reverted";
    private const string CodeRollbackPrepared = "operation.rollback_prepared";
    private const string CodeRollbackAbandoned = "operation.rollback_abandoned";
    private const string CodeReconciledApplied = "operation.reconciled_applied";
    private const string CodeReconciledUnchanged = "operation.reconciled_unchanged";
    private const string CodeRefreshPending = "operation.refresh_pending";
    private const string CodeRestartPending = "operation.restart_pending";
    private const string CodeSignOutPending = "operation.signout_pending";
    private const string CodeShellRefreshed = "operation.shell_refreshed";
    private static readonly SettingApplyOptions RecoveryOptions = new(true, false); // recovery never restarts a shell

    private readonly ISettingCatalog catalog;
    private readonly IRegistrySettingsAccess registry;
    private readonly ISettingOperationStore store;
    private readonly IOsBuildProvider? osBuild;
    private readonly TimeProvider clock;
    private readonly ErrorMapper mapper;
    private readonly IProcessLiveness liveness;
    private readonly IShellRefresh? shell;

    public SettingsService(ISettingCatalog catalog, IRegistrySettingsAccess registry, ISettingOperationStore store, TimeProvider? timeProvider)
        : this(catalog, registry, store, null, null, timeProvider) { }

    /// <param name="shellRefresh">Fixed shell actions run after a verified write. When absent every refresh counts as failed (PendingRestart), never as success.</param>
    public SettingsService(ISettingCatalog catalog, IRegistrySettingsAccess registry, ISettingOperationStore store,
        IOsBuildProvider? osBuild = null, IShellRefresh? shellRefresh = null, TimeProvider? timeProvider = null, ErrorMapper? errorMapper = null, IProcessLiveness? liveness = null)
    {
        shell = shellRefresh;
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.osBuild = osBuild;
        clock = timeProvider ?? TimeProvider.System;
        mapper = errorMapper ?? new ErrorMapper();
        this.liveness = liveness ?? SystemProcessLiveness.Instance;
    }

    // ---- public API ------------------------------------------------------------------------------

    public Task<IReadOnlyList<SettingState>> ReadAllAsync(CancellationToken cancellationToken) => Run<IReadOnlyList<SettingState>>(() =>
    {
        var states = new List<SettingState>();
        foreach (var definition in catalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            states.Add(ReadState(definition));
        }
        return states;
    }, cancellationToken);

    public Task<SettingState> ReadAsync(string settingId, CancellationToken cancellationToken) =>
        Run(() => ReadState(Resolve(settingId)), cancellationToken);

    public Task<SettingPlan> PlanAsync(string settingId, string choiceId, CancellationToken cancellationToken) =>
        Run(() => PlanCore(settingId, choiceId, cancellationToken), cancellationToken);

    public Task<SettingOutcome> ApplyAsync(SettingPlan reviewedPlan, CancellationToken cancellationToken) =>
        ApplyAsync(reviewedPlan, SettingApplyOptions.Default, cancellationToken);

    public Task<SettingOutcome> ApplyAsync(SettingPlan reviewedPlan, SettingApplyOptions options, CancellationToken cancellationToken) =>
        Run(() => ApplyCore(reviewedPlan, options ?? SettingApplyOptions.Default, cancellationToken), cancellationToken);

    public Task<SettingOutcome> CompleteAsync(Guid operationId, CancellationToken cancellationToken) =>
        Run(() => CompleteCore(operationId, cancellationToken), cancellationToken, operationId);

    public Task<SettingOutcome> RevertAsync(Guid operationId, CancellationToken cancellationToken) =>
        Run(() => RevertCore(operationId, cancellationToken), cancellationToken, operationId);

    public Task<IReadOnlyList<SettingOutcome>> RecoverAsync(CancellationToken cancellationToken) =>
        Run(() => RecoverCore(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<SettingOutcome>> HistoryAsync(CancellationToken cancellationToken) => Run<IReadOnlyList<SettingOutcome>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return store.ListSettingOperations().Reverse().Select(Outcome).ToList();
    }, cancellationToken);

    private async Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken, Guid? operationId = null)
    {
        try { return await Task.Run(work, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (WinoraException) { throw; }
        catch (Exception e) { throw new WinoraException(mapper.Map(e, operationId), e); }
    }

    // ---- read and plan ---------------------------------------------------------------------------

    private SettingState ReadState(SettingDefinition definition)
    {
        var compatibility = Compatibility(definition);
        try
        {
            var (snapshot, revision) = Observe(definition);
            return new(definition, compatibility, SettingSnapshots.MatchChoice(definition, snapshot), snapshot, revision, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(definition, compatibility, null, new RegistrySnapshot(false, false, definition.Target.View, null, null), null,
                new DiscoveryIssue(definition.Id, mapper.Map(e)));
        }
    }

    private SettingPlan PlanCore(string settingId, string choiceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definition = Resolve(settingId);
        var choice = ResolveChoice(definition, choiceId);
        EnsureBuild(definition);
        var (observed, revision) = Observe(definition);
        var willChange = !SettingSnapshots.Matches(observed, choice.Value);
        if (willChange) { EnsureRecognized(definition, observed); EnsureApplicable(choice, observed); }
        return new(definition.Id, definition.Version, choice.Id, observed, revision, willChange, SettingSnapshots.MatchChoice(definition, observed),
            SettingPlanTokens.Compute(definition.Id, definition.Version, choice.Id, observed, null));
    }

    // ---- apply -----------------------------------------------------------------------------------

    private SettingOutcome ApplyCore(SettingPlan? plan, SettingApplyOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan is null || string.IsNullOrEmpty(plan.Token)) throw new OperationException(ErrorCode.PlanStale);
        var definition = Resolve(plan.SettingId);
        var choice = ResolveChoice(definition, plan.ChoiceId);
        if (plan.DefinitionVersion != definition.Version) throw new OperationException(ErrorCode.PlanStale);
        EnsureBuild(definition);
        var (observed, revision) = Observe(definition);
        if (!string.Equals(SettingPlanTokens.Compute(definition.Id, definition.Version, choice.Id, observed, null), plan.Token, StringComparison.Ordinal))
            throw new OperationException(ErrorCode.PlanStale);

        if (SettingSnapshots.Matches(observed, choice.Value)) // idempotent: nothing is written to the registry
            return Outcome(store.PrepareSetting(New(definition, choice, observed, revision, OperationState.AlreadyConfigured, CodeAlready, options) with { After = observed }));
        EnsureRecognized(definition, observed);
        EnsureApplicable(choice, observed);

        var record = store.PrepareSetting(New(definition, choice, observed, revision, OperationState.Prepared, CodePrepared, options)); // failure here precedes any mutation
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Save(record with { State = OperationState.Failed, Code = ErrorCode.Cancelled });
                cancellationToken.ThrowIfCancellationRequested();
            }
            record = Save(record with { State = OperationState.Executing, Code = CodeExecuting });
            try
            {
                // The target comes from the catalog definition, never from the journal row.
                var swapped = registry.CompareAndSet(definition, record.Before, choice.Value);
                var actual = registry.Read(definition);
                var actualRevision = registry.ReadRevision(definition);
                record = !swapped
                    ? record with { State = OperationState.Failed, After = actual, Code = ErrorCode.ExternalConflict }
                    : SettingSnapshots.Matches(actual, choice.Value)
                        ? record with { State = OperationState.Applied, After = actual, AppliedRevision = actualRevision, Code = CodeApplied }
                        : record with { State = OperationState.Partial, After = actual, Code = ErrorCode.VerificationFailed };
            }
            catch (Exception e) when (e is not OperationCanceledException && !IsRevisionConflict(e))
            {
                var (actual, actualRevision) = Observe(definition);
                record = ReconcileApply(record, choice, actual, actualRevision, ErrorMapper.Classify(e)?.Code ?? CodeReconciledUnchanged);
            }
            if (record.State != OperationState.Applied) return Outcome(Save(record));
            // The registry write is verified. Make it take effect: intent (PendingRestart/refresh_pending) is committed before any shell action.
            return Outcome(FinishWrite(record, definition, options, revert: false, cancellationToken));
        }
        catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict)
        {
            return Outcome(store.GetSetting(record.Id)); // a concurrent recovery already ruled on this row
        }
    }

    // ---- revert ----------------------------------------------------------------------------------

    private SettingOutcome RevertCore(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = store.GetSetting(id);
        var definition = Trust(record) ?? throw new OperationException(ErrorCode.UntrustedDefinition);
        if (record.State == OperationState.Reverted) return Outcome(record); // idempotent
        if (record.State is not (OperationState.Applied or OperationState.PendingRestart) || record.CompensationPrepared || definition.Recovery == RecoveryKind.Irreversible)
            throw new OperationException(ErrorCode.NotReversible);
        if (record.AppliedRevision is null || record.After is null) throw new OperationException(ErrorCode.RevisionUnknown);

        var (actual, _) = Observe(definition);
        if (!SettingSnapshots.SameSnapshot(actual, record.After)) return Conflict(record, actual);

        cancellationToken.ThrowIfCancellationRequested(); // last safe boundary before the compensation intent is persisted
        var restore = SettingSnapshots.ToSpec(definition, record.Before);
        var (pid, ticks) = liveness.Current;
        record = Save(record with { CompensationPrepared = true, Code = CodeRollbackPrepared, OwnerProcessId = pid, OwnerProcessStartTicks = ticks });
        try
        {
            var swapped = registry.CompareAndSet(definition, record.After!, restore);
            var after = registry.Read(definition);
            record = !swapped
                ? record with { State = OperationState.Partial, After = after, CompensationPrepared = false, Code = ErrorCode.ExternalConflict }
                : SettingSnapshots.Matches(after, restore)
                    ? record with { State = OperationState.Reverted, After = after, CompensationPrepared = false, Code = CodeReverted }
                    : record with { State = OperationState.Partial, After = after, CompensationPrepared = false, Code = ErrorCode.RollbackFailed };
        }
        catch (Exception e) when (e is not OperationCanceledException && !IsRevisionConflict(e))
        {
            var (now, _) = Observe(definition);
            record = ReconcileCompensation(definition, record, now);
        }
        try
        {
            return Outcome(record.State == OperationState.Reverted ? FinishWrite(record, definition, SettingApplyOptions.Default, true, cancellationToken) : Save(record));
        }
        catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict) { return Outcome(store.GetSetting(id)); }
    }

    // The registry no longer matches what this operation produced: a newer Winora operation owns it (refuse, history stays) or something
    // external changed it (classify Partial, never overwrite).
    private SettingOutcome Conflict(SettingOperationRecord record, RegistrySnapshot actual)
    {
        var all = store.ListSettingOperations();
        var index = all.ToList().FindIndex(o => o.Id == record.Id);
        if (index >= 0 && all.Skip(index + 1).Any(o => o.SettingId == record.SettingId && o.State is (OperationState.Applied or OperationState.PendingRestart or OperationState.Reverted)))
            throw new OperationException(ErrorCode.Superseded);
        return Outcome(Save(record with { State = OperationState.Partial, After = actual, Code = ErrorCode.ExternalConflict }));
    }

    // ---- recovery --------------------------------------------------------------------------------

    private IReadOnlyList<SettingOutcome> RecoverCore(CancellationToken cancellationToken)
    {
        var recovered = new List<SettingOutcome>();
        foreach (var entry in store.ListRecoverableSettingEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry.Record is { } live && liveness.IsAlive(live.OwnerProcessId, live.OwnerProcessStartTicks)) continue; // its owner is still working
                SettingDefinition? definition = null;
                var fault = entry.Fault ?? (entry.Record is null ? ErrorCode.JournalInvalid : (definition = Trust(entry.Record)) is null ? ErrorCode.UntrustedDefinition : null);
                if (fault is not null || definition is null) { store.QuarantineSetting(entry.Id, entry.Revision, fault ?? ErrorCode.UntrustedDefinition); continue; }
                var record = entry.Record!;
                var choice = definition.Choices.First(c => c.Id == record.ChoiceId);
                var (actual, revision) = Observe(definition); // reconcile from the ACTUAL state, never from journal targets
                var result = record.CompensationPrepared ? ReconcileCompensation(definition, record, actual) : ReconcileApply(record, choice, actual, revision, CodeReconciledUnchanged);
                recovered.Add(Outcome(result.State == OperationState.Applied ? FinishWrite(result, definition, RecoveryOptions, false, cancellationToken)
                    : record.CompensationPrepared && result.State == OperationState.Reverted ? FinishWrite(result, definition, RecoveryOptions, true, cancellationToken)
                    : Save(result)));
            }
            catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict) { /* another process resolved it first */ }
        }
        // Rows interrupted mid-refresh: the registry write is already verified and journaled, so only the idempotent refresh is re-run.
        foreach (var row in store.ListSettingOperations().Where(o => o.Code == CodeRefreshPending && o.State is (OperationState.PendingRestart or OperationState.Reverted) && !o.CompensationPrepared))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (liveness.IsAlive(row.OwnerProcessId, row.OwnerProcessStartTicks) || Trust(row) is not { } definition) continue;
                recovered.Add(Outcome(RunRefresh(row, definition, RecoveryOptions, cancellationToken)));
            }
            catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict) { }
        }
        return recovered;
    }

    private SettingOperationRecord ReconcileApply(SettingOperationRecord record, SettingChoice choice, RegistrySnapshot actual, long? revision, string unchangedCode)
    {
        if (SettingSnapshots.Matches(actual, choice.Value))
            return record.State == OperationState.Prepared // the mutation never started: a matching value was written by someone else
                ? record with { State = OperationState.Partial, After = actual, Code = ErrorCode.ReconciledPartial }
                : record with { State = OperationState.Applied, After = actual, AppliedRevision = revision, Code = CodeReconciledApplied };
        if (SettingSnapshots.SameSnapshot(actual, record.Before))
            return record with { State = OperationState.Failed, After = actual, Code = unchangedCode };
        return record with { State = OperationState.Partial, After = actual, Code = ErrorCode.ReconciledPartial };
    }

    private static SettingOperationRecord ReconcileCompensation(SettingDefinition definition, SettingOperationRecord record, RegistrySnapshot actual)
    {
        if (SettingSnapshots.SameSnapshot(actual, record.After))
            return record with { CompensationPrepared = false, Code = CodeRollbackAbandoned };
        if (SettingSnapshots.Matches(actual, SettingSnapshots.ToSpec(definition, record.Before)))
            return record with { State = OperationState.Reverted, CompensationPrepared = false, After = actual, Code = CodeReverted };
        return record with { State = OperationState.Partial, CompensationPrepared = false, After = actual, Code = ErrorCode.RollbackUncertain };
    }

    // ---- shell refresh ---------------------------------------------------------------------------

    private SettingOutcome CompleteCore(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = store.GetSetting(id);
        var definition = Trust(record) ?? throw new OperationException(ErrorCode.UntrustedDefinition);
        var open = record.State is (OperationState.PendingRestart or OperationState.Reverted) && !record.CompensationPrepared &&
            record.Code != CodeReverted && record.Code != CodeShellRefreshed;
        if (record.State == OperationState.Applied || record.State is OperationState.Reverted && !open) return Outcome(record); // nothing left to do
        if (!open) throw new OperationException(ErrorCode.InvalidTransition);
        if (definition.RequiresSignOut) return Outcome(record); // takes effect at the next sign-in; that cannot be verified from here
        var (pid, ticks) = liveness.Current;
        try
        {
            record = Save(record with { Code = CodeRefreshPending, OwnerProcessId = pid, OwnerProcessStartTicks = ticks });
            return Outcome(RunRefresh(record, definition, SettingApplyOptions.Default, cancellationToken));
        }
        catch (OperationException e) when (e.Code == ErrorCode.RevisionConflict) { return Outcome(store.GetSetting(id)); }
    }

    // Called with a record whose registry change is verified (in-memory State Applied for an apply, Reverted for a revert).
    private SettingOperationRecord FinishWrite(SettingOperationRecord record, SettingDefinition definition, SettingApplyOptions options, bool revert, CancellationToken cancellationToken)
    {
        var step = options.RefreshShell || definition.RequiresExplorerRestart && options.RestartExplorer;
        var pending = definition.RequiresExplorerRestart || definition.RequiresSignOut;
        if (!step)
            return Save(pending ? record with { State = revert ? OperationState.Reverted : OperationState.PendingRestart, Code = definition.RequiresExplorerRestart ? CodeRestartPending : CodeSignOutPending } : record);
        var (pid, ticks) = liveness.Current;
        record = Save(record with { State = revert ? OperationState.Reverted : OperationState.PendingRestart, Code = CodeRefreshPending, OwnerProcessId = pid, OwnerProcessStartTicks = ticks }); // intent first
        return RunRefresh(record, definition, options, cancellationToken);
    }

    private SettingOperationRecord RunRefresh(SettingOperationRecord record, SettingDefinition definition, SettingApplyOptions options, CancellationToken cancellationToken)
    {
        var revert = record.State == OperationState.Reverted;
        string? failure = null;
        if (options.RefreshShell)
        {
            // Fixed actions that apply values the registry already holds come first; the general broadcast comes last.
            foreach (var kind in RefreshKinds(definition))
                if (!Invoke(kind, cancellationToken, out var extraCancelled)) { failure = extraCancelled ? ErrorCode.Cancelled : ErrorCode.ShellRefreshFailed; break; }
            if (failure is null && !Invoke(ShellRefreshKind.BroadcastSettingChange, cancellationToken, out var cancelled)) failure = cancelled ? ErrorCode.Cancelled : ErrorCode.ShellRefreshFailed;
        }
        var restarted = false;
        if (definition.RequiresExplorerRestart && options.RestartExplorer)
        {
            if (Invoke(ShellRefreshKind.RestartExplorer, cancellationToken, out var restartCancelled)) restarted = true;
            else failure = restartCancelled ? ErrorCode.Cancelled : ErrorCode.ShellRestartFailed;
        }
        var restartPending = definition.RequiresExplorerRestart && !restarted;
        var (state, code) =
            failure is not null ? (revert ? OperationState.Reverted : OperationState.PendingRestart, failure) :
            restartPending ? (revert ? OperationState.Reverted : OperationState.PendingRestart, CodeRestartPending) :
            definition.RequiresSignOut ? (revert ? OperationState.Reverted : OperationState.PendingRestart, CodeSignOutPending) :
            (revert ? OperationState.Reverted : OperationState.Applied, revert ? CodeReverted : CodeShellRefreshed);
        return Save(record with { State = state, Code = code });
    }

    private static IEnumerable<ShellRefreshKind> RefreshKinds(SettingDefinition definition)
    {
        if (definition.Refresh.HasFlag(SettingRefresh.Mouse)) yield return ShellRefreshKind.ApplyMouse;
        if (definition.Refresh.HasFlag(SettingRefresh.KeyboardRepeat)) yield return ShellRefreshKind.ApplyKeyboardRepeat;
        if (definition.Refresh.HasFlag(SettingRefresh.AccessibilityKeys)) yield return ShellRefreshKind.ApplyAccessibilityKeys;
        if (definition.Refresh.HasFlag(SettingRefresh.Intl)) yield return ShellRefreshKind.BroadcastIntl;
        if (definition.Refresh.HasFlag(SettingRefresh.Environment)) yield return ShellRefreshKind.BroadcastEnvironment;
    }

    private bool Invoke(ShellRefreshKind kind, CancellationToken cancellationToken, out bool cancelled)
    {
        cancelled = false;
        if (shell is null) return false;
        try { return shell.RefreshAsync(kind, cancellationToken).GetAwaiter().GetResult() is { Succeeded: true }; }
        catch (OperationCanceledException) { cancelled = true; return false; }
        catch (Exception) { return false; } // a throwing shell action is a failed refresh, never a thrown outcome after a verified write
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private SettingDefinition Resolve(string? id) =>
        (string.IsNullOrEmpty(id) ? null : catalog.Find(id)) ?? throw new OperationException(ErrorCode.SettingUnknown);

    private static SettingChoice ResolveChoice(SettingDefinition definition, string? choiceId) =>
        definition.Choices.FirstOrDefault(c => c.Id == choiceId) ?? throw new OperationException(ErrorCode.SettingChoiceInvalid);

    private SettingCompatibility Compatibility(SettingDefinition definition)
    {
        if (definition.MinBuild is null && definition.MaxBuild is null) return SettingCompatibility.Supported;
        if (osBuild?.CurrentBuild is not int build) return SettingCompatibility.Unknown;
        return build >= (definition.MinBuild ?? int.MinValue) && build <= (definition.MaxBuild ?? int.MaxValue)
            ? SettingCompatibility.Supported : SettingCompatibility.UnsupportedBuild;
    }

    private void EnsureBuild(SettingDefinition definition)
    {
        if (Compatibility(definition) != SettingCompatibility.Supported) throw new OperationException(ErrorCode.SettingUnsupportedBuild); // unknown build fails closed
    }

    // A value Winora could not restore faithfully must not be overwritten: its kind has to be one the definition manages.
    private static void EnsureRecognized(SettingDefinition definition, RegistrySnapshot observed)
    {
        if (!SettingSnapshots.IsLegalFor(definition, observed)) throw new OperationException(ErrorCode.SettingStateUnrecognized);
    }

    private static void EnsureApplicable(SettingChoice choice, RegistrySnapshot observed)
    {
        if (!SettingSnapshots.IsApplicable(choice, observed)) throw new OperationException(ErrorCode.SettingStateUnrecognized);
    }

    // The snapshot is the witness of the state: it must read the same twice (a write in between is retried, then reported as a moved resource).
    // The key last-write time is returned for the journal and diagnostics only. Decisions never compare it, because other writers
    // (Explorer at start, the Settings app) touch shared keys without changing the value.
    private (RegistrySnapshot Snapshot, long? Revision) Observe(SettingDefinition definition)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var revision = registry.ReadRevision(definition);
            var snapshot = registry.Read(definition);
            if (SettingSnapshots.SameSnapshot(snapshot, registry.Read(definition))) return (snapshot, revision);
        }
        throw new OperationException(ErrorCode.ResourceChanged);
    }

    private static bool IsRevisionConflict(Exception e) => e is OperationException { Code: ErrorCode.RevisionConflict };

    private SettingOperationRecord New(SettingDefinition definition, SettingChoice choice, RegistrySnapshot observed, long? revision, OperationState state, string code, SettingApplyOptions options)
    {
        var (pid, ticks) = liveness.Current;
        var now = clock.GetUtcNow();
        return new(Guid.NewGuid(), definition.Id, definition.Version, SettingSnapshots.TargetFingerprint(definition), choice.Id, state, false, code,
            observed, null, revision, null, definition.RequiresExplorerRestart, definition.RequiresSignOut, pid, ticks,
            SettingOperationRecord.CurrentSchemaVersion, now, now, options.Group, options.Label);
    }

    private SettingOperationRecord Save(SettingOperationRecord record) => store.SaveSetting(record with { UpdatedAtUtc = clock.GetUtcNow() });

    /// <summary>
    /// The definition a stored row may act on, or null. Fails closed on an unknown setting, a changed definition version, a changed
    /// target, an unknown choice, or snapshots whose kind/view the definition cannot produce.
    /// </summary>
    private SettingDefinition? Trust(SettingOperationRecord record)
    {
        if (record.SchemaVersion != SettingOperationRecord.CurrentSchemaVersion || !Enum.IsDefined(record.State)) return null;
        var definition = catalog.Find(record.SettingId);
        if (definition is null || definition.Id != record.SettingId || definition.Version != record.DefinitionVersion) return null;
        if (!string.Equals(record.TargetFingerprint, SettingSnapshots.TargetFingerprint(definition), StringComparison.Ordinal)) return null;
        if (definition.Choices.All(c => c.Id != record.ChoiceId)) return null;
        if (!SettingSnapshots.IsLegalFor(definition, record.Before)) return null;
        if (record.After is not null && record.State is (OperationState.Applied or OperationState.PendingRestart) && !SettingSnapshots.IsLegalFor(definition, record.After)) return null;
        return definition;
    }

    private SettingOutcome Outcome(SettingOperationRecord record)
    {
        var definition = catalog.Find(record.SettingId);
        // Remaining needs only: a verified refresh leaves nothing pending.
        var pending = record.State == OperationState.PendingRestart || record.State == OperationState.Reverted && record.Code != CodeReverted && record.Code != CodeShellRefreshed;
        var explorer = pending && record.NeedsExplorerRestart && (record.Code == CodeRestartPending || record.Code == CodeRefreshPending || record.Code == ErrorCode.ShellRestartFailed);
        var signOut = pending && record.NeedsSignOut;
        return new(record.Id, record.SettingId, record.ChoiceId, record.State, record.Code,
            definition is null ? null : SettingSnapshots.MatchChoice(definition, record.Before),
            definition is null ? null : SettingSnapshots.MatchChoice(definition, record.After),
            explorer, signOut, record.UpdatedAtUtc, record.Group, record.Label);
    }
}
