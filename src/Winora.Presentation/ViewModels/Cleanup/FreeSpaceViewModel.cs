// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;

namespace Winora.Desktop;

public enum FreeSpacePhase { Idle, Scanning, Review, Cleaning, Done }

/// <summary>One choice of how recent a file may be and still be kept. 0 days keeps nothing for its age.</summary>
public sealed record CleanupAgeChoice(int Days, string Label);

/// <summary>One cleanable group in the review list. The user can untick it; a group with nothing to clean cannot be ticked.</summary>
public sealed class CleanupCategoryRow : ObservableObject
{
    private bool selected;

    public CleanupCategoryRow(CleanupCategory category, string title, string description, CleanupScan scan, string size, string count, string note, Action changed, string riskText = "")
    {
        Category = category; Title = title; Description = description; Scan = scan; SizeText = size; CountText = count; NoteText = note;
        RiskText = riskText;
        this.changed = changed;
    }

    /// <summary>The Recycle Bin holds things the person deleted on purpose, so emptying it is Medium risk. Every other group is a cache, temp or dump folder that Windows or the program makes again: Low.</summary>
    public static RiskLevel RiskOf(CleanupCategory category) => category.Kind == CleanupCategoryKind.RecycleBin ? RiskLevel.Medium : RiskLevel.Low;
    public RiskLevel Risk => RiskOf(Category);
    public string RiskText { get; }

    private readonly Action changed;
    public CleanupCategory Category { get; }
    public CleanupScan Scan { get; }
    public string Title { get; }
    public string Description { get; }
    public string SizeText { get; }
    public string CountText { get; }
    public string NoteText { get; }
    public bool HasNote => NoteText.Length > 0;
    /// <summary>True for the first row of the list card (no divider above it).</summary>
    public bool IsFirst { get; set; } = true;
    public bool CanSelect => Scan.Count > 0;
    public bool IsSelected
    {
        get => selected && CanSelect;
        set { if (SetProperty(ref selected, value)) changed(); }
    }
    public string AutomationName => $"{Title}, {CountText}, {SizeText}, {RiskText}";

    public IconKey IconSymbol => Category.Id switch
    {
        CleanupCategoryId.UserTemp or CleanupCategoryId.WindowsTemp => IconKey.Document24,
        CleanupCategoryId.ThumbnailCache => IconKey.Image24,
        CleanupCategoryId.InternetCache or CleanupCategoryId.BrowserCaches => IconKey.Globe24,
        CleanupCategoryId.ShaderCache => IconKey.Games24,
        CleanupCategoryId.ErrorReports or CleanupCategoryId.SystemErrorReports or CleanupCategoryId.CrashDumps or CleanupCategoryId.SystemDumps => IconKey.Bug24,
        CleanupCategoryId.RecycleBin => IconKey.Delete24,
        CleanupCategoryId.UpdateCache => IconKey.ArrowSync24,
        CleanupCategoryId.DeliveryOptimization => IconKey.ArrowDownload24,
        _ => IconKey.Folder24,
    };

    /// <summary>The first folder the group looks in, as the person knows it (no environment variables). Empty for the Recycle Bin.</summary>
    public string LocationText => Category.Kind == CleanupCategoryKind.RecycleBin || Category.Locations.Count == 0 ? "" : Environment.ExpandEnvironmentVariables(Category.Locations[0].Root);
    public bool HasLocation => LocationText.Length > 0;
    /// <summary>Large groups stand out: 1 GB and more is High, 50 MB and more is Medium.</summary>
    public string SizeBand => Scan.Bytes >= 1L << 30 ? "Large" : Scan.Bytes >= 50L << 20 ? "Medium" : "Small";
    public string CleanOnlyName { get; init; } = "";
    public string MenuName { get; init; } = "";
    public string OpenFolderLabel { get; init; } = "";
    public string RescanLabel { get; init; } = "";
    public System.Windows.Input.ICommand? RescanCommand { get; init; }
    public RelayCommand? OpenFolderCommand { get; init; }
    public RelayCommand? CleanOnlyCommand { get; init; }
}

