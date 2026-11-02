// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Winora.Application;
using Winora.Domain;

namespace Winora.Infrastructure.Windows;

/// <summary>One started uninstaller. The seam lets tests run a plan without starting any program.</summary>
internal interface IUninstallProcess : IDisposable
{
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
}

internal interface IUninstallProcessStarter
{
    /// <exception cref="Win32Exception">The program could not be started.</exception>
    IUninstallProcess Start(string file, string arguments, string workingDirectory, bool showWindow, IReadOnlyDictionary<string, string> environment);
}

/// <summary>The desktop shell of the signed-in user, which starts a program with that user's own rights (ISS-3201). The seam lets tests run a plan without the real shell.</summary>
internal interface IUserShell
{
    /// <summary>True when the desktop shell runs as the same account as this process and is not elevated, so a program it starts gets the user's own rights only.</summary>
    bool Available();

    /// <summary>Starts the program through the shell and returns it when it can be found; null when it started but cannot be followed.</summary>
    /// <exception cref="COMException">The shell refused or is gone.</exception>
    /// <exception cref="InvalidOperationException">The shell is not the one of this account or is elevated.</exception>
    Task<IUninstallProcess?> StartAsync(string file, string arguments, string workingDirectory, bool showWindow, CancellationToken cancellationToken);
}

