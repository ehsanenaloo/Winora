// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
namespace Winora.Infrastructure.Windows;

/// <summary>Read-only OS build number: RtlGetVersion first (not subject to manifest shimming), then the CurrentBuild registry value.</summary>
public sealed class WindowsOsBuildProvider : Winora.Application.IOsBuildProvider
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public int Size; public int Major; public int Minor; public int Build; public int PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string CsdVersion;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OsVersionInfo info);

    /// <summary>Current Windows build number, or null when it cannot be determined (non-Windows host or read failure).</summary>
    public int? CurrentBuild
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            return FromRtl() ?? FromRegistry();
        }
    }

    /// <summary>The <c>EditionID</c> value of the current Windows version key (for example <c>Core</c> or <c>Professional</c>), or null when it cannot be read.</summary>
    public string? EditionId
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
                return key?.GetValue("EditionID") as string is { Length: > 0 and <= 64 } id ? id : null;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
    }

    /// <summary>The <c>DisplayVersion</c> value (for example <c>24H2</c>); Windows versions before 20H2 only have <c>ReleaseId</c>. Null when neither is readable.</summary>
    public string? DisplayVersion
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
                if (key?.GetValue("DisplayVersion") as string is { Length: > 0 and <= 16 } display) return display;
                return key?.GetValue("ReleaseId") as string is { Length: > 0 and <= 16 } release ? release : null;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
    }

    /// <summary>The <c>UBR</c> value of the current Windows version key, or null when it cannot be read.</summary>
    public int? UpdateRevision
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
                return key?.GetValue("UBR") is int ubr and >= 0 ? ubr : null;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? FromRtl()
    {
        try
        {
            var info = new OsVersionInfo { Size = Marshal.SizeOf<OsVersionInfo>(), CsdVersion = "" };
            return RtlGetVersion(ref info) == 0 && info.Build > 0 ? info.Build : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static int? FromRegistry()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
            return int.TryParse(key?.GetValue("CurrentBuild") as string, out var build) && build > 0 ? build : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
