// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Display;

/// <summary>
/// Lists the display devices that are present, with the facts <see cref="DisplayAdapterResolver"/> needs (class, bus, hardware ids, driver service, driver key, problem code).
/// It uses only read calls of the device installation functions (SetupDiGetClassDevs for the display class with the present flag, SetupDiGetDeviceRegistryProperty,
/// SetupDiGetDeviceInstanceId and CM_Get_DevNode_Status). It never walks the Class registry key to find adapters, never opens a driver key and never changes a device.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DisplayDeviceEnumerator : IDisplayDeviceEnumerator
{
    private const uint DigcfPresent = 0x2;
    private const uint SpdrpDeviceDesc = 0x0, SpdrpHardwareId = 0x1, SpdrpService = 0x4, SpdrpClassGuid = 0x8, SpdrpDriver = 0x9, SpdrpFriendlyName = 0xC,
        SpdrpLocationInformation = 0xD, SpdrpEnumeratorName = 0x16;
    private const int ErrorNoMoreItems = 259;
    private const int MaxDevices = 64, MaxPropertyBytes = 8192;
    private static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint index, ref DevInfoData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint deviceInfoSet, ref DevInfoData data, uint property, out uint propertyType, byte[]? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    private static extern bool SetupDiGetDeviceInstanceId(nint deviceInfoSet, ref DevInfoData data, StringBuilder? id, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    public IReadOnlyList<DisplayDevice> Enumerate()
    {
        var displayClass = new Guid(DisplayDriverKeys.ClassGuid);
        var set = SetupDiGetClassDevs(ref displayClass, null, 0, DigcfPresent);
        if (set == InvalidHandle || set == 0) throw new DisplayEnumerationException("the display devices could not be listed");
        try
        {
            var devices = new List<DisplayDevice>();
            for (uint index = 0; index < MaxDevices; index++)
            {
                var data = new DevInfoData { Size = (uint)Marshal.SizeOf<DevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) break;
                    throw new DisplayEnumerationException("the display devices could not be listed");
                }
                var instance = InstanceId(set, ref data);
                if (instance is null) continue;
                var name = Text(set, ref data, SpdrpFriendlyName) ?? Text(set, ref data, SpdrpDeviceDesc) ?? instance;
                var hardwareIds = MultiText(set, ref data, SpdrpHardwareId);
                var status = CM_Get_DevNode_Status(out _, out var problem, data.DevInst, 0);
                devices.Add(new DisplayDevice(instance, name, Text(set, ref data, SpdrpLocationInformation) ?? "", Text(set, ref data, SpdrpClassGuid) ?? "",
                    Text(set, ref data, SpdrpEnumeratorName) ?? "", hardwareIds, Text(set, ref data, SpdrpService), Text(set, ref data, SpdrpDriver),
                    status == 0 ? (int)problem : -1));
            }
            return devices;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static string? InstanceId(nint set, ref DevInfoData data)
    {
        var builder = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref data, builder, (uint)builder.Capacity, out _) ? builder.ToString() : null;
    }

    private static byte[]? Property(nint set, ref DevInfoData data, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, null, 0, out var required);
        if (required == 0 || required > MaxPropertyBytes) return null;
        var buffer = new byte[required];
        return SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, required, out _) ? buffer : null;
    }

    private static string? Text(nint set, ref DevInfoData data, uint property)
    {
        var bytes = Property(set, ref data, property);
        if (bytes is null) return null;
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private static List<string> MultiText(nint set, ref DevInfoData data, uint property)
    {
        var bytes = Property(set, ref data, property);
        return bytes is null ? [] : Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
