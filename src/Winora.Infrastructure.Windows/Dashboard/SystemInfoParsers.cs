// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Numerics;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Dashboard;

/// <summary>Pure parsers for the values the dashboard reads. They take bytes or strings and touch no system state, so they are tested with fixed input.</summary>
public static class SystemInfoParsers
{
    /// <summary>What a storage device descriptor says about a disk. The serial number is shown on the page only; it is never copied into a summary or a log.</summary>
    public sealed record DiskIdentity(string? Model, string? Bus, string? Serial, string? Brand, string? MediaType = null);

    private static readonly string[] Brands =
    [
        "Samsung", "Kingston", "Toshiba", "Kioxia", "Western Digital", "WDC", "SanDisk", "Seagate", "Crucial", "Micron", "Intel", "SK hynix", "Hynix", "Hitachi", "HGST", "Lexar", "ADATA", "Corsair",
        "PNY", "Transcend", "Gigabyte", "Sabrent", "Patriot", "Netac", "Apacer", "Lenovo", "Dell", "HP", "Innodisk", "Phison", "Silicon Power", "Team", "Fanxiang", "Verbatim", "VMware", "Msft", "Microsoft", "QEMU",
    ];

    /// <summary>The maker named in the vendor or the model text, written the usual way (WDC becomes Western Digital), or null when none is recognized.</summary>
    public static string? BrandOf(string? vendor, string? model)
    {
        var text = ((vendor ?? "") + " " + (model ?? "")).Trim();
        if (text.Length == 0) return null;
        foreach (var brand in Brands)
            if (text.Contains(brand, StringComparison.OrdinalIgnoreCase)) return brand switch { "WDC" => "Western Digital", "Msft" => "Microsoft", "Hynix" => "SK hynix", _ => brand };
        return Clean(vendor);
    }

