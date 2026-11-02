// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Security.Cryptography;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>Direction of a frame; the numeric value is part of the AES-GCM nonce.</summary>
public enum FrameDirection : uint { ClientToWorker = 1, WorkerToClient = 2 }

/// <summary>A set of message types, used to state which frames a session state accepts.</summary>
public readonly struct MessageTypeSet
{
    private readonly UInt128 bits;
    private MessageTypeSet(UInt128 bits) { this.bits = bits; }
    public static MessageTypeSet None => default;
    public static MessageTypeSet Of(params MessageType[] types)
    {
        UInt128 bits = 0;
        foreach (var t in types) bits |= UInt128.One << (byte)t;
        return new(bits);
    }
    public bool Contains(byte rawType) => rawType < 128 && ((bits >> rawType) & UInt128.One) != UInt128.Zero;
    public bool Contains(MessageType type) => Contains((byte)type);
    public MessageTypeSet Plus(MessageType type) => new(bits | (UInt128.One << (byte)type));
    public bool IsEmpty => bits == UInt128.Zero;
}

/// <summary>The 16-byte frame header: magic, protocol, type, payload length, counter.</summary>
public readonly record struct FrameHeader(byte Proto, byte RawType, uint Length, ulong Counter)
{
    public const int Size = ElevationLimits.FrameHeaderBytes;
    public MessageType Type => (MessageType)RawType;

    /// <summary>Parses exactly 16 bytes; false when the length or the magic is wrong.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, out FrameHeader header)
    {
        header = default;
        if (bytes.Length != Size || bytes[0] != ElevationLimits.MagicByte0 || bytes[1] != ElevationLimits.MagicByte1) return false;
        header = new(bytes[2], bytes[3], BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));
        return true;
    }

    public void WriteTo(Span<byte> destination)
    {
        destination[0] = ElevationLimits.MagicByte0; destination[1] = ElevationLimits.MagicByte1; destination[2] = Proto; destination[3] = RawType;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Length);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], Counter);
    }
}

/// <summary>Header validation before any payload is read or allocated. Order: framing, size, counter, state.</summary>
public static class FrameRules
{
    public static bool IsKnownType(byte raw) => raw is 0x01 or 0x02 or 0x10 or 0x20 or 0x21 or 0x30 or 0x31 or 0x32 or 0x33 or 0x40 or 0x41;

    /// <summary>The pre-key frames: hello frames and an advisory plaintext Abort (counter 0, no tag).</summary>
    public static ProtocolFailure? ValidatePlain(in FrameHeader h, byte proto, MessageTypeSet allowed)
    {
        if (h.Proto != proto || !IsKnownType(h.RawType)) return ProtocolFailure.Channel(ProtocolDetail.Framing);
        var cap = h.Type == MessageType.Abort ? ElevationLimits.MaxPlainAbortPayloadBytes : ElevationLimits.MaxHelloPayloadBytes;
        if (h.Length > cap) return ProtocolFailure.Channel(ProtocolDetail.Size);
        if (h.Counter != 0) return ProtocolFailure.Channel(ProtocolDetail.Counter);
        if (!allowed.Contains(h.RawType) || h.Type is not (MessageType.ClientHello or MessageType.ServerHello or MessageType.Abort)) return ProtocolFailure.Channel(ProtocolDetail.State);
        return null;
    }

    /// <summary>Encrypted frames: strict next counter, hard cap of 64 frames per direction, size cap, type allowed in the current state.</summary>
    public static ProtocolFailure? ValidateEncrypted(in FrameHeader h, byte proto, ulong expectedCounter, MessageTypeSet allowed)
    {
        if (h.Proto != proto || !IsKnownType(h.RawType)) return ProtocolFailure.Channel(ProtocolDetail.Framing);
        if (h.Length > ElevationLimits.MaxCiphertextBytes) return ProtocolFailure.Channel(ProtocolDetail.Size);
        if (expectedCounter >= ElevationLimits.MaxFramesPerDirection || h.Counter != expectedCounter) return ProtocolFailure.Channel(ProtocolDetail.Counter);
        if (!allowed.Contains(h.RawType) || h.Type is MessageType.ClientHello or MessageType.ServerHello) return ProtocolFailure.Channel(ProtocolDetail.State);
        return null;
    }

    /// <summary>A plaintext frame: header (counter 0) followed by the payload, no tag.</summary>
    public static byte[] EncodePlain(MessageType type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[FrameHeader.Size + payload.Length];
        new FrameHeader(ElevationLimits.ProtocolVersion, (byte)type, (uint)payload.Length, 0).WriteTo(frame);
        payload.CopyTo(frame.AsSpan(FrameHeader.Size));
        return frame;
    }
}

public readonly record struct FrameSealResult(byte[]? Frame, ProtocolFailure? Failure)
{
    public bool IsSuccess => Frame is not null;
}

public readonly record struct FrameOpenResult(MessageType Type, byte[]? Payload, ProtocolFailure? Failure)
{
    public bool IsSuccess => Payload is not null;
}

