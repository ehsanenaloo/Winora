// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// Decides which pipe names a server may create. Production uses <see cref="ProductionPipeNames"/>; the
/// unelevated real-pipe tests inject a policy that accepts only a <c>winora-test-</c> prefix, so a test can never touch a production name.
/// </summary>
public interface IPipeNamePolicy
{
    bool Allows(string pipeName);
}

/// <summary>The production names: <c>\\.\pipe\Winora.Elev.&lt;32 hex&gt;</c> for the release flavor and <c>\\.\pipe\Winora.VmElev.&lt;32 hex&gt;</c> for the VM flavor.</summary>
public sealed class ProductionPipeNames(ElevationFlavor flavor) : IPipeNamePolicy
{
    public bool Allows(string pipeName)
    {
        string prefix = PipeCreationPlan.NameFor(default, flavor)[..^32];
        if (pipeName.Length != prefix.Length + 32 || !pipeName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        for (int i = prefix.Length; i < pipeName.Length; i++)
            if (!(pipeName[i] is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}

public enum PipeCreateStatus { Created, Squatted, NameNotAllowed, DescriptorInvalid, Failed }

public sealed record PipeCreateResult(PipeCreateStatus Status, int Win32Error, PipeServer? Server)
{
    public bool IsCreated => Status == PipeCreateStatus.Created;
}

/// <summary>Seams of the pipe server: the image check of a connecting client and the clock-free token reader are real by default.</summary>
public sealed class PipeServerOptions
{
    /// <summary>Called with the image path of the process that created the client end, only when that path is one of the allowed client images; true means the Authenticode and install rules of 7.2 step 2 hold and the signer equals the worker's.</summary>
    public required Func<string, bool> VerifyClientImage { get; init; }
    /// <summary>The image paths the worker accepts for its client (<c>desktop\Winora.Desktop.exe</c>, <c>cli\Winora.Cli.exe</c> under the install root).</summary>
    public required IReadOnlyList<string> AllowedClientImages { get; init; }
}

/// <summary>
/// The worker's single pipe instance. Created from a <see cref="PipeCreationPlan"/> with exactly the plan's open mode
/// (FIRST_PIPE_INSTANCE, overlapped), pipe mode (byte, wait, REJECT_REMOTE_CLIENTS), one instance and the exact descriptor of section 5. It implements
/// the connect loop's <see cref="IConnectionAcceptor"/>: <see cref="AcceptAsync"/> waits for a connection (cancelled by the host's deadlines), and a
/// rejected connection is freed with <see cref="IWorkerConnection.Disconnect"/> before the next accept. The token of the peer is read by
/// impersonation at the Identification level that the client's open requested; nothing else runs while impersonating.
/// This is the only file in the project that creates a pipe.
/// </summary>
public sealed class PipeServer : IConnectionAcceptor, IDisposable
{
    private readonly NamedPipeServerStream stream;
    private readonly SafePipeHandle handle;
    private readonly PipeServerOptions options;
    private int accepted;
    private int closed;

    private PipeServer(PipeCreationPlan plan, SafePipeHandle handle, NamedPipeServerStream stream, PipeServerOptions options)
    { Plan = plan; this.handle = handle; this.stream = stream; this.options = options; }

    public PipeCreationPlan Plan { get; }
    /// <summary>The open mode and pipe mode actually passed to CreateNamedPipeW (equal to the plan, kept for tests).</summary>
    public uint AppliedOpenMode => Plan.OpenMode;
    public uint AppliedPipeMode => Plan.PipeMode;
    /// <summary>Number of connections returned so far (rejected ones included).</summary>
    public int ConnectionsAccepted => Volatile.Read(ref accepted);

    /// <summary>
    /// Creates the pipe. A name the policy does not allow is refused before any call. <see cref="PipeCreateStatus.Squatted"/> means the name already existed
    ///: the caller reports the <c>squatted</c> reason.
    /// </summary>
    public static unsafe PipeCreateResult Create(PipeCreationPlan plan, IPipeNamePolicy namePolicy, PipeServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(namePolicy);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows()) return new(PipeCreateStatus.Failed, 0, null);
        if (!namePolicy.Allows(plan.Name)) return new(PipeCreateStatus.NameNotAllowed, 0, null);
        var verified = PipeSecurity.VerifyWithWindows(plan.Security);
        if (!verified.Ok) return new(PipeCreateStatus.DescriptorInvalid, 0, null);

        SafePipeHandle created;
        fixed (byte* sd = verified.Binary)
        {
            var attributes = new SecurityAttributes { nLength = (uint)sizeof(SecurityAttributes), lpSecurityDescriptor = (nint)sd, bInheritHandle = plan.InheritHandle ? 1 : 0 };
            created = Native.CreateNamedPipeW(plan.Name, plan.OpenMode, plan.PipeMode, plan.MaxInstances, plan.OutBufferBytes, plan.InBufferBytes, plan.DefaultTimeoutMs, &attributes);
        }
        if (created.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            created.Dispose();
            return new(err is 5 or 231 ? PipeCreateStatus.Squatted : PipeCreateStatus.Failed, err, null);
        }
        try
        {
            var wrapped = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, created);
            return new(PipeCreateStatus.Created, 0, new PipeServer(plan, created, wrapped, options));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            created.Dispose();
            return new(PipeCreateStatus.Failed, 0, null);
        }
    }

    public async ValueTask<IWorkerConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref closed) != 0) throw new ObjectDisposedException(nameof(PipeServer));
        await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref accepted);
        return new PipeConnection(this, stream, handle, options);
    }

    internal void DisconnectInstance()
    {
        try { if (stream.IsConnected) stream.Disconnect(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stream.Dispose();
    }

    /// <summary>One accepted connection: the framed channel, the identity source and the disconnect that frees the instance for the next client.</summary>
    private sealed class PipeConnection(PipeServer owner, NamedPipeServerStream stream, SafePipeHandle handle, PipeServerOptions options) : IWorkerConnection
    {
        public IFrameChannel Channel { get; } = new PipeFrameChannel(stream);
        public IPeerIdentity Peer { get; } = new PipePeerIdentity(stream, handle, options);
        public void Disconnect() => owner.DisconnectInstance();
    }
}

/// <summary>The byte channel over the connected pipe stream. Reads return 0 at end of stream; writes throw on a broken pipe; <see cref="Close"/> is idempotent and flushes before it closes.</summary>
internal sealed class PipeFrameChannel(NamedPipeServerStream stream) : IFrameChannel
{
    private int closed;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => stream.ReadAsync(buffer, cancellationToken);

    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        // Data already written stays readable by the client when the server handle closes; Disconnect would discard unread data, so only the stream is closed here.
        try { stream.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
}

/// <summary>
/// Reads the facts of 7.2 about the client of one connection. Token (primary): impersonate at the level the client's open allows,
/// open the thread token with OpenAsSelf, revert, then read user SID, logon SID, session, elevation and integrity. Image: the process id the kernel
/// reports for the client end, its image path and start time, and the signature verdict. PID and start time are secondary evidence only (A-2).
/// </summary>
internal sealed class PipePeerIdentity(NamedPipeServerStream stream, SafePipeHandle handle, PipeServerOptions options) : IPeerIdentity
{
    private const uint TokenQuery = 0x8;

    public PeerFacts? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        SafeKernelHandle? token = null;
        int tokenError = 0;
        try
        {
            // Nothing but OpenThreadToken runs while impersonating; RunAsClient reverts in a finally block. At identification level the thread cannot open
            // files, so the probe is warmed up (compiled, P/Invoke bound) before the first impersonation: see WarmUp.
            WarmUp();
            stream.RunAsClient(() => ProbeThreadToken(out token, out tokenError));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException) { return null; }
        if (token is null || tokenError != 0) return null;
        using (token)
        {
            var tokenRead = ProcessIdentityReader.ReadToken(token);
            if (!tokenRead.IsOk || tokenRead.Token is null) return null;
            var facts = tokenRead.Token;
            if (facts.LogonSid is null) return null;
            var user = SidText.ToBinary(facts.UserSid);
            var logon = SidText.ToBinary(facts.LogonSid);
            if (user is null || logon is null || !BinarySid.IsValid(user) || !BinarySid.IsValid(logon)) return null;

            uint pid = 0;
            ulong start = 0;
            string? image = null;
            BinarySid? processUser = null, processLogon = null;
            if (Native.GetNamedPipeClientProcessId(handle, out var observed) && observed != 0)
            {
                pid = observed;
                var proc = ProcessIdentityReader.ReadProcess(observed);
                if (proc.IsOk && proc.Process is { } peerFacts)
                {
                    image = peerFacts.ImagePath;
                    start = peerFacts.StartFiletime ?? 0;
                    if (SidText.ToBinary(proc.Token!.UserSid) is { } pu && BinarySid.IsValid(pu)) processUser = new BinarySid(pu);
                    if (proc.Token.LogonSid is not null && SidText.ToBinary(proc.Token.LogonSid) is { } pl && BinarySid.IsValid(pl)) processLogon = new BinarySid(pl);
                }
            }
            bool signerMatches = image is not null && options.AllowedClientImages.Any(p => string.Equals(p, image, StringComparison.OrdinalIgnoreCase)) && options.VerifyClientImage(image);
            return new PeerFacts(pid, start, new BinarySid(user), new BinarySid(logon), facts.SessionId, facts.IsElevated, MapIntegrity(facts.Integrity),
                image, signerMatches, processUser, processLogon);
        }
    }

    private static int warmedUp;

    /// <summary>
    /// The only code that runs while the thread impersonates the client: open the thread token with OpenAsSelf. Kept in its own method so that
    /// <see cref="WarmUp"/> can run the identical path once without impersonation (a first call under an identification-level token must not need to load
    /// anything: file opens fail there, which a lazy assembly load or a first P/Invoke binding could trip over).
    /// </summary>
    private static void ProbeThreadToken(out SafeKernelHandle? token, out int error)
    {
        token = null;
        error = 0;
        if (!Native.OpenThreadToken(Native.CurrentThreadPseudoHandle, TokenQuery, true, out var opened)) error = Marshal.GetLastPInvokeError();
        else token = opened;
    }

    /// <summary>Runs the probe once while not impersonating (no thread token yet: the call fails with ERROR_NO_TOKEN, which is the expected warm-up result) and touches the readers used right after the revert.</summary>
    internal static void WarmUp()
    {
        if (Interlocked.Exchange(ref warmedUp, 1) != 0) return;
        ProbeThreadToken(out var token, out _);
        token?.Dispose();
        _ = Marshal.GetLastPInvokeError();
        _ = ProcessIdentityReader.IsCurrentThreadImpersonating();
    }

    /// <summary>Maps the token integrity to the protocol enum conservatively: only exactly Medium maps to Medium; MediumPlus and unknown levels never pass as Medium.</summary>
    internal static Protocol.IntegrityLevel MapIntegrity(IntegrityLevel level) => level switch
    {
        IntegrityLevel.Untrusted => Protocol.IntegrityLevel.Untrusted,
        IntegrityLevel.Low => Protocol.IntegrityLevel.Low,
        IntegrityLevel.Medium => Protocol.IntegrityLevel.Medium,
        IntegrityLevel.System or IntegrityLevel.Protected => Protocol.IntegrityLevel.System,
        IntegrityLevel.MediumPlus or IntegrityLevel.High => Protocol.IntegrityLevel.High,
        _ => Protocol.IntegrityLevel.Untrusted,
    };
}
