// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using Winora.Domain;

namespace Winora.Application;

/// <summary>
/// Removes installed programs (decision DEC-IW-240). Registered desktop programs (Windows Installer and uninstall keys) run their own quiet uninstall command through the one reviewed launcher; Store
/// packages go through <see cref="BloatwareService"/>, so they share its rules and its record. A program is removed only when it is not protected, the plan comes from its registration read just now,
/// and the intent is in the journal before the uninstaller starts. The result is confirmed by reading the registration back. Winora cannot undo a removal and does not remove leftovers.
/// </summary>
public sealed class AppUninstallService(Func<CancellationToken, Task<DiscoveryResult<InstalledApplication>>> desktopApps, IUninstallRegistrations registrations, IUninstallLauncher launcher,
    IAppUninstallJournal journal, IElevationState elevation, BloatwareService packages, string windowsFolder, TimeProvider? clock = null, string? appFolder = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, AppAssessment> assessments = new(StringComparer.Ordinal);

    public bool IsElevated => elevation.IsElevated;

    public static string KeyOf(InstalledApplication i) => $"{i.Identity.Kind}|{i.Identity.Scope}|{i.Identity.RegistryView}|{i.Identity.StableIdentity}";

    public IReadOnlyList<AppUninstallRecord> History(int max) => journal.History(max);

    // ---- listing ----

    /// <summary>Registered desktop programs and Store apps, each assessed. Store frameworks and resource packages are not listed: they are not programs a person installed.</summary>
    public async Task<DiscoveryResult<InstalledApplication>> ScanAsync(CancellationToken cancellationToken)
    {
        var desktop = await desktopApps(cancellationToken).ConfigureAwait(false);
        var items = new List<InstalledApplication>(desktop.Items);
        var issues = new List<DiscoveryIssue>(desktop.Issues);
        var next = new Dictionary<string, AppAssessment>(StringComparer.Ordinal);
        foreach (var app in desktop.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            next[KeyOf(app)] = AssessDesktop(app);
        }
        try
        {
            var listing = await Task.Run(() => packages.List(cancellationToken), cancellationToken).ConfigureAwait(false);
            foreach (var item in listing.Items)
            {
                var p = item.Package;
                if (!p.IsInstalled || p.IsFramework || p.IsResource) continue;
                var app = new InstalledApplication(new(InstallationKind.Msix, p.FullName, InstallationScope.User, null, PackageText.Organisation(p.Publisher)),
                    string.IsNullOrWhiteSpace(p.DisplayName) ? PackageText.FriendlyName(p.Name) : p.DisplayName, p.InstallLocation, "package:" + p.FullName, p.Version, null, null, item.BlockCode is not null);
                items.Add(app);
                next[KeyOf(app)] = item.BlockCode is { } block ? AppAssessment.Locked(LockOf(block)) : new(AppRemoval.Package, null, null);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            issues.Add(new DiscoveryIssue("AppPackages", WinoraError.From(e is UnauthorizedAccessException ? ErrorCode.AccessDenied : ErrorCode.DiscoveryUnavailable)));
        }
        assessments.Clear();
        foreach (var pair in next) assessments[pair.Key] = pair.Value;
        items.Sort((a, b) =>
        {
            var c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Identity.StableIdentity, b.Identity.StableIdentity);
        });
        return new(items, issues, clock.GetUtcNow());
    }

    private AppAssessment AssessDesktop(InstalledApplication app)
    {
        try { return registrations.Read(app) is { } reg ? Vetted(AppUninstallPlanner.Assess(reg, app.Identity.Scope, windowsFolder, appFolder)) : AppAssessment.Locked(AppCode.NotInstalled); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return AppAssessment.Locked(AppCode.LockNoUninstaller); }
    }

    /// <summary>A plan the launcher would refuse is shown as locked with that reason, so the list never offers a removal that cannot start.</summary>
    private AppAssessment Vetted(AppAssessment assessment) => assessment.Plan is { } plan && launcher.Refusal(plan) is { } refusal ? AppAssessment.Locked(refusal) : assessment;

    /// <summary>What the last scan found for this program. A program the scan did not see is treated as locked.</summary>
    public AppAssessment Assess(InstalledApplication app) => assessments.TryGetValue(KeyOf(app), out var a) ? a : AppAssessment.Locked(AppCode.LockNoUninstaller);

    private static string LockOf(string bloatCode) => bloatCode switch
    {
        BloatCode.Protected => AppCode.LockProtected,
        BloatCode.Framework => AppCode.LockFramework,
        BloatCode.HasDependents => AppCode.LockHasDependents,
        _ => AppCode.LockWindows,
    };

    // ---- preview ----

    public AppRemovalPreview Preview(IReadOnlyList<InstalledApplication> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        return new([.. apps.Select(a =>
        {
            var assessment = Assess(a);
            string? refusal = assessment.LockCode ?? (elevation.IsElevated ? null : AppCode.NeedsAdmin);
            return new AppRemovalItem(a, assessment, refusal);
        })]);
    }

    // ---- removal ----

    public async Task<AppRemovalRun> RemoveAsync(IReadOnlyList<InstalledApplication> apps, IProgress<AppRemovalProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apps);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<AppRemovalResult>();
            var stopped = false;
            for (var i = 0; i < apps.Count; i++)
            {
                var app = apps[i];
                var key = KeyOf(app);
                if (cancellationToken.IsCancellationRequested)
                {
                    foreach (var rest in apps.Skip(i)) results.Add(new(KeyOf(rest), rest.DisplayName, AppRemovalState.Cancelled, AppCode.Cancelled, false, 0));
                    break;
                }
                progress?.Report(new(i + 1, apps.Count, app.DisplayName));
                var result = app.Identity.Kind == InstallationKind.Msix ? await RemovePackageAsync(app, key, cancellationToken).ConfigureAwait(false) : await RemoveDesktopAsync(app, key, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (result.Code == AppCode.JournalFailed) { stopped = true; break; }
            }
            return new(results, stopped);
        }
        finally { gate.Release(); }
    }

    private async Task<AppRemovalResult> RemovePackageAsync(InstalledApplication app, string key, CancellationToken ct)
    {
        if (!elevation.IsElevated) return new(key, app.DisplayName, AppRemovalState.Refused, AppCode.NeedsAdmin, false, 0);
        var run = await packages.RemoveAsync([new RemovalTarget(app.Identity.StableIdentity, null)], null, ct).ConfigureAwait(false);
        var r = run.Items.FirstOrDefault();
        if (r is null) return new(key, app.DisplayName, AppRemovalState.Failed, AppCode.Failed, false, 0);
        var state = r.State switch
        {
            BloatState.Applied => AppRemovalState.Removed,
            BloatState.Partial => r.AppRemoved ? AppRemovalState.Removed : AppRemovalState.Failed,
            BloatState.Cancelled => AppRemovalState.Cancelled,
            BloatState.Refused => AppRemovalState.Refused,
            _ => AppRemovalState.Failed,
        };
        var code = r.State == BloatState.Partial && r.AppRemoved ? AppCode.Partial : r.AppRemoved ? AppCode.Ok : r.Code switch
        {
            BloatCode.NeedsAdmin or BloatCode.AccessDenied => AppCode.NeedsAdmin,
            BloatCode.InUse => AppCode.InUse,
            BloatCode.NotInstalled or BloatCode.NotFound => AppCode.NotInstalled,
            BloatCode.Protected or BloatCode.Framework or BloatCode.SystemOwned or BloatCode.HasDependents => LockOf(r.Code),
            BloatCode.JournalFailed => AppCode.JournalFailed,
            BloatCode.Cancelled => AppCode.Cancelled,
            BloatCode.VerifyFailed => AppCode.StillInstalled,
            _ => AppCode.Refused,
        };
        return new(key, app.DisplayName, state, code, false, 0);
    }

    private async Task<AppRemovalResult> RemoveDesktopAsync(InstalledApplication app, string key, CancellationToken ct)
    {
        AppRemovalResult Done(AppRemovalState state, string code, bool restart = false, long freed = 0) => new(key, app.DisplayName, state, code, restart, freed);

        UninstallRegistration? reg;
        try { reg = registrations.Read(app); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return Done(AppRemovalState.Refused, AppCode.NeedsAdmin); }
        if (reg is null) return Done(AppRemovalState.Refused, AppCode.NotInstalled);
        var assessment = Vetted(AppUninstallPlanner.Assess(reg, app.Identity.Scope, windowsFolder, appFolder));
        if (assessment.LockCode is { } locked) return Done(AppRemovalState.Refused, locked);
        if (assessment.Plan is not { } plan) return Done(AppRemovalState.Refused, AppCode.UninstallerMissing);
        if (!elevation.IsElevated) return Done(AppRemovalState.Refused, AppCode.NeedsAdmin);

        var change = new AppUninstallChange(Guid.NewGuid(), app.DisplayName, app.Version ?? "", app.Identity.Publisher ?? "", app.Identity.Kind, app.Identity.Scope, plan.Method,
            plan.MsiProductCode, AppUninstallPlanner.ExecutableHint(plan), app.EstimatedSizeKb, app.RegistrationProvenance);
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Done(AppRemovalState.Refused, AppCode.JournalFailed); }

        var launch = await launcher.RunAsync(plan, ct).ConfigureAwait(false);
        AppRemovalState state; string code; var restart = false; int? exit = launch.ExitCode;
        if (launch.ExitCode is null && launch.Code == AppCode.StartedAsUser)
        {
            // The program runs as the user and could not be followed: only the registration read back tells whether it is gone.
            bool gone;
            try { gone = registrations.Read(app) is null; }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { gone = false; }
            (state, code) = gone ? (AppRemovalState.Removed, AppCode.Ok) : (AppRemovalState.Failed, AppCode.StartedAsUser);
        }
        else if (launch.ExitCode is not { } exitCode)
        {
            state = launch.Code == AppCode.Cancelled ? AppRemovalState.Cancelled : AppRemovalState.Refused;
            code = launch.Code;
            if (launch.Code == AppCode.Failed) state = AppRemovalState.Failed;
            // The limit only ended the wait: the uninstaller may still finish, so the removal is not a failure.
            if (launch.Code == AppCode.Timeout) state = AppRemovalState.StillRunning;
        }
        else
        {
            var (success, restartNeeded, meaning) = AppUninstallPlanner.Interpret(exitCode, plan.IsMsi);
            restart = restartNeeded;
            bool gone;
            try { gone = registrations.Read(app) is null; }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { gone = false; }
            if (gone && (success || plan.Method == AppRemoval.Interactive)) { state = restart ? AppRemovalState.PendingRestart : AppRemovalState.Removed; code = restart ? AppCode.Restart : AppCode.Ok; }
            else if (success) { state = restart ? AppRemovalState.PendingRestart : AppRemovalState.Failed; code = restart ? AppCode.Restart : AppCode.StillInstalled; }
            else { state = meaning == AppCode.Cancelled ? AppRemovalState.Cancelled : AppRemovalState.Failed; code = meaning; }
        }
        try { journal.Complete(change.Id, new(state, code, exit, restart), clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the removal ran; the history shows it as interrupted */ }
        return Done(state, code, restart, state is AppRemovalState.Removed or AppRemovalState.PendingRestart ? app.EstimatedSizeKb ?? 0 : 0);
    }
}
