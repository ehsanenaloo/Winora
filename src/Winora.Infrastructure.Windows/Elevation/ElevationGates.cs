// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;
using EW = Winora.Elevation.Windows;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>One evaluated gate. <see cref="Code"/> and <see cref="Detail"/> are Domain tokens.</summary>
public sealed record GateResult(string Id, bool Passed, string? Code = null, string Detail = "")
{
    /// <summary>Resource key of the user guidance (exists in every host's Strings.resx for every elevation code).</summary>
    public string? GuidanceKey => Code is null ? null : ErrorCatalog.ResourceKey(Code);
    public static GateResult Pass(string id) => new(id, true);
    public static GateResult Fail(string id, string code, string detail = "") => new(id, false, code, detail);
}

/// <summary>Ordered results; evaluation stops at the first refusal, so <see cref="Results"/> ends with it.</summary>
public sealed class GateReport
{
    public GateReport(IReadOnlyList<GateResult> results, bool noConsentPrompt) { Results = results; NoConsentPrompt = noConsentPrompt; }
    public IReadOnlyList<GateResult> Results { get; }
    public GateResult? FirstFailure => Results.FirstOrDefault(r => !r.Passed);
    public bool Passed => FirstFailure is null;
    /// <summary>Windows will not ask for consent on this machine: the host warns and asks a second explicit confirmation.</summary>
    public bool NoConsentPrompt { get; }
}

/// <summary>
/// Availability gates G1 to G11 of section 3, plus the two development-host guards that precede them (test hooks of
/// section 20 and the build flavor). A pure function over injected facts: it reads nothing and starts nothing. In a release configuration
/// on a machine without the signed per-machine install the first refusal is G1 (location), so the launcher is never reached.
/// </summary>
public static class ElevationGates
{
    public const string TestHooks = "T0", Flavor = "F0", G1 = "G1", G2 = "G2", G3 = "G3", G4 = "G4", G5 = "G5", G6 = "G6", G7 = "G7", G8 = "G8", G9 = "G9", G10 = "G10", G11 = "G11";
    public const int MinimumBuild = 22621;

    /// <param name="buildFlavor">The flavor this build carries; defaults to the compile-time constant. Only tests pass another value.</param>
    public static GateReport Evaluate(ElevationFlavor flavor, InstallFacts install, ElevationEnvironment env, ElevationFlavor? buildFlavor = null)
    {
        var results = new List<GateResult>();
        bool Step(GateResult r) { results.Add(r); return r.Passed; }
        GateReport Done() => new(results, env.ConsentPromptBehaviorAdmin == 0);

        // Development-host guards: a test convenience on a machine path, or a flavor this build does not carry.
        if (!Step(env.TestHooksPresent ? GateResult.Fail(TestHooks, ErrorCode.ElevationUnavailable, "testhook") : GateResult.Pass(TestHooks))) return Done();
        if (!Step(flavor == (buildFlavor ?? ElevationBuild.Flavor) ? GateResult.Pass(Flavor) : GateResult.Fail(Flavor, ErrorCode.ElevationUnavailable))) return Done();

        // G1: the client runs from the per-machine install root and is Authenticode-valid with a pinned signer.
        if (!Step(ClientResult(install))) return Done();
        // G2: the HKLM install record names that same root.
        if (!Step(install.RecordedRoot is not null && SamePath(install.RecordedRoot, install.ExpectedRoot)
            ? GateResult.Pass(G2) : GateResult.Fail(G2, ErrorCode.ElevationInstallUntrusted, "location"))) return Done();
        // G3: UAC enabled. The persisted value can differ from the running state until reboot; unreadable is refused with uac_state.
        if (!Step(env.EnableLua switch
        {
            true => GateResult.Pass(G3),
            false => GateResult.Fail(G3, ErrorCode.ElevationUacDisabled),
            null => GateResult.Fail(G3, ErrorCode.ElevationUnavailable, "uac_state"),
        })) return Done();
        // G4: the client is not elevated and runs at Medium integrity.
        if (!Step(env.ClientTokenElevated || !env.ClientIntegrityIsMedium ? GateResult.Fail(G4, ErrorCode.ElevationClientElevated) : GateResult.Pass(G4))) return Done();
        // G5: the definition set the client carries exists for this flavor. The release set is empty by design:
        // the worker, not the client, answers it with worker.unimplemented (11.2), so the gate has nothing further to refuse in.
        if (!Step(GateResult.Pass(G5))) return Done();
        // G6: worker image guard.
        if (!Step(WorkerResult(install))) return Done();
        // G7: interactive desktop session, not session 0.
        if (!Step(env.SessionId == 0 || !env.InteractiveSession ? GateResult.Fail(G7, ErrorCode.ElevationUnavailable, "session") : GateResult.Pass(G7))) return Done();
        // G8: the pipe DACL needs the logon SID; the user SID must be known for the command line.
        if (!Step(env.ClientUserSid is null || !EW.SidText.IsValid(env.ClientUserSid) || !EW.SidText.IsLogonSid(env.ClientLogonSid)
            ? GateResult.Fail(G8, ErrorCode.ElevationUnavailable, "logon_sid")
            : env.ClientProcessId == 0 || env.ClientStartFileTime == 0 ? GateResult.Fail(G8, ErrorCode.ElevationUnavailable)   // the worker's secondary pid check needs both
            : GateResult.Pass(G8))) return Done();
        // G9: no runtime-hook variable in this process.
        if (!Step(env.HookVariableNames.Count != 0 ? GateResult.Fail(G9, ErrorCode.ElevationUnavailable, "environment") : GateResult.Pass(G9))) return Done();
        // G10: x64 Windows 11, build 22621 or later.
        if (!Step(env.IsX64 && env.OsBuild is { } b && b >= MinimumBuild ? GateResult.Pass(G10) : GateResult.Fail(G10, ErrorCode.ElevationPlatformUnsupported))) return Done();
        // G11: Administrator protection enabled is "unverified" until a VM run settles it. Undetectable (null) skips the gate (3.1).
        Step(env.AdministratorProtection == true ? GateResult.Fail(G11, ErrorCode.ElevationPlatformUnverified) : GateResult.Pass(G11));
        return Done();
    }

