// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
namespace Winora.Instance;

/// <summary>What the server answers, one byte.</summary>
public enum HandOverReply : byte
{
    Accepted = (byte)'K',
    UnknownKey = (byte)'U',
    Malformed = (byte)'M',
    TooSoon = (byte)'B',
}

/// <summary>
/// The only message of the channel: one length byte (1 to 64) and that many ASCII bytes, <c>winora-nav-1:</c> followed by a page key of 0 to 32 letters.
/// An empty key means "bring the window to the front". There is no other command.
/// </summary>
public static class InstanceMessage
{
    public const int MaxBytes = 64;
    public const string Prefix = "winora-nav-1:";

    /// <summary>The bytes to send (length byte first). Throws for a key that is not 1 to 32 ASCII letters; null means no page.</summary>
    public static byte[] Encode(string? key)
    {
        if (key is not null && !PageKeys.IsWellFormed(key)) throw new ArgumentException("The page key must be 1 to 32 ASCII letters.", nameof(key));
        var text = Prefix + key;
        var bytes = new byte[1 + text.Length];
        bytes[0] = (byte)text.Length;
        Encoding.ASCII.GetBytes(text, 0, text.Length, bytes, 1);
        return bytes;
    }

    /// <summary>Reads the payload (without the length byte). False when it is not a navigation message of this version.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out string? key)
    {
        key = null;
        if (payload.Length is 0 or > MaxBytes) return false;
        foreach (var b in payload) if (b is < 0x20 or > 0x7E) return false;
        var text = Encoding.ASCII.GetString(payload);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = text[Prefix.Length..];
        if (rest.Length == 0) return true;
        if (!PageKeys.IsWellFormed(rest)) return false;
        key = rest;
        return true;
    }
}
