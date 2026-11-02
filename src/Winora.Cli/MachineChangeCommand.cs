// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;
using static Winora.Cli.CliText;
namespace Winora.Cli;

/// <summary>What the host learned from the availability gates before it asks for consent.</summary>
internal sealed record MachinePreflight(bool Passed, string? Code, bool NoConsentPrompt);

/// <summary>
/// <c>winora machine plan|apply|revert|recover|history</c> (technical-docs/trust.md 11, protocol.md 3.2). Planning and history only read. Apply and revert change
/// the machine, so they run only in an interactive console: the review is printed, the user types the first 8 hex characters of the batch digest and, when
/// Windows will not prompt, once more after a plain warning. There is no <c>--yes</c>, no path, root, database or log option, and the verb refuses when a test
/// convenience is present. The release definition set is empty, so on a release build every plan ends with <c>definition.unknown</c>: administrator-level changes are
/// not available in this release.
/// </summary>
internal static class MachineChangeCommand
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] Verbs = ["plan", "apply", "revert", "recover", "history"];

    public static bool IsChangeVerb(string? first) => first is not null && Verbs.Contains(first);

    private sealed record Parsed(string Verb, string[] Positional, bool Json);

    private static bool TryParse(IReadOnlyList<string> args, out Parsed parsed)
    {
        parsed = new("", [], false);
        if (args.Count == 0 || !IsChangeVerb(args[0])) return false;
        var json = false; var positional = new List<string>();
        foreach (var a in args.Skip(1))
        {
            if (a == "--json" && !json) json = true;
            else if (a.StartsWith('-')) return false;
            else positional.Add(a);
        }
        var expected = args[0] switch { "plan" or "apply" => 2, "revert" => 1, _ => 0 };
        if (positional.Count != expected) return false;
        if (args[0] is "apply" or "revert" && json) return false;           // consent is a human step; no machine-readable apply
        parsed = new(args[0], [.. positional], json);
        return true;
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, bool testHooks, bool interactive, IMachineSettingsExecutor executor,
        Func<IReadOnlyList<MachineOperationRecord>> history, Func<MachinePreflight> preflight, TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (testHooks) return Fail(ErrorCode.ElevationUnavailable, stderr);
        if (!TryParse(args, out var p))
        {
            stderr.WriteLine(T("InvalidArguments"));
            stderr.WriteLine(T("MachineChangeUsage"));
            return 2;
        }
        try
        {
            return p.Verb switch
            {
                "plan" => await PlanAsync(p, executor, stdout, stderr, ct).ConfigureAwait(false),
                "apply" => await ApplyAsync(p, executor, interactive, preflight, stdin, stdout, stderr, ct).ConfigureAwait(false),
                "revert" => await RevertAsync(p, executor, history, interactive, preflight, stdin, stdout, stderr, ct).ConfigureAwait(false),
                "recover" => await RecoverAsync(p, executor, stdout, ct).ConfigureAwait(false),
                _ => History(p, history, stdout),
            };
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine(T("DiscoverCancelled"));
            return ErrorCatalog.ExitCode(ErrorCategory.Cancelled);
        }
        catch (OperationException e) { return Fail(e.Code, stderr); }
        catch (Exception e)
        {
            // Never print exception text: only the localized guidance, the stable code and the reference.
            var error = new ErrorMapper().Map(e);
            stderr.WriteLine(F("DiscoverError", T(error.UserMessageKey), error.Code) + (error.CorrelationId is null ? "" : " " + F("DiscoverReference", error.CorrelationId)));
            return error.ExitCode;
        }
    }

    private static int Fail(string code, TextWriter stderr)
    {
        stderr.WriteLine(F("DiscoverError", T(ErrorCatalog.ResourceKey(code)), code));
        return ErrorCatalog.ExitCode(ErrorCatalog.Describe(code).Category);
    }

    // ---- plan ------------------------------------------------------------------------------------

    private static async Task<int> PlanAsync(Parsed p, IMachineSettingsExecutor executor, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var plan = await executor.PlanAsync(p.Positional[0], p.Positional[1], ct).ConfigureAwait(false);
        if (p.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = SchemaVersion, kind = "machine.plan", definitionId = plan.DefinitionId, choice = plan.ChoiceId, refused = plan.IsRefused, code = plan.RefusalCode,
                willChange = plan.WillChange, current = plan.IsRefused ? null : Describe(plan.Before), desired = plan.DesiredValue is null ? null : Describe(plan.DesiredValue),
                requiresRestart = plan.RequiresRestart, setSerial = plan.SetSerial, changesMade = false,
            }, JsonOptions));
            return plan.IsRefused ? ErrorCatalog.ExitCode(ErrorCatalog.Describe(plan.RefusalCode!).Category) : 0;
        }
        if (plan.IsRefused) return Fail(plan.RefusalCode!, stderr);
        WriteReview(plan, stdout);
        stdout.WriteLine(plan.WillChange ? T("MachineChangeNothingChanged") : T("MachineChangeAlreadySet"));
        return 0;
    }

    // ---- apply -----------------------------------------------------------------------------------

    private static async Task<int> ApplyAsync(Parsed p, IMachineSettingsExecutor executor, bool interactive, Func<MachinePreflight> preflight, TextReader stdin, TextWriter stdout,
        TextWriter stderr, CancellationToken ct)
    {
        if (!interactive) return Fail(ErrorCode.ElevationConfirmationRequired, stderr);
        var plan = await executor.PlanAsync(p.Positional[0], p.Positional[1], ct).ConfigureAwait(false);
        if (plan.IsRefused) return Fail(plan.RefusalCode!, stderr);
        var gates = preflight();
        if (!gates.Passed) return Fail(gates.Code ?? ErrorCode.ElevationUnavailable, stderr);
        WriteReview(plan, stdout);
        if (!plan.WillChange)
        {
            stdout.WriteLine(T("MachineChangeAlreadySet"));
            return 0;
        }
        var digest = executor.BatchDigestShort([plan]);
        var confirmation = Confirm(digest, gates.NoConsentPrompt, stdin, stdout);
        if (confirmation is null) return Fail(ErrorCode.ElevationDeclined, stderr);
        var outcomes = await executor.ApplyBatchAsync([plan], confirmation, ct).ConfigureAwait(false);
        return Report(outcomes[0], stdout, stderr);
    }

    // ---- revert ----------------------------------------------------------------------------------

    private static async Task<int> RevertAsync(Parsed p, IMachineSettingsExecutor executor, Func<IReadOnlyList<MachineOperationRecord>> history, bool interactive,
        Func<MachinePreflight> preflight, TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (!interactive) return Fail(ErrorCode.ElevationConfirmationRequired, stderr);
        if (!Guid.TryParse(p.Positional[0], out var id)) { stderr.WriteLine(T("InvalidArguments")); return 2; }
        var row = history().FirstOrDefault(r => r.Id == id);
        if (row is null) return Fail(ErrorCode.OperationMissing, stderr);
        var gates = preflight();
        if (!gates.Passed) return Fail(gates.Code ?? ErrorCode.ElevationUnavailable, stderr);
        stdout.WriteLine(F("MachineChangeRevertReview", row.DefinitionId, row.Id.ToString("D")));
        stdout.WriteLine(T("MachineChangeScope"));
        var digest = executor.RevertDigestShort(id);
        var confirmation = Confirm(digest, gates.NoConsentPrompt, stdin, stdout);
        if (confirmation is null) return Fail(ErrorCode.ElevationDeclined, stderr);
        return Report(await executor.RevertAsync(id, confirmation, ct).ConfigureAwait(false), stdout, stderr);
    }

    private static ElevationConfirmation? Confirm(string digest, bool noConsentPrompt, TextReader stdin, TextWriter stdout)
    {
        if (noConsentPrompt) stdout.WriteLine(T("MachineNoConsentWarning"));
        if (!Typed(F("MachinePromptDigest", digest), digest, stdin, stdout)) return null;
        if (noConsentPrompt && !Typed(F("MachinePromptSecond", digest), digest, stdin, stdout)) return null;
        return new ElevationConfirmation(digest, true, noConsentPrompt);
    }

    private static bool Typed(string question, string expected, TextReader stdin, TextWriter stdout)
    {
        stdout.Write(question);
        stdout.Flush();
        return string.Equals(stdin.ReadLine()?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static int Report(MachineOutcome outcome, TextWriter stdout, TextWriter stderr)
    {
        var ok = outcome.State is OperationState.Applied or OperationState.PendingRestart or OperationState.AlreadyConfigured or OperationState.Reverted;
        if (ok)
        {
            stdout.WriteLine(F("MachineChangeDone", outcome.DefinitionId, outcome.State.ToString()));
            if (outcome.State == OperationState.PendingRestart) stdout.WriteLine(T("MachineChangeRestart"));
            return 0;
        }
        if (outcome.RecoveryRequired) stderr.WriteLine(T("MachineChangeRecovery"));
        return Fail(outcome.Code, stderr);
    }

    // ---- recover and history -----------------------------------------------------------------------

    private static async Task<int> RecoverAsync(Parsed p, IMachineSettingsExecutor executor, TextWriter stdout, CancellationToken ct)
    {
        var results = (await executor.RecoverAsync(ct).ConfigureAwait(false)).Concat(executor.DriftCheck()).ToList();
        if (p.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = SchemaVersion, kind = "machine.recover", changesMade = false,
                rows = results.Select(r => new { id = r.OperationId, definitionId = r.DefinitionId, state = r.State.ToString().ToLowerInvariant(), code = r.Code, recoveryRequired = r.RecoveryRequired }),
            }, JsonOptions));
            return 0;
        }
        if (results.Count == 0) stdout.WriteLine(T("MachineChangeNothingToRecover"));
        foreach (var r in results) stdout.WriteLine(F("MachineChangeRecoverLine", r.OperationId.ToString("D"), r.DefinitionId, r.State.ToString(), r.Code));
        return 0;
    }

    private static int History(Parsed p, Func<IReadOnlyList<MachineOperationRecord>> history, TextWriter stdout)
    {
        var rows = history();
        if (p.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = SchemaVersion, kind = "machine.history",
                rows = rows.Select(r => new { id = r.Id, definitionId = r.DefinitionId, version = r.DefinitionVersion, choice = r.ChoiceId, state = r.State.ToString().ToLowerInvariant(), code = r.Code, createdAtUtc = r.CreatedAtUtc }),
            }, JsonOptions));
            return 0;
        }
        if (rows.Count == 0) stdout.WriteLine(T("MachineChangeNoHistory"));
        foreach (var r in rows) stdout.WriteLine(F("MachineChangeHistoryLine", r.Id.ToString("D"), r.DefinitionId, r.ChoiceId, r.State.ToString(), r.Code));
        return 0;
    }

    // ---- review text -------------------------------------------------------------------------------

    private static void WriteReview(MachinePlan plan, TextWriter stdout)
    {
        stdout.WriteLine(F("MachineChangeReviewTitle", plan.DefinitionId, plan.DefinitionVersion.ToString(CultureInfo.InvariantCulture)));
        stdout.WriteLine(T("MachineChangeScope"));
        stdout.WriteLine(F("MachineChangeCurrent", Describe(plan.Before)));
        stdout.WriteLine(F("MachineChangeDesired", plan.DesiredValue is null ? "" : plan.Before.Kind == MachineTargetKind.ServiceStartType ? DescribeStart((byte)plan.DesiredValue.Number!.Value) : Describe(plan.DesiredValue)));
        stdout.WriteLine(F("MachineChangeRestoreNote", Describe(plan.Before)));
        if (plan.RequiresRestart) stdout.WriteLine(T("MachineChangeNeedsRestart"));
        stdout.WriteLine(F("MachineChangeSet", plan.SetSerial.ToString(CultureInfo.InvariantCulture), plan.SetHash.Length >= 8 ? plan.SetHash[..8] : plan.SetHash));
    }

    internal static string Describe(MachineSnapshot snapshot) =>
        snapshot.Kind == MachineTargetKind.ServiceStartType ? (snapshot.TryReadService(out var exists, out var start) && exists ? DescribeStart(start) : T("MachineChangeServiceMissing")) :
        snapshot.TryReadRegistry(out var keyExists, out _, out var value) ? (keyExists || value.Kind != MachineValueKind.Absent ? Describe(value) : T("MachineChangeKeyMissing")) : T("MachineChangeUnknownState");

    private static string DescribeStart(byte start) => start switch
    {
        2 => T("MachineChangeStartAutomatic"), 3 => T("MachineChangeStartManual"), 4 => T("MachineChangeStartDisabled"), _ => T("MachineChangeUnknownState"),
    };

    internal static string Describe(MachineValue value) => value.Kind switch
    {
        MachineValueKind.Absent => T("MachineChangeNotSet"),
        MachineValueKind.DWord or MachineValueKind.QWord => value.Number!.Value.ToString(CultureInfo.InvariantCulture),
        _ => "\"" + value.Text + "\"",
    };
}

