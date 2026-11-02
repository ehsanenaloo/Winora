// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using Winora.Application;
using Winora.Elevation.Protocol.Definitions;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>The production composition of the machine-change executor for a composition root (CLI, Desktop).</summary>
public sealed record MachineComposition(IMachineSettingsExecutor Executor, MachineExecutorPreflight Preflight);

/// <summary>What the host learns from the availability gates before it asks for consent.</summary>
public sealed record MachineExecutorPreflight(Func<GateEvaluation> Evaluate);

public static class MachineProduction
{
    /// <summary>
    /// Builds the executor around the given journal with the real, read-only probes, the real pipe and attestation readers, and a lazily created launcher: the real shell
    /// executor exists only when a launch is really about to happen, so every gate refusal ends before it. No definition set is embedded in any build yet (key custody is
    /// undecided), so the verified set is the empty one and every plan ends with <c>definition.unknown</c>.
    /// </summary>
    private static string InstallRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ElevationBuild.InstallFolderName);

    /// <summary>The verified definition set of this build, read through the same loader the executor uses. A release build has no embedded set, so the set is empty.</summary>
    [SupportedOSPlatform("windows")]
    public static VerifiedDefinitionSource CreateDefinitionSource()
    {
        var material = ElevationBuild.Flavor == Winora.Domain.Elevation.ElevationFlavor.VmTrust ? new Winora.Elevation.Windows.Worker.FileDefinitionMaterial(Path.Combine(InstallRoot(), "definitions")) : null;
        return new VerifiedDefinitionSource(new DefinitionSetVerifier(material?.Keys ?? []), new RegistryFloorReader(), () => material?.Load());
    }

    [SupportedOSPlatform("windows")]
    public static MachineComposition Create(IMachineOperationStore store, bool testHooksPresent)
    {
        var root = InstallRoot();
        var runDirectory = AttestationFilePoller.RunDirectoryFor(root);
        var flavor = ElevationBuild.Flavor;
        var definitions = CreateDefinitionSource();
        var channel = new WorkerMachineChannel(flavor, new WindowsInstallProbe(), new WindowsElevationEnvironmentProbe(testHooksPresent), new WorkerLauncher(static () => new RunasShellExecutor()),
            new AttestationFilePoller(runDirectory), new WindowsPipeConnector(), new WindowsServerProcessInspector(),
            () => new Winora.Domain.Elevation.Bytes32(Convert.FromHexString(definitions.Get().Set?.HashHex ?? new string('0', 64))), buildFlavor: flavor);
        var executor = new MachineSettingsExecutor(store, definitions, new WindowsMachineStateReader(), new WindowsMachinePolicyReader(), channel, new MachineDigests(),
            new WindowsOsBuildProvider(), SystemProcessLiveness.Instance, new WorkerLockProbe(runDirectory), new WindowsBootSession());
        return new(executor, new MachineExecutorPreflight(channel.Preflight));
    }
}
