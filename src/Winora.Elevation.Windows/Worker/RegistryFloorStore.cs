// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Winora.Elevation.Protocol.Definitions;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// The anti-rollback floor in the machine registry (technical-docs/trust.md 6.4): ONE <c>REG_BINARY</c> value <c>Floor</c> (41 bytes) in <c>HKLM\SOFTWARE\Winora\Trust</c>, 64-bit view.
/// The worker creates the key on first use with an explicit protected DACL (SYSTEM and Administrators full control, Users read). Before a value is trusted, the key's effective
/// permissions are judged: the owner and every principal with a write-class right must be SYSTEM, Administrators or TrustedInstaller; otherwise the store is <c>Insecure</c>.
/// A malformed or wrongly typed value is <c>Malformed</c>, never a floor of zero. The write is one <c>RegSetValueEx</c> call, so a torn pair cannot exist, followed by a flush.
/// Known limit (trust.md 6.4): an administrator can reset the floor. A registry symbolic link is refused by comparing the opened key's final name.
/// </summary>
public sealed unsafe class RegistryFloorStore : IAntiRollbackStore
{
    public const string TrustSubKey = @"SOFTWARE\Winora\Trust";
    public const string ValueName = "Floor";
    /// <summary>SYSTEM and Administrators: full control. Users: read. Protected from inheritance.</summary>
    public const string ProtectedSddl = "D:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)";
    private const uint ReadControl = 0x20000, OwnerGroupDacl = 0x7;
    private const string RegistryMachinePrefix = @"\REGISTRY\MACHINE\";

    private readonly string subKey;

    public RegistryFloorStore() : this(TrustSubKey) { }

    /// <summary>The key is a parameter so a test can read a key that does not exist; production uses the default constructor only.</summary>
    internal RegistryFloorStore(string subKey) => this.subKey = subKey;

    public FloorRead Read()
    {
        var rc = Native.RegOpenKeyEx(Native.HkeyLocalMachine, subKey, 0, Native.KeyQueryValue | Native.KeyWow64_64 | ReadControl, out var key);
        if (rc is Native.ErrorFileNotFound or Native.ErrorPathNotFound) return FloorRead.Absent;
        if (rc != Native.ErrorSuccess) return FloorRead.Insecure;
        try { return ReadOpen(key); }
        finally { Native.RegCloseKey(key); }
    }

    private FloorRead ReadOpen(nint key)
    {
        if (!IsRequestedKey(key) || !KeyPermissionsAreSafe(key)) return FloorRead.Insecure;
        uint size = 0;
        var rc = Native.RegQueryValueEx(key, ValueName, 0, out var type, null, ref size);
        if (rc == Native.ErrorFileNotFound) return FloorRead.Absent;
        if (rc != Native.ErrorSuccess && rc != Native.ErrorMoreData) return FloorRead.Insecure;
        if (type != Native.RegBinary || size != FloorRecord.EncodedLength) return FloorRead.Malformed;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            var read = size;
            if (Native.RegQueryValueEx(key, ValueName, 0, out type, p, ref read) != Native.ErrorSuccess || read != size) return FloorRead.Insecure;
        }
        return FloorRecord.TryDecode(buffer, out var record) ? FloorRead.Of(record) : FloorRead.Malformed;
    }

    public bool TryAdvance(FloorRecord expected, FloorRecord next)
    {
        if (!EnsureKey()) return false;
        var rc = Native.RegOpenKeyEx(Native.HkeyLocalMachine, subKey, 0, Native.KeyQueryValue | Native.KeySetValue | Native.KeyWow64_64 | ReadControl, out var key);
        if (rc != Native.ErrorSuccess) return false;
        try
        {
            var current = ReadOpen(key);
            var now = current.Status switch { FloorReadStatus.Absent => FloorRecord.None, FloorReadStatus.Present => current.Record, _ => (FloorRecord?)null };
            if (now is null || !now.Value.Equals(expected)) return false;
            var bytes = next.Encode();
            fixed (byte* p = bytes)
                if (Native.RegSetValueEx(key, ValueName, 0, Native.RegBinary, p, (uint)bytes.Length) != Native.ErrorSuccess) return false;
            return Native.RegFlushKey(key) == Native.ErrorSuccess;
        }
        finally { Native.RegCloseKey(key); }
    }

    /// <summary>Creates the key with the protected permissions when it does not exist. An existing key is never re-permissioned here: its permissions are judged on read.</summary>
    private bool EnsureKey()
    {
        var rc = Native.RegOpenKeyEx(Native.HkeyLocalMachine, subKey, 0, Native.KeyQueryValue | Native.KeyWow64_64, out var existing);
        if (rc == Native.ErrorSuccess) { Native.RegCloseKey(existing); return true; }
        if (rc is not (Native.ErrorFileNotFound or Native.ErrorPathNotFound)) return false;
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(ProtectedSddl, 1, out var descriptor, out _)) return false;
        try
        {
            var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), Descriptor = descriptor, InheritHandle = 0 };
            rc = Native.RegCreateKeyEx(Native.HkeyLocalMachine, subKey, 0, 0, 0, Native.KeyQueryValue | Native.KeySetValue | Native.KeyWow64_64, (nint)(&attributes), out var created, out _);
            if (rc != Native.ErrorSuccess) return false;
            Native.RegCloseKey(created);
            return true;
        }
        finally { Native.LocalFree(descriptor); }
    }

    private static bool KeyPermissionsAreSafe(nint key)
    {
        uint length = 0;
        var rc = Native.RegGetKeySecurity(key, OwnerGroupDacl, null, ref length);
        if (rc != 122 || length == 0 || length > 64 * 1024) return false;            // 122 = ERROR_INSUFFICIENT_BUFFER
        var buffer = new byte[length];
        fixed (byte* p = buffer)
            if (Native.RegGetKeySecurity(key, OwnerGroupDacl, p, ref length) != Native.ErrorSuccess) return false;
        return SecurityDescriptorParser.TryParse(buffer, out var info) && info is not null && AssessSecurity(info).Count == 0;
    }

    /// <summary>The findings that make the key unsafe to trust: an untrusted owner or a write-class right for any other principal.</summary>
    public static IReadOnlyList<AclFinding> AssessSecurity(SecurityDescriptorInfo descriptor) => AclEvaluator.Evaluate(descriptor, AclEvaluator.RegistryWriteMask);

    private bool IsRequestedKey(nint key)
    {
        var buffer = new byte[2048];
        fixed (byte* p = buffer)
        {
            if (Native.NtQueryKey(key, 3, p, (uint)buffer.Length, out _) != 0) return false;
            var length = BitConverter.ToInt32(buffer, 0);
            if (length < 0 || length > buffer.Length - 4) return false;
            var name = System.Text.Encoding.Unicode.GetString(buffer, 4, length);
            return string.Equals(name, RegistryMachinePrefix + subKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint Descriptor;
        public int InheritHandle;
    }
}
