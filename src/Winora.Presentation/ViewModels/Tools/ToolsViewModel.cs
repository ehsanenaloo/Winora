// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;

namespace Winora.Desktop;

/// <summary>One repair tool on the page. The disk check also carries the list of fixed drives.</summary>
public sealed class RepairToolRow : ObservableObject
{
    private readonly Func<RepairToolRow, string>? commandFor;
    private string? volume;
    private string commandText = "";
    private bool commandShown;
    private string stateText = "";
    private Tone stateTone = Tone.Neutral;

    public RepairToolRow(RepairToolInfo info, string title, string description, string changes, string duration, string risk, string restart, string noUndo, string admin, string volumeLabel, string volumeHelp, IReadOnlyList<string> volumes, Func<bool> canRun, Action<RepairToolRow> run,
        Func<RepairToolRow, string>? commandFor = null)
    {
        Info = info; Title = title; Description = description; ChangesText = changes; DurationText = duration; RiskText = risk; RestartText = restart; NoUndoText = noUndo; AdminText = admin;
        VolumeLabel = volumeLabel; VolumeHelp = volumeHelp; Volumes = volumes;
        volume = volumes.FirstOrDefault();
        this.commandFor = commandFor;
        commandText = commandFor?.Invoke(this) ?? "";
        RunCommand = new(() => run(this), () => canRun() && (!NeedsVolume || volume is not null));
    }

    /// <summary>The exact command the tool starts, as the confirmation shows it. Follows the chosen drive.</summary>
    public string CommandText { get => commandText; private set => SetProperty(ref commandText, value); }
    public bool HasCommand => commandText.Length > 0;
    /// <summary>The "Exact command" section is open.</summary>
    public bool IsCommandShown { get => commandShown; set => SetProperty(ref commandShown, value); }
    /// <summary>Duration, risk, restart, administrator and undo facts on one quiet line. A medium risk is a badge instead.</summary>
    public string MetaText => string.Join(" · ", new[] { DurationText, Info.Risk == RepairRisk.Low ? RiskText : "", RestartText, AdminText, NoUndoText }.Select(t => t.Trim().TrimEnd('.')).Where(t => t.Length > 0));
    /// <summary>The picture of what the tool works on: system files, a drive, the component store, the Store or the network.</summary>
    public IconKey Icon => Info.Tool switch
    {
        RepairTool.SystemFileCheck => IconKey.ShieldCheckmark24,
        RepairTool.DiskCheck => IconKey.HardDrive20,
        RepairTool.ComponentCheckHealth or RepairTool.ComponentScanHealth => IconKey.Search24,
        RepairTool.ComponentRestoreHealth => IconKey.Toolbox24,
        RepairTool.StoreCacheReset => IconKey.Apps24,
        RepairTool.WinsockReset or RepairTool.IpReset or RepairTool.Ipv6Reset or RepairTool.WinHttpProxyReset => IconKey.Globe24,
        _ => IconKey.Wrench24,
    };
    public bool IsMediumRisk => Info.Risk == RepairRisk.Medium;

    /// <summary>The state of this card: running now, or how the last run ended. Empty before any run.</summary>
    public string StateText => stateText;
    public bool HasState => stateText.Length > 0;
    public Tone StateTone => stateTone;
    public void SetState(string text, Tone appearance)
    {
        stateText = text; stateTone = appearance;
        OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(HasState)); OnPropertyChanged(nameof(StateTone));
    }

    public RepairToolInfo Info { get; }
    public string Title { get; }
    public string Description { get; }
    public string ChangesText { get; }
    public string DurationText { get; }
    public string RiskText { get; }
    public string RestartText { get; }
    public string NoUndoText { get; }
    public string AdminText { get; }
    public string VolumeLabel { get; }
    public string VolumeHelp { get; }
    public IReadOnlyList<string> Volumes { get; }
    public bool NeedsVolume => Info.Tool == RepairTool.DiskCheck;
    public string? SelectedVolume { get => volume; set { if (SetProperty(ref volume, value)) { RunCommand.NotifyCanExecuteChanged(); if (commandFor is not null) { CommandText = commandFor(this); OnPropertyChanged(nameof(HasCommand)); } } } }
    public RelayCommand RunCommand { get; }
    public string AutomationName => HasState ? $"{Title}. {Description}. {StateText}" : $"{Title}. {Description}";
    public RepairRequest Request() => new(Info.Tool, NeedsVolume ? volume : null);
}

