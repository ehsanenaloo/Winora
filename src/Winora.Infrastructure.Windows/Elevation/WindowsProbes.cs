// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Winora.Domain.Elevation;
using EW = Winora.Elevation.Windows;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>
/// A pinned signer (trust.md 4.3, D-07): the leaf subject plus the SHA-256 of the issuing key. A single leaf thumbprint is never a pin.
/// M4a ships NO release pin: nothing is signed yet, so every production image is "unsigned" and elevation is unavailable (G1/G6) until
/// the signing route exists. A VM build supplies its own test-chain pin at compile time.
/// </summary>
public sealed record SignerPin(string LeafSubject, string IssuerSpkiSha256Hex);

public static class SignerPins
{
#if WINORA_VM_TRUST
    public static IReadOnlyList<SignerPin> ForBuild { get; } = [new SignerPin(VmTrustPin.LeafSubject, VmTrustPin.IssuerSpkiSha256)];
#else
    public static IReadOnlyList<SignerPin> ForBuild { get; } = [];
#endif

    public static bool Matches(EW.SignatureEvidence? signature, IReadOnlyList<SignerPin> pins) =>
        signature is { IsValid: true, Leaf: { } leaf, Issuer: { } issuer }
        && pins.Any(p => string.Equals(p.LeafSubject, leaf.Subject, StringComparison.Ordinal) && string.Equals(p.IssuerSpkiSha256Hex, issuer.SpkiSha256Hex, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Production install probe: reads the HKLM install record (read only) and the client's own location FIRST. Only when the client really runs
/// from the per-machine layout does it run the image guard on the client and the worker (reads only, never executes either). On any other
/// machine, including every development machine, it reports the location facts and runs nothing further.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInstallProbe : IInstallProbe, IDisposable
{
    private readonly IReadOnlyList<SignerPin> pins;
    private EW.ImageGuardResult? heldWorker;

    public WindowsInstallProbe(IReadOnlyList<SignerPin>? pins = null) => this.pins = pins ?? SignerPins.ForBuild;

    public InstallFacts Read()
    {
        heldWorker?.Dispose();
        heldWorker = null;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ElevationBuild.InstallFolderName);
        var recorded = ReadRecordedRoot();
        var client = Environment.ProcessPath;
        var layoutHolds = client is not null && ElevationGates.IsInsideClientFolder(client, root) && recorded is not null && ElevationGates.SamePath(recorded, root);
        if (!layoutHolds) return new InstallFacts(root, recorded, client, null, ImageVerdict.NotEvaluated, ImageVerdict.NotEvaluated);

        var worker = Path.Combine(root, ElevationBuild.WorkerFolder, ElevationBuild.WorkerFileName);
        var subtree = EW.ImageGuard.AuditSubtree(root);
        using var clientGuard = Guard(client!, root);
        var workerGuard = Guard(worker, root);
        var clientVerdict = Verdict(clientGuard, subtree);
        var workerVerdict = Verdict(workerGuard, subtree);
        heldWorker = workerGuard;
        return new InstallFacts(root, recorded, client, worker, clientVerdict, workerVerdict, workerGuard.HeldHandle is { IsInvalid: false } h ? FileIdOf(h) : null);
    }

    public string? ReadWorkerFileId(string workerImagePath)
    {
        try
        {
            using var handle = File.OpenHandle(workerImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FileIdOf(handle);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        heldWorker?.Dispose();
        heldWorker = null;
    }

    private static EW.ImageGuardResult Guard(string path, string root) =>
        EW.ImageGuard.Verify(path, new EW.ImageGuardOptions { TrustBoundaryDirectory = root, RequireSignature = true });

    private ImageVerdict Verdict(EW.ImageGuardResult guard, IReadOnlyList<EW.ImageGuardFinding> subtree)
    {
        if (subtree.Count != 0) return ImageVerdict.Untrusted(DetailOf(subtree[0].Detail));
        if (!guard.Trusted)
            return guard.Findings[0] is { Stage: EW.ImageGuardStage.Signature, Detail: "signer" } ? ImageVerdict.Unsigned : ImageVerdict.Untrusted(DetailOf(guard.Detail));
        return SignerPins.Matches(guard.Signature, pins) ? ImageVerdict.Trusted : ImageVerdict.Unsigned;
    }

    private static string DetailOf(string? detail) => detail ?? "location";

    private static string? ReadRecordedRoot()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(ElevationBuild.InstallRecordKey, false);   // read only
            return key?.GetValue("Root") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation info);

    private static string? FileIdOf(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info) ? $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}" : null;
}

/// <summary>
/// Production environment probe: read-only registry values, the client's own token, the OS build and the environment NAMES (never values).
/// <paramref name="testHooksPresent"/> is what the host saw on its command line; the probe adds the two test variables of protocol.md 20.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsElevationEnvironmentProbe(bool testHooksPresent) : IElevationEnvironmentProbe
{
    private const string PoliciesSystem = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public ElevationEnvironment Read()
    {
        var identity = EW.ProcessIdentityReader.ReadCurrentProcess();
        var token = identity.Token;
        var hooks = EW.EnvironmentTripwire.ScanCurrentEnvironment().Offending;
        var testVariables = Environment.GetEnvironmentVariable("WINORA_DATA_DIR") is not null || Environment.GetEnvironmentVariable("WINORA_NO_LOG") is not null;
        return new ElevationEnvironment(
            IsX64: RuntimeInformation.OSArchitecture == Architecture.X64,
            OsBuild: new WindowsOsBuildProvider().CurrentBuild,
            EnableLua: ReadPolicy("EnableLUA") is { } lua ? lua != 0 : null,
            ConsentPromptBehaviorAdmin: ReadPolicy("ConsentPromptBehaviorAdmin"),
            AdministratorProtection: null,   // no reliable detection yet (protocol.md 3.1 G11, X13): the gate is skipped
            ClientTokenElevated: token?.IsElevated ?? true,
            ClientIntegrityIsMedium: token?.Integrity == EW.IntegrityLevel.Medium,
            ClientUserSid: token?.UserSid, ClientLogonSid: token?.LogonSid, SessionId: token?.SessionId ?? 0,
            InteractiveSession: Environment.UserInteractive, HookVariableNames: hooks, TestHooksPresent: testHooksPresent || testVariables,
            ClientProcessId: (uint)Environment.ProcessId, ClientStartFileTime: identity.Process?.StartFiletime ?? 0);
    }

    private static int? ReadPolicy(string name)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(PoliciesSystem, false);   // read only
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
