// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;

namespace Winora.Desktop;

/// <summary>Builds the row that shows one disk the same way everywhere: the title with the letter, chips (SYSTEM, bus, file system), the model, the usage text and the bar.</summary>
public static class DriveRowFactory
{
    public static DriveRow Build(DriveEntry d, LocalizationService text)
    {
        var name = !string.IsNullOrWhiteSpace(d.Label) ? d.Label : text[d.Kind == DriveKind.Removable ? "DashRemovableDisk" : "DashLocalDisk"];
        var chips = new List<DriveChip>();
        if (d.IsSystem) chips.Add(new(text["DashSystemChip"], true));
        if (!string.IsNullOrWhiteSpace(d.Bus)) chips.Add(new(d.Bus));
        if (!string.IsNullOrWhiteSpace(d.MediaType)) chips.Add(new(d.MediaType));
        if (!string.IsNullOrWhiteSpace(d.FileSystem)) chips.Add(new(d.FileSystem));
        if (d.Kind == DriveKind.Removable) chips.Add(new(text["DashRemovableChip"]));
        var low = d.TotalBytes > 0 && d.FreeBytes * 10 < d.TotalBytes;
        var used = text.Size(Bytes(d.UsedBytes));
        var total = text.Size(Bytes(d.TotalBytes));
        var free = text.Size(Bytes(d.FreeBytes));
        var details = new List<(string?, bool)>();
        if (!string.IsNullOrWhiteSpace(d.Brand)) details.Add((text.Format("DashLabelValue", text["DashBrand"], text.Ltr(d.Brand)), false));
        if (!string.IsNullOrWhiteSpace(d.Serial)) details.Add((text.Format("DashLabelValue", text["DashSerial"], text.Ltr(d.Serial)), false));
        var detailText = Bidi.Compose("  ·  ", text.Direction, [.. details]);
        // A Latin label and the letter form one left-to-right run: pinned apart, a right-to-left page would move the closing bracket to the wrong end ("(Data (D:").
        var title = Bidi.HasRightToLeft(name) ? text.Format("DashDriveTitle", text.Ltr(d.Letter), name) : text.Ltr(text.Format("DashDriveTitle", d.Letter, name));
        return new(title, chips, string.IsNullOrWhiteSpace(d.Model) ? d.Model : text.Name(d.Model), text.Format("DashDriveUsedOf", used, total, free), d.UsedPercent,
            low ? text["DashLowSpace"] : null, detailText.Length > 0 ? detailText : null);
    }

    private static long Bytes(ulong value) => (long)Math.Min(value, long.MaxValue);
}
