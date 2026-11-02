// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// Bounded reader for the documented Shell Link (.lnk) binary format (MS-SHLLINK). It only parses bytes: it never loads
/// the shortcut through the shell and never launches or resolves the target. Any malformed input returns false.
/// </summary>
internal static class ShortcutParser
{
    public const int MaxBytes = 64 * 1024;
    private const int HeaderSize = 0x4C;

    public static bool TryRead(ReadOnlySpan<byte> data, out string? target, out string? arguments)
    {
        target = null; arguments = null;
        try { return Parse(data, out target, out arguments); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            target = null; arguments = null; return false;
        }
    }

    private static bool Parse(ReadOnlySpan<byte> d, out string? target, out string? arguments)
    {
        target = null; arguments = null;
        if (d.Length < HeaderSize || d.Length > MaxBytes) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != HeaderSize) return false;
        // CLSID 00021401-0000-0000-C000-000000000046
        ReadOnlySpan<byte> clsid = [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];
        if (!d.Slice(4, 16).SequenceEqual(clsid)) return false;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(d[20..]);
        bool hasIdList = (flags & 1) != 0, hasLinkInfo = (flags & 2) != 0, hasName = (flags & 4) != 0, hasRelative = (flags & 8) != 0,
            hasWorking = (flags & 16) != 0, hasArgs = (flags & 32) != 0, hasIcon = (flags & 64) != 0, unicode = (flags & 128) != 0;
        var pos = HeaderSize;
        if (hasIdList)
        {
            if (pos + 2 > d.Length) return false;
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(d[pos..]);
        }
        if (hasLinkInfo)
        {
            if (pos + 28 > d.Length) return false;
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[pos..]);
            if (size < 28 || pos + size > d.Length) return false;
            var info = d.Slice(pos, size);
            var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
            var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
            if ((infoFlags & 1) != 0)
            {
                string? basePath = null, suffix = null;
                if (headerSize >= 0x24 && info.Length >= 36)
                {
                    var bu = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[28..]);
                    var su = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
                    if (bu > 0) basePath = ReadZ(info, bu, true);
                    if (su > 0) suffix = ReadZ(info, su, true);
                }
                if (basePath is null)
                {
                    var bo = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
                    var so = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[24..]);
                    if (bo > 0) basePath = ReadZ(info, bo, false);
                    if (suffix is null && so > 0) suffix = ReadZ(info, so, false);
                }
                if (!string.IsNullOrEmpty(basePath)) target = basePath + (suffix ?? "");
            }
            pos += size;
        }
        // Optional StringData, in file order: name, relative path, working dir, arguments, icon location.
        var strings = new[] { (hasName, false), (hasRelative, false), (hasWorking, false), (hasArgs, true), (hasIcon, false) };
        foreach (var (present, isArgs) in strings)
        {
            if (!present) continue;
            if (pos + 2 > d.Length) return target is not null;
            var chars = BinaryPrimitives.ReadUInt16LittleEndian(d[pos..]);
            pos += 2;
            var bytes = unicode ? chars * 2 : chars;
            if (pos + bytes > d.Length) return target is not null;
            if (isArgs && bytes > 0) arguments = (unicode ? Encoding.Unicode : Encoding.Latin1).GetString(d.Slice(pos, Math.Min(bytes, 2048)));
            pos += bytes;
        }
        return target is not null;
    }
    private static string? ReadZ(ReadOnlySpan<byte> info, int offset, bool unicode)
    {
        if (offset < 0 || offset >= info.Length) return null;
        var rest = info[offset..];
        if (unicode)
        {
            var max = Math.Min(rest.Length / 2, 1024);
            var len = 0;
            while (len < max && (rest[len * 2] | rest[len * 2 + 1]) != 0) len++;
            return Encoding.Unicode.GetString(rest[..(len * 2)]);
        }
        var n = Math.Min(rest.Length, 1024);
        var l = 0;
        while (l < n && rest[l] != 0) l++;
        return Encoding.Latin1.GetString(rest[..l]);
    }
}