/// <summary>
/// The only place where Winora starts a program that is not on a closed list (decision DEC-IW-240, owner rule in technical-docs/uninstall.md). It runs the removal command that a program
/// registered for itself, and nothing else, after these checks: the command comes from a plan the service built from a registration it read just before, and the registration is of the
/// machine scope (any program of the user can write a user-scope key, so its command never runs with administrator rights). A Windows Installer product runs
/// as <c>msiexec.exe /x {code} /qn /norestart</c> from the System32 folder with a code that passed the GUID pattern and that Windows Installer reports as installed for all users. Any other
/// command is allowed only by place: it must name an existing <c>.exe</c> file inside the install folder of its own registration (not a drive root and not the Windows folder), or inside the
/// Package Cache folder of Windows Installer bundles with a valid signature, or, for a registration with no InstallLocation, inside a folder below Program Files whose whole chain is admin-only (see <see cref="Place"/>). The file must also be named by a full local path (no UNC or device path, no link, no <c>..</c>), must not be a
/// command interpreter, script host or loader, must not name an alternate data stream, and must be trusted: the file itself, its folder and every folder above it can be changed only by
/// administrators (checked on the open file for the file), or Windows confirms its signature. Variables in the command are expanded with machine values only. There is no shell, and
/// the program gets the fixed environment of <see cref="ChildEnvironment"/> (TEMP in a new admin-only folder), not the app's own. The program is never ended by Winora: a wait that times out or is cancelled stops waiting and leaves the uninstaller running, because ending it half way can leave
/// a program broken. Only one process exists per call. A source scan covers this file; do not add another place that starts a program.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class RegisteredUninstallLauncher : IUninstallLauncher
{
    public static readonly TimeSpan QuietLimit = WaitLimits.Of(WaitKind.QuietUninstall).Normal;
    public static readonly TimeSpan InteractiveLimit = WaitLimits.Of(WaitKind.InteractiveUninstall).Normal;
    private const int MaxArguments = 2048;

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductCode();

    [GeneratedRegex(@"~\d", RegexOptions.CultureInvariant)]
    private static partial Regex ShortName();

    private bool InsideWindowsFolder(string full)
    {
        var tools = system32.TrimEnd('\\');
        var wow = Path.Combine(Path.GetDirectoryName(tools) ?? "", "SysWOW64");
        return full.StartsWith(tools + "\\", StringComparison.OrdinalIgnoreCase) || full.StartsWith(wow + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe", "msiexec.exe", "wmic.exe", "bash.exe", "wsl.exe",
        "sh.exe", "python.exe", "pythonw.exe", "node.exe", "java.exe", "javaw.exe", "forfiles.exe", "schtasks.exe", "reg.exe", "sc.exe", "net.exe", "certutil.exe", "bitsadmin.exe", "explorer.exe",
        "conhost.exe", "pcalua.exe", "ftp.exe", "msbuild.exe", "installutil.exe", "regasm.exe", "regsvcs.exe", "cmstp.exe", "odbcconf.exe", "mmc.exe", "control.exe", "hh.exe", "infdefaultinstall.exe",
        "powershell_ise.exe", "wuauclt.exe", "bash.exe", "ssh.exe", "scp.exe", "curl.exe", "tar.exe", "expand.exe", "extrac32.exe", "makecab.exe", "esentutl.exe", "replace.exe", "xcopy.exe", "robocopy.exe",
        "attrib.exe", "takeown.exe", "icacls.exe", "cacls.exe", "runas.exe", "at.exe", "taskkill.exe", "tasklist.exe", "whoami.exe", "dllhost.exe", "svchost.exe", "wmiprvse.exe", "msdt.exe", "mavinject.exe",
    };

    private readonly string system32;
    private readonly IFileSignatureChecker signatures;
    private readonly Func<string, bool> adminOnlyFolder;
    private readonly Func<string, FileStream?, bool> adminOnlyFile;
    private readonly Func<string, bool> isLocalFixedDrive;
    private readonly IUninstallProcessStarter starter;
    private readonly Func<string, bool> machineProduct;
    private readonly string packageCache;
    private readonly Func<string, string> expand;
    private readonly Func<IReadOnlyDictionary<string, string>?> environment;
    private readonly Func<TimeSpan> quietLimit, interactiveLimit;
    private readonly IReadOnlyList<string> programFiles;
    private readonly IUserShell userShell;

    /// <param name="slowToolWait">The Settings choice "Wait for slow tools", read at the start of each removal. Null is Normal.</param>
    public RegisteredUninstallLauncher(IFileSignatureChecker signatures, Func<SlowToolWait>? slowToolWait = null)
        : this(Environment.GetFolderPath(Environment.SpecialFolder.System), signatures, static folder => AdminOnlyFolder.IsProtected(folder), static (file, open) => AdminOnlyFolder.IsProtectedFile(file, open), IsFixedDrive, new ProcessStarter(),
            () => WaitLimits.For(WaitKind.QuietUninstall, slowToolWait?.Invoke() ?? SlowToolWait.Normal), () => WaitLimits.For(WaitKind.InteractiveUninstall, slowToolWait?.Invoke() ?? SlowToolWait.Normal),
            MsiProducts.InstalledPerMachine, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
            static text => SystemVariables.Machine().Expand(text), ChildEnvironment.ForChild, ProgramFilesRoots(), new DesktopShell()) { }

    private static IReadOnlyList<string> ProgramFilesRoots() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }.Select(Environment.GetFolderPath).Where(p => p.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal RegisteredUninstallLauncher(string system32, IFileSignatureChecker signatures, Func<string, bool> adminOnlyFolder, Func<string, FileStream?, bool> adminOnlyFile,
        Func<string, bool> isLocalFixedDrive, IUninstallProcessStarter starter,
        TimeSpan quietLimit, TimeSpan interactiveLimit, Func<string, bool> machineProduct, string packageCache, Func<string, string> expand, Func<IReadOnlyDictionary<string, string>?> environment,
        IReadOnlyList<string>? programFiles = null, IUserShell? userShell = null)
        : this(system32, signatures, adminOnlyFolder, adminOnlyFile, isLocalFixedDrive, starter, () => quietLimit, () => interactiveLimit, machineProduct, packageCache, expand, environment, programFiles, userShell) { }

    private RegisteredUninstallLauncher(string system32, IFileSignatureChecker signatures, Func<string, bool> adminOnlyFolder, Func<string, FileStream?, bool> adminOnlyFile,
        Func<string, bool> isLocalFixedDrive, IUninstallProcessStarter starter,
        Func<TimeSpan> quietLimit, Func<TimeSpan> interactiveLimit, Func<string, bool> machineProduct, string packageCache, Func<string, string> expand, Func<IReadOnlyDictionary<string, string>?> environment,
        IReadOnlyList<string>? programFiles, IUserShell? userShell)
    {
        this.system32 = system32; this.signatures = signatures; this.adminOnlyFolder = adminOnlyFolder; this.adminOnlyFile = adminOnlyFile; this.isLocalFixedDrive = isLocalFixedDrive; this.starter = starter;
        this.quietLimit = quietLimit; this.interactiveLimit = interactiveLimit; this.machineProduct = machineProduct; this.packageCache = packageCache;
        this.expand = expand; this.environment = environment;
        this.programFiles = [.. (programFiles ?? []).Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))];
        this.userShell = userShell ?? NoUserShell.Instance;
    }

    private string WindowsFolder => Path.GetDirectoryName(system32.TrimEnd('\\')) ?? "";

    public string? Refusal(UninstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Scope != InstallationScope.Machine) return UserRefusal(plan, out _, out _);
        if (plan.MsiProductCode is { } code) return ProductCode().IsMatch(code) ? null : AppCode.Unsafe;
        if (plan.CommandLine is not { } line || !TryParse(line, File.Exists, out var file, out var arguments, expand)) return AppCode.UninstallerMissing;
        return Shape(file, arguments) ?? Place(file, plan.InstallLocation, out _);
    }

    private static bool IsFixedDrive(string root)
    {
        try { return new DriveInfo(root).DriveType == DriveType.Fixed; }
        catch (ArgumentException) { return false; }
    }

    public async Task<LaunchResult> RunAsync(UninstallPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string file, arguments;
        FileStream? hold = null;
        if (plan.Scope != InstallationScope.Machine) return await RunAsUserAsync(plan, cancellationToken).ConfigureAwait(false);
        if (plan.MsiProductCode is { } code)
        {
            if (!ProductCode().IsMatch(code)) return LaunchResult.Fail(AppCode.Unsafe);
            // msiexec /x removes any product it is given, so the code must be a product that is installed for all users, as the machine registration it came from says.
            code = code.ToUpperInvariant();
            if (!machineProduct(code)) return LaunchResult.Fail(AppCode.Unsafe);
            file = Path.Combine(system32, "msiexec.exe");
            if (!File.Exists(file)) return LaunchResult.Fail(AppCode.UninstallerMissing);
            arguments = "/x " + code + " /qn /norestart";
        }
        else
        {
            if (plan.CommandLine is not { } line) return LaunchResult.Fail(AppCode.UninstallerMissing);
            if (!TryParse(line, File.Exists, out file, out arguments, expand)) return LaunchResult.Fail(AppCode.UninstallerMissing);
            if (Shape(file, arguments) is { } unsafeShape) return LaunchResult.Fail(unsafeShape);
            if (Place(file, plan.InstallLocation, out var rule) is { } outside) return LaunchResult.Fail(outside);
            if (Trust(file, rule, null, cancellationToken) is { } first) return LaunchResult.Fail(first);
            // The file is then held open for reading, with writes and deletes denied, until the program has started, and checked again: it cannot be swapped between the check and the start.
            try { hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return LaunchResult.Fail(AppCode.Failed); }
            if (Trust(file, rule, hold, cancellationToken) is { } refusal) { hold.Dispose(); return LaunchResult.Fail(refusal); }
        }
        var interactive = plan.Method == AppRemoval.Interactive;
        if (environment() is not { } variables) { hold?.Dispose(); return LaunchResult.Fail(AppCode.Failed); }
        IUninstallProcess process;
        try { process = starter.Start(file, arguments, Path.GetDirectoryName(file) ?? system32, interactive, variables); }
        catch (Win32Exception e) { return LaunchResult.Fail(NativeCode(e.NativeErrorCode)); }
        catch (InvalidOperationException) { return LaunchResult.Fail(AppCode.Failed); }
        finally { hold?.Dispose(); }
        using (process)
        {
            using var timeout = new CancellationTokenSource(interactive ? interactiveLimit() : quietLimit());
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try { return LaunchResult.Exited(await process.WaitForExitAsync(linked.Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { return LaunchResult.Fail(cancellationToken.IsCancellationRequested ? AppCode.Cancelled : AppCode.Timeout); }
        }
    }

    /// <summary>
    /// The reason a plan of the user scope does not run, or null with the program and its arguments. Any program of the user can write such a registration, so it runs only through the
    /// desktop shell of the same account, never with this process's token: it then has the user's own rights, as when Windows Settings removes it. The place and trust rules of the machine
    /// scope are not needed for that, but the command must still name a real program file on a local drive, and no command interpreter or loader.
    /// </summary>
    private string? UserRefusal(UninstallPlan plan, out string file, out string arguments)
    {
        file = arguments = "";
        if (!userShell.Available()) return AppCode.LockUserScope;
        if (plan.MsiProductCode is { } code)
        {
            if (!ProductCode().IsMatch(code)) return AppCode.Unsafe;
            file = Path.Combine(system32, "msiexec.exe");
            arguments = "/x " + code.ToUpperInvariant() + (plan.Method == AppRemoval.Interactive ? "" : " /qn /norestart");
            return File.Exists(file) ? null : AppCode.UninstallerMissing;
        }
        if (plan.CommandLine is not { } line || !TryParse(line, File.Exists, out file, out arguments, expand)) return AppCode.UninstallerMissing;
        return Shape(file, arguments) ?? (File.Exists(file) ? null : AppCode.UninstallerMissing);
    }

    private async Task<LaunchResult> RunAsUserAsync(UninstallPlan plan, CancellationToken cancellationToken)
    {
        if (UserRefusal(plan, out var file, out var arguments) is { } refusal) return LaunchResult.Fail(refusal);
        var interactive = plan.Method == AppRemoval.Interactive;
        IUninstallProcess? process;
        try { process = await userShell.StartAsync(file, arguments, Path.GetDirectoryName(file) ?? system32, interactive, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return LaunchResult.Fail(AppCode.StartedAsUser); }
        catch (Exception e) when (e is COMException or InvalidOperationException or InvalidCastException or UnauthorizedAccessException or Win32Exception) { return LaunchResult.Fail(AppCode.LockUserScope); }
        if (process is null) return LaunchResult.Fail(AppCode.StartedAsUser);
        using (process)
        {
            using var timeout = new CancellationTokenSource(interactive ? interactiveLimit() : quietLimit());
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try { return LaunchResult.Exited(await process.WaitForExitAsync(linked.Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { return LaunchResult.Fail(cancellationToken.IsCancellationRequested ? AppCode.Cancelled : AppCode.Timeout); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return LaunchResult.Fail(AppCode.StartedAsUser); }
        }
    }

    private static string NativeCode(int error) => error switch
    {
        740 or 5 => AppCode.NeedsAdmin,
        1223 => AppCode.Cancelled,
        2 or 3 => AppCode.UninstallerMissing,
        _ => AppCode.Failed,
    };

    /// <summary>
    /// Splits a registered command into the program file and its arguments. A path with spaces that is not in quotes is resolved against the files that exist. Variables are expanded
    /// with <paramref name="expand"/>, machine values only by default.
    /// </summary>
    internal static bool TryParse(string commandLine, Func<string, bool> fileExists, out string file, out string arguments, Func<string, string>? expand = null)
    {
        file = arguments = "";
        var line = (expand ?? SystemVariables.Machine().Expand)(commandLine).Trim();
        if (line.Length == 0 || line.Length > MaxArguments + 260) return false;
        if (line[0] == '"')
        {
            var close = line.IndexOf('"', 1);
            if (close < 2) return false;
            file = line[1..close]; arguments = line[(close + 1)..].Trim();
            return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }
        for (var i = 0; i <= line.Length; i++)
        {
            if (i < line.Length && line[i] != ' ') continue;
            var candidate = line[..i];
            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !fileExists(candidate)) continue;
            file = candidate; arguments = line[i..].Trim();
            return true;
        }
        return false;
    }

    /// <summary>The reason the file name or the arguments may not run, or null. Text and drive checks only.</summary>
    internal string? Shape(string file, string arguments)
    {
        if (arguments.Length > MaxArguments || arguments.Any(char.IsControl)) return AppCode.Unsafe;
        if (!Path.IsPathFullyQualified(file) || file.StartsWith(@"\\", StringComparison.Ordinal) || file.IndexOf(':') != 1 || file.IndexOf(':', 2) >= 0) return AppCode.Unsafe; // a later colon names a stream
        string full;
        try { full = Path.GetFullPath(file); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return AppCode.Unsafe; }
        if (!string.Equals(full, file, StringComparison.OrdinalIgnoreCase)) return AppCode.Unsafe;
        var name = Path.GetFileName(full);
        if (Interpreters.Contains(name) || ShortName().IsMatch(name)) return AppCode.Unsafe; // an 8.3 alias would hide a name from the list
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || !isLocalFixedDrive(root)) return AppCode.Unsafe;
        return null;
    }

    /// <summary>
    /// The allow rule by place: the file is inside the install folder of its registration, or inside the Package Cache folder (then a valid signature is required too). A registration
    /// with no InstallLocation at all (ISS-3200) may still run a file in a folder below <c>%ProgramFiles%</c> or <c>%ProgramFiles(x86)%</c>, but only when that root and every folder
    /// from it down to the file can be changed by SYSTEM, Administrators or TrustedInstaller only (owner and every write right, read from the ACL), and the file itself is not
    /// writable by anyone else. There is no signature way around that rule. Anything else, a System32 tool included, is refused whatever its signature.
    /// </summary>
    internal string? Place(string file, string? installLocation, out PlaceRule rule)
    {
        rule = PlaceRule.InstallFolder;
        var cache = Path.TrimEndingDirectorySeparator(packageCache);
        if (cache.Length > 3 && file.StartsWith(cache + "\\", StringComparison.OrdinalIgnoreCase)) { rule = PlaceRule.PackageCache; return null; }
        if (InstallFolder(installLocation) is { } folder) return file.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) ? null : AppCode.OutsideInstallFolder;
        if (!string.IsNullOrWhiteSpace(installLocation) || ProgramFilesRoot(file) is not { } root) return AppCode.OutsideInstallFolder;
        rule = PlaceRule.ProgramFiles;
        return ProtectedChain(root, file) ? null : AppCode.OutsideInstallFolder;
    }

    /// <summary>The Program Files root that holds the file in a folder of its own (not directly in the root), or null.</summary>
    private string? ProgramFilesRoot(string file)
    {
        var folder = Path.GetDirectoryName(file) ?? "";
        return programFiles.FirstOrDefault(root => folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The root, each folder below it down to the folder of the file, and the file: no link, and only trusted principals can change them.</summary>
    private bool ProtectedChain(string root, string file)
    {
        var folder = Path.GetDirectoryName(file)!;
        for (var current = folder; ; current = Path.GetDirectoryName(current)!)
        {
            try { if (new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint) || !adminOnlyFolder(current)) return false; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            if (current.Length <= root.Length) return false;
        }
        return adminOnlyFile(file, null);
    }

    /// <summary>The install folder of a registration as a full local path, or null when it is missing, a drive root, the Windows folder, inside it or above it.</summary>
    private string? InstallFolder(string? location)
    {
        var text = location?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(text) || !Path.IsPathFullyQualified(text) || text.StartsWith(@"\\", StringComparison.Ordinal) || text.IndexOf(':') != 1 || text.IndexOf(':', 2) >= 0 || text.Any(char.IsControl)) return null;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), full, StringComparison.OrdinalIgnoreCase)) return null;
        var windows = Path.TrimEndingDirectorySeparator(WindowsFolder);
        if (windows.Length > 0 && (string.Equals(windows, full, StringComparison.OrdinalIgnoreCase) || windows.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))) return null;
        return full;
    }

    /// <summary>
    /// The reason the file is not trusted, or null: no link, the file, its folder and the folders above it can be changed only by administrators, and (for the package cache, System32 and SysWOW64) a valid signature. With <paramref name="open"/> the file's own security is read from that open handle.
    /// </summary>
    internal string? Trust(string full, PlaceRule rule, FileStream? open, CancellationToken cancellationToken)
    {
        var signatureRequired = rule == PlaceRule.PackageCache;
        FileAttributes attributes;
        try { attributes = File.GetAttributes(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return AppCode.UninstallerMissing; }
        var folder = Path.GetDirectoryName(full)!;
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) || new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) return AppCode.Unsafe;
        var adminOnly = adminOnlyFolder(folder) && adminOnlyFile(full, open);
        if (rule == PlaceRule.ProgramFiles) return adminOnly && !InsideWindowsFolder(full) ? null : AppCode.OutsideInstallFolder;
        // The uninstaller runs with administrator rights and loads files from its own folder: a folder others can change is refused, signed or not.
        if (!adminOnly) return AppCode.Untrusted;
        // A folder only administrators can change is trusted for programs that were installed there, but the System32 and SysWOW64 folders hold many tools that run other programs: those need a valid signature too.
        if (!signatureRequired && adminOnly && !InsideWindowsFolder(full)) return null;
        return signatures.Check(full, cancellationToken).State == SignatureState.Valid ? null : AppCode.Untrusted;
    }

    private sealed class ProcessStarter : IUninstallProcessStarter
    {
        public IUninstallProcess Start(string file, string arguments, string workingDirectory, bool showWindow, IReadOnlyDictionary<string, string> environment)
        {
            var info = new ProcessStartInfo(file) { Arguments = arguments, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = !showWindow };
            ChildEnvironment.Apply(info.Environment, environment);
            return new Running(Process.Start(info) ?? throw new InvalidOperationException("The program did not start."));
        }

        private sealed class Running(Process process) : IUninstallProcess
        {
            public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return process.ExitCode;
            }

            public void Dispose() => process.Dispose();
        }
    }
}

