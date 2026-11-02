// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>
/// The Scheduled Tasks page: one card per task with its icon, state, path, triggers and command, an On/Off switch, Details, Run or Stop, and Delete. The page has search, quick
/// filters, sort and a Hide Microsoft tasks switch that is on by default and remembered. Each change shows what will change, asks once, is recorded before it is made, is read back afterwards
/// and can be undone while nothing else changed the task. Tasks that keep Windows updated and safe are protected: their controls stay visible but disabled, with the reason as a tooltip.
/// A delete stores a copy of the task first so it can be restored from the history.
/// </summary>
public sealed class ScheduledTasksViewModel : DiscoveryPageViewModel<ScheduledTaskItem>
{
    private const int HistoryLimit = 30;
    private const int MaxTriggerChips = 3;
    private const int CommandDisplayLimit = 260;
    private const int CommandTipLimit = 1500;
    public const string HideMicrosoftKey = "tasks.hideMicrosoft";
    private readonly ScheduledTaskService service;
    private readonly IReviewPrompt prompt;
    private readonly IElevationState elevation;
    private readonly IAppIconSource? icons;
    private readonly Func<string, bool> copy;
    private readonly ToggleFilter hideMicrosoft;
    private readonly ConcurrentDictionary<string, TaskRow> rowsByKey = new();
    private readonly TaskNoticePanel notices;
    private readonly TaskHistoryPanel history;
    public PageHistoryModel HistoryList { get; }
    public IReadOnlyList<TaskHistoryRow> HistoryRows { get; private set; } = [];
    private readonly RelayCommand<TaskHistoryRow> revert;
    private Task work = Task.CompletedTask;
    private bool busy;
    private string? pendingKey;

    public ScheduledTasksViewModel(LocalizationService text, SimulationModel model, ErrorPresenter errors, ScheduledTaskService service, IReviewPrompt prompt, IElevationState elevation,
        IAppIconSource? icons = null, IPagePreferences? preferences = null, Func<string, bool>? copy = null)
        : base("ScheduledTasks", IconKey.CalendarClock24, text, model, errors, new InventorySlot<ScheduledTaskItem>(service.ListAsync))
    {
        this.service = service; this.prompt = prompt; this.elevation = elevation; this.icons = icons;
        this.copy = copy ?? HostServices.Clipboard.TrySetText;
        var state = AddChoice("FilterState", i => i.Entry.State);
        AddChoice("FilterTaskSafety", i => i.Policy.Class);
        var advice = AddChoice("FilterTaskAdvice", i => i.Policy.Risk);
        var logon = AddYesNoChoice("FilterRunsAtLogon", i => i.Entry.RunsAtLogonOrBoot);
        hideMicrosoft = AddHiddenToggle("FilterHideMicrosoft", i => ScheduledTaskPolicy.IsMicrosoft(i.Entry.Path, i.Entry.Author));
        hideMicrosoft.IsOn = preferences?.Read(HideMicrosoftKey) != "0";
        hideMicrosoft.Changed += (_, _) => preferences?.Write(HideMicrosoftKey, hideMicrosoft.IsOn ? "1" : "0");
        ChoiceFilter[] managed = [state, advice, logon];
        AddChoiceChip("all", "DiscFilterAll", managed, null, null);
        AddChoiceChip("ready", "Enum_TaskRunState_Ready", managed, state, nameof(TaskRunState.Ready));
        AddChoiceChip("disabled", "Enum_TaskRunState_Disabled", managed, state, nameof(TaskRunState.Disabled));
        AddChoiceChip("logon", "BadgeLogonBoot", managed, logon, "yes");
        AddChoiceChip("recommended", "BadgeTaskRecommended", managed, advice, nameof(TaskRisk.Recommended));
        AddSort("SortName", i => i.Entry.Name);
        AddSort("SortTaskPath", i => i.Entry.Path);
        AddSort("SortLastRun", i => i.Entry.LastRunUtc);
        AddSort("SortNextRun", i => i.Entry.NextRunUtc);
        AddSort("SortState", i => i.Entry.State);
        revert = new(row => { if (row is not null) Track(RevertAsync(row)); });
        notices = new(text, elevation) { FeedbackSource = () => Feedback };
        HistoryList = new(text, HistoryArea.Tasks, () => OpenHistory);
        history = new(HistoryList);
    }

    public override bool ShowReadOnlyNotice => false;
    public override bool RowsHaveControls => true;
    public override ToggleFilter? InlineToggle => hideMicrosoft;

