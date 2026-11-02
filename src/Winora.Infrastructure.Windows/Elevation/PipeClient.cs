// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;
using EW = Winora.Elevation.Windows;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>An open pipe plus the process id the kernel reports for its server end (secondary evidence only, technical-docs/protocol.md 6.2).</summary>
public sealed record PipeConnection(IFrameChannel Channel, uint? ServerProcessId);

/// <summary>Opens the worker's pipe for one rendezvous. Failures are typed (<see cref="PipeConnectException"/>), never raw exceptions.</summary>
public interface IPipeConnector
{
    Task<PipeConnection> ConnectAsync(Bytes16 rendezvous, ElevationFlavor flavor, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>A connect failure expressed as a Domain code and closed detail token. Nothing was sent when this is thrown.</summary>
public sealed class PipeConnectException(string code, string detail = "") : Exception(code)
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;
}

/// <summary>
/// Client end of the worker pipe (technical-docs/protocol.md 5, 7.1). It opens with exactly the desired access of section 5, <c>0x00100083</c>
/// (read data, write data, read attributes, synchronize; no create-instance and no write-attributes right) and at the
/// SECURITY_IDENTIFICATION impersonation level, so a server that is not the worker can read who the client is but cannot act as it.
/// It never changes pipe state after connecting and sends nothing by itself: the session layer writes the first frame only after the
/// attestation was accepted and the secondary server checks ran.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeClient : IFrameChannel, IDisposable
{
    /// <summary>The exact rights requested on open; equals <see cref="EW.PipeRights.ClientDesired"/> (0x00100083).</summary>
    public const PipeAccessRights DesiredAccess = PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.Synchronize;
    public const TokenImpersonationLevel ImpersonationLevel = TokenImpersonationLevel.Identification;

    private const string PipePrefix = @"\\.\pipe\";
    private readonly NamedPipeClientStream stream;
    private int closed;

    private PipeClient(NamedPipeClientStream stream, uint? serverProcessId) { this.stream = stream; ServerProcessId = serverProcessId; }

    public uint? ServerProcessId { get; }

    /// <summary>The short pipe name (without the <c>\\.\pipe\</c> prefix) derived from the rendezvous exactly as the worker derives it.</summary>
    public static string PipeNameFor(Bytes16 rendezvous, ElevationFlavor flavor)
    {
        var full = EW.PipeCreationPlan.NameFor(rendezvous, flavor);
        return full[PipePrefix.Length..];
    }

    public static Task<PipeClient> ConnectAsync(Bytes16 rendezvous, ElevationFlavor flavor, TimeSpan timeout, CancellationToken cancellationToken) =>
        ConnectByNameAsync(PipeNameFor(rendezvous, flavor), timeout, cancellationToken);

    /// <summary>Connects to a pipe by its short name. Internal so that tests can use a unique <c>winora-test-&lt;guid&gt;</c> name; production code derives the name from R.</summary>
    internal static async Task<PipeClient> ConnectByNameAsync(string shortName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stream = new NamedPipeClientStream(".", shortName, DesiredAccess, PipeOptions.Asynchronous, ImpersonationLevel, HandleInheritability.None);
        try
        {
            await stream.ConnectAsync(timeout <= TimeSpan.Zero ? 1 : (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { stream.Dispose(); throw new PipeConnectException(ErrorCode.ElevationTimeout, "connect"); }
        catch (UnauthorizedAccessException) { stream.Dispose(); throw new PipeConnectException(ErrorCode.ElevationServerUntrusted); }
        catch (OperationCanceledException) { stream.Dispose(); throw; }
        catch (IOException) { stream.Dispose(); throw new PipeConnectException(ErrorCode.ElevationChannelFailed, "handshake"); }
        return new PipeClient(stream, ReadServerProcessId(stream.SafePipeHandle));
    }

    private static uint? ReadServerProcessId(SafePipeHandle handle) => GetNamedPipeServerProcessId(handle, out var pid) ? pid : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => stream.ReadAsync(buffer, cancellationToken);

    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stream.Dispose();
    }

    public void Dispose() => Close();
}

/// <summary>Production connector: derives the pipe name from R and the flavor only.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPipeConnector : IPipeConnector
{
    public async Task<PipeConnection> ConnectAsync(Bytes16 rendezvous, ElevationFlavor flavor, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = await PipeClient.ConnectAsync(rendezvous, flavor, timeout, cancellationToken).ConfigureAwait(false);
        return new PipeConnection(client, client.ServerProcessId);
    }
}

/// <summary>What the client learned about the pipe server's process. Facts are best effort: a Medium client may not be allowed to open an elevated process.</summary>
/// <param name="Facts">Pid, start time and image of the server process, or null when they could not be read.</param>
/// <param name="TokenElevated">Elevation of the server token when it could be read, else null.</param>
public sealed record ServerInspection(PeerFacts? Facts, bool? TokenElevated);

public interface IServerProcessInspector
{
    ServerInspection Inspect(uint? serverProcessId);
}

/// <summary>Reads the server process through <see cref="EW.ProcessIdentityReader"/> with PROCESS_QUERY_LIMITED_INFORMATION only.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServerProcessInspector : IServerProcessInspector
{
    public ServerInspection Inspect(uint? serverProcessId)
    {
        if (serverProcessId is not { } pid) return new(null, null);
        var read = EW.ProcessIdentityReader.ReadProcess(pid);
        if (!read.IsOk || read.Process?.StartFiletime is not { } start) return new(null, null);
        var token = read.Token;
        var sid = ToSid(token?.UserSid) ?? NullSid;
        var facts = new PeerFacts(pid, start, sid, ToSid(token?.LogonSid) ?? NullSid, read.Process.SessionId ?? 0, token?.IsElevated ?? false,
            token is null ? IntegrityLevel.Untrusted : MapIntegrity(token.Integrity), read.Process.ImagePath, SignerMatches: false);
        return new(facts, token?.IsElevated);
    }

    private static BinarySid NullSid { get; } = new([1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    private static BinarySid? ToSid(string? text) => text is not null && EW.SidText.ToBinary(text) is { } b && BinarySid.IsValid(b) ? new BinarySid(b) : null;

    private static IntegrityLevel MapIntegrity(EW.IntegrityLevel level) => level switch
    {
        EW.IntegrityLevel.Low => IntegrityLevel.Low,
        EW.IntegrityLevel.Medium or EW.IntegrityLevel.MediumPlus => IntegrityLevel.Medium,
        EW.IntegrityLevel.High => IntegrityLevel.High,
        EW.IntegrityLevel.System or EW.IntegrityLevel.Protected => IntegrityLevel.System,
        _ => IntegrityLevel.Untrusted,
    };
}
