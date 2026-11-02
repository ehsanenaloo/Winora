// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop;

/// <summary>Shared pieces of the adapters. An adapter turns the records of one journal into entries; it never writes.</summary>
internal static class HistoryText
{
    public static HistoryEntry Entry(HistoryArea area, Guid id, DateTimeOffset at, HistoryResult result, string title, string outcome, string code, bool isUndo, bool restart, bool canUndo, string? reason,
        Guid? batch = null, string? label = null, HistoryBatchKind kind = HistoryBatchKind.None, bool ownReview = false, bool canDismiss = false, bool canRestoreBefore = false) =>
        new($"{area}:{id:N}", area, id, at, result, title, outcome, code, isUndo, restart, canUndo, reason, batch, label, kind, ownReview, canDismiss, canRestoreBefore);

    public static HistoryBatchKind KindOf(string? label) => label is not null && label.StartsWith(UpdateModes.LabelPrefix, StringComparison.Ordinal) ? HistoryBatchKind.Updates : HistoryBatchKind.Profile;

    /// <summary>Why a change that is not undoable cannot be undone now; the keys are texts of the History page.</summary>
    public static string? Reason(bool isUndo, bool elevated, bool undone, bool canRevert) =>
        undone ? null : isUndo ? "HistUndoIsUndo" : !elevated ? "HistUndoNeedsAdmin" : canRevert ? null : "HistUndoChangedSince";

    public static string OutcomeOf(LocalizationService text, string? state, string code, bool known) =>
        known && state is not null && text.Has("HistOutcome_" + state) ? text["HistOutcome_" + state] : text["HistOutcome_Interrupted"];
}

public sealed class SettingsHistorySource(SettingsModel settings, LocalizationService text) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Settings;

    public IReadOnlyList<HistoryEntry> Read()
    {
        var entries = new List<HistoryEntry>();
        var newestApplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in settings.History)
        {
            if (o.State == OperationState.AlreadyConfigured) continue;
            var definition = settings.DefinitionOf(o.SettingId);
            string Label(string? id) => definition is null ? (id ?? text["SetCustomValue"]) : id is null ? text["SetPreviousValue"] : settings.Format.Choice(definition, id);
            var title = definition is null ? o.SettingId : settings.Format.Title(definition);
            var line = $"{title}: {Label(o.BeforeChoiceId)} {text["ArrowGlyph"]} {Label(o.AfterChoiceId ?? o.ChoiceId)}";
            var applied = o.State is OperationState.Applied or OperationState.PendingRestart;
            var result = applied ? HistoryResult.Applied : o.State == OperationState.Reverted ? HistoryResult.Undone : HistoryResult.Failed;
            string? reason = null;
            var canUndo = false;
            if (applied)
            {
                var newest = newestApplied.Add(o.SettingId);
                var live = settings.StateOf(o.SettingId)?.MatchedChoiceId;
                var expected = o.AfterChoiceId ?? o.ChoiceId;
                // An older change that a newer Winora change replaced was not changed by something else: it stays Applied and points to the newer entry.
                if (!newest) reason = "HistUndoNewerChange";
                else if (settings.StateOf(o.SettingId) is not null && live != expected) { result = HistoryResult.ChangedSince; reason = "HistUndoChangedSince"; }
                else if (settings.IsRevertible(o)) canUndo = true;
                else reason = "HistUndoNotPossible";
            }
            var outcome = text.Has("SetHist_" + o.State) ? text["SetHist_" + o.State] : text["UnknownResult"];
            entries.Add(HistoryText.Entry(HistoryArea.Settings, o.OperationId, o.AtUtc, result, line, outcome, o.Code, false, o.State == OperationState.PendingRestart, canUndo, reason,
                o.Group, o.Label, o.Group is null ? HistoryBatchKind.None : HistoryText.KindOf(o.Label), ownReview: true));
        }
        return entries;
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        await settings.ReviewAndRevertAsync(entry.Id).ConfigureAwait(true);
        if (settings.History.FirstOrDefault(o => o.OperationId == entry.Id)?.State == OperationState.Reverted) return HistoryUndoOutcome.Success();
        if (!settings.RevertConfirmed) return new(false, "cancelled", Cancelled: true);
        // The model put the reason into its notice; the History page shows it once, as its own result.
        var notice = settings.Notice;
        var code = notice.IsOpen && notice.Code.Length > 0 ? notice.Code : "settings.revert_failed";
        var message = notice.IsOpen ? notice.Message : null;
        notice.Clear();
        return message is { Length: > 0 } ? HistoryUndoOutcome.Failure(code, message) : HistoryUndoOutcome.Failure(code);
    }
}

