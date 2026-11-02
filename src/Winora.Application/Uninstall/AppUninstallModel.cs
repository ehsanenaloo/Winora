// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;
using Winora.Domain;

namespace Winora.Application;

/// <summary>How Winora removes a program: the quiet command of its registration, the program's own window, or the Windows package API.</summary>
public enum AppRemoval { Quiet, Interactive, Package }

/// <summary>How a removal ended. <see cref="StillRunning"/>: the wait reached its limit while the uninstaller still ran; Winora stopped waiting and did not end it, so the result is not known yet.</summary>
public enum AppRemovalState { Removed, PendingRestart, Failed, Refused, Cancelled, StillRunning }

/// <summary>Stable codes of a program removal. The window turns each into a plain sentence.</summary>
public static class AppCode
{
    public const string Ok = "ok";
    public const string Restart = "restart";
    public const string NotInstalled = "not_installed";
    public const string NeedsAdmin = "needs_admin";
    public const string InUse = "in_use";
    public const string Refused = "refused_by_program";
    public const string Cancelled = "cancelled";
    public const string Timeout = "timeout";
    public const string StillInstalled = "still_installed";
    public const string UninstallerMissing = "uninstaller_missing";
    public const string Untrusted = "untrusted_uninstaller";
    public const string Unsafe = "unsafe_uninstaller";
    public const string OutsideInstallFolder = "outside_install_folder";
    public const string JournalFailed = "journal_failed";
    /// <summary>The uninstaller of a program of the user scope was started as the user, but Winora could not follow it to its end.</summary>
    public const string StartedAsUser = "started_as_user";
    public const string Failed = "failed";
    /// <summary>The program is gone, but a step after it failed (for example the copy kept for new users could not be removed).</summary>
    public const string Partial = "partial";

    // Locks: Winora does not offer these programs for removal.
    public const string LockWindows = "windows_component";
    public const string LockNoRemove = "no_remove";
    public const string LockWinora = "winora";
    public const string LockRuntime = "runtime";
    public const string LockDriver = "driver";
    public const string LockNoUninstaller = "no_uninstaller";
    public const string LockProtected = "protected";
    public const string LockHasDependents = "has_dependents";
    public const string LockFramework = "framework";
    public const string LockUserScope = "user_scope";
}

/// <summary>
/// What a registered program says about its own removal, read fresh from its uninstall key. The commands are used only to remove the program; they are shown in the review as technical
/// details and only the program file is written to the record.
/// </summary>
public sealed record UninstallRegistration(string KeyName, string DisplayName, string? Publisher, string? InstallLocation, string? UninstallString, string? QuietUninstallString,
    bool WindowsInstaller, bool NoRemove, bool SystemComponent);

/// <summary>
/// What Winora will run. <see cref="MsiProductCode"/> is set for a Windows Installer product; otherwise <see cref="CommandLine"/> is the registered command. <see cref="Scope"/> and
/// <see cref="InstallLocation"/> come from the registration. The launcher runs a machine-scope plan as administrator, only from a trusted place, and a user-scope plan only as the
/// signed-in user through the desktop shell (technical-docs/uninstall.md).
/// </summary>
public sealed record UninstallPlan(AppRemoval Method, string? MsiProductCode, string? CommandLine, InstallationScope Scope = InstallationScope.User, string? InstallLocation = null)
{
    public bool IsMsi => MsiProductCode is not null;

    /// <summary>The program runs with the user's own rights, not as administrator.</summary>
    public bool RunsAsUser => Scope != InstallationScope.Machine;
}

/// <summary>Whether a program can be removed here. <see cref="LockCode"/> is set exactly when it cannot.</summary>
public sealed record AppAssessment(AppRemoval? Method, string? LockCode, UninstallPlan? Plan)
{
    public bool CanRemove => LockCode is null && Method is not null;
    public static AppAssessment Locked(string code) => new(null, code, null);
}

/// <summary>Reads the uninstall registration of an installed program. The port never writes.</summary>
public interface IUninstallRegistrations
{
    /// <summary>The current registration, or null when the key is gone or is not the one the application was discovered from.</summary>
    UninstallRegistration? Read(InstalledApplication app);
}

/// <summary>The outcome of starting the uninstaller. <see cref="ExitCode"/> is set when the program ran to its end; otherwise <see cref="Code"/> says why it did not.</summary>
public sealed record LaunchResult(int? ExitCode, string Code)
{
    public static LaunchResult Exited(int code) => new(code, AppCode.Ok);
    public static LaunchResult Fail(string code) => new(null, code);
}

