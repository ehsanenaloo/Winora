// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Text;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Windows.Worker;

/// <summary>A registry fault with the catalog code the worker reports (<c>access.denied</c> or <c>setting.state_unrecognized</c>).</summary>
public sealed class MachineRegistryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>The state of one registry value: the canonical snapshot (protocol.md A.3.1) and the key revision (the key's last-write FILETIME, 0 when the key is absent).</summary>
public readonly record struct MachineRegistryState(MachineSnapshot Snapshot, ulong KeyRevision);

/// <summary>
/// The only machine-registry access of the worker (trust.md 7). HKLM only: there is no hive parameter. Reads never create anything; <see cref="Write"/> sets exactly one
/// value (creating the target key when it is missing, never anything else) or deletes exactly one value. Implementations refuse a key whose final name differs from the
/// requested path (a registry symbolic link, trust.md 7.3).
/// </summary>
public interface IMachineRegistry
{
    MachineRegistryState Read(string subKey, string valueName, RegistryViewKind view);

    /// <summary>Sets <paramref name="value"/> or, for <see cref="MachineValue.Absent"/>, deletes the value; flushes the key to disk before it returns.</summary>
    void Write(string subKey, string valueName, RegistryViewKind view, MachineValue value);
}

/// <summary>The real machine registry. The worker and the administrator Desktop app (DEC-IW-023) construct it; tests use their own <see cref="IMachineRegistry"/>.</summary>
public sealed unsafe class WindowsMachineRegistry : IMachineRegistry
{
    private const string RegistryMachinePrefix = @"\REGISTRY\MACHINE\";
    private const int KeyNameInformation = 3;

    public MachineRegistryState Read(string subKey, string valueName, RegistryViewKind view)
    {
        var rc = Native.RegOpenKeyEx(Native.HkeyLocalMachine, subKey, 0, Native.KeyQueryValue | ViewMask(view), out var key);
        if (rc is Native.ErrorFileNotFound or Native.ErrorPathNotFound) return new(MachineSnapshot.Registry(false, false, view, null), 0);
        if (rc != Native.ErrorSuccess) throw Fault(rc);
        try
        {
            RequireRequestedKey(key, subKey, view);
            long lastWrite = 0;
            if (Native.RegQueryInfoKey(key, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, &lastWrite) != Native.ErrorSuccess) throw new MachineRegistryException(ErrorCode.AccessDenied);
            return new(ReadValue(key, valueName, view), (ulong)lastWrite);
        }
        finally { Native.RegCloseKey(key); }
    }

    public void Write(string subKey, string valueName, RegistryViewKind view, MachineValue value)
    {
        var sam = Native.KeyQueryValue | Native.KeySetValue | ViewMask(view);
        int rc;
        nint key;
        if (value.Kind == MachineValueKind.Absent)
        {
            // Removing a value never creates a key.
            rc = Native.RegOpenKeyEx(Native.HkeyLocalMachine, subKey, 0, sam, out key);
            if (rc is Native.ErrorFileNotFound or Native.ErrorPathNotFound) return;
        }
        else rc = Native.RegCreateKeyEx(Native.HkeyLocalMachine, subKey, 0, 0, 0, sam, 0, out key, out _);
        if (rc != Native.ErrorSuccess) throw Fault(rc);
        try
        {
            RequireRequestedKey(key, subKey, view);
            switch (value.Kind)
            {
                case MachineValueKind.Absent:
                    rc = Native.RegDeleteValue(key, valueName);
                    if (rc == Native.ErrorFileNotFound) rc = Native.ErrorSuccess;
                    break;
                case MachineValueKind.DWord:
                {
                    var v = (uint)value.Number!.Value;
                    rc = Native.RegSetValueEx(key, valueName, 0, Native.RegDword, (byte*)&v, sizeof(uint));
                    break;
                }
                case MachineValueKind.QWord:
                {
                    var v = value.Number!.Value;
                    rc = Native.RegSetValueEx(key, valueName, 0, Native.RegQword, (byte*)&v, sizeof(ulong));
                    break;
                }
                default:
                {
                    var bytes = Encoding.Unicode.GetBytes(value.Text! + "\0");
                    fixed (byte* p = bytes) rc = Native.RegSetValueEx(key, valueName, 0, Native.RegSz, p, (uint)bytes.Length);
                    break;
                }
            }
            if (rc != Native.ErrorSuccess) throw Fault(rc);
            rc = Native.RegFlushKey(key);
            if (rc != Native.ErrorSuccess) throw Fault(rc);
        }
        finally { Native.RegCloseKey(key); }
    }

    private static MachineSnapshot ReadValue(nint key, string valueName, RegistryViewKind view)
    {
        uint size = 0;
        var rc = Native.RegQueryValueEx(key, valueName, 0, out var type, null, ref size);
        if (rc == Native.ErrorFileNotFound) return MachineSnapshot.Registry(true, false, view, null);
        if (rc != Native.ErrorSuccess && rc != Native.ErrorMoreData) throw Fault(rc);
        if (size > ElevationLimits.MaxRestoreStringBytes + 2) throw new MachineRegistryException(ErrorCode.SettingStateUnrecognized);
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            var read = size;
            rc = Native.RegQueryValueEx(key, valueName, 0, out type, p, ref read);
            if (rc != Native.ErrorSuccess) throw Fault(rc);
            size = read;
        }
        try
        {
            switch (type)
            {
                case Native.RegDword when size == 4:
                    return MachineSnapshot.Registry(true, true, view, MachineValue.DWord(BitConverter.ToUInt32(buffer, 0)));
                case Native.RegQword when size == 8:
                    return MachineSnapshot.Registry(true, true, view, MachineValue.QWord(BitConverter.ToUInt64(buffer, 0)));
                case Native.RegSz when size % 2 == 0:
                {
                    var text = Encoding.Unicode.GetString(buffer, 0, (int)size);
                    var end = text.IndexOf('\0');
                    return MachineSnapshot.Registry(true, true, view, MachineValue.String(end >= 0 ? text[..end] : text));
                }
            }
        }
        catch (ArgumentException) { /* too long or not well-formed text */ }
        throw new MachineRegistryException(ErrorCode.SettingStateUnrecognized);
    }

    /// <summary>A registry symbolic link makes the opened key a different key than the one requested; refuse it (trust.md 7.3).</summary>
    private static void RequireRequestedKey(nint key, string subKey, RegistryViewKind view)
    {
        var finalName = FinalName(key);
        var wanted = (RegistryMachinePrefix + subKey.Trim('\\')).ToUpperInvariant();
        var actual = finalName.ToUpperInvariant();
        if (IsCurrentControlSetPath(subKey))
        {
            // CurrentControlSet is a symbolic link to ControlSetNNN: the key opened through it reports its final name under the numbered set. Nothing else may differ.
            if (!MatchesControlSetName(subKey.Trim('\\'), actual)) throw new MachineRegistryException(ErrorCode.AccessDenied);
            return;
        }
        if (view == RegistryViewKind.Registry64 || !subKey.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase))
        {
            if (actual != wanted) throw new MachineRegistryException(ErrorCode.AccessDenied);
            return;
        }
        // 32-bit view: the key may live under SOFTWARE\WOW6432Node; the tail after SOFTWARE must still match.
        var tail = "\\" + subKey.Trim('\\')[@"SOFTWARE\".Length..].ToUpperInvariant();
        if (!actual.StartsWith(RegistryMachinePrefix + @"SOFTWARE\", StringComparison.Ordinal) || !actual.EndsWith(tail, StringComparison.Ordinal))
            throw new MachineRegistryException(ErrorCode.AccessDenied);
    }

    private const string CurrentControlSetPrefix = @"SYSTEM\CurrentControlSet\";

    private static bool IsCurrentControlSetPath(string subKey) => subKey.Trim('\\').StartsWith(CurrentControlSetPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="actualUpper"/> is <c>\REGISTRY\MACHINE\SYSTEM\ControlSet</c>, three digits and then exactly the rest of the requested path.</summary>
    internal static bool MatchesControlSetName(string subKey, string actualUpper)
    {
        var head = RegistryMachinePrefix.ToUpperInvariant() + @"SYSTEM\CONTROLSET";
        if (!actualUpper.StartsWith(head, StringComparison.Ordinal) || actualUpper.Length < head.Length + 4) return false;
        var digits = actualUpper.AsSpan(head.Length, 3);
        foreach (var c in digits) if (c is < '0' or > '9') return false;
        return actualUpper.AsSpan(head.Length + 3).SequenceEqual(("\\" + subKey[CurrentControlSetPrefix.Length..]).ToUpperInvariant());
    }

    private static string FinalName(nint key)
    {
        var buffer = new byte[2048];
        fixed (byte* p = buffer)
        {
            var status = Native.NtQueryKey(key, KeyNameInformation, p, (uint)buffer.Length, out _);
            if (status != 0) throw new MachineRegistryException(ErrorCode.AccessDenied);
            var length = BitConverter.ToInt32(buffer, 0);
            if (length < 0 || length > buffer.Length - 4) throw new MachineRegistryException(ErrorCode.AccessDenied);
            return Encoding.Unicode.GetString(buffer, 4, length);
        }
    }

    private static uint ViewMask(RegistryViewKind view) => view == RegistryViewKind.Registry32 ? Native.KeyWow64_32 : Native.KeyWow64_64;

    private static MachineRegistryException Fault(int code) => new(ErrorCode.AccessDenied);
}
