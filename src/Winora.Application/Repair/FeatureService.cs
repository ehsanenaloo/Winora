// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>
/// One optional feature with the reviewed class. A feature that is not in the reviewed list has <see cref="Known"/> false and cannot be changed.
/// <see cref="BlockedBy"/> names the enabled features that stop a disable.
/// </summary>
public sealed record WindowsFeature(string Name, FeatureState State, string RawState, FeatureClassEntry? Info, IReadOnlyList<string> BlockedBy)
{
    public bool Known => Info is not null;
    public FeatureClass Class => Info?.Class ?? FeatureClass.Unknown;
    public bool RestartPending => State is FeatureState.EnablePending or FeatureState.DisablePending;
    public bool IsOn => State is FeatureState.Enabled or FeatureState.EnablePending;
    public bool CanEnable => Known && Class != FeatureClass.Protected && State == FeatureState.Disabled;
    public bool CanDisable => Known && Class != FeatureClass.Protected && State == FeatureState.Enabled && BlockedBy.Count == 0;
}

public sealed record FeatureSnapshot(string Code, IReadOnlyList<WindowsFeature> Features)
{
    public bool Ok => Code == RepairCode.Ok;
    public WindowsFeature? Find(string name) => Features.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed record FeaturePreview(string Name, bool Enable, WindowsFeature? Feature, string? Command, string? RefusalCode)
{
    public bool IsRefused => RefusalCode is not null;
}

public sealed record FeatureApplyResult(RepairResult Run, string Code, FeatureState? After, bool RestartNeeded)
{
    public bool Ok => Code == RepairCode.Ok;
}

/// <summary>
/// Optional Windows features through the closed repair list (decision DEC-IW-041). The list is read fresh before every change; a feature is changed only when it is in the reviewed list, is not
/// protected, is in the state the page showed, and (for a disable) no blocking feature is on. The state before is recorded in the journal; an undo restores it and only while the feature still
/// has the state Winora left. After a change the list is read again to check the result.
/// </summary>
public sealed class FeatureService(RepairService repair, FeatureClassCatalog classes)
{
    public FeatureClassCatalog Classes => classes;

    public async Task<FeatureSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await repair.RunAsync(new(RepairTool.FeatureList), null, null, null, cancellationToken).ConfigureAwait(false);
        return result.Run is null ? new(result.Code, []) : Snapshot(result.Run);
    }

    private FeatureSnapshot Snapshot(RepairRun run)
    {
        if (run.Outcome != RepairOutcome.Succeeded) return new(run.Outcome == RepairOutcome.Cancelled ? RepairCode.Cancelled : RepairCode.FeaturesUnreadable, []);
        var parsed = DismFeatureParser.Parse(run.Output);
        if (parsed.Count == 0) return new(RepairCode.FeaturesUnreadable, []);
        return new(RepairCode.Ok, Build(parsed));
    }