    /// <summary>Model, bus, serial number and brand from a storage device descriptor (offsets in the descriptor point to zero-terminated text).</summary>
    public static DiskIdentity DiskIdentityOf(byte[] descriptor, int length)
    {
        if (length < 36) return new(null, null, null, null);
        string? At(int offsetField)
        {
            var offset = BitConverter.ToInt32(descriptor, offsetField);
            if (offset <= 0 || offset >= length) return null;
            var end = Array.IndexOf(descriptor, (byte)0, offset, length - offset);
            if (end < 0) end = length;
            return Clean(System.Text.Encoding.ASCII.GetString(descriptor, offset, end - offset));
        }
        var vendor = At(12);
        var product = At(16);
        var model = Clean(string.Join(' ', new[] { product, vendor }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)));
        return new(model, BusName(BitConverter.ToInt32(descriptor, 28)), At(24), BrandOf(vendor, product));
    }

    public static string? BusName(int bus) => bus switch
    {
        7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 17 => "NVMe", 3 => "ATA", 1 => "SCSI", _ => null,
    };

    private const uint HvciEnabled = 0x400, HvciIumEnabled = 0x2000;

    /// <summary>
    /// Reads the code integrity option flags of a running system: memory integrity is the HVCI flag, and the isolated user mode flag shows that virtualization-based security runs
    /// (memory integrity needs it, so that flag counts too).
    /// </summary>
    public static IsolationInfo IsolationOf(uint options)
    {
        var memoryIntegrity = (options & HvciEnabled) != 0;
        return new(memoryIntegrity || (options & HvciIumEnabled) != 0, memoryIntegrity);
    }

    // ---- Windows name ----

    /// <summary>Windows 11 still reports "Windows 10" in the ProductName value, so the name comes from the build number (22000 and later is Windows 11).</summary>
    public static (string? Name, string? Edition) WindowsName(int? build, string? productName, string? editionId)
    {
        var name = build is null ? null : build >= 22000 ? "Windows 11" : "Windows 10";
        var product = Clean(productName);
        string? edition = null;
        if (product is not null)
        {
            var stripped = System.Text.RegularExpressions.Regex.Replace(product, @"^Windows\s+(10|11)\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (stripped.Length > 0) edition = stripped;
            name ??= product;
        }
        edition ??= Clean(editionId);
        return (name, edition);
    }

    public static string? Clean(string? value)
    {
        var text = value?.Trim().Trim('\0').Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>The install date is stored as seconds since 1970 (UTC).</summary>
    public static DateTimeOffset? FromUnixSeconds(object? value)
    {
        long seconds = value switch { int i => unchecked((uint)i), uint u => u, long l => l, _ => -1 };
        return seconds is > 0 and < 4_102_444_800 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    // ---- placeholders in firmware strings ----

    private static readonly string[] Placeholders =
    [
        "to be filled by o.e.m.", "to be filled by oem", "default string", "system manufacturer", "system product name", "system version", "not specified", "not applicable", "none", "unknown",
        "o.e.m.", "oem", "type1productconfigid", "default", "n/a", "0",
    ];

    /// <summary>A value from the firmware tables, or null when it is empty or a placeholder that board makers leave in.</summary>
    public static string? FirmwareText(string? value)
    {
        var text = Clean(value);
        return text is null || Placeholders.Contains(text, StringComparer.OrdinalIgnoreCase) ? null : text;
    }

    // ---- processors ----

    /// <summary>
    /// Counts the cores and logical processors in the buffer that GetLogicalProcessorInformationEx fills for RelationProcessorCore. Each record has the relationship (4 bytes), its size (4 bytes)
    /// and then the PROCESSOR_RELATIONSHIP: flags, efficiency class, 20 reserved bytes, the group count (2 bytes) and that many GROUP_AFFINITY entries (mask 8, group 2, reserved 6).
    /// </summary>
    public static (int Cores, int Threads)? CountProcessors(ReadOnlySpan<byte> buffer)
    {
        int cores = 0, threads = 0, offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            var relationship = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 4)..]);
            if (size < 8 || offset + size > buffer.Length) return cores == 0 ? null : (cores, threads);
            if (relationship == 0) // RelationProcessorCore
            {
                const int head = 8, groupCountAt = head + 22, masksAt = groupCountAt + 2;
                if (size < masksAt) return null;
                var groups = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + groupCountAt)..]);
                if (size < masksAt + groups * 16) return null;
                cores++;
                for (var g = 0; g < groups; g++)
                    threads += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(buffer[(offset + masksAt + g * 16)..]));
            }
            offset += size;
        }
        return cores == 0 ? null : (cores, threads);
    }

    // ---- display adapters ----

    private static readonly Dictionary<string, string> PciVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10DE"] = "NVIDIA", ["1002"] = "AMD", ["1022"] = "AMD", ["8086"] = "Intel", ["1414"] = "Microsoft", ["15AD"] = "VMware", ["1AF4"] = "Red Hat", ["1234"] = "QEMU", ["80EE"] = "VirtualBox",
        ["1AE0"] = "Google", ["5143"] = "Qualcomm", ["14C3"] = "MediaTek", ["1D17"] = "Zhaoxin", ["1ED5"] = "Moore Threads",
    };

    /// <summary>The vendor from a hardware id such as PCI\VEN_10DE&amp;DEV_2504. Null for ids that do not carry a known PCI vendor.</summary>
    public static string? VendorOf(string? matchingDeviceId)
    {
        if (matchingDeviceId is null) return null;
        var at = matchingDeviceId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || matchingDeviceId.Length < at + 8) return null;
        return PciVendors.TryGetValue(matchingDeviceId.Substring(at + 4, 4), out var vendor) ? vendor : null;
    }

    /// <summary>True for real adapters: a PCI device or a virtual machine bus device. Virtual displays and the empty first slot are left out.</summary>
    public static bool IsPhysicalAdapter(string? matchingDeviceId) =>
        matchingDeviceId is not null && (matchingDeviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || matchingDeviceId.StartsWith(@"VMBUS\", StringComparison.OrdinalIgnoreCase));

    /// <summary>The adapter memory value is a 64-bit number in a QWORD or an 8-byte or 4-byte binary value. Zero means unknown.</summary>
    public static ulong? MemorySize(object? value)
    {
        ulong? size = value switch
        {
            long l when l > 0 => (ulong)l,
            int i when i > 0 => (uint)i,
            byte[] { Length: 8 } b => BinaryPrimitives.ReadUInt64LittleEndian(b),
            byte[] { Length: 4 } b => BinaryPrimitives.ReadUInt32LittleEndian(b),
            _ => null,
        };
        return size is > 0 ? size : null;
    }

    // ---- SMBIOS ----

    /// <summary>
    /// The configured memory speed in MT/s from the raw SMBIOS data that GetSystemFirmwareTable returns (the 8-byte RawSMBIOSData header, then the structures). Only populated memory devices
    /// (type 17) count. The configured speed (offset 0x20) is preferred over the rated speed (offset 0x15). Null when no device reports a speed.
    /// </summary>
    public static int? MemorySpeed(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8) return null;
        var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]), (uint)(raw.Length - 8));
        var table = raw.Slice(8, length);
        int? best = null;
        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var type = table[offset];
            var formatted = table[offset + 1];
            if (formatted < 4 || offset + formatted > table.Length) break;
            if (type == 127) break; // end of table
            if (type == 17 && formatted >= 0x16)
            {
                var structure = table.Slice(offset, formatted);
                var size = BinaryPrimitives.ReadUInt16LittleEndian(structure[0x0C..]);
                if (size != 0)
                {
                    int speed = BinaryPrimitives.ReadUInt16LittleEndian(structure[0x15..]);
                    if (formatted >= 0x22 && BinaryPrimitives.ReadUInt16LittleEndian(structure[0x20..]) is var configured and not 0 and not 0xFFFF) speed = configured;
                    if (speed is not 0 and not 0xFFFF && (best is null || speed > best)) best = speed;
                }
            }
            // The string set after the formatted area ends with two zero bytes.
            var next = offset + formatted;
            while (next + 1 < table.Length && !(table[next] == 0 && table[next + 1] == 0)) next++;
            offset = next + 2;
        }
        return best;
    }

    // ---- other values ----

    public static FirmwareMode FirmwareModeOf(uint type) => type switch { 1 => FirmwareMode.Bios, 2 => FirmwareMode.Uefi, _ => FirmwareMode.Unknown };

    /// <summary>Secure Boot from the UEFISecureBootEnabled value. A PC that starts through the legacy BIOS does not support it; a UEFI PC without the value is unknown.</summary>
    public static SecureBootState SecureBootOf(FirmwareMode mode, object? enabled) => (mode, enabled) switch
    {
        (FirmwareMode.Bios, _) => SecureBootState.NotSupported,
        (_, int and 1) => SecureBootState.On,
        (_, int and 0) => SecureBootState.Off,
        _ => SecureBootState.Unknown,
    };

    /// <summary>The system power status: byte 128 of the battery flag means there is no battery, 255 means unknown. A percentage of 255 is unknown.</summary>
    public static BatteryInfo? BatteryOf(byte acLine, byte batteryFlag, byte percent)
    {
        if (batteryFlag == 255) return null;
        if (batteryFlag == 128) return new(false, null, acLine == 1 ? true : null);
        return new(true, percent <= 100 ? percent : null, acLine switch { 1 => true, 0 => false, _ => null });
    }

    public static string? TpmVersionOf(uint version) => version switch { 1 => "1.2", 2 => "2.0", _ => null };

    /// <summary>"1.2 GB" style sizes with the binary unit names that Windows uses for storage (KB, MB, GB, TB for 1024-based units).</summary>
    public static string Size(ulong bytes, IFormatProvider? culture = null)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, culture) + " " + units[unit];
    }
}
