// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Winora.Application;

namespace Winora.Infrastructure.Windows.RestorePoint;

/// <summary>
/// The only file that calls SRSetRestorePointW. The library is loaded from the system folder at run time, as the documentation asks, and the function is called with
/// the documented structures: BEGIN_SYSTEM_CHANGE then END_SYSTEM_CHANGE with the type MODIFY_SETTINGS. Reads are two registry values. Nothing here turns System Protection on or changes the
/// creation limit, and no program is started.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRestorePointPort : IRestorePointPort
{
    private const uint BeginSystemChange = 100, EndSystemChange = 101, ModifySettings = 12;
    private const int MaxDescriptionW = 256;
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";

    // The header declares both structures with 1-byte packing.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    internal struct RestorePointInfoW
    {
        public uint EventType;
        public uint RestorePointType;
        public long SequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxDescriptionW)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct StateMgrStatus
    {
        public uint Status;
        public long SequenceNumber;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetRestorePointFn(ref RestorePointInfoW info, out StateMgrStatus status);

    public RestoreProtection ReadProtection() =>
        ReadDword(RegistryHive.LocalMachine, PolicyKey, "DisableSR") is 1 ? RestoreProtection.Disabled : RestoreProtection.Unknown;

    public int? ReadCreationFrequencyMinutes() =>
        ReadDword(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency") is { } v && v <= int.MaxValue ? (int)v : null;

    private static uint? ReadDword(RegistryHive hive, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path, false);
            return key?.GetValue(name) is int value ? unchecked((uint)value) : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public RestorePointNativeResult Create(string description)
    {
        var call = Load();
        if (call is null) return new(false, 0, 0);
        var text = description.Length >= MaxDescriptionW ? description[..(MaxDescriptionW - 1)] : description;
        var begin = new RestorePointInfoW { EventType = BeginSystemChange, RestorePointType = ModifySettings, SequenceNumber = 0, Description = text };
        if (!call(ref begin, out var started)) return new(true, started.Status != 0 ? started.Status : uint.MaxValue, 0);
        if (started.Status != 0) return new(true, started.Status, 0);
        // The snapshot is taken at the start of the change; a failed END does not remove it.
        var end = new RestorePointInfoW { EventType = EndSystemChange, RestorePointType = ModifySettings, SequenceNumber = started.SequenceNumber, Description = text };
        call(ref end, out _);
        return new(true, 0, started.SequenceNumber);
    }

    private static SetRestorePointFn? Load()
    {
        try
        {
            if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "srclient.dll"), out var library)) return null;
            return NativeLibrary.TryGetExport(library, "SRSetRestorePointW", out var address) ? Marshal.GetDelegateForFunctionPointer<SetRestorePointFn>(address) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or ArgumentException) { return null; }
    }
}