/// <summary>One optional feature. A feature that cannot be changed says why in <see cref="ReadOnlyText"/>.</summary>
public sealed class FeatureRow : ObservableObject
{
    public FeatureRow(WindowsFeature feature, string stateText, string classText, string description, string source, string restartText, string readOnlyText, string enableLabel, string disableLabel,
        string enableHelp, string disableHelp, Func<bool> canAct, Action<FeatureRow, bool> act)
    {
        Feature = feature; StateText = stateText; ClassText = classText; Description = description; SourceText = source; RestartText = restartText; ReadOnlyText = readOnlyText;
        EnableLabel = enableLabel; DisableLabel = disableLabel; EnableHelp = enableHelp; DisableHelp = disableHelp;
        EnableCommand = new(() => act(this, true), () => feature.CanEnable && canAct());
        DisableCommand = new(() => act(this, false), () => feature.CanDisable && canAct());
    }

    public WindowsFeature Feature { get; }
    /// <summary>The Windows name of the feature; it identifies the feature to the service.</summary>
    public string Name => Feature.Name;
    /// <summary>The name shown to the user.</summary>
    public string Title { get; init; } = "";
    public string StateText { get; }
    public string ClassText { get; }
    public string Description { get; }
    public string SourceText { get; }
    public string RestartText { get; }
    public string ReadOnlyText { get; }
    public string EnableLabel { get; }
    public string DisableLabel { get; }
    public string EnableHelp { get; }
    public string DisableHelp { get; }
    public bool IsCaution => Feature.Class == FeatureClass.Caution;
    public string CautionText { get; init; } = "";
    /// <summary>The state of the feature and, when a restart is pending, that fact: one quiet line.</summary>
    public string MetaText => StateText;
    public bool ShowEnable => Feature.CanEnable;
    public bool ShowDisable => Feature.CanDisable;
    public bool HasReadOnly => ReadOnlyText.Length > 0;
    public bool HasDescription => Description.Length > 0;
    public bool HasSource => SourceText.Length > 0;
    public bool HasRestart => RestartText.Length > 0;
    public RelayCommand EnableCommand { get; }
    public RelayCommand DisableCommand { get; }
    public string AutomationName => $"{Title}. {StateText}. {ClassText}";
    /// <summary>Accessible names of the row buttons: the label alone repeats on every row.</summary>
    public string EnableName => $"{EnableLabel}, {Title}";
    public string DisableName => $"{DisableLabel}, {Title}";
}

public sealed class ToolsHistoryRow(RepairRecord record, string description, string stateText, string when, bool canRevert, RelayCommand<ToolsHistoryRow> revert, string undoLabel, string undoHelp)
{
    public RepairRecord Record { get; } = record;
    public string Description { get; } = description;
    public string StateText { get; } = stateText;
    public string When { get; } = when;
    public bool CanRevert { get; } = canRevert;
    public RelayCommand<ToolsHistoryRow> RevertCommand { get; } = revert;
    public string UndoLabel { get; } = undoLabel;
    public string UndoHelp { get; } = undoHelp;
    public string AutomationName => $"{Description}, {StateText}, {When}";
}

/// <summary>
/// The Tools page, "Repair and features" (decision DEC-IW-041): repair tools that start one fixed Windows program each, and the optional Windows features. Every action shows its exact command,
/// asks once, is recorded before it starts, shows progress, can be cancelled and ends with a result log the user can copy. Only one action runs at a time. Repairs have no undo; a feature change
/// does, while the feature still has the state Winora left.
/// </summary>
public sealed class ToolsViewModel : PageViewModel, INoticePage
{
    private int selectedTab;
    /// <summary>0 repair tools, 1 optional features.</summary>
    public int SelectedTab { get => selectedTab; set => SetProperty(ref selectedTab, value); }

    private const int HistoryLimit = 30;
    private readonly RepairService repair;
    private readonly FeatureService features;
    private readonly IReviewPrompt prompt;
    private readonly IClipboardService clipboard;
    private Task work = Task.CompletedTask;
    private CancellationTokenSource? cts;
    private bool busy, cancelling, restartPending, featuresLoaded, loadingFeatures;
    private string runTitle = "";
    private string? notice, resultTitle, resultSummary, resultLog, loadError;
    private bool noticeIsError;
    private string filter = "";
    private FeatureSnapshot? snapshot;
    private bool resultExpanded, resultProblem, resultNoVerdict, noticeNeutral;

    public ToolsViewModel(LocalizationService text, SimulationModel model, RepairService repair, FeatureService features, IReviewPrompt prompt, IClipboardService clipboard) : base("Tools", text, model)
    {
        HistoryList = new(text, HistoryArea.Repair, () => OpenHistory);
        this.repair = repair; this.features = features; this.prompt = prompt; this.clipboard = clipboard;
        CancelCommand = new(Cancel, () => busy && !cancelling);
        LoadFeaturesCommand = new(() => work = LoadFeaturesAsync(), () => CanStart);
        ClearFeatureFilterCommand = new(() => FeatureFilter = "");
        CopyLogCommand = new(CopyLog, () => !string.IsNullOrEmpty(resultLog));
        RevertHistory = new(row => { if (row is not null) work = RevertAsync(row); });
        BuildTools();
        RefreshHistory();
    }