    /// <summary>Whether Microsoft tasks are hidden. On by default and remembered.</summary>
    public bool HideMicrosoft { get => hideMicrosoft.IsOn; set => hideMicrosoft.IsOn = value; }

    protected override IEnumerable<SummaryStat> BuildSummary(IReadOnlyList<ScheduledTaskItem> items)
    {
        yield return new(Text.Number(items.Count), Text["V2MaStatTasks"]);
        yield return new(Text.Number(items.Count(i => i.Entry.State is TaskRunState.Ready or TaskRunState.Running)), Text["V2MaStatActive"]);
        yield return new(Text.Number(items.Count(i => i.Entry.State == TaskRunState.Disabled)), Text["Enum_TaskRunState_Disabled"]);
        yield return new(Text.Number(items.Count(i => i.Entry.RunsAtLogonOrBoot)), Text["BadgeLogonBoot"]);
    }

    public override object? BannerPanel => notices;
    public override string Intro => Text["TaskIntro"];
    public override object? FooterPanel => history;

    /// <summary>Run or Stop and Delete in the details dialog: the row buttons sit in a list that Tab passes over, so this is their keyboard path.</summary>
    protected override IReadOnlyList<DetailsAction> DetailActions(DiscoveryRow row)
    {
        if (row is not TaskRow task) return [];
        List<DetailsAction> list = [];
        if (task.RunStopCommand.CanExecute(null)) list.Add(new(task.RunStopLabel, task.RunStopName, task.RunStopTip, () => { task.RunStopCommand.Execute(null); return Task.CompletedTask; }));
        if (task.DeleteCommand.CanExecute(null)) list.Add(new(task.DeleteLabel, task.DeleteName, task.DeleteTip, () => { task.DeleteCommand.Execute(null); return Task.CompletedTask; }, IsDanger: true));
        return list;
    }

    public override bool ToggleRow(DiscoveryRow row)
    {
        if (row is not TaskRow { CanSwitch: true } task) return false;
        task.IsOn = !task.IsOn;
        return true;
    }

    public bool IsElevated => elevation.IsElevated;

    private bool Busy { get => busy; set { if (busy == value) return; busy = value; NotifyRows(); } }

    private void NotifyRows() { foreach (var row in rowsByKey.Values) row.Notify(); }

    private void Track(Task task)
    {
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // in production nobody awaits the page work
        work = Task.WhenAll(work, task);
    }

    public override void Activate()
    {
        base.Activate();
        Track(RefreshHistoryAsync());
    }

    /// <summary>Selects the task with this path, now when the list is shown, otherwise as soon as the next list arrives. A Microsoft task is shown even while Microsoft tasks are hidden. A path that is not in the list is ignored.</summary>
    public void SelectTask(string path)
    {
        if (ScheduledTaskPolicy.IsMicrosoft(path, null) && hideMicrosoft.IsOn) hideMicrosoft.IsOn = false;
        pendingKey = RowKeyOf(path);
        if (SelectRowByKey(pendingKey)) pendingKey = null;
    }

    protected override void OnResultApplied(DiscoveryResult<ScheduledTaskItem> value)
    {
        if (pendingKey is { } key) { SelectRowByKey(key); pendingKey = null; }
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
        notices.Refresh(); history.Refresh();
        Track(RefreshHistoryAsync());
    }

    // ---- rows ----

    private static string RowKeyOf(string path) => "task:" + path.ToLowerInvariant();
    private static string RowKey(ScheduledTaskEntry e) => RowKeyOf(e.Path);

    protected override IEnumerable<string?> SearchFields(ScheduledTaskItem i) =>
        [i.Entry.Name, i.Entry.Path, i.Entry.Author, i.Entry.Description, i.Entry.RunAs, .. i.Entry.Actions.Select(a => a.Command), .. i.Entry.Actions.Select(a => a.Arguments)];

