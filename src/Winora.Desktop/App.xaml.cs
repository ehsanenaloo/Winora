// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winora.Application;
using Winora.Domain;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.Windows;
using Winora.Instance;
namespace Winora.Desktop;

public partial class App : System.Windows.Application
{
    private ServiceProvider? provider;
    private InstanceServer? instance;

    /// <summary>Composition root shared with tests so they exercise the same wiring as the application.</summary>
    /// <param name="notificationFolder">The admin-only folder of the notification list. Null keeps it in the data root, so a test never writes to the machine folder.</param>
    /// <param name="dataRoot">The folder of the log, the profiles and the diagnostics. Null puts them beside the journal database, so a test that passes a temporary database path logs there too.</param>
    public static ServiceProvider BuildServices(string databasePath, LocalizationService? text = null, Action<IServiceCollection>? configure = null, string? dataRoot = null, Func<string>? notificationFolder = null)
    {
        var services = new ServiceCollection();
        var paths = new AppDataPaths(dataRoot ?? Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        services.AddSingleton(paths);
        // Local, redacted, size-bounded log. No remote sink exists; nothing leaves the machine.
        services.AddLogging(builder => builder.AddWinoraFileLogger(new FileLoggerOptions(paths.LogsFolder, new LogRedactor(RedactionContext.FromEnvironment()))));
        services.AddSingleton(text ?? new LocalizationService());
        services.AddSingleton(new SqliteOperationStore(databasePath));
        services.AddSingleton<IOperationStore>(sp => sp.GetRequiredService<SqliteOperationStore>());
        services.AddSingleton<SimulationEngine>();
        // Read-only Windows discovery adapters behind Application ports (tests replace them through configure).
        services.AddSingleton<IDiscoverySource<StartupItem>, StartupDiscovery>();
        services.AddSingleton<IDiscoverySource<ScheduledTaskInfo>, ScheduledTaskDiscovery>();
        services.AddSingleton<IDiscoverySource<ServiceInfo>, ServiceDiscovery>();
        services.AddSingleton<IDiscoverySource<InstalledApplication>, InstalledApplicationDiscovery>();
        services.AddSingleton<IDiscoverySource<PowerPlanInfo>, PowerPlanDiscovery>();
        services.AddSingleton<IDiscoverySource<Winora.Domain.Elevation.MachineSettingDefinition>>(static _ => new MachineDefinitionDiscovery(Winora.Infrastructure.Windows.Elevation.MachineProduction.CreateDefinitionSource()));
        // The quarantine lives in a folder only administrators can change (%ProgramData%\Winora\quarantine), so a manifest there can be trusted to name the original place of an item.
        services.AddSingleton(static _ => new Winora.Infrastructure.Windows.Cleanup.FileQuarantine(static () => AdminOnlyFolder.Ensure(QuarantineFolder())));
        services.AddSingleton<IQuarantine>(sp => sp.GetRequiredService<Winora.Infrastructure.Windows.Cleanup.FileQuarantine>());
        services.AddSingleton<IQuarantineDelete>(sp => sp.GetRequiredService<Winora.Infrastructure.Windows.Cleanup.FileQuarantine>());
        services.AddSingleton<IFolderQuarantine>(sp => sp.GetRequiredService<Winora.Infrastructure.Windows.Cleanup.FileQuarantine>());
        services.AddSingleton<IDiscoverySource<QuarantineEntry>>(sp => new QuarantineDiscovery(sp.GetRequiredService<IQuarantine>()));
        services.AddSingleton<IQuarantineJournal>(static _ => new Winora.Infrastructure.Windows.Cleanup.FileQuarantineJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "quarantine-journal.jsonl")));
        services.AddSingleton<ILeftoverFolderReader, Winora.Infrastructure.Windows.Cleanup.LeftoverFolderReader>();
        services.AddSingleton<LeftoverService>(sp =>
        {
            var environment = sp.GetService<CleanupEnvironment>() ?? Winora.Infrastructure.Windows.Cleanup.WindowsCleanupEnvironment.Create();
            var quarantine = sp.GetRequiredService<IQuarantine>();
            // A restore goes back only under the places the leftover scan looks in: the program folders, ProgramData and the profile of the signed-in user.
            var actions = new QuarantineActions(quarantine, sp.GetRequiredService<IQuarantineDelete>(), () => [.. environment.ProgramFiles, environment.ProgramData, environment.UserProfile]);
            return new LeftoverService(new LeftoverFolderScanner(sp.GetRequiredService<ILeftoverFolderReader>(), environment), sp.GetRequiredService<IFolderQuarantine>(), quarantine, actions,
                sp.GetRequiredService<IQuarantineJournal>());
        });
        services.AddSingleton<IQuarantineActions>(sp => sp.GetRequiredService<LeftoverService>());
        services.AddSingleton<IElevationState, ProcessElevationState>();
        services.AddSingleton<IPowerPlans>(static _ => new PowrProfPlans());
        services.AddSingleton<IPowerSystem>(static _ => new WindowsPowerSystem());
        services.AddSingleton<IMachinePolicyReader>(static _ => new Winora.Infrastructure.Windows.Elevation.WindowsMachinePolicyReader());
        services.AddSingleton<IMachineSettingsBackend>(sp => new Winora.Infrastructure.Windows.Elevation.DirectMachineSettings(new Winora.Elevation.Windows.Worker.WindowsMachineRegistry(), new Winora.Elevation.Windows.Worker.WindowsMachineServices(), sp.GetRequiredService<IElevationState>()));
        services.AddSingleton<IOptimizeJournal>(sp => new Winora.Infrastructure.Windows.Optimize.FileOptimizeJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "optimize-journal.jsonl")));
        services.AddSingleton<Winora.Infrastructure.Windows.Optimize.EmbeddedMachineDefinitions>();
        services.AddSingleton<OptimizeService>(sp => new OptimizeService(() => sp.GetRequiredService<Winora.Infrastructure.Windows.Optimize.EmbeddedMachineDefinitions>().Get(), sp.GetRequiredService<IMachineSettingsBackend>(),
            sp.GetRequiredService<IMachinePolicyReader>(), sp.GetRequiredService<IOsBuildProvider>(), sp.GetRequiredService<IOptimizeJournal>(), adapters: sp.GetRequiredService<IDisplayAdapterSource>()));
        services.AddSingleton<IDisplayAdapterSource>(static _ => new DisplayAdapterResolver(new Winora.Infrastructure.Windows.Display.DisplayDeviceEnumerator()));
        services.AddSingleton<IStartupApprovals>(static _ => new RegistryStartupApprovals());
        services.AddSingleton<IStartupFiles>(static _ => new WindowsStartupFiles());
        services.AddSingleton<IFileLocationOpener>(static _ => new ShellFileLocation());
        services.AddSingleton<IFileSignatureChecker>(static _ => new WinTrustSignatureChecker());
        services.AddSingleton<SignatureService>(sp => new SignatureService(sp.GetRequiredService<IFileSignatureChecker>()));
        services.AddSingleton<IStartupJournal>(sp => new FileStartupJournal(static () => ProtectedJournal.UserPathFor("startup-journal")));
        services.AddSingleton<StartupService>(sp => new StartupService(sp.GetRequiredService<IDiscoverySource<StartupItem>>(), sp.GetRequiredService<IStartupApprovals>(), sp.GetRequiredService<IStartupFiles>(),
            sp.GetRequiredService<IStartupJournal>(), sp.GetRequiredService<IElevationState>()));
        services.AddSingleton<IPowerJournal>(sp => new FilePowerJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "power-journal.jsonl")));
        services.AddSingleton<PowerChangeService>(sp => new PowerChangeService(sp.GetRequiredService<IPowerPlans>(), sp.GetRequiredService<IPowerJournal>(), null, sp.GetRequiredService<IPowerSystem>()));
        services.AddSingleton<IDnsAdapters>(static _ => new WindowsDnsAdapters());
        services.AddSingleton<IConnectivityProbe, SystemConnectivityProbe>();
        services.AddSingleton<IUpdateFactsSource>(static _ => new Winora.Infrastructure.Windows.Updates.WindowsUpdateFacts());
        services.AddSingleton<UpdatesService>(sp => new UpdatesService(sp.GetRequiredService<OptimizeService>(), sp.GetRequiredService<IOsBuildProvider>(), sp.GetRequiredService<IUpdateFactsSource>()));
        services.AddSingleton<IWindowsSettingsLauncher, ShellSettingsLauncher>();
        services.AddSingleton<INetworkJournal>(static _ => new FileNetworkJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "network-journal.jsonl")));
        services.AddSingleton<NetworkService>(sp => new NetworkService(sp.GetRequiredService<IDnsAdapters>(), sp.GetRequiredService<INetworkJournal>(), sp.GetRequiredService<IElevationState>(), EmbeddedDnsProviders.Load(), sp.GetRequiredService<IConnectivityProbe>()));
        services.AddSingleton<IConsentStore>(static _ => new RegistryConsentStore());
        services.AddSingleton<IPermissionJournal>(static _ => new FilePermissionJournal(static () => ProtectedJournal.UserPathFor("permissions-journal")));
        services.AddSingleton<PermissionService>(sp => new PermissionService(sp.GetRequiredService<IConsentStore>(), sp.GetRequiredService<IPermissionJournal>()));
        services.AddSingleton<IRepairRunner>(static sp => new Winora.Infrastructure.Windows.Repair.RepairProcessRunner(() => sp.GetRequiredService<SettingsViewModel>().SlowToolWait));
        services.AddSingleton<IRepairJournal>(static _ => new Winora.Infrastructure.Windows.Repair.FileRepairJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "repair-journal.jsonl")));
        services.AddSingleton<RepairService>(sp => new RepairService(sp.GetRequiredService<IRepairRunner>(), sp.GetRequiredService<IRepairJournal>(), sp.GetRequiredService<IElevationState>()));
        services.AddSingleton<FeatureService>(sp => new FeatureService(sp.GetRequiredService<RepairService>(), Winora.Infrastructure.Windows.Repair.EmbeddedFeatureClasses.Load()));
        services.AddSingleton<IScheduledTaskCatalog>(static _ => new ScheduledTaskCatalog());
        services.AddSingleton<IScheduledTaskControl>(static _ => new ScheduledTaskControl());
        services.AddSingleton<IScheduledTaskBackups>(static _ => new ScheduledTaskBackupStore(static () => ScheduledTaskBackupStore.DefaultFolder()));
        services.AddSingleton<IScheduledTaskJournal>(static _ => new FileScheduledTaskJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "scheduled-task-journal.jsonl")));
        services.AddSingleton<ScheduledTaskService>(sp => new ScheduledTaskService(sp.GetRequiredService<IScheduledTaskCatalog>(), sp.GetRequiredService<IScheduledTaskControl>(), sp.GetRequiredService<IScheduledTaskBackups>(), sp.GetRequiredService<IScheduledTaskJournal>()));
        services.AddSingleton<IServiceControl>(static _ => new WindowsServiceControl(new Winora.Elevation.Windows.Worker.WindowsMachineServices()));
        services.AddSingleton<IServiceJournal>(static _ => new FileServiceJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "service-journal.jsonl")));
        services.AddSingleton<ServiceManagerService>(sp => new ServiceManagerService(sp.GetRequiredService<IServiceControl>(), sp.GetRequiredService<IServiceJournal>()));
        services.AddSingleton<IRestorePointPort>(static _ => new Winora.Infrastructure.Windows.RestorePoint.WindowsRestorePointPort());
        services.AddSingleton<IRestorePointJournal>(static _ => new Winora.Infrastructure.Windows.RestorePoint.FileRestorePointJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "restore-point-journal.jsonl")));
        services.AddSingleton<RestorePointService>(sp => new RestorePointService(sp.GetRequiredService<IRestorePointPort>(), sp.GetRequiredService<IRestorePointJournal>(), sp.GetRequiredService<IElevationState>()));
        services.AddSingleton<IRestorePointPrompt>(sp => sp.GetRequiredService<ReviewPromptHost>());
        services.AddSingleton<IRestorePointGate>(sp => new SessionRestorePointGate(sp.GetRequiredService<RestorePointService>(), sp.GetRequiredService<IRestorePointPrompt>()));
        services.AddSingleton<ISystemInfoSource>(sp => new Winora.Infrastructure.Windows.Dashboard.WindowsSystemInfoSource(sp.GetRequiredService<IPowerPlans>(), sp.GetRequiredService<IOsBuildProvider>()));
        services.AddSingleton<IAppIconService>(static _ => new AppIconService(new ShellExecutableIcons()));
        services.AddSingleton<IAppIconSource>(sp => sp.GetRequiredService<IAppIconService>());
        services.AddSingleton<IPerformanceProbe, Winora.Infrastructure.Windows.Performance.WindowsPerformanceProbe>();
        services.AddSingleton<PerformanceService>();
        services.AddSingleton<DashboardService>(sp => new DashboardService(sp.GetRequiredService<ISystemInfoSource>()));
        services.AddSingleton<DashboardViewModel>(sp => new DashboardViewModel(sp.GetRequiredService<LocalizationService>(), sp.GetRequiredService<DashboardService>(), () => new WinoraSelfState(
            sp.GetRequiredService<IElevationState>().IsElevated, sp.GetRequiredService<IElevationState>().UnsafeLocation, sp.GetRequiredService<ISettingCatalog>().All.Count,
            sp.GetRequiredService<OptimizeService>().List().Count, PowerSettings.All.Count), sp.GetRequiredService<IClipboardService>()));
        services.AddSingleton<IAppPackages>(static _ => new Winora.Infrastructure.Windows.Bloatware.WinRtAppPackages());
        services.AddSingleton<IBloatJournal>(static _ => new Winora.Infrastructure.Windows.Bloatware.FileBloatJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "bloatware-journal.jsonl")));
        services.AddSingleton<BloatwareService>(sp => new BloatwareService(sp.GetRequiredService<IAppPackages>(), sp.GetRequiredService<IBloatJournal>(), sp.GetRequiredService<IElevationState>(), Winora.Infrastructure.Windows.Bloatware.EmbeddedBloatwareRules.Load(), store: sp.GetRequiredService<IStoreLauncher>(), removeProvisioned: () => sp.GetRequiredService<SettingsViewModel>().RemoveProvisioned));
        services.AddSingleton<IUninstallRegistrations>(static _ => new Winora.Infrastructure.Windows.UninstallRegistrationReader());
        services.AddSingleton<IUninstallLauncher>(sp => new Winora.Infrastructure.Windows.RegisteredUninstallLauncher(sp.GetRequiredService<IFileSignatureChecker>(), () => sp.GetRequiredService<SettingsViewModel>().SlowToolWait));
        services.AddSingleton<IAppUninstallJournal>(static _ => new Winora.Infrastructure.Windows.FileAppUninstallJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "apps-journal.jsonl")));
        services.AddSingleton<AppUninstallService>(sp => new AppUninstallService(sp.GetRequiredService<DiscoveryService>().ApplicationsAsync, sp.GetRequiredService<IUninstallRegistrations>(), sp.GetRequiredService<IUninstallLauncher>(),
            sp.GetRequiredService<IAppUninstallJournal>(), sp.GetRequiredService<IElevationState>(), sp.GetRequiredService<BloatwareService>(), Environment.GetFolderPath(Environment.SpecialFolder.Windows), appFolder: AppContext.BaseDirectory));
        services.AddSingleton<IProfileStore>(sp => new FileProfileStore(sp.GetRequiredService<AppDataPaths>()));
        services.AddSingleton<IProfileFileAccess, Win32ProfileFileAccess>();
        services.AddSingleton<ProfileService>(sp => new ProfileService(sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<ISettingCatalog>(), sp.GetRequiredService<OptimizeService>(),
            sp.GetRequiredService<IElevationState>(), sp.GetRequiredService<IProfileStore>(), Winora.Infrastructure.Windows.Optimize.EmbeddedProfiles.Load(), sp.GetRequiredService<IRestorePointGate>()));
        services.AddSingleton<IStoreLauncher, ShellStoreLauncher>();
        services.AddSingleton<IDiskScanner, Winora.Infrastructure.Windows.Cleanup.DiskScanner>();
        services.AddSingleton<ICleanupCatalog>(static _ => new CleanupCatalog(Winora.Infrastructure.Windows.Cleanup.WindowsCleanupEnvironment.Create()));
        services.AddSingleton<IRecycleBin>(static _ => new Winora.Infrastructure.Windows.Cleanup.WindowsRecycleBin());
        services.AddSingleton<ICleanupScanner>(sp => new Winora.Infrastructure.Windows.Cleanup.CleanupScanner(recycleBin: sp.GetRequiredService<IRecycleBin>()));
        services.AddSingleton<IFileRemover>(static _ => new Winora.Infrastructure.Windows.Cleanup.HandleFileRemover());
        services.AddSingleton<ICleanupJournal>(sp => new Winora.Infrastructure.Windows.Cleanup.FileCleanupJournal(static () => Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "cleanup-journal.jsonl")));
        services.AddSingleton<CleanupRunner>(sp => new CleanupRunner(sp.GetRequiredService<IFileRemover>(), sp.GetRequiredService<ICleanupJournal>(), null, sp.GetRequiredService<IRecycleBin>()));
        services.AddSingleton<DiscoveryService>();
        services.AddSingleton<InventoryModel>();
        // Real user-scope settings. The default composition uses the real HKCU; tests replace ISettingsService (and never resolve these) through configure.
        services.AddSingleton<ISettingOperationStore>(sp => sp.GetRequiredService<SqliteOperationStore>());
        services.AddSingleton<ISettingCatalog, SettingCatalog>();
        services.AddSingleton<IOsBuildProvider, WindowsOsBuildProvider>();
        services.AddSingleton<IRegistrySettingsAccess>(sp => new RegistrySettingsAccess(sp.GetRequiredService<ISettingCatalog>()));
        services.AddSingleton<ISettingsService>(sp => new SettingsService(sp.GetRequiredService<ISettingCatalog>(), sp.GetRequiredService<IRegistrySettingsAccess>(),
            sp.GetRequiredService<ISettingOperationStore>(), sp.GetRequiredService<IOsBuildProvider>(), sp.GetService<IShellRefresh>()));
        services.AddSingleton<IShellRefresh>(_ => new WindowsShellRefresh()); // fixed actions only, after a reviewed and confirmed apply
        services.AddSingleton<IWindowsSession, WindowsSession>();
        services.AddSingleton<SettingsModel>();
        services.AddSingleton<ErrorPresenter>();
        services.AddSingleton<SimulationModel>();
        services.AddSingleton<ReviewPromptHost>();
        services.AddSingleton<IReviewPrompt>(sp => sp.GetRequiredService<ReviewPromptHost>());
        services.AddSingleton<IDiagnosticsExporter>(sp => new DiagnosticsExporter(sp.GetRequiredService<SqliteOperationStore>(), sp.GetRequiredService<AppDataPaths>(), null,
            DiagnosticsJournals.For(sp.GetRequiredService<OptimizeService>(), sp.GetRequiredService<PowerChangeService>(), sp.GetRequiredService<NetworkService>(), sp.GetRequiredService<PermissionService>(),
                sp.GetRequiredService<StartupService>(), sp.GetRequiredService<ServiceManagerService>(), sp.GetRequiredService<ScheduledTaskService>(), sp.GetRequiredService<RepairService>(),
                sp.GetRequiredService<AppUninstallService>(), sp.GetRequiredService<BloatwareService>())));
        services.AddSingleton<IFolderPicker, Win32FolderPicker>();
        services.AddSingleton<IClipboardService, SystemClipboardService>();
        services.AddSingleton<ITextScaleSource, WindowsTextScaleSource>();
        services.AddSingleton(sp => new SettingsViewModel(sp.GetRequiredService<LocalizationService>(), sp.GetRequiredService<SimulationModel>(),
            sp.GetRequiredService<SqliteOperationStore>(), sp.GetRequiredService<ErrorPresenter>(), databasePath,
            sp.GetRequiredService<IDiagnosticsExporter>(), sp.GetRequiredService<IFolderPicker>(), sp.GetRequiredService<IClipboardService>(), sp.GetRequiredService<AppDataPaths>(), sp.GetRequiredService<IFileLocationOpener>()));
        services.AddSingleton(_ => new Winora.Application.Activity.ActivityService(new FileNotificationStore(notificationFolder ?? (() => paths.Root))));
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Test hosts set this before constructing App so no real window or database is opened.</summary>
    public static bool SkipStartup { get; set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SkipStartup) return;
        // Start phases in milliseconds from process start, written once to the log (ISS-3614).
        var processStart = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        long Since() => (long)(DateTime.UtcNow - processStart).TotalMilliseconds;
        var atOnStartup = Since();
        InstallSafetyNets();
        var text = new LocalizationService();