    /// <summary>The Settings choice "Wait for slow tools"; the page shows the limit that applies to a run.</summary>
    public Func<SlowToolWait> WaitChoice { get; set; } = static () => SlowToolWait.Normal;
    /// <summary>How long a normal run of a tool takes. Past it the page shows "Still running" with the time and a Stop button.</summary>
    public Func<RepairTool, TimeSpan> UsualTimeOf { get; set; } = WaitLimits.UsualTime;
    /// <summary>How often the elapsed time is written while a run is past its usual time. Nothing ticks before that or while idle.</summary>
    public TimeSpan TickEvery { get; set; } = TimeSpan.FromSeconds(1);
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public ObservableCollection<RepairToolRow> Tools { get; } = [];
    public ObservableCollection<FeatureRow> Features { get; } = [];
    public ObservableCollection<ToolsHistoryRow> History { get; } = [];
    public PageHistoryModel HistoryList { get; }

    public RelayCommand CancelCommand { get; }
    public RelayCommand LoadFeaturesCommand { get; }
    public RelayCommand ClearFeatureFilterCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public RelayCommand<ToolsHistoryRow> RevertHistory { get; }

    public override bool IsBusy => busy;
    public bool IsElevated => repair.IsElevated;
    public bool ShowNeedsAdministrator => !repair.IsElevated;
    public string NeedsAdministratorTitle => repair.UnsafeLocation ? Text["AppLocationUnsafeTitle"] : Text["AdminNoticeTitle"];
    public string NeedsAdministratorText => repair.UnsafeLocation ? Text["AppLocationUnsafe"] : Text["ToolsNeedsAdminBody"];
    public bool ShowAdminHowTo => !repair.UnsafeLocation;
    private bool CanStart => IsElevated && !busy;

