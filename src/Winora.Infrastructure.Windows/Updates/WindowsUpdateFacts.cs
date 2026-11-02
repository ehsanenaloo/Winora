// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Updates;

/// <summary>
/// Reads what Windows holds about updates, from the registry only and without writing: the policy values under the Windows Update policy keys, the values that
/// device management put in the policy manager store, the pause dates that the Settings page writes, the legacy last-check and last-install times, the restart-pending flags and the
/// "get the latest updates as soon as available" switch. No WMI, no COM and no service call.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateFacts : IUpdateFactsSource
{
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string PolicyAuKey = PolicyKey + @"\AU";
    public const string DeviceManagementKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\Update";
    public const string UxSettingsKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string AutoUpdateKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update";
    private const string ServicingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";
    private const int MaxValuesPerKey = 64;
    private const int MaxDisplayChars = 120;

    private readonly Func<RegistryKey?> openRoot;

    /// <summary>Reads the 64-bit view of HKEY_LOCAL_MACHINE.</summary>
    public WindowsUpdateFacts() : this(static () => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) { }

    /// <summary>The root key is asked for on every read and disposed after it. Tests pass a sandbox key with the same layout.</summary>
    public WindowsUpdateFacts(Func<RegistryKey?> openRoot) => this.openRoot = openRoot;

    public UpdateFacts Read()
    {
        try
        {
            using var root = openRoot();
            if (root is null) return UpdateFacts.Empty;
            var pending = new List<string>();
            if (Exists(root, AutoUpdateKey + @"\RebootRequired") || Exists(root, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\RebootRequired")) pending.Add("windows_update");
            if (Exists(root, ServicingKey + @"\RebootPending")) pending.Add("servicing");
            var general = ReadTime(root, UxSettingsKey, "PauseUpdatesExpiryTime");
            return new(
                Values(root, PolicyKey).Concat(Values(root, PolicyAuKey)).ToList(),
                Values(root, DeviceManagementKey).Where(v => !v.Name.EndsWith("_ProviderSet", StringComparison.Ordinal) && !v.Name.EndsWith("_WinningProvider", StringComparison.Ordinal)).ToList(),
                ReadTime(root, AutoUpdateKey + @"\Results\Detect", "LastSuccessTime"),
                ReadTime(root, AutoUpdateKey + @"\Results\Install", "LastSuccessTime"),
                pending,
                Latest(general, ReadTime(root, UxSettingsKey, "PauseQualityUpdatesEndTime")),
                Latest(general, ReadTime(root, UxSettingsKey, "PauseFeatureUpdatesEndTime")),
                ReadDword(root, UxSettingsKey, "IsContinuousInnovationOptedIn") is { } flag ? flag != 0 : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { return UpdateFacts.Empty; }
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static bool Exists(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path, false);
        return key is not null;
    }

    private static IEnumerable<UpdatePolicyValue> Values(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path, false);
        if (key is null) yield break;
        foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal).Take(MaxValuesPerKey))
        {
            if (name.Length is 0 or > 128) continue;
            yield return new(path, name, Display(key, name));
        }
    }

    internal static string Display(RegistryKey key, string name)
    {
        object? raw;
        RegistryValueKind kind;
        try
        {
            kind = key.GetValueKind(name);
            raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { return "?"; }
        return kind switch
        {
            RegistryValueKind.DWord when raw is int d => ((uint)d).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.QWord when raw is long q => ((ulong)q).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.String or RegistryValueKind.ExpandString when raw is string s => Clean(s),
            _ => "(" + kind + ")",
        };
    }

    /// <summary>Text from the registry is data: control characters are dropped and long text is cut.</summary>
    internal static string Clean(string s)
    {
        var chars = s.Where(c => !char.IsControl(c)).Take(MaxDisplayChars + 1).ToArray();
        return chars.Length > MaxDisplayChars ? new string(chars, 0, MaxDisplayChars) + "..." : new string(chars);
    }

    private static uint? ReadDword(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path, false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int d ? (uint)d : null;
    }

    private static DateTimeOffset? ReadTime(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path, false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string text ? ParseTime(text) : null;
    }

    /// <summary>Accepts the ISO 8601 form of the pause dates and the legacy <c>yyyy-MM-dd HH:mm:ss</c> form of the last-success times (UTC). Anything else is not a time.</summary>
    internal static DateTimeOffset? ParseTime(string text)
    {
        if (text.Length is < 10 or > 40) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) && value.Year is >= 2000 and <= 2200 ? value : null;
    }
}