    protected override DiscoveryRow BuildRow(ScheduledTaskItem item)
    {
        var e = item.Entry;
        var key = RowKey(e);
        List<BadgeModel> badges = [new(Text.Enum(e.State), State(e.State))];
        switch (item.Policy.Class)
        {
            case TaskPolicyClass.Protected: badges.Add(new(Text["BadgeTaskProtected"], Tone.Success)); break;
            case TaskPolicyClass.WindowsUnlisted: badges.Add(Neutral(Text["BadgeTaskLocked"])); break;
        }
        if (item.Policy.Risk == TaskRisk.Recommended) badges.Add(new(Text["BadgeTaskRecommended"], Tone.Info));
        else if (item.Policy.Risk == TaskRisk.ReducesDiagnostics) badges.Add(new(Text["BadgeTaskReducesDiagnostics"], Tone.Caution));
        if (e.RunsAtLogonOrBoot) badges.Add(new(Text["BadgeLogonBoot"], Tone.Info));
        if (e.Hidden) badges.Add(Neutral(Text["BadgeHidden"]));
        if (ScheduledTaskPolicy.IsMicrosoft(e.Path, e.Author)) badges.Add(Neutral(Text["BadgeMicrosoft"]));
        var source = new DiscoveryRow(key, e.Name, e.Path, true, e.Author ?? "", badges, () => Fields(item));
        var row = new TaskRow(source, PartsOf(item));
        rowsByKey[key] = row;
        return row;
    }

    private TaskRowParts PartsOf(ScheduledTaskItem item)
    {
        var e = item.Entry;
        var running = e.State == TaskRunState.Running;
        var switchKind = e.Enabled ? ScheduledTaskChangeKind.Disable : ScheduledTaskChangeKind.Enable;
        var runKind = running ? ScheduledTaskChangeKind.Stop : ScheduledTaskChangeKind.Run;
        var switchBlock = Block(switchKind, item);
        var runBlock = Block(runKind, item);
        var deleteBlock = Block(ScheduledTaskChangeKind.Delete, item);
        var runLabel = Text[running ? "TaskStop" : "TaskRun"];
        var command = CommandOf(e);
        return new()
        {
            Item = item,
            Icon = new AppIconLoader(icons, IconKeyOf(e)),
            StateText = Text.Enum(e.State),
            StateTone = StateChip(e.State),
            TriggerChips = TriggerChips(e),
            CommandLine = Cap(command, CommandDisplayLimit),
            CommandTip = Cap(command, CommandTipLimit),
            CommandIsLtr = e.Actions.Count == 0 || e.Actions[0].Kind == "exec",
            PolicyChip = item.Policy.Class switch
            {
                TaskPolicyClass.Protected => Text["BadgeTaskProtected"],
                TaskPolicyClass.WindowsUnlisted => Text["BadgeTaskLocked"],
                _ => "",
            },
            IsOn = e.Enabled,
            OnText = Text["V2StOn"],
            OffText = Text["V2StOff"],
            SwitchName = Text.Format("TaskSwitchName", e.Name),
            CanSwitch = switchBlock is null,
            SwitchTip = switchBlock is null ? Text[e.Enabled ? "TaskDisableHelp" : "TaskEnableHelp"] : BlockText(switchBlock),
            RunStopSymbol = running ? IconKey.Stop24 : IconKey.Play24,
            RunStopLabel = runLabel,
            RunStopTip = runBlock is null ? runLabel : BlockText(runBlock),
            DeleteLabel = Text["TaskDelete"],
            DeleteTip = deleteBlock is null ? Text["TaskDeleteHelp"] : BlockText(deleteBlock),
            InfoLabel = Text["TaskInfo"],
            CanAct = () => elevation.IsElevated && !busy,
            CanRunStop = () => elevation.IsElevated && !busy && runBlock is null,
            CanDelete = () => elevation.IsElevated && !busy && deleteBlock is null,
            Switched = (row, on) => { var task = SwitchAsync(row, on); Track(task); return task; },
            RunStop = row => Track(PerformAsync(runKind, row.Item)),
            Delete = row => Track(PerformAsync(ScheduledTaskChangeKind.Delete, row.Item)),
            Info = row => Track(ShowDetailsCommand.ExecuteAsync(row)),
        };
    }

    /// <summary>Why the action is not offered for this task: a missing administrator right, the safety policy or the state of the task. Null when it is.</summary>
    private string? Block(ScheduledTaskChangeKind kind, ScheduledTaskItem item) => !elevation.IsElevated ? "admin" : ScheduledTaskService.Availability(kind, item);

    private string BlockText(string code) => code == "admin" ? Text["TaskNeedsAdminTitle"] : CodeText(code);

    private async Task SwitchAsync(TaskRow row, bool on)
    {
        var applied = await PerformAsync(on ? ScheduledTaskChangeKind.Enable : ScheduledTaskChangeKind.Disable, row.Item).ConfigureAwait(true);
        if (!applied) row.Reset();
    }

    private static Tone StateChip(TaskRunState s) => s switch
    {
        TaskRunState.Ready => Tone.Success,
        TaskRunState.Running => Tone.Info,
        TaskRunState.Disabled => Tone.Neutral,
        _ => Tone.Caution,
    };