public sealed class OptimizeHistorySource(OptimizeService service, LocalizationService text, IElevationState elevation) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Optimize;

    public IReadOnlyList<HistoryEntry> Read()
    {
        var records = service.History(500);
        var revertable = elevation.IsElevated && records.Count > 0 ? service.CanRevertAll(records) : [];
        var undone = records.Where(r => r.Change.RevertOf is not null && r.State == OptimizeState.Applied).Select(r => r.Change.RevertOf!.Value).ToHashSet();
        var entries = new List<HistoryEntry>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i]; var c = r.Change;
            var definition = service.Find(c.DefinitionId);
            var title = definition is not null && text.Has(definition.TitleKey) ? text[definition.TitleKey] : c.DefinitionId;
            if (c.TargetName is { } card) title += $" ({card})";
            string Label(StoredValue v)
            {
                var value = v.ToValue();
                var choice = definition?.Choices.FirstOrDefault(x => x.EffectiveValue is { } ev && value is not null && ev.Equals(value));
                return choice is null ? text["OptimizeOtherValue"] : text.Has(choice.LabelKey) ? text[choice.LabelKey] : choice.Id;
            }
            var line = text.Format("OptimizeHistoryLine", title, Label(c.Before), Label(c.After)) + (c.RevertOf is null ? "" : " " + text["PowerUndoSuffix"]);
            var isUndo = c.RevertOf is not null;
            var wasUndone = undone.Contains(c.Id);
            var can = !isUndo && !wasUndone && i < revertable.Count && revertable[i]; // the service decides which states can still be undone (also a failed verification)
            var result = r.State switch
            {
                OptimizeState.Applied => wasUndone ? HistoryResult.Undone : isUndo || can || !elevation.IsElevated ? HistoryResult.Applied : HistoryResult.ChangedSince,
                OptimizeState.Dismissed => HistoryResult.Dismissed,
                null => HistoryResult.Unconfirmed,
                _ => HistoryResult.Failed,
            };
            var unconfirmed = result == HistoryResult.Unconfirmed;
            entries.Add(HistoryText.Entry(HistoryArea.Optimize, c.Id, r.BeganUtc, result, line, HistoryText.OutcomeOf(text, r.State?.ToString(), r.Code, r.State is not null), r.Code, isUndo,
                result == HistoryResult.Applied && definition?.RequiresRestart == true && !isUndo, can, can || result is HistoryResult.Undone or HistoryResult.Failed or HistoryResult.Dismissed or HistoryResult.Unconfirmed ? null : HistoryText.Reason(isUndo, elevation.IsElevated, wasUndone, can),
                c.Group, c.Label, c.Group is null ? HistoryBatchKind.None : HistoryText.KindOf(c.Label), false, unconfirmed, unconfirmed && service.CanRestoreBefore(r)));
        }
        return entries;
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.RevertAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == OptimizeState.Applied ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public async Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.DismissAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == OptimizeState.Dismissed ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public Task<RecoveryPlan?> PlanRestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(service.History(500).FirstOrDefault(r => r.Change.Id == entry.Id) is { } record ? service.PlanRestoreBefore(record) : null);

    public async Task<HistoryUndoOutcome> RestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken, string? expectedToken = null)
    {
        var result = await service.RestoreBeforeAsync(entry.Id, cancellationToken, expectedToken).ConfigureAwait(true);
        return result.State == OptimizeState.Applied ? HistoryUndoOutcome.Success() with { RecordLost = result.RecordLost } : HistoryUndoOutcome.Failure(result.Code);
    }
}