/// <summary>Starts the removal command of a plan after its own safety checks. One implementation exists and one source file starts programs (see technical-docs/uninstall.md).</summary>
public interface IUninstallLauncher
{
    Task<LaunchResult> RunAsync(UninstallPlan plan, CancellationToken cancellationToken);

    /// <summary>The reason the plan will never run, from the rules that need no signature check (scope, install folder, path shape), or null. Nothing is started.</summary>
    string? Refusal(UninstallPlan plan);
}

public sealed record AppUninstallChange(Guid Id, string Name, string Version, string Publisher, InstallationKind Kind, InstallationScope Scope, AppRemoval Method,
    string? ProductCode, string? Executable, long? SizeKb, string Registration);

public sealed record AppUninstallEnd(AppRemovalState State, string Code, int? ExitCode, bool RestartNeeded);

public sealed record AppUninstallRecord(AppUninstallChange Change, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, AppUninstallEnd? End);

/// <summary>Append-only record of program removals. <see cref="Begin"/> must be durable before it returns.</summary>
public interface IAppUninstallJournal
{
    void Begin(AppUninstallChange change, DateTimeOffset atUtc);
    void Complete(Guid id, AppUninstallEnd end, DateTimeOffset atUtc);
    /// <summary>The newest first.</summary>
    IReadOnlyList<AppUninstallRecord> History(int max);
}

public sealed record AppRemovalItem(InstalledApplication App, AppAssessment Assessment, string? RefusalCode)
{
    public bool WillRun => RefusalCode is null;
}

public sealed record AppRemovalPreview(IReadOnlyList<AppRemovalItem> Items)
{
    public bool AnyRefused => Items.Any(i => !i.WillRun);
}

public sealed record AppRemovalProgress(int Index, int Total, string Name);

public sealed record AppRemovalResult(string Key, string Name, AppRemovalState State, string Code, bool RestartNeeded, long FreedKb);

public sealed record AppRemovalRun(IReadOnlyList<AppRemovalResult> Items, bool Stopped)
{
    public long FreedKb => Items.Where(i => i.State is AppRemovalState.Removed or AppRemovalState.PendingRestart).Sum(i => i.FreedKb);
}

/// <summary>Chooses the removal command of a registration and the programs that are never offered.</summary>
public static partial class AppUninstallPlanner
{
    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductCode();

