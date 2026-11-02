// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Windows;

/// <summary>Shared, side-effect free rules for catalog registry targets. Used by the writer at run time and by the catalog self-check.</summary>
public static class RegistryTargetRules
{
    public const int MaxRawBytes = 64 * 1024;
    public const string SandboxPrefix = @"Software\WinoraTests\";

    public static bool IsSafeSubKey(string? subKey)
    {
        if (string.IsNullOrWhiteSpace(subKey) || subKey.StartsWith('\\') || subKey.EndsWith('\\') || subKey.Contains('/') || subKey.Contains("\\\\")) return false;
        if (subKey.Contains("HKEY_", StringComparison.OrdinalIgnoreCase) || subKey.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ||
            subKey.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) || subKey.StartsWith("HKCR", StringComparison.OrdinalIgnoreCase)) return false;
        if (subKey.Any(char.IsControl)) return false;
        return subKey.Split('\\').All(part => part.Length > 0 && part != "." && part != "..");
    }

    public static bool IsSafeValueName(string? name) => !string.IsNullOrEmpty(name) && !name.Contains('\\') && !name.Any(char.IsControl);

    /// <summary>A member target is safe when its key is safe and its value name suits its mode: a key member has no value name (its default value is used).</summary>
    public static bool IsSafeTarget(RegistryTarget? target) =>
        target is not null && IsSafeSubKey(target.SubKey) && (target.IsKey ? target.ValueName.Length == 0 : IsSafeValueName(target.ValueName));

    /// <summary>True when every member of the spec is well-formed for its operation and within the size bound. Absent is Kind None with null Raw.</summary>
    public static bool IsLegalSpec(RegistryValueSpec? spec) => spec is not null && spec.Members.All(IsLegalMember);

    private static bool IsLegalMember(RegistryValueSpec spec)
    {
        switch (spec.Op)
        {
            case RegistryValueOp.ClearBits or RegistryValueOp.SetBits:
                return spec.Raw is null && spec.Mask != 0 && spec.Kind is RegistryDataKind.DWord or RegistryDataKind.String;
            case RegistryValueOp.KeyAbsent:
                return spec.Kind == RegistryDataKind.None && spec.Raw is null;
            case RegistryValueOp.KeyPresent:
                return spec.Kind == RegistryDataKind.None ? spec.Raw is null : spec.Kind == RegistryDataKind.String && spec.Raw is { Length: 0 };
            case RegistryValueOp.Set:
                if (spec.Kind == RegistryDataKind.None) return spec.Raw is null;
                if (spec.Raw is null || spec.Raw.Length > MaxRawBytes) return false;
                return spec.Kind switch
                {
                    RegistryDataKind.DWord => spec.Raw.Length == 4,
                    RegistryDataKind.QWord => spec.Raw.Length == 8,
                    RegistryDataKind.String or RegistryDataKind.ExpandString => spec.Raw.Length % 2 == 0,
                    RegistryDataKind.MultiString => spec.Raw.Length >= 2 && spec.Raw.Length % 2 == 0,
                    RegistryDataKind.Binary => true,
                    _ => false,
                };
            default:
                return false;
        }
    }

    /// <summary>Whether a legal member spec belongs to a member target: key operations only on key members and the other operations only on value members.</summary>
    public static bool Fits(RegistryValueSpec member, RegistryTarget target) => target.Mode switch
    {
        RegistryKeyMode.KeyOnly => member.IsKeyOp && member.Kind == RegistryDataKind.None,
        RegistryKeyMode.KeyWithEmptyDefault => member.IsKeyOp && (member.Op == RegistryValueOp.KeyAbsent || member.Kind == RegistryDataKind.String),
        _ => !member.IsKeyOp,
    };
}