    private static GateResult ClientResult(InstallFacts f)
    {
        if (f.ClientImagePath is null || !IsInsideClientFolder(f.ClientImagePath, f.ExpectedRoot)) return GateResult.Fail(G1, ErrorCode.ElevationInstallUntrusted, "location");
        return f.ClientImage.Kind switch
        {
            ImageVerdictKind.Trusted => GateResult.Pass(G1),
            ImageVerdictKind.Unsigned => GateResult.Fail(G1, ErrorCode.ElevationUnsignedBuild),
            ImageVerdictKind.Untrusted => GateResult.Fail(G1, ErrorCode.ElevationInstallUntrusted, DetailOrLocation(f.ClientImage.Detail)),
            _ => GateResult.Fail(G1, ErrorCode.ElevationInstallUntrusted, "location"),
        };
    }

    private static GateResult WorkerResult(InstallFacts f)
    {
        var expected = Path.Combine(f.ExpectedRoot, ElevationBuild.WorkerFolder, ElevationBuild.WorkerFileName);
        if (f.WorkerImagePath is null || !SamePath(f.WorkerImagePath, expected)) return GateResult.Fail(G6, ErrorCode.ElevationInstallUntrusted, "location");
        return f.WorkerImage.Kind switch
        {
            ImageVerdictKind.Trusted => GateResult.Pass(G6),
            ImageVerdictKind.Unsigned => GateResult.Fail(G6, ErrorCode.ElevationInstallUntrusted, "signer"),
            ImageVerdictKind.Untrusted => GateResult.Fail(G6, ErrorCode.ElevationInstallUntrusted, DetailOrLocation(f.WorkerImage.Detail)),
            _ => GateResult.Fail(G6, ErrorCode.ElevationInstallUntrusted, "location"),
        };
    }

    private static string DetailOrLocation(string detail) =>
        detail.Length != 0 && ElevationDetail.IsAllowed(ErrorCode.ElevationInstallUntrusted, detail) ? detail : "location";

    /// <summary>The client must be the desktop or CLI image directly inside the fixed sub-folders of the root.</summary>
    internal static bool IsInsideClientFolder(string imagePath, string root)
    {
        var dir = Path.GetDirectoryName(imagePath);
        if (dir is null) return false;
        return SamePath(dir, Path.Combine(root, ElevationBuild.DesktopFolder)) || SamePath(dir, Path.Combine(root, ElevationBuild.CliFolder));
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
