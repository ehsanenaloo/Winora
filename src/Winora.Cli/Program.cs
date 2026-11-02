// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winora.Application;
using Winora.Cli;
using Winora.Domain;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.Windows;
using static Winora.Cli.CliText;

var noLog = CliLog.IsDisabled(args);
var machineTestHooks = MachineCommand.TestHooksPresent(args);   // read from the ORIGINAL arguments, before test flags are stripped
args = args.Where(a => a != CliLog.NoLogFlag).ToArray();
using var logFactory = CliLog.CreateFactory(noLog);
var cliLogger = logFactory.CreateLogger("Winora.Cli");
var commandName = args.Length > 0 && args[0] is "simulate" or "discover" or "settings" or "diagnostics" or "machine" ? args[0] : "other";
cliLogger.LogInformation("Command {Command} started", commandName);
var exitCode = await MainCoreAsync(args, cliLogger, machineTestHooks);
cliLogger.LogInformation("Command {Command} exited {ExitCode}", commandName, exitCode);
return exitCode;

static async Task<int> MainCoreAsync(string[] args, ILogger log, bool machineTestHooks)
{
    if (args.Length == 0 || args is ["--help"] or ["help"]) { Console.WriteLine(T("Help")); return 0; }
    if (args is ["--version"]) { Console.WriteLine("Winora " + VersionText.Current); return 0; }
    if (args[0] == "discover") return await RunDiscoverAsync(args.Skip(1).ToList());
    if (args[0] == "settings") return await RunSettingsAsync(args.Skip(1).ToList());
    if (args[0] == "diagnostics") return await RunDiagnosticsAsync(args.Skip(1).ToList());
    if (args[0] == "machine") return await RunMachineAsync(args.Skip(1).ToList(), machineTestHooks);
    if (args[0] != "simulate") { Console.Error.WriteLine(T("InvalidArguments")); return 2; }
    // The explicit DB location is local application data, never a Windows mutation target.
    var remaining = args.Skip(1).ToList();
    var json = remaining.Remove("--json");
    string? db = null;
    var dbIndex = remaining.IndexOf("--database");
    if (dbIndex >= 0)
    {
        if (dbIndex + 1 >= remaining.Count) { Console.Error.WriteLine(T("InvalidArguments")); return 2; }
        db = remaining[dbIndex + 1]; remaining.RemoveRange(dbIndex, 2);
    }
    string? dbRefusal = null;
    db ??= DefaultJournal(ProtectedJournal.LegacyPath(), out dbRefusal);
    if (db is null) { Console.Error.WriteLine(T(dbRefusal ?? "RecordsFolderUnsafe")); return 4; }
    if (remaining.Count != 1 || remaining[0] is not ("preview" or "apply" or "recover" or "history"))
    { Console.Error.WriteLine(T("InvalidArguments")); return 2; }
    try
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new SqliteOperationStore(db));
        services.AddSingleton<IOperationStore>(sp => sp.GetRequiredService<SqliteOperationStore>());
        services.AddSingleton<SimulationEngine>();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<SimulationEngine>();
        var store = provider.GetRequiredService<SqliteOperationStore>();
        var serializer = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        object result = remaining[0] switch
        {
            "preview" => engine.Plan(true),
            "apply" => engine.Apply(true),
            "recover" => engine.Recover(),
            _ => store.ListOperations()
        };
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { Simulation = true, SchemaVersion = 1, Result = result }, serializer));
        else
        {
            Console.WriteLine(T("Simulation"));
            if (result is SimulationPlan plan) Console.WriteLine(string.Format(CultureInfo.CurrentCulture, T("Preview"), plan.Current, plan.Desired));
            else if (result is OperationRecord operation) Render(operation);
            else foreach (var operationRecord in (IReadOnlyList<OperationRecord>)result) Render(operationRecord);
        }
        return result is OperationRecord { State: OperationState.Failed or OperationState.Partial or OperationState.Unsupported }
            || result is IReadOnlyList<OperationRecord> outcomes && outcomes.Any(r => r.State is OperationState.Failed or OperationState.Partial or OperationState.Unsupported) ? 1 : 0;
        void Render(OperationRecord record) => Console.WriteLine(string.Format(CultureInfo.CurrentCulture, T("Record"), record.Id, T("Outcome" + record.State), record.Code));
    }
    catch (Exception ex)
    {
        // Failures keep the localized summary and add the stable code plus its category exit code; raw exception text is never printed.
        var error = new ErrorMapper().Map(ex);
        log.LogError(ex, "Command simulate failed: {Code} {CorrelationId}", error.Code, error.CorrelationId);
        Console.Error.WriteLine(T("Failed"));
        Console.Error.WriteLine(F("DiscoverError", T(error.UserMessageKey), error.Code) + (error.CorrelationId is null ? "" : " " + F("DiscoverReference", error.CorrelationId)));
        return error.ExitCode;
    }
}

