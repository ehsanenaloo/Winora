// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Winora.Application;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// The ONLY code that writes the app consent values (a source scan test enforces it). It writes exactly one string value named <c>Value</c>, set to Allow or Deny or removed, in a key that already
/// exists below <c>HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}</c> (the capability key, its NonPackaged key, or the key of one package), as a
/// compare-and-set. The capability must be in <see cref="PermissionCatalog"/>. It creates no key and never writes the device-wide values or the policy values: those hives are read only.
/// HKCU is the hive of the user of this process token. Tests give a sandbox root under <c>Software\WinoraTests\</c>; both hives are then mapped to sub keys of it and the real hives are never touched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryConsentStore : IConsentStore
{
    private const string ConsentRoot = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    private const string ValueName = "Value";
    private const int MaxApps = 500;
    internal const string NotText = "<not text>";
    private readonly string? sandbox;

    public RegistryConsentStore() { }

    /// <summary>Redirects both hives below HKCU\&lt;root&gt;\User and HKCU\&lt;root&gt;\Machine. The root must be inside the test area.</summary>
    public RegistryConsentStore(string sandboxRoot)
    {
        if (!sandboxRoot.StartsWith(RegistryTargetRules.SandboxPrefix, StringComparison.Ordinal) || sandboxRoot.Contains("..") || !RegistryTargetRules.IsSafeSubKey(sandboxRoot))
            throw new ArgumentException("The root is not a test sandbox.", nameof(sandboxRoot));
        sandbox = sandboxRoot;
    }

    private RegistryKey Hive(bool machine) =>
        RegistryKey.OpenBaseKey(sandbox is not null || !machine ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

    private string Full(bool machine, string path) => sandbox is null ? path : sandbox + "\\" + (machine ? "Machine" : "User") + "\\" + path;

    private static string? PathOf(ConsentTarget target) => target.Scope switch
    {
        ConsentScope.Global => ConsentRoot + "\\" + target.Capability,
        ConsentScope.Desktop => ConsentRoot + "\\" + target.Capability + "\\" + ConsentAppNames.DesktopKey,
        ConsentScope.App when ConsentAppNames.IsPackageKey(target.AppKey) => ConsentRoot + "\\" + target.Capability + "\\" + target.AppKey,
        _ => null,
    };

    private static bool IsValid(ConsentTarget target) => PermissionCatalog.IsKnown(target.Capability) && (target.Scope == ConsentScope.App || target.AppKey is null) && PathOf(target) is not null;

    // A missing value is absent; a value that is not text is something Winora does not understand and reports as such.
    private static string? RawOf(RegistryKey key)
    {
        if (!key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase)) return null;
        if (key.GetValueKind(ValueName) != RegistryValueKind.String) return NotText;
        return key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? NotText;
    }

    private static DateTimeOffset? TimeOf(RegistryKey key, string name)
    {
        try
        {
            if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) || key.GetValueKind(name) != RegistryValueKind.QWord) return null;
            var ticks = Convert.ToInt64(key.GetValue(name));
            return ticks <= 0 || ticks > DateTime.MaxValue.ToFileTimeUtc() ? null : DateTimeOffset.FromFileTime(ticks).ToUniversalTime();
        }
        catch (Exception e) when (e is ArgumentException or InvalidCastException or OverflowException or FormatException) { return null; }
    }

    // ---- read ----

    public ConsentRead Read(string capability)
    {
        if (!PermissionCatalog.IsKnown(capability)) return ConsentRead.Fail(PermissionCode.Unknown);
        try
        {
            string? globalRaw = null, desktopRaw = null, machineRaw = null;
            var exists = false;
            var hasDesktop = false;
            var apps = new List<ConsentApp>();
            using (var hive = Hive(false))
            using (var key = hive.OpenSubKey(Full(false, ConsentRoot + "\\" + capability), false))
            {
                if (key is not null)
                {
                    exists = true;
                    globalRaw = RawOf(key);
                    foreach (var name in key.GetSubKeyNames())
                    {
                        if (name.Equals(ConsentAppNames.DesktopKey, StringComparison.OrdinalIgnoreCase))
                        {
                            using var desktop = key.OpenSubKey(name, false);
                            if (desktop is null) continue;
                            hasDesktop = true;
                            desktopRaw = RawOf(desktop);
                            foreach (var program in desktop.GetSubKeyNames().Take(MaxApps))
                            {
                                using var sub = desktop.OpenSubKey(program, false);
                                if (sub is null) continue;
                                apps.Add(new(program, true, ConsentAppNames.DesktopName(program), ConsentAppNames.DesktopPath(program), RawOf(sub), TimeOf(sub, "LastUsedTimeStart"), TimeOf(sub, "LastUsedTimeStop")));
                            }
                        }
                        else if (apps.Count < MaxApps * 2)
                        {
                            using var sub = key.OpenSubKey(name, false);
                            if (sub is null) continue;
                            apps.Add(new(name, false, ConsentAppNames.PackageName(name), name, RawOf(sub), TimeOf(sub, "LastUsedTimeStart"), TimeOf(sub, "LastUsedTimeStop")));
                        }
                    }
                }
            }
            machineRaw = ReadMachine(capability);
            return new(true, PermissionCode.Ok, exists, globalRaw, hasDesktop, desktopRaw, machineRaw, ReadPolicy(capability), apps);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException) { return ConsentRead.Fail(PermissionCode.AccessDenied); }
        catch (Exception e) when (e is IOException or ArgumentException) { return ConsentRead.Fail(PermissionCode.Unreadable); }
    }

    private string? ReadMachine(string capability)
    {
        try
        {
            using var hive = Hive(true);
            using var key = hive.OpenSubKey(Full(true, ConsentRoot + "\\" + capability), false);
            return key is null ? null : RawOf(key);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException or ArgumentException) { return null; }
    }

    private int? ReadPolicy(string capability)
    {
        var name = PermissionCatalog.Find(capability)?.PolicyValue;
        if (name is null) return null;
        try
        {
            using var hive = Hive(true);
            using var key = hive.OpenSubKey(Full(true, PolicyKey), false);
            if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) || key.GetValueKind(name) != RegistryValueKind.DWord) return null;
            return key.GetValue(name) is int value ? value : null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException or ArgumentException) { return null; }
    }

    public ConsentValueRead ReadValue(ConsentTarget target)
    {
        if (!IsValid(target)) return ConsentValueRead.Fail(PermissionCode.Unknown);
        try
        {
            using var hive = Hive(false);
            using var key = hive.OpenSubKey(Full(false, PathOf(target)!), false);
            return key is null ? ConsentValueRead.Fail(PermissionCode.NotFound) : ConsentValueRead.Of(RawOf(key));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException) { return ConsentValueRead.Fail(PermissionCode.AccessDenied); }
        catch (Exception e) when (e is IOException or ArgumentException) { return ConsentValueRead.Fail(PermissionCode.Unreadable); }
    }

    // ---- write ----

    public PermissionResult Write(ConsentTarget target, string? expected, string? value)
    {
        if (!IsValid(target) || !ConsentValues.IsWritable(expected) || !ConsentValues.IsWritable(value)) return PermissionResult.Fail(PermissionCode.Failed);
        try
        {
            using var hive = Hive(false);
            var path = Full(false, PathOf(target)!);
            using var key = hive.OpenSubKey(path, true);
            if (key is null) return PermissionResult.Fail(PermissionCode.NotFound);
            var current = RawOf(key);
            if (current == NotText) return PermissionResult.Fail(PermissionCode.Unusual);
            if (current != expected) return PermissionResult.Fail(PermissionCode.Stale);
            if (value is null) key.DeleteValue(ValueName, false);
            else key.SetValue(ValueName, value, RegistryValueKind.String);
            return PermissionResult.Success;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException) { return PermissionResult.Fail(PermissionCode.AccessDenied); }
        catch (Exception e) when (e is IOException or ArgumentException) { return PermissionResult.Fail(PermissionCode.Failed); }
    }
}
