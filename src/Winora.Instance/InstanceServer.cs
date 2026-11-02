// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Pipes;
using System.Security.Principal;
namespace Winora.Instance;

/// <summary>
/// The listening side. The first process that creates the pipe is the running instance. It waits with one asynchronous pipe operation (no timer, no thread of its own)
/// and handles one connection at a time. A connection must be from the same session and the same user, send one message within <see cref="ReadLimit"/>,
/// and carry a key from <see cref="PageKeys.Allowed"/> (or no key). Everything else is refused without effect.
/// </summary>
public sealed class InstanceServer : IDisposable
{
    public static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(300);

    private readonly string pipeName, userSid;
    private readonly int sessionId;
    private readonly Action<string> log;
    private readonly Func<DateTime> clock;
    private readonly CancellationTokenSource stop = new();
    private NamedPipeServerStream? waiting;
    private Task? loop;
    private DateTime lastAccepted = DateTime.MinValue;

    private InstanceServer(string pipeName, string userSid, int sessionId, NamedPipeServerStream first, Action<string> log, Func<DateTime> clock)
    { this.pipeName = pipeName; this.userSid = userSid; this.sessionId = sessionId; waiting = first; this.log = log; this.clock = clock; }

    /// <summary>
    /// Tries to become the running instance. Returns null when another process already holds the pipe name (this one is a second start).
    /// <paramref name="log"/> receives short fixed texts; message content is logged only when it is a plain page key.
    /// </summary>
    public static InstanceServer? TryClaim(string pipeName, string userSid, int sessionId, Action<string>? log = null, Func<DateTime>? clock = null)
    {
        try { return new InstanceServer(pipeName, userSid, sessionId, Create(pipeName, userSid), log ?? (_ => { }), clock ?? (() => DateTime.UtcNow)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Claims the pipe of the current user and session.</summary>
    public static InstanceServer? TryClaimCurrent(Action<string>? log = null) =>
        TryClaim(InstanceChannel.CurrentPipeName(), InstanceChannel.CurrentUserSid(), System.Diagnostics.Process.GetCurrentProcess().SessionId, log);

    private static NamedPipeServerStream Create(string pipeName, string userSid)
    {
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm(InstanceChannel.Sddl(userSid));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 128, 128, security);
    }

    /// <summary>
    /// Starts accepting. <paramref name="navigate"/> is called on a pool thread with the key (null to only bring the window forward); it returns whether the window took the request.
    /// Messages sent before this call wait in the pipe.
    /// </summary>
    public void Start(Func<string?, bool> navigate)
    {
        if (loop is not null) throw new InvalidOperationException("The server is already started.");
        loop = Task.Run(() => RunAsync(navigate));
    }

    private async Task RunAsync(Func<string?, bool> navigate)
    {
        // The one pipe instance is kept for the life of the server and handed to the next client with Disconnect: the name is never free, so a second start cannot take it.
        while (!stop.IsCancellationRequested)
        {
            var pipe = waiting;
            if (pipe is null) break;
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                await HandleAsync(pipe, navigate).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException or InvalidOperationException) { log("A hand-over connection failed"); }
            if (stop.IsCancellationRequested) break;
            try { pipe.Disconnect(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The instance cannot be reused: replace it. The name is free for a moment only on this rare path.
                waiting = null;
                try { pipe.Dispose(); } catch (IOException) { }
                try { waiting = Create(pipeName, userSid); }
                catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { log("The hand-over pipe could not be created again; later starts open a new window"); break; }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, Func<string?, bool> navigate)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        limit.CancelAfter(ReadLimit);
        if (!Native.GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var clientSession) || clientSession != (uint)sessionId) { log("A hand-over from another session was refused"); return; }
        var length = new byte[1];
        if (await ReadExactAsync(pipe, length, limit.Token).ConfigureAwait(false) != 1) { log("A hand-over without data was refused"); return; }
        if (length[0] is 0 or > InstanceMessage.MaxBytes) { await ReplyAsync(pipe, HandOverReply.Malformed).ConfigureAwait(false); log("A hand-over with a wrong length was refused"); return; }
        var payload = new byte[length[0]];
        if (await ReadExactAsync(pipe, payload, limit.Token).ConfigureAwait(false) != payload.Length) { log("A hand-over that stopped early was refused"); return; }
        // The user of the client token can only be read after data arrived.
        string? clientSid = null;
        pipe.RunAsClient(() => clientSid = WindowsIdentity.GetCurrent().User?.Value);
        if (!string.Equals(clientSid, userSid, StringComparison.OrdinalIgnoreCase)) { log("A hand-over from another user was refused"); return; }
        var reply = Decide(payload, navigate);
        await ReplyAsync(pipe, reply).ConfigureAwait(false);
        // Wait for the client to close so the answer is read before the pipe goes away.
        try { await pipe.ReadAsync(length, limit.Token).ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
    }

    private HandOverReply Decide(byte[] payload, Func<string?, bool> navigate)
    {
        if (!InstanceMessage.TryDecode(payload, out var key)) { log("A malformed hand-over was refused"); return HandOverReply.Malformed; }
        if (key is not null && !PageKeys.IsAllowed(key)) { log($"A hand-over with the unknown page key '{key}' was ignored"); return HandOverReply.UnknownKey; }
        var now = clock();
        if (now - lastAccepted < MinGap) { log("A hand-over that came too soon was ignored"); return HandOverReply.TooSoon; }
        lastAccepted = now;
        return navigate(key) ? HandOverReply.Accepted : HandOverReply.TooSoon;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            int n;
            try { n = await stream.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return read; }
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    private static async Task ReplyAsync(Stream pipe, HandOverReply reply)
    {
        await pipe.WriteAsync(new[] { (byte)reply }).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        stop.Cancel();
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        try { waiting?.Dispose(); } catch (IOException) { }
        stop.Dispose();
    }
}
