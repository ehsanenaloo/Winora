// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>
/// The Services page: every service and driver of this PC with search, filters and sort, and for a service that is not locked the actions Start, Stop, Restart and a startup type picker
/// (DEC-IW-270). Each change shows what will change, asks once, is recorded before it is made, is read back afterwards, and a startup type change can be undone while nothing else changed it.
/// Services that Windows or Winora need, drivers and boot services are locked with the reason shown. The machine definitions give the Recommended group and its risk.
/// </summary>
public sealed class ServicesViewModel : DiscoveryPageViewModel<ServiceInfo>
{
    private const int HistoryLimit = 30;
    private const int NamedLimit = 5;
    private readonly ServiceManagerService service;
    private readonly IReviewPrompt prompt;
    private readonly IElevationState elevation;
    private readonly Func<IReadOnlyDictionary<string, ServiceRecommendation>> recommendations;
    private readonly ConcurrentDictionary<string, ServiceInfo> byKey = new();
    private readonly ServiceActionsPanel actions;
    private readonly ServiceNoticePanel notices;
    private readonly ServiceHistoryPanel history;
    public PageHistoryModel HistoryList { get; }
    public IReadOnlyList<ServiceHistoryRow> HistoryRows { get; private set; } = [];
    private readonly RelayCommand<ServiceHistoryRow> revert;
    private Task work = Task.CompletedTask;
    private bool busy;

