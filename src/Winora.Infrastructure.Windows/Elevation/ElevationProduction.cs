// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>
/// The production composition of the self-test for composition roots (CLI). Everything is the real, read-only probes and the real pipe and
/// attestation readers; the real shell executor is created lazily, only at the moment a launch is really about to happen, so every gate
/// refusal (every development machine, every unsigned, portable or per-user install) ends before the executor exists.
/// </summary>
public static class ElevationProduction
{
    /// <summary>Number of real launch executors ever created in this process (diagnostic for tests: a gate refusal leaves it unchanged).</summary>
    [SupportedOSPlatform("windows")]
    internal static int ExecutorsCreated => RunasShellExecutor.InstancesCreated;

    /// <param name="confirmation">The host's interactive confirmation.</param>
    /// <param name="testHooksPresent">True when the host saw a test convenience (sandbox root, database, no-log flag or variable) on a machine command.</param>
    public static IElevationSelfTest CreateSelfTest(IElevationConfirmation confirmation, bool testHooksPresent)
    {
        if (!OperatingSystem.IsWindows()) return new UnavailableSelfTest();
        return CreateWindows(confirmation, testHooksPresent);
    }

    [SupportedOSPlatform("windows")]
    private static ElevationSelfTestService CreateWindows(IElevationConfirmation confirmation, bool testHooksPresent) =>
        Build(confirmation, testHooksPresent, static () => new RunasShellExecutor());

    /// <summary>Internal so a test can build the production probes with an executor factory that fails if it is ever called.</summary>
    [SupportedOSPlatform("windows")]
    internal static ElevationSelfTestService Build(IElevationConfirmation confirmation, bool testHooksPresent, Func<IShellExecutor> executorFactory)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ElevationBuild.InstallFolderName);
        return new ElevationSelfTestService(
            ElevationBuild.Flavor, new WindowsInstallProbe(), new WindowsElevationEnvironmentProbe(testHooksPresent), new WorkerLauncher(executorFactory),
            new AttestationFilePoller(AttestationFilePoller.RunDirectoryFor(root)), new WindowsPipeConnector(), new WindowsServerProcessInspector(), confirmation);
    }

    private sealed class UnavailableSelfTest : IElevationSelfTest
    {
        public Task<ElevationSelfTestResult> RunNoopAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ElevationSelfTestResult(false, ErrorCode.ElevationPlatformUnsupported, "", ElevationSelfTestStage.Gates, false, false, ElevationGates.G10));
    }
}
