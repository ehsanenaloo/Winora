// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// The process-wide hardening steps of 14.1 behind a seam, so the order and the failure handling of the bootstrap are tested with a
/// fake that only records calls. The real implementation changes the CURRENT process (DLL search order, working directory, mitigation policies, job
/// object) and must never be constructed by a test.
/// </summary>
public interface IHardeningApplier
{
    /// <summary>14.1 step 3: SetDefaultDllDirectories(system32 + application directory) and the working directory set to the worker directory.</summary>
    bool ApplyDllSearchHardening(string workerDirectory);
    /// <summary>14.1 step 4: the committed table <c>mitigations.v1</c>.</summary>
    IReadOnlyList<MitigationOutcome> ApplyMitigations(MitigationPlan plan, bool nativeAot);
    /// <summary>14.1 step 5: the job object limits; the handle stays open for the process lifetime.</summary>
    bool ApplyJob(JobLimits limits);
}

/// <summary>
/// Real hardening. Reversible in a child-free way? No: SetDefaultDllDirectories, SetProcessMitigationPolicy and AssignProcessToJobObject cannot be
/// undone for a process, and the job (ActiveProcessLimit 1, kill on close) would constrain the test host, so tests use a fake. The VM runs exercise this
/// class (experiments X6 and X16).
/// </summary>
public sealed class WindowsHardeningApplier : IHardeningApplier, IDisposable
{
    private const uint DllSearchApplicationDir = 0x200, DllSearchSystem32 = 0x800;
    private SafeHandleZeroOrMinusOneIsInvalid? job;

    public bool ApplyDllSearchHardening(string workerDirectory)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!Native.SetDefaultDllDirectories(DllSearchSystem32 | DllSearchApplicationDir)) return false;
        return Native.SetCurrentDirectoryW(workerDirectory);
    }

    public IReadOnlyList<MitigationOutcome> ApplyMitigations(MitigationPlan plan, bool nativeAot) => plan.Apply(nativeAot);

    public bool ApplyJob(JobLimits limits)
    {
        job = limits.ApplyToCurrentProcess();
        return job is not null;
    }

    public void Dispose() => job?.Dispose();
}

/// <summary>Verdict of the worker's check of its own image. The signature evidence is kept to compare with the client's.</summary>
public sealed record OwnImageVerdict(bool Trusted, SignatureEvidence? Signature, string Detail);

/// <summary>Seams for the facts the bootstrap needs from the operating system. The production set reads the real process; tests inject plain data.</summary>
public sealed class WorkerBootstrapDependencies
{
    public required Func<IdentityReadResult> ReadOwnProcess { get; init; }
    public required Func<bool?> IsImpersonating { get; init; }
    public required Func<TripwireResult> ScanEnvironment { get; init; }
    public required IHardeningApplier Hardening { get; init; }
    public required Func<string, OwnImageVerdict> VerifyOwnImage { get; init; }
    /// <summary>Absolute path of the running worker image (<c>&lt;InstallRoot&gt;\worker\&lt;name&gt;.exe</c>).</summary>
    public required string ImagePath { get; init; }
    public bool NativeAot { get; init; } = true;
    public MitigationPlan Mitigations { get; init; } = MitigationPlan.V1;
    public JobLimits Job { get; init; } = JobLimits.Default;
    public IAclReader? AclReader { get; init; }
    /// <summary>Name of the folder the worker image must sit in, directly under the install root (<c>worker</c>).</summary>
    public string WorkerFolderName { get; init; } = "worker";
    public string RunFolderName { get; init; } = "run";
}

/// <summary>What the rest of the worker needs once every startup step passed. <see cref="Lock"/> is held until disposal.</summary>
public sealed class BootstrapContext : IDisposable
{
    internal BootstrapContext(WorkerArguments arguments, TokenFacts token, ProcessFacts process, string installRoot, string runDirectory, WorkerLock workerLock, SignatureEvidence? ownSignature)
    { Arguments = arguments; Token = token; Self = process; InstallRoot = installRoot; RunDirectory = runDirectory; Lock = workerLock; OwnSignature = ownSignature; }

