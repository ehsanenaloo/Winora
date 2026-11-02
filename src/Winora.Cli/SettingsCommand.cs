// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Winora.Application;
using Winora.Domain;
using static Winora.Cli.CliText;
namespace Winora.Cli;

internal sealed record SettingsOptions(string Verb, string? Id, string? Choice, Guid? OperationId, bool Json, bool Yes, SettingCategory? Category,
    SettingApplyOptions Apply, string? Database, string? SandboxName);

/// <summary>
/// `winora settings ...`: list, show, preview, apply, revert, complete, history, recover. Every write goes through <see cref="ISettingsService"/>
/// (catalog allowlist, journal before mutation, compare-and-set) and only after a review that the user confirmed (or --yes). JSON schemaVersion 1.
/// </summary>
internal static partial class SettingsCommand
{
    public const int SchemaVersion = 1;
    private const int MaxCell = 44;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex SandboxNamePattern();

    /// <summary>Test-only sandbox names are exactly one registry path segment of letters, digits and hyphens (at most 64). Anything else is refused.</summary>
    public static bool IsValidSandboxName(string? name) => name is not null && SandboxNamePattern().IsMatch(name);

    /// <summary>HKCU-relative sandbox root for the hidden test option, or null for the real HKCU. Program uses exactly this value.</summary>
    public static string? SandboxRoot(SettingsOptions o) => o.SandboxName is null ? null : Winora.Infrastructure.Windows.RegistryTargetRules.SandboxPrefix + o.SandboxName;