#if DEBUG
        if (Environment.GetEnvironmentVariable("WINORA_GALLERY") == "1") { Gallery.GalleryWindow.Open(text); return; }
#endif
        var startup = StartupArguments.Parse(e.Args);
        if (!ClaimInstance(startup)) { Shutdown(0); return; }
        var atInstance = Since();
        try
        {
            var legacy = ProtectedJournal.LegacyPath();
            var catalog = new SettingCatalog();
            var journal = JournalStartup.Prepare(ProcessElevationState.TokenIsElevated, legacy, static () => AdminOnlyFolder.PrepareRoot(), static () => ProtectedJournal.PathFor(),
                id => catalog.Find(id) is not null, DateTimeOffset.UtcNow);
            if (journal.DatabasePath is not { } path)
            {
                foreach (var note in journal.Notes) System.Diagnostics.Trace.TraceWarning(note);
                MessageBox.Show(text[journal.RefusalKey ?? "StartupFailed"], text["AppName"], MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            var atJournal = Since();
            provider = BuildServices(path, text, dataRoot: Path.GetDirectoryName(legacy), notificationFolder: static () => AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()));
            var log = provider.GetRequiredService<ILogger<App>>();
            foreach (var note in journal.Notes) log.LogWarning("{Note}", note);
            if (ProcessElevationState.TokenIsElevated)
                _ = Task.Run(() =>
                {
                    try { var sweep = AdminTempFolders.Sweep(DateTimeOffset.UtcNow); if (sweep.Removed + sweep.Kept > 0) log.LogInformation("Old temp folders: {Removed} removed, {Kept} kept", sweep.Removed, sweep.Kept); }
                    catch (Exception sweepError) when (sweepError is IOException or UnauthorizedAccessException) { log.LogWarning("The temp folders could not be checked"); }
                });
            // Changes that were begun and never finished (a crash, a lost journal write) are closed from the live state, so the history does not keep them as interrupted for good.
            _ = Task.Run(async () =>
            {
                try
                {
                    var closed = await provider.GetRequiredService<OptimizeService>().ReconcileInterruptedAsync().ConfigureAwait(false)
                        + await provider.GetRequiredService<PowerChangeService>().ReconcileInterruptedAsync().ConfigureAwait(false)
                        + await provider.GetRequiredService<PermissionService>().ReconcileInterruptedAsync().ConfigureAwait(false)
                        + await provider.GetRequiredService<StartupService>().ReconcileInterruptedAsync().ConfigureAwait(false)
                        + await provider.GetRequiredService<ServiceManagerService>().ReconcileInterruptedAsync().ConfigureAwait(false)
                        + await provider.GetRequiredService<ScheduledTaskService>().ReconcileInterruptedAsync().ConfigureAwait(false)
                        + await provider.GetRequiredService<NetworkService>().ReconcileInterruptedAsync().ConfigureAwait(false);
                    if (closed > 0) log.LogInformation("Interrupted changes closed from the live state: {Count}", closed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or WinoraException) { log.LogWarning("Interrupted changes could not be checked"); }
            });
            var atServices = Since();
            var window = provider.GetRequiredService<MainWindow>();
            var atWindow = Since();
            window.AskBeforeClosingWithPending = true;
            window.EnableShellIntegration(); // taskbar progress and badge, finish notice, notification area icon; fed only by the activity service
            // Every way the app ends leaves one line, so a window that goes away can be traced (ISS-3610).
            window.ExitReason += (_, reason) => log.LogInformation("The window closes: {Reason}", reason);
            SessionEnding += (_, e) => log.LogInformation("Windows session ends: {Reason}", e.ReasonSessionEnding);
            Exit += (_, e) => log.LogInformation("Winora exits with code {Code}", e.ApplicationExitCode);
            var load = provider.GetRequiredService<SimulationModel>().LoadAsync();
            window.Show();
            var atShown = Since();
            log.LogInformation("Start phases (ms from process start): app {App}, instance {Instance}, journal {Journal}, services {Services}, window {Window}, shown {Shown}",
                atOnStartup, atInstance, atJournal, atServices, atWindow, atShown);
            var shell = provider.GetRequiredService<ShellViewModel>();
            if (startup.Page is not null && !PageKeys.IsAllowed(startup.Page)) log.LogWarning("The page key '{Key}' of the command line is not on the list and was ignored", startup.Page);
            if (startup.Ignored > 0) log.LogWarning("{Count} command line argument(s) were ignored", startup.Ignored);
            if (startup.Page is not null && PageKeys.IsAllowed(startup.Page)) shell.Navigate(startup.Page);
            else shell.ActivateCurrentPage();
            instance?.Start(key => { Dispatcher.BeginInvoke(() => window.ShowFromHandOver(key)); return true; });
            JumpListService.Install(this, text, AppContext.BaseDirectory, Environment.ProcessPath ?? "", LogInstance);
            _ = provider.GetRequiredService<SettingsModel>().LoadAsync(); // read-only plus journal recovery; nothing is applied without review
            // Recovery and the first reads run off the UI thread; failures surface in the outcome bar.
            // First run: a welcome screen once. It is marked as seen when it closes; it can be reopened from Settings.
            if (provider.GetRequiredService<SettingsViewModel>().NeedsWelcome) _ = window.ShowWelcomeAsync();
            _ = PurgeQuarantineAsync(provider.GetRequiredService<LeftoverService>());
            await load;
            log.LogInformation("First data read {Data} ms from process start", Since());
        }
        catch (Exception ex)
        {
            // Nothing is usable yet, so a plain localized message is the only safe surface; raw exception text is never shown.
            System.Diagnostics.Trace.TraceError("Startup failed: {0}", ex);
            MessageBox.Show(text["StartupFailed"], text["AppName"], MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Makes this process the running instance, or hands the request to the one that runs and returns false (the caller ends the process). A pipe held by a process that is not
    /// this program does not stop Winora from starting: the start continues without the channel. Windows has already asked for consent when this runs (administrator manifest).
    /// </summary>
    private bool ClaimInstance(StartupArguments startup)
    {
        try
        {
            instance = InstanceServer.TryClaimCurrent(LogInstance);
            if (instance is not null) return true;
            var result = InstanceClient.Send(InstanceChannel.CurrentPipeName(), startup.Page, Environment.ProcessPath ?? "", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
            if (result is HandOverResult.Delivered or HandOverResult.Refused or HandOverResult.Failed) return false;
            System.Diagnostics.Trace.TraceWarning("Single instance: {0}; starting without the hand-over channel", result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("Single instance check failed: {0}", ex.GetType().Name); }
        return true;
    }

    private void LogInstance(string message)
    {
        if (provider?.GetService<ILogger<App>>() is { } log) log.LogWarning("{Message}", message);
        else System.Diagnostics.Trace.TraceWarning(message);
    }

    /// <summary>The folder of the quarantine: <c>%ProgramData%\Winora\quarantine</c>, beside the journals.</summary>
    public static string QuarantineFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Winora", "quarantine");

    /// <summary>Items set aside longer than <see cref="LeftoverService.RetentionDays"/> days are deleted for good once at start, off the UI thread. Each deletion is journaled.</summary>
    private static async Task PurgeQuarantineAsync(LeftoverService leftovers)
    {
        try { await Task.Run(leftovers.Purge); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("Quarantine purge skipped: {0}", ex.GetType().Name); }
    }

    /// <summary>Unhandled failures are logged once with a reference id and shown through the same presenter as every other error.</summary>
    private void InstallSafetyNets()
    {
        DispatcherUnhandledException += (_, args) => args.Handled = HandleUnhandled(args.Exception, "dispatcher");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => HandleUnhandled(args.ExceptionObject as Exception, "appdomain");
        TaskScheduler.UnobservedTaskException += (_, args) => { args.SetObserved(); HandleUnhandled(args.Exception, "task"); };
    }

    private bool HandleUnhandled(Exception? exception, string source)
    {
        var presenter = provider?.GetService<ErrorPresenter>();
        if (presenter is null) return false;
        presenter.ReportUnhandled(exception, source);
        return true;
    }

    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); provider?.Dispose(); base.OnExit(e); }
}