public sealed class PowerHistorySource(PowerChangeService service, LocalizationService text, IElevationState elevation) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Power;

    public IReadOnlyList<HistoryEntry> Read()
    {
        var records = service.History(500);
        var revertable = elevation.IsElevated && records.Count > 0 ? service.CanRevertAll(records) : [];
        var undone = records.Where(r => r.Change.RevertOf is not null && r.State == PowerChangeState.Applied).Select(r => r.Change.RevertOf!.Value).ToHashSet();
        var entries = new List<HistoryEntry>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i]; var c = r.Change;
            var isUndo = c.RevertOf is not null; var wasUndone = undone.Contains(c.Id);
            var can = r.State == PowerChangeState.Applied && !isUndo && !wasUndone && i < revertable.Count && revertable[i];
            var result = r.State == PowerChangeState.Applied ? (wasUndone ? HistoryResult.Undone : isUndo || can || !elevation.IsElevated ? HistoryResult.Applied : HistoryResult.ChangedSince)
                : r.State == PowerChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed;
            var unconfirmed = result == HistoryResult.Unconfirmed;
            entries.Add(HistoryText.Entry(HistoryArea.Power, c.Id, r.BeganUtc, result, Describe(c), HistoryText.OutcomeOf(text, r.State?.ToString(), r.Code, r.State is not null), r.Code, isUndo, false, can,
                can || result is HistoryResult.Undone or HistoryResult.Failed or HistoryResult.Dismissed or HistoryResult.Unconfirmed ? null : HistoryText.Reason(isUndo, elevation.IsElevated, wasUndone, can),
                c.Group, c.Label, c.Group is null ? HistoryBatchKind.None : HistoryBatchKind.Group, false, unconfirmed, unconfirmed && elevation.IsElevated && service.CanRestoreBefore(r)));
        }
        return entries;
    }

    private string Describe(PowerChange c) => PowerViewModel.Describe(text, c);

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.RevertAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == PowerChangeState.Applied ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public async Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.DismissAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == PowerChangeState.Dismissed ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public Task<RecoveryPlan?> PlanRestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(service.History(500).FirstOrDefault(r => r.Change.Id == entry.Id) is { } record ? service.PlanRestoreBefore(record) : null);

    public async Task<HistoryUndoOutcome> RestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken, string? expectedToken = null)
    {
        var result = await service.RestoreBeforeAsync(entry.Id, cancellationToken, expectedToken).ConfigureAwait(true);
        return result.State == PowerChangeState.Applied ? HistoryUndoOutcome.Success() with { RecordLost = result.RecordLost } : HistoryUndoOutcome.Failure(result.Code);
    }
}

public sealed class NetworkHistorySource(NetworkService service, LocalizationService text, IElevationState elevation) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Network;

    public IReadOnlyList<HistoryEntry> Read()
    {
        var records = service.History(200);
        var revertable = elevation.IsElevated && records.Count > 0 ? service.CanRevertAll(records) : [];
        var undone = records.Where(r => r.Change.RevertOf is not null && r.State == NetworkChangeState.Applied).Select(r => r.Change.RevertOf!.Value).ToHashSet();
        var entries = new List<HistoryEntry>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i]; var c = r.Change;
            var name = c.ProviderId == DnsProviderCatalog.AutomaticId ? text["NetProviderAutomatic"] : c.ProviderId == DnsProviderCatalog.CustomId ? text["NetProviderCustom"] : service.Catalog.Find(c.ProviderId)?.Name ?? c.ProviderId;
            var line = text.Format("NetHistoryLine", name, text.LtrList(c.Entries.Select(e => e.AdapterName))) + (c.RevertOf is null ? "" : " " + text["NetUndoSuffix"]);
            var isUndo = c.RevertOf is not null; var wasUndone = undone.Contains(c.Id);
            var can = !isUndo && !wasUndone && i < revertable.Count && revertable[i]; // a partial change can still be undone for the adapters that changed
            var result = r.State == NetworkChangeState.Applied ? (wasUndone ? HistoryResult.Undone : isUndo || can || !elevation.IsElevated ? HistoryResult.Applied : HistoryResult.ChangedSince) 
                : r.State == NetworkChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed;
            var unconfirmed = result == HistoryResult.Unconfirmed;
            entries.Add(HistoryText.Entry(HistoryArea.Network, c.Id, r.BeganUtc, result, line, HistoryText.OutcomeOf(text, r.State?.ToString(), r.Code, r.State is not null), r.Code, isUndo, false, can,
                can || result is HistoryResult.Undone or HistoryResult.Failed or HistoryResult.Dismissed or HistoryResult.Unconfirmed ? null : HistoryText.Reason(isUndo, elevation.IsElevated, wasUndone, can),
                null, null, HistoryBatchKind.None, false, unconfirmed, unconfirmed && elevation.IsElevated && service.CanRestoreBefore(r)));
        }
        return entries;
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.RevertAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == NetworkChangeState.Applied ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }
    public async Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.DismissAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == NetworkChangeState.Dismissed ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public Task<RecoveryPlan?> PlanRestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(service.History(200).FirstOrDefault(r => r.Change.Id == entry.Id) is { } record ? service.PlanRestoreBefore(record) : null);

    public async Task<HistoryUndoOutcome> RestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken, string? expectedToken = null)
    {
        var result = await service.RestoreBeforeAsync(entry.Id, cancellationToken, expectedToken).ConfigureAwait(true);
        return result.State == NetworkChangeState.Applied ? HistoryUndoOutcome.Success() with { RecordLost = result.RecordLost } : HistoryUndoOutcome.Failure(result.Code);
    }
}