    /// <summary>Parses the arguments after "settings". Returns false for any malformed input; nothing is partially applied.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out SettingsOptions options)
    {
        options = new("", null, null, null, false, false, null, SettingApplyOptions.Default, null, null);
        if (args.Count == 0) return false;
        var verb = args[0];
        if (verb is not ("list" or "show" or "preview" or "apply" or "revert" or "complete" or "history" or "recover")) return false;
        bool json = false, yes = false, noRefresh = false, restart = false, noRestart = false;
        SettingCategory? category = null; string? database = null, sandbox = null;
        var positional = new List<string>();
        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--json" when !json: json = true; break;
                case "--yes" when !yes: yes = true; break;
                case "--no-refresh" when !noRefresh: noRefresh = true; break;
                case "--restart-explorer" when !restart: restart = true; break;
                case "--no-restart-explorer" when !noRestart: noRestart = true; break;
                case "--category" when category is null && i + 1 < args.Count && Enum.TryParse<SettingCategory>(args[i + 1], true, out var c) && Enum.IsDefined(c): category = c; i++; break;
                case "--database" when database is null && i + 1 < args.Count && args[i + 1].Length > 0: database = args[++i]; break;
                case "--sandbox-root" when sandbox is null && i + 1 < args.Count && IsValidSandboxName(args[i + 1]): sandbox = args[++i]; break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) return false;
                    positional.Add(a); break;
            }
        }
        if (noRefresh && (restart || noRestart) || restart && noRestart) return false;
        if (category is not null && verb != "list") return false;
        if (yes && verb is not ("apply" or "revert" or "complete")) return false;
        if ((noRefresh || restart || noRestart) && verb != "apply") return false;
        string? id = null, choice = null; Guid? op = null;
        switch (verb)
        {
            case "list" or "history" or "recover": if (positional.Count != 0) return false; break;
            case "show": if (positional.Count != 1) return false; id = positional[0]; break;
            case "preview" or "apply": if (positional.Count != 2) return false; id = positional[0]; choice = positional[1]; break;
            default: // revert, complete
                if (positional.Count != 1 || !Guid.TryParse(positional[0], out var g) || g == Guid.Empty) return false; op = g; break;
        }
        var apply = noRefresh ? SettingApplyOptions.WriteOnly : noRestart ? new SettingApplyOptions(true, false) : SettingApplyOptions.Default;
        options = new(verb, id, choice, op, json, yes, category, apply, database, sandbox);
        return true;
    }

    /// <param name="interactive">True when stdin is a terminal. Without --yes a non-interactive run never writes.</param>
    public static async Task<int> RunAsync(SettingsOptions o, ISettingsService service, bool interactive, TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            // Writes need a yes before anything else happens: a script that forgot --yes fails fast and touches nothing.
            if (o.Verb is "apply" or "revert" or "complete" && !o.Yes && !interactive)
            {
                stderr.WriteLine(T("SettingsNeedsYes"));
                return 2;
            }
            return o.Verb switch
            {
                "list" => await ListAsync(o, service, stdout, ct),
                "show" => await ShowAsync(o, service, stdout, ct),
                "preview" => await PreviewAsync(o, service, stdout, ct),
                "apply" => await ApplyAsync(o, service, stdin, stdout, stderr, ct),
                "revert" => await RevertOrCompleteAsync(o, service, stdin, stdout, stderr, ct, complete: false),
                "complete" => await RevertOrCompleteAsync(o, service, stdin, stdout, stderr, ct, complete: true),
                "history" => await HistoryAsync(o, service, stdout, ct),
                _ => await RecoverAsync(o, service, stdout, ct),
            };
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine(T("SettingsCancelled"));
            return ErrorCatalog.ExitCode(ErrorCategory.Cancelled);
        }
        catch (Exception e)
        {
            // Never print exception text; classify to a stable code and show only its localized guidance.
            var error = new ErrorMapper().Map(e);
            stderr.WriteLine(ErrorLine(error));
            return error.ExitCode;
        }
    }

    // ---- read-only verbs ----

    private static async Task<int> ListAsync(SettingsOptions o, ISettingsService service, TextWriter stdout, CancellationToken ct)
    {
        var states = (await service.ReadAllAsync(ct)).Where(s => o.Category is null || s.Definition.Category == o.Category).ToList();
        if (o.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new { SchemaVersion, Kind = "settings.list", Count = states.Count, Items = states.Select(ToJson).ToList() }, JsonOptions));
            return 0;
        }
        stdout.WriteLine(T("SettingsBanner"));
        stdout.WriteLine();
        if (states.Count == 0) { stdout.WriteLine(T("SettingsNone")); return 0; }
        var table = new List<string[]> { new[] { T("ColId"), T("ColCategory"), T("ColCurrent"), T("ColStatus") } };
        table.AddRange(states.Select(s => new[] { s.Definition.Id, s.Definition.Category.ToString(), Cell(CurrentLabel(s)), Cell(StatusText(s)) }));
        WriteTable(stdout, table);
        stdout.WriteLine();
        stdout.WriteLine(F("SettingsListSummary", states.Count));
        return 0;
    }

    private static async Task<int> ShowAsync(SettingsOptions o, ISettingsService service, TextWriter stdout, CancellationToken ct)
    {
        var s = await service.ReadAsync(o.Id!, ct);
        if (o.Json) { stdout.WriteLine(JsonSerializer.Serialize(new { SchemaVersion, Kind = "settings.show", Item = ToJson(s) }, JsonOptions)); return 0; }
        var d = s.Definition;
        stdout.WriteLine(F("SettingsShowTitle", Title(d), d.Id));
        stdout.WriteLine(Describe(d));
        stdout.WriteLine(F("SettingsShowCurrent", CurrentLabel(s)));
        stdout.WriteLine(F("SettingsShowStatus", StatusText(s)));
        stdout.WriteLine(F("SettingsShowLocation", Location(d)));
        stdout.WriteLine(F("SettingsShowRisk", d.Risk, d.Recovery));
        stdout.WriteLine(F("SettingsShowChoices", string.Join(", ", d.Choices.Select(c => $"{c.Id} ({ChoiceLabel(c)})"))));
        stdout.WriteLine(F("SettingsShowDefault", d.DefaultChoiceId));
        if (RestartNote(d) is { } note) stdout.WriteLine(note);
        if (s.Issue is { } issue) stdout.WriteLine(F("SettingsIssue", issue.Error.Code, T(issue.Error.UserMessageKey)));
        return 0;
    }

    private static async Task<int> PreviewAsync(SettingsOptions o, ISettingsService service, TextWriter stdout, CancellationToken ct)
    {
        var state = await service.ReadAsync(o.Id!, ct);
        var plan = await service.PlanAsync(o.Id!, o.Choice!, ct); // plan only; nothing is written
        var d = state.Definition;
        if (o.Json)
        {
            var doc = new
            {
                SchemaVersion, Kind = "settings.preview", Writes = false,
                Plan = new { plan.SettingId, plan.ChoiceId, plan.CurrentChoiceId, plan.WillChange, plan.DefinitionVersion, Location = Location(d), RestartExplorer = d.RequiresExplorerRestart, SignOut = d.RequiresSignOut },
            };
            stdout.WriteLine(JsonSerializer.Serialize(doc, JsonOptions));
            return 0;
        }
        stdout.WriteLine(T("SettingsPreviewBanner"));
        WriteReview(stdout, d, plan, o.Apply);
        return 0;
    }

    private static async Task<int> HistoryAsync(SettingsOptions o, ISettingsService service, TextWriter stdout, CancellationToken ct)
    {
        var items = (await service.HistoryAsync(ct)).OrderByDescending(x => x.AtUtc).ToList();
        if (o.Json) { stdout.WriteLine(JsonSerializer.Serialize(new { SchemaVersion, Kind = "settings.history", Count = items.Count, Items = items.Select(OutcomeJson).ToList() }, JsonOptions)); return 0; }
        if (items.Count == 0) { stdout.WriteLine(T("SettingsHistoryNone")); return 0; }
        foreach (var x in items) stdout.WriteLine(OutcomeLine(x));
        return 0;
    }

    private static async Task<int> RecoverAsync(SettingsOptions o, ISettingsService service, TextWriter stdout, CancellationToken ct)
    {
        var items = await service.RecoverAsync(ct);
        if (o.Json) { stdout.WriteLine(JsonSerializer.Serialize(new { SchemaVersion, Kind = "settings.recover", Count = items.Count, Items = items.Select(OutcomeJson).ToList() }, JsonOptions)); return 0; }
        if (items.Count == 0) { stdout.WriteLine(T("SettingsRecoverNone")); return 0; }
        stdout.WriteLine(F("SettingsRecovered", items.Count));
        foreach (var x in items) stdout.WriteLine(OutcomeLine(x));
        return items.Any(x => x.State is OperationState.Failed or OperationState.Partial) ? 1 : 0;
    }

    // ---- verbs that can write: always review, then confirm ----

    private static async Task<int> ApplyAsync(SettingsOptions o, ISettingsService service, TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var state = await service.ReadAsync(o.Id!, ct);
        var plan = await service.PlanAsync(o.Id!, o.Choice!, ct);
        var review = o.Json ? stderr : stdout;
        WriteReview(review, state.Definition, plan, o.Apply);
        if (!o.Yes && !Confirm(review, stdin, "SettingsConfirmApply")) { stderr.WriteLine(T("SettingsDeclined")); return 1; }
        var outcome = await service.ApplyAsync(plan, o.Apply, ct);
        return Present(o, state.Definition, outcome, o.Apply, stdout, stderr, reverted: false);
    }

    private static async Task<int> RevertOrCompleteAsync(SettingsOptions o, ISettingsService service, TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct, bool complete)
    {
        var history = await service.HistoryAsync(ct);
        var recorded = history.FirstOrDefault(h => h.OperationId == o.OperationId) ?? throw new WinoraException(WinoraError.From(ErrorCode.OperationMissing));
        var state = await service.ReadAsync(recorded.SettingId, ct);
        var d = state.Definition;
        var review = o.Json ? stderr : stdout;
        review.WriteLine(complete ? F("SettingsCompleteReview", Title(d), d.Id) : F("SettingsRevertReview", Title(d), d.Id));
        if (complete) review.WriteLine(T("SettingsExplorerWarning"));
        else
        {
            review.WriteLine(F("SettingsBeforeAfter", Label(d, state.MatchedChoiceId), recorded.BeforeChoiceId is null ? T("SettingsPreviousValue") : Label(d, recorded.BeforeChoiceId)));
            review.WriteLine(F("SettingsShowLocation", Location(d)));
            review.WriteLine(T("SettingsRevertNote"));
        }
        if (!o.Yes && !Confirm(review, stdin, complete ? "SettingsConfirmComplete" : "SettingsConfirmRevert")) { stderr.WriteLine(T("SettingsDeclined")); return 1; }
        var outcome = complete ? await service.CompleteAsync(o.OperationId!.Value, ct) : await service.RevertAsync(o.OperationId!.Value, ct);
        return Present(o, d, outcome, SettingApplyOptions.Default, stdout, stderr, reverted: !complete);
    }

    private static bool Confirm(TextWriter prompt, TextReader stdin, string key)
    {
        prompt.Write(T(key) + " ");
        prompt.Flush();
        var answer = stdin.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static int Present(SettingsOptions o, SettingDefinition d, SettingOutcome outcome, SettingApplyOptions apply, TextWriter stdout, TextWriter stderr, bool reverted)
    {
        var failed = outcome.State is OperationState.Failed or OperationState.Partial or OperationState.Unsupported;
        var exit = 0;
        if (failed) exit = ErrorCatalog.TryGet(outcome.Code, out var info) ? ErrorCatalog.ExitCode(info.Category) : 1;
        if (o.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new { SchemaVersion, Kind = "settings." + o.Verb, Outcome = OutcomeJson(outcome) }, JsonOptions));
            return exit;
        }
        var title = Title(d);
        switch (outcome.State)
        {
            case OperationState.Applied or OperationState.Reverted:
                stdout.WriteLine(F(reverted ? "SettingsReverted" : "SettingsApplied", title, Label(d, outcome.AfterChoiceId)));
                if (!reverted && d.RequiresExplorerRestart && apply.RefreshShell && apply.RestartExplorer) stdout.WriteLine(T("SettingsExplorerRestarted"));
                break;
            case OperationState.AlreadyConfigured: stdout.WriteLine(F("SettingsAlready", title)); break;
            case OperationState.PendingRestart:
                stdout.WriteLine(F("SettingsPending", title));
                if (outcome.NeedsExplorerRestart) stdout.WriteLine(F("SettingsPendingExplorer", outcome.OperationId));
                if (outcome.NeedsSignOut) stdout.WriteLine(T("SettingsPendingSignOut"));
                if (!ErrorCatalog.IsKnown(outcome.Code) && outcome.Code.StartsWith("shell.", StringComparison.Ordinal)) stdout.WriteLine(F("SettingsShellFailed", outcome.Code));
                break;
            default:
                var known = ErrorCatalog.IsKnown(outcome.Code);
                stderr.WriteLine(F("SettingsNotChanged", title, outcome.State));
                stderr.WriteLine(known ? F("DiscoverError", T(ErrorCatalog.ResourceKey(outcome.Code)), outcome.Code) : F("SettingsCode", outcome.Code));
                break;
        }
        stdout.WriteLine(F("SettingsOperation", outcome.OperationId, outcome.State, outcome.Code));
        return exit;
    }

    // ---- formatting ----

    private static void WriteReview(TextWriter w, SettingDefinition d, SettingPlan plan, SettingApplyOptions apply)
    {
        w.WriteLine(F("SettingsReviewTitle", Title(d), d.Id));
        w.WriteLine(F("SettingsBeforeAfter", Label(d, plan.CurrentChoiceId), Label(d, plan.ChoiceId)));
        w.WriteLine(F("SettingsShowLocation", Location(d)));
        w.WriteLine(F("SettingsShowRisk", d.Risk, d.Recovery));
        if (!plan.WillChange) w.WriteLine(T("SettingsNoChange"));
        if (d.RequiresExplorerRestart)
            w.WriteLine(apply.RefreshShell && apply.RestartExplorer ? T("SettingsExplorerWarning") : T("SettingsExplorerNotRestarted"));
        if (d.RequiresSignOut) w.WriteLine(T("SettingsSignOutNote"));
    }

    private static string? RestartNote(SettingDefinition d) =>
        d.RequiresExplorerRestart && d.RequiresSignOut ? T("SettingsRestartBoth") : d.RequiresExplorerRestart ? T("SettingsRestartExplorer") : d.RequiresSignOut ? T("SettingsRestartSignOut") : null;

    internal static string Title(SettingDefinition d) => Res(d.TitleKey, d.Id);
    private static string Describe(SettingDefinition d) => Res(d.DescriptionKey, "");
    private static string ChoiceLabel(SettingChoice c) => Res(c.LabelKey, c.Id);
    private static string Label(SettingDefinition d, string? id) => id is null ? T("SettingsCustomValue") : d.Choices.FirstOrDefault(c => c.Id == id) is { } c ? ChoiceLabel(c) : T("SettingsCustomValue");
    private static string Res(string key, string fallback) { var v = T(key); return v == key ? fallback : v; }
    private static string Location(SettingDefinition d) => SettingLocations.Of(d);

    private static string CurrentLabel(SettingState s) =>
        s.Compatibility != SettingCompatibility.Supported || s.Issue is not null ? "-" : Label(s.Definition, s.MatchedChoiceId);

    private static string StatusText(SettingState s) =>
        s.Compatibility == SettingCompatibility.UnsupportedBuild ? T("SettingsStatusUnsupported")
        : s.Compatibility == SettingCompatibility.Unknown ? T("SettingsStatusUnknown")
        : s.Issue is { } i ? F("SettingsStatusUnreadable", i.Error.Code)
        : s.MatchedChoiceId is null ? T("SettingsStatusCustom") : T("SettingsStatusOk");

    private static object ToJson(SettingState s)
    {
        var d = s.Definition;
        return new
        {
            d.Id, Category = d.Category.ToString(), Risk = d.Risk.ToString(), Recovery = d.Recovery.ToString(),
            RequiresExplorerRestart = d.RequiresExplorerRestart, RequiresSignOut = d.RequiresSignOut,
            Compatibility = s.Compatibility.ToString(), CurrentChoiceId = s.MatchedChoiceId, Custom = s.Compatibility == SettingCompatibility.Supported && s.Issue is null && s.MatchedChoiceId is null,
            Choices = d.Choices.Select(c => c.Id).ToList(), d.DefaultChoiceId, d.RecommendedChoiceId, Location = Location(d),
            Issue = s.Issue is null ? null : new { s.Issue.Error.Code, Category = s.Issue.Error.Category.ToString() },
        };
    }

    private static object OutcomeJson(SettingOutcome x) => new
    {
        x.OperationId, x.SettingId, x.ChoiceId, State = x.State.ToString(), x.Code, x.BeforeChoiceId, x.AfterChoiceId,
        x.NeedsExplorerRestart, x.NeedsSignOut, TookEffect = x.State is OperationState.Applied or OperationState.Reverted or OperationState.AlreadyConfigured, x.AtUtc,
    };

    private static string OutcomeLine(SettingOutcome x) =>
        F("SettingsHistoryLine", x.AtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), x.SettingId, x.State, x.Code, x.OperationId);

    private static void WriteTable(TextWriter w, List<string[]> table)
    {
        var cols = table[0].Length;
        var widths = Enumerable.Range(0, cols).Select(c => table.Max(r => r[c].Length)).ToArray();
        foreach (var r in table) w.WriteLine(string.Join("  ", r.Select((v, c) => c == cols - 1 ? v : v.PadRight(widths[c]))).TrimEnd());
    }

    private static string Cell(string value) => Truncate(Clean(value), MaxCell);
    private static string Clean(string value) { var sb = new StringBuilder(value.Length); foreach (var ch in value) sb.Append(char.IsControl(ch) ? ' ' : ch); return sb.ToString().Trim(); }
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string ErrorLine(WinoraError e) =>
        F("DiscoverError", T(e.UserMessageKey), e.Code) + (e.CorrelationId is null ? "" : " " + F("DiscoverReference", e.CorrelationId));
}
