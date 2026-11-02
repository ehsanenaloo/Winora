// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Application;

/// <summary>
/// Start, stop, restart and startup type changes of Windows services (DEC-IW-270) with a preview, a journal entry before the change, a read-back after it and a conditional undo of the
/// startup type. A service that someone else changed after the review is never touched. Locked services (<see cref="ServiceSafetyPolicy"/>) refuse every action. A stop is refused while a
/// running service depends on the target, and the refusal names those services; dependents are never stopped. Disabling is refused while an enabled service depends on the target.
/// </summary>
public sealed class ServiceManagerService
{
    private const int MaxNameLength = 256;
    private const int WaitAttempts = 60;
    private static readonly ServiceStartMode[] Modes = [ServiceStartMode.Automatic, ServiceStartMode.AutomaticDelayed, ServiceStartMode.Manual, ServiceStartMode.Disabled];

    private readonly IServiceControl control;
    private readonly IServiceJournal journal;
    private readonly ServiceSafetyPolicy policy;
    private readonly TimeProvider clock;
    private readonly TimeSpan wait;
    private readonly SemaphoreSlim gate = new(1, 1);

    public ServiceManagerService(IServiceControl control, IServiceJournal journal, ServiceSafetyPolicy? policy = null, TimeProvider? clock = null, TimeSpan? wait = null)
    {
        this.control = control; this.journal = journal;
        this.policy = policy ?? ServiceSafetyPolicy.Default;
        this.clock = clock ?? TimeProvider.System;
        this.wait = wait ?? TimeSpan.FromSeconds(30);
    }

    public ServiceSafetyPolicy Policy => policy;

    /// <summary>The startup types the page offers, in the order of the picker.</summary>
    public static IReadOnlyList<ServiceStartMode> StartModes => Modes;

    public IReadOnlyList<ServiceChangeRecord> History(int max) => journal.History(max);