    public WorkerArguments Arguments { get; }
    public TokenFacts Token { get; }
    public ProcessFacts Self { get; }
    public string InstallRoot { get; }
    public string RunDirectory { get; }
    public WorkerLock Lock { get; }
    public SignatureEvidence? OwnSignature { get; }
    public void Dispose() => Lock.Dispose();
}

/// <summary>Outcome of the bootstrap: a context, or the exit code, the early-failure reason (null when nothing may be reported) and the rendezvous for the record.</summary>
public sealed class BootstrapResult
{
    private BootstrapResult() { }
    public BootstrapContext? Context { get; private init; }
    public WorkerExitCode ExitCode { get; private init; }
    public EarlyFailureReason? Reason { get; private init; }
    public Bytes16? Rendezvous { get; private init; }
    /// <summary>The run directory when it could be derived (always, once the image path has the expected shape), for writing the early-failure record.</summary>
    public string? RunDirectory { get; private init; }
    /// <summary>The first failing step, for tests and diagnostics (never user text).</summary>
    public string Step { get; private init; } = "";
    public bool Succeeded => Context is not null;

    internal static BootstrapResult Ok(BootstrapContext context) => new() { Context = context, ExitCode = WorkerExitCode.Finished, Step = "ok" };
    internal static BootstrapResult Fail(string step, WorkerExitCode exit, EarlyFailureReason? reason, Bytes16? rendezvous, string? runDirectory) =>
        new() { Step = step, ExitCode = exit, Reason = reason, Rendezvous = rendezvous, RunDirectory = runDirectory };
}