    private IReadOnlyList<WindowsFeature> Build(IReadOnlyList<ParsedFeature> parsed)
    {
        var on = parsed.Where(p => p.State is FeatureState.Enabled or FeatureState.EnablePending).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return parsed.Select(p =>
        {
            var info = classes.Find(p.Name);
            var blocked = info is null ? [] : info.DisableBlockedBy.Where(on.Contains).ToList();
            return new WindowsFeature(p.Name, p.State, p.RawState, info, blocked);
        }).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public FeaturePreview Preview(FeatureSnapshot snapshot, string name, bool enable)
    {
        var feature = snapshot.Find(name);
        if (feature is null) return new(name, enable, null, null, RepairCode.UnknownFeature);
        var refusal = Refusal(feature, enable);
        var request = new RepairRequest(enable ? RepairTool.FeatureEnable : RepairTool.FeatureDisable, feature.Name);
        var command = refusal is null ? repair.Preview(request).Command : null;
        return new(feature.Name, enable, feature, command, refusal ?? (command is null ? RepairCode.InvalidRequest : null));
    }

    private static string? Refusal(WindowsFeature f, bool enable)
    {
        if (!f.Known) return RepairCode.UnknownFeature;
        if (f.Class == FeatureClass.Protected) return RepairCode.ProtectedFeature;
        if (enable) return f.State == FeatureState.Disabled ? null : f.IsOn ? RepairCode.AlreadySet : RepairCode.Stale;
        if (f.BlockedBy.Count > 0) return RepairCode.RequiredBy;
        return f.State == FeatureState.Enabled ? null : f.State == FeatureState.Disabled ? RepairCode.AlreadySet : RepairCode.Stale;
    }

    /// <summary>Changes one feature after the caller has shown the preview and the user has confirmed.</summary>
    public async Task<FeatureApplyResult> ApplyAsync(string name, bool enable, IProgress<RepairProgress>? progress = null, Guid? revertOf = null, CancellationToken cancellationToken = default)
    {
        var fresh = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!fresh.Ok) return new(new(RepairOutcome.NotStarted, fresh.Code, null, null), fresh.Code, null, false);
        var preview = Preview(fresh, name, enable);
        if (preview.IsRefused) return new(new(RepairOutcome.NotStarted, preview.RefusalCode!, null, null), preview.RefusalCode!, preview.Feature?.State, false);
        var feature = preview.Feature!;
        FeatureState? now = null; var restart = false; var good = false;
        // The end line of the journal is written once, after the feature has been read again.
        async Task<RepairRun> Verify(RepairRun ran, Func<Task<RepairRun>> list)
        {
            var after = Snapshot(await list().ConfigureAwait(false));
            now = after.Find(feature.Name)?.State;
            good = now is not null && (enable ? now is FeatureState.Enabled or FeatureState.EnablePending : now is FeatureState.Disabled or FeatureState.DisablePending);
            restart = ran.RestartNeeded || now is FeatureState.EnablePending or FeatureState.DisablePending;
            // The tool said success but the feature is not in the state it should be: the record and the shown run say so too.
            return good ? ran with { RestartNeeded = restart } : ran with { Outcome = RepairOutcome.Failed, Code = RepairCode.VerifyFailed, RestartNeeded = restart };
        }
        var run = await repair.RunAsync(new(enable ? RepairTool.FeatureEnable : RepairTool.FeatureDisable, feature.Name), progress, feature.State.ToString(), revertOf, cancellationToken, Verify).ConfigureAwait(false);
        if (run.Run is null || run.Run.Outcome == RepairOutcome.NotStarted || (run.Run.Outcome != RepairOutcome.Succeeded && run.Code != RepairCode.VerifyFailed)) return new(run, run.Code, feature.State, run.Run?.RestartNeeded ?? false);
        if (good) return new(run, RepairCode.Ok, now, restart);
        return new(run, RepairCode.VerifyFailed, now, restart);
    }

    // ---- undo ----

    /// <summary>The state an undo of this record would restore, or null when the record is not a finished feature change.</summary>
    public static bool IsRevertable(RepairRecord record) =>
        record.Begin.Tool is RepairTool.FeatureEnable or RepairTool.FeatureDisable && record.End is { Outcome: RepairOutcome.Succeeded } && record.Begin.RevertOf is null
        && Enum.TryParse<FeatureState>(record.Begin.PreviousState, out var s) && s is FeatureState.Enabled or FeatureState.Disabled;

    /// <summary>Whether the feature still has the state this change left. Another change by the user or by Windows ends the undo offer.</summary>
    public bool CanRevert(RepairRecord record, FeatureSnapshot snapshot) => CanRevert(record, snapshot, null);

    /// <summary>The same answer for many records: <paramref name="history"/> (newest first, the whole journal) is read once by the caller.</summary>
    public bool CanRevert(RepairRecord record, FeatureSnapshot snapshot, IReadOnlyList<RepairRecord>? history)
    {
        if (!IsRevertable(record) || !snapshot.Ok || IsSuperseded(record, history)) return false;
        var feature = snapshot.Find(record.Begin.Argument ?? "");
        if (feature is null) return false;
        var wrote = record.Begin.Tool == RepairTool.FeatureEnable ? FeatureState.Enabled : FeatureState.Disabled;
        return feature.State == wrote;
    }

    public async Task<FeatureApplyResult> RevertAsync(Guid recordId, IProgress<RepairProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var record = repair.Find(recordId);
        if (record is null || !IsRevertable(record) || IsSuperseded(record)) return Fail(RepairCode.CannotRevert);
        var snapshot = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Ok) return Fail(snapshot.Code);
        if (!CanRevert(record, snapshot)) return Fail(RepairCode.CannotRevert);
        var restoreOn = Enum.Parse<FeatureState>(record.Begin.PreviousState!) == FeatureState.Enabled;
        return await ApplyAsync(record.Begin.Argument!, restoreOn, progress, recordId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the change was undone already, or a newer finished change of the same feature exists: the undo would not go back to what the person did last.</summary>
    private bool IsSuperseded(RepairRecord record, IReadOnlyList<RepairRecord>? history = null)
    {
        foreach (var other in history ?? repair.History(int.MaxValue))
        {
            if (other.Begin.Id == record.Begin.Id) break; // newest first: what follows is older
            if (other.End is not { Outcome: RepairOutcome.Succeeded }) continue;
            if (other.Begin.RevertOf == record.Begin.Id) return true;
            if (other.Begin.Tool is RepairTool.FeatureEnable or RepairTool.FeatureDisable && string.Equals(other.Begin.Argument, record.Begin.Argument, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static FeatureApplyResult Fail(string code) => new(new(RepairOutcome.NotStarted, code, null, null), code, null, false);
}
