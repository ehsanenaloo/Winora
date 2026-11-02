// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
namespace Winora.Application;

/// <summary>A display device as the platform reports it, before any filtering. Nothing here is trusted until <see cref="DisplayAdapterResolver"/> accepted it.</summary>
/// <param name="InstanceId">The device instance id, for example <c>PCI\VEN_10DE&amp;DEV_2206&amp;SUBSYS_...\4&amp;...</c>.</param>
/// <param name="Name">The friendly name, or the device description when there is none.</param>
/// <param name="Location">The bus location text, used only to tell two adapters with the same name apart.</param>
/// <param name="ClassGuid">The setup class of the device, as the platform reports it.</param>
/// <param name="Enumerator">The enumerator name of the device (<c>PCI</c> for a real graphics card).</param>
/// <param name="HardwareIds">The hardware ids, most specific first.</param>
/// <param name="Service">The driver service of the device; the generic Microsoft display driver is <c>BasicDisplay</c>.</param>
/// <param name="DriverKey">The software (driver) key as the platform reports it, for example <c>{4d36e968-e325-11ce-bfc1-08002be10318}\0001</c>; null when no driver is installed.</param>
/// <param name="ProblemCode">The configuration manager problem code; 0 when the device works.</param>
public sealed record DisplayDevice(string InstanceId, string Name, string Location, string ClassGuid, string Enumerator, IReadOnlyList<string> HardwareIds, string? Service, string? DriverKey, int ProblemCode);

/// <summary>Lists the display devices that are present. Implemented over the device installation functions of Windows; tests use a list.</summary>
public interface IDisplayDeviceEnumerator
{
    /// <exception cref="DisplayEnumerationException">The devices could not be listed.</exception>
    IReadOnlyList<DisplayDevice> Enumerate();
}

public sealed class DisplayEnumerationException(string message) : Exception(message);

/// <summary>
/// One display adapter that may be a target: its vendor is on the reviewed list, it has a working driver and its driver key was resolved by <see cref="DisplayDriverKeys"/>.
/// <see cref="KeyPath"/> is the machine registry path of the driver key, below <c>SYSTEM\CurrentControlSet\Control\Class\{4d36e968-...}</c>.
/// </summary>
public sealed record DisplayAdapter(string InstanceId, string FriendlyName, string VendorId, string KeyPath);

/// <summary>The adapters a display driver definition can be applied to.</summary>
public interface IDisplayAdapterSource
{
    /// <summary>The present, usable adapters of one vendor (a PCI vendor id such as <c>10DE</c>), in a stable order. Empty when there is none.</summary>
    /// <exception cref="DisplayEnumerationException">The devices could not be listed.</exception>
    IReadOnlyList<DisplayAdapter> Adapters(string vendorId);
}

/// <summary>The shape of a display driver key and of the hardware ids. The driver key is never typed anywhere: only a string the platform returned for a present device can pass.</summary>
public static class DisplayDriverKeys
{
    /// <summary>The setup class of display adapters.</summary>
    public const string ClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    /// <summary>The registry path (below HKLM) that holds the numbered driver keys of display adapters.</summary>
    public const string ClassKey = @"SYSTEM\CurrentControlSet\Control\Class\" + ClassGuid;
    /// <summary>The vendors a display driver definition may name: NVIDIA, AMD and Intel.</summary>
    public static readonly IReadOnlyList<string> Vendors = ["10DE", "1002", "8086"];

    /// <summary>
    /// The registry path of a driver key string of the form <c>{class guid}\NNNN</c> (four decimal digits). Anything else is refused: another class, another depth, a path with
    /// dots or extra segments, an empty string.
    /// </summary>
    public static bool TryResolve(string? driverKey, out string keyPath)
    {
        keyPath = "";
        if (driverKey is null || driverKey.Length != ClassGuid.Length + 5) return false;
        if (!driverKey.StartsWith(ClassGuid, StringComparison.OrdinalIgnoreCase) || driverKey[ClassGuid.Length] != '\\') return false;
        var number = driverKey.AsSpan(ClassGuid.Length + 1);
        foreach (var c in number) if (c is < '0' or > '9') return false;
        keyPath = ClassKey + "\\" + number.ToString();
        return true;
    }

