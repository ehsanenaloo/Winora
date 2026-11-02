// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public enum BloatChangeKind { Remove, Reinstall }

/// <summary>Dismissed: the person closed a record whose outcome was never confirmed. It has no undo.</summary>
public enum BloatState { Applied, Partial, Failed, Refused, Cancelled, Dismissed }

/// <summary>
/// Everything needed to describe a removal later, recorded before the first step. The fields are the evidence for the history and for bringing the app back (register again from files that
/// are still on the disk, else the Microsoft Store page of the package family).
/// </summary>
public sealed record BloatChange(Guid Id, BloatChangeKind Kind, string FullName, string FamilyName, string Name, string Version, string Publisher, string PublisherId, string? InstallLocation,
    bool WasInstalled, bool WasProvisioned, PackageSignature Signature, BloatClass Class, IReadOnlyList<string> Users, Guid? RevertOf, string? DisplayName = null)
{
    /// <summary>The name a person knows the app by (the package name when the journal line has none).</summary>
    public string Shown => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

public sealed record BloatStepResult(string Step, bool Ok, string Code, int Hresult);

public sealed record BloatChangeRecord(BloatChange Change, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, BloatState? State, IReadOnlyList<BloatStepResult> Steps);

/// <summary>Append-only record of package changes. <see cref="Begin"/> must be durable before it returns.</summary>
public interface IBloatJournal
{
    void Begin(BloatChange change, DateTimeOffset atUtc);
    void Complete(Guid id, BloatState state, IReadOnlyList<BloatStepResult> steps, DateTimeOffset atUtc);
    /// <summary>The newest changes first.</summary>
    IReadOnlyList<BloatChangeRecord> History(int max);
}

public static class BloatStep
{
    public const string RemoveAllUsers = "remove_all_users";
    public const string Deprovision = "deprovision";
    public const string Register = "register";
    public const string StopApp = "stop_app";
    public const string OpenStore = "open_store";
    public const string WaitStore = "wait_store";
}

/// <summary>A package with its class and, when Winora will not remove it, the reason. <see cref="RemoveFirst"/> are the full names of the installed apps that need this one and that Winora may remove too.</summary>
public sealed record BloatItem(AppPackage Package, BloatClass Class, BloatRule? Rule, string? BlockCode, IReadOnlyList<string> Dependents)
{
    public bool CanRemove => BlockCode is null;
    public IReadOnlyList<string> RemoveFirst { get; init; } = [];
}

public sealed record BloatListing(IReadOnlyList<BloatItem> Items, bool AllUsers, bool ProvisionedKnown, bool Elevated);

public sealed record RemovalTarget(string FullName, long? SizeBytes);

/// <summary><see cref="Added"/> is true for an app that was not ticked but that the ticked ones need, so it is removed first.</summary>
public sealed record RemovalPreviewItem(BloatItem Item, IReadOnlyList<PackageUser> Users, long? SizeBytes, bool WillRemove, bool WillDeprovision, string? RefusalCode, bool Added = false);

public sealed record RemovalPreview(IReadOnlyList<RemovalPreviewItem> Items)
{
    public long EstimatedBytes => Items.Where(i => i.RefusalCode is null && i.WillRemove).Sum(i => i.SizeBytes ?? 0);
    public bool AnyRefused => Items.Any(i => i.RefusalCode is not null);
}

public sealed record RemovalProgress(int Index, int Total, string Name);

public sealed record RemovalItemResult(string FullName, string Name, BloatState State, string Code, IReadOnlyList<BloatStepResult> Steps, long FreedBytes, bool RecordLost = false)
{
    /// <summary>Whether the app itself is gone. A partial result can also mean the removal failed after the copy for new users was taken away.</summary>
    public bool AppRemoved => State == BloatState.Applied || State == BloatState.Partial && Steps.All(s => s.Step != BloatStep.RemoveAllUsers || s.Ok);
}

public enum RemovalRunState { Completed, Cancelled, Stopped }

public sealed record RemovalRunResult(RemovalRunState State, IReadOnlyList<RemovalItemResult> Items)
{
    public long FreedBytes => Items.Sum(i => i.FreedBytes);
}

public enum ReinstallKind { None, Register, Store }

/// <summary>What Winora can do to bring a removed app back: <see cref="Register"/> registers the files that are still on the disk; <see cref="Store"/> opens the Microsoft Store page of the package family and waits until Windows has installed it.</summary>
public sealed record ReinstallOption(ReinstallKind Kind, string? FamilyName);

/// <summary>An app that was removed and is not installed now. <see cref="Option"/> says how it can be restored; <see cref="ReinstallKind.None"/> means it cannot, and the page says so.</summary>
public sealed record RemovedApp(BloatChangeRecord Record, ReinstallOption Option);

/// <summary>Where a restore is. <see cref="Waited"/> is the time spent waiting for the Store.</summary>
public sealed record ReinstallProgress(string Step, TimeSpan Waited);

/// <summary>Opens the Microsoft Store page of a package family (one fixed link; the only variable part is the family name). Winora downloads nothing: the Store installs the app.</summary>
public interface IStoreInstaller
{
    /// <summary>False when the family name is not valid or the shell could not open the page.</summary>
    bool OpenAppPage(string packageFamilyName);
}

/// <summary>How long and how often the service looks for the app after the Store page was opened.</summary>
public sealed record StoreWaitOptions(TimeSpan Poll, TimeSpan Timeout)
{
    public static StoreWaitOptions Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(10));
}