    [GeneratedRegex(@"(?:^|[\\\s""])msiexec(?:\.exe)?""?\s+/[xXiI]\s*(\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiCommand();

    private static readonly Regex[] RuntimeNames =
    [
        new(@"^Microsoft Visual C\+\+", RegexOptions.IgnoreCase), new(@"^Microsoft \.NET\b", RegexOptions.IgnoreCase), new(@"^Microsoft ASP\.NET Core", RegexOptions.IgnoreCase),
        new(@"^Microsoft Windows Desktop Runtime", RegexOptions.IgnoreCase), new(@"^Microsoft Edge( WebView2.*| Update)?$", RegexOptions.IgnoreCase),
        new(@"^Microsoft Update Health Tools", RegexOptions.IgnoreCase), new(@"^Microsoft Windows Desktop Runtime", RegexOptions.IgnoreCase),
    ];
    private static readonly Regex DriverNames = new(@"\b(drivers?|chipset|firmware|management engine)\b", RegexOptions.IgnoreCase);
    private static readonly Regex WindowsNames = new(@"^Windows (Driver Package|Update|Subsystem|PC Health Check)\b", RegexOptions.IgnoreCase);
    // The product names and the publisher that Winora's own installers register (installer/Winora.Installer and installer/Winora.Machine.Installer, Package Name and Manufacturer).
    private static readonly string[] WinoraProducts = ["Winora", "Winora (all users)"];
    public const string WinoraPublisher = "Winora project";

    /// <summary>
    /// True for Winora's own installation only: a registration with one of the product names of Winora's installers and their publisher, or one whose install folder is the folder
    /// the app runs from. Another program whose name only starts with "Winora" is not Winora.
    /// </summary>
    public static bool IsWinora(string name, string? publisher, string? installLocation, string? appFolder)
    {
        if (WinoraProducts.Any(p => string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase)) && string.Equals(publisher?.Trim(), WinoraPublisher, StringComparison.OrdinalIgnoreCase)) return true;
        static string Folder(string path) => path.Trim().Trim('"').TrimEnd('\\', '/');
        return installLocation is { Length: > 0 } && appFolder is { Length: > 0 } && Folder(installLocation).Length > 3
            && string.Equals(Folder(installLocation), Folder(appFolder), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The reason Winora never offers this program, or null.</summary>
    public static string? Protection(string name, string? publisher, bool systemComponent, bool noRemove, string? installLocation, string windowsFolder, string? appFolder = null)
    {
        if (IsWinora(name, publisher, installLocation, appFolder)) return AppCode.LockWinora;
        if (systemComponent || WindowsNames.IsMatch(name)) return AppCode.LockWindows;
        if (installLocation is { Length: > 0 } && windowsFolder.Length > 0 && installLocation.TrimStart('"').StartsWith(windowsFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return AppCode.LockWindows;
        if (RuntimeNames.Any(r => r.IsMatch(name))) return AppCode.LockRuntime;
        if (DriverNames.IsMatch(name)) return AppCode.LockDriver;
        if (noRemove) return AppCode.LockNoRemove;
        return null;
    }

    /// <summary>
    /// The removal plan of a registration. Any program of the user can write a registration of the user scope, so its plan is marked to run as the user (<see cref="UninstallPlan.RunsAsUser"/>):
    /// the launcher starts it through the desktop shell with the user's own rights, as Windows Settings does, or keeps it locked when it cannot. A Windows Installer plan needs the flag WindowsInstaller=1 and a key name that is the product code itself; an msiexec command in any other registration is refused, so a key cannot name another product.
    /// </summary>
    /// <param name="appFolder">The folder the running app was started from; a registration that names it is Winora itself.</param>
    public static AppAssessment Assess(UninstallRegistration r, InstallationScope scope, string windowsFolder, string? appFolder = null)
    {
        if (Protection(r.DisplayName, r.Publisher, r.SystemComponent, r.NoRemove, r.InstallLocation, windowsFolder, appFolder) is { } lockCode) return AppAssessment.Locked(lockCode);
        if (r.WindowsInstaller && ProductCode().IsMatch(r.KeyName))
            return new(AppRemoval.Quiet, null, new(AppRemoval.Quiet, r.KeyName.ToUpperInvariant(), null, scope, r.InstallLocation));
        if (MsiCommand().IsMatch(r.UninstallString ?? "") || MsiCommand().IsMatch(r.QuietUninstallString ?? "")) return AppAssessment.Locked(AppCode.Unsafe);
        if (Clean(r.QuietUninstallString) is { } quiet) return new(AppRemoval.Quiet, null, new(AppRemoval.Quiet, null, quiet, scope, r.InstallLocation));
        // Inno Setup writes keys that end in _is1 and its uninstaller always understands these switches.
        if (Clean(r.UninstallString) is { } line && r.KeyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase))
            return new(AppRemoval.Quiet, null, new(AppRemoval.Quiet, null, line + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", scope, r.InstallLocation));
        if (Clean(r.UninstallString) is { } own) return new(AppRemoval.Interactive, null, new(AppRemoval.Interactive, null, own, scope, r.InstallLocation));
        return AppAssessment.Locked(AppCode.LockNoUninstaller);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) || text.Length > 2048 || text.Any(char.IsControl) ? null : text.Trim();

    /// <summary>The program file of a command, for the record: the text up to the first ".exe", with the user's own folder replaced by a token. Arguments are never recorded.</summary>
    public static string? ExecutableHint(UninstallPlan plan, string? profileFolder = null)
    {
        if (plan.IsMsi) return "msiexec.exe";
        var line = plan.CommandLine?.TrimStart('"');
        if (line is null) return null;
        var end = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : PersonalPaths.Redact(line[..(end + 4)], profileFolder ?? PersonalPaths.CurrentProfile());
    }

    /// <summary>What an exit code means. A zero or restart code still has to be confirmed by reading the registration back.</summary>
    public static (bool Success, bool Restart, string Code) Interpret(int exit, bool msi) => exit switch
    {
        0 => (true, false, AppCode.Ok),
        3010 or 1641 => (true, true, AppCode.Restart),
        1605 or 1614 when msi => (true, false, AppCode.NotInstalled),
        1602 => (false, false, AppCode.Cancelled),
        1618 => (false, false, AppCode.InUse),
        5 or 1925 => (false, false, AppCode.NeedsAdmin),
        740 => (false, false, AppCode.NeedsAdmin),
        _ => (false, false, AppCode.Refused),
    };
}