public sealed class StartupHistorySource(StartupService service, LocalizationService text, IElevationState elevation) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Startup;

    public IReadOnlyList<HistoryEntry> Read()
    {
        var records = service.History(200);
        var revertable = elevation.IsElevated && records.Count > 0 ? service.CanRevertAll(records) : [];
        var undone = records.Where(r => r.Change.RevertOf is not null && r.State == StartupChangeState.Applied).Select(r => r.Change.RevertOf!.Value).ToHashSet();
        var entries = new List<HistoryEntry>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i]; var c = r.Change;
            var line = text.Format(c.Action == StartupAction.Enable ? "StartupHistoryEnable" : "StartupHistoryDisable", text.Name(c.Name), text.Enum(c.Scope)) + (c.RevertOf is null ? "" : " " + text["StartupUndoSuffix"]);
            var isUndo = c.RevertOf is not null; var wasUndone = undone.Contains(c.Id);
            var can = r.State == StartupChangeState.Applied && !isUndo && !wasUndone && i < revertable.Count && revertable[i];
            var result = r.State == StartupChangeState.Applied ? (wasUndone ? HistoryResult.Undone : isUndo || can || !elevation.IsElevated ? HistoryResult.Applied : HistoryResult.ChangedSince) 
                : r.State == StartupChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed;
            var unconfirmed = result == HistoryResult.Unconfirmed;
            entries.Add(HistoryText.Entry(HistoryArea.Startup, c.Id, r.BeganUtc, result, line, HistoryText.OutcomeOf(text, r.State?.ToString(), r.Code, r.State is not null), r.Code, isUndo, false, can,
                can || result is HistoryResult.Undone or HistoryResult.Failed or HistoryResult.Dismissed or HistoryResult.Unconfirmed ? null : HistoryText.Reason(isUndo, elevation.IsElevated, wasUndone, can),
                null, null, HistoryBatchKind.None, false, unconfirmed, unconfirmed && elevation.IsElevated && service.CanRestoreBefore(r)));
        }
        return entries;
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.RevertAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == StartupChangeState.Applied ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }
    public async Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.DismissAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == StartupChangeState.Dismissed ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public async Task<RecoveryPlan?> PlanRestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        service.History(200).FirstOrDefault(r => r.Change.Id == entry.Id) is { } record ? await service.PlanRestoreBeforeAsync(record, cancellationToken).ConfigureAwait(true) : null;

    public async Task<HistoryUndoOutcome> RestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken, string? expectedToken = null)
    {
        var result = await service.RestoreBeforeAsync(entry.Id, cancellationToken, expectedToken).ConfigureAwait(true);
        return result.State == StartupChangeState.Applied ? HistoryUndoOutcome.Success() with { RecordLost = result.RecordLost } : HistoryUndoOutcome.Failure(result.Code);
    }
}

public sealed class TaskHistorySource(ScheduledTaskService service, LocalizationService text, IElevationState elevation) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Tasks;

    public IReadOnlyList<HistoryEntry> Read()
    {
        var records = service.History(200);
        var revertable = elevation.IsElevated && records.Count > 0 ? service.CanRevertAll(records) : [];
        var undone = records.Where(r => r.Change.RevertOf is not null && r.State == ScheduledTaskChangeState.Applied).Select(r => r.Change.RevertOf!.Value).ToHashSet();
        var entries = new List<HistoryEntry>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i]; var c = r.Change;
            var line = text.Format("TaskHistory_" + c.Kind, text.Name(c.Name)) + (c.RevertOf is null ? "" : " " + text["TaskUndoSuffix"]);
            var isUndo = c.RevertOf is not null; var wasUndone = undone.Contains(c.Id);
            var can = r.State == ScheduledTaskChangeState.Applied && !isUndo && !wasUndone && i < revertable.Count && revertable[i];
            var result = r.State == ScheduledTaskChangeState.Applied ? (wasUndone ? HistoryResult.Undone : isUndo || can || !elevation.IsElevated ? HistoryResult.Applied : HistoryResult.ChangedSince) 
                : r.State == ScheduledTaskChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed;
            var unconfirmed = result == HistoryResult.Unconfirmed;
            entries.Add(HistoryText.Entry(HistoryArea.Tasks, c.Id, r.BeganUtc, result, line, HistoryText.OutcomeOf(text, r.State?.ToString(), r.Code, r.State is not null), r.Code, isUndo, false, can,
                can || result is HistoryResult.Undone or HistoryResult.Failed or HistoryResult.Dismissed or HistoryResult.Unconfirmed ? null : HistoryText.Reason(isUndo, elevation.IsElevated, wasUndone, can),
                null, null, HistoryBatchKind.None, false, unconfirmed, unconfirmed && elevation.IsElevated && service.CanRestoreBefore(r)));
        }
        return entries;
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.RevertAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == ScheduledTaskChangeState.Applied ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }
    public async Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.DismissAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == ScheduledTaskChangeState.Dismissed ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public Task<RecoveryPlan?> PlanRestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(service.History(200).FirstOrDefault(r => r.Change.Id == entry.Id) is { } record ? service.PlanRestoreBefore(record) : null);

    public async Task<HistoryUndoOutcome> RestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken, string? expectedToken = null)
    {
        var result = await service.RestoreBeforeAsync(entry.Id, cancellationToken, expectedToken).ConfigureAwait(true);
        return result.State == ScheduledTaskChangeState.Applied ? HistoryUndoOutcome.Success() with { RecordLost = result.RecordLost } : HistoryUndoOutcome.Failure(result.Code);
    }
}