/// <summary>
/// The worker startup order of 14.1, each step fatal on failure:
/// (1) own token: elevated, High integrity, not impersonating, else exit 3 and NOTHING is created;
/// (2) environment tripwire (names only);
/// (3) DLL search hardening; (4) mitigation table; (5) job object;
/// (6) strict argument parse; (7) run-directory rule, <c>worker.lock</c>, own image check.
/// Steps 8 (pipe and attestation) and 9 (client watcher) need the session and live in <see cref="WorkerRunner"/>.
/// The argument list is parsed purely before step 2 only to learn the rendezvous for the early-failure record; its verdict is applied at step 6.
/// </summary>
public static class WorkerBootstrap
{
    public static BootstrapResult Run(IReadOnlyList<string> args, WorkerBootstrapDependencies d)
    {
        var parsed = WorkerArgumentParser.Parse(args);
        string? runDirectory = DeriveRunDirectory(d, out var installRoot);

        // 1. Token self-check: an unelevated launch ends here without creating any object.
        var own = d.ReadOwnProcess();
        if (!own.IsOk || own.Token is null || own.Process is null) return BootstrapResult.Fail("token", WorkerExitCode.NotElevated, null, null, null);
        var token = own.Token;
        if (!token.IsElevated || token.Integrity != IntegrityLevel.High || d.IsImpersonating() != false || token.IsImpersonationToken)
            return BootstrapResult.Fail("token", WorkerExitCode.NotElevated, null, null, null);

        // 2. Environment tripwire.
        if (d.ScanEnvironment().Tripped) return BootstrapResult.Fail("environment", WorkerExitCode.TrustFailure, EarlyFailureReason.Environment, parsed.Rendezvous, runDirectory);

        // 3. DLL search order and working directory.
        string? workerDirectory = Path.GetDirectoryName(d.ImagePath);
        if (workerDirectory is null || !d.Hardening.ApplyDllSearchHardening(workerDirectory))
            return BootstrapResult.Fail("dll_directories", WorkerExitCode.TrustFailure, EarlyFailureReason.Hardening, parsed.Rendezvous, runDirectory);

        // 4. Mitigation table: every required row must succeed.
        var outcomes = d.Hardening.ApplyMitigations(d.Mitigations, d.NativeAot);
        if (!MitigationPlan.RequiredEntriesSatisfied(d.Mitigations.Entries.Where(e => MitigationPlan.AppliesTo(e, d.NativeAot)), outcomes))
            return BootstrapResult.Fail("mitigations", WorkerExitCode.TrustFailure, EarlyFailureReason.Hardening, parsed.Rendezvous, runDirectory);

        // 5. Job object.
        if (!d.Hardening.ApplyJob(d.Job)) return BootstrapResult.Fail("job", WorkerExitCode.TrustFailure, EarlyFailureReason.Hardening, parsed.Rendezvous, runDirectory);

        // 6. Strict arguments.
        if (!parsed.IsSuccess) return BootstrapResult.Fail("arguments", WorkerExitCode.BadArguments, EarlyFailureReason.Arguments, parsed.Rendezvous, runDirectory);
        var arguments = parsed.Arguments!;

        // 7. Run directory rule, single-instance lock, own image.
        if (runDirectory is null || installRoot is null) return BootstrapResult.Fail("location", WorkerExitCode.TrustFailure, EarlyFailureReason.Image, arguments.Rendezvous, runDirectory);
        var dir = RunDirectoryGuard.Check(runDirectory, d.AclReader);
        if (!dir.Trusted) return BootstrapResult.Fail("run_dir", WorkerExitCode.TrustFailure, EarlyFailureReason.RunDir, arguments.Rendezvous, runDirectory);
        var workerLock = WorkerLock.TryAcquire(runDirectory);
        if (!workerLock.IsAcquired)
        {
            workerLock.Dispose();
            return workerLock.Status == WorkerLockStatus.Busy
                ? BootstrapResult.Fail("lock", WorkerExitCode.Busy, EarlyFailureReason.Busy, arguments.Rendezvous, runDirectory)
                : BootstrapResult.Fail("lock", WorkerExitCode.ChannelFailure, EarlyFailureReason.Internal, arguments.Rendezvous, runDirectory);
        }
        var image = d.VerifyOwnImage(d.ImagePath);
        if (!image.Trusted)
        {
            workerLock.Dispose();
            return BootstrapResult.Fail("image", WorkerExitCode.TrustFailure, EarlyFailureReason.Image, arguments.Rendezvous, runDirectory);
        }
        return BootstrapResult.Ok(new BootstrapContext(arguments, token, own.Process, installRoot, runDirectory, workerLock, image.Signature));
    }

    /// <summary>
    /// The install layout is <c>&lt;InstallRoot&gt;\worker\&lt;image&gt;</c> with <c>run</c> beside it. Returns null when the image does
    /// not sit in a folder named like <c>WorkerFolderName</c> directly under a root.
    /// </summary>
    internal static string? DeriveRunDirectory(WorkerBootstrapDependencies d, out string? installRoot)
    {
        installRoot = null;
        string? workerDir = Path.GetDirectoryName(d.ImagePath);
        if (workerDir is null || !string.Equals(Path.GetFileName(workerDir), d.WorkerFolderName, StringComparison.OrdinalIgnoreCase)) return null;
        installRoot = Path.GetDirectoryName(workerDir);
        return installRoot is null ? null : Path.Combine(installRoot, d.RunFolderName);
    }

    /// <summary>The image check for production: the held-handle guard of 4.3 on the own image, with the install root as trust boundary (D0 finding on volume roots).</summary>
    public static OwnImageVerdict VerifyOwnImageWithImageGuard(string imagePath, string? installRoot, IAclReader? aclReader = null, bool requireSignature = true)
    {
        using var result = ImageGuard.Verify(imagePath, new ImageGuardOptions { TrustBoundaryDirectory = installRoot, AclReader = aclReader, RequireSignature = requireSignature });
        return new OwnImageVerdict(result.Trusted, result.Signature, result.Detail ?? "ok");
    }
}
