// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application.Activity;
using Winora.Domain;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;

namespace Winora.Desktop;

/// <summary>
/// One consent value as a row of the settings page: the global switch of a capability, its switch for desktop programs, or the switch of one app. The row owns no state: the page keeps
/// the staged choice, the review and the undo.
/// </summary>
public sealed class PermissionSettingRow : PlainSettingRow
{
    public const string AllowId = "allow", DenyId = "deny";
    private readonly PermissionsViewModel page;
    private readonly IReadOnlyList<RowChoice> choices;
    private readonly RelayCommand discard, undo;

    public PermissionSettingRow(LocalizationService text, PermissionsViewModel page, ConsentTarget target, string title, string description, string categoryKey, string categoryLabel, int categoryOrder,
        ConsentState current, PermissionLimit limit, string hint) : base(text)
    {
        this.page = page; Target = target; Title = title; Description = description; CategoryKey = categoryKey; CategoryLabel = categoryLabel; CategoryOrder = categoryOrder;
        Current = current; Limit = limit; CurrentHint = hint;
        choices = [new(AllowId, Text["PermState_Allow"], true), new(DenyId, Text["PermState_Deny"], false)];
        discard = new(() => page.Unstage(this), () => IsPending && !page.IsBusy);
        undo = new(() => page.UndoLatest(this), () => CanUndo);
    }

    public ConsentTarget Target { get; }
    public ConsentState Current { get; }
    public PermissionLimit Limit { get; }
    public override int CategoryOrder { get; }
    public override string Id => PermissionsViewModel.KeyOf(Target);
    public override string Title { get; }
    public override string Description { get; }
    public override string CategoryKey { get; }
    public override string CategoryLabel { get; }
    public override string CurrentHint { get; }
    /// <summary>A value of a form Winora does not write (neither Allow nor Deny) is shown and never changed.</summary>
    public override bool IsAvailable => Current != ConsentState.Other;
    public override bool IsManaged => Limit is PermissionLimit.PolicyForceAllow or PermissionLimit.PolicyForceDeny;
    public override string StatusText => IsAvailable ? "" : Text["PermState_Other"];
    public override string CurrentLabel => Text["PermState_" + Current];
    public override IReadOnlyList<RowChoice> Choices => choices;
    public override bool CanChoose => IsAvailable && !page.IsBusy;
    /// <summary>Allowing is the riskier way: apps may use the device or data again.</summary>
    public override RiskLevel Risk => page.StagedChoice(this) == PermissionChoice.Allow ? RiskLevel.Medium : RiskLevel.Low;

    public override RowChoice? SelectedChoice
    {
        get => page.StagedChoice(this) is { } staged ? ChoiceOf(staged) : Current switch { ConsentState.Allow => choices[0], ConsentState.Deny => choices[1], _ => null };
        set { if (value is not null) page.Stage(this, value.Id == AllowId ? PermissionChoice.Allow : PermissionChoice.Deny); OnPropertyChanged(); }
    }

    public RowChoice ChoiceOf(PermissionChoice choice) => choice == PermissionChoice.Allow ? choices[0] : choices[1];
    public override bool IsPending => page.StagedChoice(this) is not null;
    public override string ConditionalNote => Limit == PermissionLimit.None ? "" : Text["PermLimit_" + Limit];
    public override string TechnicalText => Target.Scope switch
    {
        ConsentScope.Desktop => Target.Capability + @"\" + ConsentAppNames.DesktopKey,
        ConsentScope.App => Target.Capability + @"\" + Target.AppKey,
        _ => Target.Capability,
    };
    public override ICommand DiscardCommand => discard;
    public override ICommand UndoCommand => undo;
    public override bool CanUndo => page.CanUndoLatest(this);
    public override string UndoHelp => Text[CanUndo ? "PermUndoHelp" : "SetRevertUnavailableHelp"];
    public override string SearchHaystack => string.Join('\n', Title, Description, CategoryLabel, CurrentLabel, Target.Capability);

    public void RefreshCommands() { discard.NotifyCanExecuteChanged(); undo.NotifyCanExecuteChanged(); }
}

