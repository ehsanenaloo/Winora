// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;
namespace Winora.Application;

/// <summary>
/// Changes to machine settings of the verified catalog with a preview, a journal line before the write, a compare-and-set against what the preview saw, a read-back after it and a
/// conditional undo. A setting that a policy manages (Group Policy or device management), that does not apply to this Windows build or whose state is not one the definition knows is
/// never changed. The catalog is the definition set: nothing outside it can be written (decision DEC-IW-023).
/// </summary>
public sealed class OptimizeService(Func<DefinitionSetResult> definitions, IMachineSettingsBackend backend, IMachinePolicyReader policy, IOsBuildProvider build, IOptimizeJournal journal, TimeProvider? clock = null,
    IDisplayAdapterSource? adapters = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);

    public IReadOnlyList<OptimizeRecord> History(int max) => journal.History(max);

    private VerifiedDefinitionSet? Set => definitions().Set;

    /// <summary>Every definition of the verified catalog (no reads of the machine). Empty when the catalog could not be loaded.</summary>
    public IReadOnlyList<MachineSettingDefinition> Definitions() => Set?.Definitions ?? [];

    /// <summary>The definition with this id in the catalog, or null.</summary>
    public MachineSettingDefinition? Find(string id) => Set?.Find(id);

    // ---- the catalog as it stands on this PC ----

    public IReadOnlyList<OptimizeItem> List()
    {
        var set = Set;
        if (set is null) return [];
        var history = History(int.MaxValue);
        return set.Definitions.SelectMany(d => d.TargetKind == MachineTargetKind.DisplayDriverValue ? DescribeDisplay(d, history) : [Describe(d, history)]).ToList();
    }

    /// <summary>A display driver setting is one item for each present adapter of its vendor; with no such adapter it is not offered at all.</summary>
    private IEnumerable<OptimizeItem> DescribeDisplay(MachineSettingDefinition d, IReadOnlyList<OptimizeRecord> history)
    {
        if (!BuildSupported(d)) return [new(d, null, null, OptimizeCode.UnsupportedBuild)];
        if (!EditionSupported(d)) return [new(d, null, null, OptimizeCode.UnsupportedEdition)];
        if (d.DisplayDriver is not { } target || adapters is null) return [];
        IReadOnlyList<DisplayAdapter> found;
        try { found = adapters.Adapters(target.VendorId); }
        catch (DisplayEnumerationException) { return [new(d, null, null, OptimizeCode.Unreadable)]; }
        return found.Select(a => Describe(d, history, a)).ToList();
    }

    private DisplayAdapter? FindAdapter(MachineSettingDefinition d, string instanceId)
    {
        if (d.DisplayDriver is not { } target || adapters is null) return null;
        try { return adapters.Adapters(target.VendorId).FirstOrDefault(a => string.Equals(a.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)); }
        catch (DisplayEnumerationException) { return null; }
    }

    /// <summary>The catalog writes the very policy value that Group Policy would manage, so a value that Winora itself set last is its own, not a policy's.</summary>
    private static bool IsOwnWrite(MachineSettingDefinition d, MachineValue current, IReadOnlyList<OptimizeRecord> history)
    {
        // Only when every policy channel is the target itself. A policy at another place, or one that cannot be read, always blocks.
        if (!d.ManagedOnlyAtTarget) return false;
        var last = history.FirstOrDefault(r => r.Change.DefinitionId == d.Id && r.State == OptimizeState.Applied);
        return last?.Change.After is { } after && after.Matches(current);
    }

    /// <summary>The named settings of the catalog as they stand on this PC. A name that is not in the catalog is left out.</summary>
    public IReadOnlyList<OptimizeItem> List(IReadOnlyCollection<string> definitionIds)
    {
        ArgumentNullException.ThrowIfNull(definitionIds);
        var set = Set;
        if (set is null) return [];
        var history = History(int.MaxValue);
        return set.Definitions.Where(d => definitionIds.Contains(d.Id)).Select(d => Describe(d, history)).ToList();
    }

    private OptimizeItem Describe(MachineSettingDefinition d, IReadOnlyList<OptimizeRecord> history, DisplayAdapter? adapter = null)
    {
        if (!BuildSupported(d)) return new(d, null, null, OptimizeCode.UnsupportedBuild, adapter);
        if (!EditionSupported(d)) return new(d, null, null, OptimizeCode.UnsupportedEdition, adapter);
        var observed = backend.Observe(d, adapter);
        if (observed.Observation is not { } observation || !d.TryReadComparable(observation.Snapshot, out var current)) return new(d, null, null, observed.Code ?? OptimizeCode.Unreadable, adapter);
        var choice = d.Choices.FirstOrDefault(c => c.EffectiveValue is { } v && v.Equals(current))?.Id;
        if (!d.EffectiveRestoreDomain.Contains(current)) return new(d, current, null, OptimizeCode.StateUnrecognized, adapter);
        if (policy.IsManagedByPolicy(d) && !IsOwnWrite(d, current, history)) return new(d, current, choice, OptimizeCode.ManagedByPolicy, adapter);
        return new(d, current, choice, null, adapter);
    }

    private bool BuildSupported(MachineSettingDefinition d) => build.CurrentBuild is { } b && b >= d.MinBuild && (d.MaxBuild is not { } max || b <= max);

    /// <summary>A definition with an edition list applies only to those editions; an unknown edition fails closed.</summary>
    private bool EditionSupported(MachineSettingDefinition d) => d.Editions.Count == 0 || WindowsEditions.Classify(build.EditionId) is { } edition && d.Editions.Contains(edition);

    // ---- preview ----

    public OptimizePreview Preview(OptimizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var set = Set;
        if (set is null) return Refused(request, null, OptimizeCode.NoCatalog);
        if (set.Find(request.DefinitionId) is not { } d) return Refused(request, null, OptimizeCode.UnknownDefinition);
        if (d.Choices.FirstOrDefault(c => c.Id == request.ChoiceId) is not { EffectiveValue: { } desired }) return Refused(request, d, OptimizeCode.UnknownChoice);
        if (set.RevocationOf(d.Id, d.Version) is not null) return Refused(request, d, OptimizeCode.NotReversible);
        DisplayAdapter? adapter = null;
        if (d.TargetKind == MachineTargetKind.DisplayDriverValue)
        {
            if (request.AdapterInstanceId is null || FindAdapter(d, request.AdapterInstanceId) is not { } found) return Refused(request, d, OptimizeCode.NoAdapter);
            adapter = found;
        }
        var item = Describe(d, History(int.MaxValue), adapter);
        if (item.Problem is { } problem) return Refused(request, d, problem);
        var current = item.Current!;
        var before = StoredValue.From(current);
        return new(request, d, before, StoredValue.From(desired), !current.Equals(desired), null, adapter);
    }

    private static OptimizePreview Refused(OptimizeRequest request, MachineSettingDefinition? d, string code) => new(request, d, null, null, false, code);

    // ---- apply ----

    public Task<OptimizeApplyResult> ApplyAsync(OptimizeRequest request, CancellationToken cancellationToken = default) =>
        RunAsync<OptimizeApplyResult>(() => ApplyCore(request, null), cancellationToken);

    // ---- groups: several settings changed together under one label, with one undo ----

    /// <summary>The preview of each request of a group, in order. A request that would change nothing is included with WillChange false.</summary>
    public IReadOnlyList<OptimizePreview> PreviewGroup(IReadOnlyList<OptimizeRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return requests.Select(Preview).ToList();
    }

    /// <summary>
    /// Applies the requests in order as one group. Nothing is written when any request is refused (managed by policy, unsupported, unknown state): the result then holds that one refusal.
    /// After that the first change that fails stops the group; what was applied stays in the journal and <see cref="RevertGroupAsync"/> undoes it.
    /// </summary>
    public Task<IReadOnlyList<OptimizeApplyResult>> ApplyGroupAsync(IReadOnlyList<OptimizeRequest> requests, string label, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<OptimizeApplyResult>>(() =>
        {
            if (!backend.CanWrite) return [new(OptimizeState.Refused, OptimizeCode.NeedsAdministrator, null)];
            var previews = PreviewGroup(requests);
            if (previews.FirstOrDefault(p => p.IsRefused) is { } refused) return [new(OptimizeState.Refused, refused.RefusalCode!, null)];
            if (!previews.Any(p => p.WillChange)) return [new(OptimizeState.Refused, OptimizeCode.AlreadySet, null)];
            var group = Guid.NewGuid();
            var results = new List<OptimizeApplyResult>();
            foreach (var request in requests)
            {
                var result = ApplyCore(request, null, group, label);
                if (result.Code == OptimizeCode.AlreadySet) continue;
                results.Add(result);
                if (!result.Ok) break;
            }
            return results;
        }, cancellationToken);

    /// <summary>True when at least one change of the group can still be undone.</summary>
    public bool CanRevertGroup(Guid group)
    {
        var history = History(int.MaxValue);
        return history.Where(r => r.Change.Group == group && r.Change.RevertOf is null).Any(r => RevertPlan(r, history) is not null);
    }

    /// <summary>Undoes the changes of a group, newest first. A change whose value someone else changed since is left alone and counted in Skipped.</summary>
    public Task<(IReadOnlyList<OptimizeApplyResult> Results, int Skipped)> RevertGroupAsync(Guid group, CancellationToken cancellationToken = default) =>
        RunAsync<(IReadOnlyList<OptimizeApplyResult>, int)>(() =>
        {
            if (!backend.CanWrite) return ([new(OptimizeState.Refused, OptimizeCode.NeedsAdministrator, null)], 0);
            var members = History(int.MaxValue).Where(r => r.Change.Group == group && r.Change.RevertOf is null && r.State == OptimizeState.Applied).ToList();
            var results = new List<OptimizeApplyResult>();
            var skipped = 0;
            var undoGroup = Guid.NewGuid();
            foreach (var member in members)
            {
                var result = RevertOne(member, History(int.MaxValue), undoGroup, member.Change.Label);
                if (result is null) { skipped++; continue; }
                results.Add(result);
                if (!result.Ok) skipped++;
            }
            return (results, skipped);
        }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private OptimizeApplyResult ApplyCore(OptimizeRequest request, Guid? revertOf, Guid? group = null, string? label = null)
    {
        if (!backend.CanWrite) return new(OptimizeState.Refused, OptimizeCode.NeedsAdministrator, null);
        var preview = Preview(request);
        if (preview.IsRefused) return new(OptimizeState.Refused, preview.RefusalCode!, null);
        if (!preview.WillChange) return new(OptimizeState.Refused, OptimizeCode.AlreadySet, null);
        var d = preview.Definition!;
        var observed = backend.Observe(d, preview.Adapter);
        if (observed.Observation is not { } observation) return new(OptimizeState.Refused, observed.Code ?? OptimizeCode.Unreadable, null);
        // The value written to the journal is the one just observed, the same observation the compare-and-set uses.
        if (!d.TryReadComparable(observation.Snapshot, out var live)) return new(OptimizeState.Refused, OptimizeCode.Unreadable, null);
        var before = StoredValue.From(live);
        if (request.ExpectedBefore is { } expected && expected != before) return new(OptimizeState.Refused, OptimizeCode.Stale, null);
        if (before.Matches(preview.After!.ToValue() ?? live) && before == preview.After) return new(OptimizeState.Refused, OptimizeCode.AlreadySet, null);
        var adapter = preview.Adapter;
        var change = new OptimizeChange(Guid.NewGuid(), d.Id, d.Version, OptimizeChangeKind.Apply, request.ChoiceId, before, preview.After!, revertOf, adapter?.InstanceId, adapter?.FriendlyName, adapter?.KeyPath, group ?? request.Group, label ?? request.Label);
        return Run(change, d, () => backend.Apply(d, request.ChoiceId, observation, adapter), preview.After!);
    }

    private OptimizeApplyResult Run(OptimizeChange change, MachineSettingDefinition d, Func<MachineWriteResult> write, StoredValue expectedAfter)
    {
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(OptimizeState.Refused, OptimizeCode.JournalFailed, null); }

        MachineWriteResult result;
        try { result = write(); }
        catch (Exception e) when (e is not OperationCanceledException) { result = new(false, OptimizeCode.Failed, null, 0); } // the entry is begun, so it must be completed
        // The proof that the write was made. When this line is lost the record stays unconfirmed.
        if (result.Ok)
        {
            try { journal.Written(change.Id, clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the end line follows */ }
        }
        OptimizeState state; string code;
        if (!result.Ok) { state = OptimizeState.Failed; code = result.Code; }
        else if (result.After is { } after && d.TryReadComparable(after, out var written) && expectedAfter.Matches(written)) { state = OptimizeState.Applied; code = OptimizeCode.Ok; }
        else { state = OptimizeState.Failed; code = OptimizeCode.VerifyFailed; }
        var lost = false;
        try { journal.Complete(change.Id, state, code, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The change is made; the history shows it as interrupted. One more try, then the caller is told.
            try { journal.Complete(change.Id, state, code, clock.GetUtcNow()); }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { lost = true; }
        }
        return new(state, code, change, lost);
    }

    // ---- interrupted changes ----

    /// <summary>
    /// Closes the records of changes that were begun and never finished. Two cases are proven. The journal shows the write as made and the live value is the intended one: Applied (code
    /// reconciled), so the conditional undo works. The live value is still the one from before: Failed (interrupted_unchanged). Anything else is unconfirmed and stays open: a value that
    /// equals the intended one without the written line does not show who set it. The person resolves those records (<see cref="DismissAsync"/>, <see cref="RestoreBeforeAsync"/>).
    /// </summary>
    public Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken = default) =>
        RunAsync<int>(() =>
        {
            var done = 0;
            foreach (var record in History(int.MaxValue).Where(IsUnconfirmed))
            {
                if (record.Change.Before is null || record.Change.After is null) continue;
                if (ReadLive(record) is not { } live) continue;
                var wrote = record.Change.After.Matches(live.Now); var old = record.Change.Before.Matches(live.Now);
                OptimizeState state; string code;
                if (record.Written && wrote && !old) (state, code) = (OptimizeState.Applied, OptimizeCode.Reconciled);
                else if (old && !wrote) (state, code) = (OptimizeState.Failed, OptimizeCode.InterruptedUnchanged);
                else continue;
                try { journal.Complete(record.Change.Id, state, code, clock.GetUtcNow()); done++; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays open */ }
            }
            return done;
        }, cancellationToken);

    /// <summary>A record with a begin line and no end line.</summary>
    public static bool IsUnconfirmed(OptimizeRecord record) => record.State is null && record.EndedUtc is null;

    /// <summary>Reads the live value of the setting of a record. False when the record or the setting cannot be read.</summary>
    private sealed record Live(MachineSettingDefinition Definition, MachineObservation Observation, DisplayAdapter? Adapter, MachineValue Now);

    private Live? ReadLive(OptimizeRecord record)
    {
        if (Set?.Find(record.Change.DefinitionId) is not { } d) return null;
        DisplayAdapter? adapter = null;
        if (d.TargetKind == MachineTargetKind.DisplayDriverValue)
        {
            if (record.Change.TargetInstanceId is not { } instance || record.Change.TargetKey is not { } key || FindAdapter(d, instance) is not { } found
                || !string.Equals(found.KeyPath, key, StringComparison.OrdinalIgnoreCase)) return null;
            adapter = found;
        }
        var result = backend.Observe(d, adapter);
        if (result.Observation is not { } observation || !d.TryReadComparable(observation.Snapshot, out var value)) return null;
        return new(d, observation, adapter, value);
    }

    /// <summary>
    /// The checks every restore of the value from before passes, in the offer and again at the write. A restore is an ordinary change, so the setting must be one Winora may change now: the
    /// recorded definition version is the current one or a declared compatible one, the definition is not withdrawn, the build and edition are supported, no policy manages the setting, the live
    /// value is one the definition knows, and the old value is one Winora may restore. Null when the restore may go ahead; otherwise the code that says why not.
    /// </summary>
    private string? RestoreRefusal(OptimizeRecord record, out Live? live)
    {
        live = null;
        if (record.Change.Before is not { } stored || stored.ToValue() is not { } before) return OptimizeCode.CannotRevert;
        if (Set is not { } set) return OptimizeCode.NoCatalog;
        if (set.Find(record.Change.DefinitionId) is not { } d) return OptimizeCode.UnknownDefinition;
        if (d.Version != record.Change.Version && !d.RestoreCompatible.Contains(record.Change.Version)) return OptimizeCode.CannotRevert;
        if (set.RevocationOf(d.Id, record.Change.Version) is { AllowRestore: false }) return OptimizeCode.NotReversible;
        if (ReadLive(record) is not { } read) return OptimizeCode.Unreadable;
        if (Describe(d, History(int.MaxValue), read.Adapter).Problem is { } problem) return problem;
        if (!d.EffectiveRestoreDomain.Contains(before)) return OptimizeCode.CannotRevert;
        if (stored.Matches(read.Now)) return OptimizeCode.AlreadySet;
        live = read;
        return null;
    }

    /// <summary>Whether the value from before can be put back for an unconfirmed record (see <see cref="RestoreRefusal"/>).</summary>
    public bool CanRestoreBefore(OptimizeRecord record) => backend.CanWrite && IsUnconfirmed(record) && RestoreRefusal(record, out _) is null;

    private static string ValueText(MachineSettingDefinition d, MachineValue value) => d.Choices.FirstOrDefault(c => c.EffectiveValue is { } e && e.Equals(value))?.Id ?? value.ToString() ?? "";

    private static RecoveryPlan PlanOf(OptimizeRecord record, Live live)
    {
        var d = live.Definition;
        var current = StoredValue.From(live.Now);
        var token = RecoveryToken.Of("optimize", record.Change.Id, d.Id, d.Version, live.Adapter?.InstanceId, current, record.Change.Before);
        return new([new(d.Id, ValueText(d, live.Now), ValueText(d, record.Change.Before!.ToValue()!))], token, d.RequiresRestart);
    }

    /// <summary>
    /// What a restore of the value from before would do now: the setting, what it holds and what would be put there, with a token for that state. Null when the restore is not possible (see
    /// <see cref="RestoreRefusal"/>). The restore is made only with this token, and only while the live value still gives it.
    /// </summary>
    public RecoveryPlan? PlanRestoreBefore(OptimizeRecord record) =>
        backend.CanWrite && IsUnconfirmed(record) && RestoreRefusal(record, out var live) is null && live is not null ? PlanOf(record, live) : null;

    /// <summary>The person keeps the current value and closes an unconfirmed record. Nothing is written and the record gets no undo (Dismissed). Refused when the record is not unconfirmed.</summary>
    public Task<OptimizeApplyResult> DismissAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync<OptimizeApplyResult>(() =>
        {
            var record = History(int.MaxValue).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return new(OptimizeState.Refused, OptimizeCode.NotUnconfirmed, null);
            try { journal.Complete(changeId, OptimizeState.Dismissed, OptimizeCode.Dismissed, clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(OptimizeState.Refused, OptimizeCode.JournalFailed, null); }
            return new(OptimizeState.Dismissed, OptimizeCode.Dismissed, record.Change);
        }, cancellationToken);

    /// <summary>
    /// The person puts the value from before back and closes the unconfirmed record. It is an ordinary change: it needs administrator rights, is recorded as a restore of its own with its own
    /// undo, and writes only while the live value is the one that was just read. The unconfirmed record is then Dismissed with the code resolved_restored.
    /// </summary>
    public Task<OptimizeApplyResult> RestoreBeforeAsync(Guid changeId, CancellationToken cancellationToken = default, string? expectedToken = null) =>
        RunAsync<OptimizeApplyResult>(() =>
        {
            if (!backend.CanWrite) return new(OptimizeState.Refused, OptimizeCode.NeedsAdministrator, null);
            var record = History(int.MaxValue).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record) || record.Change.Before is null) return new(OptimizeState.Refused, OptimizeCode.NotUnconfirmed, null);
            var refusal = RestoreRefusal(record, out var read);
            if (refusal is not null || read is not { } live) return new(OptimizeState.Refused, refusal ?? OptimizeCode.Unreadable, null);
            // What was reviewed is what is written: a value that changed since the review needs a new review.
            if (expectedToken is not null && PlanOf(record, live).Token != expectedToken) return new(OptimizeState.Refused, OptimizeCode.Stale, null);
            var d = live.Definition; var now = live.Now;
            var before = record.Change.Before.ToValue()!;
            var adapter = live.Adapter;
            var change = new OptimizeChange(Guid.NewGuid(), d.Id, d.Version, OptimizeChangeKind.Restore, null, StoredValue.From(now), record.Change.Before, null, adapter?.InstanceId, adapter?.FriendlyName, adapter?.KeyPath, null, record.Change.Label);
            var result = Run(change, d, () => backend.Restore(d, before, live.Observation, adapter), record.Change.Before);
            if (result.Ok)
            {
                try { journal.Complete(changeId, OptimizeState.Dismissed, OptimizeCode.ResolvedRestored, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result with { RecordLost = true }; }
            }
            return result;
        }, cancellationToken);

    // ---- revert ----

    public bool CanRevert(OptimizeRecord record) => RevertPlan(record, History(int.MaxValue)) is not null;

    /// <summary>The same answer for many records with one read of the journal.</summary>
    public IReadOnlyList<bool> CanRevertAll(IReadOnlyList<OptimizeRecord> records)
    {
        var history = History(int.MaxValue);
        return records.Select(r => RevertPlan(r, history) is not null).ToList();
    }

    public Task<OptimizeApplyResult> RevertAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync<OptimizeApplyResult>(() =>
        {
            if (!backend.CanWrite) return new(OptimizeState.Refused, OptimizeCode.NeedsAdministrator, null);
            var history = History(int.MaxValue);
            var record = history.FirstOrDefault(r => r.Change.Id == changeId);
            return record is null ? new(OptimizeState.Refused, OptimizeCode.CannotRevert, null) : RevertOne(record, history, null, record.Change.Label) ?? new(OptimizeState.Refused, OptimizeCode.CannotRevert, null);
        }, cancellationToken);

    /// <summary>Undoes one change while the live value is still what it wrote. Null when it cannot be undone.</summary>
    private OptimizeApplyResult? RevertOne(OptimizeRecord record, IReadOnlyList<OptimizeRecord> history, Guid? group, string? label)
    {
        if (RevertPlan(record, history) is not { } plan) return null;
        var observed = backend.Observe(plan.Definition, plan.Adapter);
        if (observed.Observation is not { } observation || !plan.Definition.TryReadComparable(observation.Snapshot, out var now) || !record.Change.After.Matches(now)) return null;
        var change = new OptimizeChange(Guid.NewGuid(), plan.Definition.Id, plan.Definition.Version, OptimizeChangeKind.Restore, null, record.Change.After, record.Change.Before, record.Change.Id, plan.Adapter?.InstanceId, plan.Adapter?.FriendlyName, plan.Adapter?.KeyPath, group, label);
        return Run(change, plan.Definition, () => backend.Restore(plan.Definition, plan.Value, observation, plan.Adapter), record.Change.Before);
    }

    private sealed record Plan(MachineSettingDefinition Definition, MachineValue Value, DisplayAdapter? Adapter = null);

    private Plan? RevertPlan(OptimizeRecord record, IReadOnlyList<OptimizeRecord> history)
    {
        if (record.Change.Before is null || record.Change.After is null) return null; // a damaged line
        if (!(record.State == OptimizeState.Applied || (record.State == OptimizeState.Failed && record.Code == OptimizeCode.VerifyFailed)) || record.Change.RevertOf is not null) return null;
        if (history.Any(r => r.Change.RevertOf == record.Change.Id && r.State == OptimizeState.Applied)) return null;
        var set = Set;
        if (set?.Find(record.Change.DefinitionId) is not { } d) return null;
        // The recorded version must be the current one or one the definition declares compatible.
        if (d.Version != record.Change.Version && !d.RestoreCompatible.Contains(record.Change.Version)) return null;
        if (record.Change.Before.ToValue() is not { } before || !d.EffectiveRestoreDomain.Contains(before)) return null;
        DisplayAdapter? adapter = null;
        if (d.TargetKind == MachineTargetKind.DisplayDriverValue)
        {
            // The driver key is resolved again now. The undo goes ahead only when the same device still has the same key; a reinstalled driver or a swapped card ends the undo.
            if (record.Change.TargetInstanceId is not { } instance || record.Change.TargetKey is not { } key || FindAdapter(d, instance) is not { } found
                || !string.Equals(found.KeyPath, key, StringComparison.OrdinalIgnoreCase)) return null;
            adapter = found;
        }
        else if (record.Change.TargetInstanceId is not null) return null;
        var observed = backend.Observe(d, adapter);
        if (observed.Observation is not { } observation || !d.TryReadComparable(observation.Snapshot, out var now) || !record.Change.After.Matches(now)) return null;
        return new(d, before, adapter);
    }
}