public sealed class ServiceHistorySource(ServiceManagerService service, LocalizationService text, IElevationState elevation) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Services;

    public IReadOnlyList<HistoryEntry> Read()
    {
        var records = service.History(200);
        var revertable = elevation.IsElevated && records.Count > 0 ? service.CanRevertAll(records) : [];
        var undone = records.Where(r => r.Change.RevertOf is not null && r.State == ServiceChangeState.Applied).Select(r => r.Change.RevertOf!.Value).ToHashSet();
        var entries = new List<HistoryEntry>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i]; var c = r.Change;
            var line = text.Format("V2SvHistory_" + c.Kind, c.DisplayName, text.Enum(c.BeforeMode ?? ServiceStartMode.Unknown), text.Enum(c.AfterMode ?? ServiceStartMode.Unknown)) + (c.RevertOf is null ? "" : " " + text["V2SvUndoSuffix"]);
            var isUndo = c.RevertOf is not null; var wasUndone = undone.Contains(c.Id);
            var can = r.State == ServiceChangeState.Applied && !isUndo && !wasUndone && i < revertable.Count && revertable[i];
            var result = r.State == ServiceChangeState.Applied ? (wasUndone ? HistoryResult.Undone : isUndo || can || !elevation.IsElevated ? HistoryResult.Applied : HistoryResult.ChangedSince) 
                : r.State == ServiceChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed;
            var unconfirmed = result == HistoryResult.Unconfirmed;
            entries.Add(HistoryText.Entry(HistoryArea.Services, c.Id, r.BeganUtc, result, line, HistoryText.OutcomeOf(text, r.State?.ToString(), r.Code, r.State is not null), r.Code, isUndo, false, can,
                can || result is HistoryResult.Undone or HistoryResult.Failed or HistoryResult.Dismissed or HistoryResult.Unconfirmed ? null : HistoryText.Reason(isUndo, elevation.IsElevated, wasUndone, can),
                null, null, HistoryBatchKind.None, false, unconfirmed, unconfirmed && elevation.IsElevated && service.CanRestoreBefore(r)));
        }
        return entries;
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.RevertAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.Ok ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }
    public async Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.DismissAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == ServiceChangeState.Dismissed ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public Task<RecoveryPlan?> PlanRestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(service.History(200).FirstOrDefault(r => r.Change.Id == entry.Id) is { } record ? service.PlanRestoreBefore(record) : null);

    public async Task<HistoryUndoOutcome> RestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken, string? expectedToken = null)
    {
        var result = await service.RestoreBeforeAsync(entry.Id, cancellationToken, expectedToken).ConfigureAwait(true);
        return result.State == ServiceChangeState.Applied ? HistoryUndoOutcome.Success() with { RecordLost = result.RecordLost } : HistoryUndoOutcome.Failure(result.Code);
    }
}

