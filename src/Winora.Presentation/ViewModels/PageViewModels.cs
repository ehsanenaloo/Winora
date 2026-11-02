// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;
using Winora.Infrastructure.Persistence;

namespace Winora.Desktop;

/// <summary>Asks the user to confirm the exact simulated change. Implemented by the window with a ContentDialog.</summary>
public interface IReviewPrompt
{
    Task<bool> ConfirmAsync(ReviewSummary summary);
    /// <summary>Shows the exact before and after of every real settings change; true only when the user confirms the whole list.</summary>
    Task<bool> ConfirmChangesAsync(ChangeReview review);
    /// <summary>"Apply now" for a pending change: warns that open File Explorer windows will close.</summary>
    Task<bool> ConfirmExplorerRestartAsync();
    /// <summary>Explicit confirmation before ending the user's session.</summary>
    Task<bool> ConfirmSignOutAsync();
    /// <summary>Lists what will be removed for good and asks once. True only when the user confirms.</summary>
    Task<bool> ConfirmActionAsync(ActionConfirmation confirmation);
    /// <summary>Shows read-only details with a Copy details button and one closing button. A host without a dialog shows nothing.</summary>
    Task ShowDetailsAsync(DetailsDialog details) => Task.CompletedTask;
}

/// <summary>Safe default: nothing is confirmed unless a host supplies a dialog.</summary>
public sealed class DenyReviewPrompt : IReviewPrompt
{
    public Task<bool> ConfirmAsync(ReviewSummary summary) => Task.FromResult(false);
    public Task<bool> ConfirmChangesAsync(ChangeReview review) => Task.FromResult(false);
    public Task<bool> ConfirmExplorerRestartAsync() => Task.FromResult(false);
    public Task<bool> ConfirmSignOutAsync() => Task.FromResult(false);
    public Task<bool> ConfirmActionAsync(ActionConfirmation confirmation) => Task.FromResult(false);
}

public sealed record ReviewSummary(string SettingName, string Before, string After, string Arrow, bool WillChange, string Risk, string Recovery, string Restart);

/// <summary>A page that reports the result of its last action as a short text. A failure stays as a banner; a success becomes a toast.</summary>
public interface INoticePage
{
    string? Notice { get; }
    bool NoticeIsError { get; }
}

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(string key, LocalizationService text, SimulationModel model)
    {
        Key = key; Text = text; Model = model;
        Runs = new RunSource(key);
        Feedback = new FeedbackCenter(text);
        text.PropertyChanged += (_, _) => OnLanguageChanged();
        model.Changed += (_, _) => OnModelChanged();
    }
    public string Key { get; }
    /// <summary>Registers the long runs of this page in the activity service of the Action center. The shell sets the service; a page created alone has none and the handles do nothing.</summary>
    public RunSource Runs { get; }
    /// <summary>The staged changes of this page with its own review words and actions, or null when the page stages nothing. The Action center shows them.</summary>
    public virtual StagingView? Staging => null;
    /// <summary>Toasts and result reports. The shell replaces it with the shared center; a page created alone has its own that nothing shows.</summary>
    public FeedbackCenter Feedback { get; set; }
    /// <summary>Opens the History page for one area (the "Show all in History" link of the change history block). The shell sets it; a page created alone does nothing.</summary>
    public Action<HistoryArea>? OpenHistory { get; set; }
    /// <summary>Tells the page that a journal was written elsewhere since it last read (the shell sets the signal). A page that reads once reads again on its next activation.</summary>
    public JournalReader Journal { get; } = new();
    /// <summary>The failure banner of a page with a <see cref="INoticePage"/> notice. A success notice is shown as a toast, never as a banner.</summary>
    public bool ShowNoticeBanner => this is INoticePage { Notice.Length: > 0, NoticeIsError: true };
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is "Notice" or "NoticeIsError" or "HasNotice" or "") base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowNoticeBanner)));
    }
    public LocalizationService Text { get; }
    protected SimulationModel Model { get; }
    public string Title => Text[Key];
    /// <summary>Resource key of the navigation section this page sits in (set by the shell); null for the footer pages.</summary>
    public string? SectionKey { get; set; }
    /// <summary>"Tune › Customize": where the page is, shown above its title. Empty for pages outside a section.</summary>
    public string Breadcrumb => SectionKey is null ? "" : Text[SectionKey] + " " + Text["BreadcrumbSeparator"] + " " + Title;
    public virtual bool IsBusy => Model.IsBusy;
    /// <summary>Changes staged on this page and not yet applied. The navigation shows a dot while it is above zero.</summary>
    public virtual int PendingCount => 0;
    /// <summary>No page shows the "simulation only" banner any more: Customize and History work on real settings, discovery pages are read-only real data, and the demonstration card on Overview labels itself.</summary>
    public virtual bool ShowsSimulationBanner => false;
    /// <summary>Called when the page becomes the current page. Pages that scan start or resume work here.</summary>
    public virtual void Activate() { }
    /// <summary>The page's own read-only refresh (F5). Null when the page has nothing to refresh.</summary>
    public virtual System.Windows.Input.ICommand? ShortcutRefresh() => null;
    /// <summary>Called when the user leaves the page. Pages cancel any running scan here.</summary>
    public virtual void Deactivate() { }
    /// <summary>Completes when no background work started by this page is outstanding.</summary>
    public virtual Task WhenIdleAsync() => Task.CompletedTask;
    protected virtual void OnLanguageChanged() => OnPropertyChanged(string.Empty);
    protected virtual void OnModelChanged() => OnPropertyChanged(nameof(IsBusy));
    protected string EnabledLabel(bool value) => Text[value ? "Enabled" : "Disabled"];
}