static async Task<int> RunDiagnosticsAsync(List<string> diagnosticsArgs)
{
    if (!DiagnosticsCommand.TryParse(diagnosticsArgs, out var options))
    {
        Console.Error.WriteLine(T("InvalidArguments"));
        Console.Error.WriteLine(T("DiagnosticsUsage"));
        return 2;
    }
    Console.OutputEncoding = new UTF8Encoding(false);
    var paths = CliLog.Paths();
    string? refusal = null;
    var database = options.Database ?? DefaultJournal(Path.Combine(paths.Root, ProtectedJournal.LegacyFileName), out refusal);
    if (database is null) { Console.Error.WriteLine(T(refusal ?? "RecordsFolderUnsafe")); return 4; }
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try { return await DiagnosticsCommand.RunAsync(options, () => new DiagnosticsExporter(new SqliteOperationStore(database), paths), paths.LogsFolder, Directory.GetCurrentDirectory(), Console.Out, Console.Error, cts.Token); }
    finally { Console.CancelKeyPress -= onCancel; }
}

static async Task<int> RunDiscoverAsync(List<string> discoverArgs)
{
    if (!DiscoverCommand.TryParse(discoverArgs, out var options))
    {
        Console.Error.WriteLine(T("InvalidArguments"));
        Console.Error.WriteLine(T("DiscoverUsage"));
        return 2;
    }
    // UTF-8 without BOM so JSON and Persian text survive redirection.
    var utf8 = new UTF8Encoding(false);
    Console.OutputEncoding = utf8;
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IDiscoverySource<StartupItem>, StartupDiscovery>();
    services.AddSingleton<IDiscoverySource<ScheduledTaskInfo>, ScheduledTaskDiscovery>();
    services.AddSingleton<IDiscoverySource<ServiceInfo>, ServiceDiscovery>();
    services.AddSingleton<IDiscoverySource<InstalledApplication>, InstalledApplicationDiscovery>();
    services.AddSingleton<DiscoveryService>();
    using var provider = services.BuildServiceProvider();
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try { return await DiscoverCommand.RunAsync(options, provider.GetRequiredService<DiscoveryService>(), Console.Out, Console.Error, cts.Token); }
    finally { Console.CancelKeyPress -= onCancel; }
}

static async Task<int> RunSettingsAsync(List<string> settingsArgs)
{
    // Hidden, test-only: --sandbox-root <name> redirects registry access to HKCU\Software\WinoraTests\<name>. Invalid names are refused outright.
    var sandboxAt = settingsArgs.IndexOf("--sandbox-root");
    if (sandboxAt >= 0 && (sandboxAt + 1 >= settingsArgs.Count || !SettingsCommand.IsValidSandboxName(settingsArgs[sandboxAt + 1])))
    {
        Console.Error.WriteLine(T("SettingsSandboxInvalid"));
        return 2;
    }
    if (!SettingsCommand.TryParse(settingsArgs, out var options))
    {
        Console.Error.WriteLine(T("InvalidArguments"));
        Console.Error.WriteLine(T("SettingsUsage"));
        return 2;
    }
    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine(F("DiscoverError", T(ErrorCatalog.ResourceKey(ErrorCode.DiscoveryUnavailable)), ErrorCode.DiscoveryUnavailable)); return 4; }
    var utf8 = new UTF8Encoding(false);
    Console.OutputEncoding = utf8;
    string? refusal = null;
    var database = options.Database ?? DefaultJournal(ProtectedJournal.LegacyPath(), out refusal);
    if (database is null) { Console.Error.WriteLine(T(refusal ?? "RecordsFolderUnsafe")); return 4; }
    var catalog = new SettingCatalog();
    var sandbox = SettingsCommand.SandboxRoot(options);
    var registry = new RegistrySettingsAccess(catalog, sandbox);
    // The sandbox never touches the real shell: a no-op refresh that always reports success is used instead of any real broadcast or restart.
    IShellRefresh shell = sandbox is null ? new WindowsShellRefresh() : new SandboxShellRefresh();
    var service = new SettingsService(catalog, registry, new SqliteOperationStore(database), new WindowsOsBuildProvider(), shell);
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try { return await SettingsCommand.RunAsync(options, service, !Console.IsInputRedirected, Console.In, Console.Out, Console.Error, cts.Token); }
    finally { Console.CancelKeyPress -= onCancel; }
}

