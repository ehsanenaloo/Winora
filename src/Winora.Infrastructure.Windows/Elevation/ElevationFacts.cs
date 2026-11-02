// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>
/// Build identity of this client (technical-docs/protocol.md 2, trust.md 5). The flavor is a compile-time constant: there is no runtime switch,
/// environment variable or registry value that changes it (D-09). The VM-only configuration defines the symbol WINORA_VM_TRUST.
/// </summary>
public static class ElevationBuild
{
#if WINORA_VM_TRUST
    public const ElevationFlavor Flavor = ElevationFlavor.VmTrust;
    public const string InstallFolderName = "WinoraVm";
    public const string InstallRecordKey = @"SOFTWARE\WinoraVm\Install";
#else
    public const ElevationFlavor Flavor = ElevationFlavor.Release;
    public const string InstallFolderName = "Winora";
    public const string InstallRecordKey = @"SOFTWARE\Winora\Install";
#endif
    /// <summary>Fixed sub-folders and file name of the per-machine layout (trust.md 3.1, protocol.md 4.3).</summary>
    public const string WorkerFolder = "worker", DesktopFolder = "desktop", CliFolder = "cli", RunFolder = "run";
    public const string WorkerFileName = "Winora.Worker.exe";
}

/// <summary>Verdict of the image guard for one binary, reduced to plain data (technical-docs/protocol.md 4.3).</summary>
public enum ImageVerdictKind
{
    /// <summary>The guard was not run (an earlier fact already refused).</summary>
    NotEvaluated,
    /// <summary>Walk, ACL, hash, signature and signer pin all hold.</summary>
    Trusted,
    /// <summary>No acceptable signer: unsigned, or signed by someone who is not pinned.</summary>
    Unsigned,
    /// <summary>Location, ACL, reparse, volume, hash or offline-chain failure; see the detail token.</summary>
    Untrusted,
}

public sealed record ImageVerdict(ImageVerdictKind Kind, string Detail = "")
{
    public static ImageVerdict NotEvaluated { get; } = new(ImageVerdictKind.NotEvaluated);
    public static ImageVerdict Trusted { get; } = new(ImageVerdictKind.Trusted);
    public static ImageVerdict Unsigned { get; } = new(ImageVerdictKind.Unsigned);
    public static ImageVerdict Untrusted(string detail) => new(ImageVerdictKind.Untrusted, detail);
}

/// <summary>
/// What the install probe observed (technical-docs/protocol.md 3.1 G1, G2, G6, 4.3). Paths are canonical strings; null means "could not be determined",
/// which is always a refusal. The worker path is derived by the probe from the fixed layout and is exposed only when the root checks held.
/// </summary>
public sealed record InstallFacts(
    string ExpectedRoot, string? RecordedRoot, string? ClientImagePath, string? WorkerImagePath, ImageVerdict ClientImage, ImageVerdict WorkerImage, string? WorkerFileId = null);

/// <summary>Injected reader of the install facts. The Windows implementation reads the install record and runs the image guard; tests inject records.</summary>
public interface IInstallProbe
{
    InstallFacts Read();
    /// <summary>Re-opens the worker path (not the held handle) and returns its file id for the post-launch comparison (technical-docs/protocol.md 4.5); null when unreadable.</summary>
    string? ReadWorkerFileId(string workerImagePath);
}

/// <summary>Facts about the machine, the session and the client process (G3, G4, G7 to G11, D-12, D-13), as plain data.</summary>
/// <param name="EnableLua">Persisted EnableLUA; null when unreadable.</param>
/// <param name="ConsentPromptBehaviorAdmin">Persisted prompt behavior for administrators; 0 means elevate without prompting (D-12). Null when unreadable.</param>
/// <param name="AdministratorProtection">True when enabled, false when disabled, null when it cannot be detected (the gate is then skipped, protocol.md 3.1 G11).</param>
/// <param name="TestHooksPresent">A test convenience (sandbox root, database, no-log flag or variable) is present on a machine command (protocol.md 20).</param>
public sealed record ElevationEnvironment(
    bool IsX64, int? OsBuild, bool? EnableLua, int? ConsentPromptBehaviorAdmin, bool? AdministratorProtection,
    bool ClientTokenElevated, bool ClientIntegrityIsMedium, string? ClientUserSid, string? ClientLogonSid, uint SessionId, bool InteractiveSession,
    IReadOnlyList<string> HookVariableNames, bool TestHooksPresent,
    uint ClientProcessId, ulong ClientStartFileTime);

public interface IElevationEnvironmentProbe
{
    ElevationEnvironment Read();
}
