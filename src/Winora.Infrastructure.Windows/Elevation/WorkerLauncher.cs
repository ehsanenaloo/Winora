// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Winora.Domain;
using Winora.Domain.Elevation;
using EW = Winora.Elevation.Windows;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>
/// The only request the launcher ever hands to the operating system: a fixed verb, a verified file, a fixed argument grammar. There is no
/// field a caller could fill with a command, a path of their choosing or a response file (technical-docs/protocol.md 4.1, 4.4, 20 item 6).
/// </summary>
/// <param name="ScrubEnvironmentNames">Names of runtime-hook variables to remove from this process's environment for the duration of the call (G9 normally leaves none).</param>
public sealed record RunasRequest(string File, string Arguments, string WorkingDirectory, bool HideWindow, string Verb, IReadOnlyList<string> ScrubEnvironmentNames);

/// <summary>Outcome of the OS call: started, or the Win32 error (1223 = the user declined the prompt).</summary>
public sealed record RunasResult(bool Started, int Win32Error);

/// <summary>The seam in front of the one Win32 call. The real implementation is internal and is never constructed by a test.</summary>
public interface IShellExecutor
{
    RunasResult Execute(RunasRequest request);
}

/// <summary>Everything the command line carries, as typed data (technical-docs/protocol.md 4.4). The worker path is not part of it.</summary>
public sealed record WorkerLaunchPlan(
    Bytes16 Rendezvous, uint ClientProcessId, ulong ClientStartFileTime, string InitiatorSid, string InitiatorLogonSid, Bytes32 BatchDigest, byte Operations,
    bool NoConsentPrompt, bool SecondConfirmationGiven);

public sealed record WorkerLaunchResult(bool Started, string? Code, string Detail, int Win32Error)
{
    public static WorkerLaunchResult Ok { get; } = new(true, null, "", 0);
    public static WorkerLaunchResult Refused(string code, string detail = "", int win32 = 0) => new(false, code, detail, win32);
}

/// <summary>
/// Builds the exact worker command line and asks the executor to start the verified worker image elevated (<c>runas</c>, hidden window, no
/// response file, fixed working directory). The path comes only from <see cref="InstallFacts"/> produced by the install probe and must equal
/// the fixed layout path; nothing a user typed can reach it. A launch is refused, without calling the executor, unless both images are
/// trusted and, when Windows would not prompt for consent, the second confirmation was given (D-12).
/// </summary>
public sealed class WorkerLauncher
{
    public const string Verb = "runas";
    /// <summary>ERROR_CANCELLED: the user declined the prompt (technical-docs/protocol.md 4.1).</summary>
    public const int ErrorCancelled = 1223;
    /// <summary>ERROR_ACCESS_DISABLED_BY_POLICY and ERROR_ACCESS_DISABLED_NO_SAFER: blocked by policy; the exact code for Smart App Control is unverified (X20).</summary>
    private static readonly int[] BlockedErrors = [1260, 1261];

    private readonly Func<IShellExecutor> executorFactory;
    private readonly Func<IEnumerable<string>> environmentNames;

    public WorkerLauncher(Func<IShellExecutor> executorFactory, Func<IEnumerable<string>>? environmentNames = null)
    {
        this.executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        this.environmentNames = environmentNames ?? CurrentEnvironmentNames;
    }

    public WorkerLauncher(IShellExecutor executor, Func<IEnumerable<string>>? environmentNames = null) : this(() => executor, environmentNames) { }

    /// <summary>The exact argument string of technical-docs/protocol.md 4.4. Public so tests and the documentation can compare it byte for byte.</summary>
    public static string BuildArguments(WorkerLaunchPlan p) => string.Create(CultureInfo.InvariantCulture,
        $"--proto {ElevationLimits.ProtocolVersion} --rendezvous {p.Rendezvous.ToHex()} --client-pid {p.ClientProcessId} --client-start {p.ClientStartFileTime} "
        + $"--initiator-sid {p.InitiatorSid} --initiator-logon-sid {p.InitiatorLogonSid} --batch-digest {p.BatchDigest.ToHex()} --ops {p.Operations}");

    public WorkerLaunchResult Launch(WorkerLaunchPlan plan, InstallFacts install)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(install);
        if (plan.Operations is < 1 or > ElevationLimits.MaxBatchOperations || !EW.SidText.IsValid(plan.InitiatorSid) || !EW.SidText.IsLogonSid(plan.InitiatorLogonSid))
            return WorkerLaunchResult.Refused(ErrorCode.ElevationUnavailable);
        if (plan.NoConsentPrompt && !plan.SecondConfirmationGiven) return WorkerLaunchResult.Refused(ErrorCode.ElevationConfirmationRequired);

        var worker = FixedWorkerPath(install);
        if (worker is null) return WorkerLaunchResult.Refused(ErrorCode.ElevationInstallUntrusted, "location");
        if (install.ClientImage.Kind != ImageVerdictKind.Trusted || install.WorkerImage.Kind != ImageVerdictKind.Trusted)
            return WorkerLaunchResult.Refused(ErrorCode.ElevationInstallUntrusted, "signer");

        var scrub = EW.EnvironmentTripwire.Scan(environmentNames()).Offending.ToArray();
        var request = new RunasRequest(worker, BuildArguments(plan), Path.GetDirectoryName(worker)!, HideWindow: true, Verb, scrub);
        RunasResult result;
        try { result = executorFactory().Execute(request); }
        catch (Exception e) when (e is not OperationCanceledException) { return WorkerLaunchResult.Refused(ErrorCode.ElevationLaunchFailed); }
        if (result.Started) return WorkerLaunchResult.Ok;
        if (result.Win32Error == ErrorCancelled) return WorkerLaunchResult.Refused(ErrorCode.ElevationDeclined, "", result.Win32Error);
        if (Array.IndexOf(BlockedErrors, result.Win32Error) >= 0) return WorkerLaunchResult.Refused(ErrorCode.ElevationBlocked, "", result.Win32Error);
        return WorkerLaunchResult.Refused(ErrorCode.ElevationLaunchFailed, "", result.Win32Error);
    }

    /// <summary>The worker path of the fixed layout, or null unless the probe's path equals it (a hostile or confused probe cannot redirect the launch).</summary>
    internal static string? FixedWorkerPath(InstallFacts install)
    {
        var expected = Path.Combine(install.ExpectedRoot, ElevationBuild.WorkerFolder, ElevationBuild.WorkerFileName);
        return install.WorkerImagePath is not null && ElevationGates.SamePath(install.WorkerImagePath, expected) ? expected : null;
    }

    private static IEnumerable<string> CurrentEnvironmentNames() =>
        Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(k => k.ToString() ?? "").Where(k => k.Length != 0);
}
