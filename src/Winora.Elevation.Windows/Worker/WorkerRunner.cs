// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// Watches the client process (technical-docs/protocol.md 14.1 step 9). <see cref="Watch"/> calls <c>onClientGone</c> when the process with the given id and start
/// time has ended, or at once when it cannot be opened or its start time differs (the client named on the command line is not there).
/// </summary>
public interface IClientWatcher
{
    IDisposable Watch(uint processId, ulong startFileTime, Action onClientGone);
}

/// <summary>Real watcher: opens the client with SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION and waits on the process handle through the thread pool.</summary>
public sealed class ProcessClientWatcher : IClientWatcher
{
    private const uint Synchronize = 0x100000, QueryLimited = 0x1000;

    public IDisposable Watch(uint processId, ulong startFileTime, Action onClientGone)
    {
        if (!OperatingSystem.IsWindows()) { onClientGone(); return Registration.None; }
        var process = Native.OpenProcess(Synchronize | QueryLimited, false, processId);
        if (process.IsInvalid) { onClientGone(); return Registration.None; }
        if (!Native.GetProcessTimes(process.DangerousGetHandle(), out var creation, out _, out _, out _) || creation != startFileTime)
        {
            process.Dispose();
            onClientGone();
            return Registration.None;
        }
        return new Registration(process, onClientGone);
    }

    private sealed class Registration : IDisposable
    {
        public static readonly Registration None = new();
        private readonly SafeKernelHandle? process;
        private readonly ProcessWaitHandle? wait;
        private readonly RegisteredWaitHandle? registered;
        private Registration() { }
        public Registration(SafeKernelHandle process, Action gone)
        {
            this.process = process;
            wait = new ProcessWaitHandle(process);
            registered = ThreadPool.RegisterWaitForSingleObject(wait, static (state, _) => ((Action)state!)(), gone, Timeout.Infinite, true);
        }
        public void Dispose()
        {
            registered?.Unregister(null);
            wait?.Dispose();
            process?.Dispose();
        }
    }

