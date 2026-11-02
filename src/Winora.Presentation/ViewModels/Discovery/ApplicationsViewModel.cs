// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>One program in the Apps list. It can be ticked for a removal, removed on its own, or shown as locked with the reason.</summary>
public sealed class AppRow : DiscoveryRow
{
    private readonly ApplicationsViewModel page;

    public AppRow(DiscoveryRow source, ApplicationsViewModel page, bool canSelect, string? lockText, string removeLabel, string removeName, string selectName, RelayCommand<AppRow> remove) : base(source)
    {
        this.page = page; CanSelect = canSelect; LockText = lockText ?? ""; RemoveLabel = removeLabel; RemoveName = removeName; SelectName = selectName; RemoveCommand = remove;
    }


    public bool CanSelect { get; }
    public bool IsLocked => LockText.Length > 0;
    public string LockText { get; }
    /// <summary>The app is gone already: the badge is a plain fact, not the lock of a protected app.</summary>
    public bool IsAlreadyRemoved { get; init; }
    public string RemoveLabel { get; }
    public string RemoveName { get; }
    public string SelectName { get; }
    public RelayCommand<AppRow> RemoveCommand { get; }
    public AppIconLoader Icon { get; init; } = new(null, null);
    public bool IsSelected { get => page.IsSelected(Key); set => page.SetSelected(Key, value); }
    public void RefreshSelection() => RaisePropertyChanged(nameof(IsSelected));
}

/// <summary>The result line of one program after a removal. A program that was removed gets a "Find leftovers" button (<see cref="FindCommand"/>) until its leftovers were looked for.</summary>
public sealed record AppResultLine(string Name, string Status, bool IsProblem)
{
    /// <summary>The key of the removed program whose leftovers the button looks for.</summary>
    public string? LeftoverKey { get; init; }
    public System.Windows.Input.ICommand? FindCommand { get; init; }
    public bool CanFindLeftovers => FindCommand is not null && LeftoverKey is not null;
    public string FindLabel { get; init; } = "";
    public string FindName { get; init; } = "";
    public string FindHelp { get; init; } = "";
    /// <summary>What a screen reader says for the row.</summary>
    public string AutomationName => $"{Name}, {Status}";
    // UI Automation names an item of a plain list by its text; a record would otherwise print its fields.
    public override string ToString() => AutomationName;
}

