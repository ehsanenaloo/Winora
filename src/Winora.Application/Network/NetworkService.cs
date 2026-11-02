// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>Dismissed: the person closed a record whose outcome was never confirmed. It has no undo.</summary>
public enum NetworkChangeState { Applied, Partial, Failed, Refused, Dismissed }

/// <summary>What happened to one adapter of a change. <see cref="Code"/> is "ok" or a stable code.</summary>
public sealed record NetworkEntryResult(Guid Adapter, string Code);

/// <summary>The planned change of one adapter, with the full previous DNS settings. Written to the journal before anything is changed.</summary>
public sealed record NetworkEntry(Guid Adapter, string AdapterName, DnsSettings Before, DnsSettings After, bool WriteV4, bool WriteV6);

public sealed record DnsChange(Guid Id, string ProviderId, DohMode Doh, IReadOnlyList<NetworkEntry> Entries, Guid? RevertOf);

/// <param name="Written">The journal holds the line that says every write of the change was made (after the last write, before the end line).</param>
public sealed record DnsChangeRecord(DnsChange Change, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, NetworkChangeState? State, string Code, IReadOnlyList<NetworkEntryResult> Results, bool Written = false);

/// <summary>Append-only record of DNS changes. <see cref="Begin"/> must be durable before it returns.</summary>
public interface INetworkJournal
{
    void Begin(DnsChange change, DateTimeOffset atUtc);
    void Complete(Guid id, NetworkChangeState state, string code, IReadOnlyList<NetworkEntryResult> results, DateTimeOffset atUtc);
    /// <summary>Records that every write of the change was made. Called after the last write returned and before the end line; never after only some of the writes.</summary>
    void Written(Guid id, DateTimeOffset atUtc) { }
    /// <summary>The newest changes first.</summary>
    IReadOnlyList<DnsChangeRecord> History(int max);
}

/// <summary>What the user asks for. <see cref="Custom"/> is used only when the provider is "custom". <see cref="AllowVirtual"/> names the virtual adapters the user added on purpose.</summary>
public sealed record DnsRequest(string ProviderId, DohMode Doh, IReadOnlyList<Guid> Adapters, IReadOnlyList<Guid>? AllowVirtual = null, string? Custom = null);

/// <summary>The change for one adapter. <see cref="RefusalCode"/> is set when this adapter cannot be changed.</summary>
public sealed record AdapterPreview(NetworkAdapter Adapter, DnsSettings Before, DnsSettings After, bool WriteV4, bool WriteV6, string? RefusalCode, IReadOnlyList<string> Notes)
{
    public bool WillChange => RefusalCode is null && (WriteV4 || WriteV6);
}

public sealed record NetworkPreview(DnsRequest Request, DnsProvider? Provider, IReadOnlyList<AdapterPreview> Adapters, string? RefusalCode)
{
    public bool IsRefused => RefusalCode is not null;
    public IReadOnlyList<AdapterPreview> Changes => Adapters.Where(a => a.WillChange).ToList();
}

public enum ConnectivityResult { NotChecked, Ok, Failed }

public sealed record NetworkApplyResult(NetworkChangeState State, string Code, DnsChange? Change, IReadOnlyList<NetworkEntryResult> Results, ConnectivityResult Connectivity, bool RecordLost = false)
{
    public bool Ok => State == NetworkChangeState.Applied;
}