    /// <summary>True for exactly <see cref="ClassKey"/>, a backslash and four decimal digits (case-insensitive on the fixed part). The last check before a registry key of an adapter is opened.</summary>
    public static bool IsResolvedKeyPath(string? keyPath)
    {
        if (keyPath is null || keyPath.Length != ClassKey.Length + 5) return false;
        if (!keyPath.StartsWith(ClassKey, StringComparison.OrdinalIgnoreCase) || keyPath[ClassKey.Length] != '\\') return false;
        foreach (var c in keyPath.AsSpan(ClassKey.Length + 1)) if (c is < '0' or > '9') return false;
        return true;
    }

    /// <summary>The PCI vendor id (upper case hex) of the first hardware id of the form <c>PCI\VEN_xxxx&amp;DEV_xxxx...</c>; false when there is none.</summary>
    public static bool TryVendor(IReadOnlyList<string> hardwareIds, out string vendor)
    {
        vendor = "";
        foreach (var id in hardwareIds)
        {
            if (id.Length < 21 || !id.StartsWith(@"PCI\VEN_", StringComparison.OrdinalIgnoreCase)) continue;
            var ven = id.AsSpan(8, 4);
            if (id[12] != '&' || !id.AsSpan(13, 4).Equals("DEV_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsHex(ven)) continue;
            vendor = ven.ToString().ToUpperInvariant();
            return true;
        }
        return false;
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text) if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}

/// <summary>
/// Turns the display devices that are present into the adapters a vendor tweak may target. A device is accepted only when it is in the display class, sits on the PCI bus, works,
/// does not run the generic Microsoft display driver, names a reviewed vendor in its first PCI hardware id, and its driver key string has the exact shape of a numbered key below the display
/// class key. Virtual adapters (Microsoft Basic Display, Hyper-V, VMware and others) fail the vendor or the bus test. A laptop with an integrated and a discrete GPU gives two adapters.
/// Two devices that report the same driver key are both refused, because the key would be ambiguous.
/// </summary>
public sealed class DisplayAdapterResolver(IDisplayDeviceEnumerator devices) : IDisplayAdapterSource
{
    public IReadOnlyList<DisplayAdapter> Adapters(string vendorId)
    {
        ArgumentNullException.ThrowIfNull(vendorId);
        return Resolve().Where(a => string.Equals(a.VendorId, vendorId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Every accepted adapter of every reviewed vendor.</summary>
    public IReadOnlyList<DisplayAdapter> All() => Resolve();

    private List<DisplayAdapter> Resolve()
    {
        var accepted = new List<(DisplayDevice Device, string Vendor, string KeyPath)>();
        foreach (var device in devices.Enumerate())
        {
            if (!string.Equals(device.ClassGuid, DisplayDriverKeys.ClassGuid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(device.Enumerator, "PCI", StringComparison.OrdinalIgnoreCase)) continue;
            if (device.ProblemCode != 0) continue;
            if (device.Service is null || device.Service.Length == 0 || string.Equals(device.Service, "BasicDisplay", StringComparison.OrdinalIgnoreCase)) continue;
            if (!DisplayDriverKeys.TryVendor(device.HardwareIds, out var vendor) || !DisplayDriverKeys.Vendors.Contains(vendor, StringComparer.Ordinal)) continue;
            if (!DisplayDriverKeys.TryResolve(device.DriverKey, out var keyPath)) continue;
            accepted.Add((device, vendor, keyPath));
        }
        var sharedKeys = accepted.GroupBy(a => a.KeyPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usable = accepted.Where(a => !sharedKeys.Contains(a.KeyPath)).OrderBy(a => a.Device.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
        var duplicateNames = usable.GroupBy(a => a.Device.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return usable.Select(a => new DisplayAdapter(a.Device.InstanceId, Name(a.Device, duplicateNames), a.Vendor, a.KeyPath)).ToList();
    }

    private static string Name(DisplayDevice device, HashSet<string> duplicates)
    {
        var name = string.IsNullOrWhiteSpace(device.Name) ? device.InstanceId : device.Name.Trim();
        return duplicates.Contains(device.Name) && !string.IsNullOrWhiteSpace(device.Location) ? string.Create(CultureInfo.InvariantCulture, $"{name} ({device.Location.Trim()})") : name;
    }
}