/// <summary>The navigation groups, in display order. Footer items sit at the bottom of the pane.</summary>
public enum NavSection { Home, Tune, Clean, Manage, Tools, Footer }

public sealed class DestinationInfo(string key, IconKey icon, string page, NavSection section = NavSection.Footer)
{
    public string Key { get; } = key;
    public IconKey Icon { get; } = icon;
    /// <summary>Overview, Customize, History, Settings or Planned.</summary>
    public string Page { get; } = page;
    public NavSection Section { get; } = section;
    public bool IsAvailable => Page != "Planned";
    /// <summary>Resource key of the section header text; null for the footer.</summary>
    public string? SectionKey => Section == NavSection.Footer ? null : "NavSection" + Section;
}

public static class StateStyle
{
    public static Tone For(OperationState state) => state switch
    {
        OperationState.Applied or OperationState.Reverted => Tone.Success,
        OperationState.AlreadyConfigured => Tone.Info,
        OperationState.Partial or OperationState.PendingRestart or OperationState.Prepared or OperationState.Executing => Tone.Caution,
        OperationState.Failed or OperationState.Unsupported => Tone.Danger,
        _ => Tone.Info
    };
}

public sealed class OverviewViewModel : PageViewModel
{
    private int selectedTab;
    /// <summary>The tab of the page (Summary, Performance, This PC, Recent changes), kept while the app runs.</summary>
    public int SelectedTab { get => selectedTab; set => SetProperty(ref selectedTab, value); }