/// <summary>
/// The free-up-space flow: scan the cache locations, show what can go with its size, ask once, remove what the user ticked, and report the result. The scan changes nothing; the
/// removal is permanent and says so before the user confirms. Leaving the page stops a running scan or removal at the next file.
/// </summary>
public sealed class FreeSpaceViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService text;
    private readonly ICleanupCatalog catalog;
    private readonly ICleanupScanner scanner;
    private readonly CleanupRunner runner;
    private readonly ICleanupJournal journal;
    private readonly IReviewPrompt prompt;
    private readonly IElevationState elevation;
    private readonly IFileLocationOpener? opener;
    private readonly Func<IReadOnlyList<DriveEntry>?>? drives;
    private CancellationTokenSource? cts;
    private Task work = Task.CompletedTask;
    private FreeSpacePhase phase;
    private string? errorText;
    private string progressText = "";
    private CleanupRunResult? lastResult;
    private int ageDays = CleanupCatalog.DefaultMinAgeDays;

    public FreeSpaceViewModel(LocalizationService text, ICleanupCatalog catalog, ICleanupScanner scanner, CleanupRunner runner, ICleanupJournal journal, IReviewPrompt prompt, IElevationState elevation, IFileLocationOpener? opener = null, Func<IReadOnlyList<DriveEntry>?>? drives = null)
    {
        this.elevation = elevation; this.opener = opener; this.drives = drives;
        this.text = text; this.catalog = catalog; this.scanner = scanner; this.runner = runner; this.journal = journal; this.prompt = prompt;
        ScanCommand = new(ScanAsync, () => Phase is FreeSpacePhase.Idle or FreeSpacePhase.Review or FreeSpacePhase.Done);
        CleanCommand = new(CleanAsync, () => Phase == FreeSpacePhase.Review && SelectedRows.Any());
        CancelCommand = new(Cancel, () => Phase is FreeSpacePhase.Scanning or FreeSpacePhase.Cleaning);
        SelectLowRiskCommand = new(SelectLowRisk, () => Phase == FreeSpacePhase.Review && Rows.Any(r => r.Risk == RiskLevel.Low && r.CanSelect));
        ClearSelectionCommand = new(ClearSelection, () => Phase == FreeSpacePhase.Review && SelectedRows.Any());
        text.PropertyChanged += (_, _) => { if (phase is FreeSpacePhase.Review or FreeSpacePhase.Done) RebuildTexts(); RefreshHistory(); OnPropertyChanged(string.Empty); };
        RefreshHistory();
    }

    public AsyncRelayCommand ScanCommand { get; }
    /// <summary>Where the scan and the removal register their runs; the Cleanup page sets it.</summary>
    public RunSource Runs { get; set; } = new("Cleanup");
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectLowRiskCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public LocalizationService Text => text;

    /// <summary>How recent a file may be and still be kept, for the groups that have an age rule. It lasts for this session; every start goes back to 7 days.</summary>
    public IReadOnlyList<CleanupAgeChoice> AgeChoices =>
        [new(CleanupCatalog.DefaultMinAgeDays, text["FreeSpaceAge7"]), new(1, text["FreeSpaceAge1"]), new(0, text["FreeSpaceAgeAny"])];
    public CleanupAgeChoice SelectedAge
    {
        get => AgeChoices.First(c => c.Days == ageDays);
        set
        {
            if (value is null || value.Days == ageDays) return;
            ageDays = value.Days;
            OnPropertyChanged(); OnPropertyChanged(nameof(ShowAnyAgeWarning));
            // The list shows what the new rule would remove, so it is read again.
            if (Phase is FreeSpacePhase.Review or FreeSpacePhase.Done && ScanCommand.CanExecute(null)) ScanCommand.Execute(null);
        }
    }
    public bool ShowAnyAgeWarning => ageDays == 0;
    public bool CanChangeAge => !IsBusy;

    private CleanupCategory WithAge(CleanupCategory category) => category.MinAgeDays > 0 ? category with { MinAgeDays = ageDays } : category;
    private bool HasAgeRule(CleanupCategoryId id) => catalog.Categories.Any(c => c.Id == id && c.MinAgeDays > 0);
    public ObservableCollection<CleanupCategoryRow> Rows { get; } = [];
    public ObservableCollection<DriveRow> Disks { get; } = [];
    public bool HasDisks => Disks.Count > 0;

    /// <summary>Reads the disks again (free space changes after a cleaning) and builds one card for each.</summary>
    public void RefreshDisks()
    {
        Disks.Clear();
        IReadOnlyList<DriveEntry>? entries = null;
        try { entries = drives?.Invoke(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        foreach (var d in (entries ?? []).OrderByDescending(x => x.IsSystem).ThenBy(x => x.Letter, StringComparer.OrdinalIgnoreCase)) Disks.Add(DriveRowFactory.Build(d, text));
        OnPropertyChanged(nameof(HasDisks));
    }
    public ObservableCollection<string> ReportLines { get; } = [];

    public FreeSpacePhase Phase
    {
        get => phase;
        private set
        {
            if (!SetProperty(ref phase, value)) return;
            ScanCommand.NotifyCanExecuteChanged(); CleanCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); SelectLowRiskCommand.NotifyCanExecuteChanged(); ClearSelectionCommand.NotifyCanExecuteChanged();
            foreach (var n in new[] { nameof(IsBusy), nameof(ShowIntro), nameof(ShowNotScanned), nameof(ShowReview), nameof(ShowReport), nameof(ShowProgress), nameof(ShowScanning), nameof(ShowCleaning), nameof(CanChangeSelection), nameof(CanChangeAge) }) OnPropertyChanged(n);
        }
    }

    public bool IsBusy => Phase is FreeSpacePhase.Scanning or FreeSpacePhase.Cleaning;
    public bool ShowIntro => Phase == FreeSpacePhase.Idle;
    public bool ShowReview => Phase is FreeSpacePhase.Review or FreeSpacePhase.Cleaning;
    public bool ShowReport => Phase == FreeSpacePhase.Done;
    public bool ShowProgress => IsBusy;
    /// <summary>Before the first scan and with no error: the page shows the empty state that says what a scan does.</summary>
    public bool ShowNotScanned => ShowIntro && !HasError;
    /// <summary>A scan is running: the page shows the loading skeleton with Cancel.</summary>
    public bool ShowScanning => Phase == FreeSpacePhase.Scanning;
    /// <summary>A clean is running: the page shows the progress panel with Cancel above the list.</summary>
    public bool ShowCleaning => Phase == FreeSpacePhase.Cleaning;
    public bool CanChangeSelection => Phase == FreeSpacePhase.Review;
    public string ProgressText { get => progressText; private set => SetProperty(ref progressText, value); }
    public string? ErrorText { get => errorText; private set { if (SetProperty(ref errorText, value)) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ShowNotScanned)); } } }
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public IEnumerable<CleanupCategoryRow> SelectedRows => Rows.Where(r => r.IsSelected);
    public long SelectedBytes => SelectedRows.Sum(r => r.Scan.Bytes);
    public long SelectedCount => SelectedRows.Sum(r => r.Scan.Count);
    public int SelectedCategories => SelectedRows.Count();
    /// <summary>"Selected 2.4 GB in 3 categories": the summary of the pending bar.</summary>
    public string SelectionBarText => text.Format("FreeSpaceBarSummary", SizeFormat.Bytes(text, SelectedBytes), text.Number(SelectedCategories));
    public IReadOnlyList<string> SelectedTitles => SelectedRows.Select(r => r.Title).ToList();
    public string SelectionText => SelectedCount == 0 ? text["FreeSpaceNothingSelected"] : text.Format("FreeSpaceSelected", SizeFormat.Bytes(text, SelectedBytes), Files(SelectedCount));
    private string Items(long count) => count == 1 ? text["FreeSpaceOneItem"] : text.Format("FreeSpaceItems", text.Number(count));
    private string Files(long count) => count == 1 ? text["FreeSpaceOneFile"] : text.Format("FreeSpaceFiles", text.Number(count));

    /// <summary>Shown when Winora does not run as administrator: the system folders are then not part of the scan.</summary>
    public bool ShowNotElevatedHint => !elevation.IsElevated;
    public string NotElevatedText => elevation.UnsafeLocation ? text["AppLocationUnsafe"] : text["FreeSpaceNotElevated"];
    public bool ShowElevatedNote => elevation.IsElevated;
    public bool NothingToClean => Rows.Count > 0 && Rows.All(r => r.Scan.Count == 0);

    public string HistoryText { get; private set; } = "";
    public bool HasHistory => HistoryText.Length > 0;
    public string ReportHeadline => lastResult is null ? "" : text.Format(lastResult.State == CleanupRunState.Cancelled ? "FreeSpaceCancelled" : "FreeSpaceDone",
        SizeFormat.Bytes(text, lastResult.RemovedBytes), Files(lastResult.RemovedFiles));

    // ---- scan ----

    private Task ScanAsync()
    {
        if (!ScanCommand.CanExecute(null)) return Task.CompletedTask;
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Phase = FreeSpacePhase.Scanning; ErrorText = null; ProgressText = text["FreeSpaceScanning"];
        lastResult = null; ReportLines.Clear();
        return work = RunScanAsync(token, Runs.Begin(text["ACRunFreeScan"], false, Cancel));
    }

    private async Task RunScanAsync(CancellationToken token, ActivityRun run)
    {
        using var _ = run;
        try
        {
            var categories = catalog.Categories.Select(WithAge).ToList();
            var scans = await Task.WhenAll(categories.Select(c => scanner.ScanAsync(c, null, token))).ConfigureAwait(true);
            Rows.Clear();
            for (var i = 0; i < scans.Length; i++) Rows.Add(MakeRow(categories[i], scans[i]));
            MarkFirst();
            Phase = FreeSpacePhase.Review;
            ProgressText = "";
            RaiseSelection();
            run.Finish(RunResult.Ok, quiet: true);
        }
        catch (OperationCanceledException) { Phase = Rows.Count > 0 ? FreeSpacePhase.Review : FreeSpacePhase.Idle; ProgressText = text["FreeSpaceScanCancelled"]; run.Finish(RunResult.Stopped); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorText = text["FreeSpaceScanFailed"]; Phase = Rows.Count > 0 ? FreeSpacePhase.Review : FreeSpacePhase.Idle; ProgressText = "";
            run.Finish(RunResult.Error, text["FreeSpaceScanFailed"]);
        }
    }

    private CleanupCategoryRow MakeRow(CleanupCategory category, CleanupScan scan)
    {
        var notes = new List<string>();
        if (scan.SkippedRecent > 0)
            notes.Add(text.Format(category.MinAgeDays == 1 ? "FreeSpaceRecentKeptDay" : "FreeSpaceRecentKept", Files(scan.SkippedRecent), category.MinAgeDays, SizeFormat.Bytes(text, scan.SkippedRecentBytes)));
        if (scan.SkippedLinks > 0) notes.Add(text.Format("FreeSpaceLinksSkipped", text.Number(scan.SkippedLinks)));
        if (scan.Truncated) notes.Add(text["FreeSpaceTruncated"]);
        CleanupCategoryRow? row = null;
        row = new(category, text[$"Cleanup_{category.Id}_Title"], text[$"Cleanup_{category.Id}_Description"], scan, SizeFormat.Bytes(text, scan.Bytes),
            category.Kind == CleanupCategoryKind.RecycleBin ? Items(scan.Count) : Files(scan.Count), string.Join(" ", notes), RaiseSelection, text.Enum(CleanupCategoryRow.RiskOf(category)))
        {
            OpenFolderLabel = text["V2ClOpenFolder"], RescanLabel = text["V2ClRescan"], RescanCommand = ScanCommand,
            CleanOnlyName = text.Format("V2ClCleanOnlyName", text[$"Cleanup_{category.Id}_Title"]), MenuName = text.Format("RowMoreName", text[$"Cleanup_{category.Id}_Title"]),
            OpenFolderCommand = new(() => { if (row!.HasLocation) opener?.Reveal(row.LocationText); }, () => opener is not null && row!.HasLocation && Directory.Exists(row.LocationText)),
            CleanOnlyCommand = new(() => { foreach (var other in Rows) other.IsSelected = ReferenceEquals(other, row); if (CleanCommand.CanExecute(null)) CleanCommand.Execute(null); }, () => Phase == FreeSpacePhase.Review && row!.CanSelect),
        };
        return row;
    }

    private void RebuildTexts()
    {
        var old = Rows.ToDictionary(r => r.Category.Id, r => r.IsSelected);
        var rebuilt = Rows.Select(r => MakeRow(r.Category, r.Scan)).ToList();
        Rows.Clear();
        foreach (var r in rebuilt) { r.IsSelected = old[r.Category.Id] && r.CanSelect; Rows.Add(r); }
        MarkFirst();
        RaiseSelection(); RefreshHistory();
        foreach (var n in new[] { nameof(ReportHeadline) }) OnPropertyChanged(n);
    }

    private void MarkFirst() { for (var i = 0; i < Rows.Count; i++) Rows[i].IsFirst = i == 0; }

    /// <summary>"Found 1.2 GB you can remove": the size of everything the scan found.</summary>
    public string TotalTitle => text["V2ClTotalTitle"];
    /// <summary>For example "15.87 GB selected · 7 items · 169,823 files".</summary>
    public string TotalSummary => text.Format("V2ClSelSummary", SizeFormat.Bytes(text, SelectedBytes), text.Number(SelectedCategories), text.Number(SelectedCount));
    public string CleanSelectedLabel => text.Format("V2ClCleanSelected", SizeFormat.Bytes(text, SelectedBytes));
    public string FoundHeading => text.Format("V2ClFound", SizeFormat.Bytes(text, Rows.Sum(r => r.Scan.Bytes)));

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(FoundHeading)); OnPropertyChanged(nameof(TotalSummary)); OnPropertyChanged(nameof(CleanSelectedLabel)); OnPropertyChanged(nameof(TotalTitle));
        OnPropertyChanged(nameof(SelectionText)); OnPropertyChanged(nameof(SelectedBytes)); OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(NothingToClean));
        OnPropertyChanged(nameof(SelectedCategories)); OnPropertyChanged(nameof(SelectionBarText)); OnPropertyChanged(nameof(SelectedTitles));
        CleanCommand.NotifyCanExecuteChanged(); SelectLowRiskCommand.NotifyCanExecuteChanged(); ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private void SelectLowRisk()
    {
        foreach (var row in Rows.Where(r => r.Risk == RiskLevel.Low && r.CanSelect)) row.IsSelected = true;
    }

    private void ClearSelection()
    {
        foreach (var row in Rows.Where(r => r.IsSelected)) row.IsSelected = false;
    }

    // ---- remove ----

    private Task CleanAsync()
    {
        if (!CleanCommand.CanExecute(null)) return Task.CompletedTask;
        return work = RunCleanAsync();
    }

    private async Task RunCleanAsync()
    {
        var chosen = SelectedRows.ToList();
        var confirmation = new ActionConfirmation(text["FreeSpaceConfirmTitle"], text["FreeSpaceConfirmBody"],
            chosen.Select(r => text.Format("FreeSpaceConfirmLine", r.Title, r.Category.Kind == CleanupCategoryKind.RecycleBin ? Items(r.Scan.Count) : Files(r.Scan.Count), r.SizeText)).ToList(),
            ShowAnyAgeWarning && chosen.Any(r => HasAgeRule(r.Category.Id)) ? text["FreeSpaceConfirmWarning"] + " " + text["FreeSpaceAnyAgeWarning"] : text["FreeSpaceConfirmWarning"], text["FreeSpaceConfirmPrimary"])
        {
            Changes = chosen.Select(r => new ConfirmChange(r.Title, null, null, null, text.Format("FreeSpaceConfirmItem", r.Category.Kind == CleanupCategoryKind.RecycleBin ? Items(r.Scan.Count) : Files(r.Scan.Count), r.SizeText))).ToList(),
            Risk = RiskLevel.Medium, Undo = ConfirmUndo.No, Restart = false, Irreversible = true,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;

        using var run = Runs.Begin(text["ACRunClean"], true, Cancel);
        if (!run.Started) return;
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Phase = FreeSpacePhase.Cleaning; ErrorText = null; ProgressText = text["FreeSpaceCleaning"];
        var progress = new Progress<CleanupProgress>(p => { if (Phase == FreeSpacePhase.Cleaning) ProgressText = text.Format("FreeSpaceCleaningProgress", text[$"Cleanup_{p.Category}_Title"], Files(p.RemovedFiles), SizeFormat.Bytes(text, p.RemovedBytes)); });
        try
        {
            lastResult = await runner.RunAsync(chosen.Select(r => new CleanupSelection(r.Category, r.Scan)).ToList(), progress, token).ConfigureAwait(true);
            BuildReport();
            Phase = FreeSpacePhase.Done;
            RefreshDisks();
            ProgressText = "";
            RefreshHistory();
            OnPropertyChanged(nameof(ReportHeadline));
            run.Finish(lastResult.State == CleanupRunState.Cancelled ? RunResult.Stopped : RunResult.Ok, ReportHeadline);
        }
        catch (OperationCanceledException) { Phase = FreeSpacePhase.Review; ProgressText = ""; RefreshHistory(); run.Finish(RunResult.Stopped); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The intent could not be recorded (nothing was removed) or the record could not be completed.
            ErrorText = text["FreeSpaceCleanFailed"]; Phase = FreeSpacePhase.Review; ProgressText = "";
            RefreshHistory();
            run.Finish(RunResult.Error, text["FreeSpaceCleanFailed"]);
        }
    }

    private void BuildReport()
    {
        ReportLines.Clear();
        if (lastResult is null) return;
        foreach (var c in lastResult.Categories)
        {
            // Emptying the bin removes what is in it at that moment; the numbers are the ones the scan saw.
            ReportLines.Add(c.Id == CleanupCategoryId.RecycleBin
                ? text.Format("FreeSpaceReportBin", text[$"Cleanup_{c.Id}_Title"], SizeFormat.Bytes(text, c.RemovedBytes))
                : text.Format("FreeSpaceReportLine", text[$"Cleanup_{c.Id}_Title"], Files(c.RemovedFiles), SizeFormat.Bytes(text, c.RemovedBytes)));
            foreach (var (reason, count) in c.NotRemoved.OrderByDescending(kv => kv.Value))
                ReportLines.Add("    " + text.Format("FreeSpaceNotRemoved", text.Number(count), text.Has("Reason_" + reason) ? text["Reason_" + reason] : reason));
        }
        if (lastResult.RemovedFiles == 0 && lastResult.State == CleanupRunState.Completed) ReportLines.Add(text["FreeSpaceNoneRemoved"]);
    }

    private void Cancel() => cts?.Cancel();

    /// <summary>Completes when no scan or removal is running.</summary>
    public Task WhenIdleAsync() => work;

    /// <summary>Stops a running scan when the user leaves the page. A removal goes on: it is a registered run that the Action center can stop.</summary>
    public void Stop() { if (Phase == FreeSpacePhase.Scanning) cts?.Cancel(); }

    public void Dispose() { cts?.Cancel(); cts?.Dispose(); }

    private void RefreshHistory()
    {
        string value;
        try
        {
            var last = journal.History(1).FirstOrDefault();
            value = last is null ? "" : last.Result is null ? text["FreeSpaceInterrupted"]
                : text.Format("FreeSpaceLast", text.When(last.Result.EndedUtc.ToLocalTime(), "g"), SizeFormat.Bytes(text, last.Result.RemovedBytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { value = ""; }
        if (value == HistoryText) return;
        HistoryText = value;
        OnPropertyChanged(nameof(HistoryText)); OnPropertyChanged(nameof(HasHistory));
    }
}