public sealed class PermissionHistorySource(PermissionService service, LocalizationService text) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Permissions;

    private string Subject(ConsentTarget target)
    {
        var name = PermissionCatalog.Find(target.Capability) is null ? target.Capability : text["Perm_" + target.Capability + "_Name"];
        return target.Scope switch
        {
            ConsentScope.Desktop => text.Format("PermSubjectDesktop", name),
            ConsentScope.App => text.Format("PermSubjectApp", name, ConsentAppNames.PackageName(target.AppKey ?? "")),
            _ => name,
        };
    }

    public IReadOnlyList<HistoryEntry> Read()
    {
        var records = service.History(200);
        var revertable = records.Count > 0 ? service.CanRevertAll(records) : [];
        var undone = records.Where(r => r.Change.RevertOf is not null && r.State == PermissionChangeState.Applied).Select(r => r.Change.RevertOf!.Value).ToHashSet();
        var entries = new List<HistoryEntry>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i]; var c = r.Change;
            var undo = c.RevertOf is null ? "" : " " + text["PermUndoSuffix"];
            var line = c.Group != PermissionGroup.Single || c.Entries.Count != 1
                ? text.Format("PermHistoryGroup", text["PermGroup_" + c.Group], c.Entries.Count) + undo
                : text.Format("PermHistorySingle", Subject(c.Entries[0].Target), text["PermState_" + ConsentValues.StateOf(c.Entries[0].AfterRaw)]) + undo;
            var isUndo = c.RevertOf is not null; var wasUndone = undone.Contains(c.Id);
            var can = (r.State is PermissionChangeState.Applied or PermissionChangeState.Partial) && !isUndo && !wasUndone && i < revertable.Count && revertable[i];
            var result = r.State == PermissionChangeState.Applied ? (wasUndone ? HistoryResult.Undone : isUndo || can ? HistoryResult.Applied : HistoryResult.ChangedSince)
                : r.State == PermissionChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed;
            var unconfirmed = result == HistoryResult.Unconfirmed;
            entries.Add(HistoryText.Entry(HistoryArea.Permissions, c.Id, r.BeganUtc, result, line, HistoryText.OutcomeOf(text, r.State?.ToString(), r.Code, r.State is not null), r.Code, isUndo, false, can,
                can || result is HistoryResult.Undone or HistoryResult.Failed or HistoryResult.Dismissed or HistoryResult.Unconfirmed ? null : HistoryText.Reason(isUndo, true, wasUndone, can),
                null, null, HistoryBatchKind.None, false, unconfirmed, unconfirmed && service.CanRestoreBefore(r)));
        }
        return entries;
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.RevertAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == PermissionChangeState.Applied ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public async Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.DismissAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == PermissionChangeState.Dismissed ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }

    public Task<RecoveryPlan?> PlanRestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(service.History(200).FirstOrDefault(r => r.Change.Id == entry.Id) is { } record ? service.PlanRestoreBefore(record) : null);

    public async Task<HistoryUndoOutcome> RestoreBeforeAsync(HistoryEntry entry, CancellationToken cancellationToken, string? expectedToken = null)
    {
        var result = await service.RestoreBeforeAsync(entry.Id, cancellationToken, expectedToken).ConfigureAwait(true);
        return result.State == PermissionChangeState.Applied ? HistoryUndoOutcome.Success() with { RecordLost = result.RecordLost } : HistoryUndoOutcome.Failure(result.Code);
    }
}

/// <summary>A removed app comes back through Restore: Winora registers its files again when they are still on the disk, else it opens the Microsoft Store page of the app and waits until Windows has installed it.</summary>
public sealed class BloatwareHistorySource(BloatwareService service, LocalizationService text) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Bloatware;

    public IReadOnlyList<HistoryEntry> Read()
    {
        var records = service.History(200);
        IReadOnlySet<string> installed;
        try { installed = records.Count > 0 ? service.InstalledFamilies() : new HashSet<string>(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException) { installed = new HashSet<string>(); }
        return records.Select(r =>
        {
            var c = r.Change;
            var line = text.Format(c.Kind == BloatChangeKind.Remove ? "BloatHistoryRemove" : "BloatHistoryReinstall", c.Shown, c.Version);
            var result = BloatHistory.Result(r, records);
            var option = result == HistoryResult.Removed ? Option(r, records, installed) : new ReinstallOption(ReinstallKind.None, null);
            // Registering again is one quick step and is undone here; installing through the Store takes minutes and shows its progress on the Preinstalled apps page, so the entry points there.
            var here = option.Kind == ReinstallKind.Register;
            return HistoryText.Entry(HistoryArea.Bloatware, c.Id, r.BeganUtc, result, line, HistoryText.OutcomeOf(text, r.State?.ToString(), "", r.State is not null), r.State?.ToString() ?? "interrupted", c.RevertOf is not null, false, here,
                result == HistoryResult.Removed && !here ? "HistUndoStore" : null, ownReview: here, canDismiss: result == HistoryResult.Unconfirmed);
        }).ToList();
    }

    private ReinstallOption Option(BloatChangeRecord record, IReadOnlyList<BloatChangeRecord> history, IReadOnlySet<string> installed)
    {
        try { return service.GetReinstallOption(record, history, installed); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException) { return new(ReinstallKind.None, null); }
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.ReinstallAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.State == BloatState.Applied ? HistoryUndoOutcome.Success() with { RecordLost = result.RecordLost } : HistoryUndoOutcome.Failure(result.Code, BloatFailureText.Format(text, result.Name.Length > 0 ? result.Name : result.FullName, result.Code, result.Steps.LastOrDefault(s => !s.Ok)?.Hresult ?? 0));
    }
    public async Task<HistoryUndoOutcome> DismissAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var result = await service.DismissAsync(entry.Id, cancellationToken).ConfigureAwait(true);
        return result.Ok ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure(result.Code);
    }
}

