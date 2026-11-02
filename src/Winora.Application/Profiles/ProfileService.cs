// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;
namespace Winora.Application;

/// <summary>
/// Profiles: named lists of catalog ids with one choice each. The service only plans and sequences. Every change goes through the existing services
/// (<see cref="ISettingsService"/> for per-user settings, <see cref="OptimizeService"/> for machine definitions), so each entry keeps its own journal line before the change,
/// compare-and-set, read-back and conditional undo. All entries of one run share one group id in the journals.
/// </summary>
public sealed class ProfileService : IProfileReferences
{
    private const int MaxCustomProfiles = 50;

    private readonly ISettingsService settings;
    private readonly ISettingCatalog catalog;
    private readonly OptimizeService machine;
    private readonly IElevationState elevation;
    private readonly IProfileStore store;
    private readonly IRestorePointGate? gate;
    private readonly IReadOnlyList<string> builtInJson;
    private readonly SemaphoreSlim run = new(1, 1);

    public ProfileService(ISettingsService settings, ISettingCatalog catalog, OptimizeService machine, IElevationState elevation, IProfileStore store,
        IReadOnlyList<string> builtInJson, IRestorePointGate? gate = null)
    {
        this.settings = settings; this.catalog = catalog; this.machine = machine; this.elevation = elevation; this.store = store; this.builtInJson = builtInJson; this.gate = gate;
    }

    // ---- catalogs and loading ----

    public ProfileReference? Find(ProfileEntryKind kind, string id)
    {
        if (kind == ProfileEntryKind.User)
            return catalog.Find(id) is { } u ? new(u.Risk, u.Choices.Select(c => c.Id).ToHashSet(StringComparer.Ordinal)) : null;
        return machine.Find(id) is { } m ? new(m.Risk, m.Choices.Select(c => c.Id).ToHashSet(StringComparer.Ordinal)) : null;
    }

    /// <summary>The resource key of the title of a setting a profile entry names; null when no catalog knows the id.</summary>
    public string? TitleKeyOf(ProfileEntryKind kind, string id) => kind == ProfileEntryKind.User ? catalog.Find(id)?.TitleKey : machine.Find(id)?.TitleKey;