    /// <summary>A wait handle over a process handle the owner keeps alive; it never closes the handle itself.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeKernelHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}

/// <summary>Signer equality of technical-docs/protocol.md 7.2 step 2 and trust.md 4.3: same leaf subject AND same issuing-key hash, never a leaf thumbprint.</summary>
public static class SignerPin
{
    public static bool Equal(SignatureEvidence? a, SignatureEvidence? b)
    {
        if (a is null || b is null || !a.IsValid || !b.IsValid || a.Leaf is null || b.Leaf is null) return false;
        if (!string.Equals(a.Leaf.Subject, b.Leaf.Subject, StringComparison.Ordinal)) return false;
        var ia = a.Issuer; var ib = b.Issuer;
        if (ia is null || ib is null) return ia is null && ib is null && string.Equals(a.Leaf.SpkiSha256Hex, b.Leaf.SpkiSha256Hex, StringComparison.OrdinalIgnoreCase);
        return string.Equals(ia.SpkiSha256Hex, ib.SpkiSha256Hex, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Seams and options of a worker run. <see cref="WorkerEntry"/> builds the production set; tests inject fakes, temp directories and test pipe names.</summary>
public sealed class WorkerRunnerDependencies
{
    public required WorkerBootstrapDependencies Bootstrap { get; init; }
    public required WorkerFlavorProfile Flavor { get; init; }
    public required IPipeNamePolicy PipeNames { get; init; }
    public Func<Bytes16, ElevationFlavor, string> PipeNameFor { get; init; } = PipeCreationPlan.NameFor;
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public IHandshakeEntropy Entropy { get; init; } = SystemHandshakeEntropy.Instance;
    public IClientWatcher? Watcher { get; init; }
    /// <summary>Overrides the client image check (tests). Default: the held-handle image guard plus signer equality with the worker's own signature.</summary>
    public Func<string, bool>? VerifyClientImage { get; init; }
    public TimeSpan? LifetimeCap { get; init; }
    /// <summary>
    /// Resolves the definitions for an install root (trust.md 6.3). Null keeps the flavor's built-in handlers and set hash (the M4a behavior, and every test that does not need a signed set).
    /// </summary>
    public Func<string, WorkerDefinitionState>? Definitions { get; init; }
    public TimeSpan EarlyFailureGrace { get; init; } = TimeSpan.FromMilliseconds(ElevationLimits.EarlyFailureGraceMs);
    /// <summary>The client image locations under the install root (relative to it): desktop and cli.</summary>
    public IReadOnlyList<string> ClientImageRelativePaths { get; init; } = [@"desktop\Winora.Desktop.exe", @"cli\Winora.Cli.exe"];
}

public sealed class WorkerRunResult
{
    public WorkerExitCode ExitCode { get; init; }
    /// <summary>The first failing bootstrap or setup step (<c>ok</c> when the host ran).</summary>
    public string Step { get; init; } = "";
    public EarlyFailureReason? Reason { get; init; }
    public WorkerHostResult? Host { get; init; }
}

/// <summary>
/// The worker's whole life behind seams (technical-docs/protocol.md 14.1): bootstrap steps 1 to 7, then the pipe and the attestation (step 8), the client watcher
/// (step 9), the connect loop and the session. The worker executable is a thin shell around this class so that no test ever starts it.
/// Every failure after argument parsing tries to leave the early-failure record and returns a typed exit code; nothing is written to the console.
/// </summary>
public static class WorkerRunner
{
    public static async Task<WorkerRunResult> RunAsync(IReadOnlyList<string> args, WorkerRunnerDependencies d, CancellationToken ct = default)
    {
        var boot = WorkerBootstrap.Run(args, d.Bootstrap);
        if (!boot.Succeeded)
        {
            if (boot.Reason is { } reason && boot.Rendezvous is { } r && boot.RunDirectory is { } dir)
                await EarlyFailureWriter.WriteAndHoldAsync(dir, reason, r, d.EarlyFailureGrace, d.Time, ct).ConfigureAwait(false);
            return new() { ExitCode = boot.ExitCode, Step = boot.Step, Reason = boot.Reason };
        }

        using var ctx = boot.Context!;
        var a = ctx.Arguments;
        async Task<WorkerRunResult> StartupFailure(string step, EarlyFailureReason reason)
        {
            await EarlyFailureWriter.WriteAndHoldAsync(ctx.RunDirectory, reason, a.Rendezvous, d.EarlyFailureGrace, d.Time, ct).ConfigureAwait(false);
            return new() { ExitCode = EarlyFailureMapping.ExitCodeFor(reason), Step = step, Reason = reason };
        }

        var userSidBytes = SidText.ToBinary(ctx.Token.UserSid);
        var initiatorSid = SidText.ToBinary(a.InitiatorSid);
        var logonSid = SidText.ToBinary(a.InitiatorLogonSid);
        if (userSidBytes is null || initiatorSid is null || logonSid is null || !BinarySid.IsValid(userSidBytes) || !BinarySid.IsValid(initiatorSid) || !BinarySid.IsValid(logonSid))
            return await StartupFailure("sid", EarlyFailureReason.Arguments).ConfigureAwait(false);

        var profile = d.Flavor;
        var definitions = d.Definitions?.Invoke(ctx.InstallRoot) ?? WorkerDefinitionState.BuiltIn(profile.Handlers, profile.CommitSupported);
        if (definitions.Failure is { } definitionFailure) return await StartupFailure("definitions", definitionFailure).ConfigureAwait(false);
        var self = new ServerIdentity(ctx.Self.ProcessId, ctx.Self.StartFiletime ?? 0, ctx.Token.SessionId);
        WorkerSession session;
        try
        {
            session = new WorkerSession(new WorkerSessionOptions(profile.Flavor, a.Rendezvous, self, new BinarySid(userSidBytes), definitions.Handlers, d.Entropy, d.Time,
                definitions.CommitSupported, definitions.MaxOperations, definitions.SetHash, definitions.BeforeBatchAccepted));
        }
        catch (ArgumentException) { return await StartupFailure("session", EarlyFailureReason.Internal).ConfigureAwait(false); }
        using var _ = session;

        var plan = PipeCreationPlan.For(a.Rendezvous, profile.Flavor, a.InitiatorLogonSid) with { Name = d.PipeNameFor(a.Rendezvous, profile.Flavor) };
        var allowed = d.ClientImageRelativePaths.Select(p => Path.Combine(ctx.InstallRoot, p)).ToArray();
        var pipeOptions = new PipeServerOptions
        {
            AllowedClientImages = allowed,
            VerifyClientImage = d.VerifyClientImage ?? (path => ClientImageVerification.Verify(path, ctx.InstallRoot, ctx.OwnSignature, d.Bootstrap.AclReader)),
        };
        var created = PipeServer.Create(plan, d.PipeNames, pipeOptions);
        if (!created.IsCreated)
            return await StartupFailure("pipe", created.Status == PipeCreateStatus.Squatted ? EarlyFailureReason.Squatted : EarlyFailureReason.Internal).ConfigureAwait(false);
        using var server = created.Server!;

        var attestation = AttestationWriter.TryWrite(ctx.RunDirectory, session.Attestation);
        if (!attestation.IsCreated) return await StartupFailure("attestation", EarlyFailureReason.Internal).ConfigureAwait(false);
        using var attestationHolder = attestation.Holder!;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IDisposable? watch = null;
        if (d.Watcher is { } watcher)
            watch = watcher.Watch(a.ClientProcessId, a.ClientStartFileTime, () =>
            {
                // Once a connection exists a dead client shows up as a closed pipe; the watcher only ends the wait for a client that is already gone.
                if (server.ConnectionsAccepted == 0) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            });
        try
        {
            var expected = new ExpectedClient(new BinarySid(initiatorSid), new BinarySid(logonSid), ctx.Token.SessionId, a.ClientProcessId, a.ClientStartFileTime, allowed);
            var host = new WorkerHost(session, expected, d.Time, d.LifetimeCap);
            var result = await host.RunAsync(server, cts.Token).ConfigureAwait(false);
            return new() { ExitCode = result.ExitCode, Step = "ok", Host = result };
        }
        finally { watch?.Dispose(); }
    }
}

/// <summary>The real client image check: the held-handle guard of 4.3 steps 2 to 5 on the client image, with the install root as trust boundary, and signer equality.</summary>
public static class ClientImageVerification
{
    public static bool Verify(string imagePath, string installRoot, SignatureEvidence? ownSignature, IAclReader? aclReader = null)
    {
        using var guard = ImageGuard.Verify(imagePath, new ImageGuardOptions { TrustBoundaryDirectory = installRoot, AclReader = aclReader });
        return guard.Trusted && SignerPin.Equal(ownSignature, guard.Signature);
    }
}

/// <summary>
/// Production wiring of the worker (used by the thin <c>Winora.Worker</c> executable): real process, real hardening, real image guard, release or
/// VmTrust profile chosen at compile time. With no arguments it does nothing and returns 3, so an accidental plain launch of the shipped binary is
/// inert (technical-docs/protocol.md 4.1).
/// </summary>
public static class WorkerEntry
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || !OperatingSystem.IsWindows()) return (int)WorkerExitCode.NotElevated;
        string? image = Environment.ProcessPath;
        if (string.IsNullOrEmpty(image)) return (int)WorkerExitCode.NotElevated;
        string? installRoot = Path.GetDirectoryName(Path.GetDirectoryName(image));
        // Never disposed: the job object has KILL_ON_JOB_CLOSE, so closing its handle before Main returns would terminate the process with the wrong exit code.
        var hardening = new WindowsHardeningApplier();
        var profile = WorkerFlavorProfile.Current;
        var registry = new WindowsMachineRegistry();
        var dependencies = new WorkerRunnerDependencies
        {
            Flavor = profile,
            // The signed set, the pinned keys and the floor are resolved per run; with no embedded set the worker keeps its built-in handlers (none in a release build).
            Definitions = root => WorkerDefinitions.Resolve(profile.Handlers, profile.CommitSupported, ProductionMaterial(root), new RegistryFloorStore(), registry,
                Environment.OSVersion.Version.Build, WorkerFlavorProfile.IsVmBuild, new WindowsMachineServices()),
            PipeNames = new ProductionPipeNames(profile.Flavor),
            Watcher = new ProcessClientWatcher(),
            Bootstrap = new WorkerBootstrapDependencies
            {
                ReadOwnProcess = ProcessIdentityReader.ReadCurrentProcess,
                IsImpersonating = ProcessIdentityReader.IsCurrentThreadImpersonating,
                ScanEnvironment = EnvironmentTripwire.ScanCurrentEnvironment,
                Hardening = hardening,
                VerifyOwnImage = path => WorkerBootstrap.VerifyOwnImageWithImageGuard(path, installRoot),
                ImagePath = image,
                NativeAot = MitigationPlan.IsNativeAotRuntime,
            },
        };
        var result = WorkerRunner.RunAsync(args, dependencies).GetAwaiter().GetResult();
        GC.KeepAlive(hardening);
        return (int)result.ExitCode;
    }

    /// <summary>The definition material of this build: embedded resources in a release build; the VM build reads a signed set and its key from the VM install root.</summary>
    private static IDefinitionMaterial ProductionMaterial(string installRoot)
    {
#if WINORA_VM_TRUST
        return new FileDefinitionMaterial(Path.Combine(installRoot, "definitions"));
#else
        return new EmbeddedDefinitionMaterial(typeof(Winora.Domain.ErrorCode).Assembly);
#endif
    }
}

