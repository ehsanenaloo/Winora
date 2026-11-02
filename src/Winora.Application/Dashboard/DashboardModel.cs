// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public enum FirmwareMode { Unknown, Bios, Uefi }

public enum SecureBootState { Unknown, Off, On, NotSupported }

/// <summary>Windows as installed. Every field is null when its source could not be read.</summary>
public sealed record OsInfo(string? Name, string? Edition, string? DisplayVersion, int? Build, int? Ubr, DateTimeOffset? InstalledUtc, TimeSpan? Uptime, string? Architecture);

public sealed record CpuInfo(string? Name, string? Vendor, int? Cores, int? Threads, int? BaseMhz);

public sealed record MemoryInfo(ulong? TotalBytes, ulong? AvailableBytes, int? SpeedMts)
{
    public ulong? UsedBytes => TotalBytes is { } t && AvailableBytes is { } a && a <= t ? t - a : null;
}

public sealed record GpuInfo(string Name, string? Vendor, string? DriverVersion, ulong? DedicatedBytes);

public enum DriveKind { Fixed, Removable }

public sealed record DriveEntry(string Letter, string? Label, string? FileSystem, DriveKind Kind, ulong TotalBytes, ulong FreeBytes, bool IsSystem, string? Model = null, string? Bus = null, string? Serial = null, string? Brand = null, string? MediaType = null)
{
    public ulong UsedBytes => FreeBytes <= TotalBytes ? TotalBytes - FreeBytes : 0;
    /// <summary>Share of the drive in use, 0 to 100.</summary>
    public double UsedPercent => TotalBytes == 0 ? 0 : Math.Clamp(UsedBytes * 100.0 / TotalBytes, 0, 100);
}

public sealed record BatteryInfo(bool Present, int? Percent, bool? OnMains);

public sealed record DeviceInfo(string? Manufacturer, string? Model, string? Family, string? BiosVendor, string? BiosVersion, string? BiosDate);

public sealed record FirmwareInfo(FirmwareMode Mode, SecureBootState SecureBoot);

public sealed record TpmInfo(bool Present, string? Version);

/// <summary>Virtualization-based security and memory integrity as Windows runs them now (not as they are set up). A value is null when it cannot be told.</summary>
/// <summary>The active power plan; <see cref="Id"/> is empty when only the name is known.</summary>
public sealed record ActivePowerPlan(Guid Id, string Name);

public sealed record IsolationInfo(bool? VbsRunning, bool? MemoryIntegrityRunning);

/// <summary>
/// What the dashboard shows. A section is null when it could not be read: the page then says "Unknown" for it and the other sections are not affected.
/// </summary>
public sealed record SystemSnapshot(OsInfo? Os, CpuInfo? Cpu, MemoryInfo? Memory, IReadOnlyList<GpuInfo>? Gpus, IReadOnlyList<DriveEntry>? Drives, BatteryInfo? Battery, DeviceInfo? Device,
    FirmwareInfo? Firmware, TpmInfo? Tpm, string? PowerPlan, DateTimeOffset CollectedUtc, IsolationInfo? Isolation = null, Guid? PowerPlanId = null)
{
    public static SystemSnapshot Empty(DateTimeOffset at) => new(null, null, null, null, null, null, null, null, null, null, at);
}

/// <summary>
/// Read-only probes of the PC. Each method returns null when the information is not available on this PC and may throw; <see cref="DashboardService"/> turns both into an Unknown section.
/// No method changes anything on the machine.
/// </summary>
public interface ISystemInfoSource
{
    OsInfo? ReadOs();
    CpuInfo? ReadCpu();
    MemoryInfo? ReadMemory();
    IReadOnlyList<GpuInfo>? ReadGpus();
    IReadOnlyList<DriveEntry>? ReadDrives();
    BatteryInfo? ReadBattery();
    DeviceInfo? ReadDevice();
    FirmwareInfo? ReadFirmware();
    TpmInfo? ReadTpm();
    string? ReadPowerPlan();
    /// <summary>The active plan with its id, so a plan that Windows ships can be shown in the page language. The default knows only the name.</summary>
    ActivePowerPlan? ReadActivePowerPlan() => ReadPowerPlan() is { } name ? new(Guid.Empty, name) : null;
    IsolationInfo? ReadIsolation() => null;
}