/// <summary>
/// The Permissions page: what apps may use, per capability, for this account (decision DEC-IW-080). It reads the consent values that the Settings app writes and the apps that used each
/// capability. The page is the shared settings page (DEC-IW-340): the person flips switches, the changes wait in the pending bar, and one review applies them. The quick switches (camera and
/// microphone, privacy lockdown) stage their values and open the same review. A change is recorded before it is made, written with compare-and-set, read back and can be undone. Nothing here
/// needs administrator rights, because the values belong to the signed-in user.
/// </summary>
public sealed class PermissionsViewModel : PageViewModel, INoticePage, ISettingsPage
{
    public const string AppsCategory = "Apps";
    private const int HistoryLimit = 30;
    private readonly PermissionService service;
    private readonly IReviewPrompt prompt;
    private readonly Dictionary<string, PermissionChoice> staged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PermissionSettingRow> rows = new(StringComparer.Ordinal);
    private PermissionGroup? stagedGroup;
    private Task work = Task.CompletedTask;
    private PermissionSnapshot? snapshot;
    private IReadOnlyList<PermissionChangeRecord> records = [];
    private IReadOnlyList<bool> revertable = [];
    private string? notice;
    private bool noticeIsError;
    private bool busy;
    private bool loaded;
    private string? loadError;
    private Guid? offerUndo;

    public PermissionsViewModel(LocalizationService text, SimulationModel model, PermissionService service, IReviewPrompt prompt, IPagePreferences? preferences = null, Func<string, bool>? copy = null)
        : base("Permissions", text, model)
    {
        HistoryList = new(text, HistoryArea.Permissions, () => OpenHistory);
        this.service = service; this.prompt = prompt;
        List = new SettingsListModel(text, "Permissions", preferences) { FeedbackSource = () => Feedback };
        var clipboard = copy ?? HostServices.Clipboard.TrySetText;
        List.ShowDetails = row => prompt.ShowDetailsAsync(SettingDetails.Build(Text, clipboard, row));
        RefreshCommand = new(() => work = ReloadAsync(), () => !Busy);
        ReviewCommand = new(() => work = ReviewAsync(), () => CanReview);
        DiscardCommand = new(DiscardAll, () => staged.Count > 0 && !Busy);
        CameraMicrophoneCommand = new(() => work = StageGroupAsync(PermissionGroup.CameraMicrophone), () => CanQuick);
        LockdownCommand = new(() => work = StageGroupAsync(PermissionGroup.Lockdown), () => CanQuick);
        UndoLastCommand = new(() => work = UndoOfferedAsync(), () => offerUndo is not null && !Busy);
        RevertHistory = new(row => { if (row is not null && !Busy) work = RevertAsync(row.Record.Change.Id); }, _ => !Busy);
    }

