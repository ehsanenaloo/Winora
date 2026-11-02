// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Winora.Application;

namespace Winora.Infrastructure.Windows;

/// <summary>DNS_SERVER_PROPERTY (netioapi.h). The union holds one pointer, here the DoH settings.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDnsServerProperty
{
    public uint Version;
    public uint ServerIndex;
    public int Type;
    public IntPtr DohSettings;
}

/// <summary>DNS_DOH_SERVER_SETTINGS (netioapi.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDnsDohServerSettings
{
    public IntPtr Template;
    public ulong Flags;
}

/// <summary>DNS_INTERFACE_SETTINGS3 (netioapi.h, Windows 10 build 19645 and later).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDnsInterfaceSettings3
{
    public uint Version;
    public ulong Flags;
    public IntPtr Domain;
    public IntPtr NameServer;
    public IntPtr SearchList;
    public uint RegistrationEnabled;
    public uint RegisterAdapterName;
    public uint EnableLlmnr;
    public uint QueryAdapterName;
    public IntPtr ProfileNameServer;
    public uint DisableUnconstrainedQueries;
    public IntPtr SupplementalSearchList;
    public uint ServerPropertyCount;
    public IntPtr ServerProperties;
    public uint ProfileServerPropertyCount;
    public IntPtr ProfileServerProperties;
}