/// <summary>
/// DNS provider changes. A change has a preview, one confirmation by the caller, a journal line with the full previous settings before the first write, a compare-and-set
/// per adapter, a read-back and an undo that restores the exact previous settings (including "taken from the network"). A setting that someone else changed after the preview is never
/// overwritten. After a change, one DNS lookup of a fixed name tells whether name resolution still works; the service never reverts by itself.
/// </summary>
public sealed class NetworkService(IDnsAdapters adapters, INetworkJournal journal, IElevationState elevation, DnsProviderCatalog catalog, IConnectivityProbe? probe = null, TimeProvider? clock = null)
{
    public const string ProbeName = "www.msftconnecttest.com";
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
    private const int HistoryRead = int.MaxValue;

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);

    public DnsProviderCatalog Catalog => catalog;
    public bool IsElevated => elevation.IsElevated;
    public bool UnsafeLocation => elevation.UnsafeLocation;

    public IReadOnlyList<DnsChangeRecord> History(int max) => journal.History(max);

    public NetworkSnapshot Read(CancellationToken cancellationToken = default) => adapters.Read(cancellationToken);

    // ---- preview ----

    public NetworkPreview Preview(DnsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = adapters.Read(cancellationToken);
        if (snapshot.Support != DnsSupport.Available) return Refused(request, null, NetworkCode.Unavailable);

        DnsProvider? provider = null;
        List<string> customV4 = [], customV6 = [];
        if (request.ProviderId == DnsProviderCatalog.CustomId)
        {
            if (!DnsAddress.TryParseList(request.Custom, out customV4, out customV6, out var error)) return Refused(request, null, error!);
            if (customV4.Count == 0 && customV6.Count == 0) return Refused(request, null, NetworkCode.NoServers);
            if (request.Doh != DohMode.Off) return Refused(request, null, NetworkCode.DohUnavailable);
        }
        else if (request.ProviderId == DnsProviderCatalog.AutomaticId)
        {
            if (request.Doh != DohMode.Off) return Refused(request, null, NetworkCode.DohUnavailable);
        }
        else
        {
            provider = catalog.Find(request.ProviderId);
            if (provider is null) return Refused(request, null, NetworkCode.UnknownProvider);
            if (request.Doh != DohMode.Off && !provider.SupportsDoh) return Refused(request, provider, NetworkCode.DohUnavailable);
        }
        if (request.Adapters.Count == 0) return Refused(request, provider, NetworkCode.NoAdapter);

        var previews = new List<AdapterPreview>();
        foreach (var id in request.Adapters.Distinct())
        {
            var adapter = snapshot.Adapters.FirstOrDefault(a => a.Id == id);
            if (adapter is null) { previews.Add(Missing(id)); continue; }
            if (adapter.Dns is null) { previews.Add(new(adapter, DnsSettings.Automatic, DnsSettings.Automatic, false, false, adapter.DnsReadCode ?? NetworkCode.ReadFailed, [])); continue; }
            if (adapter.IsVirtual && request.AllowVirtual?.Contains(id) != true) { previews.Add(new(adapter, adapter.Dns, adapter.Dns, false, false, NetworkCode.VirtualAdapter, [])); continue; }
            previews.Add(Build(adapter, provider, request, customV4, customV6));
        }
        return new(request, provider, previews, null);
    }

    private static NetworkPreview Refused(DnsRequest request, DnsProvider? provider, string code) => new(request, provider, [], code);

    private static AdapterPreview Missing(Guid id) =>
        new(new(id, "", "", AdapterKind.Other, null, [], [], [], false, null, null), DnsSettings.Automatic, DnsSettings.Automatic, false, false, NetworkCode.UnknownAdapter, []);

    private static AdapterPreview Build(NetworkAdapter adapter, DnsProvider? provider, DnsRequest request, List<string> customV4, List<string> customV6)
    {
        var before = adapter.Dns!;
        var notes = new List<string>();
        DnsFamily v4, v6;
        if (request.ProviderId == DnsProviderCatalog.AutomaticId)
        {
            v4 = DnsFamily.Automatic; v6 = DnsFamily.Automatic;
        }
        else if (provider is null)
        {
            v4 = customV4.Count > 0 ? Family(customV4, null, DohMode.Off) : before.V4;
            v6 = customV6.Count > 0 && (adapter.HasIpv6 || !before.V6.IsAutomatic) ? Family(customV6, null, DohMode.Off) : before.V6;
            if (customV6.Count == 0 && (adapter.HasIpv6 || !before.V6.IsAutomatic)) notes.Add(NetworkCode.NoteIpv6Unchanged);
        }
        else
        {
            v4 = Family(provider.Ipv4, provider.DohTemplate, request.Doh);
            if (provider.Ipv6.Count == 0)
            {
                v6 = before.V6;
                if (adapter.HasIpv6 || !before.V6.IsAutomatic) notes.Add(NetworkCode.NoteIpv6Unchanged);
            }
            else v6 = adapter.HasIpv6 || !before.V6.IsAutomatic ? Family(provider.Ipv6, provider.DohTemplate, request.Doh) : before.V6;
        }
        var after = new DnsSettings(v4, v6);
        var write4 = !before.V4.SameAs(v4);
        var write6 = !before.V6.SameAs(v6);
        return new(adapter, before, after, write4, write6, write4 || write6 ? null : NetworkCode.AlreadySet, notes);
    }

    private static DnsFamily Family(IReadOnlyList<string> addresses, string? template, DohMode mode) =>
        new(addresses.Select(a => new DnsServer(a, mode == DohMode.Off ? null : template, DohFlag.For(mode))).ToList());

    // ---- apply ----

    /// <summary>Applies the changes of a preview that the caller has shown and the user has confirmed.</summary>
    public async Task<NetworkApplyResult> ApplyAsync(NetworkPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.IsRefused) return Fail(NetworkChangeState.Refused, preview.RefusalCode!);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = preview.Changes.Select(c => new Plan(c.Adapter.Id, c.Adapter.Name, c.Before, c.After, c.WriteV4, c.WriteV6, Exact: true)).ToList();
            var work = await Task.Run(() => RunCore(preview.Request.ProviderId, preview.Request.Doh, plan, null, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            return await Finish(work, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private sealed record Plan(Guid Adapter, string Name, DnsSettings Expected, DnsSettings Target, bool V4, bool V6, bool Exact);

    private static NetworkApplyResult Fail(NetworkChangeState state, string code) => new(state, code, null, [], ConnectivityResult.NotChecked);

    private async Task<NetworkApplyResult> Finish(NetworkApplyResult result, CancellationToken ct)
    {
        if (probe is null || result.Results.All(r => r.Code != NetworkCode.Ok)) return result;
        bool ok;
        try { ok = await probe.CanResolveAsync(ProbeName, ProbeTimeout, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { ok = false; }
        return result with { Connectivity = ok ? ConnectivityResult.Ok : ConnectivityResult.Failed };
    }

    /// <summary>
    /// Shared by apply and undo. <c>Expected</c> is the state the adapter must still have (compare-and-set); for an apply it must match exactly, for an undo it must have the same effect as
    /// the state the earlier change wrote. The journal line is written before the first write.
    /// </summary>
    private NetworkApplyResult RunCore(string providerId, DohMode doh, List<Plan> plan, Guid? revertOf, CancellationToken ct)
    {
        if (!elevation.IsElevated) return Fail(NetworkChangeState.Refused, NetworkCode.NeedsAdmin);
        var snapshot = adapters.Read(ct);
        if (snapshot.Support != DnsSupport.Available) return Fail(NetworkChangeState.Refused, NetworkCode.Unavailable);

        var results = new List<NetworkEntryResult>();
        var entries = new List<NetworkEntry>();
        foreach (var p in plan)
        {
            var current = snapshot.Adapters.FirstOrDefault(a => a.Id == p.Adapter);
            if (current?.Dns is null) { results.Add(new(p.Adapter, NetworkCode.UnknownAdapter)); continue; }
            var unchanged = p.Exact ? current.Dns.SameAs(p.Expected) : current.Dns.EquivalentTo(p.Expected);
            if (!unchanged) { results.Add(new(p.Adapter, p.Exact ? NetworkCode.Stale : NetworkCode.Conflict)); continue; }
            // The journal keeps what is really there now, so an undo writes back the exact bits.
            entries.Add(new(p.Adapter, p.Name, current.Dns, p.Target, p.V4, p.V6));
        }
        if (entries.Count == 0) return new(NetworkChangeState.Refused, results.Count > 0 ? results[0].Code : NetworkCode.NoAdapter, null, results, ConnectivityResult.NotChecked);

        var change = new DnsChange(Guid.NewGuid(), providerId, doh, entries, revertOf);
        try { journal.Begin(change, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail(NetworkChangeState.Refused, NetworkCode.JournalFailed); }

        var writesMade = 0;
        foreach (var entry in entries)
        {
            // Earlier writes and the journal took time: the adapter must still hold what was read for this change.
            var now = adapters.Read(ct).Adapters.FirstOrDefault(a => a.Id == entry.Adapter)?.Dns;
            if (now is null || !now.SameAs(entry.Before)) { results.Add(new(entry.Adapter, NetworkCode.Stale)); continue; }
            var write = adapters.Write(entry.Adapter, entry.After, entry.WriteV4, entry.WriteV6, entry.Before);
            if (!write.Ok)
            {
                // A family may have been written before the other one failed: that part is real and stays recoverable through an undo.
                var left = adapters.Read(ct).Adapters.FirstOrDefault(a => a.Id == entry.Adapter)?.Dns;
                var residue = left is not null && (entry.WriteV4 && !left.V4.EquivalentTo(entry.Before.V4) && left.V4.EquivalentTo(entry.After.V4)
                    || entry.WriteV6 && !left.V6.EquivalentTo(entry.Before.V6) && left.V6.EquivalentTo(entry.After.V6));
                results.Add(new(entry.Adapter, residue ? NetworkCode.PartialWrite : write.Code));
                continue;
            }
            if (++writesMade == entries.Count)
            {
                // The proof that every adapter of the change was written: a record that stops before the last adapter has no such line, so the adapters it did not write never count as its own.
                // When this line is lost the record stays unconfirmed.
                try { journal.Written(change.Id, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the end line follows */ }
            }
            var back = adapters.Read(ct).Adapters.FirstOrDefault(a => a.Id == entry.Adapter)?.Dns;
            var good = back is not null && (!entry.WriteV4 || back.V4.EquivalentTo(entry.After.V4)) && (!entry.WriteV6 || back.V6.EquivalentTo(entry.After.V6));
            results.Add(new(entry.Adapter, good ? NetworkCode.Ok : NetworkCode.VerifyFailed));
        }
        var okCount = results.Count(r => r.Code == NetworkCode.Ok);
        var state = okCount == results.Count ? NetworkChangeState.Applied : okCount > 0 || results.Any(r => r.Code == NetworkCode.PartialWrite) ? NetworkChangeState.Partial : NetworkChangeState.Failed;
        var code = state == NetworkChangeState.Applied ? NetworkCode.Ok : results.First(r => r.Code != NetworkCode.Ok).Code;
        var lost = JournalGuard.CompleteLost(() => journal.Complete(change.Id, state, code, results, clock.GetUtcNow())); // the change is made; a lost line shows in the history as interrupted
        return new(state, code, change, results, ConnectivityResult.NotChecked, lost);
    }

    // ---- interrupted changes ----

    /// <summary>A record with a begin line and no end line.</summary>
    public static bool IsUnconfirmed(DnsChangeRecord record) => record.State is null && record.EndedUtc is null;

    /// <summary>
    /// Closes the records of changes that were begun and never finished. Proven cases only: the journal shows a write as made and every adapter holds the settings the change wrote (Applied, code
    /// reconciled), or every adapter still holds the settings it had before (Failed, interrupted_unchanged). Everything else stays unconfirmed: settings equal to the intended ones without the
    /// written line do not show who wrote them. The person resolves those records (<see cref="DismissAsync"/>, <see cref="RestoreBeforeAsync"/>).
    /// </summary>
    public async Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var done = 0;
                var records = journal.History(HistoryRead).Where(IsUnconfirmed).ToList();
                if (records.Count == 0) return 0;
                var snapshot = adapters.Read(CancellationToken.None);
                if (snapshot.Support != DnsSupport.Available) return 0;
                foreach (var record in records)
                {
                    var c = record.Change;
                    if (c.Entries.Count == 0) continue;
                    var states = c.Entries.Select(e => (Entry: e, Now: snapshot.Adapters.FirstOrDefault(a => a.Id == e.Adapter)?.Dns)).ToList();
                    if (states.Any(s => s.Now is null)) continue;
                    var wroteAll = states.All(s => (!s.Entry.WriteV4 || s.Now!.V4.EquivalentTo(s.Entry.After.V4)) && (!s.Entry.WriteV6 || s.Now!.V6.EquivalentTo(s.Entry.After.V6)));
                    var oldAll = states.All(s => (!s.Entry.WriteV4 || s.Now!.V4.EquivalentTo(s.Entry.Before.V4)) && (!s.Entry.WriteV6 || s.Now!.V6.EquivalentTo(s.Entry.Before.V6)));
                    NetworkChangeState state; string code;
                    if (record.Written && wroteAll && !oldAll) (state, code) = (NetworkChangeState.Applied, NetworkCode.Reconciled);
                    else if (oldAll && !wroteAll) (state, code) = (NetworkChangeState.Failed, NetworkCode.InterruptedUnchanged);
                    else continue;
                    try { journal.Complete(c.Id, state, code, [], clock.GetUtcNow()); done++; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record stays open */ }
                }
                return done;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>The person keeps the current settings and closes an unconfirmed record. Nothing is written and the record gets no undo (Dismissed).</summary>
    public async Task<NetworkApplyResult> DismissAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = journal.History(HistoryRead).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return Fail(NetworkChangeState.Refused, NetworkCode.NotUnconfirmed);
            try { journal.Complete(changeId, NetworkChangeState.Dismissed, NetworkCode.Dismissed, [], clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail(NetworkChangeState.Refused, NetworkCode.JournalFailed); }
            return new(NetworkChangeState.Dismissed, NetworkCode.Dismissed, record.Change, [], ConnectivityResult.NotChecked);
        }
        finally { gate.Release(); }
    }

    /// <summary>The adapters and families of an unconfirmed record that do not hold the settings from before.</summary>
    private static List<Plan> RestorePlan(DnsChangeRecord record, NetworkSnapshot snapshot)
    {
        var plan = new List<Plan>();
        if (!IsUnconfirmed(record)) return plan;
        // One adapter that cannot be read now (unplugged, disabled) makes the whole restore wait: closing the record after a subset would drop that adapter's way back.
        if (record.Change.Entries.Any(e => snapshot.Adapters.FirstOrDefault(a => a.Id == e.Adapter)?.Dns is null)) return plan;
        foreach (var e in record.Change.Entries)
        {
            var current = snapshot.Adapters.FirstOrDefault(a => a.Id == e.Adapter)!.Dns!;
            var v4 = e.WriteV4 && !current.V4.EquivalentTo(e.Before.V4);
            var v6 = e.WriteV6 && !current.V6.EquivalentTo(e.Before.V6);
            if (v4 || v6) plan.Add(new(e.Adapter, e.AdapterName, current, e.Before, v4, v6, Exact: true));
        }
        return plan;
    }

    private static string Text(DnsSettings d, bool v4, bool v6) =>
        string.Join(';', (v4 ? d.V4.Servers : []).Concat(v6 ? d.V6.Servers : []).Select(x => $"{x.Address}|{x.DohTemplate}|{x.DohFlags}")) is { Length: > 0 } t ? t : "automatic";

    private static string TokenOf(DnsChangeRecord record, List<Plan> plan) =>
        RecoveryToken.Of("dns", record.Change.Id, string.Join("", plan.Select(p => $"{p.Adapter}|{p.V4}|{p.V6}|{Text(p.Expected, p.V4, p.V6)}|{Text(p.Target, p.V4, p.V6)}")));

    /// <summary>What the restore would do, read now: each adapter with its DNS servers now and the servers they return to. Null when it is not possible. The token binds the confirmation to what was shown.</summary>
    public RecoveryPlan? PlanRestoreBefore(DnsChangeRecord record, NetworkSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!elevation.IsElevated) return null;
        var plan = RestorePlan(record, snapshot ?? adapters.Read(CancellationToken.None));
        if (plan.Count == 0) return null;
        return new(plan.Select(p => new RecoveryLine(p.Name, Text(p.Expected, p.V4, p.V6), Text(p.Target, p.V4, p.V6))).ToList(), TokenOf(record, plan));
    }

    /// <summary>Whether the settings from before can be put back for an unconfirmed record.</summary>
    public bool CanRestoreBefore(DnsChangeRecord record, NetworkSnapshot? snapshot = null) =>
        elevation.IsElevated && IsUnconfirmed(record) && RestorePlan(record, snapshot ?? adapters.Read(CancellationToken.None)).Count > 0;

    /// <summary>
    /// The person puts the DNS settings from before back and closes the unconfirmed record. It is an ordinary change (shown as custom or automatic DNS) with its own record and undo; each
    /// adapter is written only while it holds what was just read. The unconfirmed record is then Dismissed with the code resolved_restored.
    /// </summary>
    public async Task<NetworkApplyResult> RestoreBeforeAsync(Guid changeId, CancellationToken cancellationToken = default, string? expectedToken = null)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = journal.History(HistoryRead).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null || !IsUnconfirmed(record)) return Fail(NetworkChangeState.Refused, NetworkCode.NotUnconfirmed);
            var snapshot = adapters.Read(cancellationToken);
            var plan = RestorePlan(record, snapshot);
            if (plan.Count == 0) return Fail(NetworkChangeState.Refused, NetworkCode.CannotRevert);
            if (expectedToken is not null && expectedToken != TokenOf(record, plan)) return Fail(NetworkChangeState.Refused, NetworkCode.Stale);
            var automatic = plan.All(p => (!p.V4 || p.Target.V4.IsAutomatic) && (!p.V6 || p.Target.V6.IsAutomatic));
            var result = await Task.Run(() => RunCore(automatic ? DnsProviderCatalog.AutomaticId : DnsProviderCatalog.CustomId, DohMode.Off, plan, null, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            if (result.State == NetworkChangeState.Applied)
            {
                try { journal.Complete(changeId, NetworkChangeState.Dismissed, NetworkCode.ResolvedRestored, [], clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result = result with { RecordLost = true }; }
            }
            return await Finish(result, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // ---- undo ----

    /// <summary>Whether any adapter of the change still has the settings the change wrote.</summary>
    public bool CanRevert(DnsChangeRecord record, NetworkSnapshot? snapshot = null) => RevertPlan(record, snapshot ?? adapters.Read(CancellationToken.None)).Count > 0;

    public IReadOnlyList<bool> CanRevertAll(IReadOnlyList<DnsChangeRecord> records)
    {
        var snapshot = adapters.Read(CancellationToken.None);
        return records.Select(r => RevertPlan(r, snapshot).Count > 0).ToList();
    }

    private static List<Plan> RevertPlan(DnsChangeRecord record, NetworkSnapshot snapshot)
    {
        var plan = new List<Plan>();
        // An undo is not undone again: that would put back the settings the person just removed.
        // Only a change that is known to have written something can be undone: an unconfirmed, failed, refused or dismissed record owns nothing.
        if (record.State is not (NetworkChangeState.Applied or NetworkChangeState.Partial) || record.Change.RevertOf is not null) return plan;
        foreach (var e in record.Change.Entries)
        {
            var current = snapshot.Adapters.FirstOrDefault(a => a.Id == e.Adapter)?.Dns;
            if (current is null) continue;
            // Only the families this change wrote, and only while they still have what it wrote.
            var v4 = e.WriteV4 && current.V4.EquivalentTo(e.After.V4) && !current.V4.EquivalentTo(e.Before.V4);
            var v6 = e.WriteV6 && current.V6.EquivalentTo(e.After.V6) && !current.V6.EquivalentTo(e.Before.V6);
            if (!v4 && !v6) continue;
            plan.Add(new(e.Adapter, e.AdapterName, current, e.Before, v4, v6, Exact: false));
        }
        return plan;
    }

    /// <summary>Restores the previous settings of every adapter that still has what the change wrote. Other adapters and families are left alone.</summary>
    public async Task<NetworkApplyResult> RevertAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = journal.History(HistoryRead).FirstOrDefault(r => r.Change.Id == changeId);
            if (record is null) return Fail(NetworkChangeState.Refused, NetworkCode.CannotRevert);
            var snapshot = adapters.Read(cancellationToken);
            var plan = RevertPlan(record, snapshot);
            if (plan.Count == 0) return Fail(NetworkChangeState.Refused, NetworkCode.CannotRevert);
            var result = await Task.Run(() => RunCore(record.Change.ProviderId, record.Change.Doh, plan, record.Change.Id, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            return await Finish(result, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