static async Task<int> RunMachineAsync(List<string> machineArgs, bool testHooks)
{
    Console.OutputEncoding = new UTF8Encoding(false);
    // Interactive only: stdin and stdout must both be consoles (technical-docs/protocol.md 3.2). The production composition takes no path, root or database option.
    var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.CancelKeyPress += onCancel;
    if (MachineChangeCommand.IsChangeVerb(machineArgs.FirstOrDefault()))
    {
        try { return await RunMachineChangeAsync(machineArgs, testHooks, interactive, cts.Token); }
        finally { Console.CancelKeyPress -= onCancel; }
    }
    try
    {
        return await MachineCommand.RunAsync(machineArgs, testHooks, interactive,
            confirmation => Winora.Infrastructure.Windows.Elevation.ElevationProduction.CreateSelfTest(confirmation, testHooks), Console.In, Console.Out, Console.Error, cts.Token);
    }
    finally { Console.CancelKeyPress -= onCancel; }
}

static async Task<int> RunMachineChangeAsync(List<string> machineArgs, bool testHooks, bool interactive, CancellationToken ct)
{
    // The change verbs take no path, root, database, log or shell-refresh option: the journal is always the default one.
    // No journal is opened and nothing is composed when the command will refuse anyway (test convenience, other platform, non-interactive apply or revert).
    var refuseEarly = testHooks || !OperatingSystem.IsWindows() || (!interactive && machineArgs[0] is "apply" or "revert");
    if (refuseEarly)
        return await MachineChangeCommand.RunAsync(machineArgs, testHooks, interactive, new UnavailableExecutor(), static () => [], static () => new MachinePreflight(false, Winora.Domain.ErrorCode.ElevationPlatformUnsupported, false),
            Console.In, Console.Out, Console.Error, ct);
    if (DefaultJournal(ProtectedJournal.LegacyPath(), out var refusal) is not { } database) { Console.Error.WriteLine(T(refusal!)); return 4; }
    var store = new SqliteOperationStore(database);
    var composition = Winora.Infrastructure.Windows.Elevation.MachineProduction.Create(store, testHooks);
    return await MachineChangeCommand.RunAsync(machineArgs, testHooks, interactive, composition.Executor, store.ListMachineOperations,
        () => { var g = composition.Preflight.Evaluate(); return new MachinePreflight(g.Passed, g.Code, g.NoConsentPrompt); }, Console.In, Console.Out, Console.Error, ct);
}

// An elevated process keeps the operation journal in the admin-only folder (ISS-2713), like the desktop app, and never reads the user-writable file then. A process that is not
// elevated keeps the file in the user's data folder: it acts with the user's own rights. Null with a text key when the protected folder is refused.
static string? DefaultJournal(string legacy, out string? refusal)
{
    refusal = null;
    if (!OperatingSystem.IsWindows() || !ProcessElevationState.TokenIsElevated) return legacy;
    var root = AdminOnlyFolder.PrepareRoot();
    if (!root.Usable) { refusal = root.ParentProblem ? "RecordsFolderParentUnsafe" : "RecordsFolderUnsafe"; return null; }
    try
    {
        var path = ProtectedJournal.PathFor();
        var catalog = new SettingCatalog();
        SqliteOperationStore.MoveLegacy(legacy, path, id => catalog.Find(id) is not null);
        return path;
    }
    catch (AdminOnlyFolderException e) { refusal = e.Problem is AdminOnlyProblem.ParentLink or AdminOnlyProblem.ParentOwner or AdminOnlyProblem.ParentRights ? "RecordsFolderParentUnsafe" : "RecordsFolderUnsafe"; return null; }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or StoreException) { refusal = "RecordsFolderUnsafe"; return null; }
}