/// <summary>Which place rule allowed a registered uninstaller.</summary>
internal enum PlaceRule { InstallFolder, PackageCache, ProgramFiles }

/// <summary>No desktop shell: every plan of the user scope keeps its lock.</summary>
internal sealed class NoUserShell : IUserShell
{
    public static readonly NoUserShell Instance = new();
    public bool Available() => false;
    public Task<IUninstallProcess?> StartAsync(string file, string arguments, string workingDirectory, bool showWindow, CancellationToken cancellationToken) => throw new InvalidOperationException("No desktop shell.");
}

/// <summary>
/// Starts a program as the signed-in user through the desktop window of File Explorer, the documented way for an elevated program to start one that is not elevated: the ShellWindows
/// object finds the desktop folder view, and its Application object (IShellDispatch2) runs ShellExecute inside explorer.exe, so the new program gets Explorer's token and environment.
/// The shell is used only when its process is explorer.exe of the Windows folder, runs as the same account as this process and is not elevated. Otherwise the program would get rights
/// the registration was never meant to have (an elevated Explorer), or would run as another account (after an over-the-shoulder elevation). ShellExecute returns no process, so the
/// new program is looked for by its full image path, its account and its start time, and followed when found.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopShell : IUserShell
{
    private const int CsidlDesktop = 0, SwcDesktop = 8, SwfoNeedDispatch = 1, ShowNormal = 1, Hide = 0;
    private const uint SvgioBackground = 0;
    private static readonly TimeSpan FindFor = TimeSpan.FromSeconds(10);

    public bool Available() => TrustedShell(GetShellWindow());

    public async Task<IUninstallProcess?> StartAsync(string file, string arguments, string workingDirectory, bool showWindow, CancellationToken cancellationToken)
    {
        SecurityIdentifier user;
        using (var identity = WindowsIdentity.GetCurrent()) user = identity.User ?? throw new InvalidOperationException("The token has no user.");
        var since = DateTime.UtcNow.AddSeconds(-2);
        dynamic shell = Dispatch();
        shell.ShellExecute(file, arguments, workingDirectory, "", showWindow ? ShowNormal : Hide);
        for (var waited = TimeSpan.Zero; waited < FindFor; waited += TimeSpan.FromMilliseconds(250))
        {
            if (Find(file, since, user) is { } found) return new Followed(found);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>The Application object (IShellDispatch2) of the desktop folder view, after the check that its Explorer is trusted. Getting it starts nothing.</summary>
    internal static object Dispatch()
    {
        var windows = (IShellWindows)new ShellWindowsClass();
        object location = CsidlDesktop, root = null!;
        var desktop = windows.FindWindowSW(ref location, ref root, SwcDesktop, out var hwnd, SwfoNeedDispatch);
        if (!TrustedShell(hwnd)) throw new InvalidOperationException("The desktop shell is not the one of this account, or it is elevated.");
        var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        var browserId = new Guid("000214E2-0000-0000-C000-000000000046");
        var browser = (IShellBrowser)((IComServiceProvider)desktop).QueryService(ref service, ref browserId);
        var dispatchId = new Guid("00020400-0000-0000-C000-000000000046");
        dynamic folderView = browser.QueryActiveShellView().GetItemObject(SvgioBackground, ref dispatchId);
        return folderView.Application;
    }

    /// <summary>The window belongs to explorer.exe of the Windows folder, running as this account and not elevated.</summary>
    private static bool TrustedShell(nint hwnd)
    {
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return false;
        using var identity = WindowsIdentity.GetCurrent();
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        return string.Equals(ImagePath(pid), explorer, StringComparison.OrdinalIgnoreCase) && Token(pid) is { Elevated: false } token && token.User == identity.User;
    }

    private static Process? Find(string file, DateTime sinceUtc, SecurityIdentifier user)
    {
        Process? match = null;
        foreach (var process in Process.GetProcesses())
        {
            bool keep;
            try { keep = match is null && string.Equals(ImagePath((uint)process.Id), file, StringComparison.OrdinalIgnoreCase) && process.StartTime.ToUniversalTime() >= sinceUtc && Token((uint)process.Id) is { } t && t.User == user; }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { keep = false; }
            if (keep) match = process; else process.Dispose();
        }
        return match;
    }

    private static string? ImagePath(uint pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return null;
        var size = 1024;
        var buffer = new char[size];
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    private static (SecurityIdentifier User, bool Elevated)? Token(uint pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token)) return null;
        using (token)
        {
            const int size = 256;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenUserClass, buffer, size, out _)) return null;
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                if (!GetTokenInformation(token, TokenElevationClass, buffer, 4, out _)) return null;
                return (sid, Marshal.ReadInt32(buffer) != 0);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private sealed class Followed(Process process) : IUninstallProcess
    {
        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        public void Dispose() => process.Dispose();
    }

    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
    private const int TokenUserClass = 1, TokenElevationClass = 20;

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, [Out] char[] name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint access, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, int infoClass, nint info, int length, out int returned);

    [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    private class ShellWindowsClass { }

    // Only FindWindowSW is called; the members before it keep the order of the interface.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item([MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int cookie);
        void RegisterPending(int threadId, [MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int swClass, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object location);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)] object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int swClass, out int hwnd, int options);
        void OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool attach);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid riid);
    }

    // Only QueryActiveShellView is called; the methods before it keep the order of the interface (IOleWindow first).
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB(); void SetStatusTextSB(); void EnableModelessSB();
        void TranslateAcceleratorSB(); void BrowseObject(); void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        IShellView QueryActiveShellView();
    }

    // Only GetItemObject is called; the methods before it keep the order of the interface (IOleWindow first).
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(); void ContextSensitiveHelp(); void TranslateAccelerator(); void EnableModeless(); void UIActivate(); void Refresh(); void CreateViewWindow();
        void DestroyViewWindow(); void GetCurrentInfo(); void AddPropertySheetPages(); void SaveViewState(); void SelectItem();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetItemObject(uint item, ref Guid riid);
    }
}
