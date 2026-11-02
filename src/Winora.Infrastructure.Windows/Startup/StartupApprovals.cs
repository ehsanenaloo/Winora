// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// The ONLY code that writes the Explorer StartupApproved values (a source scan test enforces it). It reads and writes exactly one value in
/// <c>Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run|Run32|StartupFolder}</c> of the hive that belongs to the entry's scope, as a compare-and-set, and it can remove that
/// one value again. It never opens, writes or removes any other registry value or any file. HKCU is the hive of the user of this process token. Tests give a sandbox root under
/// <c>Software\WinoraTests\</c>; both hives are then mapped to sub keys of it and the real hives are never touched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryStartupApprovals : IStartupApprovals
{
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const int MaxNameLength = 256;
    private const int MaxValueBytes = 256;
    private readonly string? sandbox;

    public RegistryStartupApprovals() { }

    /// <summary>Redirects both hives below HKCU\&lt;root&gt;\User and HKCU\&lt;root&gt;\Machine. The root must be inside the test area.</summary>
    public RegistryStartupApprovals(string sandboxRoot)
    {
        if (!sandboxRoot.StartsWith(RegistryTargetRules.SandboxPrefix, StringComparison.Ordinal) || sandboxRoot.Contains("..") || !RegistryTargetRules.IsSafeSubKey(sandboxRoot))
            throw new ArgumentException("The root is not a test sandbox.", nameof(sandboxRoot));
        sandbox = sandboxRoot;
    }

    private static string Leaf(StartupApprovalStore store) => store switch
    {
        StartupApprovalStore.Run => "Run",
        StartupApprovalStore.Run32 => "Run32",
        _ => "StartupFolder",
    };

    private RegistryKey Hive(InstallationScope scope) =>
        RegistryKey.OpenBaseKey(sandbox is not null || scope == InstallationScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

    private string KeyPath(InstallationScope scope, StartupLocation location)
    {
        var path = ApprovedRoot + "\\" + Leaf(location.Store);
        return sandbox is null ? path : sandbox + "\\" + (scope == InstallationScope.User ? "User" : "Machine") + "\\" + path;
    }

    private static bool IsValidName(string? name) => !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && !name.Any(char.IsControl);

    public StartupApprovalRead Read(InstallationScope scope, StartupLocation location)
    {
        if (!IsValidName(location.ValueName)) return StartupApprovalRead.Fail(StartupCode.Unreadable);
        try
        {
            using var hive = Hive(scope);
            using var key = hive.OpenSubKey(KeyPath(scope, location), false);
            return Current(key, location.ValueName);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException) { return StartupApprovalRead.Fail(StartupCode.AccessDenied); }
        catch (Exception e) when (e is IOException or ArgumentException) { return StartupApprovalRead.Fail(StartupCode.Unreadable); }
    }

    // A missing key or value is "absent"; a value that is not binary is not something Winora understands.
    private static StartupApprovalRead Current(RegistryKey? key, string name)
    {
        if (key is null) return StartupApprovalRead.Of(null);
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return StartupApprovalRead.Of(null);
        if (key.GetValueKind(name) != RegistryValueKind.Binary || key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not byte[] bytes || bytes.Length > MaxValueBytes)
            return StartupApprovalRead.Fail(StartupCode.Unreadable);
        return StartupApprovalRead.Of(bytes);
    }

    public StartupResult Write(InstallationScope scope, StartupLocation location, byte[]? expected, byte[]? value)
    {
        if (!IsValidName(location.ValueName) || (value is not null && value.Length is 0 or > MaxValueBytes)) return StartupResult.Fail(StartupCode.Failed);
        try
        {
            using var hive = Hive(scope);
            var path = KeyPath(scope, location);
            using var key = value is null ? hive.OpenSubKey(path, true) : hive.CreateSubKey(path, true);
            var current = Current(key, location.ValueName);
            if (!current.Ok) return StartupResult.Fail(current.Code);
            var same = current.Bytes is null || expected is null ? current.Bytes is null && expected is null : current.Bytes.AsSpan().SequenceEqual(expected);
            if (!same) return StartupResult.Fail(StartupCode.Stale);
            if (value is null) key?.DeleteValue(location.ValueName, false);
            else key!.SetValue(location.ValueName, value, RegistryValueKind.Binary);
            return StartupResult.Success;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException) { return StartupResult.Fail(StartupCode.AccessDenied); }
        catch (Exception e) when (e is IOException or ArgumentException) { return StartupResult.Fail(StartupCode.Failed); }
    }
}