/// <summary>Stands in on hosts where the machine verbs cannot run (not Windows, or a test convenience is present). Every call refuses with a typed code.</summary>
internal sealed class UnavailableExecutor : IMachineSettingsExecutor
{
    private static OperationException Unavailable => new(ErrorCode.ElevationPlatformUnsupported);
    public Task<MachinePlan> PlanAsync(string definitionId, string choiceId, CancellationToken cancellationToken) => throw Unavailable;
    public string BatchDigestShort(IReadOnlyList<MachinePlan> plans) => throw Unavailable;
    public Task<IReadOnlyList<MachineOutcome>> ApplyBatchAsync(IReadOnlyList<MachinePlan> reviewed, ElevationConfirmation confirmation, CancellationToken cancellationToken) => throw Unavailable;
    public string RevertDigestShort(Guid operationId) => throw Unavailable;
    public Task<MachineOutcome> RevertAsync(Guid operationId, ElevationConfirmation confirmation, CancellationToken cancellationToken) => throw Unavailable;
    public Task<IReadOnlyList<MachineOutcome>> RecoverAsync(CancellationToken cancellationToken) => throw Unavailable;
    public IReadOnlyList<MachineOutcome> DriftCheck() => throw Unavailable;
    public MachineOutcome MarkReviewed(Guid operationId) => throw Unavailable;
}