/// <summary>
/// The ONLY registry-mutating code in the Windows project. Reads and writes exactly the catalog's own targets under HKCU (or under a
/// Software\WinoraTests\ sandbox root in tests). It never writes HKLM, never expands environment strings, and never
/// accepts a target that is not the catalog instance of the definition. It deletes a key only for a key member of a catalog row, only when the key holds
/// nothing but what the row created, and never with its subkeys. Raw value encoding: DWORD/QWORD little-endian, strings UTF-16LE
/// without terminator, multi-string = each item followed by NUL plus a final NUL, binary as is. Values larger than 64 KiB are refused.
/// A grouped row is written member by member after one compare of all members; when a later member fails, the earlier ones are put back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistrySettingsAccess : IRegistrySettingsAccess
{
    private const int KeySetValue = 0x0002, KeyCreateSubKey = 0x0004, Wow64_64 = 0x0100, Wow64_32 = 0x0200;
    private readonly ISettingCatalog catalog;
    private readonly string root;

    /// <summary>Tests only: runs after each member is written, with the number of members written so far. A throw simulates a failing write.</summary>
    internal Action<int>? AfterMemberWrite { get; set; }
    internal Action<int>? BeforeMemberReadBack { get; set; }

    /// <param name="catalog">The trusted catalog; definitions are validated against it by id, version and target.</param>
    /// <param name="sandboxRoot">Tests only: an HKCU-relative root that must start with Software\WinoraTests\. Null means the real HKCU.</param>
    public RegistrySettingsAccess(ISettingCatalog catalog, string? sandboxRoot = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (sandboxRoot is null) root = "";
        else
        {
            var trimmed = sandboxRoot.TrimEnd('\\');
            if (!trimmed.StartsWith(RegistryTargetRules.SandboxPrefix, StringComparison.OrdinalIgnoreCase) || trimmed.Length == RegistryTargetRules.SandboxPrefix.Length ||
                !RegistryTargetRules.IsSafeSubKey(trimmed))
                throw new ArgumentException("sandbox root must be under Software\\WinoraTests\\", nameof(sandboxRoot));
            root = trimmed;
        }
    }

    private static WinoraException Denied(Exception e) => new(WinoraError.From(ErrorCode.AccessDenied), e);
    private static WinoraException NotAllowed(string detail) => new(WinoraError.From(ErrorCode.TargetNotAllowed, detail));

    private string FullPath(RegistryTarget target) => root.Length == 0 ? target.SubKey : root + "\\" + target.SubKey;
    private static RegistryView ViewOf(RegistryViewKind v) => v == RegistryViewKind.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64;

    /// <summary>Throws target.not_allowed unless the definition is the catalog's own row (id and version) with the catalog's exact targets.</summary>
    private SettingDefinition Validate(SettingDefinition definition)
    {
        if (definition is null) throw NotAllowed("null");
        var own = catalog.Find(definition.Id);
        if (own is null || own.Version != definition.Version) throw NotAllowed(DiscoverySupport.Detail(definition.Id));
        if (own.Target != definition.Target || definition.Target is null || !own.ExtraTargets.SequenceEqual(definition.ExtraTargets)) throw NotAllowed(DiscoverySupport.Detail(definition.Id));
        if (!own.Targets.All(RegistryTargetRules.IsSafeTarget)) throw NotAllowed(DiscoverySupport.Detail(own.Id));
        return own;
    }

    public RegistrySnapshot Read(SettingDefinition definition)
    {
        var own = Validate(definition);
        try { return ReadAll(own.Targets); }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException) { throw Denied(e); }
        catch (Exception e) when (e is IOException or InvalidCastException or ArgumentException)
        { throw new WinoraException(WinoraError.From(ErrorCode.DiscoveryItemUnreadable, DiscoverySupport.Detail(definition.Id)), e); }
    }

    private RegistrySnapshot ReadAll(IReadOnlyList<RegistryTarget> targets) => SettingSnapshots.Group(targets.Select(ReadCore).ToList());

    private RegistrySnapshot ReadCore(RegistryTarget target)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, ViewOf(target.View));
        using var key = hive.OpenSubKey(FullPath(target), RegistryRights.QueryValues);
        if (key is null) return new(false, false, target.View, null, null);
        if (target.Mode == RegistryKeyMode.KeyOnly) return new(true, false, target.View, null, null);
        var absent = new object();
        if (target.Mode == RegistryKeyMode.KeyWithEmptyDefault)
        {
            // A key that holds more than its default value, or a subkey, is somebody else's: report it as unrecognizable so no choice matches and nothing is changed or deleted.
            if (key.SubKeyCount > 0 || key.GetValueNames().Any(n => n.Length > 0)) return new(true, true, target.View, RegistryDataKind.Binary, [0xFF]);
            var def = key.GetValue("", absent, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (ReferenceEquals(def, absent)) return new(true, false, target.View, null, null);
            var defKind = key.GetValueKind("");
            var (defMapped, defRaw) = Encode(defKind, def!);
            return new(true, true, target.View, defMapped, defRaw);
        }
        var data = key.GetValue(target.ValueName, absent, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (ReferenceEquals(data, absent)) return new(true, false, target.View, null, null);
        var kind = key.GetValueKind(target.ValueName);
        var (mapped, raw) = Encode(kind, data!);
        if (raw.Length > RegistryTargetRules.MaxRawBytes)
            throw new WinoraException(WinoraError.From(ErrorCode.DiscoveryItemUnreadable, "oversized"));
        return new(true, true, target.View, mapped, raw);
    }

    private static (RegistryDataKind, byte[]) Encode(RegistryValueKind kind, object data) => kind switch
    {
        RegistryValueKind.DWord => (RegistryDataKind.DWord, BitConverter.GetBytes((int)data)),
        RegistryValueKind.QWord => (RegistryDataKind.QWord, BitConverter.GetBytes((long)data)),
        RegistryValueKind.String => (RegistryDataKind.String, Encoding.Unicode.GetBytes((string)data)),
        RegistryValueKind.ExpandString => (RegistryDataKind.ExpandString, Encoding.Unicode.GetBytes((string)data)),
        RegistryValueKind.MultiString => (RegistryDataKind.MultiString, Encoding.Unicode.GetBytes(string.Concat(((string[])data).Select(s => s + "\0")) + "\0")),
        RegistryValueKind.Binary => (RegistryDataKind.Binary, (byte[])((byte[])data).Clone()),
        _ => (RegistryDataKind.None, data as byte[] ?? []),
    };

    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr cls, IntPtr clsLen, IntPtr reserved, IntPtr subKeys, IntPtr maxSubKey,
        IntPtr maxClass, IntPtr values, IntPtr maxValueName, IntPtr maxValue, IntPtr security, out long lastWrite);

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, int reserved, string? cls, int options, int sam, IntPtr security,
        out SafeRegistryHandle result, out int disposition);

    public long? ReadRevision(SettingDefinition definition)
    {
        var target = Validate(definition).Target;
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, ViewOf(target.View));
            using var key = hive.OpenSubKey(FullPath(target), RegistryRights.QueryValues);
            if (key is null) return null;
            var rc = RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var lastWrite);
            if (rc == 5) throw new UnauthorizedAccessException();
            if (rc != 0) throw new IOException("registry.query_info");
            return lastWrite; // FILETIME ticks (100 ns since 1601-01-01 UTC)
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException) { throw Denied(e); }
        catch (IOException e) { throw new WinoraException(WinoraError.From(ErrorCode.DiscoveryItemUnreadable, DiscoverySupport.Detail(definition.Id)), e); }
    }

    public bool CompareAndSet(SettingDefinition definition, RegistrySnapshot expected, RegistryValueSpec desired)
    {
        var own = Validate(definition);
        ArgumentNullException.ThrowIfNull(expected);
        var targets = own.Targets; var specs = desired?.Members ?? [];
        if (desired is null || specs.Count != targets.Count || !RegistryTargetRules.IsLegalSpec(desired) || !specs.Select((s, i) => RegistryTargetRules.Fits(s, targets[i])).All(ok => ok))
            throw new WinoraException(WinoraError.From(ErrorCode.SettingChoiceInvalid, DiscoverySupport.Detail(definition.Id)));
        if (SettingSnapshots.Members(expected).Count != targets.Count) return false;
        try
        {
            // Best effort: the registry has no atomic compare-and-set; the window between this read and the write is a few microseconds.
            var live = targets.Select(ReadCore).ToList();
            if (!SettingSnapshots.SameSnapshot(SettingSnapshots.Group(live), expected)) return false;
            return WriteMembers(targets, live, specs);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException) { throw Denied(e); }
        catch (IOException e) { throw new WinoraException(WinoraError.From(ErrorCode.Unexpected, "registry_write"), e); }
    }

    // Keys that go away are removed first, last member first, so a child goes before its parent. Everything else is written in member order.
    private bool WriteMembers(IReadOnlyList<RegistryTarget> targets, IReadOnlyList<RegistrySnapshot> live, IReadOnlyList<RegistryValueSpec> specs)
    {
        var order = Enumerable.Range(0, targets.Count).Where(i => specs[i].Op == RegistryValueOp.KeyAbsent).Reverse()
            .Concat(Enumerable.Range(0, targets.Count).Where(i => specs[i].Op != RegistryValueOp.KeyAbsent)).ToList();
        var done = new List<int>();
        var written = new Dictionary<int, RegistrySnapshot>();
        try
        {
            foreach (var i in order)
            {
                if (SettingSnapshots.Matches(live[i], specs[i])) continue;
                WriteMember(targets[i], live[i], specs[i]);
                done.Add(i);
                BeforeMemberReadBack?.Invoke(done.Count);
                var observed = ReadCore(targets[i]);
                // A read-back that is not the requested state was changed by someone else: it is never taken as Winora's value, so the compensation leaves it alone.
                if (!SettingSnapshots.Matches(observed, specs[i])) throw new WinoraException(WinoraError.From(ErrorCode.VerificationFailed));
                written[i] = observed;
                AfterMemberWrite?.Invoke(done.Count);
            }
            return true;
        }
        catch
        {
            // Put the members that were already written back to what was read, newest first, but only while a member still holds what Winora wrote: a value another program changed since is left alone. Failures here are ignored; the caller reads the state again and reports what is left.
            foreach (var i in Enumerable.Reverse(done))
            {
                try
                {
                    var now = ReadCore(targets[i]);
                    if (!written.TryGetValue(i, out var mine) || !SettingSnapshots.SameSnapshot(now, mine)) continue;
                    WriteMember(targets[i], now, SettingSnapshots.ToMemberSpec(targets[i].Mode, live[i]));
                }
                catch (Exception) { }
            }
            throw;
        }
    }

    private void WriteMember(RegistryTarget target, RegistrySnapshot live, RegistryValueSpec desired)
    {
        switch (desired.Op)
        {
            case RegistryValueOp.ClearBits or RegistryValueOp.SetBits:
                if (live.Kind != desired.Kind || !RegistryBits.TryRead(live.Kind, live.RawData, out var number)) throw new WinoraException(WinoraError.From(ErrorCode.SettingStateUnrecognized));
                WriteValue(target, desired.Kind, RegistryBits.Encode(desired.Kind, RegistryBits.Apply(desired, number)));
                break;
            case RegistryValueOp.KeyPresent:
                WriteKey(target, withEmptyDefault: desired.Kind == RegistryDataKind.String);
                break;
            case RegistryValueOp.KeyAbsent:
                DeleteKey(target);
                break;
            default:
                WriteValue(target, desired.Kind, desired.Raw);
                break;
        }
    }

    private void WriteValue(RegistryTarget target, RegistryDataKind kind, byte[]? raw)
    {
        var path = FullPath(target);
        var view = ViewOf(target.View);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        if (kind == RegistryDataKind.None)
        {
            using var existing = hive.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.SetValue);
            if (existing is null) return; // nothing to remove; never create a key for an absent value
            existing.DeleteValue(target.ValueName, throwOnMissingValue: false);
            return;
        }
        using var key = OpenOrCreate(hive, path, target.View);
        switch (kind)
        {
            case RegistryDataKind.DWord: key.SetValue(target.ValueName, BitConverter.ToInt32(raw!), RegistryValueKind.DWord); break;
            case RegistryDataKind.QWord: key.SetValue(target.ValueName, BitConverter.ToInt64(raw!), RegistryValueKind.QWord); break;
            case RegistryDataKind.String: key.SetValue(target.ValueName, Encoding.Unicode.GetString(raw!), RegistryValueKind.String); break;
            case RegistryDataKind.ExpandString: key.SetValue(target.ValueName, Encoding.Unicode.GetString(raw!), RegistryValueKind.ExpandString); break;
            case RegistryDataKind.Binary: key.SetValue(target.ValueName, raw!, RegistryValueKind.Binary); break;
            case RegistryDataKind.MultiString: key.SetValue(target.ValueName, DecodeMulti(raw!), RegistryValueKind.MultiString); break;
            default: throw new WinoraException(WinoraError.From(ErrorCode.SettingChoiceInvalid));
        }
    }

    private RegistryKey OpenOrCreate(RegistryKey hive, string path, RegistryViewKind viewKind)
    {
        var key = hive.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.SetValue | RegistryRights.CreateSubKey);
        if (key is not null) return key;
        var sam = KeySetValue | KeyCreateSubKey | (viewKind == RegistryViewKind.Registry32 ? Wow64_32 : Wow64_64);
        var rc = RegCreateKeyEx(hive.Handle, path, 0, null, 0, sam, 0, out var handle, out _);
        if (rc == 5) throw new UnauthorizedAccessException();
        if (rc != 0) throw new IOException("registry.create");
        return RegistryKey.FromHandle(handle, ViewOf(viewKind));
    }

    private void WriteKey(RegistryTarget target, bool withEmptyDefault)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, ViewOf(target.View));
        using var key = OpenOrCreate(hive, FullPath(target), target.View);
        if (withEmptyDefault) key.SetValue("", "", RegistryValueKind.String);
    }

    // Deletes the key of a key member, never its subkeys: the key must hold nothing but what Winora creates (no value for KeyOnly, at most the empty default value for KeyWithEmptyDefault).
    private void DeleteKey(RegistryTarget target)
    {
        var path = FullPath(target);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, ViewOf(target.View));
        using (var key = hive.OpenSubKey(path, RegistryRights.QueryValues))
        {
            if (key is null) return;
            var names = key.GetValueNames();
            var owned = key.SubKeyCount == 0 && (target.Mode == RegistryKeyMode.KeyOnly ? names.Length == 0 : names.All(n => n.Length == 0));
            if (!owned) throw new WinoraException(WinoraError.From(ErrorCode.ExternalConflict));
        }
        var cut = path.LastIndexOf('\\');
        if (cut <= 0) throw NotAllowed("key");
        using var parent = hive.OpenSubKey(path[..cut], RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.SetValue | RegistryRights.CreateSubKey);
        parent?.DeleteSubKey(path[(cut + 1)..], throwOnMissingSubKey: false);
    }

    private static string[] DecodeMulti(byte[] raw)
    {
        var s = Encoding.Unicode.GetString(raw);
        if (!s.EndsWith('\0')) throw new WinoraException(WinoraError.From(ErrorCode.SettingChoiceInvalid));
        s = s[..^1];
        if (s.Length == 0) return [];
        if (!s.EndsWith('\0')) throw new WinoraException(WinoraError.From(ErrorCode.SettingChoiceInvalid));
        return s[..^1].Split('\0');
    }
}