/// <summary>
/// Lists packages with a class, previews a removal, removes packages one at a time, brings them back and keeps the history. A package is removed for all users and its provisioned copy is
/// removed too, each step with its own result and read-back. When Windows says the app is in use its processes are closed and the removal is tried again; when Windows refuses, the copy for
/// new users is removed first and the removal is tried again; an app that Windows still refuses is kept and listed with that reason. Protected, system and framework packages and packages
/// that others need are never removed, whatever the caller asks for. The journal line is durable before the first step.
/// </summary>
public sealed class BloatwareService(IAppPackages packages, IBloatJournal journal, IElevationState elevation, BloatRuleSet rules, TimeProvider? clock = null, IStoreInstaller? store = null,
    StoreWaitOptions? storeWait = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<bool>? removeProvisioned = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly StoreWaitOptions wait = storeWait ?? StoreWaitOptions.Default;
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>Whether the copy that Windows installs for new users is removed too. On by default; the person can turn it off in Settings.</summary>
    private bool RemoveProvisionedCopy => removeProvisioned?.Invoke() ?? true;

    public bool IsElevated => elevation.IsElevated;

    public IReadOnlyList<BloatChangeRecord> History(int max) => journal.History(max);

    /// <summary>The family names of the packages installed for at least one user.</summary>
    public IReadOnlySet<string> InstalledFamilies() => packages.InstalledFamilies();

    // ---- listing ----

    public BloatListing List(CancellationToken cancellationToken)
    {
        var snapshot = packages.Read(cancellationToken);
        return new(Build(snapshot), snapshot.AllUsers, snapshot.ProvisionedKnown, elevation.IsElevated);
    }

    private IReadOnlyList<BloatItem> Build(PackageSnapshot snapshot)
    {
        var all = snapshot.Packages;
        var forbidden = ForbiddenByWindows();
        return all.Where(p => !p.IsResource && (p.IsInstalled || p.IsProvisioned)).Select(p => Evaluate(p, all, forbidden)).OrderBy(i => i.Package.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Package.FullName, StringComparer.Ordinal).ToList();
    }

    /// <summary>The packages (family and version) that Windows refused to remove after every supported way was tried on this PC. They are listed as kept, with the reason, instead of failing again.</summary>
    private HashSet<string> ForbiddenByWindows()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<BloatChangeRecord> history;
        try { history = journal.History(int.MaxValue); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in history)   // the newest first: the latest outcome of a package decides
        {
            if (r.Change.Kind != BloatChangeKind.Remove || r.State is null) continue;
            var key = r.Change.FamilyName + "|" + r.Change.Version;
            if (!seen.Add(key)) continue;
            if ((r.State is BloatState.Failed or BloatState.Partial) && r.Steps.Any(s => s.Code == BloatCode.SystemForbids)) result.Add(key);
        }
        return result;
    }

    private bool RemovableByRules(AppPackage p)
    {
        if (p.IsFramework || p.IsResource || p.IsSystemOwned(packages.WindowsFolder)) return false;
        return rules.Classify(p.Name, p.PublisherId).Class is BloatClass.Safe or BloatClass.Caution;
    }

    private BloatItem Evaluate(AppPackage p, IReadOnlyList<AppPackage> all, HashSet<string> forbidden)
    {
        var classification = rules.Classify(p.Name, p.PublisherId);
        var dependentPackages = all.Where(o => !string.Equals(o.FamilyName, p.FamilyName, StringComparison.OrdinalIgnoreCase) && o.IsInstalled && o.DependencyFamilies.Contains(p.FamilyName, StringComparer.OrdinalIgnoreCase)).ToList();
        var dependents = dependentPackages.Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? block = null;
        if (classification.Class == BloatClass.Protected) block = BloatCode.Protected;
        else if (p.IsFramework) block = BloatCode.Framework;
        else if (p.IsSystemOwned(packages.WindowsFolder)) block = BloatCode.SystemOwned;
        else if (dependentPackages.Any(o => !RemovableByRules(o))) block = BloatCode.HasDependents;
        else if (forbidden.Contains(p.FamilyName + "|" + p.Version)) block = BloatCode.SystemForbids;
        var first = block is null ? dependentPackages.Select(o => o.FullName).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
        return new(p, classification.Class, classification.Rule, block, dependents) { RemoveFirst = first };
    }

    public long? Measure(AppPackage package, CancellationToken cancellationToken) =>
        package.InstallLocation is { Length: > 0 } location ? packages.MeasureSize(location, cancellationToken) : null;

    // ---- preview ----

    /// <summary>The ticked apps, each preceded by the apps that need it (and that Winora may remove), once each.</summary>
    private static List<(RemovalTarget Target, bool Added)> Expand(IReadOnlyList<RemovalTarget> targets, IReadOnlyDictionary<string, BloatItem> items)
    {
        var chosen = new HashSet<string>(targets.Select(t => t.FullName), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<(RemovalTarget, bool)>();
        void Visit(RemovalTarget target, bool added, int depth)
        {
            if (!seen.Add(target.FullName)) return;
            if (depth < 8 && items.TryGetValue(target.FullName, out var item))
                foreach (var name in item.RemoveFirst) Visit(new(name, null), !chosen.Contains(name), depth + 1);
            ordered.Add((target, added));
        }
        foreach (var t in targets) Visit(t, false, 0);
        return ordered;
    }

    public RemovalPreview Preview(IReadOnlyList<RemovalTarget> targets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var snapshot = packages.Read(cancellationToken);
        var items = Build(snapshot).ToDictionary(i => i.Package.FullName, StringComparer.OrdinalIgnoreCase);
        var result = new List<RemovalPreviewItem>();
        foreach (var (target, added) in Expand(targets, items))
        {
            if (!items.TryGetValue(target.FullName, out var item))
            {
                result.Add(new(new(Placeholder(target.FullName), BloatClass.Unknown, null, BloatCode.UnknownPackage, []), [], null, false, false, BloatCode.UnknownPackage));
                continue;
            }
            var refusal = Refusal(item, snapshot);
            IReadOnlyList<PackageUser> users = [];
            if (refusal is null)
            {
                try { users = packages.ReadUsers(item.Package.FullName); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException) { users = []; }
            }
            result.Add(new(item, users, target.SizeBytes ?? (added ? Measure(item.Package, cancellationToken) : null), item.Package.IsInstalled, item.Package.IsProvisioned && snapshot.ProvisionedKnown && RemoveProvisionedCopy, refusal, added));
        }
        return new(result);
    }

    private static AppPackage Placeholder(string fullName) => new(fullName, "", fullName, "", "", "", "", fullName, null, PackageSignature.Unknown, false, false, false, false, false, false, false, false, []);

    private string? Refusal(BloatItem item, PackageSnapshot snapshot)
    {
        if (!elevation.IsElevated) return BloatCode.NeedsAdmin;
        if (!snapshot.AllUsers) return BloatCode.NeedsAdmin;
        if (item.BlockCode is not null) return item.BlockCode;
        if (!item.Package.IsInstalled && !item.Package.IsProvisioned) return BloatCode.NotInstalled;
        return null;
    }

    // ---- removal ----

    public Task<RemovalRunResult> RemoveAsync(IReadOnlyList<RemovalTarget> targets, IProgress<RemovalProgress>? progress, CancellationToken cancellationToken) =>
        RunAsync(() => Remove(targets, progress, cancellationToken), cancellationToken);

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private RemovalRunResult Remove(IReadOnlyList<RemovalTarget> targets, IProgress<RemovalProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var results = new List<RemovalItemResult>();
        var state = RemovalRunState.Completed;
        var first = packages.Read(CancellationToken.None);
        var ordered = Expand(targets, Build(first).ToDictionary(i => i.Package.FullName, StringComparer.OrdinalIgnoreCase)).Select(x => x.Target).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                state = RemovalRunState.Cancelled;
                foreach (var rest in ordered.Skip(i)) results.Add(new(rest.FullName, rest.FullName, BloatState.Cancelled, BloatCode.Cancelled, [], 0));
                break;
            }
            // A fresh read per package: an earlier removal can change the dependents and the state of the next one.
            var snapshot = packages.Read(CancellationToken.None);
            var items = Build(snapshot);
            var item = items.FirstOrDefault(x => string.Equals(x.Package.FullName, ordered[i].FullName, StringComparison.OrdinalIgnoreCase));
            if (item is null) { results.Add(new(ordered[i].FullName, ordered[i].FullName, BloatState.Refused, BloatCode.UnknownPackage, [], 0)); continue; }
            progress?.Report(new(i + 1, ordered.Count, item.Package.DisplayName));
            var refusal = Refusal(item, snapshot);
            if (refusal is not null) { results.Add(new(item.Package.FullName, item.Package.Name, BloatState.Refused, refusal, [], 0)); continue; }
            var one = RemoveOne(item, snapshot, ordered[i].SizeBytes);
            results.Add(one);
            if (one.Code == BloatCode.JournalFailed) { state = RemovalRunState.Stopped; break; }
        }
        return new(state, results);
    }

    private RemovalItemResult RemoveOne(BloatItem item, PackageSnapshot snapshot, long? size)
    {
        var p = item.Package;
        IReadOnlyList<string> users;
        try { users = packages.ReadUsers(p.FullName).Where(u => u.State == PackageInstallState.Installed || u.State == PackageInstallState.Staged).Select(u => u.Sid).ToList(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException) { users = []; }
        var change = new BloatChange(Guid.NewGuid(), BloatChangeKind.Remove, p.FullName, p.FamilyName, p.Name, p.Version, p.Publisher, p.PublisherId, p.InstallLocation, p.IsInstalled,
            p.IsProvisioned, p.Signature, item.Class, users, null, p.DisplayName);
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(p.FullName, p.Name, BloatState.Refused, BloatCode.JournalFailed, [], 0); }

        var steps = new List<BloatStepResult>();
        var removedOk = true;
        var deprovisioned = false;
        var deprovisionOk = true;
        try
        {
            if (p.IsInstalled)
            {
                var r = RemoveInstalled(p, snapshot, steps, ref deprovisioned);
                steps.Add(new(BloatStep.RemoveAllUsers, r.Ok, r.Code, r.Hresult));
                removedOk = r.Ok;
            }
            if (removedOk && !deprovisioned && p.IsProvisioned && snapshot.ProvisionedKnown && RemoveProvisionedCopy)
            {
                var r = DeprovisionChecked(p.FamilyName);
                steps.Add(new(BloatStep.Deprovision, r.Ok, r.Code, r.Hresult));
                deprovisionOk = r.Ok;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) // the entry is begun, so it must be completed
        {
            var inRemoval = p.IsInstalled && !steps.Any(x => x.Step == BloatStep.RemoveAllUsers);
            steps.Add(new(inRemoval ? BloatStep.RemoveAllUsers : BloatStep.Deprovision, false, BloatCode.Failed, 0));
            if (inRemoval) removedOk = false; else deprovisionOk = false;
        }
        var state = !removedOk ? (deprovisioned ? BloatState.Partial : BloatState.Failed) : deprovisionOk ? BloatState.Applied : BloatState.Partial;
        var lost = false;
        try { journal.Complete(change.Id, state, steps, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The change is made; the history shows it as interrupted. One more try, then the caller is told.
            try { journal.Complete(change.Id, state, steps, clock.GetUtcNow()); }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { lost = true; }
        }
        var firstFailure = steps.LastOrDefault(s => !s.Ok);
        return new(p.FullName, p.Name, state, firstFailure?.Code ?? BloatCode.Ok, steps, removedOk && p.IsInstalled ? size ?? 0 : 0, lost);
    }

    private PackageStepResult DeprovisionChecked(string family)
    {
        var r = packages.Deprovision(family);
        if (r.Ok && packages.IsProvisioned(family)) return PackageStepResult.Fail(BloatCode.VerifyFailed);
        // Windows answers "not found" when no provisioned copy exists (removing for all users can take it away already); the goal (no copy left) is reached.
        if (!r.Ok && r.Code == BloatCode.NotFound && !packages.IsProvisioned(family)) return PackageStepResult.Success;
        return r;
    }

    /// <summary>
    /// The supported ways, in order: remove for all users; when Windows says the app is in use, close its processes and remove again; when Windows refuses and the app has a copy for new users,
    /// remove that copy first and remove again. What Windows still refuses afterwards is reported as <see cref="BloatCode.SystemForbids"/>.
    /// </summary>
    private PackageStepResult RemoveInstalled(AppPackage p, PackageSnapshot snapshot, List<BloatStepResult> steps, ref bool deprovisioned)
    {
        var r = packages.RemoveForAllUsers(p.FullName);
        if (!r.Ok && r.Code == BloatCode.InUse)
        {
            var stop = packages.StopApp(p.FullName);
            steps.Add(new(BloatStep.StopApp, stop.Ok, stop.Code, stop.Hresult));
            if (stop.Ok) r = packages.RemoveForAllUsers(p.FullName);
        }
        if (!r.Ok && r.Code is BloatCode.RefusedByWindows or BloatCode.Failed or BloatCode.InUse && p.IsProvisioned && snapshot.ProvisionedKnown && RemoveProvisionedCopy)
        {
            var d = DeprovisionChecked(p.FamilyName);
            steps.Add(new(BloatStep.Deprovision, d.Ok, d.Code, d.Hresult));
            deprovisioned = d.Ok;
            if (d.Ok) r = packages.RemoveForAllUsers(p.FullName);
        }
        if (r.Ok && packages.InstalledFullNames(p.FamilyName).Contains(p.FullName, StringComparer.OrdinalIgnoreCase)) r = PackageStepResult.Fail(BloatCode.VerifyFailed);
        if (!r.Ok && r.Code == BloatCode.RefusedByWindows) r = PackageStepResult.Fail(BloatCode.SystemForbids, r.Hresult);
        return r;
    }

    // ---- restore ----

    private static bool StoreCapable(BloatChange c) =>
        c.Signature == PackageSignature.Store || (c.Signature == PackageSignature.Developer && c.Class is BloatClass.Safe or BloatClass.Caution);

    /// <summary>
    /// What can bring the app back. Register works while the package files are still on disk (a copy for new users that was not removed, for example) and registers the app for the current user
    /// only. Otherwise a Store app can be installed again: Winora opens the Store page of the package family and waits for Windows to finish the install (it downloads nothing itself).
    /// </summary>
    public ReinstallOption GetReinstallOption(BloatChangeRecord record, IReadOnlyList<BloatChangeRecord>? history = null, IReadOnlySet<string>? installedFamilies = null)
    {
        var c = record.Change;
        if (c.Kind != BloatChangeKind.Remove || record.State is not (BloatState.Applied or BloatState.Partial)) return new(ReinstallKind.None, null);
        history ??= journal.History(int.MaxValue);
        if (history.Any(r => r.Change.RevertOf == c.Id && r.State == BloatState.Applied)) return new(ReinstallKind.None, null);
        if (!c.WasInstalled) return new(ReinstallKind.None, null);
        installedFamilies ??= packages.InstalledFamilies();
        if (installedFamilies.Contains(c.FamilyName)) return new(ReinstallKind.None, null);
        if (c.InstallLocation is { Length: > 0 } location && packages.HasManifest(location)) return new(ReinstallKind.Register, c.FamilyName);
        return StoreCapable(c) ? new(ReinstallKind.Store, c.FamilyName) : new(ReinstallKind.None, null);
    }

    /// <summary>The apps that were removed and are not installed now, newest first, one per package family, each with the way back (or none).</summary>
    public IReadOnlyList<RemovedApp> RemovedApps(int maxHistory = int.MaxValue)
    {
        var history = journal.History(maxHistory);
        var installed = packages.InstalledFamilies();
        var result = new List<RemovedApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in history)
        {
            var c = r.Change;
            if (c.Kind != BloatChangeKind.Remove || r.State is not (BloatState.Applied or BloatState.Partial) || !c.WasInstalled) continue;
            if (!seen.Add(c.FamilyName) || installed.Contains(c.FamilyName)) continue;
            // A newer restore of this family (or a newer removal) decides; only the newest removal of a family is listed.
            result.Add(new(r, GetReinstallOption(r, history, installed)));
        }
        return result;
    }

    /// <summary>A record with a begin line and no end line.</summary>
    public static bool IsUnconfirmed(BloatChangeRecord record) => record.State is null && record.EndedUtc is null;

    /// <summary>
    /// The person closes an unconfirmed removal or reinstall record. Nothing is written and the record gets no undo (Dismissed). Winora does not guess whether the app was removed: the Preinstalled
    /// apps page reads the installed apps, and a removed app that is gone can be restored from its Removed apps tab only when a completed removal record names it.
    /// </summary>
    public Task<(bool Ok, string Code)> DismissAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync<(bool Ok, string Code)>(() =>
        {
            var record = journal.History(int.MaxValue).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return (false, BloatCode.NotUnconfirmed);
            try { journal.Complete(changeId, BloatState.Dismissed, [], clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (false, BloatCode.JournalFailed); }
            return (true, BloatCode.Dismissed);
        }, cancellationToken);

    public Task<RemovalItemResult> ReinstallAsync(Guid changeId, CancellationToken cancellationToken) => ReinstallAsync(changeId, null, cancellationToken);

    public Task<RemovalItemResult> ReinstallAsync(Guid changeId, IProgress<ReinstallProgress>? progress, CancellationToken cancellationToken) =>
        RunAsync(() => Reinstall(changeId, progress, cancellationToken), cancellationToken);

    private RemovalItemResult Reinstall(Guid changeId, IProgress<ReinstallProgress>? progress, CancellationToken ct)
    {
        var history = journal.History(int.MaxValue);
        var record = history.FirstOrDefault(r => r.Change.Id == changeId);
        if (record is null) return new(changeId.ToString("D"), "", BloatState.Refused, BloatCode.CannotReinstall, [], 0);
        var c = record.Change;
        var option = GetReinstallOption(record, history);
        if (option.Kind == ReinstallKind.None) return new(c.FullName, c.Shown, BloatState.Refused, BloatCode.CannotReinstall, [], 0);
        var canStore = StoreCapable(c);
        if (option.Kind == ReinstallKind.Register && !elevation.IsElevated) return new(c.FullName, c.Shown, BloatState.Refused, BloatCode.NeedsAdmin, [], 0);
        var change = c with { Id = Guid.NewGuid(), Kind = BloatChangeKind.Reinstall, RevertOf = c.Id };
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(c.FullName, c.Shown, BloatState.Refused, BloatCode.JournalFailed, [], 0); }

        var steps = new List<BloatStepResult>();
        var done = false;
        var code = BloatCode.Ok;
        var state = BloatState.Applied;
        if (option.Kind == ReinstallKind.Register)
        {
            progress?.Report(new(BloatStep.Register, TimeSpan.Zero));
            var r = packages.RegisterStaged(c.FullName);
            if (r.Ok && !packages.InstalledFullNames(c.FamilyName).Contains(c.FullName, StringComparer.OrdinalIgnoreCase)) r = PackageStepResult.Fail(BloatCode.VerifyFailed);
            steps.Add(new(BloatStep.Register, r.Ok, r.Code, r.Hresult));
            done = r.Ok;
            if (!r.Ok) { code = r.Code; state = BloatState.Failed; }
        }
        // The Store is the way back when the files are gone, and the second way when registering the files failed.
        if (!done && canStore)
        {
            (state, code) = RestoreFromStore(c, steps, progress, ct);
        }
        var lost = false;
        try { journal.Complete(change.Id, state, steps, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { lost = true; } // shown as interrupted
        return new(c.FullName, c.Shown, state, code, steps, 0, lost);
    }

    private (BloatState State, string Code) RestoreFromStore(BloatChange c, List<BloatStepResult> steps, IProgress<ReinstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new(BloatStep.OpenStore, TimeSpan.Zero));
        if (store is null || !store.OpenAppPage(c.FamilyName))
        {
            steps.Add(new(BloatStep.OpenStore, false, BloatCode.StoreFailed, 0));
            return (BloatState.Failed, BloatCode.StoreFailed);
        }
        steps.Add(new(BloatStep.OpenStore, true, BloatCode.Ok, 0));
        var waited = TimeSpan.Zero;
        while (true)
        {
            if (packages.InstalledFamilies().Contains(c.FamilyName))
            {
                steps.Add(new(BloatStep.WaitStore, true, BloatCode.Ok, 0));
                return (BloatState.Applied, BloatCode.Ok);
            }
            if (ct.IsCancellationRequested) { steps.Add(new(BloatStep.WaitStore, false, BloatCode.Cancelled, 0)); return (BloatState.Cancelled, BloatCode.Cancelled); }
            if (waited >= wait.Timeout) { steps.Add(new(BloatStep.WaitStore, false, BloatCode.StoreNotFinished, 0)); return (BloatState.Failed, BloatCode.StoreNotFinished); }
            progress?.Report(new(BloatStep.WaitStore, waited));
            try { delay(wait.Poll, ct).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* the next pass reports the stop */ }
            waited += wait.Poll;
        }
    }
}
