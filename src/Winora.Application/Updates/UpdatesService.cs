// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Winora.Domain.Elevation;
namespace Winora.Application;

/// <summary>
/// Windows Update modes. A mode is a full set of values of the machine catalog (every definition in <see cref="UpdateIds.All"/> gets a choice), applied through the Optimize service as
/// one named group: preview of every value, journal line before each write, compare-and-set, read-back, one undo for the group. Only policy values of the catalog
/// change. Services, tasks, the update cache and other policy keys are never touched.
/// </summary>
public sealed class UpdatesService(OptimizeService optimize, IOsBuildProvider build, IUpdateFactsSource facts, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private const string Default = "default";

    // ---- what the PC has now ----

    public UpdatesState Read()
    {
        var items = optimize.List(UpdateIds.All);
        var f = facts.Read();
        var now = clock.GetUtcNow();
        var pause = new UpdatePause(
            f.QualityPauseUntilUtc is { } q && q > now ? q : null,
            f.FeaturePauseUntilUtc is { } fe && fe > now ? fe : null);
        var displayVersion = build.DisplayVersion;
        var rows = PolicyRows(items, f);
        var organization = rows.Any(r => r.Source != UpdateValueSource.Winora);
        if (items.Count != UpdateIds.All.Count)
            return new(false, [], null, null, false, organization, rows, pause, build.CurrentBuild?.ToString(CultureInfo.InvariantCulture), displayVersion, build.UpdateRevision, f);

        var current = items.ToDictionary(i => i.Definition.Id, i => i.CurrentChoiceId, StringComparer.Ordinal);
        var constraint = items.Select(i => i.Problem).FirstOrDefault(p => p is OptimizeCode.UnsupportedBuild or OptimizeCode.UnsupportedEdition);
        UpdateModeId? active = UpdateModes.Normal.Concat(UpdateModes.Advanced).Where(m => Matches(m, current)).Cast<UpdateModeId?>().FirstOrDefault();
        var allDefault = current.Values.All(v => v == Default);
        var modes = UpdateModes.Normal.Concat(UpdateModes.Advanced).Select(m =>
        {
            var code = constraint ?? (m == UpdateModeId.Pin && ReleaseChoice(items) is null ? UpdateCode.ReleaseUnknown : null);
            return new UpdateModeState(m, code is null, code, m == active);
        }).ToList();
        var options = active is { } a ? OptionsOf(a, current) : null;
        return new(true, modes, active, options, active is null && !allDefault, organization, rows, pause, build.CurrentBuild?.ToString(CultureInfo.InvariantCulture), displayVersion, build.UpdateRevision, f);
    }

    private static bool Is(IReadOnlyDictionary<string, string?> c, string id, string choice) => c.TryGetValue(id, out var v) && v == choice;
    private static bool IsSet(IReadOnlyDictionary<string, string?> c, string id) => c.TryGetValue(id, out var v) && v is not null && v != Default;
    private static bool OthersDefault(IReadOnlyDictionary<string, string?> c, params string[] except) =>
        c.Where(kv => !except.Contains(kv.Key, StringComparer.Ordinal)).All(kv => kv.Value == Default);

    /// <summary>Whether the live choices are exactly what the mode sets. The driver exclusion is free in the modes that offer it.</summary>
    internal static bool Matches(UpdateModeId mode, IReadOnlyDictionary<string, string?> c) => mode switch
    {
        UpdateModeId.WindowsDefault => OthersDefault(c),
        UpdateModeId.NoDrivers => Is(c, UpdateIds.ExcludeDrivers, "exclude") && OthersDefault(c, UpdateIds.ExcludeDrivers),
        UpdateModeId.Security => Is(c, UpdateIds.DeferFeature, "on") && Is(c, UpdateIds.DeferQuality, "on") && IsSet(c, UpdateIds.DeferFeatureDays) && IsSet(c, UpdateIds.DeferQualityDays)
            && OthersDefault(c, UpdateIds.ExcludeDrivers, UpdateIds.DeferFeature, UpdateIds.DeferFeatureDays, UpdateIds.DeferQuality, UpdateIds.DeferQualityDays),
        UpdateModeId.Pin => Is(c, UpdateIds.PinRelease, "pin") && IsSet(c, UpdateIds.PinReleaseName) && Is(c, UpdateIds.PinProduct, "win11")
            && OthersDefault(c, UpdateIds.ExcludeDrivers, UpdateIds.PinRelease, UpdateIds.PinReleaseName, UpdateIds.PinProduct),
        UpdateModeId.Notify => Is(c, UpdateIds.Notify, "ask_download") && OthersDefault(c, UpdateIds.ExcludeDrivers, UpdateIds.Notify),
        UpdateModeId.AutomaticOff => Is(c, UpdateIds.AutomaticOff, "off") && OthersDefault(c, UpdateIds.ExcludeDrivers, UpdateIds.AutomaticOff),
        _ => false,
    };

    private static UpdateModeOptions OptionsOf(UpdateModeId mode, IReadOnlyDictionary<string, string?> c)
    {
        static int? Days(string? choice) => choice is { Length: > 1 } && choice[0] == 'd' && int.TryParse(choice.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        var defaults = new UpdateModeOptions();
        return mode == UpdateModeId.Security
            ? new(Days(c.GetValueOrDefault(UpdateIds.DeferFeatureDays)) ?? defaults.FeatureDays, Days(c.GetValueOrDefault(UpdateIds.DeferQualityDays)) ?? defaults.QualityDays, Is(c, UpdateIds.ExcludeDrivers, "exclude"))
            : defaults with { ExcludeDrivers = Is(c, UpdateIds.ExcludeDrivers, "exclude") };
    }

    private IReadOnlyList<UpdatePolicyRow> PolicyRows(IReadOnlyList<OptimizeItem> items, UpdateFacts f)
    {
        var rows = new List<UpdatePolicyRow>();
        foreach (var v in f.PolicyValues)
        {
            var item = items.FirstOrDefault(i => i.Definition.Registry is { } r && string.Equals(r.SubKey, v.KeyPath, StringComparison.OrdinalIgnoreCase) && string.Equals(r.ValueName, v.Name, StringComparison.OrdinalIgnoreCase));
            // A value of the catalog counts as Winora's own while the Optimize service does not see it as managed (it equals what Winora last wrote).
            var own = item is { Problem: null };
            rows.Add(new(v.KeyPath, v.Name, v.Display, own ? UpdateValueSource.Winora : UpdateValueSource.Organization));
        }
        foreach (var v in f.DeviceManagementValues) rows.Add(new(v.KeyPath, v.Name, v.Display, UpdateValueSource.DeviceManagement));
        return rows;
    }

    // ---- preview ----

    public UpdatePreview Preview(UpdateModeId mode, UpdateModeOptions? options = null)
    {
        options ??= new();
        var items = optimize.List(UpdateIds.All);
        if (items.Count != UpdateIds.All.Count) return Refused(mode, options, OptimizeCode.NoCatalog);
        var choices = ChoicesFor(mode, options, items, out var code);
        if (choices is null) return Refused(mode, options, code!);
        var lines = new List<UpdateLine>();
        foreach (var id in UpdateIds.All)
        {
            var item = items.First(i => i.Definition.Id == id);
            var want = choices[id];
            var desired = item.Definition.Choices.FirstOrDefault(c => c.Id == want)?.EffectiveValue;
            if (desired is null) return Refused(mode, options, UpdateCode.MissingDefinition);
            var before = item.Current is { } cur ? StoredValue.From(cur) : null;
            // A value that policy manages and that already has the wanted data needs no change and does not block the mode.
            if (item.Problem == OptimizeCode.ManagedByPolicy && item.Current is { } managed && managed.Equals(desired))
            {
                lines.Add(new(item.Definition, item.CurrentChoiceId, before, want, false, null));
                continue;
            }
            if (item.Problem is { } problem) { lines.Add(new(item.Definition, item.CurrentChoiceId, before, want, false, problem)); continue; }
            lines.Add(new(item.Definition, item.CurrentChoiceId, before, want, !item.Current!.Equals(desired), null));
        }
        var refused = lines.FirstOrDefault(l => l.RefusalCode is not null);
        return new(mode, options, lines, refused?.RefusalCode, refused?.Definition.Id);
    }

    private static UpdatePreview Refused(UpdateModeId mode, UpdateModeOptions options, string code) => new(mode, options, [], code, null);

    /// <summary>The choice for every definition of the mode, or null with a code.</summary>
    private Dictionary<string, string>? ChoicesFor(UpdateModeId mode, UpdateModeOptions o, IReadOnlyList<OptimizeItem> items, out string? code)
    {
        code = null;
        var c = UpdateIds.All.ToDictionary(id => id, _ => Default, StringComparer.Ordinal);
        switch (mode)
        {
            case UpdateModeId.WindowsDefault:
                break;
            case UpdateModeId.NoDrivers:
                c[UpdateIds.ExcludeDrivers] = "exclude";
                break;
            case UpdateModeId.Security:
                if (!UpdateModeOptions.FeatureDaysSteps.Contains(o.FeatureDays) || !UpdateModeOptions.QualityDaysSteps.Contains(o.QualityDays)) { code = UpdateCode.InvalidOption; return null; }
                c[UpdateIds.DeferFeature] = "on"; c[UpdateIds.DeferFeatureDays] = "d" + o.FeatureDays.ToString(CultureInfo.InvariantCulture);
                c[UpdateIds.DeferQuality] = "on"; c[UpdateIds.DeferQualityDays] = "d" + o.QualityDays.ToString(CultureInfo.InvariantCulture);
                if (o.ExcludeDrivers) c[UpdateIds.ExcludeDrivers] = "exclude";
                break;
            case UpdateModeId.Pin:
                if (ReleaseChoice(items) is not { } release) { code = UpdateCode.ReleaseUnknown; return null; }
                c[UpdateIds.PinRelease] = "pin"; c[UpdateIds.PinReleaseName] = release; c[UpdateIds.PinProduct] = "win11";
                if (o.ExcludeDrivers) c[UpdateIds.ExcludeDrivers] = "exclude";
                break;
            case UpdateModeId.Notify:
                c[UpdateIds.Notify] = "ask_download";
                if (o.ExcludeDrivers) c[UpdateIds.ExcludeDrivers] = "exclude";
                break;
            case UpdateModeId.AutomaticOff:
                c[UpdateIds.AutomaticOff] = "off";
                break;
            default:
                code = UpdateCode.InvalidOption; return null;
        }
        return c;
    }

    /// <summary>The choice of the release name definition that equals the running feature release (for example <c>r24h2</c>), or null when this release is not in the list.</summary>
    internal string? ReleaseChoice(IReadOnlyList<OptimizeItem> items)
    {
        if (build.DisplayVersion is not { Length: > 0 } version) return null;
        var item = items.FirstOrDefault(i => i.Definition.Id == UpdateIds.PinReleaseName);
        return item?.Definition.Choices.FirstOrDefault(c => c.Value is { Kind: MachineValueKind.String } v && string.Equals(v.Text, version, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>The release name that Pin would use (the running release), or null when unknown or not in the list.</summary>
    public string? PinTargetRelease() => ReleaseChoice(optimize.List([UpdateIds.PinReleaseName])) is not null ? build.DisplayVersion : null;

    // ---- apply ----

    public async Task<UpdateApplyResult> ApplyAsync(UpdateModeId mode, UpdateModeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var preview = Preview(mode, options);
        if (preview.IsRefused) return new(false, preview.RefusalCode!, null, 0);
        if (!preview.WillChange) return new(false, OptimizeCode.AlreadySet, null, 0);
        var requests = preview.Lines.Where(l => l.WillChange).Select(l => new OptimizeRequest(l.Definition.Id, l.AfterChoiceId, l.Before)).ToList();
        var results = await optimize.ApplyGroupAsync(requests, UpdateModes.Label(mode), cancellationToken).ConfigureAwait(false);
        var applied = results.Count(r => r.Ok);
        var group = results.Select(r => r.Change?.Group).FirstOrDefault(g => g is not null);
        var failed = results.FirstOrDefault(r => !r.Ok);
        return failed is null && applied > 0 ? new(true, OptimizeCode.Ok, group, applied) : new(false, failed?.Code ?? OptimizeCode.Failed, group, applied);
    }

    // ---- history and undo ----

    /// <summary>The newest mode changes and their undos, newest first.</summary>
    public IReadOnlyList<UpdateChangeGroup> History(int max)
    {
        if (max <= 0) return [];
        var records = optimize.History(int.MaxValue).Where(r => r.Change.Group is not null && r.Change.Label?.StartsWith(UpdateModes.LabelPrefix, StringComparison.Ordinal) == true).ToList();
        var groups = new List<UpdateChangeGroup>();
        foreach (var g in records.GroupBy(r => r.Change.Group!.Value).Take(max))
        {
            var isUndo = g.All(r => r.Change.RevertOf is not null);
            groups.Add(new(g.Key, UpdateModes.FromLabel(g.First().Change.Label), g.Max(r => r.BeganUtc), g.Count(r => r.State == OptimizeState.Applied), isUndo, !isUndo && optimize.CanRevertGroup(g.Key)));
        }
        return groups;
    }

    public async Task<UpdateRevertResult> RevertAsync(Guid group, CancellationToken cancellationToken = default)
    {
        var (results, skipped) = await optimize.RevertGroupAsync(group, cancellationToken).ConfigureAwait(false);
        var reverted = results.Count(r => r.Ok);
        if (reverted == 0) return new(false, results.FirstOrDefault(r => !r.Ok)?.Code ?? OptimizeCode.CannotRevert, 0, skipped);
        return skipped == 0 ? new(true, OptimizeCode.Ok, reverted, 0) : new(true, OptimizeCode.GroupPartial, reverted, skipped);
    }
}