    /// <summary>Loads the built-in files and the custom files. A file that fails the strict parser is reported and left out; the others still load.</summary>
    public ProfileCatalogResult Load()
    {
        var errors = new List<ProfileLoadError>();
        var builtIn = new List<Profile>();
        foreach (var json in builtInJson)
        {
            try
            {
                var profile = ProfileFile.ParseBuiltIn(json, this);
                if (builtIn.Any(p => p.Id == profile.Id)) throw new ProfileFormatException(ProfileCode.Invalid, "duplicate profile id");
                builtIn.Add(profile);
            }
            catch (ProfileFormatException e) { errors.Add(new("built-in", e.Code, e.Message)); }
        }
        var custom = new List<Profile>();
        IReadOnlyList<string> ids;
        try { ids = store.List(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ids = []; errors.Add(new("custom", ProfileCode.StoreFailed, e.GetType().Name)); }
        foreach (var id in ids.Take(MaxCustomProfiles))
        {
            try
            {
                if (!ProfileFile.IsCustomId(id) || store.Read(id) is not { } json) throw new ProfileFormatException(ProfileCode.Invalid, "the file cannot be read");
                custom.Add(ProfileFile.ParseCustom(json, id, this));
            }
            catch (ProfileFormatException e) { errors.Add(new(id, e.Code, e.Message)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add(new(id, ProfileCode.StoreFailed, e.GetType().Name)); }
        }
        return new(builtIn, custom, errors);
    }

    // ---- plan ----

    public async Task<ProfilePlan> PlanAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var entries = new List<ProfilePlanEntry>(profile.Entries.Count);
        foreach (var entry in profile.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(entry.Kind == ProfileEntryKind.User ? await PlanUserAsync(entry, cancellationToken).ConfigureAwait(false) : await PlanMachineAsync(entry, cancellationToken).ConfigureAwait(false));
        }
        return new(profile, entries);
    }

    private static ProfilePlanEntry Skip(ProfileEntry entry, string code, string titleKey, SettingCategory category, RiskLevel risk, string afterLabelKey) =>
        new(entry, ProfileEntryStatus.Skipped, code, titleKey, category, risk, null, null, afterLabelKey);

    private async Task<ProfilePlanEntry> PlanUserAsync(ProfileEntry entry, CancellationToken ct)
    {
        if (catalog.Find(entry.Id) is not { } d) return Skip(entry, ProfileCode.UnknownEntry, entry.Id, SettingCategory.System, RiskLevel.Low, entry.ChoiceId);
        var choice = d.Choices.FirstOrDefault(c => c.Id == entry.ChoiceId);
        if (choice is null) return Skip(entry, ProfileCode.UnknownEntry, d.TitleKey, d.Category, d.Risk, entry.ChoiceId);
        if (d.Risk == RiskLevel.High) return Skip(entry, ProfileCode.HighRisk, d.TitleKey, d.Category, d.Risk, choice.LabelKey);
        SettingPlan plan;
        try { plan = await settings.PlanAsync(d.Id, choice.Id, ct).ConfigureAwait(false); }
        catch (WinoraException e) when (e.Error.Code is ErrorCode.SettingUnsupportedBuild) { return Skip(entry, ProfileCode.UnsupportedBuild, d.TitleKey, d.Category, d.Risk, choice.LabelKey); }
        catch (WinoraException e) when (e.Error.Code is ErrorCode.SettingStateUnrecognized) { return Skip(entry, ProfileCode.StateUnrecognized, d.TitleKey, d.Category, d.Risk, choice.LabelKey); }
        catch (WinoraException) { return Skip(entry, ProfileCode.Unreadable, d.TitleKey, d.Category, d.Risk, choice.LabelKey); }
        var before = plan.CurrentChoiceId is { } b ? d.Choices.FirstOrDefault(c => c.Id == b) : null;
        return new(entry, plan.WillChange ? ProfileEntryStatus.WillChange : ProfileEntryStatus.AlreadySet, plan.WillChange ? null : ProfileCode.AlreadySet, d.TitleKey, d.Category, d.Risk,
            before?.Id, before?.LabelKey, choice.LabelKey)
        { UserPlan = plan.WillChange ? plan : null, NeedsRestart = d.RequiresExplorerRestart || d.RequiresSignOut };
    }

    private async Task<ProfilePlanEntry> PlanMachineAsync(ProfileEntry entry, CancellationToken ct)
    {
        if (machine.Find(entry.Id) is not { } d) return Skip(entry, ProfileCode.UnknownEntry, entry.Id, SettingCategory.System, RiskLevel.Low, entry.ChoiceId);
        var choice = d.Choices.FirstOrDefault(c => c.Id == entry.ChoiceId);
        if (choice is null) return Skip(entry, ProfileCode.UnknownEntry, d.TitleKey, d.Category, d.Risk, entry.ChoiceId);
        // High risk is refused by the parser; the plan refuses it again so a profile built in code cannot slip one in.
        if (d.Risk == RiskLevel.High) return Skip(entry, ProfileCode.HighRisk, d.TitleKey, d.Category, d.Risk, choice.LabelKey);
        if (!elevation.IsElevated) return Skip(entry, ProfileCode.NeedsAdministrator, d.TitleKey, d.Category, d.Risk, choice.LabelKey);
        var preview = await Task.Run(() => machine.Preview(new OptimizeRequest(d.Id, choice.Id)), ct).ConfigureAwait(false);
        if (preview.RefusalCode is { } refusal) return Skip(entry, MachineSkipCode(refusal), d.TitleKey, d.Category, d.Risk, choice.LabelKey);
        var beforeValue = preview.Before?.ToValue();
        var before = beforeValue is null ? null : d.Choices.FirstOrDefault(c => c.EffectiveValue is { } v && v.Equals(beforeValue));
        if (!preview.WillChange) return new(entry, ProfileEntryStatus.AlreadySet, ProfileCode.AlreadySet, d.TitleKey, d.Category, d.Risk, before?.Id, before?.LabelKey, choice.LabelKey);
        return new(entry, ProfileEntryStatus.WillChange, null, d.TitleKey, d.Category, d.Risk, before?.Id, before?.LabelKey, choice.LabelKey)
        {
            MachineRequest = new OptimizeRequest(d.Id, choice.Id, preview.Before),
            NeedsRestart = d.RequiresRestart,
        };
    }

    private static string MachineSkipCode(string code) => code switch
    {
        OptimizeCode.UnsupportedBuild => ProfileCode.UnsupportedBuild,
        OptimizeCode.UnsupportedEdition => ProfileCode.UnsupportedEdition,
        OptimizeCode.ManagedByPolicy => ProfileCode.ManagedByPolicy,
        OptimizeCode.StateUnrecognized => ProfileCode.StateUnrecognized,
        OptimizeCode.NoCatalog or OptimizeCode.UnknownDefinition or OptimizeCode.UnknownChoice => ProfileCode.UnknownEntry,
        _ => ProfileCode.Unreadable,
    };

    // ---- apply ----

    public async Task<ProfileApplyResult> ApplyAsync(ProfilePlan plan, IProgress<ProfileProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var group = Guid.NewGuid();
            var label = plan.Profile.Id;
            var results = new List<ProfileEntryResult>(plan.Entries.Count);
            RestorePointGateResult? restore = null;
            var todo = plan.Entries.Where(e => e.Status == ProfileEntryStatus.WillChange).ToList();
            var cancelled = false;
            if (todo.Any(e => e.Entry.Kind == ProfileEntryKind.Machine) && gate is not null)
            {
                restore = await gate.BeforeChangeAsync("Profiles", cancellationToken).ConfigureAwait(false);
                if (!restore.Proceed) cancelled = true;
            }
            var done = 0;
            foreach (var item in plan.Entries)
            {
                if (item.Status == ProfileEntryStatus.AlreadySet) { results.Add(new(item.Entry, ProfileEntryOutcome.AlreadySet, ProfileCode.AlreadySet, false)); continue; }
                if (item.Status == ProfileEntryStatus.Skipped) { results.Add(new(item.Entry, ProfileEntryOutcome.Skipped, item.SkipCode ?? ProfileCode.Failed, false)); continue; }
                if (cancelled || cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    results.Add(new(item.Entry, ProfileEntryOutcome.NotRun, ProfileCode.Cancelled, false));
                    continue;
                }
                progress?.Report(new(done, todo.Count, item.Entry));
                try
                {
                    results.Add(item.Entry.Kind == ProfileEntryKind.User
                        ? await ApplyUserAsync(item, group, label, cancellationToken).ConfigureAwait(false)
                        : await ApplyMachineAsync(item, group, label, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    results.Add(new(item.Entry, ProfileEntryOutcome.NotRun, ProfileCode.Cancelled, false));
                }
                done++;
            }
            progress?.Report(new(todo.Count, todo.Count, null));
            return new(group, plan.Profile.Id, results, cancelled, restore);
        }
        finally { run.Release(); }
    }

    private async Task<ProfileEntryResult> ApplyUserAsync(ProfilePlanEntry item, Guid group, string label, CancellationToken ct)
    {
        if (item.UserPlan is not { } plan) return new(item.Entry, ProfileEntryOutcome.Failed, ProfileCode.Stale, false);
        try
        {
            // Broadcast only: Explorer is never restarted by a profile. What needs a restart or a sign-out is reported per entry.
            var outcome = await settings.ApplyAsync(plan, new SettingApplyOptions(true, false) { Group = group, Label = label }, ct).ConfigureAwait(false);
            return outcome.State switch
            {
                OperationState.Applied => new(item.Entry, ProfileEntryOutcome.Applied, ProfileCode.Ok, false),
                OperationState.PendingRestart => new(item.Entry, ProfileEntryOutcome.Applied, outcome.Code, true),
                OperationState.AlreadyConfigured => new(item.Entry, ProfileEntryOutcome.AlreadySet, ProfileCode.AlreadySet, false),
                _ => new(item.Entry, ProfileEntryOutcome.Failed, outcome.Code, false),
            };
        }
        catch (WinoraException e) { return new(item.Entry, ProfileEntryOutcome.Failed, e.Error.Code, false); }
    }

    private async Task<ProfileEntryResult> ApplyMachineAsync(ProfilePlanEntry item, Guid group, string label, CancellationToken ct)
    {
        if (item.MachineRequest is not { } request) return new(item.Entry, ProfileEntryOutcome.Failed, ProfileCode.Stale, false);
        OptimizeApplyResult result;
        try { result = await machine.ApplyAsync(request with { Group = group, Label = label }, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { return new(item.Entry, ProfileEntryOutcome.Failed, ProfileCode.Failed, false); } // one entry must not end the run and hide the results of the others
        if (result.Ok) return new(item.Entry, ProfileEntryOutcome.Applied, ProfileCode.Ok, item.NeedsRestart);
        if (result.Code == OptimizeCode.AlreadySet) return new(item.Entry, ProfileEntryOutcome.AlreadySet, ProfileCode.AlreadySet, false);
        return new(item.Entry, ProfileEntryOutcome.Failed, result.Code, false);
    }

    // ---- history and undo ----

    private IReadOnlyList<OptimizeRecord> MachineHistory()
    {
        try { return machine.History(int.MaxValue); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsLive(OperationState state) => state is OperationState.Applied or OperationState.PendingRestart;

    /// <summary>The profile runs that left changes in the journals, newest first.</summary>
    public async Task<IReadOnlyList<ProfileHistoryEntry>> HistoryAsync(CancellationToken cancellationToken = default)
    {
        var user = (await settings.HistoryAsync(cancellationToken).ConfigureAwait(false)).Where(o => o.Group is not null).ToList();
        var records = await Task.Run(MachineHistory, cancellationToken).ConfigureAwait(false);
        var machineRows = records.Where(r => r.Change.Group is not null && r.Change.RevertOf is null && r.State == OptimizeState.Applied).ToList();
        var live = machineRows.Count == 0 ? [] : await Task.Run(() => machine.CanRevertAll(machineRows), cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<Guid, (string Label, DateTimeOffset At, int Changed, int Live)>();
        void Add(Guid g, string? label, DateTimeOffset at, bool isLive)
        {
            map.TryGetValue(g, out var cur);
            map[g] = (cur.Label ?? label ?? "", cur.At == default || at < cur.At ? at : cur.At, cur.Changed + 1, cur.Live + (isLive ? 1 : 0));
        }
        foreach (var o in user.Where(o => o.State is OperationState.Applied or OperationState.PendingRestart or OperationState.Reverted))
            Add(o.Group!.Value, o.Label, o.AtUtc, IsLive(o.State));
        for (var i = 0; i < machineRows.Count; i++) Add(machineRows[i].Change.Group!.Value, machineRows[i].Change.Label, machineRows[i].BeganUtc, live[i]);
        return map.OrderByDescending(p => p.Value.At).Select(p => new ProfileHistoryEntry(p.Key, p.Value.Label, p.Value.At, p.Value.Changed, p.Value.Live)).ToList();
    }

    /// <summary>
    /// Undoes the entries of one run that are still in the state Winora left. Each underlying service compares first, so an entry that was changed since is left alone and reported as Changed.
    /// </summary>
    public async Task<ProfileUndoResult> UndoAsync(Guid group, CancellationToken cancellationToken = default)
    {
        await run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = new List<ProfileUndoEntry>();
            var userRows = (await settings.HistoryAsync(cancellationToken).ConfigureAwait(false)).Where(o => o.Group == group && IsLive(o.State)).ToList();
            var records = await Task.Run(MachineHistory, cancellationToken).ConfigureAwait(false);
            var machineRows = records.Where(r => r.Change.Group == group && r.Change.RevertOf is null && r.State == OptimizeState.Applied).ToList();
            var cancelled = false;
            if (machineRows.Count > 0 && gate is not null)
            {
                var asked = await gate.BeforeChangeAsync("Profiles", cancellationToken).ConfigureAwait(false);
                if (!asked.Proceed) return new(group, [], true);
            }
            foreach (var row in userRows)
            {
                if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
                entries.Add(await UndoUserAsync(row, cancellationToken).ConfigureAwait(false));
            }
            if (!cancelled)
            {
                var revertable = machineRows.Count == 0 ? [] : await Task.Run(() => machine.CanRevertAll(machineRows), cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < machineRows.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
                    var change = machineRows[i].Change;
                    if (!revertable[i])
                    {
                        // Already undone by an earlier run of this method leaves an applied restore line; anything else changed since.
                        if (records.Any(r => r.Change.RevertOf == change.Id && r.State == OptimizeState.Applied)) continue;
                        entries.Add(new(ProfileEntryKind.Machine, change.DefinitionId, ProfileUndoOutcome.Changed, ProfileCode.Conflict));
                        continue;
                    }
                    var result = await machine.RevertAsync(change.Id, cancellationToken).ConfigureAwait(false);
                    entries.Add(new(ProfileEntryKind.Machine, change.DefinitionId, result.Ok ? ProfileUndoOutcome.Reverted : result.Code == OptimizeCode.CannotRevert ? ProfileUndoOutcome.Changed : ProfileUndoOutcome.Failed,
                        result.Ok ? ProfileCode.Ok : result.Code));
                }
            }
            return new(group, entries, cancelled);
        }
        finally { run.Release(); }
    }

    private async Task<ProfileUndoEntry> UndoUserAsync(SettingOutcome row, CancellationToken ct)
    {
        try
        {
            var outcome = await settings.RevertAsync(row.OperationId, ct).ConfigureAwait(false);
            return outcome.State switch
            {
                OperationState.Reverted => new(ProfileEntryKind.User, row.SettingId, ProfileUndoOutcome.Reverted, ProfileCode.Ok),
                OperationState.Partial => new(ProfileEntryKind.User, row.SettingId, ProfileUndoOutcome.Changed, ProfileCode.Conflict),
                _ => new(ProfileEntryKind.User, row.SettingId, ProfileUndoOutcome.Failed, outcome.Code),
            };
        }
        catch (WinoraException e) when (e.Error.Code is ErrorCode.Superseded or ErrorCode.NotReversible)
        {
            return new(ProfileEntryKind.User, row.SettingId, ProfileUndoOutcome.Changed, ProfileCode.Superseded);
        }
        catch (WinoraException e) { return new(ProfileEntryKind.User, row.SettingId, ProfileUndoOutcome.Failed, e.Error.Code); }
    }

    // ---- custom profiles ----

    /// <summary>
    /// The settings Winora changed that are still in the state Winora left: the newest applied journal line of each setting, checked against the live value. High risk entries are counted and left out.
    /// </summary>
    public async Task<ProfileDraft> CaptureChangedAsync(CancellationToken cancellationToken = default)
    {
        var picks = new List<ProfileSelection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in await settings.HistoryAsync(cancellationToken).ConfigureAwait(false)) // newest first
        {
            // Only a row that changed what the registry holds speaks for the setting; a newer no-op or failed attempt must not hide an older applied row.
            if (o.State is not (OperationState.Applied or OperationState.PendingRestart or OperationState.Reverted or OperationState.Partial)) continue;
            if (!seen.Add(o.SettingId)) continue;
            if (IsLive(o.State)) picks.Add(new(ProfileEntryKind.User, o.SettingId, o.ChoiceId));
        }
        var records = await Task.Run(MachineHistory, cancellationToken).ConfigureAwait(false);
        var seenMachine = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records) // newest first
        {
            if (r.State != OptimizeState.Applied || !seenMachine.Add(r.Change.DefinitionId)) continue;
            if (r.Change.RevertOf is null && r.Change.ChoiceId is { } choice) picks.Add(new(ProfileEntryKind.Machine, r.Change.DefinitionId, choice));
        }
        var draft = FromSelection(picks);
        // Keep only what is still set. A value that was changed since is not "the state Winora left".
        var kept = new List<ProfileEntry>();
        foreach (var entry in draft.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var planned = await PlanAsync(new Profile("custom.capture", null, null, "capture", [entry]), cancellationToken).ConfigureAwait(false);
            if (planned.Entries[0].Status == ProfileEntryStatus.AlreadySet) kept.Add(entry);
        }
        return draft with { Entries = kept };
    }

    /// <summary>Entries from a selection of ids and choices. Unknown ids, unknown choices and High risk entries are left out and counted.</summary>
    public ProfileDraft FromSelection(IReadOnlyList<ProfileSelection> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var entries = new List<ProfileEntry>();
        int high = 0, unknown = 0;
        foreach (var s in selection)
        {
            var reference = Find(s.Kind, s.Id);
            if (reference is null || !reference.ChoiceIds.Contains(s.ChoiceId) || entries.Any(e => e.Kind == s.Kind && e.Id == s.Id)) { unknown++; continue; }
            if (reference.Risk == RiskLevel.High) { high++; continue; }
            entries.Add(new(s.Kind, s.Id, s.ChoiceId));
        }
        return new(entries.Take(ProfileFile.MaxEntries).ToList(), high, unknown);
    }

    /// <summary>Saves entries as a custom profile. The id comes from the name; a taken id gets a number. Throws <see cref="ProfileFormatException"/> for a bad name or an empty list, IOException when the file cannot be written.</summary>
    public Profile SaveCustom(string name, IReadOnlyList<ProfileEntry> entries)
    {
        var clean = ProfileFile.CleanName(name);
        if (entries.Count == 0) throw new ProfileFormatException(ProfileCode.NothingToSave, "the profile has no entries");
        var existing = store.List();
        if (existing.Count >= MaxCustomProfiles) throw new ProfileFormatException(ProfileCode.TooMany, "too many custom profiles");
        var id = FreeId(ProfileFile.CustomPrefix + ProfileFile.SlugFor(clean), existing);
        var profile = new Profile(id, null, null, clean, entries.Select(e => new ProfileEntry(e.Kind, e.Id, e.ChoiceId)).ToList());
        var json = ProfileFile.WriteCustom(profile);
        // The file must read back through the same strict parser before it is stored.
        ProfileFile.ParseCustom(json, id, this);
        store.Write(id, json);
        return profile;
    }

    /// <summary>Imports a profile file. Never trusted: the strict parser checks it against the catalogs, then it is stored under a new id.</summary>
    public Profile ImportCustom(string json)
    {
        var draft = ProfileFile.ParseCustom(json, "custom.import", this);
        return SaveCustom(draft.Name!, draft.Entries);
    }

    /// <summary>The text to write to an export file.</summary>
    public string Export(Profile profile) => ProfileFile.WriteCustom(profile);

    public void DeleteCustom(string id)
    {
        if (!ProfileFile.IsCustomId(id)) throw new ProfileFormatException(ProfileCode.Invalid, "the profile id is not valid");
        store.Delete(id);
    }

    private static string FreeId(string wanted, IReadOnlyList<string> taken)
    {
        if (!taken.Contains(wanted, StringComparer.Ordinal)) return wanted;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = wanted + "-" + i;
            if (candidate.Length <= ProfileFile.CustomPrefix.Length + 40 && !taken.Contains(candidate, StringComparer.Ordinal)) return candidate;
        }
        throw new ProfileFormatException(ProfileCode.TooMany, "no free profile id");
    }
}