    public string? Notice { get => notice; private set { noticeNeutral = false; if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasNotice)); OnPropertyChanged(nameof(NoticeSeverity)); } }
    public bool HasNotice => !string.IsNullOrEmpty(notice);
    public bool NoticeIsError { get => noticeIsError; private set { if (SetProperty(ref noticeIsError, value)) OnPropertyChanged(nameof(NoticeSeverity)); } }
    public BannerSeverity NoticeSeverity => noticeIsError ? BannerSeverity.Warning : noticeNeutral ? BannerSeverity.Informational : BannerSeverity.Success;

    public bool ShowResult => !string.IsNullOrEmpty(resultLog);
    /// <summary>The result card uses the same tone as the result notice: success, a problem, or neutral when the tool gave no result Winora can read.</summary>
    public Tone ResultTone => resultProblem ? Tone.Caution : resultNoVerdict ? Tone.Neutral : Tone.Success;
    public string ResultBadgeText => Text[resultProblem ? "V2MaResultProblem" : resultNoVerdict ? "ToolsOutcomeNoVerdict" : "V2MaResultDone"];
    public bool ResultExpanded { get => resultExpanded; set => SetProperty(ref resultExpanded, value); }
    public string ResultTitle => resultTitle ?? "";
    public string ResultSummary => resultSummary ?? "";
    public string ResultLog => resultLog ?? "";
    public bool ShowRestartBanner => restartPending;

    public string? LoadError => loadError;
    public bool HasLoadError => loadError is not null;
    public bool FeaturesLoaded => featuresLoaded;
    public bool ShowFeaturesHint => !featuresLoaded && !busy;
    public string LoadFeaturesLabel => Text[featuresLoaded ? "ToolsFeatRefresh" : "ToolsFeatLoad"];
    public bool HasFeatures => Features.Count > 0;
    public bool ShowFeatureEmpty => featuresLoaded && Features.Count == 0 && !HasLoadError;
    /// <summary>The first read of the feature list is running: the tab shows the loading skeleton.</summary>
    public bool ShowFeaturesLoading => loadingFeatures && !featuresLoaded;
    /// <summary>Windows returned no optional feature at all.</summary>
    public bool ShowNoFeatures => ShowFeatureEmpty && (snapshot?.Features.Count ?? 0) == 0;
    /// <summary>The filter hides every feature: the tab offers to clear it.</summary>
    public bool ShowFeatureNoMatches => ShowFeatureEmpty && (snapshot?.Features.Count ?? 0) > 0;
    public bool HasHistory => History.Count > 0;
    public string FeatureCountText => Text.Format("ToolsFeatCount", Features.Count, snapshot?.Features.Count ?? 0);

    public string FeatureFilter { get => filter; set { if (SetProperty(ref filter, value ?? "")) { FillFeatures(); OnPropertyChanged(nameof(HasFeatureFilter)); } } }
    public bool HasFeatureFilter => filter.Trim().Length > 0;

    // ---- a run that passes its usual time ----

    private bool pastUsual;
    private string stillRunningText = "";
    private DateTimeOffset runStartedAt;

    /// <summary>The run is longer than usual: the page shows "Still running", the elapsed time and Stop. The program is never ended because of the time.</summary>
    public bool ShowStillRunning => pastUsual && busy;
    public string StillRunningText => stillRunningText;

    private async Task WatchUsualTimeAsync(string title, RepairTool tool, CancellationToken token)
    {
        runStartedAt = Clock.GetUtcNow();
        try
        {
            await Task.Delay(UsualTimeOf(tool), Clock, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || !busy) return;
            while (!token.IsCancellationRequested && busy)
            {
                var limit = WaitLimits.For(tool, WaitChoice());
                // The text is set before the flag, so a reader never sees "Still running" with an empty text.
                stillRunningText = Text.Format("ToolsStillRunningBody", title, Text.Digits(ActionCenterRules.Elapsed(Clock.GetUtcNow() - runStartedAt)), (int)Math.Round(limit.TotalMinutes));
                pastUsual = true;
                OnPropertyChanged(nameof(StillRunningText)); OnPropertyChanged(nameof(ShowStillRunning));
                await Task.Delay(TickEvery, Clock, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { /* the run ended */ }
    }

    private void StopWatching()
    {
        if (!pastUsual) return;
        pastUsual = false; stillRunningText = "";
        OnPropertyChanged(nameof(StillRunningText)); OnPropertyChanged(nameof(ShowStillRunning));
    }

    public override void Activate() { RefreshHistory(); }
    public override Task WhenIdleAsync() => work;
    public override System.Windows.Input.ICommand? ShortcutRefresh() => LoadFeaturesCommand;

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        BuildTools();
        if (snapshot is not null) FillFeatures();
        RefreshHistory();
    }

    private void Changed()
    {
        foreach (var t in Tools) t.RunCommand.NotifyCanExecuteChanged();
        foreach (var f in Features) { f.EnableCommand.NotifyCanExecuteChanged(); f.DisableCommand.NotifyCanExecuteChanged(); }
        LoadFeaturesCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        foreach (var n in new[] { nameof(IsBusy), nameof(ShowFeaturesHint), nameof(ShowFeaturesLoading), nameof(LoadFeaturesLabel), nameof(ShowStillRunning) })
            OnPropertyChanged(n);
        foreach (var h in History) h.RevertCommand.NotifyCanExecuteChanged();
    }

    // ---- tools ----

    private void BuildTools()
    {
        Tools.Clear();
        IReadOnlyList<string> volumes;
        try { volumes = repair.FixedVolumes(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { volumes = []; }
        foreach (var info in RepairTools.All)
        {
            var key = "Tools_" + info.Id;
            var duration = info.MinutesLow == info.MinutesHigh ? Text.Format("ToolsDurationOne", info.MinutesLow) : Text.Format("ToolsDuration", info.MinutesLow, info.MinutesHigh);
            Tools.Add(new(info, Text[key + "_Title"], Text[key + "_Desc"], Text[key + "_Changes"], duration, Text[info.Risk == RepairRisk.Low ? "ToolsRiskLow" : "ToolsRiskMedium"],
                Text[info.RestartLikely ? "ToolsRestartLikely" : "ToolsRestartNo"], HasNoUndo(info) ? Text["ToolsNoUndo"] : "", Text["ToolsAdminYes"], Text["ToolsVolumeLabel"], Text["ToolsVolumeHelp"], volumes,
                () => CanStart, row => work = RunToolAsync(row), row => repair.Preview(row.Request()).Command ?? ""));
        }
    }

    // The checks that only read show no "No undo"; chkdsk /scan counts as a repair because Windows may fix small errors while it runs.
    private static bool HasNoUndo(RepairToolInfo info) => info.ChangesSystem || info.Tool == RepairTool.DiskCheck;

    private async Task RunToolAsync(RepairToolRow row)
    {
        if (!CanStart) return;
        var request = row.Request();
        var preview = repair.Preview(request);
        if (preview.IsRefused) { Fail(CodeText(preview.RefusalCode!)); return; }
        var lines = new List<string>
        {
            Text.Format("ToolsCommandLabel", preview.Command!),
            Text.Format("ToolsChangesLabel", row.ChangesText),
            row.DurationText, row.RiskText, row.RestartText, row.AdminText, row.NoUndoText,
        };
        lines.RemoveAll(l => l.Length == 0);
        var confirmation = new ActionConfirmation(Text["ToolsConfirmTitle"], Text.Format("ToolsConfirmBody", preview.Command!), lines, Text["ToolsConfirmWarning"], Text["ToolsConfirmPrimary"])
        {
            Changes = [new ConfirmChange(row.Title, null, null, preview.Command, row.DurationText)],
            Risk = row.Info.Risk == RepairRisk.Medium ? RiskLevel.Medium : RiskLevel.Low, Undo = ConfirmUndo.No, Restart = row.Info.RestartLikely,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        if (!CanStart) return;

        foreach (var other in Tools) if (other.HasState) other.SetState("", Tone.Neutral);
        row.SetState(Text["V2MaToolRunning"], Tone.Info);
        if (StartRun(row.Title) is not { } token) { row.SetState("", Tone.Neutral); return; }
        _ = WatchUsualTimeAsync(row.Title, request.Tool, watch!.Token);
        RepairResult result;
        try { result = await repair.RunAsync(request, NewProgress(), null, null, token).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { EndRun(RunResult.Error, Text.Format("ACRunFailedText", row.Title)); row.SetState("", Tone.Neutral); Fail(Text["ActionFailedUnexpected"]); return; }
        // A program that was ended on request can report a failure; the person asked for the end, so the run is stopped.
        EndRun(RunResultOf(result.Outcome), $"{row.Title}: {OutcomeText(result)}");
        row.SetState(OutcomeText(result), NoVerdict(result) ? Tone.Neutral : ToneOf(result.Outcome));
        ShowResultOf(row.Title, preview.Command!, result);
        RefreshHistory();
    }

    /// <summary>A run that ended without a result Winora can read (sfc in another language) is shown as finished with a neutral tone, never as a proven success.</summary>
    private static bool NoVerdict(RepairResult result) => result.Outcome == RepairOutcome.Succeeded && result.Code == RepairCode.SfcNoVerdict;

    private string OutcomeText(RepairResult result) => NoVerdict(result) ? Text["ToolsOutcomeNoVerdict"] : Text["ToolsOutcome_" + result.Outcome];

    /// <summary>A run that reached its wait limit is not a failure: the program still runs and its result is not known.</summary>
    private RunResult RunResultOf(RepairOutcome outcome) => outcome switch
    {
        RepairOutcome.Succeeded => RunResult.Ok,
        RepairOutcome.FoundProblems or RepairOutcome.StillRunning => RunResult.Warning,
        RepairOutcome.Cancelled => RunResult.Stopped,
        _ when cancelling => RunResult.Stopped,
        _ => RunResult.Error,
    };

    public static Tone ToneOf(RepairOutcome outcome) => outcome switch
    {
        RepairOutcome.Succeeded => Tone.Success,
        RepairOutcome.FoundProblems or RepairOutcome.Cancelled or RepairOutcome.NotStarted or RepairOutcome.StillRunning => Tone.Caution,
        _ => Tone.Danger,
    };

    private IProgress<RepairProgress> NewProgress() => new Progress<RepairProgress>(p =>
    {
        if (p.Percent is { } done) activeRun.Report(done / 100.0);
    });

    private ActivityRun activeRun = ActivityRun.None;
    private CancellationTokenSource? watch;

    /// <summary>Registers the run in the Action center and starts it. Null when another change to the system is running: the person then sees the "Another change is running" toast and nothing starts.</summary>
    private CancellationToken? StartRun(string title, bool changesSystem = true)
    {
        var registered = Runs.Begin(Text.Format(changesSystem ? "ACRunTool" : "ACRunRead", title), changesSystem, Cancel);
        if (!registered.Started) return null;
        activeRun = registered;
        cts?.Dispose();
        cts = new CancellationTokenSource();
        watch?.Dispose();
        watch = new CancellationTokenSource();
        busy = true; cancelling = false; runTitle = title; Notice = null;
        Changed();
        return cts.Token;
    }

    private void EndRun(RunResult result = RunResult.Ok, string? message = null)
    {
        activeRun.Finish(result, message ?? Text.Format("ACRunToolDone", runTitle), quiet: message == "");
        activeRun = ActivityRun.None;
        busy = false; cancelling = false;
        cts?.Dispose(); cts = null;
        try { watch?.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
        watch?.Dispose(); watch = null;
        StopWatching();
        Changed();
    }

    private void Cancel()
    {
        if (cts is null || cancelling) return;
        cancelling = true;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the run ended just now */ }
        Changed();
    }

    private void ShowResultOf(string title, string command, RepairResult result)
    {
        var run = result.Run;
        var outcome = OutcomeText(result);
        resultTitle = Text.Format("ToolsResultLine", title, outcome);
        resultSummary = CodeText(result.Code);
        var log = new StringBuilder();
        log.AppendLine(title);
        log.AppendLine($"{Text["ToolsLogCommand"]}: {command}");
        log.AppendLine($"{Text["ToolsLogResult"]}: {outcome} ({result.Code})");
        log.AppendLine($"{Text["ToolsLogExit"]}: {(run?.ExitCode is { } code ? code.ToString(Text.Culture) : Text["ToolsLogNone"])}");
        log.AppendLine($"{Text["ToolsLogStarted"]}: {Text.When(DateTimeOffset.Now, "g")}");
        log.AppendLine(CodeText(result.Code));
        if (result.RecordLost) log.AppendLine(Text["RecordLostNote"]);
        if (run is { Output.Length: > 0 })
        {
            log.AppendLine().AppendLine(Text["ToolsLogOutput"] + ":");
            log.AppendLine(run.Output.TrimEnd());
            if (run.OutputTruncated) log.AppendLine(Text["ToolsLogTruncated"]);
        }
        if (result.Outcome == RepairOutcome.StillRunning) log.AppendLine().AppendLine(Text["ToolsLogStillRunning"]);
        resultLog = log.ToString();
        if (run?.RestartNeeded == true) { restartPending = true; OnPropertyChanged(nameof(ShowRestartBanner)); }
        resultProblem = result.Outcome != RepairOutcome.Succeeded;
        resultNoVerdict = NoVerdict(result);
        NoticeIsError = resultProblem;
        Notice = CodeText(result.Code) + (result.RecordLost ? " " + Text["RecordLostNote"] : "");
        noticeNeutral = resultNoVerdict;
        OnPropertyChanged(nameof(NoticeSeverity));
        foreach (var n in new[] { nameof(ShowResult), nameof(ResultTitle), nameof(ResultSummary), nameof(ResultLog), nameof(ResultTone), nameof(ResultBadgeText) }) OnPropertyChanged(n);
        CopyLogCommand.NotifyCanExecuteChanged();
    }

    private void CopyLog()
    {
        if (string.IsNullOrEmpty(resultLog)) return;
        Notice = clipboard.TrySetText(resultLog) ? Text["ToolsCopied"] : Text["ToolsCopyFailed"];
        NoticeIsError = Notice == Text["ToolsCopyFailed"];
    }

    private void Fail(string message) { Notice = message; NoticeIsError = true; }

    public string CodeText(string code)
    {
        var key = "ToolsCode_" + code.Replace('.', '_');
        return Text.Has(key) ? Text[key] : Text["ToolsCode_unknown"];
    }

    // ---- features ----

    private async Task LoadFeaturesAsync()
    {
        if (!CanStart) return;
        loadingFeatures = true;
        if (StartRun(Text["ToolsFeatListName"], changesSystem: false) is not { } token) { loadingFeatures = false; return; }
        FeatureSnapshot result;
        try { result = await features.ReadAsync(token).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { loadingFeatures = false; EndRun(RunResult.Error, Text.Format("ACRunFailedText", Text["ToolsFeatListName"])); loadError = Text["ToolsCode_features_unreadable"]; OnFeatureStateChanged(); return; }
        loadingFeatures = false;
        EndRun(RunResult.Ok, "");
        if (result.Ok)
        {
            snapshot = result; featuresLoaded = true; loadError = null;
            FillFeatures();
            Notice = Text.Format("ToolsFeatLoaded", result.Features.Count); NoticeIsError = false;
        }
        else { loadError = CodeText(result.Code); }
        OnFeatureStateChanged();
        RefreshHistory();
    }

    private void OnFeatureStateChanged()
    {
        foreach (var n in new[] { nameof(LoadError), nameof(HasLoadError), nameof(FeaturesLoaded), nameof(ShowFeaturesHint), nameof(ShowFeaturesLoading), nameof(LoadFeaturesLabel), nameof(ShowFeatureEmpty), nameof(ShowNoFeatures), nameof(ShowFeatureNoMatches) })
            OnPropertyChanged(n);
    }

    private void FillFeatures()
    {
        Features.Clear();
        if (snapshot is not null)
        {
            var text = filter.Trim();
            foreach (var f in snapshot.Features.Where(f => text.Length == 0 || f.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
                Features.Add(MakeRow(f));
        }
        foreach (var n in new[] { nameof(HasFeatures), nameof(ShowFeatureEmpty), nameof(ShowNoFeatures), nameof(ShowFeatureNoMatches), nameof(FeatureCountText) }) OnPropertyChanged(n);
    }

    private FeatureRow MakeRow(WindowsFeature f)
    {
        var descKey = "Feat_" + new string(f.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + "_Desc";
        var description = Text.Has(descKey) ? Text[descKey] : f.Info?.Description ?? "";
        var source = f.Info is null ? "" : Text.Format("ToolsFeatSource", f.Info.Source);
        string readOnly;
        if (f.Class == FeatureClass.Unknown) readOnly = Text["ToolsCode_features_unknown"];
        else if (f.Class == FeatureClass.Protected) readOnly = Text["ToolsCode_features_protected"];
        else if (f.State == FeatureState.Enabled && f.BlockedBy.Count > 0) readOnly = Text.Format("ToolsFeatBlocked", string.Join(", ", f.BlockedBy));
        else readOnly = "";
        return new(f, Text.Format("ToolsFeatStateLine", Text["ToolsFeatState_" + f.State]), Text["ToolsFeatClass_" + f.Class], description, source, f.RestartPending ? Text["ToolsFeatRestartPending"] : "", readOnly,
            Text["ToolsFeatEnable"], Text["ToolsFeatDisable"], Text["ToolsFeatEnableHelp"], Text["ToolsFeatDisableHelp"], () => CanStart, (row, enable) => work = ChangeFeatureAsync(row, enable))
        { CautionText = Text["V2MaFeatCaution"], Title = RepairText.FeatureTitle(Text, f.Name) };
    }

    private async Task ChangeFeatureAsync(FeatureRow row, bool enable)
    {
        if (!CanStart || snapshot is null) return;
        var preview = features.Preview(snapshot, row.Name, enable);
        if (preview.IsRefused) { Fail(CodeText(preview.RefusalCode!)); return; }
        var feature = preview.Feature!;
        var word = Text[enable ? "ToolsFeatOn" : "ToolsFeatOff"];
        var lines = new List<string>
        {
            Text.Format("ToolsCommandLabel", preview.Command!),
            row.Title,
            Text.Format("ToolsFeatBefore", Text["ToolsFeatState_" + feature.State]),
            row.Description,
            row.ClassText,
        };
        var warning = Text[feature.Class == FeatureClass.Caution ? "ToolsFeatWarnCaution" : "ToolsFeatWarnSafe"];
        var confirmation = new ActionConfirmation(Text[enable ? "ToolsFeatConfirmOnTitle" : "ToolsFeatConfirmOffTitle"], Text.Format("ToolsFeatConfirmBody", row.Title, word), lines, warning, Text[enable ? "ToolsFeatPrimaryOn" : "ToolsFeatPrimaryOff"])
        {
            Changes = [new ConfirmChange(row.Title, Text["ToolsFeatState_" + feature.State], word, preview.Command, row.Description)],
            Risk = feature.Class == FeatureClass.Caution ? RiskLevel.Medium : RiskLevel.Low, Undo = ConfirmUndo.Exact, Restart = row.RestartText.Length > 0 ? true : null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        if (!CanStart) return;
        await RunFeatureAsync(feature.Name, enable, preview.Command!, null).ConfigureAwait(true);
    }

    private async Task RunFeatureAsync(string name, bool enable, string command, Guid? revertOf)
    {
        if (StartRun(name) is not { } token) return;
        _ = WatchUsualTimeAsync(name, enable ? RepairTool.FeatureEnable : RepairTool.FeatureDisable, watch!.Token);
        FeatureApplyResult result;
        try { result = await features.ApplyAsync(name, enable, NewProgress(), revertOf, token).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { EndRun(RunResult.Error, Text.Format("ACRunFailedText", name)); Fail(Text["ActionFailedUnexpected"]); return; }
        var still = result.Run.Outcome == RepairOutcome.StillRunning;
        EndRun(result.Ok ? RunResult.Ok : still ? RunResult.Warning : RunResult.Error,
            result.Ok ? Text.Format("ToolsFeatDone", name, Text[enable ? "ToolsFeatOn" : "ToolsFeatOff"]) : still ? $"{name}: {Text["ToolsOutcome_StillRunning"]}" : Text.Format("ACRunFailedText", name));
        ShowResultOf(name, command, result.Run);
        if (result.RestartNeeded) { restartPending = true; OnPropertyChanged(nameof(ShowRestartBanner)); }
        if (result.Ok) { Notice = Text.Format("ToolsFeatDone", name, Text[enable ? "ToolsFeatOn" : "ToolsFeatOff"]); NoticeIsError = false; }
        else { Notice = CodeText(result.Code); NoticeIsError = true; }
        // Read the list again so the rows show the state Windows reports.
        if (result.Run.Started || result.Code == RepairCode.VerifyFailed) await LoadFeaturesCoreQuietAsync().ConfigureAwait(true);
        RefreshHistory();
    }

    private async Task LoadFeaturesCoreQuietAsync()
    {
        FeatureSnapshot fresh;
        try { fresh = await features.ReadAsync().ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { return; }
        if (!fresh.Ok) return;
        snapshot = fresh; FillFeatures();
    }

    private async Task RevertAsync(ToolsHistoryRow row)
    {
        if (!CanStart) return;
        var record = row.Record;
        var name = record.Begin.Argument ?? "";
        var back = Enum.TryParse<FeatureState>(record.Begin.PreviousState, out var s) ? Text["ToolsFeatState_" + s] : "";
        var confirmation = new ActionConfirmation(Text["ToolsRevertTitle"], Text.Format("ToolsRevertBody", name, back), [name], Text["ToolsFeatWarnSafe"], Text["ToolsRevertPrimary"])
        {
            Changes = [new ConfirmChange(name, null, back)], Risk = RiskLevel.Low, Undo = ConfirmUndo.Exact,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        if (StartRun(name) is not { } token) return;
        FeatureApplyResult result;
        try { result = await features.RevertAsync(record.Begin.Id, NewProgress(), token).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { EndRun(RunResult.Error, Text.Format("ACRunFailedText", name)); Fail(Text["ActionFailedUnexpected"]); return; }
        EndRun(result.Ok ? RunResult.Ok : RunResult.Error, result.Ok ? Text.Format("ToolsFeatDone", name, back.ToLower(Text.Culture)) : Text.Format("ACRunFailedText", name));
        if (result.Run.Run is not null) ShowResultOf(name, record.Begin.Command, result.Run);
        if (result.RestartNeeded) { restartPending = true; OnPropertyChanged(nameof(ShowRestartBanner)); }
        Notice = result.Ok ? Text.Format("ToolsFeatDone", name, back.ToLower(Text.Culture)) : CodeText(result.Code);
        NoticeIsError = !result.Ok;
        if (result.Run.Run is not null) await LoadFeaturesCoreQuietAsync().ConfigureAwait(true);
        RefreshHistory();
    }

    // ---- history ----

    private void RefreshHistory()
    {
        History.Clear();
        var shown = new List<HistoryRowViewModel>();
        IReadOnlyList<RepairRecord> all;
        try { all = repair.History(int.MaxValue); } // one read of the whole journal: whether a change is superseded depends on newer records
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { all = []; }
        foreach (var r in all.Take(HistoryLimit))
        {
            var can = snapshot is not null && features.CanRevert(r, snapshot, all) && IsElevated;
            History.Add(new(r, Describe(r), Text.Enum(RepairText.Result(r)), Text.When(r.BeganUtc.ToLocalTime(), "g"), can, RevertHistory, Text["ToolsUndo"], Text["ToolsUndoHelp"]));
            var h = History[^1];
            shown.Add(HistoryList.Row(r.Begin.Id, r.BeganUtc, RepairText.Result(r), h.Description, h.CanRevert, h.RevertCommand, h, new(h.UndoLabel, h.UndoHelp)));
        }
        HistoryList.Set(shown);
        OnPropertyChanged(nameof(HasHistory));
    }

    private string Describe(RepairRecord r) => RepairText.Describe(Text, r);
}

/// <summary>Display text of repair runs and feature changes, shared by the Tools page and the History page.</summary>
public static class RepairText
{
    /// <summary>The known features have a title in both languages; any other keeps its Windows name, pinned left to right.</summary>
    public static string FeatureTitle(LocalizationService text, string name)
    {
        var key = "Feat_" + new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + "_Title";
        return text.Name(text.Has(key) ? text[key] : name);
    }

    /// <summary>The status word of a run: a finished check counts as applied whether or not it found problems; a run the user stopped is Cancelled on every page; a run Winora stopped waiting for is Unconfirmed, never Failed.</summary>
    public static HistoryResult Result(RepairRecord r) => r.End?.Outcome switch
    {
        RepairOutcome.Succeeded or RepairOutcome.FoundProblems => HistoryResult.Applied,
        RepairOutcome.Cancelled => HistoryResult.Cancelled,
        RepairOutcome.StillRunning => HistoryResult.Unconfirmed,
        _ => HistoryResult.Failed,
    };

    public static string Describe(LocalizationService text, RepairRecord r)
    {
        switch (r.Begin.Tool)
        {
            case RepairTool.FeatureEnable or RepairTool.FeatureDisable:
                var line = text.Format("ToolsHistoryFeature", text[r.Begin.Tool == RepairTool.FeatureEnable ? "ToolsHistoryOn" : "ToolsHistoryOff"], FeatureTitle(text, r.Begin.Argument ?? ""));
                return r.Begin.RevertOf is null ? line : line + " " + text["ToolsHistoryUndo"];
            default:
                var info = RepairTools.Find(r.Begin.Tool);
                var title = info is null ? r.Begin.Tool.ToString() : text["Tools_" + info.Id + "_Title"];
                // A drive letter such as "C:" is Latin data: pinned, or the colon moves to the other side in a right-to-left row.
                return r.Begin.Argument is { } arg ? text.Format("ToolsHistoryTarget", title, text.Ltr(arg)) : title;
        }
    }
}