    public ServicesViewModel(LocalizationService text, SimulationModel model, ErrorPresenter errors, InventoryModel inventory, ServiceManagerService service, IReviewPrompt prompt,
        IElevationState elevation, Func<IReadOnlyDictionary<string, ServiceRecommendation>>? recommendations = null)
        : base("Services", IconKey.Wrench24, text, model, errors, inventory.Services)
    {
        this.service = service; this.prompt = prompt; this.elevation = elevation;
        this.recommendations = recommendations ?? (static () => new Dictionary<string, ServiceRecommendation>());
        var mode = AddChoice("FilterStartMode", i => i.StartMode);
        var state = AddChoice("FilterState", i => i.RunState);
        AddChoice("FilterKind", i => i.Kind);
        var safety = AddChoice("V2SvFilterSafety", i => service.Policy.Classify(i).Class);
        var recommended = AddYesNoChoice("V2SvFilterRecommended", i => Recommendation(i) is not null);
        AddToggle("FilterHideDrivers", i => i.Kind is ServiceKind.KernelDriver or ServiceKind.FileSystemDriver);
        ChoiceFilter[] managed = [mode, state, safety, recommended];
        AddChoiceChip("all", "DiscFilterAll", managed, null, null);
        AddChoiceChip("running", "Enum_ServiceRunState_Running", managed, state, nameof(ServiceRunState.Running));
        AddChoiceChip("stopped", "Enum_ServiceRunState_Stopped", managed, state, nameof(ServiceRunState.Stopped));
        AddChoiceChip("automatic", "Enum_ServiceStartMode_Automatic", managed, mode, nameof(ServiceStartMode.Automatic));
        AddChoiceChip("disabled", "Enum_ServiceStartMode_Disabled", managed, mode, nameof(ServiceStartMode.Disabled));
        AddChoiceChip("recommended", "V2SvChipRecommended", managed, recommended, "yes");
        AddChoiceChip("protected", "V2SvChipProtected", managed, safety, nameof(ServicePolicyClass.Protected));
        AddSort("SortName", i => i.DisplayName);
        AddSort("SortStartMode", i => i.StartMode);
        AddSort("SortState", i => i.RunState);
        revert = new(row => { if (row is not null) Track(RevertAsync(row)); });
        actions = new(text, Perform, () => !busy);
        notices = new(text, elevation) { FeedbackSource = () => Feedback };
        HistoryList = new(text, HistoryArea.Services, () => OpenHistory);
        history = new(HistoryList);
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectedRow)) UpdatePanel(); };
    }

    public override bool ShowReadOnlyNotice => false;

    public override object? BannerPanel => notices;
    public override string Intro => Text["V2SvIntro"];
    public override object? FooterPanel => history;

    public bool IsElevated => elevation.IsElevated;
    /// <summary>The state of the Start, Stop, Restart and startup type actions for the selected service (the details dialog offers them).</summary>
    public ServiceActionsPanel Actions => actions;

    protected override IReviewPrompt? DetailsHost => prompt;

    protected override DetailsDialog BuildDetails(DiscoveryRow row)
    {
        var fields = row.BuildFields().ToList();
        if (actions.WarningText.Length > 0) fields.Add(new(Text["DetailsNote"], actions.WarningText));
        return DetailsDialogBuilder.Build(Text, CopyText, row.Title, fields, row.Badges, actions: DetailActions(row));
    }

    /// <summary>Start, Stop, Restart and the startup types: the actions that used to sit in the details pane. Each goes through the same preview, confirmation and journal as before.</summary>
    protected override IReadOnlyList<DetailsAction> DetailActions(DiscoveryRow row)
    {
        if (!actions.CanChange || SelectedItem is not { } item) return [];
        string Named(string key) => $"{Text[key]}, {item.DisplayName}";
        List<DetailsAction> list =
        [
            new(Text["V2SvStart"], Named("V2SvStart"), Text["V2SvStartHelp"], () => Perform(ServiceChangeKind.Start), IsPrimary: true, IsEnabled: actions.CanPerform(ServiceChangeKind.Start)),
            new(Text["V2SvStop"], Named("V2SvStop"), Text["V2SvStopHelp"], () => Perform(ServiceChangeKind.Stop), IsEnabled: actions.CanPerform(ServiceChangeKind.Stop)),
            new(Text["V2SvRestart"], Named("V2SvRestart"), Text["V2SvRestartHelp"], () => Perform(ServiceChangeKind.Restart), IsEnabled: actions.CanPerform(ServiceChangeKind.Restart)),
        ];
        foreach (var mode in ServiceManagerService.StartModes)
        {
            var label = Text.Format("DetailsSetStartup", Text.Enum(mode));
            list.Add(new(label, $"{label}, {item.DisplayName}", Text["V2SvChangeTypeHelp"], () => { actions.Pick(mode); return Perform(ServiceChangeKind.SetStartMode); }, IsEnabled: actions.CanPickMode(mode)));
        }
        return list;
    }

    private Task Perform(ServiceChangeKind kind) { var task = PerformAsync(kind); Track(task); return task; }

    private bool Busy { get => busy; set { if (busy == value) return; busy = value; actions.Notify(); } }

    private ServiceInfo? SelectedItem => SelectedRow is { } row && byKey.TryGetValue(row.Key, out var item) ? item : null;

    private void Track(Task task)
    {
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // in production nobody awaits the page work
        work = Task.WhenAll(work, task);
    }

    private static string NameOf(ServiceInfo i) => ServiceSafetyPolicy.ShortName(i.StableId);

    private ServiceRecommendation? Recommendation(ServiceInfo i) => recommendations().TryGetValue(NameOf(i), out var r) ? r : null;

    public override void Activate()
    {
        base.Activate();
        Track(RefreshHistoryAsync());
    }

    public override async Task WhenIdleAsync()
    {
        await base.WhenIdleAsync();
        while (true)
        {
            var current = work;
            await current;
            if (ReferenceEquals(current, work)) break;
        }
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        notices.Refresh(); actions.Refresh(); history.Refresh();
        UpdatePanel();
        Track(RefreshHistoryAsync());
    }

    protected override IEnumerable<SummaryStat> BuildSummary(IReadOnlyList<ServiceInfo> items)
    {
        yield return new(Text.Number(items.Count), Text["V2MaStatServices"]);
        yield return new(Text.Number(items.Count(i => i.RunState == ServiceRunState.Running)), Text["Enum_ServiceRunState_Running"]);
        yield return new(Text.Number(items.Count(i => i.StartMode is ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed)), Text["V2MaStatAutoStart"]);
        yield return new(Text.Number(items.Count(i => i.StartMode == ServiceStartMode.Disabled)), Text["Enum_ServiceStartMode_Disabled"]);
    }

    protected override IEnumerable<string?> SearchFields(ServiceInfo i) => [i.DisplayName, i.StableId, i.ImagePath, i.Account, i.Description];

    // ---- rows ----

    protected override DiscoveryRow BuildRow(ServiceInfo i)
    {
        byKey[i.StableId] = i;
        var info = service.Policy.Classify(i);
        List<BadgeModel> badges = [new(Text.Enum(i.RunState), Run(i.RunState)), new(Text.Enum(i.StartMode), Start(i.StartMode)), Neutral(Text.Enum(i.Kind))];
        if (info.Class == ServicePolicyClass.Protected) badges.Add(new(Text["V2SvBadgeProtected"], Tone.Success));
        else if (info.IsLocked) badges.Add(Neutral(Text["V2SvBadgeLocked"]));
        if (Recommendation(i) is { } rec)
        {
            badges.Add(new(Text["V2SvBadgeRecommended"], Tone.Info));
            badges.Add(new(Text.Format("V2SvBadgeRisk", Text["V2SvRisk_" + rec.Risk]), rec.Risk == RiskLevel.Low ? Tone.Neutral : Tone.Caution));
        }
        return new(i.StableId, i.DisplayName, NameOf(i), true, i.Description ?? "", badges, () => Fields(i, info));
    }

    private IReadOnlyList<DetailField> Fields(ServiceInfo i, ServicePolicyInfo info)
    {
        var fields = new List<DetailField>
        {
            new(Text["FieldDisplayName"], i.DisplayName),
            new(Text["FieldServiceName"], NameOf(i), true),
            new(Text["FieldState"], Text.Enum(i.RunState)),
            new(Text["FieldStartMode"], Text.Enum(i.StartMode)),
            new(Text["FieldKind"], Text.Enum(i.Kind)),
            new(Text["FieldAccount"], Or(i.Account), true),
            new(Text["FieldImagePath"], Or(i.ImagePath), true),
            new(Text["FieldSafety"], PolicyText(info)),
        };
        if (!string.IsNullOrWhiteSpace(i.Description)) fields.Insert(0, new(Text["FieldDescription"], i.Description, false, DetailGroup.Description));
        if (Recommendation(i) is { } rec) fields.Add(new(Text["V2SvFieldSuggested"], RecommendationText(rec)));
        return fields;
    }

    private string PolicyText(ServicePolicyInfo info) => info.IsLocked
        ? Text.Format("V2SvPolicyLocked", Text["V2SvReason_" + info.Reason])
        : Text["V2SvPolicy_" + info.Class];

    private string RecommendationText(ServiceRecommendation rec) => Text.Format("V2SvSuggested", Text.Enum(rec.Suggested), Text["V2SvRisk_" + rec.Risk]);

    private static Tone Run(ServiceRunState s) => s switch
    {
        ServiceRunState.Running => Tone.Success,
        ServiceRunState.Stopped => Tone.Neutral,
        ServiceRunState.StartPending or ServiceRunState.StopPending or ServiceRunState.ContinuePending or ServiceRunState.PausePending => Tone.Info,
        _ => Tone.Caution,
    };
    private static Tone Start(ServiceStartMode m) => m switch
    {
        ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed or ServiceStartMode.Boot or ServiceStartMode.System => Tone.Info,
        ServiceStartMode.Manual or ServiceStartMode.Disabled => Tone.Neutral,
        _ => Tone.Caution,
    };

    private void UpdatePanel()
    {
        var item = SelectedItem;
        if (item is null) { actions.Update(null, null, elevation.IsElevated, "", "", "", null); return; }
        var info = service.Policy.Classify(item);
        var rec = Recommendation(item);
        // The picker starts on the suggestion when the service is a recommended one that is not there yet, otherwise on the current type.
        ServiceStartMode? pre = rec is { } r && r.Suggested != item.StartMode ? r.Suggested : ServiceManagerService.StartModes.Contains(item.StartMode) ? item.StartMode : null;
        var warning = info.IsLocked ? "" : info.Class == ServicePolicyClass.Windows ? Text["V2SvWarnWindows"] : "";
        actions.Update(item, info, elevation.IsElevated, PolicyText(info), warning, rec is null ? "" : RecommendationText(rec), pre);
    }

    // ---- actions ----

    private string CodeText(string code, IReadOnlyList<ServiceRef>? names = null)
    {
        var key = "V2SvCode_" + code.Replace('.', '_');
        if (!Text.Has(key)) return Text["V2SvCode_service_failed"];
        return Text.Format(key, NameList(names));
    }

    private string NameList(IReadOnlyList<ServiceRef>? names)
    {
        if (names is null or { Count: 0 }) return Text["V2SvSomeServices"];
        var shown = string.Join(", ", names.Take(NamedLimit).Select(n => n.DisplayName));
        return names.Count > NamedLimit ? shown + " " + Text.Format("V2SvAndMore", Text.Number((names.Count - NamedLimit))) : shown;
    }

    private async Task PerformAsync(ServiceChangeKind kind)
    {
        if (SelectedItem is not { } item || busy || !elevation.IsElevated) return;
        var name = NameOf(item);
        var mode = kind == ServiceChangeKind.SetStartMode ? actions.PickedMode : null;
        var tried = false;
        var run = ActivityRun.None;
        Busy = true; notices.Show(null, false);
        try
        {
            var preview = await Task.Run(() => service.Preview(new(kind, name, mode))).ConfigureAwait(true);
            if (preview.IsRefused || !preview.WillChange)
            {
                notices.Show(CodeText(preview.RefusalCode ?? ServiceCode.AlreadySet, preview.BlockedBy), true);
                return;
            }
            if (!await prompt.ConfirmActionAsync(Confirmation(kind, item, preview)).ConfigureAwait(true)) return;
            run = Runs.Begin(Text.Format("ACRunApply", Title), true);
            if (!run.Started) return;
            tried = true;
            var result = await service.ApplyAsync(new(kind, name, mode, preview.Fingerprint)).ConfigureAwait(true);
            Report(kind, result);
            run.Finish(result.Ok ? RunResult.Ok : RunResult.Error, result.Ok ? Text.Format("V2SvApplied_" + (result.Change?.Kind ?? kind), result.Change?.DisplayName ?? "") : CodeText(result.Code, result.Names));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            notices.Show(Text["ActionFailedUnexpected"], true);
            tried = true;
            run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title));
        }
        finally { run.Dispose(); Busy = false; }
        if (!tried) return;
        await ScanAsync().ConfigureAwait(true);
        await RefreshHistoryAsync().ConfigureAwait(true);
    }

    private ActionConfirmation Confirmation(ServiceChangeKind kind, ServiceInfo item, ServicePreview preview)
    {
        var name = NameOf(item);
        List<string> lines = [Text.Format("V2SvLineService", name)];
        if (!string.IsNullOrWhiteSpace(item.ImagePath)) lines.Add(Text.Format("V2SvLinePath", item.ImagePath));
        if (!string.IsNullOrWhiteSpace(item.Account)) lines.Add(Text.Format("V2SvLineAccount", item.Account));
        var note = kind switch
        {
            ServiceChangeKind.Start => "V2SvNoteStart",
            ServiceChangeKind.Stop => "V2SvNoteStop",
            ServiceChangeKind.Restart => "V2SvNoteRestart",
            _ => preview.AfterMode == ServiceStartMode.Disabled && preview.State == ServiceRunState.Running ? "V2SvNoteDisableRunning" : "V2SvNoteMode",
        };
        var warning = preview.Policy.Class == ServicePolicyClass.Windows ? Text["V2SvWarnWindows"] + " " + Text[note] : Text[note];
        ConfirmChange change = kind switch
        {
            ServiceChangeKind.SetStartMode => new(preview.DisplayName, Text.Enum(preview.BeforeMode), Text.Enum(preview.AfterMode ?? preview.BeforeMode), name),
            ServiceChangeKind.Start => new(preview.DisplayName, Text.Enum(ServiceRunState.Stopped), Text.Enum(ServiceRunState.Running), name),
            ServiceChangeKind.Stop => new(preview.DisplayName, Text.Enum(ServiceRunState.Running), Text.Enum(ServiceRunState.Stopped), name),
            _ => new(preview.DisplayName, null, null, name),
        };
        var risk = preview.Risk;
        if (kind == ServiceChangeKind.SetStartMode && Recommendation(item) is { } rec && rec.Suggested == preview.AfterMode && rec.Risk > risk) risk = rec.Risk;
        return new(Text["V2SvConfirm_" + kind + "_Title"], Text.Format("V2SvConfirm_" + kind + "_Body", preview.DisplayName), lines, warning, Text["V2SvConfirm_" + kind + "_Primary"])
        {
            Changes = [change], Risk = risk, Restart = false,
            Undo = kind == ServiceChangeKind.SetStartMode ? ConfirmUndo.WhileUnchanged : ConfirmUndo.No,
        };
    }

    private void Report(ServiceChangeKind kind, ServiceApplyResult result)
    {
        if (result.Ok) notices.Show(Text.Format("V2SvApplied_" + (result.Change?.Kind ?? kind), result.Change?.DisplayName ?? "") + (result.RecordLost ? " " + Text["RecordLostNote"] : ""), result.RecordLost);
        else notices.Show(CodeText(result.Code, result.Names), true);
    }

    // ---- history ----

    private async Task RefreshHistoryAsync()
    {
        IReadOnlyList<ServiceChangeRecord> records;
        IReadOnlyList<bool> revertable;
        try
        {
            (records, revertable) = await Task.Run(() =>
            {
                var list = service.History(HistoryLimit);
                return (list, elevation.IsElevated ? service.CanRevertAll(list) : list.Select(_ => false).ToList());
            }).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException) { records = []; revertable = []; } // a live lookup (service control or Task Scheduler) can fail in other ways than I/O
        var rows = new List<ServiceHistoryRow>();
        var shown = new List<HistoryRowViewModel>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            rows.Add(new(r, Describe(r.Change), r.State is { } s ? Text.Enum(s) : Text["TaskInterrupted"], Text.When(r.BeganUtc.ToLocalTime(), "g"), revertable[i], Text["V2SvUndo"], revert));
            var h = rows[^1];
            shown.Add(HistoryList.Row(r.Change.Id, r.BeganUtc, r.State == ServiceChangeState.Applied ? (records.Any(o => o.Change.RevertOf == r.Change.Id && o.State == ServiceChangeState.Applied) ? HistoryResult.Undone : HistoryResult.Applied) : r.State == ServiceChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed, h.Description, h.CanRevert, h.RevertCommand, h, new(h.UndoLabel, Text["V2SvUndoHelp"])));
        }
        HistoryRows = rows;
        HistoryList.Set(shown);
        history.Refresh();
    }

    public string Describe(ServiceChange c) => Text.Format("V2SvHistory_" + c.Kind, c.DisplayName, Text.Enum(c.BeforeMode ?? ServiceStartMode.Unknown), Text.Enum(c.AfterMode ?? ServiceStartMode.Unknown))
        + (c.RevertOf is null ? "" : " " + Text["V2SvUndoSuffix"]);

    private async Task RevertAsync(ServiceHistoryRow row)
    {
        if (busy) return;
        var confirmation = new ActionConfirmation(Text["V2SvUndoTitle"], Text["V2SvUndoBody"], [row.Description], Text["V2SvNoteMode"], Text["V2SvUndo"])
        {
            Changes = [new ConfirmChange(row.Description)], Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, Restart = false,
        };
        Busy = true; notices.Show(null, false);
        var run = ActivityRun.None;
        try
        {
            if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
            run = Runs.Begin(Text.Format("ACRunRevert", Title), true);
            if (!run.Started) return;
            var result = await service.RevertAsync(row.Record.Change.Id).ConfigureAwait(true);
            Report(row.Record.Change.Kind, result);
            run.Finish(result.Ok ? RunResult.Ok : RunResult.Warning, null, quiet: result.Ok);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            notices.Show(Text["ActionFailedUnexpected"], true);
            run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title));
        }
        finally { run.Dispose(); Busy = false; }
        await ScanAsync().ConfigureAwait(true);
        await RefreshHistoryAsync().ConfigureAwait(true);
    }
}