    /// <summary>The performance check card; null when the host has no probe (the card is then not shown).</summary>
    public PerformanceCheckViewModel? Performance { get => performance; set { if (SetProperty(ref performance, value)) OnPropertyChanged(nameof(HasPerformance)); } }
    private PerformanceCheckViewModel? performance;
    public bool HasPerformance => performance is not null;
    public OverviewViewModel(LocalizationService text, SimulationModel model, Action<string> navigate, InventorySummaryViewModel inventory, Func<int> pendingCount, DashboardViewModel? dashboard = null,
        NextStepsViewModel? nextSteps = null, RecentChangesViewModel? recent = null, QuickProfileViewModel? quickProfile = null, SetupScoreViewModel? score = null) : base("Overview", text, model)
    {
        this.pendingCount = pendingCount;
        Inventory = inventory; Dashboard = dashboard; NextSteps = nextSteps; Recent = recent; QuickProfile = quickProfile; Score = score;
        OpenCustomizeCommand = new(() => navigate("Customize"), () => !Model.IsBusy);
        OpenPageCommand = new(key => { if (!string.IsNullOrEmpty(key)) navigate(key); });
        ScanCommand = new(() => work = ScanAllAsync(), () => !IsScanning);
        CancelScanCommand = new(Cancel, () => IsScanning);
        var wasScanning = false;
        void Changed()
        {
            ScanCommand.NotifyCanExecuteChanged(); CancelScanCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(IsScanning)); OnPropertyChanged(nameof(MetaText));
            // The ring plays once when a scan ends.
            if (wasScanning && !IsScanning) Score?.Recount(true);
            wasScanning = IsScanning;
        }
        inventory.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(InventorySummaryViewModel.IsScanning)) Changed(); };
        if (dashboard is not null) dashboard.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(DashboardViewModel.IsLoading) or nameof(DashboardViewModel.UpdatedText)) Changed(); };
        // The rules read the facts again whenever the facts change; there is no timer.
        if (nextSteps is not null)
        {
            if (dashboard is not null) dashboard.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DashboardViewModel.HasData)) nextSteps.Request(); };
            inventory.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(InventorySummaryViewModel.LastScannedText)) nextSteps.Request(); };
        }
    }
    private readonly Func<int> pendingCount;
    private Task work = Task.CompletedTask;
    public RelayCommand OpenCustomizeCommand { get; }
    public RelayCommand<string> OpenPageCommand { get; }
    /// <summary>One scan for the page: reads the facts of the PC and scans startup items, tasks, services and apps. It only reads.</summary>
    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public bool IsScanning => Inventory.IsScanning || Dashboard?.IsLoading == true;
    public string MetaText => Dashboard?.UpdatedText ?? Inventory.LastScannedText;
    public override System.Windows.Input.ICommand? ShortcutRefresh() => ScanCommand;
    public InventorySummaryViewModel Inventory { get; }
    /// <summary>The system dashboard; null in a host that has none.</summary>
    public DashboardViewModel? Dashboard { get; }
    public NextStepsViewModel? NextSteps { get; }
    public RecentChangesViewModel? Recent { get; }
    public QuickProfileViewModel? QuickProfile { get; }
    /// <summary>The setup score of the hero card; null in a host that has none.</summary>
    public SetupScoreViewModel? Score { get; }
    public bool HasScore => Score is not null;
    public bool HasQuickProfile => QuickProfile is not null;
    public bool HasDashboard => Dashboard is not null;
    public bool HasNextSteps => NextSteps is not null;
    public bool HasRecent => Recent is not null;
    /// <summary>The developer simulation card and the "planned areas" card are shown in debug builds only.</summary>
    public bool ShowSimulation =>
#if DEBUG
        true;
#else
        false;
#endif
    public override void Activate()
    {
        Dashboard?.Activate();
        Score?.Recount(false);
        NextSteps?.Request();
        QuickProfile?.Activate();
        Recent?.Activate();
    }
    public override Task WhenIdleAsync() => Task.WhenAll(Dashboard?.WhenIdleAsync() ?? Task.CompletedTask, work, NextSteps?.WhenIdleAsync() ?? Task.CompletedTask, QuickProfile?.WhenIdleAsync() ?? Task.CompletedTask);
    public override void Deactivate() { Inventory.Cancel(); Dashboard?.Cancel(); }
    public void Cancel() { Inventory.Cancel(); Dashboard?.Cancel(); }
    private async Task ScanAllAsync()
    {
        using var run = Runs.Begin(Text["ACRunMeasure"], false, Cancel);
        var dashboardRead = Dashboard?.RefreshAsync() ?? Task.CompletedTask;
        await Task.WhenAll(dashboardRead, Inventory.ScanAsync()).ConfigureAwait(true);
        NextSteps?.Request();
        run.Finish(RunResult.Ok, quiet: true);
    }
    public string ActualLabel => EnabledLabel(Model.Actual);
    public string PendingCountText => Text.Format("PendingCountFormat", pendingCount());
    public string PendingDetail => pendingCount() > 0 ? Text["SetPendingOverview"] : Text["NoPending"];
    public bool HasLast => Model.Operations.Count > 0;
    public bool HasNoLast => !HasLast;
    public string LastStateLabel => Model.Operations.FirstOrDefault() is { } r ? Text["State" + r.State] : "";
    public Tone LastTone => Model.Operations.FirstOrDefault() is { } r ? StateStyle.For(r.State) : Tone.Info;
    public string LastWhen => Model.Operations.FirstOrDefault() is { } r ? Text.When(r.CreatedAtUtc.ToLocalTime(), "g") : "";
    public string PlannedAreasText => Text.Format("PlannedAreasFormat", 3);
    protected override void OnLanguageChanged() { NextSteps?.LanguageChanged(); Recent?.Rebuild(); base.OnLanguageChanged(); }
    protected override void OnModelChanged() { base.OnModelChanged(); OnPropertyChanged(string.Empty); OpenCustomizeCommand.NotifyCanExecuteChanged(); }
}