/// <summary>
/// Sealing and opening of encrypted frames with AES-256-GCM. One instance serves one session and one role: it
/// seals with the key of its own direction and opens with the key of the other, with strictly increasing counters starting at 0. The
/// nonce is the direction (u32) and the counter (u64); the associated data is the 16 header bytes. Replays, reordering, gaps, wrong
/// directions, oversized frames and the 64-frame cap are refused with typed failures; no operation throws for hostile input.
/// </summary>
public sealed class FrameCodec : IDisposable
{
    private readonly byte proto;
    private readonly FrameDirection sendDirection;
    private readonly FrameDirection receiveDirection;
    private readonly AesGcm sender;
    private readonly AesGcm receiver;
    private ulong sendCounter;
    private ulong receiveCounter;

    public FrameCodec(FrameDirection sendDirection, byte proto, ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey)
    {
        if (sendDirection is not (FrameDirection.ClientToWorker or FrameDirection.WorkerToClient)) throw new ArgumentOutOfRangeException(nameof(sendDirection));
        this.sendDirection = sendDirection;
        receiveDirection = sendDirection == FrameDirection.ClientToWorker ? FrameDirection.WorkerToClient : FrameDirection.ClientToWorker;
        this.proto = proto;
        sender = new AesGcm(sendKey, ElevationLimits.TagBytes);
        receiver = new AesGcm(receiveKey, ElevationLimits.TagBytes);
    }

    public byte Proto => proto;
    public ulong NextSendCounter => sendCounter;
    public ulong NextReceiveCounter => receiveCounter;

    /// <summary>Encrypts one frame with the next send counter. Fails with <c>size</c> or <c>counter</c> (nonce-exhaustion guard) and then consumes nothing.</summary>
    public FrameSealResult Seal(MessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ElevationLimits.MaxCiphertextBytes) return new(null, ProtocolFailure.Channel(ProtocolDetail.Size));
        if (sendCounter >= ElevationLimits.MaxFramesPerDirection) return new(null, ProtocolFailure.Channel(ProtocolDetail.Counter));
        var frame = new byte[FrameHeader.Size + payload.Length + ElevationLimits.TagBytes];
        new FrameHeader(proto, (byte)type, (uint)payload.Length, sendCounter).WriteTo(frame);
        Span<byte> nonce = stackalloc byte[ElevationLimits.NonceBytes];
        WriteNonce(nonce, sendDirection, sendCounter);
        sender.Encrypt(nonce, payload, frame.AsSpan(FrameHeader.Size, payload.Length), frame.AsSpan(FrameHeader.Size + payload.Length, ElevationLimits.TagBytes), frame.AsSpan(0, FrameHeader.Size));
        sendCounter++;
        return new(frame, null);
    }

    /// <summary>Validates a parsed header against the next receive counter and the allowed types, without consuming anything.</summary>
    public ProtocolFailure? CheckHeader(in FrameHeader header, MessageTypeSet allowed) => FrameRules.ValidateEncrypted(header, proto, receiveCounter, allowed);

    /// <summary>Opens a frame whose header has been validated. Advances the receive counter only on success.</summary>
    public FrameOpenResult Open(ReadOnlySpan<byte> headerBytes, ReadOnlySpan<byte> ciphertextAndTag)
    {
        if (!FrameHeader.TryParse(headerBytes, out var header) || header.Proto != proto) return new(0, null, ProtocolFailure.Channel(ProtocolDetail.Framing));
        if (ciphertextAndTag.Length != (long)header.Length + ElevationLimits.TagBytes) return new(0, null, ProtocolFailure.Channel(ProtocolDetail.Framing));
        if (header.Counter != receiveCounter || receiveCounter >= ElevationLimits.MaxFramesPerDirection) return new(0, null, ProtocolFailure.Channel(ProtocolDetail.Counter));
        var n = (int)header.Length;
        var plaintext = new byte[n];
        Span<byte> nonce = stackalloc byte[ElevationLimits.NonceBytes];
        WriteNonce(nonce, receiveDirection, header.Counter);
        try
        {
            receiver.Decrypt(nonce, ciphertextAndTag[..n], ciphertextAndTag[n..], plaintext, headerBytes);
        }
        catch (CryptographicException)
        {
            return new(0, null, ProtocolFailure.Channel(ProtocolDetail.Tag));
        }
        receiveCounter++;
        return new(header.Type, plaintext, null);
    }

    /// <summary>Convenience for a whole frame in memory: parses and validates the header, then opens it. Any trailing byte is a framing error.</summary>
    public FrameOpenResult OpenFrame(ReadOnlySpan<byte> frame, MessageTypeSet allowed)
    {
        if (frame.Length < FrameHeader.Size + ElevationLimits.TagBytes || !FrameHeader.TryParse(frame[..FrameHeader.Size], out var header))
            return new(0, null, ProtocolFailure.Channel(ProtocolDetail.Framing));
        var failure = CheckHeader(header, allowed);
        return failure is not null ? new(0, null, failure) : Open(frame[..FrameHeader.Size], frame[FrameHeader.Size..]);
    }

    private static void WriteNonce(Span<byte> nonce, FrameDirection direction, ulong counter)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, (uint)direction);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    public void Dispose()
    {
        sender.Dispose();
        receiver.Dispose();
    }
}