/// <summary>Programs removed on the Apps page (Store apps are in the Preinstalled apps record). Winora cannot bring a removed program back, so the entry says where to get it again.</summary>
public sealed class AppsHistorySource(AppUninstallService service, LocalizationService text) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Apps;

    public IReadOnlyList<HistoryEntry> Read() => service.History(200).Select(r =>
    {
        var c = r.Change;
        var state = r.End?.State;
        var ok = state is AppRemovalState.Removed or AppRemovalState.PendingRestart;
        var name = string.IsNullOrWhiteSpace(c.Version) ? c.Name : $"{c.Name} {c.Version}";
        var outcome = state switch
        {
            AppRemovalState.Removed or AppRemovalState.PendingRestart => "Applied", AppRemovalState.Cancelled => "Cancelled", AppRemovalState.Refused => "Refused", AppRemovalState.Failed => "Failed",
            AppRemovalState.StillRunning => "StillRunning", _ => "Interrupted",
        };
        var result = ok ? HistoryResult.Applied : state == AppRemovalState.StillRunning ? HistoryResult.Unconfirmed : HistoryResult.Failed;
        return HistoryText.Entry(HistoryArea.Apps, c.Id, r.BeganUtc, result, text.Format("V2ApHistoryRemove", name),
            HistoryText.OutcomeOf(text, outcome, "", state is not null), r.End?.Code ?? "interrupted", false, r.End?.RestartNeeded == true, false, ok ? "HistUndoApps" : null);
    }).ToList();

    public Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken) => Task.FromResult(HistoryUndoOutcome.Failure("history.not_undoable"));
}

/// <summary>Cleanup removes files for good unless the engine quarantined them, so a run has no undo here.</summary>
public sealed class CleanupHistorySource(ICleanupJournal journal, LocalizationService text) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Cleanup;

    public IReadOnlyList<HistoryEntry> Read() => journal.History(100).Select(r =>
    {
        var line = r.Result is null ? text["FreeSpaceInterrupted"] : text.Format("HistCleanupLine", SizeFormat.Bytes(text, r.Result.RemovedBytes), text.Number(r.Result.RemovedFiles));
        var result = r.Result is { State: CleanupRunState.Completed } ? HistoryResult.Applied : HistoryResult.Failed;
        return HistoryText.Entry(HistoryArea.Cleanup, r.Intent.RunId, r.Result?.EndedUtc ?? r.Intent.StartedUtc, result, line, r.Result is { State: CleanupRunState.Cancelled } ? text["HistOutcome_Cancelled"] : text[r.Result is null ? "HistOutcome_Interrupted" : "HistOutcome_Applied"],
            r.Result is null ? "interrupted" : "ok", false, false, false, result == HistoryResult.Applied ? "HistUndoCleanup" : null);
    }).ToList();

    public Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken) => Task.FromResult(HistoryUndoOutcome.Failure("history.not_undoable"));
}

/// <summary>Repair runs and feature changes. A feature change is undone on the Tools page, which has the feature list it needs.</summary>
public sealed class RepairHistorySource(RepairService service, LocalizationService text) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Repair;

    public IReadOnlyList<HistoryEntry> Read() => service.History(200).Select(r =>
    {
        var line = RepairText.Describe(text, r);
        var ok = r.End is { Outcome: RepairOutcome.Succeeded or RepairOutcome.FoundProblems };
        return HistoryText.Entry(HistoryArea.Repair, r.Begin.Id, r.BeganUtc, RepairText.Result(r), line, r.End is { } end ? text["ToolsOutcome_" + end.Outcome] : text["ToolsInterrupted"],
            r.End?.Code ?? "interrupted", r.Begin.RevertOf is not null, r.End?.RestartNeeded == true, false, ok && FeatureService.IsRevertable(r) ? "HistUndoInPage" : null);
    }).ToList();

    public Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken) => Task.FromResult(HistoryUndoOutcome.Failure("history.not_undoable"));
}