public class ChoiceOption(string value, string resourceKey, LocalizationService text) : ObservableObject
{
    public string Value { get; } = value;
    public string Label => text[resourceKey];
    public void Refresh() => OnPropertyChanged(nameof(Label));
    /// <summary>Screen readers and the combo box's type-to-select read the item text from here.</summary>
    public override string ToString() => Label;
}

public sealed class SettingsViewModel : PageViewModel
{
    private readonly SqliteOperationStore store;
    private readonly ErrorPresenter errors;
    private string theme;
    private string density;
    private string accent, accentHex;
    private bool accentHexInvalid;
    private bool saveFailed;
    private readonly IDiagnosticsExporter exporter;
    private readonly IFolderPicker picker;
    private readonly IClipboardService clipboard;
    private bool exporting;
    private string archivePath = "", copyStatus = "";
    public const string WelcomePreference = "firstrun.welcome_seen";

    public SettingsViewModel(LocalizationService text, SimulationModel model, SqliteOperationStore store, ErrorPresenter errors, string storagePath,
        IDiagnosticsExporter exporter, IFolderPicker picker, IClipboardService clipboard, AppDataPaths paths, IFileLocationOpener? opener = null) : base("Settings", text, model)
    {
        this.opener = opener;
        OpenDataFolderCommand = new(() => this.opener?.Reveal(DataFolder ?? ""), () => this.opener is not null);
        this.store = store; this.errors = errors; StoragePath = storagePath;
        this.exporter = exporter; this.picker = picker; this.clipboard = clipboard;
        DataFolder = paths.Root; LogFolder = paths.LogsFolder;
        LanguageOptions = [new("en", "LanguageEnglishNative", text), new("fa", "LanguagePersianNative", text)];
        ThemeOptions = [new("System", "System", text), new("Light", "Light", text), new("Dark", "Dark", text)];
        DensityOptions = [new(DensityService.Comfortable, "DensityComfortable", text), new(DensityService.Compact, "DensityCompact", text)];
        density = DensityService.Normalize(store.ReadPreference(DensityService.PreferenceKey));
        smoothScrolling = store.ReadPreference(SmoothScrollingKey) != "off";
        notifyOnCompletion = store.ReadPreference(NotifyOnCompletionKey) != "off";
        removeProvisioned = store.ReadPreference(RemoveProvisionedKey) != "off";
        keepInTray = store.ReadPreference(KeepInTrayKey) == "on";
        slowToolWait = WaitLimits.Parse(store.ReadPreference(WaitLimits.PreferenceKey));
        SlowToolWaitOptions = [new(WaitLimits.Code(Winora.Application.SlowToolWait.Normal), "SlowWaitNormal", text), new(WaitLimits.Code(Winora.Application.SlowToolWait.Long), "SlowWaitLong", text),
            new(WaitLimits.Code(Winora.Application.SlowToolWait.VeryLong), "SlowWaitVeryLong", text)];
        SmoothScrollingApplied?.Invoke(smoothScrolling);
        theme = store.ReadPreference("theme") is "Light" or "Dark" ? store.ReadPreference("theme")! : "System";
        accent = AccentService.Normalize(store.ReadPreference(AccentService.PreferenceKey));
        accentHex = AccentService.TryParseHex(store.ReadPreference(AccentService.HexPreferenceKey), out var savedHex) ? AccentService.ToHex(savedHex) : AccentService.DefaultCustomHex;
        AccentOptions = [new AccentOption(AccentService.System, "AccentSystem", HostServices.SystemAccent(), text),
            .. AccentService.Palette.Select(p => new AccentOption(p.Code, p.LabelKey, p.Light, text)),
            new AccentOption(AccentService.Custom, "AccentCustom", AccentService.TryParseHex(accentHex, out var previewHex) ? previewHex : Rgb.Gray, text)];
        text.SetLanguage(store.ReadPreference("language") ?? "en");
        ExportDiagnosticsCommand = new(ExportDiagnosticsAsync, () => !exporting);
        CopyPathCommand = new(CopyPath);
        ShowShortcutsCommand = new(() => ShortcutsRequested?.Invoke(this, EventArgs.Empty));
        ShowWelcomeCommand = new(() => WelcomeRequested?.Invoke(this, EventArgs.Empty));
        text.PropertyChanged += (_, _) => { DiagnosticsNotice.Refresh(); CopyStatus = ""; };
    }
    /// <summary>Small per-user choices of other pages (folded groups). A failed save only costs the choice for the next start.</summary>
    public void WritePreference(string key, string value) { try { store.WritePreference(key, value); } catch (Exception ex) when (ex is not OperationCanceledException) { errors.Report(ex, "Saving preference " + key); } }
    public IReadOnlyList<ChoiceOption> LanguageOptions { get; }
    public IReadOnlyList<ChoiceOption> ThemeOptions { get; }
    public IReadOnlyList<ChoiceOption> DensityOptions { get; }
    public IReadOnlyList<AccentOption> AccentOptions { get; }
    public string StoragePath { get; }
    /// <summary>The application-owned folder (journal, preferences). Shown as a copyable LTR path; Winora does not open Explorer for it.</summary>
    public string DataFolder { get; }
    public string LogFolder { get; }
    public event EventHandler? ThemeChanged;
    public event EventHandler? DensityChanged;
    public event EventHandler? AccentChanged;
    public event EventHandler? ShortcutsRequested;
    public event EventHandler? WelcomeRequested;
    public string Theme => theme;
    public string Density => density;
    public string Accent => accent;
    public string AccentHexValue => accentHex;
    public string AccentCode
    {
        get => accent;
        set { if (AccentService.IsKnown(value) && value != accent) _ = SetAccentAsync(value); }
    }
    /// <summary>The text of the custom color box. Typing a valid #RRGGBB applies it at once; an invalid text only shows a message and changes nothing.</summary>
    public string AccentHex
    {
        get => accentHex;
        set { if (value != accentHex) _ = SetAccentHexAsync(value); }
    }
    public bool IsCustomAccent => accent == AccentService.Custom;
    public bool AccentHexInvalid { get => accentHexInvalid; private set { if (SetProperty(ref accentHexInvalid, value)) { OnPropertyChanged(nameof(AccentHintText)); OnPropertyChanged(nameof(AccentHintIsError)); } } }
    public bool AccentHintIsError => accentHexInvalid;
    /// <summary>Under the custom color box: an error for a bad entry, a note when the color was changed for readability.</summary>
    public string AccentHintText => accentHexInvalid ? Text["AccentHexInvalid"]
        : accent == AccentService.Custom && AccentService.WasAdjusted(accentHex, AccentService.IsDarkApplied) ? Text["AccentAdjusted"] : Text["AccentHexHint"];
    public bool SaveFailed { get => saveFailed; private set { if (SetProperty(ref saveFailed, value)) OnPropertyChanged(nameof(SaveFailedText)); } }
    public string SaveFailedText => SaveFailed ? Text["SettingsSaveFailed"] : "";
    public static string Version => typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
    public string VersionText => Text.Format("VersionFormat", Version);
    public const string LicenseId = "GPL-3.0-only";
    public const string NoticesFile = "THIRD-PARTY-NOTICES.md";
    public string BuildInfoText
    {
        get
        {
            var assembly = typeof(SettingsViewModel).Assembly;
#if DEBUG
            var configuration = "Debug";
#else
            var configuration = "Release";
#endif
            var revision = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').ElementAtOrDefault(1);
            var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + (revision is { Length: > 0 } r ? ", " + (r.Length > 12 ? r[..12] : r) : "");
            // Runtime, architecture and build numbers are Latin data: pinned, and the build number keeps Latin digits like any version.
            return Text.Format("BuildInfoFormat", Text.LtrPart(configuration), Text.LtrPart(runtime), Text.LtrPart(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()),
                Text.LtrPart(Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    // ---- First run ----
    /// <summary>True until the welcome screen has been shown once; the choice is stored in the application preferences.</summary>
    public bool NeedsWelcome => store.ReadPreference(WelcomePreference) != "1";
    public async Task MarkWelcomeSeenAsync() => await PersistAsync(WelcomePreference, "1");

    // ---- Diagnostics (local export only; nothing is uploaded) ----
    public SettingsNotice DiagnosticsNotice { get; } = new();
    public AsyncRelayCommand ExportDiagnosticsCommand { get; }
    public RelayCommand<string> CopyPathCommand { get; }
    public RelayCommand ShowShortcutsCommand { get; }
    public RelayCommand ShowWelcomeCommand { get; }
    public bool IsExporting { get => exporting; private set { if (SetProperty(ref exporting, value)) ExportDiagnosticsCommand.NotifyCanExecuteChanged(); } }
    public string ArchivePath { get => archivePath; private set { if (SetProperty(ref archivePath, value)) OnPropertyChanged(nameof(HasArchive)); } }
    public bool HasArchive => archivePath.Length > 0;
    public string CopyStatus { get => copyStatus; private set => SetProperty(ref copyStatus, value); }

    private void CopyPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        CopyStatus = clipboard.TrySetText(path) ? Text["PathCopied"] : Text["PathCopyFailed"];
    }

    private async Task ExportDiagnosticsAsync()
    {
        if (exporting) return;
        IsExporting = true;
        DiagnosticsNotice.Clear(); ArchivePath = "";
        using var run = Runs.Begin(Text["ACRunDiag"], false);
        try
        {
            var folder = await picker.PickFolderAsync(Text["DiagPickerTitle"], CancellationToken.None);
            if (string.IsNullOrWhiteSpace(folder))
            {
                DiagnosticsNotice.Show(BannerSeverity.Informational, () => Text["DiagGroup"], () => Text["DiagNoFolder"], "diagnostics.no_folder");
                run.Finish(RunResult.Stopped);
                return;
            }
            var result = await exporter.ExportAsync(folder, CancellationToken.None);
            ArchivePath = result.ArchivePath;
            DiagnosticsNotice.Show(BannerSeverity.Success, () => Text["DiagCreatedTitle"], () => Text.Format("DiagCreatedFormat", Text.Number(result.FileCount), FormatBytes(result.Bytes)), "diagnostics.created");
            run.Finish(RunResult.Ok, Text["DiagCreatedTitle"]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = errors.Report(ex, "Diagnostics export");
            if (error is not null)
            {
                var severity = error.Severity is ErrorSeverity.Warning or ErrorSeverity.Info ? BannerSeverity.Warning : BannerSeverity.Error;
                DiagnosticsNotice.Show(severity, () => Text["DiagErrorTitle"], () => $"{errors.Message(error)} {errors.Reference(error)}", error.Code);
            }
            run.Finish(RunResult.Error, Text["DiagErrorTitle"]);
        }
        finally { IsExporting = false; }
    }

    private string FormatBytes(long bytes) => Text.Size(bytes);

    public string LanguageCode
    {
        get => Text.Language;
        set { if (value is "en" or "fa" && value != Text.Language) _ = SetLanguageAsync(value); }
    }
    public string ThemeCode
    {
        get => theme;
        set { if (value is "System" or "Light" or "Dark" && value != theme) _ = SetThemeAsync(value); }
    }

    public string DensityCode
    {
        get => density;
        set { if (value is DensityService.Comfortable or DensityService.Compact && value != density) _ = SetDensityAsync(value); }
    }

    public async Task SetLanguageAsync(string language)
    {
        language = language == "fa" ? "fa" : "en";
        Text.SetLanguage(language);
        await PersistAsync("language", language);
    }
    public async Task SetThemeAsync(string value)
    {
        theme = value is "Light" or "Dark" ? value : "System";
        OnPropertyChanged(nameof(Theme)); OnPropertyChanged(nameof(ThemeCode));
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        await PersistAsync("theme", theme);
    }
    public async Task SetAccentAsync(string value)
    {
        accent = AccentService.Normalize(value);
        OnPropertyChanged(nameof(Accent)); OnPropertyChanged(nameof(AccentCode)); OnPropertyChanged(nameof(IsCustomAccent)); OnPropertyChanged(nameof(AccentHintText));
        AccentChanged?.Invoke(this, EventArgs.Empty);
        await PersistAsync(AccentService.PreferenceKey, accent);
    }
    public async Task SetAccentHexAsync(string value)
    {
        if (!AccentService.TryParseHex(value, out var color)) { AccentHexInvalid = true; return; }
        AccentHexInvalid = false;
        accentHex = AccentService.ToHex(color);
        OnPropertyChanged(nameof(AccentHex)); OnPropertyChanged(nameof(AccentHexValue)); OnPropertyChanged(nameof(AccentHintText));
        if (AccentOptions.FirstOrDefault(o => o.Value == AccentService.Custom) is { } custom) custom.SetPreview(color);
        await PersistAsync(AccentService.HexPreferenceKey, accentHex);
        if (accent == AccentService.Custom) AccentChanged?.Invoke(this, EventArgs.Empty);
    }
    private readonly IFileLocationOpener? opener;
    public RelayCommand OpenDataFolderCommand { get; }

    public const string NotifyOnCompletionKey = "notify_completion", RemoveProvisionedKey = "remove_provisioned";
    private bool notifyOnCompletion = true, removeProvisioned = true;
    /// <summary>Shows a notification when a change has been applied or undone (a failure is always shown).</summary>
    public bool NotifyOnCompletion
    {
        get => notifyOnCompletion;
        set { if (notifyOnCompletion == value) return; notifyOnCompletion = value; OnPropertyChanged(); OnPropertyChanged(nameof(NotifyOnCompletionState)); _ = PersistAsync(NotifyOnCompletionKey, value ? "on" : "off"); }
    }
    public string NotifyOnCompletionState => Text[notifyOnCompletion ? "DashOn" : "DashOff"];
    /// <summary>When Preinstalled apps removes an app, the copy Windows would install for new users goes too.</summary>
    public bool RemoveProvisioned
    {
        get => removeProvisioned;
        set { if (removeProvisioned == value) return; removeProvisioned = value; OnPropertyChanged(); OnPropertyChanged(nameof(RemoveProvisionedState)); _ = PersistAsync(RemoveProvisionedKey, value ? "on" : "off"); }
    }
    public string RemoveProvisionedState => Text[removeProvisioned ? "DashOn" : "DashOff"];

    public const string KeepInTrayKey = "keep_in_tray";
    private bool keepInTray;
    /// <summary>Closing the window hides Winora in the notification area instead of ending it (off by default). An elevated process then stays running until Exit.</summary>
    public bool KeepInTray
    {
        get => keepInTray;
        set { if (keepInTray == value) return; keepInTray = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeepInTrayState)); _ = PersistAsync(KeepInTrayKey, value ? "on" : "off"); }
    }
    public string KeepInTrayState => Text[keepInTray ? "DashOn" : "DashOff"];
    private Winora.Application.SlowToolWait slowToolWait;
    /// <summary>Advanced: how long Winora waits for uninstallers, DISM, SFC and the other repair tools. Read by the runners at the start of each run.</summary>
    public Winora.Application.SlowToolWait SlowToolWait => slowToolWait;
    public IReadOnlyList<ChoiceOption> SlowToolWaitOptions { get; }
    public string SlowToolWaitCode
    {
        get => WaitLimits.Code(slowToolWait);
        set
        {
            var next = WaitLimits.Parse(value);
            if (next == slowToolWait || WaitLimits.Code(next) != value) return;
            slowToolWait = next;
            OnPropertyChanged(); OnPropertyChanged(nameof(SlowToolWait));
            _ = PersistAsync(WaitLimits.PreferenceKey, value);
        }
    }

    public const string SmoothScrollingKey = "smooth_scrolling";
    /// <summary>Raised with the new value when smooth scrolling is read at start and each time it is changed. The window project switches the scroll glide with it.</summary>
    public static event Action<bool>? SmoothScrollingApplied;
    private bool smoothScrolling = true;
    /// <summary>Scroll with the mouse wheel in a smooth glide (on by default; it also needs Windows animations).</summary>
    public bool SmoothScrolling
    {
        get => smoothScrolling;
        set { if (smoothScrolling == value) return; _ = SetSmoothScrollingAsync(value); }
    }
    public async Task SetSmoothScrollingAsync(bool value)
    {
        smoothScrolling = value;
        SmoothScrollingApplied?.Invoke(value);
        OnPropertyChanged(nameof(SmoothScrolling)); OnPropertyChanged(nameof(SmoothScrollingState));
        await PersistAsync(SmoothScrollingKey, value ? "on" : "off");
    }
    public string SmoothScrollingState => Text[smoothScrolling ? "DashOn" : "DashOff"];

    public async Task SetDensityAsync(string value)
    {
        density = DensityService.Normalize(value);
        OnPropertyChanged(nameof(Density)); OnPropertyChanged(nameof(DensityCode));
        DensityChanged?.Invoke(this, EventArgs.Empty);
        await PersistAsync(DensityService.PreferenceKey, density);
    }
    public string? ReadPreference(string key)
    {
        try { return store.ReadPreference(key); }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Report(ex, "Reading preference " + key); return null; }
    }
    public Task WritePreferenceAsync(string key, string value) => PersistAsync(key, value);
    private async Task PersistAsync(string key, string value)
    {
        try { await Task.Run(() => store.WritePreference(key, value)); SaveFailed = false; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The choice still applies for this session; the failure is logged once with a reference and shown inline.
            errors.Report(ex, "Saving preference " + key); SaveFailed = true;
        }
    }
    protected override void OnLanguageChanged()
    {
        foreach (var o in LanguageOptions) o.Refresh();
        foreach (var o in ThemeOptions) o.Refresh();
        foreach (var o in DensityOptions) o.Refresh();
        foreach (var o in SlowToolWaitOptions) o.Refresh();
        foreach (var o in AccentOptions) o.Refresh();
        OnPropertyChanged(nameof(AccentHintText));
        base.OnLanguageChanged();
    }
}

public sealed class PlannedViewModel(string key, IconKey icon, LocalizationService text, SimulationModel model) : PageViewModel(key, text, model)
{
    public IconKey Icon { get; } = icon;
    public string Heading => Text.Format("PlannedTitleFormat", Title);
    public string Summary => Text["Planned_" + Key + "_Summary"];
    public string Item1 => Text["Planned_" + Key + "_Item1"];
    public string Item2 => Text["Planned_" + Key + "_Item2"];
}

/// <summary>Late-bound prompt so the window can register its dialog after view models exist.</summary>
public sealed class ReviewPromptHost : IReviewPrompt, IRestorePointPrompt
{
    public IReviewPrompt Inner { get; set; } = new DenyReviewPrompt();
    /// <summary>A host without a restore point dialog continues without one: nothing is created unless a person chose it.</summary>
    public Task<RestorePointChoice> AskAsync(RestorePointAsk ask, CancellationToken cancellationToken) =>
        Inner is IRestorePointPrompt prompt ? prompt.AskAsync(ask, cancellationToken) : Task.FromResult(RestorePointChoice.ContinueWithout);
    public Task<bool> ConfirmAsync(ReviewSummary summary) => Inner.ConfirmAsync(summary);
    public Task<bool> ConfirmChangesAsync(ChangeReview review) => Inner.ConfirmChangesAsync(review);
    public Task<bool> ConfirmExplorerRestartAsync() => Inner.ConfirmExplorerRestartAsync();
    public Task<bool> ConfirmSignOutAsync() => Inner.ConfirmSignOutAsync();
    public Task<bool> ConfirmActionAsync(ActionConfirmation confirmation) => Inner.ConfirmActionAsync(confirmation);
    public Task ShowDetailsAsync(DetailsDialog details) => Inner.ShowDetailsAsync(details);
}
