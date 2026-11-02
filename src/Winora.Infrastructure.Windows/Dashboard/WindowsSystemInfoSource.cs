// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Dashboard;

/// <summary>
/// The read-only probes of the dashboard (decision DEC-IW-029). Documented functions and registry values only: RtlGetVersion through the build provider, GlobalMemoryStatusEx,
/// GetLogicalProcessorInformationEx, GetSystemFirmwareTable (SMBIOS), GetFirmwareType, GetSystemPowerStatus, Tbsi_GetDeviceInfo, the drive functions of the .NET library and the registry
/// values listed in sources.md. No WMI, no child process, no write. A probe that fails returns null.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemInfoSource(IPowerPlans? powerPlans = null, IOsBuildProvider? build = null) : ISystemInfoSource
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string SecureBootKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";

    private readonly IOsBuildProvider buildProvider = build ?? new WindowsOsBuildProvider();

    private sealed record OsRegistry(string? Product, string? Edition, string? Display, int? Ubr, DateTimeOffset? Installed);
    private sealed record CpuRegistry(string? Name, string? Vendor, int? Mhz);

    // ---- registry helpers ----

    private static T? Read<T>(string path, Func<RegistryKey, T> read) where T : class
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(path, false);
            return key is null ? null : read(key);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static string? Text(RegistryKey key, string name) => SystemInfoParsers.Clean(key.GetValue(name) as string);

    // ---- Windows ----

    public OsInfo? ReadOs()
    {
        var number = buildProvider.CurrentBuild;
        var registry = Read(CurrentVersion, key => new OsRegistry(
            Text(key, "ProductName"), Text(key, "EditionID"), Text(key, "DisplayVersion") ?? Text(key, "ReleaseId"),
            key.GetValue("UBR") is int ubr && ubr >= 0 ? ubr : null, SystemInfoParsers.FromUnixSeconds(key.GetValue("InstallDate"))));
        if (registry is null && number is null) return null;
        var (name, edition) = SystemInfoParsers.WindowsName(number, registry?.Product, registry?.Edition);
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new(name, edition, registry?.Display, number, registry?.Ubr, registry?.Installed, uptime, RuntimeInformation.OSArchitecture.ToString());
    }

    // ---- processor ----

    public CpuInfo? ReadCpu()
    {
        var registry = Read(CpuKey, key => new CpuRegistry(Text(key, "ProcessorNameString"), Text(key, "VendorIdentifier"), key.GetValue("~MHz") is int mhz && mhz > 0 ? mhz : null));
        var counts = CountProcessors();
        if (registry is null && counts is null) return null;
        return new(registry?.Name, registry?.Vendor, counts?.Cores, counts?.Threads, registry?.Mhz);
    }

    private static (int Cores, int Threads)? CountProcessors()
    {
        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
            if (length == 0 || length > 1 << 20) return null;
            var memory = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, memory, ref length)) return null;
                var bytes = new byte[length];
                Marshal.Copy(memory, bytes, 0, (int)length);
                return SystemInfoParsers.CountProcessors(bytes);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    // ---- memory ----

    public MemoryInfo? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;
        return new(status.TotalPhys, status.AvailPhys, ReadMemorySpeed());
    }

    private static int? ReadMemorySpeed()
    {
        try
        {
            const uint signature = 0x52534D42; // 'RSMB'
            var size = GetSystemFirmwareTable(signature, 0, null, 0);
            if (size == 0 || size > 4 << 20) return null;
            var buffer = new byte[size];
            return GetSystemFirmwareTable(signature, 0, buffer, size) == size ? SystemInfoParsers.MemorySpeed(buffer) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    // ---- graphics ----

    public IReadOnlyList<GpuInfo>? ReadGpus()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cls = root.OpenSubKey(DisplayClass, false);
            if (cls is null) return null;
            var list = new List<GpuInfo>();
            foreach (var name in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)).Order(StringComparer.Ordinal))
            {
                using var key = cls.OpenSubKey(name, false);
                if (key is null) continue;
                var id = Text(key, "MatchingDeviceId");
                var description = Text(key, "DriverDesc");
                if (description is null || !SystemInfoParsers.IsPhysicalAdapter(id)) continue;
                var memory = SystemInfoParsers.MemorySize(key.GetValue("HardwareInformation.qwMemorySize")) ?? SystemInfoParsers.MemorySize(key.GetValue("HardwareInformation.MemorySize"));
                list.Add(new(description, SystemInfoParsers.VendorOf(id) ?? Text(key, "ProviderName"), Text(key, "DriverVersion"), memory));
            }
            return list;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    // ---- drives ----

    public IReadOnlyList<DriveEntry>? ReadDrives()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var list = new List<DriveEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                if (!drive.IsReady) continue;
                var kind = drive.DriveType == DriveType.Fixed ? DriveKind.Fixed : DriveKind.Removable;
                var letter = drive.Name.TrimEnd('\\');
                var label = SystemInfoParsers.Clean(drive.VolumeLabel);
                var identity = ReadDiskIdentity(letter);
                list.Add(new(letter, label, SystemInfoParsers.Clean(drive.DriveFormat), kind, (ulong)Math.Max(0, drive.TotalSize), (ulong)Math.Max(0, drive.TotalFreeSpace),
                    string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase), identity.Model, identity.Bus, identity.Serial, identity.Brand, identity.MediaType));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a drive that goes away while it is read is left out */ }
        }
        return list;
    }

    /// <summary>
    /// The model and the bus of the disk that holds a volume, from the storage device descriptor (a query that needs no access to the data on the volume). Both are null when the
    /// driver does not answer.
    /// </summary>
    private static SystemInfoParsers.DiskIdentity ReadDiskIdentity(string letter)
    {
        var none = new SystemInfoParsers.DiskIdentity(null, null, null, null);
        try
        {
            using var handle = CreateFileW(@"\\.\" + letter, 0, 3, 0, 3, 0, 0);
            if (handle.IsInvalid) return none;
            var buffer = new byte[1024];
            var query = new StoragePropertyQuery { PropertyId = 0, QueryType = 0 };
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, ref query, (uint)Marshal.SizeOf<StoragePropertyQuery>(), buffer, (uint)buffer.Length, out var returned, 0) || returned < 36) return none;
            var identity = SystemInfoParsers.DiskIdentityOf(buffer, (int)returned);
            // The seek penalty tells a solid state disk (none) from a spinning one (it seeks).
            var penalty = new StoragePropertyQuery { PropertyId = 7, QueryType = 0 };
            var answer = new byte[16];
            if (DeviceIoControl(handle, IoctlStorageQueryProperty, ref penalty, (uint)Marshal.SizeOf<StoragePropertyQuery>(), answer, (uint)answer.Length, out var got, 0) && got >= 9)
                identity = identity with { MediaType = answer[8] == 0 ? "SSD" : "HDD" };
            return identity;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException) { return none; }
    }

    private const uint IoctlStorageQueryProperty = 0x2D1400;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery { public int PropertyId; public int QueryType; public byte AdditionalParameters; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, ref StoragePropertyQuery input, uint inputSize, byte[] output, uint outputSize, out uint returned, nint overlapped);

    // ---- battery ----

    public BatteryInfo? ReadBattery()
    {
        try { return GetSystemPowerStatus(out var s) ? SystemInfoParsers.BatteryOf(s.AcLineStatus, s.BatteryFlag, s.BatteryLifePercent) : null; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    // ---- device and firmware ----

    public DeviceInfo? ReadDevice() => Read(BiosKey, key => new DeviceInfo(
        SystemInfoParsers.FirmwareText(Text(key, "SystemManufacturer")) ?? SystemInfoParsers.FirmwareText(Text(key, "BaseBoardManufacturer")),
        SystemInfoParsers.FirmwareText(Text(key, "SystemProductName")) ?? SystemInfoParsers.FirmwareText(Text(key, "BaseBoardProduct")),
        SystemInfoParsers.FirmwareText(Text(key, "SystemFamily")),
        SystemInfoParsers.FirmwareText(Text(key, "BIOSVendor")), SystemInfoParsers.FirmwareText(Text(key, "BIOSVersion")), SystemInfoParsers.FirmwareText(Text(key, "BIOSReleaseDate"))));

    public FirmwareInfo? ReadFirmware()
    {
        FirmwareMode mode;
        try { mode = GetFirmwareType(out var type) ? SystemInfoParsers.FirmwareModeOf(type) : FirmwareMode.Unknown; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { mode = FirmwareMode.Unknown; }
        var enabled = Read(SecureBootKey, key => key.GetValue("UEFISecureBootEnabled") is int value ? (object)value : "");
        return new(mode, SystemInfoParsers.SecureBootOf(mode, enabled));
    }

    public TpmInfo? ReadTpm()
    {
        try
        {
            if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "tbs.dll"), out var library)) return null;
            try
            {
                if (!NativeLibrary.TryGetExport(library, "Tbsi_GetDeviceInfo", out var address)) return null;
                var call = Marshal.GetDelegateForFunctionPointer<TbsiGetDeviceInfo>(address);
                var info = new TpmDeviceInfo();
                var result = call((uint)Marshal.SizeOf<TpmDeviceInfo>(), ref info);
                if (result == 0) return new(true, SystemInfoParsers.TpmVersionOf(info.TpmVersion));
                return result == TbsNoTpm ? new(false, null) : null;
            }
            finally { NativeLibrary.Free(library); }
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The code integrity options of the running system (NtQuerySystemInformation, class SystemCodeIntegrityInformation). Memory integrity (HVCI) and the isolated user mode that VBS
    /// provides are flags of what is running now, which the registry settings alone do not tell.
    /// </summary>
    public IsolationInfo? ReadIsolation()
    {
        try
        {
            var info = new CodeIntegrityInfo { Length = (uint)Marshal.SizeOf<CodeIntegrityInfo>() };
            if (NtQuerySystemInformation(SystemCodeIntegrityInformation, ref info, info.Length, out _) != 0) return null;
            return SystemInfoParsers.IsolationOf(info.Options);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CodeIntegrityInfo { public uint Length; public uint Options; }

    private const int SystemCodeIntegrityInformation = 103;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, ref CodeIntegrityInfo information, uint length, out uint returned);

    public string? ReadPowerPlan() => ReadActivePowerPlan()?.Name;

    public ActivePowerPlan? ReadActivePowerPlan()
    {
        if (powerPlans is null) return null;
        return powerPlans.Read(CancellationToken.None).Plans.FirstOrDefault(p => p.IsActive) is { } p ? new(p.Id, p.Name) : null;
    }

    // ---- native declarations ----

    private const uint TbsNoTpm = 0x8028400F;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion, TpmVersion, TpmInterfaceType, TpmImpRevision;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint TbsiGetDeviceInfo(uint size, ref TpmDeviceInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint signature, uint tableId, byte[]? buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