    /// <summary>Search, categories (the three sections and the apps), filters and the rows.</summary>
    public SettingsListModel List { get; }
    public ObservableCollection<PermissionHistoryRow> History { get; } = [];
    public PageHistoryModel HistoryList { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ReviewCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand CameraMicrophoneCommand { get; }
    public RelayCommand LockdownCommand { get; }
    public RelayCommand UndoLastCommand { get; }
    public RelayCommand<PermissionHistoryRow> RevertHistory { get; }

    public override bool IsBusy => Busy;
    private bool Busy { get => busy; set { if (SetProperty(ref busy, value)) { OnPropertyChanged(nameof(IsBusy)); Notify(); } } }

    public string? LoadError => loadError;
    public bool HasLoadError => loadError is not null;
    public string? Notice { get => notice; private set { if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool NoticeIsError { get => noticeIsError; private set { if (SetProperty(ref noticeIsError, value)) OnPropertyChanged(nameof(NoticeSeverity)); } }
    public BannerSeverity NoticeSeverity => noticeIsError ? BannerSeverity.Warning : BannerSeverity.Success;
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool ShowUndoOffer => offerUndo is not null;
    public bool HasHistory => History.Count > 0;
    public bool HasRows => rows.Count > 0;
    public bool ShowList => loaded && HasRows && !HasLoadError;
    public bool ShowEmpty => loaded && !HasRows && !HasLoadError && !Busy;
    /// <summary>The first read is running: the page shows the loading skeleton.</summary>
    public bool ShowLoading => Busy && !loaded;
    public bool CanQuick => !Busy && snapshot is not null && PermissionCatalog.CameraMicrophone.Any(id => snapshot.Find(id) is { Read.Exists: true });
    public bool CanReview => !Busy && staged.Count > 0;
    public override int PendingCount => staged.Count;
    public IReadOnlyList<PermissionSettingRow> Rows => rows.Values.ToList();

    public bool CameraMicrophoneIsOff => snapshot is not null && PermissionService.IsCameraMicrophoneOff(snapshot);
    public string CameraMicrophoneButton => Text[CameraMicrophoneIsOff ? "PermQuickOn" : "PermQuickOff"];
    public string CameraMicrophoneState => snapshot is null ? "" : Text.Format("PermQuickState", StateOf("webcam"), StateOf("microphone"));

    public string ActionBarSummary => PendingCount == 1 ? Text["ABPendingOne"] : Text.Format("ABPendingFormat", PendingCount);
    public override StagingView? Staging => PendingCount == 0 ? null : new(Key,
        staged.Where(s => rows.ContainsKey(s.Key)).Select(s => rows[s.Key]).Select(r => new StagedChange(r.Id, r.Title, r.CurrentLabel, r.ChoiceOf(staged[r.Id]).Label, r.Risk, () => { if (!Busy) Unstage(r); })).ToList(),
        ActionBarSummary, Text["ABReview"], Text["ABReviewHelp"], Text["Discard"], Text["DiscardHelp"],
        () => ReviewCommand.CanExecute(null), () => ReviewCommand.Execute(null), () => DiscardCommand.CanExecute(null), () => DiscardCommand.Execute(null));

    private string StateOf(string id) => snapshot?.Find(id) is { Read.Exists: true } r ? Text["PermState_" + r.Read.Global] : Text["PermNotPresent"];

    private void Notify()
    {
        RefreshCommand.NotifyCanExecuteChanged(); ReviewCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged();
        CameraMicrophoneCommand.NotifyCanExecuteChanged(); LockdownCommand.NotifyCanExecuteChanged(); UndoLastCommand.NotifyCanExecuteChanged(); RevertHistory.NotifyCanExecuteChanged();
        foreach (var n in new[] { nameof(CanQuick), nameof(CanReview), nameof(ShowEmpty), nameof(ShowLoading), nameof(ShowList), nameof(ShowUndoOffer), nameof(CameraMicrophoneButton), nameof(CameraMicrophoneState),
            nameof(CameraMicrophoneIsOff), nameof(HasRows), nameof(PendingCount), nameof(ActionBarSummary) }) OnPropertyChanged(n);
        foreach (var row in rows.Values) { row.Refresh(); row.RefreshCommands(); }
        if (List.SelectedState.Id == StateFilter.Pending) List.Rebuild();
    }

    public override void Activate() { if ((!loaded || Journal.IsStale) && !Busy) work = ReloadAsync(); }
    public override Task WhenIdleAsync() => work;
    public override ICommand? ShortcutRefresh() => RefreshCommand;

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        if (snapshot is not null) Fill(snapshot);
        RefreshHistory();
        List.Refresh();
    }

    // ---- loading ----

    private async Task ReloadAsync()
    {
        Journal.MarkRead();
        Busy = true;
        try
        {
            var read = await Task.Run(() => service.Read()).ConfigureAwait(true);
            loaded = true;
            snapshot = read;
            loadError = read.Rows.Count > 0 && read.Rows.All(r => !r.Read.Ok) ? Text["PermLoadFailed"] : null;
            Fill(read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            loadError = Text["PermLoadFailed"]; loaded = true;
        }
        finally { Busy = false; OnPropertyChanged(nameof(LoadError)); OnPropertyChanged(nameof(HasLoadError)); Notify(); }
        RefreshHistory();
        Notify();
    }

    private void Fill(PermissionSnapshot read)
    {
        rows.Clear();
        var sectionOrder = PermissionCatalog.Sections.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        foreach (var data in read.Rows.Where(r => r.Read.Ok && r.Read.Exists))
        {
            var id = data.Capability.Id;
            var name = Text["Perm_" + id + "_Name"];
            var section = data.Capability.Section;
            var label = Text["PermSection_" + section];
            var order = sectionOrder.GetValueOrDefault(section);
            var used = data.Read.Apps.Count(a => a.LastUsed is not null);
            var appsHint = data.Read.Apps.Count == 0 ? Text["PermNoApps"] : Text.Format("PermAppsHeader", data.Read.Apps.Count, used);
            Add(new(Text, this, new(id, ConsentScope.Global), name, Text["Perm_" + id + "_Desc"], section, label, order, data.Read.Global, data.Limit, appsHint));
            if (data.Read.HasDesktopKey)
                Add(new(Text, this, new(id, ConsentScope.Desktop), Text.Format("PermSubjectDesktop", name), Text["PermDesktopRowDesc"], section, label, order, data.Read.Desktop, data.Limit, ""));
            foreach (var app in data.Read.Apps.Where(a => a.CanChange).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var usedText = app.LastUsed is { } t ? Text.Format("PermUsedOn", Text.When(t.ToLocalTime(), "g")) : Text["PermNeverUsed"];
                Add(new(Text, this, new(id, ConsentScope.App, app.Key), Text.Format("PermSubjectApp", name, app.Name), app.Path ?? app.Key, AppsCategory, Text["PermSectionApps"], sectionOrder.Count,
                    app.State, data.Limit, usedText));
            }
        }
        // A staged choice survives a read while its value is still there and still differs.
        foreach (var key in staged.Keys.ToList())
            if (!rows.TryGetValue(key, out var row) || !Differs(row, staged[key])) staged.Remove(key);
        if (staged.Count == 0) stagedGroup = null;
        List.SetRows(rows.Values.Cast<ISettingRow>().ToList());
        OnPropertyChanged(nameof(HasRows)); OnPropertyChanged(nameof(Staging));
    }

    private void Add(PermissionSettingRow row) => rows[row.Id] = row;

    public static string KeyOf(ConsentTarget target) => target.Capability + "|" + target.Scope + (target.AppKey is null ? "" : "|" + target.AppKey);

    private static bool Differs(PermissionSettingRow row, PermissionChoice choice) =>
        row.Current != (choice == PermissionChoice.Allow ? ConsentState.Allow : ConsentState.Deny);

    // ---- staging ----

    public PermissionChoice? StagedChoice(PermissionSettingRow row) => staged.TryGetValue(row.Id, out var c) ? c : null;

    /// <summary>Stages a choice. Picking the value that is in effect now takes the row out of the pending changes.</summary>
    public void Stage(PermissionSettingRow row, PermissionChoice choice)
    {
        if (Busy || !row.IsAvailable) return;
        if (Differs(row, choice)) staged[row.Id] = choice; else staged.Remove(row.Id);
        stagedGroup = null;
        Notice = null;
        Notify(); OnPropertyChanged(nameof(Staging));
    }

    public void Unstage(PermissionSettingRow row)
    {
        if (!staged.Remove(row.Id)) return;
        stagedGroup = null;
        Notify(); OnPropertyChanged(nameof(Staging));
    }

    private void DiscardAll()
    {
        staged.Clear(); stagedGroup = null; Notice = null;
        Notify(); OnPropertyChanged(nameof(Staging));
    }

    /// <summary>The quick switches stage the values of their group (replacing what was staged) and open the one review.</summary>
    private Task StageGroupAsync(PermissionGroup group)
    {
        if (!CanQuick || snapshot is null) return Task.CompletedTask;
        var request = service.ForGroup(group, snapshot);
        staged.Clear();
        foreach (var target in request.Targets)
            if (rows.TryGetValue(KeyOf(target), out var row) && row.IsAvailable && Differs(row, request.Choice)) staged[row.Id] = request.Choice;
        stagedGroup = staged.Count > 0 ? group : null;
        Notice = null; ClearOffer();
        Notify(); OnPropertyChanged(nameof(Staging));
        if (staged.Count == 0) { Notice = Text["PermNothingToChange"]; NoticeIsError = false; return Task.CompletedTask; }
        return ReviewAsync();
    }

    // ---- review and apply ----

    private async Task ReviewAsync()
    {
        if (!CanReview) return;
        Notice = null; ClearOffer();
        var values = staged.Where(s => rows.ContainsKey(s.Key)).Select(s => (rows[s.Key].Target, s.Value)).ToList();
        var request = PermissionService.ForValues(values, stagedGroup);
        PermissionPreview preview;
        Busy = true;
        try { preview = await Task.Run(() => service.Preview(request)).ConfigureAwait(true); }
        finally { Busy = false; }
        if (preview.IsRefused) { Fail(CodeText(preview.RefusalCode!)); return; }
        var changes = preview.Changes;
        if (changes.Count == 0)
        {
            var codes = preview.Entries.Select(e => e.RefusalCode).Where(c => c is not null).Distinct().ToList();
            Notice = codes.Count == 0 ? Text["PermNothingToChange"] : string.Join(" ", codes.Select(c => CodeText(c!)));
            NoticeIsError = codes.Any(c => c != PermissionCode.AlreadySet);
            return;
        }
        var anyAllow = changes.Any(c => ConsentValues.StateOf(c.AfterRaw) == ConsentState.Allow);
        var allAllow = changes.All(c => ConsentValues.StateOf(c.AfterRaw) == ConsentState.Allow);
        var lines = new List<string>();
        foreach (var c in changes) lines.Add(Text.Format("PermLine", Subject(c.Target), StateText(c.BeforeRaw), StateText(c.AfterRaw)));
        foreach (var limit in changes.Select(c => c.Limit).Where(l => l != PermissionLimit.None).Distinct()) lines.Add(Text["PermLimit_" + limit]);
        lines.AddRange(preview.Entries.Where(e => e.RefusalCode is not null and not PermissionCode.AlreadySet).Select(e => Text.Format("PermSkippedLine", Subject(e.Target), CodeText(e.RefusalCode!))));
        var title = request.Group switch
        {
            PermissionGroup.Lockdown => Text["PermLockdownTitle"],
            PermissionGroup.CameraMicrophone => Text[allAllow ? "PermQuickOnTitle" : "PermQuickOffTitle"],
            _ when changes.Count == 1 => Text[allAllow ? "PermAllowTitle" : "PermBlockTitle"],
            _ => Text["PermStagedTitle"],
        };
        var body = request.Group switch
        {
            PermissionGroup.Lockdown => Text["PermLockdownBody"],
            PermissionGroup.CameraMicrophone => Text["PermQuickBody"],
            _ when changes.Count == 1 => Text.Format("PermChangeBody", Subject(changes[0].Target)),
            _ => Text["PermQuickBody"],
        };
        var warning = anyAllow && !allAllow ? Text["PermWarnMixed"] : Text[allAllow ? "PermWarnAllow" : "PermWarnBlock"];
        var primary = anyAllow && !allAllow ? Text["PermApplyPrimary"] : Text[allAllow ? "PermAllowPrimary" : "PermBlockPrimary"];
        var confirmation = new ActionConfirmation(title, body, lines, warning, primary)
        {
            Changes = changes.Select(c => new ConfirmChange(Subject(c.Target), StateText(c.BeforeRaw), StateText(c.AfterRaw))).ToList(),
            Risk = anyAllow ? RiskLevel.Medium : RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, Restart = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;

        using var run = Runs.Begin(Text.Format("ACRunApply", Title), true);
        if (!run.Started) return;
        Busy = true;
        PermissionApplyResult result;
        try { result = await service.ApplyAsync(preview).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { Fail(Text["ActionFailedUnexpected"]); Busy = false; run.Finish(RunResult.Error, Notice); return; }
        finally { Busy = false; }
        Report(result);
        if (result.Ok) { staged.Clear(); stagedGroup = null; }
        run.Finish(result.Ok ? RunResult.Ok : RunResult.Error, Notice);
        await ReloadAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(Staging));
    }

    // ---- undo ----

    private PermissionChangeRecord? LatestChange(PermissionSettingRow row) =>
        records.FirstOrDefault(r => r.State == PermissionChangeState.Applied && r.Change.RevertOf is null && r.Change.Entries.Any(e => KeyOf(e.Target) == row.Id));

    public bool CanUndoLatest(PermissionSettingRow row)
    {
        if (Busy || LatestChange(row) is not { } latest) return false;
        var i = records.ToList().IndexOf(latest);
        return i >= 0 && i < revertable.Count && revertable[i];
    }

    public void UndoLatest(PermissionSettingRow row)
    {
        if (CanUndoLatest(row) && LatestChange(row) is { } latest) work = RevertAsync(latest.Change.Id);
    }

    private async Task RevertAsync(Guid id)
    {
        if (Busy) return;
        var record = service.History(500).FirstOrDefault(r => r.Change.Id == id);
        var lines = record?.Change.Entries.Select(e => Text.Format("PermLine", Subject(e.Target), StateText(e.AfterRaw), StateText(e.BeforeRaw))).ToList() ?? [];
        var confirmation = new ActionConfirmation(Text["PermRevertTitle"], Text["PermRevertBody"], lines, Text["PermWarnRevert"], Text["PermRevertPrimary"])
        {
            Changes = record?.Change.Entries.Select(e => new ConfirmChange(Subject(e.Target), StateText(e.AfterRaw), StateText(e.BeforeRaw))).ToList() ?? [],
            Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, Restart = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true) || Busy) return;
        using var run = Runs.Begin(Text.Format("ACRunRevert", Title), true);
        if (!run.Started) return;
        Busy = true; Notice = null; ClearOffer();
        PermissionApplyResult result;
        try { result = await service.RevertAsync(id).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { Fail(Text["ActionFailedUnexpected"]); Busy = false; run.Finish(RunResult.Error, Notice); return; }
        finally { Busy = false; }
        Report(result);
        run.Finish(result.Ok ? RunResult.Ok : RunResult.Error, Notice);
        await ReloadAsync().ConfigureAwait(true);
    }

    private Task UndoOfferedAsync() => offerUndo is { } id ? RevertAsync(id) : Task.CompletedTask;

    private void ClearOffer() { offerUndo = null; OnPropertyChanged(nameof(ShowUndoOffer)); UndoLastCommand.NotifyCanExecuteChanged(); }

    private void Fail(string message) { Notice = message; NoticeIsError = true; }

    private void Report(PermissionApplyResult result)
    {
        if (!result.Ok)
        {
            var text = CodeText(result.Code);
            if (result.Results.Any(r => r.Code == PermissionCode.RolledBack)) text += " " + Text["PermRolledBackNote"];
            Fail(text);
            return;
        }
        var count = result.Change?.Entries.Count ?? 0;
        Notice = (count == 1 ? Text["PermAppliedOne"] : Text.Format("PermApplied", count)) + " " + Text["PermRestartApps"] + (result.RecordLost ? " " + Text["RecordLostNote"] : "");
        NoticeIsError = false;
        if (result.Change is { RevertOf: null } change) { offerUndo = change.Id; OnPropertyChanged(nameof(ShowUndoOffer)); UndoLastCommand.NotifyCanExecuteChanged(); }
    }

    // ---- text ----

    private string StateText(string? raw) => Text["PermState_" + ConsentValues.StateOf(raw)];

    private string Subject(ConsentTarget target)
    {
        var name = PermissionCatalog.Find(target.Capability) is null ? target.Capability : Text["Perm_" + target.Capability + "_Name"];
        return target.Scope switch
        {
            ConsentScope.Desktop => Text.Format("PermSubjectDesktop", name),
            ConsentScope.App => Text.Format("PermSubjectApp", name, ConsentAppNames.PackageName(target.AppKey ?? "")),
            _ => name,
        };
    }

    public string CodeText(string code)
    {
        var key = "PermCode_" + code.Replace('.', '_');
        return Text.Has(key) ? Text[key] : Text["PermCode_perm_failed"];
    }

    // ---- history ----

    private void RefreshHistory()
    {
        History.Clear();
        var shown = new List<HistoryRowViewModel>();
        try { records = service.History(HistoryLimit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { records = []; }
        revertable = records.Count > 0 ? service.CanRevertAll(records) : [];
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            History.Add(new(r, DescribeChange(r.Change), r.State is { } s ? Text["PermHistState_" + s] : Text["PermInterrupted"], Text.When(r.BeganUtc.ToLocalTime(), "g"), i < revertable.Count && revertable[i], RevertHistory));
            var h = History[^1];
            shown.Add(HistoryList.Row(r.Change.Id, r.BeganUtc, r.State == PermissionChangeState.Applied ? (records.Any(o => o.Change.RevertOf == r.Change.Id && o.State == PermissionChangeState.Applied) ? HistoryResult.Undone : HistoryResult.Applied) : r.State == PermissionChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed, h.Description, h.CanRevert, h.RevertCommand, h, new(Text["PermUndo"], Text["PermUndoHelp"])));
        }
        HistoryList.Set(shown);
        OnPropertyChanged(nameof(HasHistory));
        foreach (var row in rows.Values) { row.Refresh(); row.RefreshCommands(); }
    }

    private string DescribeChange(PermissionChange c)
    {
        var undo = c.RevertOf is null ? "" : " " + Text["PermUndoSuffix"];
        if (c.Group != PermissionGroup.Single || c.Entries.Count != 1)
            return Text.Format("PermHistoryGroup", Text["PermGroup_" + c.Group], c.Entries.Count) + undo;
        var e = c.Entries[0];
        return Text.Format("PermHistorySingle", Subject(e.Target), StateText(e.AfterRaw)) + undo;
    }
}

public sealed class PermissionHistoryRow(PermissionChangeRecord record, string description, string stateText, string when, bool canRevert, RelayCommand<PermissionHistoryRow> revert)
{
    public PermissionChangeRecord Record { get; } = record;
    public string Description { get; } = description;
    public string StateText { get; } = stateText;
    public string When { get; } = when;
    public bool CanRevert { get; } = canRevert;
    public RelayCommand<PermissionHistoryRow> RevertCommand { get; } = revert;
    public string AutomationName => $"{Description}, {StateText}, {When}";
}