/// <summary>
/// The only code that sets DNS servers (a source scan test enforces it): the documented iphlpapi functions GetInterfaceDnsSettings, SetInterfaceDnsSettings and FreeInterfaceDnsSettings with
/// DNS_INTERFACE_SETTINGS3, so the DNS-over-HTTPS settings of each server travel with the server list. Only the server list and its DoH settings are written: the flags never name the search
/// list, the domain, registration or any other member, and those members are zero. No command line, script host or process is used.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DnsInterfaceSettings
{
    internal const uint Version3 = 3;
    internal const uint ServerPropertyVersion1 = 1;
    internal const int ServerPropertyTypeDoh = 1;
    internal const ulong SettingIpv6 = 0x1;
    internal const ulong SettingNameServer = 0x2;
    internal const ulong SettingDoh = 0x1000;

    [DllImport("iphlpapi.dll")] private static extern uint GetInterfaceDnsSettings(Guid networkInterface, IntPtr settings);
    [DllImport("iphlpapi.dll")] private static extern void FreeInterfaceDnsSettings(IntPtr settings);
    [DllImport("iphlpapi.dll")] private static extern uint SetInterfaceDnsSettings(Guid networkInterface, IntPtr settings);

    private const uint ErrorAccessDenied = 5, ErrorNotSupported = 50, ErrorInvalidParameter = 87;

    /// <summary>The static servers of one family with their DoH settings. Null with a code when Windows cannot answer.</summary>
    public static (DnsFamily? Family, string? Code) Read(Guid adapter, bool ipv6)
    {
        var block = Marshal.AllocHGlobal(Marshal.SizeOf<NativeDnsInterfaceSettings3>());
        try
        {
            Zero(block, Marshal.SizeOf<NativeDnsInterfaceSettings3>());
            Marshal.WriteInt32(block, 0, (int)Version3);
            Marshal.WriteInt64(block, 8, (long)(ipv6 ? SettingIpv6 : 0));
            uint error;
            try { error = GetInterfaceDnsSettings(adapter, block); }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return (null, NetworkCode.Unsupported); }
            if (error != 0) return (null, error == ErrorNotSupported || error == ErrorInvalidParameter ? NetworkCode.Unsupported : NetworkCode.ReadFailed);
            try { return (Parse(Marshal.PtrToStructure<NativeDnsInterfaceSettings3>(block)), null); }
            finally { FreeInterfaceDnsSettings(block); }
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    internal static DnsFamily Parse(NativeDnsInterfaceSettings3 native)
    {
        var text = native.NameServer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(native.NameServer) ?? "";
        var addresses = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(a => IPAddress.TryParse(a, out var ip) ? ip.ToString() : a).ToList();
        var flags = new ulong[addresses.Count];
        var templates = new string?[addresses.Count];
        if (native.ServerProperties != IntPtr.Zero)
        {
            var size = Marshal.SizeOf<NativeDnsServerProperty>();
            for (var i = 0; i < Math.Min(native.ServerPropertyCount, 64u); i++)
            {
                var prop = Marshal.PtrToStructure<NativeDnsServerProperty>(native.ServerProperties + i * size);
                if (prop.Type != ServerPropertyTypeDoh || prop.DohSettings == IntPtr.Zero || prop.ServerIndex >= addresses.Count) continue;
                var doh = Marshal.PtrToStructure<NativeDnsDohServerSettings>(prop.DohSettings);
                flags[prop.ServerIndex] = doh.Flags;
                templates[prop.ServerIndex] = doh.Template == IntPtr.Zero ? null : Marshal.PtrToStringUni(doh.Template);
            }
        }
        return new(addresses.Select((a, i) => new DnsServer(a, templates[i], flags[i])).ToList());
    }

    /// <summary>Sets one family. An empty list returns the family to servers from the network.</summary>
    public static DnsWriteResult Write(Guid adapter, DnsFamily family, bool ipv6)
    {
        var owned = new List<IntPtr>();
        var block = Marshal.AllocHGlobal(Marshal.SizeOf<NativeDnsInterfaceSettings3>());
        try
        {
            Zero(block, Marshal.SizeOf<NativeDnsInterfaceSettings3>());
            var native = Build(family, ipv6, owned);
            Marshal.StructureToPtr(native, block, false);
            uint error;
            try { error = SetInterfaceDnsSettings(adapter, block); }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return DnsWriteResult.Fail(NetworkCode.Unsupported); }
            return error switch
            {
                0 => DnsWriteResult.Success,
                ErrorAccessDenied => DnsWriteResult.Fail(NetworkCode.AccessDenied),
                ErrorNotSupported => DnsWriteResult.Fail(NetworkCode.Unsupported),
                _ => DnsWriteResult.Fail(NetworkCode.Failed),
            };
        }
        finally
        {
            foreach (var p in owned) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(block);
        }
    }

    internal static NativeDnsInterfaceSettings3 Build(DnsFamily family, bool ipv6, List<IntPtr> owned)
    {
        IntPtr Own(IntPtr p) { owned.Add(p); return p; }
        var native = new NativeDnsInterfaceSettings3 { Version = Version3, Flags = SettingNameServer | (ipv6 ? SettingIpv6 : 0) };
        native.NameServer = Own(Marshal.StringToHGlobalUni(string.Join(",", family.Servers.Select(s => s.Address))));
        if (family.IsAutomatic) return native;
        // The DoH flag is always set for a static list, with zero properties when no server uses DoH, so a DoH setting left from an earlier list is removed.
        native.Flags |= SettingDoh;
        var withDoh = family.Servers.Select((s, i) => (s, i)).Where(x => x.s.Mode != DohMode.Off && x.s.DohTemplate is not null).ToList();
        if (withDoh.Count == 0) return native;
        var propSize = Marshal.SizeOf<NativeDnsServerProperty>();
        var array = Own(Marshal.AllocHGlobal(propSize * withDoh.Count));
        for (var k = 0; k < withDoh.Count; k++)
        {
            var (server, index) = withDoh[k];
            var settings = Own(Marshal.AllocHGlobal(Marshal.SizeOf<NativeDnsDohServerSettings>()));
            Marshal.StructureToPtr(new NativeDnsDohServerSettings { Template = Own(Marshal.StringToHGlobalUni(server.DohTemplate!)), Flags = server.DohFlags }, settings, false);
            Marshal.StructureToPtr(new NativeDnsServerProperty { Version = ServerPropertyVersion1, ServerIndex = (uint)index, Type = ServerPropertyTypeDoh, DohSettings = settings }, array + k * propSize, false);
        }
        native.ServerPropertyCount = (uint)withDoh.Count;
        native.ServerProperties = array;
        return native;
    }

    private static void Zero(IntPtr block, int size)
    {
        for (var i = 0; i < size; i++) Marshal.WriteByte(block, i, 0);
    }
}
