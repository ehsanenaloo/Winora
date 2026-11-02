// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// Everything that differs between the release worker and the VM-only worker: the flavor byte,
/// the handler set, whether Commit is enabled and the install-root folder name. The release profile is the only one that exists in a default
/// build. The VmTrust profile is compiled in only with <c>-p:WinoraFlavor=VmTrust</c> (symbol <c>WINORA_VM_TRUST</c>), so its identity strings
/// are absent from release outputs (a test scans the compiled bytes). Tests build their own profile through the public constructor; a profile
/// handed to a session still obeys the library rule that a release flavor never carries the self-test handler.
/// </summary>
public sealed class WorkerFlavorProfile
{
    public WorkerFlavorProfile(ElevationFlavor flavor, WorkerHandlerSet handlers, bool commitSupported, string installFolderName)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentException.ThrowIfNullOrEmpty(installFolderName);
        Flavor = flavor; Handlers = handlers; CommitSupported = commitSupported; InstallFolderName = installFolderName;
    }

    public ElevationFlavor Flavor { get; }
    public WorkerHandlerSet Handlers { get; }
    /// <summary>Release: false. VmTrust: true so the VM harness can run the ping Commit.</summary>
    public bool CommitSupported { get; }
    /// <summary>Folder under Program Files that holds this flavor's install root (<c>Winora</c> for release).</summary>
    public string InstallFolderName { get; }

    /// <summary>The production flavor: byte 0, empty handler set, Commit not supported.</summary>
    public static WorkerFlavorProfile Production { get; } = new(ElevationFlavor.Release, WorkerHandlerSet.Empty, false, "Winora");

    /// <summary>The profile compiled into this build: the VmTrust profile when <c>WINORA_VM_TRUST</c> is defined, else <see cref="Production"/>.</summary>
    public static WorkerFlavorProfile Current { get; } = CreateCurrent();

    /// <summary>True when this build was made with <c>-p:WinoraFlavor=VmTrust</c>.</summary>
    public static bool IsVmBuild =>
#if WINORA_VM_TRUST
        true;
#else
        false;
#endif

    private static WorkerFlavorProfile CreateCurrent()
    {
#if WINORA_VM_TRUST
        return new WorkerFlavorProfile(ElevationFlavor.VmTrust, new WorkerHandlerSet([new SelftestNoopHandler()]), true, VmTrustIdentity.InstallFolderName);
#else
        return Production;
#endif
    }
}

#if WINORA_VM_TRUST
/// <summary>VM-only identity strings. Compiled only under <c>WINORA_VM_TRUST</c>; the scan test requires them to be absent from default outputs.</summary>
internal static class VmTrustIdentity
{
    public const string InstallFolderName = "WinoraVm";
    /// <summary>Placeholder id of the throwaway test key; the real key is generated per VM run and never committed.</summary>
    public const string TestKeyId = "winora-vmtrust-test-key-v0";
}
#endif
