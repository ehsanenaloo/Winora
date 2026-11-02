// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Winora.Application;
using Winora.Domain;
using static Winora.Cli.CliText;
namespace Winora.Cli;

internal sealed record MachineOptions(bool Json);

/// <summary>
/// `winora machine selftest [--json]`: exercises the elevation channel with a harmless no-op. It is a distinct verb
/// from `settings` and its composition takes no path, root, database, log switch or shell-refresh parameter. It refuses before reading any
/// state when a test convenience is present, and it never elevates unless an interactive console confirmed.
/// On any machine without the signed per-machine installation the result is a typed refusal from an availability gate and nothing is launched.
/// </summary>
internal static class MachineCommand
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>True when a test convenience appears on the command line or in the environment. <paramref name="args"/> are the original arguments.</summary>
    public static bool TestHooksPresent(IReadOnlyList<string> args) =>
        args.Any(a => a is "--sandbox-root" or "--database" or "--no-log")
        || Environment.GetEnvironmentVariable("WINORA_DATA_DIR") is not null || Environment.GetEnvironmentVariable("WINORA_NO_LOG") is not null;

    /// <summary>Parses the arguments after "machine": exactly <c>selftest</c> with an optional single <c>--json</c>.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out MachineOptions options)
    {
        options = new(false);
        if (args.Count == 0 || args[0] != "selftest") return false;
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--json" && !json) json = true;
            else return false;
        }
        options = new(json);
        return true;
    }

    /// <param name="args">Arguments after "machine".</param>
    /// <param name="testHooks">True when the original command line or environment carried a test convenience.</param>
    /// <param name="interactive">True when stdin and stdout are both interactive consoles.</param>
    /// <param name="createSelfTest">Builds the self-test around the given confirmation. Called only after parsing and the test-hook check.</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, bool testHooks, bool interactive, Func<IElevationConfirmation, IElevationSelfTest> createSelfTest,
        TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var json = args.Contains("--json");
        if (testHooks)
            return Render(new ElevationSelfTestResult(false, ErrorCode.ElevationUnavailable, "testhook", ElevationSelfTestStage.Gates, false, false, "T0"), json, stdout, stderr);
        if (!TryParse(args, out var options))
        {
            stderr.WriteLine(T("InvalidArguments"));
            stderr.WriteLine(T("MachineUsage"));
            return 2;
        }
        try
        {
            if (!options.Json) stdout.WriteLine(T("MachineBanner"));
            var confirmation = new ConsoleElevationConfirmation(interactive, stdin, options.Json ? stderr : stdout);
            var service = createSelfTest(confirmation);
            try
            {
                var result = await service.RunNoopAsync(ct).ConfigureAwait(false);
                return Render(result, options.Json, stdout, stderr);
            }
            finally { (service as IDisposable)?.Dispose(); }
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine(T("DiscoverCancelled"));
            return ErrorCatalog.ExitCode(ErrorCategory.Cancelled);
        }
        catch (Exception e)
        {
            // Never print exception text: only the localized guidance, the stable code and the reference.
            var error = new ErrorMapper().Map(e);
            stderr.WriteLine(F("DiscoverError", T(error.UserMessageKey), error.Code) + (error.CorrelationId is null ? "" : " " + F("DiscoverReference", error.CorrelationId)));
            return error.ExitCode;
        }
    }

    /// <summary>Exit code of a result: 0 for success, otherwise the category exit code of its stable code.</summary>
    public static int ExitCodeOf(ElevationSelfTestResult result) =>
        result.Succeeded ? 0 : ErrorCatalog.ExitCode(ErrorCatalog.Describe(result.Code).Category);

    private static int Render(ElevationSelfTestResult result, bool json, TextWriter stdout, TextWriter stderr)
    {
        var exit = ExitCodeOf(result);
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = SchemaVersion, kind = "machine.selftest", succeeded = result.Succeeded, code = result.Code, detail = result.Detail,
                stage = result.Stage.ToString().ToLowerInvariant(), gate = result.GateId, launchAttempted = result.LaunchAttempted,
                channelEstablished = result.ChannelEstablished, changesMade = false, exitCode = exit,
            }, JsonOptions));
            return exit;
        }
        if (result.Succeeded)
        {
            stdout.WriteLine(T("MachineSelftestOk"));
            return 0;
        }
        stderr.WriteLine(T("MachineSelftestFailed"));
        var shown = result.Detail.Length == 0 ? result.Code : result.Code + "/" + result.Detail;
        stderr.WriteLine(F("DiscoverError", T(result.GuidanceKey), shown));
        if (!result.LaunchAttempted) stderr.WriteLine(T("MachineNothingStarted"));
        return exit;
    }
}

/// <summary>
/// Console confirmation: the user types the first 8 hex characters of the batch digest, and when Windows will not
/// ask for consent, once more after a plain warning. A host that is not an interactive console answers NonInteractive and nothing is launched.
/// </summary>
internal sealed class ConsoleElevationConfirmation(bool interactive, TextReader stdin, TextWriter prompt) : IElevationConfirmation
{
    public Task<ElevationConfirmationAnswer> ConfirmAsync(ElevationConfirmationRequest request, CancellationToken cancellationToken)
    {
        if (!interactive) return Task.FromResult(ElevationConfirmationAnswer.NonInteractive);
        if (request.NoConsentPrompt) prompt.WriteLine(T("MachineNoConsentWarning"));
        if (!Typed(F("MachinePromptDigest", request.BatchDigestShort), request.BatchDigestShort)) return Task.FromResult(ElevationConfirmationAnswer.Declined);
        if (request.NoConsentPrompt && !Typed(F("MachinePromptSecond", request.BatchDigestShort), request.BatchDigestShort)) return Task.FromResult(ElevationConfirmationAnswer.Declined);
        return Task.FromResult(ElevationConfirmationAnswer.Confirmed);
    }

    private bool Typed(string question, string expected)
    {
        prompt.Write(question);
        prompt.Flush();
        return string.Equals(stdin.ReadLine()?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }
}
