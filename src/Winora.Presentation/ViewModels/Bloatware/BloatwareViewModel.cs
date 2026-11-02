// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;

namespace Winora.Desktop;

/// <summary>One choice of a filter list. ToString is the label because screen readers read the item text from it.</summary>
public sealed record BloatFilterItem(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One package in the list. The user can tick it unless Winora will not remove it; the reason is then the text of the row.</summary>
public sealed class BloatRow : ObservableObject
{
    private bool selected;
    private long? size;
    private readonly Action changed;
    private readonly Func<long?, string> sizeText;

    public BloatRow(BloatItem item, string classText, Tone classTone, string reason, string scopeText, string? blockText, IReadOnlyList<DetailField> details, Func<long?, string> sizeText, Action changed)
    {
        Item = item; ClassText = classText; ClassTone = classTone; Reason = reason; ScopeText = scopeText; BlockText = blockText; Details = details; this.sizeText = sizeText; this.changed = changed;
    }

    public BloatItem Item { get; }
    /// <summary>The real icon of the app (the letter badge shows while it is null).</summary>
    public AppIconLoader Icon { get; init; } = new(null, null);
    public AppPackage Package => Item.Package;
    public string Title => Package.DisplayName;
    public string Subtitle => $"{Package.Name} · {Package.Version}";
    public string ClassText { get; }
    public Tone ClassTone { get; }
    /// <summary>Safe to remove reads as Low risk; Caution and Unknown packages as Medium. A protected package is not removable, so its chip is the "Protected by Winora" badge instead.</summary>
    public RiskLevel Risk => Item.Class == BloatClass.Safe ? RiskLevel.Low : RiskLevel.Medium;
    public bool IsProtected => Item.Class == BloatClass.Protected;
    public bool ShowRisk => !IsProtected;
    /// <summary>Only an app that needs a second look carries a badge; a safe app is the plain case.</summary>
    public bool ShowMedium => Item.Class is BloatClass.Caution or BloatClass.Unknown;
    public string RiskText { get; init; } = "";
    public string ProtectedText { get; init; } = "";
    public string Reason { get; }
    public string ScopeText { get; }
    public string? BlockText { get; }
    public bool HasBlock => BlockText is not null;
    public IReadOnlyList<DetailField> Details { get; }
    public bool CanSelect => Item.CanRemove;
    /// <summary>Winora will not remove this app: the row shows a lock where the tick box would be.</summary>
    public bool IsLocked => !CanSelect;
    /// <summary>True for every row but the first of its group: a thin line separates the rows inside the card.</summary>
    public bool ShowDivider { get; set; }
    public long? SizeBytes { get => size; set { if (SetProperty(ref size, value)) { OnPropertyChanged(nameof(SizeText)); changed(); } } }
    public string SizeText => sizeText(size);
    public bool IsSelected
    {
        get => selected && CanSelect;
        set { if (CanSelect && SetProperty(ref selected, value)) changed(); }
    }
    /// <summary>Opens the details dialog of this app. Set by the page.</summary>
    public System.Windows.Input.ICommand? DetailsCommand { get; init; }
    public string MoreName { get; init; } = "";
    public string DetailsName { get; init; } = "";
    public string AutomationName => $"{Title}, {ClassText}, {ScopeText}";
    public string HelpText => BlockText ?? Reason;
}

/// <summary>A heading and the packages under it ("Review these first", "Keep unless you know you do not need it").</summary>
public sealed class BloatGroup(string heading, IReadOnlyList<BloatRow> rows, BloatGroupKind kind = BloatGroupKind.Safe, string description = "", Func<string>? selectLabel = null, System.Windows.Input.ICommand? selectCommand = null) : ObservableObject
{
    public string Heading { get; } = heading;
    public IReadOnlyList<BloatRow> Rows { get; } = rows;
    public BloatGroupKind Kind { get; } = kind;
    public string Description { get; } = description;
    /// <summary>The Keep group lists apps Winora does not remove: its rows have a lock and no tick box.</summary>
    public bool IsLocked => Kind == BloatGroupKind.Keep;
    /// <summary>"Select all" or "Select none", depending on whether every removable app of the group is ticked.</summary>
    public string SelectLabel => selectLabel?.Invoke() ?? "";
    public System.Windows.Input.ICommand? SelectCommand { get; } = selectCommand;
    public bool CanSelectAll => !IsLocked && SelectCommand is not null && Rows.Any(r => r.CanSelect);
    public string AutomationName => $"{Heading}, {Rows.Count}";
    public void RefreshSelection() => OnPropertyChanged(nameof(SelectLabel));
}

/// <summary>The three groups of the page: safe to remove, optional (Winora can remove them, check first) and keep (protected).</summary>
public enum BloatGroupKind { Safe, Optional, Keep }

/// <summary>One removed app on the Removed tab: how it can come back (or why it cannot), and the buttons for it.</summary>
public sealed class RemovedAppRow(RemovedApp app, string title, string subtitle, string when, string howText, string restoreLabel, string storeLabel, RelayCommand<RemovedAppRow> restore, RelayCommand<RemovedAppRow> openStore)
{
    public RemovedApp App { get; } = app;
    public AppIconLoader Icon { get; init; } = new(null, null);
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string When { get; } = when;
    public string HowText { get; } = howText;
    public string RestoreLabel { get; } = restoreLabel;
    public string StoreLabel { get; } = storeLabel;
    public bool CanRestore => App.Option.Kind != ReinstallKind.None;
    public bool CanOpenStore => App.Option.Kind != ReinstallKind.None && App.Record.Change.Signature is PackageSignature.Store or PackageSignature.Developer;
    public bool ShowDivider { get; set; }
    public RelayCommand<RemovedAppRow> RestoreCommand { get; } = restore;
    public RelayCommand<RemovedAppRow> OpenStoreCommand { get; } = openStore;
    public string AutomationName => $"{Title}, {When}, {HowText}";
    /// <summary>Accessible names of the row buttons: the label alone repeats on every row.</summary>
    public string RestoreName => $"{RestoreLabel}, {Title}";
    public string StoreName => $"{StoreLabel}, {Title}";
}

public sealed class BloatHistoryRow(BloatChangeRecord record, string description, string stateText, string when, string stepsText, ReinstallOption option, string reinstallLabel, RelayCommand<BloatHistoryRow> reinstall)
{
    public BloatChangeRecord Record { get; } = record;
    public string Description { get; } = description;
    public string StateText { get; } = stateText;
    public string When { get; } = when;
    public string StepsText { get; } = stepsText;
    public ReinstallOption Option { get; } = option;
    public string ReinstallLabel { get; } = reinstallLabel;
    public bool CanReinstall => Option.Kind != ReinstallKind.None;
    public RelayCommand<BloatHistoryRow> ReinstallCommand { get; } = reinstall;
    public string AutomationName => $"{Description}, {StateText}, {When}";
}

public enum BloatPhase { Idle, Loading, Removing }

/// <summary>
/// The Bloatware page: the Microsoft Store (MSIX) packages of all users and the provisioned copies for new users, each with a class (decision DEC-IW-028), a preview of what a removal does,
/// one strong confirmation, a progress view with cancel, a report and a history. A removed app is listed on the Removed tab and in the history with a Restore button: Winora registers the app
/// again from files that are still on the disk, else it opens the Store page of the app and waits until Windows has installed it.
/// </summary>
public sealed class BloatwareViewModel : PageViewModel, INoticePage
{
    private const int HistoryLimit = 30;
    private readonly BloatwareService service;
    private readonly IReviewPrompt prompt;
    private readonly IElevationState elevation;
    private readonly IStoreLauncher launcher;
    private CancellationTokenSource? cts;
    private Task work = Task.CompletedTask;
    private BloatPhase phase;
    private BloatListing? listing;
    private string search = "";
    private BloatFilterItem classFilter, scopeFilter;
    private string? notice, loadError, progressText;
    private bool noticeIsError;
    private RemovalRunResult? lastRun;
    private readonly IAppIconSource? icons;
    private int selectedTab;

    public BloatwareViewModel(LocalizationService text, SimulationModel model, BloatwareService service, IReviewPrompt prompt, IElevationState elevation, IStoreLauncher launcher, IAppIconSource? icons = null, Func<string, bool>? copy = null) : base("Bloatware", text, model)
    {
        HistoryList = new(text, HistoryArea.Bloatware, () => OpenHistory) { NoteSource = () => Text["BloatHistoryNote"] };
        this.copy = copy ?? HostServices.Clipboard.TrySetText;
        this.icons = icons; this.service = service; this.prompt = prompt; this.elevation = elevation; this.launcher = launcher;
        ClassFilters = BuildClassFilters(); ScopeFilters = BuildScopeFilters();
        classFilter = ClassFilters[0]; scopeFilter = ScopeFilters[0];
        RefreshCommand = new(() => work = LoadAsync(), () => Phase == BloatPhase.Idle);
        ClearFiltersCommand = new(() => { SearchText = ""; SelectedClassFilter = ClassFilters[0]; SelectedScopeFilter = ScopeFilters[0]; });
        SelectSafeCommand = new(SelectSafe, () => Phase == BloatPhase.Idle && Rows.Any(r => r.CanSelect && r.Item.Class == BloatClass.Safe));
        ClearCommand = new(ClearSelection, () => Phase == BloatPhase.Idle && SelectedRows.Any());
        RemoveCommand = new(() => work = RemoveAsync(), () => CanRemove);
        CancelCommand = new(() => cts?.Cancel(), () => Phase is BloatPhase.Loading or BloatPhase.Removing);
        ReinstallHistory = new(row => { if (row is not null) work = RestoreAsync(row.Record.Change.Id, row.Option, row.Record.Change.Shown, row.Description); }, row => row is not null && Phase == BloatPhase.Idle);
        RestoreCommand = new(row => { if (row is not null) work = RestoreAsync(row.App.Record.Change.Id, row.App.Option, row.App.Record.Change.Shown, row.Title); }, row => row is not null && Phase == BloatPhase.Idle);
        OpenStoreCommand = new(row => { if (row is not null) OpenStorePage(row.App.Record.Change); });
        ShowDetailsCommand = new(ShowDetailsAsync, row => row is not null && !detailsOpen);
    }

    private readonly Func<string, bool> copy;
    private bool detailsOpen;
    private Task detailsTask = Task.CompletedTask;
    /// <summary>Opens the details dialog of an app. Shared by the row menu and a double click.</summary>
    public AsyncRelayCommand<BloatRow> ShowDetailsCommand { get; }
    /// <summary>Completes when the details dialog that is open now has closed. For tests.</summary>
    public Task WhenDetailsClosed => detailsTask;

    private Task ShowDetailsAsync(BloatRow? row) => detailsTask = ShowDetailsCoreAsync(row);

    private async Task ShowDetailsCoreAsync(BloatRow? row)
    {
        if (row is null || detailsOpen) return;
        detailsOpen = true; ShowDetailsCommand.NotifyCanExecuteChanged();
        try { await prompt.ShowDetailsAsync(DetailsOf(row)).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("The details dialog failed: {0}", e.GetType().Name); }
        finally { detailsOpen = false; ShowDetailsCommand.NotifyCanExecuteChanged(); }
    }

    /// <summary>The app as the dialog shows it: tags for class and risk, why it is listed, the package facts and the size.</summary>
    private DetailsDialog DetailsOf(BloatRow row)
    {
        List<DetailField> fields = [new(Text["FieldDescription"], row.Reason, false, DetailGroup.Description)];
        if (row.BlockText is { Length: > 0 } block) fields.Add(new(Text["DetailsNote"], block, false, DetailGroup.Description));
        fields.Add(new(Text["FieldScope"], row.ScopeText));
        fields.Add(new(Text["FieldSize"], row.SizeText));
        fields.AddRange(row.Details);
        List<BadgeModel> chips = [new(row.ClassText, row.ClassTone)];
        if (row.ShowRisk) chips.Add(BadgeModel.ForRisk(row.RiskText, row.Risk));
        return DetailsDialogBuilder.Build(Text, copy, row.Title, fields, chips, row.Subtitle, true);
    }

    public ObservableCollection<BloatRow> Rows { get; } = [];
    public ObservableCollection<BloatRow> Visible { get; } = [];
    /// <summary>The visible packages in two groups by risk: the Safe ones first, then everything else.</summary>
    public ObservableCollection<BloatGroup> Groups { get; } = [];
    public ObservableCollection<string> ReportLines { get; } = [];
    public ObservableCollection<BloatHistoryRow> History { get; } = [];
    public PageHistoryModel HistoryList { get; }
    /// <summary>The apps that were removed and are not installed now (the Removed tab).</summary>
    public ObservableCollection<RemovedAppRow> RemovedApps { get; } = [];
    public bool HasRemoved => RemovedApps.Count > 0;
    public bool ShowNoRemoved => RemovedApps.Count == 0;
    public int SelectedTab { get => selectedTab; set => SetProperty(ref selectedTab, value); }
    public string AppsTabTitle => Text["V2BlTabApps"];
    public string RemovedTabTitle => Text.Format("V2BlTabRemoved", RemovedApps.Count);
    public IReadOnlyList<BloatFilterItem> ClassFilters { get; private set; }
    public IReadOnlyList<BloatFilterItem> ScopeFilters { get; private set; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SelectSafeCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand<BloatHistoryRow> ReinstallHistory { get; }
    public RelayCommand<RemovedAppRow> RestoreCommand { get; }
    public RelayCommand<RemovedAppRow> OpenStoreCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }

    public BloatPhase Phase
    {
        get => phase;
        private set
        {
            if (!SetProperty(ref phase, value)) return;
            foreach (var n in new[] { nameof(IsBusy), nameof(ShowProgress), nameof(ShowLoading), nameof(ShowProgressPanel), nameof(ShowEmpty), nameof(CanRemove), nameof(CanChangeSelection) }) OnPropertyChanged(n);
            NotifyCommands();
        }
    }

    public override bool IsBusy => Phase != BloatPhase.Idle;
    public bool ShowProgress => IsBusy;
    /// <summary>The first read of the apps is running and there is no list yet: the page shows the loading skeleton.</summary>
    public bool ShowLoading => Phase == BloatPhase.Loading && listing is null;
    /// <summary>A removal, a restore or a later read: the progress panel with Cancel. The first read shows the skeleton instead.</summary>
    public bool ShowProgressPanel => IsBusy && !ShowLoading;
    public bool CanChangeSelection => Phase == BloatPhase.Idle;
    public bool IsElevated => elevation.IsElevated;
    public bool ShowNeedsAdministrator => !elevation.IsElevated;
    public string NeedsAdministratorTitle => elevation.UnsafeLocation ? Text["AppLocationUnsafeTitle"] : Text["AdminNoticeTitle"];
    public string NeedsAdministratorText => elevation.UnsafeLocation ? Text["AppLocationUnsafe"] : Text["AdminNoticeMessage"];
    public bool ShowAdminHowTo => !elevation.UnsafeLocation;
    public bool ShowPartial => listing is { AllUsers: false } && elevation.IsElevated;
    public bool ShowProvisionedUnknown => listing is { ProvisionedKnown: false };
    public string? LoadError { get => loadError; private set { if (SetProperty(ref loadError, value)) OnPropertyChanged(nameof(HasLoadError)); } }
    public bool HasLoadError => loadError is not null;
    public string? Notice { get => notice; private set { if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool NoticeIsError { get => noticeIsError; private set { if (SetProperty(ref noticeIsError, value)) OnPropertyChanged(nameof(NoticeSeverity)); } }
    public BannerSeverity NoticeSeverity => noticeIsError ? BannerSeverity.Warning : BannerSeverity.Success;
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public string? ProgressText { get => progressText; private set => SetProperty(ref progressText, value); }
    public bool ShowEmpty => listing is not null && Visible.Count == 0 && Phase == BloatPhase.Idle;
    public bool HasReport => ReportLines.Count > 0;
    private ResultReportModel? report;
    /// <summary>The shared report of the last removal: how many apps went, the ones that failed with the reason, Copy report and Close.</summary>
    public ResultReportModel? Report { get => report; private set => SetProperty(ref report, value); }
    public string ReportHeadline => lastRun is null ? "" : Text.Format(lastRun.State == RemovalRunState.Completed ? "BloatReportDone" : lastRun.State == RemovalRunState.Cancelled ? "BloatReportCancelled" : "BloatReportStopped",
        lastRun.Items.Count(i => i.AppRemoved), lastRun.Items.Count, SizeFormat.Bytes(Text, lastRun.FreedBytes));
    public bool HasHistory => History.Count > 0;

    public string SearchText { get => search; set { if (SetProperty(ref search, value ?? "")) ApplyFilter(); } }
    public BloatFilterItem SelectedClassFilter { get => classFilter; set { if (value is not null && SetProperty(ref classFilter, value)) ApplyFilter(); } }
    public BloatFilterItem SelectedScopeFilter { get => scopeFilter; set { if (value is not null && SetProperty(ref scopeFilter, value)) ApplyFilter(); } }
    private bool filtersOpen;
    public bool FiltersOpen { get => filtersOpen; set => SetProperty(ref filtersOpen, value); }
    /// <summary>The Filters button: "Filters" or "Filters (n)" for the class and scope filters that are set.</summary>
    public string FiltersText { get { var n = (classFilter.Id != "all" ? 1 : 0) + (scopeFilter.Id != "all" ? 1 : 0); return n == 0 ? Text["DiscFilters"] : Text.Format("DiscFiltersActive", n); } }
    public bool HasActiveFilter => search.Length > 0 || classFilter.Id != "all" || scopeFilter.Id != "all";

    public IEnumerable<BloatRow> SelectedRows => Rows.Where(r => r.IsSelected);
    public override int PendingCount => SelectedRows.Count();
    public override StagingView? Staging => PendingCount == 0 ? null : new(Key,
        SelectedRows.Select(r => new StagedChange(r.Item.Package.FullName, r.Item.Package.DisplayName, "", "", null, () => { if (Phase == BloatPhase.Idle) r.IsSelected = false; })).ToList(),
        SelectionText, Text["BloatRemove"], Text["BloatRemoveHelp"], Text["BloatClear"], Text["BloatClearHelp"],
        () => RemoveCommand.CanExecute(null), () => RemoveCommand.Execute(null), () => ClearCommand.CanExecute(null), () => ClearCommand.Execute(null));
    public IReadOnlyList<string> SelectedNames => SelectedRows.Select(r => r.Item.Package.DisplayName).ToList();
    public long SelectedBytes => SelectedRows.Sum(r => r.SizeBytes ?? 0);
    public bool SizesPending => SelectedRows.Any(r => r.SizeBytes is null);
    public string SelectionText => !SelectedRows.Any() ? Text["BloatNothingSelected"]
        : Text.Format(SizesPending ? "BloatSelectedEstimate" : "BloatSelected", SelectedRows.Count(), SizeFormat.Bytes(Text, SelectedBytes));
    public string CountText => listing is null ? "" : Text.Format("BloatCount", Visible.Count, Rows.Count);
    public bool CanRemove => IsElevated && Phase == BloatPhase.Idle && SelectedRows.Any();

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged(); SelectSafeCommand.NotifyCanExecuteChanged(); ClearCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        ReinstallHistory.NotifyCanExecuteChanged(); RestoreCommand.NotifyCanExecuteChanged();
    }

    private void RaiseSelection()
    {
        foreach (var n in new[] { nameof(SelectionText), nameof(SelectedBytes), nameof(SizesPending), nameof(CanRemove), nameof(PendingCount), nameof(SelectedNames) }) OnPropertyChanged(n);
        NotifyCommands();
        foreach (var group in Groups) group.RefreshSelection();
    }

    public override void Activate() { if ((listing is null || Journal.IsStale) && Phase == BloatPhase.Idle) work = LoadAsync(); }
    public override void Deactivate() { if (Phase == BloatPhase.Loading) cts?.Cancel(); }
    public override Task WhenIdleAsync() => work;
    public override System.Windows.Input.ICommand? ShortcutRefresh() => RefreshCommand;

    protected override void OnLanguageChanged()
    {
        ClassFilters = BuildClassFilters(); ScopeFilters = BuildScopeFilters();
        classFilter = ClassFilters.First(f => f.Id == classFilter.Id); scopeFilter = ScopeFilters.First(f => f.Id == scopeFilter.Id);
        if (listing is not null) Rebuild();
        RefreshHistory();
        foreach (var n in new[] { nameof(AppsTabTitle), nameof(RemovedTabTitle) }) OnPropertyChanged(n);
        base.OnLanguageChanged();
    }

    private IReadOnlyList<BloatFilterItem> BuildClassFilters() =>
        [new("all", Text["BloatFilterAll"]), new("safe", Text.Enum(BloatClass.Safe)), new("caution", Text.Enum(BloatClass.Caution)), new("unknown", Text.Enum(BloatClass.Unknown)), new("protected", Text.Enum(BloatClass.Protected))];

    private IReadOnlyList<BloatFilterItem> BuildScopeFilters() =>
        [new("all", Text["BloatScopeAll"]), new("current", Text["BloatScopeCurrent"]), new("provisioned", Text["BloatScopeProvisioned"])];

    // ---- loading ----

    private async Task LoadAsync(bool keepNotice = false)
    {
        Journal.MarkRead();
        cts?.Cancel(); cts?.Dispose();
        var mine = cts = new CancellationTokenSource();
        var token = cts.Token;
        Phase = BloatPhase.Loading; if (!keepNotice) Notice = null; LoadError = null; ProgressText = Text["BloatLoading"];
        try
        {
            listing = await Task.Run(() => service.List(token), token).ConfigureAwait(true);
            Rebuild();
            Phase = BloatPhase.Idle; ProgressText = null;
            RefreshHistory();
            _ = MeasureAsync(token);
        }
        catch (OperationCanceledException) { if (ReferenceEquals(cts, mine)) { Phase = BloatPhase.Idle; ProgressText = null; } } // a newer load owns the state now
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Any other failure (a COM or package manager error too) must leave the page usable, or Refresh stays disabled.
            if (ReferenceEquals(cts, mine)) { LoadError = Text["BloatLoadFailed"]; Phase = BloatPhase.Idle; ProgressText = null; }
        }
        foreach (var n in new[] { nameof(ShowPartial), nameof(ShowProvisionedUnknown), nameof(ShowEmpty), nameof(ShowLoading), nameof(ShowProgressPanel), nameof(CountText) }) OnPropertyChanged(n);
    }

    private void Rebuild()
    {
        var keep = Rows.Where(r => r.IsSelected).Select(r => r.Package.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Rows.Clear();
        foreach (var item in listing!.Items)
        {
            var row = MakeRow(item);
            if (keep.Contains(item.Package.FullName)) row.IsSelected = true;
            Rows.Add(row);
        }
        ApplyFilter();
        RaiseSelection();
    }

    private BloatRow MakeRow(BloatItem item)
    {
        var p = item.Package;
        var appearance = item.Class switch { BloatClass.Safe => Tone.Success, BloatClass.Caution => Tone.Caution, BloatClass.Protected => Tone.Info, _ => Tone.Neutral };
        var reason = item.Rule is { } rule ? Text[$"Bloat_Reason_{rule.Group}"] : Text["BloatReasonUnknown"];
        string? block = item.BlockCode is null ? null : item.BlockCode == BloatCode.HasDependents ? Text.Format("BloatBlock_has_dependents", string.Join(", ", item.Dependents)) : BlockText(item.BlockCode);
        var scope = new List<string>();
        if (p.IsInstalled) scope.Add(p.InstalledForCurrentUser ? Text["BloatScopeCurrent"] : Text["BloatScopeOthers"]);
        if (p.IsProvisioned) scope.Add(Text["BloatScopeProvisioned"]);
        if (scope.Count == 0) scope.Add(Text["BloatScopeNone"]);
        var details = new List<DetailField>
        {
            new(Text["BloatDetailFullName"], p.FullName, true), new(Text["BloatDetailFamily"], p.FamilyName, true), new(Text["BloatDetailPublisher"], p.Publisher, true),
            new(Text["BloatDetailArchitecture"], p.Architecture, true), new(Text["BloatDetailSignature"], Text.Enum(p.Signature)),
            new(Text["BloatDetailLocation"], p.InstallLocation ?? Text["BloatNoLocation"], true),
            new(Text["BloatDetailKind"], string.Join(", ", new[] { p.IsFramework ? Text["BloatKindFramework"] : null, p.IsOptional ? Text["BloatKindOptional"] : null, p.IsDevelopmentMode ? Text["BloatKindDevelopment"] : null, p.IsBundle ? Text["BloatKindBundle"] : null }.Where(x => x is not null)) is { Length: > 0 } kinds ? kinds : Text["BloatKindApp"]),
        };
        if (item.Rule is { } r) details.Add(new(Text["BloatDetailSource"], $"{Text.Enum(r.Basis)}: {r.Reference}"));
        if (item.Dependents.Count > 0) details.Add(new(Text["BloatDetailDependents"], string.Join(", ", item.Dependents), true));
        return new(item, Text.Enum(item.Class), appearance, reason, string.Join(" · ", scope), block, details, SizeLabel, RaiseSelection)
        {
            Icon = new AppIconLoader(icons, p.InstallLocation is { Length: > 0 } location ? new AppIconKey(AppIconKind.Package, location) : null),
            RiskText = Text.Enum(item.Class == BloatClass.Safe ? RiskLevel.Low : RiskLevel.Medium), ProtectedText = Text["BloatProtectedBadge"],
            DetailsCommand = ShowDetailsCommand, MoreName = Text.Format("RowMoreName", p.DisplayName), DetailsName = Text.Format("DetailsName", p.DisplayName),
        };
    }

    private string SizeLabel(long? bytes) => bytes is { } b ? SizeFormat.Bytes(Text, b) : Text["BloatSizeUnknown"];

    private string BlockText(string code)
    {
        var key = code.Replace('.', '_').Replace("bloat_", "");
        return Text.Has("V2BlBlock_" + key) ? Text["V2BlBlock_" + key] : Text.Has("BloatBlock_" + key) ? Text["BloatBlock_" + key] : Text["BloatBlock_other"];
    }

    private async Task MeasureAsync(CancellationToken token)
    {
        try
        {
            foreach (var row in Rows.Where(r => r.CanSelect && r.Package.InstallLocation is not null).ToList())
            {
                token.ThrowIfCancellationRequested();
                var size = await Task.Run(() => service.Measure(row.Package, token), token).ConfigureAwait(true);
                row.SizeBytes = size ?? 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { } // sizes are only shown for information; nobody awaits this task, so nothing may escape
    }

    // ---- filters and selection ----

    private void ApplyFilter()
    {
        OnPropertyChanged(nameof(FiltersText)); OnPropertyChanged(nameof(HasActiveFilter));
        Visible.Clear();
        foreach (var row in Rows)
        {
            if (classFilter.Id != "all" && !string.Equals(row.Item.Class.ToString(), classFilter.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (scopeFilter.Id == "current" && !row.Package.InstalledForCurrentUser) continue;
            if (scopeFilter.Id == "provisioned" && !row.Package.IsProvisioned) continue;
            if (search.Length > 0 && !(row.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) || row.Package.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Package.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Package.FullName.Contains(search, StringComparison.OrdinalIgnoreCase))) continue;
            Visible.Add(row);
        }
        Groups.Clear();
        // An app that Winora or Windows will not remove (protected, needed by others, forbidden by Windows) is in Keep with its reason; every other app can be removed.
        var safe = Visible.Where(r => r.CanSelect && r.Item.Class == BloatClass.Safe).ToList();
        var optional = Visible.Where(r => r.CanSelect && r.Item.Class is BloatClass.Caution or BloatClass.Unknown).ToList();
        var keep = Visible.Where(r => !r.CanSelect).ToList();
        if (safe.Count > 0) Groups.Add(new(Text["V2BlSafe"], safe, BloatGroupKind.Safe, Text["V2BlSafeD"], () => Text[AllTicked(safe) ? "V2BlSelectNone" : "V2BlSelectAll"], new RelayCommand(() => ToggleGroup(safe))));
        if (optional.Count > 0) Groups.Add(new(Text["V2BlOpt"], optional, BloatGroupKind.Optional, Text["V2BlOptD"], () => Text[AllTicked(optional) ? "V2BlSelectNone" : "V2BlSelectAll"], new RelayCommand(() => ToggleGroup(optional))));
        if (keep.Count > 0) Groups.Add(new(Text["V2BlKeep"], keep, BloatGroupKind.Keep, Text["V2BlKeepD"]));
        foreach (var group in Groups) for (var i = 0; i < group.Rows.Count; i++) group.Rows[i].ShowDivider = i > 0;
        foreach (var n in new[] { nameof(ShowEmpty), nameof(CountText) }) OnPropertyChanged(n);
    }

    private static bool AllTicked(IEnumerable<BloatRow> rows) { var pickable = rows.Where(r => r.CanSelect).ToList(); return pickable.Count > 0 && pickable.All(r => r.IsSelected); }

    // Ticks every removable app of the group, or clears them when all are ticked already.
    private void ToggleGroup(IReadOnlyList<BloatRow> rows)
    {
        if (Phase != BloatPhase.Idle) return;
        var value = !AllTicked(rows);
        foreach (var row in rows.Where(r => r.CanSelect)) row.IsSelected = value;
        RaiseSelection();
    }

    private void SelectSafe() { foreach (var row in Visible.Where(r => r.CanSelect && r.Item.Class == BloatClass.Safe)) row.IsSelected = true; RaiseSelection(); }
    private void ClearSelection() { foreach (var row in Rows) row.IsSelected = false; RaiseSelection(); }

    // ---- removal ----

    private async Task RemoveAsync()
    {
        var chosen = SelectedRows.ToList();
        if (chosen.Count == 0) return;
        cts?.Cancel(); cts?.Dispose(); // the size measuring of the list stops before files are removed or restored
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Notice = null; ReportLines.Clear(); lastRun = null; Report = null; OnPropertyChanged(nameof(HasReport));
        RemovalPreview preview;
        Phase = BloatPhase.Loading; ProgressText = Text["BloatPreparing"];
        try { preview = await Task.Run(() => service.Preview(chosen.Select(r => new RemovalTarget(r.Package.FullName, r.SizeBytes)).ToList(), token), token).ConfigureAwait(true); }
        catch (Exception)
        {
            Phase = BloatPhase.Idle; ProgressText = null; Notice = Text["BloatLoadFailed"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); return;
        }
        Phase = BloatPhase.Idle; ProgressText = null;

        var refused = preview.Items.Where(i => i.RefusalCode is not null).ToList();
        var allowed = preview.Items.Where(i => i.RefusalCode is null).ToList();
        if (allowed.Count == 0)
        {
            Notice = string.Join(" ", refused.Select(i => Failure(i.Item.Package.DisplayName, i.RefusalCode!, 0, i.Item))); NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError));
            return;
        }
        var lines = allowed.Select(Line).ToList();
        var warnings = new List<string> { Text["V2BlConfirmWarning"] };
        if (allowed.Any(i => i.Item.Class == BloatClass.Caution)) warnings.Add(Text["BloatConfirmCaution"]);
        if (allowed.Any(i => i.Item.Class == BloatClass.Unknown)) warnings.Add(Text["BloatConfirmUnknown"]);
        if (allowed.Any(i => i.Users.Count > 1)) warnings.Add(Text["BloatConfirmUsers"]);
        var added = allowed.Where(i => i.Added).Select(i => i.Item.Package.DisplayName).ToList();
        if (added.Count > 0) warnings.Add(Text.Format("V2BlConfirmAlso", string.Join(", ", added)));
        var size = preview.EstimatedBytes;
        var body = Text.Plural("BloatConfirmBody", allowed.Count, allowed.Count, Text.Size(size)) + (refused.Count > 0 ? " " + Text.Format("BloatConfirmSkipped", refused.Count) : "");
        var confirmation = new ActionConfirmation(Text["BloatConfirmTitle"], body, lines, string.Join(" ", warnings), Text["BloatConfirmPrimary"])
        {
            Changes = allowed.Select(i => new ConfirmChange(i.Item.Package.DisplayName, Text["BloatStateInstalled"], Text["Enum_BloatState_Applied"],
                null, (i.SizeBytes is { } b ? SizeFormat.Bytes(Text, b) : null))).ToList(),
            Risk = allowed.Any(i => i.Item.Class is BloatClass.Caution or BloatClass.Unknown) ? RiskLevel.Medium : RiskLevel.Low, Undo = ConfirmUndo.Reinstall, Restart = false, Irreversible = true,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;

        using var activityRun = Runs.Begin(Text.Format("ACRunBloat", allowed.Count), true, () => { try { cts?.Cancel(); } catch (ObjectDisposedException) { } });
        if (!activityRun.Started) return;
        cts?.Cancel(); cts?.Dispose();
        cts = new CancellationTokenSource();
        token = cts.Token;
        Phase = BloatPhase.Removing; ProgressText = Text["BloatRemoving"];
        var progress = new Progress<RemovalProgress>(p => { if (Phase == BloatPhase.Removing) ProgressText = Text.Format("BloatRemovingProgress", p.Name, p.Index, p.Total); activityRun.Report(p.Total == 0 ? null : (double)Math.Max(0, p.Index - 1) / p.Total); });
        try
        {
            lastRun = await service.RemoveAsync(allowed.Select(i => new RemovalTarget(i.Item.Package.FullName, i.SizeBytes)).ToList(), progress, token).ConfigureAwait(true);
            BuildReport(refused);
            var done = lastRun.Items.Count(i => i.State == BloatState.Applied);
            activityRun.Finish(token.IsCancellationRequested ? RunResult.Stopped : done == lastRun.Items.Count ? RunResult.Ok : RunResult.Warning, ReportHeadline);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError));
            activityRun.Finish(RunResult.Error, Text.Format("ACRunFailedText", Text.Format("ACRunBloat", allowed.Count)));
        }
        finally { Phase = BloatPhase.Idle; ProgressText = null; }
        await LoadAsync(keepNotice: true).ConfigureAwait(true);
    }

    private string Line(RemovalPreviewItem i)
    {
        var steps = new List<string>();
        if (i.WillRemove) steps.Add(Text["BloatStepRemove"]);
        if (i.WillDeprovision) steps.Add(Text["BloatStepDeprovision"]);
        var users = i.Users.Count > 0 ? " · " + Text.Format("BloatUsers", i.Users.Count(u => u.State == PackageInstallState.Installed || u.State == PackageInstallState.Staged)) : "";
        var size = i.SizeBytes is { } b ? " · " + SizeFormat.Bytes(Text, b) : "";
        var line = Text.Format("BloatConfirmLine", i.Item.Package.DisplayName, Text.Enum(i.Item.Class), string.Join(" + ", steps)) + size + users;
        return i.Added ? line + " · " + Text["V2BlNeededByChosen"] : line;
    }

    private void BuildReport(IReadOnlyList<RemovalPreviewItem> refused)
    {
        ReportLines.Clear();
        if (lastRun is null) return;
        var failures = new List<ResultFailure>();
        foreach (var item in lastRun.Items)
        {
            var title = Rows.FirstOrDefault(r => r.Package.FullName == item.FullName)?.Title ?? item.Name;
            if (item.State == BloatState.Applied) { ReportLines.Add(Text.Format("V2BlReportRemoved", title)); continue; }
            if (item.State == BloatState.Cancelled) { ReportLines.Add(Text.Format("V2BlReportNotStarted", title)); continue; }
            var line = Failure(title, item.Code, item.Steps.LastOrDefault(s => !s.Ok)?.Hresult ?? 0, null, item.State == BloatState.Partial);
            ReportLines.Add(line); failures.Add(new(title, line, null));
        }
        foreach (var r in refused)
        {
            var line = Failure(r.Item.Package.DisplayName, r.RefusalCode!, 0, r.Item);
            ReportLines.Add(line); failures.Add(new(r.Item.Package.DisplayName, line, null));
        }
        if (lastRun.Items.Any(i => i.RecordLost)) ReportLines.Add(Text["RecordLostNote"]);
        Report = Feedback.CreateReport(ReportHeadline, lastRun.Items.Count(i => i.State == BloatState.Applied), 0, failures, null, CloseReport);
        OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(ReportHeadline));
    }

    private void CloseReport()
    {
        ReportLines.Clear(); lastRun = null; Report = null;
        OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(ReportHeadline));
    }

    /// <summary>One sentence that names the app, says in plain words why it failed and what to do. The Windows error number is added only for an error that has no plainer reason.</summary>
    public string Failure(string app, string code, int hresult, BloatItem? item = null, bool partial = false) => BloatFailureText.Format(Text, app, code, hresult, item?.Dependents, partial);

    public string CodeText(string code)
    {
        var key = code.Replace('.', '_');
        return Text.Has("V2BlCode_" + key) ? Text["V2BlCode_" + key] : Text.Has("BloatCode_" + key) ? Text["BloatCode_" + key] : Text["BloatCode_bloat_failed"];
    }

    // ---- history, removed apps and restore ----

    private void RefreshHistory()
    {
        History.Clear();
        var shown = new List<HistoryRowViewModel>();
        RemovedApps.Clear();
        IReadOnlyList<BloatChangeRecord> records;
        IReadOnlySet<string> installed = new HashSet<string>();
        try { records = service.History(HistoryLimit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { records = []; }
        if (records.Count > 0)
        {
            try { installed = service.InstalledFamilies(); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { installed = new HashSet<string>(); }
        }
        List<BloatChangeRecord> all = records.ToList();
        foreach (var r in records)
        {
            var option = SafeOption(r, all, installed);
            var label = option.Kind == ReinstallKind.Register ? Text["V2BlRestore"] : option.Kind == ReinstallKind.Store ? Text["V2BlRestoreStore"] : "";
            var steps = string.Join(Text["ListSeparatorStrong"], r.Steps.Select(s => $"{BloatFailureText.StepName(Text, s.Step)}: {(s.Ok ? Text["BloatStepOk"] : CodeText(s.Code))}"));
            var what = Text.Format(r.Change.Kind == BloatChangeKind.Remove ? "BloatHistoryRemove" : "BloatHistoryReinstall", r.Change.Shown, r.Change.Version);
            History.Add(new(r, what, Text.Enum(BloatHistory.Result(r, records)), Text.When(r.BeganUtc.ToLocalTime(), "g"), steps, option, label, ReinstallHistory));
            var h = History[^1];
            shown.Add(HistoryList.Row(r.Change.Id, r.BeganUtc, BloatHistory.Result(r, records), h.Description, h.CanReinstall, h.ReinstallCommand, h, new(h.ReinstallLabel, Text["BloatReinstallHelp"], h.StepsText)));
        }
        HistoryList.Set(shown);
        RebuildRemoved(all, installed);
        OnPropertyChanged(nameof(HasHistory));
    }

    private void RebuildRemoved(List<BloatChangeRecord> all, IReadOnlySet<string> installed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in all)
        {
            var c = r.Change;
            if (c.Kind != BloatChangeKind.Remove || r.State is not (BloatState.Applied or BloatState.Partial) || !c.WasInstalled) continue;
            if (!seen.Add(c.FamilyName) || installed.Contains(c.FamilyName)) continue;
            var option = SafeOption(r, all, installed);
            var how = option.Kind switch
            {
                ReinstallKind.Register => Text["V2BlHowRegister"],
                ReinstallKind.Store => Text["V2BlHowStore"],
                _ => Text.Format("V2BlHowNone", c.Shown),
            };
            RemovedApps.Add(new(new RemovedApp(r, option), c.Shown, $"{c.Name} · {c.Version}", Text.Format("V2BlRemovedOn", Text.When(r.BeganUtc.ToLocalTime(), "d")), how,
                option.Kind == ReinstallKind.Register ? Text["V2BlRestore"] : Text["V2BlRestoreStore"], Text["V2BlOpenStore"], RestoreCommand, OpenStoreCommand)
            {
                Icon = new AppIconLoader(icons, c.InstallLocation is { Length: > 0 } location ? new AppIconKey(AppIconKind.Package, location) : null),
            });
        }
        for (var i = 0; i < RemovedApps.Count; i++) RemovedApps[i].ShowDivider = i > 0;
        foreach (var n in new[] { nameof(HasRemoved), nameof(ShowNoRemoved), nameof(RemovedTabTitle) }) OnPropertyChanged(n);
    }

    private ReinstallOption SafeOption(BloatChangeRecord record, IReadOnlyList<BloatChangeRecord> history, IReadOnlySet<string> installed)
    {
        try { return service.GetReinstallOption(record, history, installed); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return new(ReinstallKind.None, null); }
    }

    private void OpenStorePage(BloatChange change)
    {
        if (!launcher.Open(change.FamilyName)) { Notice = BloatFailureText.Format(Text, change.Shown, BloatCode.StoreFailed); NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); }
    }

    private async Task RestoreAsync(Guid changeId, ReinstallOption option, string name, string line)
    {
        if (option.Kind == ReinstallKind.None || Phase != BloatPhase.Idle) return;
        var store = option.Kind == ReinstallKind.Store;
        var confirmation = new ActionConfirmation(Text.Format("V2BlRestoreTitle", name), Text.Format(store ? "V2BlRestoreStoreBody" : "V2BlRestoreRegisterBody", name), [line], Text[store ? "V2BlRestoreStoreWarning" : "BloatReinstallWarning"], Text[store ? "V2BlRestoreStoreGo" : "BloatReinstallPrimary"])
        {
            Changes = [new ConfirmChange(name, Text["Enum_BloatState_Applied"], Text["BloatStateInstalled"])], Risk = RiskLevel.Low, Undo = ConfirmUndo.Unspecified, Restart = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        using var activityRun = Runs.Begin(Text.Format("ACRunRestoreApp", name), true, () => { try { cts?.Cancel(); } catch (ObjectDisposedException) { } });
        if (!activityRun.Started) return;
        cts?.Cancel(); cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Phase = BloatPhase.Removing; ProgressText = Text.Format("V2BlRestoring", name); Notice = null;
        var progress = new Progress<ReinstallProgress>(p =>
        {
            if (Phase != BloatPhase.Removing) return;
            ProgressText = p.Step switch
            {
                BloatStep.OpenStore => Text.Format("V2BlOpeningStore", name),
                BloatStep.WaitStore => Text.Format("V2BlWaitingStore", name, $"{(int)p.Waited.TotalMinutes}:{p.Waited.Seconds:00}"),
                _ => Text.Format("V2BlRestoring", name),
            };
        });
        try
        {
            var result = await service.ReinstallAsync(changeId, progress, token).ConfigureAwait(true);
            Notice = result.State == BloatState.Applied ? Text.Format("V2BlRestored", name) : result.State == BloatState.Cancelled ? Text.Format("V2BlRestoreStopped", name) : Failure(name, result.Code, result.Steps.LastOrDefault(s => !s.Ok)?.Hresult ?? 0);
            if (result.RecordLost) Notice += " " + Text["RecordLostNote"];
            NoticeIsError = result.State != BloatState.Applied; OnPropertyChanged(nameof(NoticeIsError));
            activityRun.Finish(result.State == BloatState.Applied ? RunResult.Ok : result.State == BloatState.Cancelled ? RunResult.Stopped : RunResult.Error, Notice);
        }
        catch (OperationCanceledException) { Notice = Text.Format("V2BlRestoreStopped", name); NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); activityRun.Finish(RunResult.Stopped); }
        catch (Exception) { Notice = Failure(name, BloatCode.Failed, 0); NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); activityRun.Finish(RunResult.Error, Notice); }
        finally { Phase = BloatPhase.Idle; ProgressText = null; }
        await LoadAsync(keepNotice: true).ConfigureAwait(true);
    }
}

/// <summary>The status word of a Preinstalled apps record: a removal is Removed (Undone once the app was brought back), bringing an app back is Restored.</summary>
public static class BloatHistory
{
    public static HistoryResult Result(BloatChangeRecord r, IReadOnlyList<BloatChangeRecord> all) => r.State == BloatState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : r.State != BloatState.Applied ? HistoryResult.Failed
        : r.Change.Kind != BloatChangeKind.Remove ? HistoryResult.Restored
        : all.Any(o => o.Change.RevertOf == r.Change.Id && o.State == BloatState.Applied) ? HistoryResult.Undone : HistoryResult.Removed;
}