    /// <summary>
    /// Closes the records of start mode changes that were begun and never finished. Proven cases only: the journal shows the write as made and the service has the start mode the change set
    /// (Applied, code reconciled), or the service still has the start mode it had before (Failed, interrupted_unchanged). Everything else stays unconfirmed: a start mode equal to the intended
    /// one without the written line does not show who set it. The person resolves those records (<see cref="DismissAsync"/>, <see cref="RestoreBeforeAsync"/>). Start, stop and restart
    /// records change no setting and are only dismissed.
    /// </summary>
    public Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken = default) => UnderGate(() =>
    {
        var done = 0;
        foreach (var record in History(int.MaxValue).Where(IsUnconfirmed))
        {
            var c = record.Change;
            if (c.Kind != ServiceChangeKind.SetStartMode || c.BeforeMode is not { } before || c.AfterMode is not { } intended || intended == before || !IsValidName(c.Name)) continue;
            if (control.Observe(c.Name).Service is not { } live) continue;
            ServiceChangeState state; string code;
            // The service must be the one the change was made on: the configuration it had before, with the live image path, hashes to the fingerprint of the review. A replaced image stays unconfirmed.
            if (record.Written && live.StartMode == intended && c.BeforeFingerprint == Fingerprint($"cfg|{before}|{live.ImagePath}")) (state, code) = (ServiceChangeState.Applied, ServiceCode.Reconciled);
            else if (live.StartMode == before) (state, code) = (ServiceChangeState.Failed, ServiceCode.InterruptedUnchanged);
            else continue;
            // The fingerprint of the configuration the change left is what the undo compares with.
            try { journal.Complete(c.Id, state, code, state == ServiceChangeState.Applied ? ConfigFingerprint(live) : null, clock.GetUtcNow()); done++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays open */ }
        }
        return done;
    }, cancellationToken);

    /// <summary>
    /// Runs on the same gate as apply, dismiss and restore. A reconciler that ran beside them could close a record whose change is still being made, or write Applied over a record the person
    /// just dismissed. The records are read after the gate is held, so what it sees is final for that moment.
    /// </summary>
    private async Task<T> UnderGate<T>(Func<T> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>A record with a begin line and no end line.</summary>
    public static bool IsUnconfirmed(ServiceChangeRecord record) => record.State is null && record.EndedUtc is null;

    /// <summary>The person keeps the current state and closes an unconfirmed record. Nothing is written and the record gets no undo (Dismissed).</summary>
    public Task<ServiceApplyResult> DismissAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var record = History(int.MaxValue).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return Result(ServiceChangeState.Refused, ServiceCode.NotUnconfirmed);
            try { journal.Complete(changeId, ServiceChangeState.Dismissed, ServiceCode.Dismissed, null, clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Result(ServiceChangeState.Refused, ServiceCode.JournalFailed); }
            return Result(ServiceChangeState.Dismissed, ServiceCode.Dismissed, record.Change);
        }, cancellationToken);

    /// <summary>The request that puts the start mode from before back, or null (only start mode changes, and only when the service now has another start mode).</summary>
    private ServiceRequest? RestoreRequest(ServiceChangeRecord record)
    {
        var c = record.Change;
        if (!IsUnconfirmed(record) || c.Kind != ServiceChangeKind.SetStartMode || c.BeforeMode is not { } before || !Modes.Contains(before) || !IsValidName(c.Name)) return null;
        if (control.Observe(c.Name).Service is not { } live || live.StartMode == before) return null;
        var request = new ServiceRequest(ServiceChangeKind.SetStartMode, c.Name, before, ConfigFingerprint(live));
        return Preview(request).IsRefused ? null : request;
    }

    /// <summary>Whether the start mode from before can be put back for an unconfirmed record.</summary>
    public bool CanRestoreBefore(ServiceChangeRecord record) => RestoreRequest(record) is not null;

    /// <summary>
    /// The person puts the start mode from before back and closes the unconfirmed record. It is an ordinary change with the usual safety checks, its own record and its own undo, written only
    /// while the service has what was just read. The unconfirmed record is then Dismissed with the code resolved_restored.
    /// </summary>
    public Task<ServiceApplyResult> RestoreBeforeAsync(Guid changeId, CancellationToken cancellationToken = default, string? expectedToken = null) =>
        RunAsync(() =>
        {
            var record = History(int.MaxValue).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return Result(ServiceChangeState.Refused, ServiceCode.NotUnconfirmed);
            if (RestoreRequest(record) is not { } request) return Result(ServiceChangeState.Refused, ServiceCode.CannotRevert);
            if (expectedToken is not null && expectedToken != TokenOf(record.Change, request)) return Result(ServiceChangeState.Refused, ServiceCode.Stale);
            var result = ApplyCore(request, null, cancellationToken);
            if (result.Ok)
            {
                try { journal.Complete(changeId, ServiceChangeState.Dismissed, ServiceCode.ResolvedRestored, null, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result with { RecordLost = true }; }
            }
            return result;
        }, cancellationToken);

    private static string TokenOf(ServiceChange original, ServiceRequest request) => RecoveryToken.Of("service", original.Id, request.ExpectedFingerprint, request.Mode);

    /// <summary>What the restore would do, read now. Null when it is not possible. The token binds the confirmation to what was shown.</summary>
    public RecoveryPlan? PlanRestoreBefore(ServiceChangeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (RestoreRequest(record) is not { } request || control.Observe(record.Change.Name).Service is not { } live) return null;
        return new([new(record.Change.Name, live.StartMode.ToString(), request.Mode.ToString()!)], TokenOf(record.Change, request));
    }

    public static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string ConfigFingerprint(ServiceObservation o) => Fingerprint($"cfg|{o.StartMode}|{o.ImagePath}");

    private static string StateFingerprint(ServiceObservation o) => Fingerprint($"state|{o.RunState}|{o.StartMode}");

    /// <summary>A service name has no path separator and no control character.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && name.TrimEnd() == name && name.IndexOfAny(['\\', '/']) < 0 && !name.Any(char.IsControl);

    // ---- availability from list data ----

    /// <summary>Whether the action fits a listed service, from the list data alone: null when it does, otherwise the code that explains why not. The change is checked again when it is made.</summary>
    public static string? Availability(ServiceChangeKind kind, ServiceInfo item, ServicePolicyInfo info)
    {
        if (ServiceSafetyPolicy.LockedCode(info) is { } locked) return locked;
        var pending = item.RunState is ServiceRunState.StartPending or ServiceRunState.StopPending or ServiceRunState.ContinuePending or ServiceRunState.PausePending;
        return kind switch
        {
            ServiceChangeKind.Start => item.RunState == ServiceRunState.Running ? ServiceCode.AlreadyRunning : pending ? ServiceCode.Busy : item.StartMode == ServiceStartMode.Disabled ? ServiceCode.Disabled : null,
            ServiceChangeKind.Stop => item.RunState == ServiceRunState.Stopped ? ServiceCode.NotRunning : pending ? ServiceCode.Busy : null,
            ServiceChangeKind.Restart => item.RunState == ServiceRunState.Stopped ? ServiceCode.NotRunning : pending ? ServiceCode.Busy : item.StartMode == ServiceStartMode.Disabled ? ServiceCode.Disabled : null,
            _ => null,
        };
    }

    // ---- preview ----

    public ServicePreview Preview(ServiceRequest request) => PreviewCore(request, out _);

    private static readonly IReadOnlyList<ServiceRef> NoRefs = [];

    private ServicePreview Refused(ServiceRequest request, string display, ServicePolicyInfo info, string code, IReadOnlyList<ServiceRef>? blocked = null, ServiceObservation? o = null) =>
        new(request, display, info, o?.StartMode ?? ServiceStartMode.Unknown, null, o?.RunState ?? ServiceRunState.Unknown, ServiceSafetyPolicy.Risk(info, o?.LaunchProtected ?? false), null, false, code, blocked ?? NoRefs, o?.RunningDependents ?? NoRefs);

    private ServicePreview PreviewCore(ServiceRequest request, out ServiceObservation? observed)
    {
        ArgumentNullException.ThrowIfNull(request);
        observed = null;
        var none = new ServicePolicyInfo(ServicePolicyClass.ThirdParty, null);
        if (!IsValidName(request.Name)) return Refused(request, "", none, ServiceCode.InvalidName);
        var lookup = control.Observe(request.Name);
        if (!lookup.Found) return Refused(request, request.Name, none, lookup.Code);
        var o = observed = lookup.Service!;
        var info = policy.Classify(request.Name, o.Kind, o.StartMode, o.ImagePath, o.ImageTrusted);
        if (ServiceSafetyPolicy.LockedCode(info) is { } locked) return Refused(request, o.DisplayName, info, locked, null, o);
        if (o.StartMode == ServiceStartMode.Unknown) return Refused(request, o.DisplayName, info, ServiceCode.NotControllable, null, o);

        ServicePreview Ok(ServiceStartMode? after, string fingerprint, bool willChange) =>
            new(request, o.DisplayName, info, o.StartMode, after, o.RunState, ServiceSafetyPolicy.Risk(info, o.LaunchProtected), fingerprint, willChange, null, NoRefs, o.RunningDependents);
        ServicePreview Refuse(string code, IReadOnlyList<ServiceRef>? blocked = null) => Refused(request, o.DisplayName, info, code, blocked, o);
        var pending = o.RunState is ServiceRunState.StartPending or ServiceRunState.StopPending or ServiceRunState.ContinuePending or ServiceRunState.PausePending;

        switch (request.Kind)
        {
            case ServiceChangeKind.SetStartMode:
            {
                if (request.Mode is not { } mode || !Modes.Contains(mode)) return Refuse(ServiceCode.Failed);
                var fingerprint = ConfigFingerprint(o);
                if (request.ExpectedFingerprint is { } expected && expected != fingerprint) return Refuse(ServiceCode.Stale);
                if (o.StartMode == mode) return Ok(mode, fingerprint, false);
                if (mode == ServiceStartMode.Disabled && !o.SafetyFactsKnown) return Refuse(ServiceCode.FactsUnreadable);
                if (mode == ServiceStartMode.Disabled && o.EnabledDependents.Count > 0) return Refuse(ServiceCode.HasEnabledDependents, o.EnabledDependents);
                return Ok(mode, fingerprint, true);
            }
            case ServiceChangeKind.Start:
                if (o.RunState == ServiceRunState.Running) return Ok(null, StateFingerprint(o), false);
                if (pending) return Refuse(ServiceCode.Busy);
                if (o.StartMode == ServiceStartMode.Disabled) return Refuse(ServiceCode.Disabled);
                return Ok(null, StateFingerprint(o), true);
            case ServiceChangeKind.Stop:
            case ServiceChangeKind.Restart:
                if (o.RunState == ServiceRunState.Stopped) return Refuse(ServiceCode.NotRunning);
                if (pending) return Refuse(ServiceCode.Busy);
                if (!o.AcceptsStop) return Refuse(ServiceCode.CannotStop);
                if (o.RunningDependents.Count > 0) return Refuse(ServiceCode.HasRunningDependents, o.RunningDependents);
                if (request.Kind == ServiceChangeKind.Restart && o.StartMode == ServiceStartMode.Disabled) return Refuse(ServiceCode.Disabled);
                return Ok(null, StateFingerprint(o), true);
            default:
                return Refuse(ServiceCode.Failed);
        }
    }

    // ---- apply ----

    public Task<ServiceApplyResult> ApplyAsync(ServiceRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(() => ApplyCore(request, null, cancellationToken), cancellationToken);

    private async Task<ServiceApplyResult> RunAsync(Func<ServiceApplyResult> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private ServiceApplyResult Result(ServiceChangeState state, string code, ServiceChange? change = null, IReadOnlyList<ServiceRef>? names = null) => new(state, code, change, names ?? NoRefs);

    private ServiceApplyResult ApplyCore(ServiceRequest request, Guid? revertOf, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var preview = PreviewCore(request, out _);
        if (preview.IsRefused) return Result(ServiceChangeState.Refused, preview.RefusalCode!, null, preview.BlockedBy);
        if (!preview.WillChange) return Result(ServiceChangeState.Refused, ServiceCode.AlreadySet);
        if (request.ExpectedFingerprint is { } expected && expected != preview.Fingerprint) return Result(ServiceChangeState.Refused, ServiceCode.Stale);

        ct.ThrowIfCancellationRequested();
        var change = new ServiceChange(Guid.NewGuid(), request.Kind, request.Name, preview.DisplayName, preview.BeforeMode,
            request.Kind == ServiceChangeKind.SetStartMode ? request.Mode : null, preview.State, preview.Fingerprint, null, revertOf);
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Result(ServiceChangeState.Refused, ServiceCode.JournalFailed); }

        // The journal write took time: look at the service again and stop if it is not what the person reviewed.
        var again = PreviewCore(request with { ExpectedFingerprint = preview.Fingerprint }, out _);
        if (again.IsRefused || !again.WillChange || again.Fingerprint != preview.Fingerprint)
        {
            var stale = again.IsRefused ? again.RefusalCode! : ServiceCode.Stale;
            try { journal.Complete(change.Id, ServiceChangeState.Refused, stale, null, clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing was changed; the entry stays open */ }
            return Result(ServiceChangeState.Refused, stale, null, again.BlockedBy);
        }

        ServiceChangeState state; string code; string? afterFingerprint; IReadOnlyList<ServiceRef> names;
        try { (state, code, afterFingerprint, names) = Execute(request, preview.BeforeMode, () => MarkWritten(change.Id)); }
        catch (Exception e) when (e is not OperationCanceledException) { (state, code, afterFingerprint, names) = (ServiceChangeState.Failed, ServiceCode.Failed, null, NoRefs); } // the entry is begun, so it must be completed
        var lost = JournalGuard.CompleteLost(() => journal.Complete(change.Id, state, code, afterFingerprint, clock.GetUtcNow())); // the change is made; a lost line shows in the history as interrupted
        return Result(state, code, change with { AfterFingerprint = afterFingerprint }, names) with { RecordLost = lost };
    }

    private void MarkWritten(Guid id)
    {
        try { journal.Written(id, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays unconfirmed if the end line is lost too */ }
    }

    private (ServiceChangeState State, string Code, string? After, IReadOnlyList<ServiceRef> Names) Execute(ServiceRequest request, ServiceStartMode? beforeMode, Action written)
    {
        switch (request.Kind)
        {
            case ServiceChangeKind.SetStartMode:
            {
                var result = control.SetStartMode(request.Name, request.Mode!.Value, beforeMode);
                if (!result.Ok) return (ServiceChangeState.Failed, result.Code, null, result.Names ?? NoRefs);
                written();
                var after = control.Observe(request.Name).Service;
                return after is not null && after.StartMode == request.Mode ? (ServiceChangeState.Applied, ServiceCode.Ok, ConfigFingerprint(after), NoRefs) : (ServiceChangeState.Failed, ServiceCode.VerifyFailed, null, NoRefs);
            }
            case ServiceChangeKind.Start:
            {
                var (state, code, names) = StartAndWait(request.Name, written);
                return (state, code, null, names);
            }
            case ServiceChangeKind.Stop:
            {
                var (state, code, names) = StopAndWait(request.Name, written);
                return (state, code, null, names);
            }
            case ServiceChangeKind.Restart:
            {
                var (stopState, stopCode, stopNames) = StopAndWait(request.Name, written);
                if (stopState != ServiceChangeState.Applied) return (stopState, stopCode, null, stopNames);
                var (startState, startCode, startNames) = StartAndWait(request.Name, written);
                return startState == ServiceChangeState.Applied ? (startState, startCode, null, startNames) : (ServiceChangeState.Failed, ServiceCode.RestartStartFailed, null, startNames);
            }
            default:
                return (ServiceChangeState.Failed, ServiceCode.Failed, null, NoRefs);
        }
    }

    private (ServiceChangeState, string, IReadOnlyList<ServiceRef>) StartAndWait(string name, Action written)
    {
        var result = control.Start(name);
        if (!result.Ok) return (ServiceChangeState.Failed, result.Code, result.Names ?? NoRefs);
        written();
        var reached = WaitFor(name, o => o.RunState == ServiceRunState.Running, o => o.RunState == ServiceRunState.Stopped);
        return reached switch
        {
            WaitOutcome.Reached => (ServiceChangeState.Applied, ServiceCode.Ok, NoRefs),
            WaitOutcome.Gone => (ServiceChangeState.Failed, ServiceCode.Refused, NoRefs),
            _ => (ServiceChangeState.Failed, ServiceCode.StartTimeout, NoRefs),
        };
    }

    private (ServiceChangeState, string, IReadOnlyList<ServiceRef>) StopAndWait(string name, Action written)
    {
        var result = control.Stop(name);
        if (!result.Ok) return (ServiceChangeState.Failed, result.Code, result.Names ?? NoRefs);
        written();
        var reached = WaitFor(name, o => o.RunState == ServiceRunState.Stopped, _ => false);
        return reached == WaitOutcome.Reached ? (ServiceChangeState.Applied, ServiceCode.Ok, NoRefs) : (ServiceChangeState.Failed, ServiceCode.StopTimeout, NoRefs);
    }

    private enum WaitOutcome { Reached, Gone, Timeout }

    /// <summary>Polls the service until <paramref name="done"/> holds. <paramref name="failed"/> ends the wait early when the service fell back to the state it came from.</summary>
    private WaitOutcome WaitFor(string name, Func<ServiceObservation, bool> done, Func<ServiceObservation, bool> failed)
    {
        for (var attempt = 0; attempt < WaitAttempts; attempt++)
        {
            if (control.Observe(name).Service is { } now)
            {
                if (done(now)) return WaitOutcome.Reached;
                if (failed(now)) return WaitOutcome.Gone;
            }
            if (wait > TimeSpan.Zero) Thread.Sleep(wait / WaitAttempts);
        }
        return WaitOutcome.Timeout;
    }

    // ---- revert ----

    /// <summary>Whether the change can still be undone: it was a startup type change that was applied, it was not undone already, and nobody changed the startup type since.</summary>
    public bool CanRevert(ServiceChangeRecord record) => CanRevertAll([record])[0];

    public IReadOnlyList<bool> CanRevertAll(IReadOnlyList<ServiceChangeRecord> records)
    {
        var history = History(int.MaxValue);
        return records.Select(r => RevertRequest(r, history) is { } request && !Preview(request).IsRefused).ToList();
    }

    public Task<ServiceApplyResult> RevertAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var history = History(int.MaxValue);
            var record = history.FirstOrDefault(r => r.Change.Id == changeId);
            var request = record is null ? null : RevertRequest(record, history);
            return request is null || Preview(request).IsRefused ? Result(ServiceChangeState.Refused, ServiceCode.CannotRevert) : ApplyCore(request, record!.Change.Id, cancellationToken);
        }, cancellationToken);

    private static ServiceRequest? RevertRequest(ServiceChangeRecord record, IReadOnlyList<ServiceChangeRecord> history)
    {
        if (record.State != ServiceChangeState.Applied) return null;
        var c = record.Change;
        if (c.Kind != ServiceChangeKind.SetStartMode || c.RevertOf is not null) return null;
        if (history.Any(r => r.Change.RevertOf == c.Id && r.State == ServiceChangeState.Applied)) return null;
        if (c.BeforeMode is not { } before || !Modes.Contains(before) || c.AfterFingerprint is null || !IsValidName(c.Name)) return null;
        return new(ServiceChangeKind.SetStartMode, c.Name, before, c.AfterFingerprint);
    }
}

/// <summary>The services of the machine definitions as recommendations for the Services page.</summary>
public static class ServiceRecommendations
{
    /// <summary>One recommendation per service name. The suggested type is the recommended choice of the definition, otherwise the first choice that differs from Windows' default.</summary>
    public static IReadOnlyDictionary<string, ServiceRecommendation> From(IEnumerable<MachineSettingDefinition> definitions)
    {
        var map = new Dictionary<string, ServiceRecommendation>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in definitions)
        {
            if (d.TargetKind != MachineTargetKind.ServiceStartType || d.Service is null) continue;
            var choices = d.Choices.Where(c => c.Start is not null).ToList();
            var defaultStart = choices.FirstOrDefault(c => c.Id == d.DefaultChoiceId)?.Start;
            var pick = choices.FirstOrDefault(c => c.Id == d.RecommendedChoiceId) ?? choices.FirstOrDefault(c => c.Start != defaultStart);
            if (pick?.Start is not { } start) continue;
            map.TryAdd(d.Service.ServiceName, new(d.Service.ServiceName, d.Id, d.Risk, start switch
            {
                ServiceStartKind.Automatic => ServiceStartMode.Automatic,
                ServiceStartKind.AutomaticDelayed => ServiceStartMode.AutomaticDelayed,
                ServiceStartKind.Manual => ServiceStartMode.Manual,
                _ => ServiceStartMode.Disabled,
            }, d.SourceClass.ToString()));
        }
        return map;
    }
}
