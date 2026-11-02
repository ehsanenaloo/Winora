// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>A cancellation scope with a deadline measured by an injected <see cref="TimeProvider"/>, linked to an outer token.</summary>
internal sealed class DeadlineScope : IDisposable
{
    private readonly CancellationTokenSource source;
    private readonly CancellationTokenRegistration registration;
    private readonly CancellationToken outer;

    public DeadlineScope(TimeSpan timeout, TimeProvider time, CancellationToken outer)
    {
        this.outer = outer;
        source = new CancellationTokenSource(timeout, time);
        registration = outer.Register(static s => ((CancellationTokenSource)s!).Cancel(), source);
    }
    public CancellationToken Token => source.Token;
    /// <summary>True when the deadline (not the caller) cancelled the scope.</summary>
    public bool Expired => source.IsCancellationRequested && !outer.IsCancellationRequested;
    public void Dispose()
    {
        registration.Dispose();
        source.Dispose();
    }
}

/// <summary>How reading one frame ended.</summary>
public enum FrameReadStatus { Frame, TimedOut, Closed, Failed }

/// <summary>A frame read from a channel: the decrypted payload (plain frames: the payload as sent) or a typed status.</summary>
public readonly record struct FrameRead(FrameReadStatus Status, MessageType Type, byte[] Payload, ProtocolFailure? Failure)
{
    public static FrameRead Closed() => new(FrameReadStatus.Closed, 0, [], ProtocolFailure.Channel(ProtocolDetail.Framing));
    public static FrameRead Timeout() => new(FrameReadStatus.TimedOut, 0, [], null);
    public static FrameRead Fail(ProtocolFailure failure) => new(FrameReadStatus.Failed, 0, [], failure);
}

/// <summary>
/// Frame transport over <see cref="IFrameChannel"/>: exact-length reads, header validation before allocation, a state deadline for the
/// first byte and a per-frame deadline (10 s) from the first byte to the last. Any shortfall, trailing garbage or channel error ends the
/// read with a typed status; there is no resynchronization. Only caller cancellation propagates as an exception.
/// </summary>
public static class FrameIo
{
    public static async ValueTask<FrameRead> ReadAsync(
        IFrameChannel channel, FrameCodec? codec, byte proto, MessageTypeSet allowed, TimeSpan stateWait, TimeProvider time, CancellationToken ct)
    {
        var header = new byte[FrameHeader.Size];
        int got;
        try
        {
            using var wait = new DeadlineScope(stateWait, time, ct);
            try { got = await channel.ReadAsync(header, wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (wait.Expired) { return FrameRead.Timeout(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return FrameRead.Closed(); }
        if (got <= 0) return FrameRead.Closed();

        try
        {
            using var frame = new DeadlineScope(TimeSpan.FromMilliseconds(ElevationLimits.FrameReadMs), time, ct);
            try
            {
                if (!await ReadExactAsync(channel, header.AsMemory(got), frame.Token).ConfigureAwait(false)) return FrameRead.Closed();
                if (!FrameHeader.TryParse(header, out var parsed)) return FrameRead.Fail(ProtocolFailure.Channel(ProtocolDetail.Framing));
                var failure = codec is null
                    ? FrameRules.ValidatePlain(parsed, proto, allowed)
                    : codec.CheckHeader(parsed, allowed);
                if (failure is not null) return FrameRead.Fail(failure);

                var body = new byte[codec is null ? parsed.Length : parsed.Length + ElevationLimits.TagBytes];
                if (!await ReadExactAsync(channel, body, frame.Token).ConfigureAwait(false)) return FrameRead.Closed();
                if (codec is null) return new(FrameReadStatus.Frame, parsed.Type, body, null);
                var opened = codec.Open(header, body);
                return opened.IsSuccess ? new(FrameReadStatus.Frame, opened.Type, opened.Payload!, null) : FrameRead.Fail(opened.Failure!);
            }
            catch (OperationCanceledException) when (frame.Expired) { return FrameRead.Timeout(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return FrameRead.Closed(); }
    }

    private static async ValueTask<bool> ReadExactAsync(IFrameChannel channel, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await channel.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }

    /// <summary>Writes one whole frame; null on success, a framing failure when the channel is closed or broken.</summary>
    public static async ValueTask<ProtocolFailure?> WriteAsync(IFrameChannel channel, byte[] frame, CancellationToken ct)
    {
        try
        {
            await channel.WriteAsync(frame, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProtocolFailure.Channel(ProtocolDetail.Framing);
        }
    }
}