/// <summary>Batches: a profile run spans the user settings and the machine settings, a Windows Update mode and a power group are written by one service each.</summary>
public sealed class HistoryBatchHandler(LocalizationService text, ProfileService profiles, OptimizeService optimize, PowerChangeService power) : IHistoryBatchHandler
{
    public string Title(HistoryBatchKind kind, string label, int count) => kind switch
    {
        HistoryBatchKind.Profile => text.Format(count == 1 ? "HistBatchProfileOne" : "HistBatchProfile", ProfileName(label), count),
        HistoryBatchKind.Updates => text.Format(count == 1 ? "HistBatchUpdatesOne" : "HistBatchUpdates", UpdateModeName(label), count),
        _ => text.Format("PowerHistoryGroup", text.Has($"PowerGroup_{label}") ? text[$"PowerGroup_{label}"] : text["PowerGroup_default"], count),
    };

    private string UpdateModeName(string label)
    {
        var key = "UpdatesMode_" + (label.StartsWith(UpdateModes.LabelPrefix, StringComparison.Ordinal) ? label[UpdateModes.LabelPrefix.Length..] : label);
        return text.Has(key) ? text[key] : label;
    }

    private string ProfileName(string id)
    {
        var profile = profiles.Load().BuiltIn.FirstOrDefault(p => p.Id == id);
        return profile?.NameKey is { } key && text.Has(key) ? text[key] : id;
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryBatchKind kind, Guid batch, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case HistoryBatchKind.Profile:
                var undo = await profiles.UndoAsync(batch, cancellationToken).ConfigureAwait(true);
                return undo.Cancelled ? new(false, "cancelled", Cancelled: true) : new(true, "ok", undo.Entries.Count(e => e.Outcome == ProfileUndoOutcome.Reverted), undo.Entries.Count(e => e.Outcome != ProfileUndoOutcome.Reverted));
            case HistoryBatchKind.Updates:
                var (results, skipped) = await optimize.RevertGroupAsync(batch, cancellationToken).ConfigureAwait(true);
                return new(results.Count > 0 || skipped == 0, "ok", results.Count(r => r.State == OptimizeState.Applied), skipped);
            default:
                var (powerResults, powerSkipped) = await power.RevertGroupAsync(batch, cancellationToken).ConfigureAwait(true);
                return new(powerResults.Count > 0 || powerSkipped == 0, "ok", powerResults.Count(r => r.State == PowerChangeState.Applied), powerSkipped);
        }
    }
}

/// <summary>The developer simulation. Release builds do not add this source to the shell; tests add it themselves.</summary>
public sealed class SimulationHistorySource(SimulationModel model, LocalizationService text) : IHistorySource
{
    public HistoryArea Area => HistoryArea.Simulation;

    /// <summary>True in debug builds, the only builds that list simulation records.</summary>
    public static bool Available =>
#if DEBUG
        true;
#else
        false;
#endif

    public IReadOnlyList<HistoryEntry> Read()
    {
        return model.Operations.Select(r =>
        {
            var line = text["HistKindSimulation"] + ": " + $"{text[r.Before ? "Enabled" : "Disabled"]} {text["ArrowGlyph"]} {text[r.Desired ? "Enabled" : "Disabled"]}";
            var applied = r.State is OperationState.Applied or OperationState.PendingRestart;
            var key = "Code_" + r.Code.Replace('.', '_');
            var can = applied && !r.CompensationPrepared;
            return HistoryText.Entry(HistoryArea.Simulation, r.Id, r.CreatedAtUtc, applied ? HistoryResult.Applied : r.State == OperationState.Reverted ? HistoryResult.Undone : HistoryResult.Failed, line,
                text.Has(key) ? text[key] : text["UnknownResult"], r.Code, false, false, can, can ? null : applied ? "HistUndoNotPossible" : null);
        }).ToList();
    }

    public async Task<HistoryUndoOutcome> UndoAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        await model.RevertAsync(entry.Id).ConfigureAwait(true);
        return model.Operations.FirstOrDefault(o => o.Id == entry.Id)?.State == OperationState.Reverted ? HistoryUndoOutcome.Success() : HistoryUndoOutcome.Failure("not_reverted");
    }
}