    /// <summary>The first action of the task as text: a command with its arguments, or the plain name of another kind of action. Never raw task XML.</summary>
    private string CommandOf(ScheduledTaskEntry e) => e.Actions.Count == 0 ? "" : ActionText(e.Actions[0]).Trim();

    private static string Cap(string value, int limit) => value.Length <= limit ? value : value[..limit].TrimEnd() + "…";

    private static AppIconKey? IconKeyOf(ScheduledTaskEntry e)
    {
        if (e.Actions.FirstOrDefault(a => a.Kind == "exec")?.Command is not { Length: > 0 } command) return null;
        try
        {
            var path = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"').Trim());
            return Path.IsPathFullyQualified(path) ? new AppIconKey(AppIconKind.File, path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>One chip per different trigger, in order: only triggers that are on, the same text once, at most three and then "+N".</summary>
    private IReadOnlyList<string> TriggerChips(ScheduledTaskEntry e)
    {
        var texts = e.Triggers.Where(t => t.Enabled).Select(ChipText).Distinct().ToList();
        if (texts.Count <= MaxTriggerChips) return texts;
        var shown = texts.Take(MaxTriggerChips).ToList();
        var more = "+" + (texts.Count - MaxTriggerChips).ToString(Text.Culture);
        shown.Add(Text.Language == "fa" ? DigitConverter.ToPersian(more) : more);
        return shown;
    }

    /// <summary>The trigger in plain words, with its time of day when it has one ("Daily at 9:00").</summary>
    private string ChipText(TaskTriggerInfo t)
    {
        if (t.At is { Length: > 0 } at && Text.Has("TaskChipAt_" + t.Kind)) return Text.Format("TaskChipAt_" + t.Kind, Digits(at));
        return TriggerText(t, withOff: false);
    }

    private string Digits(string value) => Text.Language == "fa" ? DigitConverter.ToPersian(value) : value;

    private IReadOnlyList<DetailField> Fields(ScheduledTaskItem item)
    {
        var e = item.Entry;
        var fields = new List<DetailField>
        {
            new(Text["FieldName"], e.Name),
            new(Text["FieldPath"], e.Path, true),
            new(Text["FieldState"], Text.Enum(e.State)),
            new(Text["FieldDescription"], Or(e.Description)),
            new(Text["FieldAuthor"], Or(e.Author)),
            new(Text["FieldRunAs"], Or(e.RunAs), true),
            new(Text["FieldRunLevel"], Text[e.RunsElevated ? "ValueRunLevelHighest" : "ValueRunLevelStandard"]),
            new(Text["FieldTriggers"], TriggersText(e)),
            new(Text["FieldActions"], ActionsText(e), true),
            new(Text["FieldLastRun"], When(e.LastRunUtc)),
            new(Text["FieldNextRun"], When(e.NextRunUtc)),
            new(Text["FieldLastResult"], ResultText(e.LastResult)),
            new(Text["FieldHidden"], YesNo(e.Hidden)),
            new(Text["FieldXmlSize"], Text.Format("TaskXmlSize", Text.Number(e.XmlLength))),
            new(Text["FieldSafety"], PolicyText(item.Policy)),
        };
        if (WarningText(item.Policy) is { Length: > 0 } warning) fields.Add(new(Text["FieldWarning"], warning));
        return fields;
    }

    private string TriggersText(ScheduledTaskEntry e) => e.Triggers.Count == 0 ? Text["ValueNone"] : string.Join(Environment.NewLine, e.Triggers.Select(t => TriggerText(t, withOff: true) + AtSuffix(t)));

    private string ActionsText(ScheduledTaskEntry e) => e.Actions.Count == 0 ? Text["ValueNone"] : string.Join(Environment.NewLine, e.Actions.Select(ActionText));

    private string AtSuffix(TaskTriggerInfo t) => t.At is { Length: > 0 } at ? " · " + Digits(at) : "";

    private string TriggerText(TaskTriggerInfo t, bool withOff)
    {
        var label = Text.Has("TaskTrigger_" + t.Kind) ? Text["TaskTrigger_" + t.Kind] : t.Kind;
        return t.Enabled || !withOff ? label : label + " " + Text["TaskTriggerOff"];
    }

    private string ActionText(TaskActionInfo a)
    {
        if (a.Kind == "exec") return string.IsNullOrWhiteSpace(a.Arguments) ? a.Command ?? "" : a.Command + " " + a.Arguments;
        return Text.Has("TaskAction_" + a.Kind) ? Text["TaskAction_" + a.Kind] : a.Kind;
    }

    private string ResultText(int? value)
    {
        if (value is not { } v) return Text["ValueNotRecorded"];
        var hex = "0x" + ((uint)v).ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        var key = "TaskResult_" + ((uint)v).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        return Text.Has(key) ? Text.Format("TaskResultFormat", hex, Text[key]) : hex;
    }

    private string PolicyText(TaskPolicyInfo policy) => policy.Class switch
    {
        TaskPolicyClass.KnownOptional => Text["TaskPolicy_KnownOptional_" + (policy.Risk == TaskRisk.ReducesDiagnostics ? "ReducesDiagnostics" : "Recommended")],
        _ => Text["TaskPolicy_" + policy.Class],
    };

    private string WarningText(TaskPolicyInfo policy) => policy.WarningKey is { } key && Text.Has("TaskWarn_" + key) ? Text["TaskWarn_" + key] : "";

    private static Tone State(TaskRunState s) => s switch
    {
        TaskRunState.Running => Tone.Success,
        TaskRunState.Ready => Tone.Info,
        TaskRunState.Disabled => Tone.Neutral,
        _ => Tone.Caution,
    };

    // ---- details dialog ----

    protected override IReviewPrompt? DetailsHost => prompt;

    /// <summary>The details of a task as the dialog shows them: name and state as tags; path, account and command as technical details; last run, next run and last result; author, triggers and the safety verdict.</summary>
    protected override DetailsDialog BuildDetails(DiscoveryRow row)
    {
        var item = ((TaskRow)row).Item;
        var e = item.Entry;
        DetailField[] fields =
        [
            new(Text["FieldPath"], e.Path, true),
            new(Text["FieldRunAs"], Or(e.RunAs), true),
            new(Text["FieldActions"], ActionsText(e), true),
            new(Text["FieldLastRun"], When(e.LastRunUtc), false, DetailGroup.Run),
            new(Text["FieldNextRun"], When(e.NextRunUtc), false, DetailGroup.Run),
            new(Text["FieldLastResult"], ResultText(e.LastResult), false, DetailGroup.Run),
            new(Text["FieldName"], e.Name),
            new(Text["FieldState"], Text.Enum(e.State)),
            new(Text["FieldAuthor"], Or(e.Author)),
            new(Text["FieldTriggers"], TriggersText(e)),
            new(Text["FieldSafety"], PolicyText(item.Policy) + (WarningText(item.Policy) is { Length: > 0 } w ? " " + w : "")),
        ];
        return DetailsDialogBuilder.Build(Text, copy, e.Name, fields, row.Badges, actions: DetailActions(row));
    }

    // ---- actions ----

    private string CodeText(string code)
    {
        var key = "TaskCode_" + code.Replace('.', '_');
        return Text.Has(key) ? Text[key] : Text["TaskCode_task_failed"];
    }

    /// <summary>Reviews and makes one change. True only when the change was made and read back.</summary>
    private async Task<bool> PerformAsync(ScheduledTaskChangeKind kind, ScheduledTaskItem item)
    {
        if (busy || !elevation.IsElevated) return false;
        var path = item.Entry.Path;
        var tried = false;
        var applied = false;
        var run = ActivityRun.None;
        Busy = true; notices.Show(null, false);
        try
        {
            var preview = await Task.Run(() => service.Preview(new(kind, path))).ConfigureAwait(true);
            if (preview.IsRefused || !preview.WillChange)
            {
                notices.Show(CodeText(preview.RefusalCode ?? ScheduledTaskCode.AlreadySet), true);
                return false;
            }
            if (!await prompt.ConfirmActionAsync(Confirmation(kind, item, preview)).ConfigureAwait(true)) return false;
            run = Runs.Begin(Text.Format("ACRunApply", Title), true);
            if (!run.Started) return false;
            tried = true;
            applied = Report(kind, await service.ApplyAsync(new(kind, path, preview.Fingerprint)).ConfigureAwait(true));
            run.Finish(applied ? RunResult.Ok : RunResult.Error, notices.Notice);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            notices.Show(Text["ActionFailedUnexpected"], true);
            tried = true;
            run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title));
        }
        finally { run.Dispose(); Busy = false; }
        if (!tried) return false;
        await ScanAsync().ConfigureAwait(true);
        await RefreshHistoryAsync().ConfigureAwait(true);
        return applied;
    }

    private ActionConfirmation Confirmation(ScheduledTaskChangeKind kind, ScheduledTaskItem item, ScheduledTaskPreview preview)
    {
        var e = item.Entry;
        var lines = new List<string> { Text.Format("TaskLinePath", e.Path) };
        if (e.Actions.FirstOrDefault(a => a.Kind == "exec") is { } exec) lines.Add(Text.Format("TaskLineCommand", Cap(ActionText(exec), CommandTipLimit)));
        if (!string.IsNullOrWhiteSpace(e.RunAs)) lines.Add(Text.Format("TaskLineRunAs", e.RunAs));
        if (kind is ScheduledTaskChangeKind.Enable or ScheduledTaskChangeKind.Disable)
            lines.Add(Text.Format("TaskLineChange", EnabledLabel(preview.BeforeEnabled == true), EnabledLabel(preview.AfterEnabled == true)));
        var warning = WarningText(item.Policy);
        if (warning.Length == 0) warning = Text[kind == ScheduledTaskChangeKind.Delete ? "TaskDeleteWarning" : kind == ScheduledTaskChangeKind.Run || kind == ScheduledTaskChangeKind.Stop ? "TaskRunStopNote" : "TaskUndoNote"];
        var change = kind is ScheduledTaskChangeKind.Enable or ScheduledTaskChangeKind.Disable
            ? new ConfirmChange(e.Name, EnabledLabel(preview.BeforeEnabled == true), EnabledLabel(preview.AfterEnabled == true), e.Path)
            : new ConfirmChange(e.Name, null, null, e.Path);
        var risk = kind is ScheduledTaskChangeKind.Delete || item.Policy.Risk == TaskRisk.ReducesDiagnostics ? RiskLevel.Medium : RiskLevel.Low;
        return new(Text["TaskConfirm_" + kind + "_Title"], Text.Format("TaskConfirm_" + kind + "_Body", e.Name), lines, warning, Text["TaskConfirm_" + kind + "_Primary"])
        {
            Changes = [change], Risk = risk, Restart = false,
            Undo = kind is ScheduledTaskChangeKind.Run or ScheduledTaskChangeKind.Stop ? ConfirmUndo.No : ConfirmUndo.WhileUnchanged,
        };
    }

    private bool Report(ScheduledTaskChangeKind kind, ScheduledTaskApplyResult result)
    {
        if (result.Ok) notices.Show(Text["TaskApplied_" + (result.Change?.Kind ?? kind)] + (result.RecordLost ? " " + Text["RecordLostNote"] : ""), result.RecordLost);
        else notices.Show(CodeText(result.Code), true);
        return result.Ok;
    }

    // ---- history ----

    private async Task RefreshHistoryAsync()
    {
        IReadOnlyList<ScheduledTaskChangeRecord> records;
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
        var rows = new List<TaskHistoryRow>();
        var shown = new List<HistoryRowViewModel>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            rows.Add(new(r, Describe(r.Change), r.State is { } s ? Text.Enum(s) : Text["TaskInterrupted"], Text.When(r.BeganUtc.ToLocalTime(), "g"), revertable[i],
                Text[r.Change.Kind == ScheduledTaskChangeKind.Delete ? "TaskRestore" : "TaskUndo"], revert));
            var h = rows[^1];
            shown.Add(HistoryList.Row(r.Change.Id, r.BeganUtc, r.State == ScheduledTaskChangeState.Applied ? (records.Any(o => o.Change.RevertOf == r.Change.Id && o.State == ScheduledTaskChangeState.Applied) ? HistoryResult.Undone : HistoryResult.Applied) : r.State == ScheduledTaskChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed, h.Description, h.CanRevert, h.RevertCommand, h, new(h.UndoLabel, Text["TaskUndoHelp"])));
        }
        HistoryRows = rows;
        HistoryList.Set(shown);
        history.Refresh();
    }

    private string Describe(ScheduledTaskChange c) => Text.Format("TaskHistory_" + c.Kind, Text.Name(c.Name)) + (c.RevertOf is null ? "" : " " + Text["TaskUndoSuffix"]);

    private async Task RevertAsync(TaskHistoryRow row)
    {
        if (busy) return;
        var restore = row.Record.Change.Kind == ScheduledTaskChangeKind.Delete;
        var confirmation = new ActionConfirmation(Text[restore ? "TaskRestoreTitle" : "TaskRevertTitle"], Text[restore ? "TaskRestoreBody" : "TaskRevertBody"], [row.Description], Text["TaskUndoNote"], Text[restore ? "TaskRestore" : "TaskUndo"])
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