/// <summary>The part above the list: the result of the last removal. What is running, and its Stop, are in the Action center.</summary>
public sealed class AppsBannerPanel(ApplicationsViewModel page) : ObservableObject
{
    public bool HasResults => page.ResultLines.Count > 0;
    public IReadOnlyList<AppResultLine> Results => page.ResultLines;
    public string ResultsTitle => page.ResultsTitle;
    public BannerSeverity ResultsSeverity => page.ResultsHaveProblem ? BannerSeverity.Warning : BannerSeverity.Success;
    public string DismissLabel => page.Text["V2ApDismiss"];
    public System.Windows.Input.ICommand DismissCommand => page.DismissResultsCommand;
    public string NoteText => page.Text["V2ApNote"];
    public bool ShowNote => page.CanRemove;
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The Apps page. Programs registered with Windows can be removed here: the page lists them, locks the ones Windows or Winora needs with the reason, and removes the ones the person ticks (or
/// one row on its own) after one review that says what cannot be undone. Without the removal service (a view-only build) the list is read only.
/// </summary>
public sealed class ApplicationsViewModel : DiscoveryPageViewModel<InstalledApplication>
{
    private readonly AppUninstallService? service;
    private readonly LeftoverService? leftovers;
    private readonly Dictionary<string, InstalledApplication> removedApps = new(StringComparer.Ordinal);
    private readonly IReviewPrompt? prompt;
    private readonly IAppIconSource? icons;
    private readonly AppsBannerPanel banner;
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> known = new(StringComparer.Ordinal);
    private Task work = Task.CompletedTask;
    private DateTimeOffset removedAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? run;
    private bool busy;
    private IReadOnlyList<AppResultLine> results = [];
    private bool resultsHaveProblem;
    private Func<string>? resultsTitle;

    public ApplicationsViewModel(LocalizationService text, SimulationModel model, ErrorPresenter errors, InventoryModel inventory, AppUninstallService? service = null, IReviewPrompt? prompt = null, IAppIconSource? icons = null,
        LeftoverService? leftovers = null)
        : base("Applications", IconKey.Apps24, text, model, errors, service is null ? inventory.Applications : new InventorySlot<InstalledApplication>(service.ScanAsync))
    {
        this.service = service; this.prompt = prompt; this.icons = icons; this.leftovers = leftovers;
        banner = new(this);
        var scope = AddChoice("FilterScope", i => i.Identity.Scope);
        var kind = AddChoice("FilterKind", i => i.Identity.Kind);
        AddToggle("FilterHideSystem", i => i.IsSystemComponent);
        ChoiceFilter[] managed = [scope, kind];
        AddChoiceChip("all", "DiscFilterAll", managed, null, null);
        AddChoiceChip("user", "Enum_InstallationScope_User", managed, scope, nameof(InstallationScope.User));
        AddChoiceChip("machine", "Enum_InstallationScope_Machine", managed, scope, nameof(InstallationScope.Machine));
        AddChoiceChip("msix", "Enum_InstallationKind_Msix", managed, kind, nameof(InstallationKind.Msix));
        AddSort("SortName", i => i.DisplayName);
        AddSort("SortPublisher", i => i.Identity.Publisher);
        AddSort("SortSize", i => i.EstimatedSizeKb);
        AddSort("SortInstalled", i => i.InstalledOn);
        RemoveRowCommand = new(row => { if (row is not null) work = Run([row.Key]); }, row => row is { CanSelect: true } && CanRemove);
        ReviewCommand = new(() => work = Run([.. selected]), () => selected.Count > 0 && CanRemove);
        DiscardCommand = new(ClearSelection, () => selected.Count > 0);
        StopCommand = new(() => run?.Cancel(), () => run is not null);
        DismissResultsCommand = new(() => { results = []; resultsTitle = null; banner.Refresh(); });
        FindLeftoversCommand = new(line => { if (line is not null) work = FindLeftoversAsync(line); }, line => line is { CanFindLeftovers: true } && leftovers is not null && prompt is not null && !busy);
    }

    public RelayCommand<AppRow> RemoveRowCommand { get; }
    public RelayCommand ReviewCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand DismissResultsCommand { get; }
    public RelayCommand<AppResultLine> FindLeftoversCommand { get; }
    /// <summary>How long after a removal the leftover scan waits before it looks, so an uninstaller that removes its own folder late has done so.</summary>
    public TimeSpan LeftoverSettle { get; set; } = TimeSpan.FromSeconds(5);

    protected override IReviewPrompt? DetailsHost => prompt;

    /// <summary>Remove is offered in the dialog when the row can be removed; it goes through the same review as the row button.</summary>
    protected override IReadOnlyList<DetailsAction> DetailActions(DiscoveryRow row) =>
        row is AppRow { CanSelect: true } app && RemoveRowCommand.CanExecute(app)
            ? [new(app.RemoveLabel, app.RemoveName, Text["V2ApRemoveHelp"], () => { RemoveRowCommand.Execute(app); return Task.CompletedTask; }, IsDanger: true)]
            : [];

    public override bool ToggleRow(DiscoveryRow row)
    {
        if (row is not AppRow { CanSelect: true } app) return false;
        app.IsSelected = !app.IsSelected;
        return true;
    }

    public override string Intro => Text["V2ApIntro"];
    public override bool ShowReadOnlyNotice => service is null;
    public override object? BannerPanel => banner;
    public override bool IsBusy => base.IsBusy || busy;
    public bool CanRemove => service is not null && prompt is not null && !busy && !base.IsBusy;
    public IReadOnlyList<AppResultLine> ResultLines => results;
    public bool ResultsHaveProblem => resultsHaveProblem;
    public string ResultsTitle => resultsTitle?.Invoke() ?? "";

    public override int PendingCount => selected.Count;
    public override string PendingSummaryText => Text.Format(selected.Count == 1 ? "V2ApPendingOne" : "V2ApPending", selected.Count);
    public override System.Windows.Input.ICommand? PendingReviewCommand => ReviewCommand;
    public override System.Windows.Input.ICommand? PendingDiscardCommand => DiscardCommand;
    public override IReadOnlyList<string> PendingNames => [.. selected.Select(NameOf).Order(StringComparer.CurrentCultureIgnoreCase)];
    protected override IReadOnlyList<StagedChange> StagedItems => selected.Select(k => (Key: k, Name: NameOf(k))).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(x => new StagedChange(x.Key, x.Name, "", "", null, () => { if (!IsBusy) { SetSelected(x.Key, false); foreach (var row in Rows.OfType<AppRow>()) row.RefreshSelection(); } })).ToList();
    public override string PendingReviewText => Text["V2ApReview"];
    public override string PendingReviewHelp => Text["V2ApReviewHelp"];
    public override string PendingDiscardText => Text["V2ApClear"];
    public override string PendingDiscardHelp => Text["V2ApClearHelp"];

    public override async Task WhenIdleAsync() { await base.WhenIdleAsync(); await work; }

    protected override void OnLanguageChanged() { base.OnLanguageChanged(); banner.Refresh(); RaisePending(); }

    protected override void OnResultApplied(DiscoveryResult<InstalledApplication> value)
    {
        var keys = value.Items.Select(AppUninstallService.KeyOf).ToHashSet(StringComparer.Ordinal);
        selected.RemoveWhere(k => !keys.Contains(k));
        RaisePending();
    }

    private string NameOf(string key) => CurrentResult?.Items.FirstOrDefault(i => AppUninstallService.KeyOf(i) == key)?.DisplayName ?? key;

    // ---- selection ----

    public bool IsSelected(string key) => selected.Contains(key);

    public void SetSelected(string key, bool value)
    {
        if (value && known.TryGetValue(key, out var canSelect) && !canSelect) return;
        if (value ? !selected.Add(key) : !selected.Remove(key)) return;
        RaisePending();
    }

    private void ClearSelection()
    {
        selected.Clear();
        foreach (var row in Rows.OfType<AppRow>()) row.RefreshSelection();
        RaisePending();
    }

    private void RaisePending()
    {
        foreach (var name in new[] { nameof(PendingCount), nameof(PendingSummaryText), nameof(PendingNames) }) OnPropertyChanged(name);
        ReviewCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged(); RemoveRowCommand.NotifyCanExecuteChanged();
    }

    // ---- removal ----

    private string Reason(string code) => Text.Has("V2ApReason_" + code) ? Text["V2ApReason_" + code] : Text["V2ApReason_failed"];

    private async Task Run(IReadOnlyList<string> keys)
    {
        var apps = (CurrentResult?.Items ?? []).Where(i => keys.Contains(AppUninstallService.KeyOf(i))).ToList();
        if (apps.Count == 0 || service is null || prompt is null) return;
        var preview = service.Preview(apps);
        var runnable = preview.Items.Where(i => i.WillRun).Select(i => i.App).ToList();
        if (runnable.Count == 0)
        {
            Show(preview.Items.Select(i => new AppResultLine(i.App.DisplayName, Text.Format("V2ApNotRemoved", Reason(i.RefusalCode ?? AppCode.Failed)), true)).ToList(), () => Text["V2ApResultsTitleNone"], true);
            return;
        }
        var methods = preview.Items.Where(i => i.WillRun).Select(i => i.Assessment.Method).ToList();
        var confirmation = new ActionConfirmation(
            runnable.Count == 1 ? Text.Format("V2ApConfirmTitleOne", runnable[0].DisplayName) : Text.Format("V2ApConfirmTitle", runnable.Count), Text["V2ApConfirmBody"], runnable.Select(a => a.DisplayName).ToList(), Text["V2ApWarning"], Text["V2ApConfirmPrimary"])
        {
            Changes = preview.Items.Where(i => i.WillRun).Select(i => new ConfirmChange(i.App.DisplayName, Describe(i.App), Text["V2ApGone"], Technical(i), HowNote(i.Assessment))).ToList(),
            Risk = RiskLevel.Medium, Undo = methods.All(m => m == AppRemoval.Package) ? ConfirmUndo.Reinstall : ConfirmUndo.No, Restart = null, Irreversible = true,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;

        using var activityRun = Runs.Begin(Text.Format("ACRunApps", runnable.Count), true, () => run?.Cancel());
        if (!activityRun.Started) return;
        busy = true; OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); results = []; resultsTitle = null;
        using var cts = run = new CancellationTokenSource();
        StopCommand.NotifyCanExecuteChanged();
        AppRemovalRun outcome;
        try
        {
            var report = new Progress<AppRemovalProgress>(p => { if (run is null) return; activityRun.Report(p.Total == 0 ? null : (double)Math.Max(0, p.Index - 1) / p.Total); banner.Refresh(); });
            outcome = await service.RemoveAsync(runnable, report, cts.Token).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            activityRun.Finish(RunResult.Error, Text.Format("ACRunFailedText", Text.Format("ACRunApps", runnable.Count)));
            busy = false; run = null; OnPropertyChanged(nameof(IsBusy)); RefreshCommands();
            Show([new(Text["V2ApResultsUnexpected"], Text["V2ApReason_failed"], true)], () => Text["V2ApResultsTitleNone"], true);
            return;
        }
        run = null;
        foreach (var app in apps) removedApps[AppUninstallService.KeyOf(app)] = app;
        removedAt = DateTimeOffset.UtcNow;
        var lines = outcome.Items.Select(Line).Concat(preview.Items.Where(i => !i.WillRun).Select(i => new AppResultLine(i.App.DisplayName, Text.Format("V2ApNotRemoved", Reason(i.RefusalCode ?? AppCode.Failed)), true))).ToList();
        var removed = outcome.Items.Count(i => i.State is AppRemovalState.Removed or AppRemovalState.PendingRestart);
        var problem = lines.Any(l => l.IsProblem);
        var freed = outcome.FreedKb;
        selected.RemoveWhere(k => outcome.Items.Any(i => i.Key == k && i.State is AppRemovalState.Removed or AppRemovalState.PendingRestart));
        busy = false; OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); RaisePending();
        Show(lines, () => removed == 0 ? Text["V2ApResultsTitleNone"] : freed > 0 ?Text.Format("V2ApResultsTitleFreed", removed, lines.Count, Size(freed)) : Text.Format("V2ApResultsTitle", removed, lines.Count), problem);
        if (!problem) Feedback.Success(ResultsTitle);
        activityRun.Finish(cts.IsCancellationRequested ? RunResult.Stopped : problem ? RunResult.Warning : RunResult.Ok, ResultsTitle);
        await ScanAsync().ConfigureAwait(true);
        foreach (var row in Rows.OfType<AppRow>()) row.RefreshSelection();
    }

    private AppResultLine Line(AppRemovalResult r)
    {
        var status = r.State switch
        {
            AppRemovalState.Removed => Text["V2ApState_Removed"],
            AppRemovalState.PendingRestart => Text["V2ApState_PendingRestart"],
            AppRemovalState.Cancelled => Text["V2ApState_Cancelled"],
            AppRemovalState.StillRunning => Text["V2ApState_StillRunning"],
            _ => Text.Format("V2ApNotRemoved", Reason(r.Code)),
        };
        var removed = r.State is AppRemovalState.Removed or AppRemovalState.PendingRestart;
        if (removed && r.Code == AppCode.Partial) status = Text["V2ApState_RemovedPartial"];
        var line = new AppResultLine(r.Name, status, !removed || r.Code == AppCode.Partial);
        // A Store app keeps its files in folders Windows manages, so only desktop programs are offered a leftover scan.
        if (!removed || leftovers is null || !removedApps.TryGetValue(r.Key, out var app) || app.Identity.Kind == InstallationKind.Msix) return line;
        return line with
        {
            LeftoverKey = r.Key, FindCommand = FindLeftoversCommand, FindLabel = Text["V2ApLeftoversFind"], FindName = Text.Format("V2ApLeftoversFindName", r.Name), FindHelp = Text["V2ApLeftoversFindHelp"],
        };
    }

    // ---- leftovers ----

    private string LeftoverReason(string code) => Text.Has("V2ClReason_" + code) ? Text["V2ClReason_" + code] : Text["V2ClReason_failed"];

    private string PlaceText(LeftoverPlace place) => Text["V2ApLeftoversPlace_" + place];

    /// <summary>
    /// Looks for what the removed program left behind, shows it in the review dialog with a box per folder, and moves the ticked folders into Quarantine. The install folder and the
    /// ProgramData folders start ticked; folders in AppData hold the person's own settings, so they start unticked. Nothing is deleted here.
    /// </summary>
    private async Task FindLeftoversAsync(AppResultLine line)
    {
        if (leftovers is null || prompt is null || line.LeftoverKey is not { } key || !removedApps.TryGetValue(key, out var app)) return;
        var target = new LeftoverTarget(key, app.DisplayName, app.Identity.Publisher, app.InstallLocation, app.Identity.Scope);
        var installed = CurrentResult?.Items ?? [];
        using var scanRun = Runs.Begin(Text.Format("ACRunLeftoversScan", app.DisplayName), false);
        SetLeftoverBusy(true);
        // Many uninstallers delete their own folder a moment after they exit; a scan right away would offer a folder that is about to vanish.
        var settle = removedAt + LeftoverSettle - DateTimeOffset.UtcNow;
        if (settle > TimeSpan.Zero) await Task.Delay(settle).ConfigureAwait(true);
        LeftoverFindings findings;
        try { findings = await Task.Run(() => leftovers.Find(target, installed, CancellationToken.None)).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { findings = new(target, [], []); }
        finally { SetLeftoverBusy(false); scanRun.Finish(RunResult.Ok, quiet: true); }

        if (findings.Folders.Count == 0) { Replace(line, line with { Status = Text["V2ApLeftoversNone"], LeftoverKey = null, FindCommand = null }); return; }
        var choices = findings.Folders.Select(f => (Folder: f, Choice: new ConfirmChoice(f.Place is LeftoverPlace.InstallFolder or LeftoverPlace.ProgramData))).ToList();
        var confirmation = new ActionConfirmation(Text.Format("V2ApLeftoversTitle", app.DisplayName), Text.Format("V2ApLeftoversBody", LeftoverService.RetentionDays), [], Text["V2ApLeftoversWarning"], Text["V2ApLeftoversPrimary"])
        {
            Changes = choices.Select(c => new ConfirmChange(c.Folder.Path, Note: Text.Format("V2ApLeftoversNote", PlaceText(c.Folder.Place), Text.Size(c.Folder.Bytes), Text.Number(c.Folder.Files))) { Choice = c.Choice }).ToList(),
            Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, Restart = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        var chosen = choices.Where(c => c.Choice.IsChecked).Select(c => c.Folder).ToList();
        if (chosen.Count == 0) return;

        using var moveRun = Runs.Begin(Text.Format("ACRunLeftovers", app.DisplayName), true);
        if (!moveRun.Started) return;
        SetLeftoverBusy(true);
        IReadOnlyList<LeftoverMoveResult> moved;
        try { moved = await Task.Run(() => leftovers.Move(target, chosen, CancellationToken.None)).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { moved = chosen.Select(f => new LeftoverMoveResult(f, new(QuarantineStatus.Failed, QuarantineReason.Unexpected, null))).ToList(); }
        finally { SetLeftoverBusy(false); }
        Journal.Wrote(HistoryArea.Cleanup);

        var done = moved.Where(m => m.Result.Ok).ToList();
        var gone = moved.Count(m => m.Result.Reason == QuarantineReason.AlreadyGone);
        var failed = moved.FirstOrDefault(m => !m.Result.Ok && m.Result.Reason != QuarantineReason.AlreadyGone);
        var status = failed is not null ? Text.Format("V2ApLeftoversSome", done.Count, moved.Count, LeftoverReason(failed.Result.Reason))
            : done.Count == 0 ? Text["V2ApLeftoversAllGone"]
            : Text.Format("V2ApLeftoversMoved", done.Count, Text.Size(done.Sum(m => m.Folder.Bytes))) + (gone > 0 ? " " + Text.Format("V2ApLeftoversGoneNote", gone) : "");
        Replace(line, line with { Status = status, IsProblem = failed is not null, LeftoverKey = null, FindCommand = null });
        if (failed is null) Feedback.Success(status);
        moveRun.Finish(failed is null ? RunResult.Ok : RunResult.Warning, status);
    }

    private void SetLeftoverBusy(bool on)
    {
        busy = on;
        OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); FindLeftoversCommand.NotifyCanExecuteChanged();
    }

    private void Replace(AppResultLine old, AppResultLine updated)
    {
        results = results.Select(l => ReferenceEquals(l, old) || (l.LeftoverKey is not null && l.LeftoverKey == old.LeftoverKey) ? updated : l).ToList();
        resultsHaveProblem = results.Any(l => l.IsProblem);
        banner.Refresh();
    }

    private void Show(IReadOnlyList<AppResultLine> lines, Func<string> title, bool problem)
    {
        results = lines; resultsTitle = title; resultsHaveProblem = problem; banner.Refresh();
    }

    private void RefreshCommands() { RemoveRowCommand.NotifyCanExecuteChanged(); ReviewCommand.NotifyCanExecuteChanged(); banner.Refresh(); }

    private string Describe(InstalledApplication a) => string.Join(" · ", new[] { a.Version, a.EstimatedSizeKb is { } kb ? Size(kb) : null }.Where(s => !string.IsNullOrEmpty(s)));

    private string? Technical(AppRemovalItem item)
    {
        var plan = item.Assessment.Plan;
        if (item.App.Identity.Kind == InstallationKind.Msix) return item.App.Identity.StableIdentity;
        if (plan is null) return null;
        return plan.IsMsi ? $"msiexec.exe /x {plan.MsiProductCode} /qn /norestart" : AppUninstallPlanner.ExecutableHint(plan);
    }

    private string HowNote(AppAssessment assessment)
    {
        var how = assessment.Method switch
        {
            AppRemoval.Interactive => Text["V2ApHowInteractive"],
            AppRemoval.Package => Text["V2ApHowPackage"],
            _ => Text["V2ApHowQuiet"],
        };
        return assessment.Plan?.RunsAsUser == true ? how + " " + Text["V2ApHowAsUser"] : how;
    }

    // ---- rows ----

    protected override IEnumerable<SummaryStat> BuildSummary(IReadOnlyList<InstalledApplication> items)
    {
        yield return new(Text.Number(items.Count), Text["V2MaStatApps"]);
        yield return new(Size(items.Sum(i => i.EstimatedSizeKb ?? 0)), Text["V2MaStatSize"]);
        yield return new(Text.Number(items.Count(i => i.Identity.Scope == InstallationScope.User)), Text["Enum_InstallationScope_User"]);
        yield return new(Text.Number(items.Count(i => i.IsSystemComponent)), Text["BadgeSystem"]);
    }

    protected override IEnumerable<string?> SearchFields(InstalledApplication i) =>
        [i.DisplayName, i.Identity.Publisher, i.InstallLocation, i.Version, i.Identity.StableIdentity];

    protected override DiscoveryRow BuildRow(InstalledApplication i)
    {
        var key = AppUninstallService.KeyOf(i);
        var assessment = service?.Assess(i);
        string? lockText = null;
        var canSelect = false;
        if (service is not null && assessment is not null)
        {
            if (assessment.LockCode is { } code) lockText = Text.Has("V2ApLock_" + code) ? Text["V2ApLock_" + code] : Text["V2ApLock_windows_component"];
            else if (!service.IsElevated) lockText = Text["V2ApLock_needs_admin"];
            else canSelect = true;
        }
        known[key] = canSelect;
        List<BadgeModel> badges = [new(Text.Enum(i.Identity.Kind), Tone.Info), Neutral(Text.Enum(i.Identity.Scope))];
        if (i.IsSystemComponent) badges.Add(Neutral(Text["BadgeSystem"]));
        // Publisher and version are Latin tokens: isolate them so numbers and dots keep their order inside right-to-left rows.
        var subtitle = Bidi.Compose(" · ", Text.Direction, (i.Identity.Publisher, true), (i.Version, true), (i.EstimatedSizeKb is { } kb ? Size(kb) : null, false));
        var plain = new DiscoveryRow(key, PackageText.IsGuidName(i.DisplayName) ? Text["AppUnnamedPackage"] : i.DisplayName, subtitle, false, "", badges,
            () =>
            [
                new(Text["FieldName"], i.DisplayName),
                new(Text["FieldVersion"], Or(i.Version)),
                new(Text["FieldPublisher"], Or(i.Identity.Publisher)),
                new(Text["FieldKind"], Text.Enum(i.Identity.Kind)),
                new(Text["FieldScope"], Text.Enum(i.Identity.Scope)),
                new(Text["FieldInstalledOn"], i.InstalledOn is { } d ? Text.When(d.ToDateTime(TimeOnly.MinValue), "d") : Text["ValueNotRecorded"]),
                new(Text["FieldSize"], i.EstimatedSizeKb is { } kb ? Size(kb) : Text["ValueNotRecorded"]),
                new(Text["FieldSystemComponent"], YesNo(i.IsSystemComponent)),
                new(Text["FieldInstallLocation"], Or(i.InstallLocation), true),
                new(Text["FieldRegistryView"], i.Identity.RegistryView is { } v ? Text.Enum(v) : Text["ValueNotRecorded"]),
                new(Text["FieldProvenance"], Or(i.RegistrationProvenance), true),
                new(Text["FieldIdentifier"], i.Identity.StableIdentity, true),
                new(Text["V2ApFieldRemoval"], assessment is null ? Text["ValueNotRecorded"] : assessment.LockCode is { } c ? Text.Format("V2ApFieldLocked", Text["V2ApLock_" + (Text.Has("V2ApLock_" + c) ? c : "windows_component")]) : HowNote(assessment)),
            ]);
        return new AppRow(plain, this, canSelect, lockText, Text["V2ApRemove"], Text.Format("V2ApRemoveName", i.DisplayName), Text.Format("V2ApSelectName", i.DisplayName), RemoveRowCommand) { Icon = new AppIconLoader(icons, AppIconKeyOf(i)), IsAlreadyRemoved = assessment?.LockCode == AppCode.NotInstalled };
    }

    /// <summary>A Store app shows the logo of its package. A desktop program shows the icon its registration names (an .exe file, optionally followed by an icon index).</summary>
    public static AppIconKey? AppIconKeyOf(InstalledApplication i)
    {
        if (i.Identity.Kind == InstallationKind.Msix) return i.InstallLocation is { Length: > 0 } location ? new AppIconKey(AppIconKind.Package, location) : null;
        if (i.DisplayIcon is not { Length: > 0 } text) return null;
        var path = text.Trim();
        if (path.StartsWith('"')) { var end = path.IndexOf('"', 1); if (end < 0) return null; path = path[1..end]; }
        else { var comma = path.LastIndexOf(','); if (comma > 0 && int.TryParse(path[(comma + 1)..].Trim(), out _)) path = path[..comma]; }
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) ? new AppIconKey(AppIconKind.File, path) : null;
    }

    /// <summary>Human-readable size from the registry's kilobyte estimate, in the selected culture.</summary>
    private string Size(long kb) => Text.Size(kb * 1024);
}
