// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Pipes;
using System.Security.Principal;
namespace Winora.Instance;

public enum HandOverResult
{
    /// <summary>The running instance took the request.</summary>
    Delivered,
    /// <summary>The running instance answered but refused (unknown key or too soon).</summary>
    Refused,
    /// <summary>No instance holds the pipe within the wait.</summary>
    NoServer,
    /// <summary>A process holds the pipe name that is not the expected executable. Nothing was sent.</summary>
    UntrustedServer,
    /// <summary>The connection failed after it opened.</summary>
    Failed,
}

/// <summary>The sending side, used by the launcher and by a second Desktop start.</summary>
public static class InstanceClient
{
    /// <summary>
    /// Sends <paramref name="key"/> (null to only bring the window forward) to the running instance. The server is checked first: its process must run
    /// <paramref name="expectedServerImage"/>. The client gives the server the right to take the foreground, so the window can come to the front.
    /// </summary>
    public static HandOverResult Send(string pipeName, string? key, string expectedServerImage, TimeSpan connectWait, TimeSpan replyWait)
        => Send(pipeName, key, pid => InstanceChannel.ProcessRunsImage(pid, expectedServerImage), connectWait, replyWait);

    /// <summary>Same with the server check supplied by the caller (tests).</summary>
    public static HandOverResult Send(string pipeName, string? key, Func<int, bool> serverIsTrusted, TimeSpan connectWait, TimeSpan replyWait)
    {
        var bytes = InstanceMessage.Encode(key);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        try { pipe.Connect((int)connectWait.TotalMilliseconds); }
        catch (TimeoutException) { return HandOverResult.NoServer; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return HandOverResult.NoServer; }
        try
        {
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || !serverIsTrusted((int)serverPid)) return HandOverResult.UntrustedServer;
            Native.AllowSetForegroundWindow(serverPid);
            using var limit = new CancellationTokenSource(replyWait);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            var answer = new byte[1];
            var read = pipe.ReadAsync(answer, limit.Token).AsTask().GetAwaiter().GetResult();
            if (read != 1) return HandOverResult.Failed;
            return answer[0] == (byte)HandOverReply.Accepted ? HandOverResult.Delivered : HandOverResult.Refused;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return HandOverResult.Failed; }
    }
}
