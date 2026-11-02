// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Winora.Elevation.Windows;

/// <summary>SID text helpers and the well-known SIDs the trust rules name. Pure logic.</summary>
public static class SidText
{
    public const string LocalSystem = "S-1-5-18";
    public const string Administrators = "S-1-5-32-544";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    public const string Network = "S-1-5-2";
    public const string CreatorOwner = "S-1-3-0";
    public const string CreatorGroup = "S-1-3-1";
    public const string MediumIntegrity = "S-1-16-8192";

    /// <summary>Text form of a binary SID; null when malformed. <paramref name="length"/> is the byte length consumed.</summary>
    public static string? FromBinary(ReadOnlySpan<byte> sid, out int length)
    {
        length = 0;
        if (sid.Length < 8 || sid[0] != 1 || sid[1] > 15) return null;
        int total = 8 + 4 * sid[1];
        if (sid.Length < total) return null;
        ulong authority = 0;
        for (int i = 2; i < 8; i++) authority = (authority << 8) | sid[i];
        var sb = new StringBuilder("S-1-", 32);
        sb.Append(authority.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < sid[1]; i++)
            sb.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(sid[(8 + 4 * i)..]).ToString(CultureInfo.InvariantCulture));
        length = total;
        return sb.ToString();
    }

    /// <summary>Strict SID text grammar: <c>S-1-&lt;authority&gt;(-&lt;sub&gt;){1,15}</c>, decimal only, no signs, no leading zeros beyond one digit.</summary>
    public static bool IsValid(string? text)
    {
        if (text is null || text.Length < 7 || text.Length > 120 || !text.StartsWith("S-1-", StringComparison.Ordinal)) return false;
        var parts = text.Split('-');
        if (parts.Length < 4 || parts.Length > 3 + 15) return false;
        if (!TryDecimal(parts[2], out var authority) || authority > 0xFFFFFFFFFFFFUL) return false;
        for (int i = 3; i < parts.Length; i++) if (!TryDecimal(parts[i], out var sub) || sub > uint.MaxValue) return false;
        return true;
    }

    /// <summary>A logon SID: <c>S-1-5-5-X-Y</c>.</summary>
    public static bool IsLogonSid(string? text)
    {
        if (!IsValid(text) || !text!.StartsWith("S-1-5-5-", StringComparison.Ordinal)) return false;
        return text.Split('-').Length == 6;
    }

    /// <summary>Binary form of a valid SID text (used to feed in-memory conversions and tests).</summary>
    public static byte[]? ToBinary(string? text)
    {
        if (!IsValid(text)) return null;
        var parts = text!.Split('-');
        int subs = parts.Length - 3;
        var bytes = new byte[8 + 4 * subs];
        bytes[0] = 1; bytes[1] = (byte)subs;
        ulong authority = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
        for (int i = 0; i < 6; i++) bytes[2 + i] = (byte)(authority >> (8 * (5 - i)));
        for (int i = 0; i < subs; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + 4 * i), uint.Parse(parts[3 + i], CultureInfo.InvariantCulture));
        return bytes;
    }

    public static bool IsTrustedInstallPrincipal(string? sid) =>
        string.Equals(sid, LocalSystem, StringComparison.Ordinal) || string.Equals(sid, Administrators, StringComparison.Ordinal) || string.Equals(sid, TrustedInstaller, StringComparison.Ordinal);

    private static bool TryDecimal(string s, out ulong value)
    {
        value = 0;
        if (s.Length == 0 || s.Length > 20 || (s.Length > 1 && s[0] == '0')) return false;
        foreach (var c in s) if (c is < '0' or > '9') return false;
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>ACE types read by the parser (winnt.h). Only the kinds that matter for the trust rules are interpreted.</summary>
public static class AceTypes
{
    public const byte AccessAllowed = 0x00, AccessDenied = 0x01, SystemAudit = 0x02, AccessAllowedObject = 0x05, AccessDeniedObject = 0x06,
        AccessAllowedCallback = 0x09, AccessDeniedCallback = 0x0A, AccessAllowedCallbackObject = 0x0B, AccessDeniedCallbackObject = 0x0C, MandatoryLabel = 0x11;
    public const byte InheritOnly = 0x08;

    public static bool IsAllow(byte type) => type is AccessAllowed or AccessAllowedObject or AccessAllowedCallback or AccessAllowedCallbackObject;
    public static bool IsDeny(byte type) => type is AccessDenied or AccessDeniedObject or AccessDeniedCallback or AccessDeniedCallbackObject;
}

public sealed record AceInfo(byte Type, byte Flags, uint Mask, string Sid);

/// <summary>Parsed self-relative security descriptor: plain data. <c>Dacl</c> is null when the descriptor has no DACL (everyone has full access).</summary>
public sealed record SecurityDescriptorInfo(string? OwnerSid, string? GroupSid, bool DaclProtected, IReadOnlyList<AceInfo>? Dacl, IReadOnlyList<AceInfo> Sacl);

/// <summary>Managed reader of self-relative security descriptors, so ACL evaluation is pure logic that tests can feed with generated bytes.</summary>
public static class SecurityDescriptorParser
{
    private const ushort SeDaclPresent = 0x0004, SeSaclPresent = 0x0010, SeDaclProtected = 0x1000, SeSelfRelative = 0x8000;

    public static bool TryParse(ReadOnlySpan<byte> sd, out SecurityDescriptorInfo? info)
    {
        info = null;
        if (sd.Length < 20 || sd[0] != 1) return false;
        ushort control = BinaryPrimitives.ReadUInt16LittleEndian(sd[2..]);
        if ((control & SeSelfRelative) == 0) return false;
        uint ownerOff = BinaryPrimitives.ReadUInt32LittleEndian(sd[4..]), groupOff = BinaryPrimitives.ReadUInt32LittleEndian(sd[8..]);
        uint saclOff = BinaryPrimitives.ReadUInt32LittleEndian(sd[12..]), daclOff = BinaryPrimitives.ReadUInt32LittleEndian(sd[16..]);
        string? owner = null, group = null;
        if (ownerOff != 0 && !TrySid(sd, ownerOff, out owner)) return false;
        if (groupOff != 0 && !TrySid(sd, groupOff, out group)) return false;
        IReadOnlyList<AceInfo>? dacl = null;
        if ((control & SeDaclPresent) != 0 && daclOff != 0 && !TryAcl(sd, daclOff, out dacl)) return false;
        IReadOnlyList<AceInfo> sacl = Array.Empty<AceInfo>();
        if ((control & SeSaclPresent) != 0 && saclOff != 0)
        {
            if (!TryAcl(sd, saclOff, out var s) || s is null) return false;
            sacl = s;
        }
        info = new SecurityDescriptorInfo(owner, group, (control & SeDaclProtected) != 0, dacl, sacl);
        return true;
    }

    private static bool TrySid(ReadOnlySpan<byte> sd, uint offset, out string? sid)
    {
        sid = null;
        if (offset >= (uint)sd.Length) return false;
        sid = SidText.FromBinary(sd[(int)offset..], out _);
        return sid is not null;
    }

    private static bool TryAcl(ReadOnlySpan<byte> sd, uint offset, out IReadOnlyList<AceInfo>? aces)
    {
        aces = null;
        if ((long)offset + 8 > sd.Length) return false;
        var acl = sd[(int)offset..];
        int aclSize = BinaryPrimitives.ReadUInt16LittleEndian(acl[2..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(acl[4..]);
        if (aclSize < 8 || aclSize > acl.Length) return false;
        acl = acl[..aclSize];
        var list = new List<AceInfo>(count);
        int pos = 8;
        for (int i = 0; i < count; i++)
        {
            if (pos + 4 > acl.Length) return false;
            byte type = acl[pos], flags = acl[pos + 1];
            int size = BinaryPrimitives.ReadUInt16LittleEndian(acl[(pos + 2)..]);
            if (size < 8 || pos + size > acl.Length) return false;
            var ace = acl.Slice(pos, size);
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(ace[4..]);
            int sidAt = 8;
            if (type is AceTypes.AccessAllowedObject or AceTypes.AccessDeniedObject or AceTypes.AccessAllowedCallbackObject or AceTypes.AccessDeniedCallbackObject)
            {
                if (ace.Length < 12) return false;
                uint objFlags = BinaryPrimitives.ReadUInt32LittleEndian(ace[8..]);
                sidAt = 12 + ((objFlags & 1) != 0 ? 16 : 0) + ((objFlags & 2) != 0 ? 16 : 0);
            }
            var sid = sidAt < ace.Length ? SidText.FromBinary(ace[sidAt..], out _) : null;
            if (sid is null) return false;
            list.Add(new AceInfo(type, flags, mask, sid));
            pos += size;
        }
        aces = list;
        return true;
    }
}

public enum AclReadStatus { Ok, AccessDenied, Failed }

public sealed record AclReadResult(AclReadStatus Status, int Win32Error, SecurityDescriptorInfo? Descriptor);

/// <summary>Source of security descriptors for open handles. The default reads the real descriptor; tests substitute fakes (U-I01 to U-I09).</summary>
public interface IAclReader
{
    AclReadResult Read(SafeFileHandle handle, string path);
}

/// <summary>Reads the owner, group and DACL of an open file or directory (needs READ_CONTROL on the handle).</summary>
public sealed class WindowsAclReader : IAclReader
{
    private const uint OwnerGroupDacl = 1 | 2 | 4;

    public AclReadResult Read(SafeFileHandle handle, string path)
    {
        if (!OperatingSystem.IsWindows()) return new AclReadResult(AclReadStatus.Failed, 0, null);
        unsafe
        {
            byte[] buffer = new byte[1024];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint needed;
                bool ok;
                fixed (byte* p = buffer) ok = Native.GetKernelObjectSecurity(handle, OwnerGroupDacl, p, (uint)buffer.Length, out needed);
                if (ok)
                    return SecurityDescriptorParser.TryParse(buffer, out var info)
                        ? new AclReadResult(AclReadStatus.Ok, 0, info)
                        : new AclReadResult(AclReadStatus.Failed, 0, null);
                int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (err == 122 && needed > buffer.Length && needed <= 65536) { buffer = new byte[needed]; continue; }
                return new AclReadResult(err == 5 ? AclReadStatus.AccessDenied : AclReadStatus.Failed, err, null);
            }
            return new AclReadResult(AclReadStatus.Failed, 122, null);
        }
    }
}

/// <summary>Which rights count as "can write" for a component.</summary>
public enum WriteRightsTier
{
    /// <summary>The literal rule: write, append, delete, delete-child, create-file, create-subdirectory, WRITE_DAC, WRITE_OWNER (and generic write or all).</summary>
    Full,
    /// <summary>Components above the install root: only rights that let a principal replace or re-permission the child (delete, delete-child, WRITE_DAC, WRITE_OWNER, generic all).</summary>
    ReplaceOnly,
}

public sealed record AclFinding(string Reason, string Principal, uint Bits);

/// <summary>
/// Effective-ACL evaluator for the install-tree rule. A principal outside SYSTEM, Administrators and TrustedInstaller that holds any
/// listed right in an allow entry is a finding. Deny entries are never credited (fail closed), inherit-only entries are skipped (they do
/// not apply to the object itself), CREATOR OWNER and CREATOR GROUP resolve to the actual owner and group, and the owner must be trusted.
/// </summary>
public static class AclEvaluator
{
    public const uint FileWriteData = 0x2, FileAppendData = 0x4, FileDeleteChild = 0x40, Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000;
    public const uint GenericWrite = 0x40000000, GenericAll = 0x10000000, MaximumAllowed = 0x02000000;

    public const uint FullMask = FileWriteData | FileAppendData | FileDeleteChild | Delete | WriteDac | WriteOwner | GenericWrite | GenericAll | MaximumAllowed;
    public const uint ReplaceMask = FileDeleteChild | Delete | WriteDac | WriteOwner | GenericAll | MaximumAllowed;

    public static uint MaskFor(WriteRightsTier tier) => tier == WriteRightsTier.Full ? FullMask : ReplaceMask;

    /// <summary>Write-class rights of a registry key: set value, create subkey, create link, delete, change permissions or owner, and the generic write rights.</summary>
    public const uint RegistryWriteMask = 0x2 | 0x4 | 0x20 | Delete | WriteDac | WriteOwner | GenericWrite | GenericAll | MaximumAllowed;

    public static IReadOnlyList<AclFinding> Evaluate(SecurityDescriptorInfo descriptor, WriteRightsTier tier) => Evaluate(descriptor, MaskFor(tier));

    /// <summary>Same rule with an explicit mask, for objects other than files (the anti-rollback key uses <see cref="RegistryWriteMask"/>).</summary>
    public static IReadOnlyList<AclFinding> Evaluate(SecurityDescriptorInfo descriptor, uint mask)
    {
        var findings = new List<AclFinding>();
        if (!SidText.IsTrustedInstallPrincipal(descriptor.OwnerSid)) findings.Add(new AclFinding("owner", descriptor.OwnerSid ?? "(none)", 0));
        if (descriptor.Dacl is null) { findings.Add(new AclFinding("null_dacl", SidText.CreatorOwner, uint.MaxValue)); return findings; }
        foreach (var ace in descriptor.Dacl)
        {
            if (!AceTypes.IsAllow(ace.Type) || (ace.Flags & AceTypes.InheritOnly) != 0) continue;
            string sid = ace.Sid;
            if (sid == SidText.CreatorOwner) sid = descriptor.OwnerSid ?? sid;
            else if (sid == SidText.CreatorGroup) sid = descriptor.GroupSid ?? sid;
            if (SidText.IsTrustedInstallPrincipal(sid)) continue;
            uint bits = ace.Mask & mask;
            if (bits != 0) findings.Add(new AclFinding("write_right", sid, bits));
        }
        return findings;
    }
}
