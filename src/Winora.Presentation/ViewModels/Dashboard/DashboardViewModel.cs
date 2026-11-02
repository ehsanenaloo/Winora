// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;

namespace Winora.Desktop;

public sealed record DashboardLine(string Label, string Value);

/// <summary>One group of facts on the dashboard. The automation name carries every fact, so a screen reader reads the card in one stop.</summary>
public sealed class DashboardCard(string title, IconKey icon, IReadOnlyList<DashboardLine> lines)
{
    public string Title { get; } = title;
    public IconKey Icon { get; } = icon;
    public IReadOnlyList<DashboardLine> Lines { get; } = lines;
    public string AutomationName => $"{Title}: " + string.Join(", ", Lines.Select(l => $"{l.Label} {l.Value}"));
}

/// <summary>One drive with a usage bar. The bar is decoration: the numbers are text.</summary>
public sealed record DriveRow(string Title, IReadOnlyList<DriveChip> Chips, string? Model, string UsedText, double UsedPercent, string? Warning, string? Details = null)
{
    /// <summary>Brand and serial number, for the page only: the copied summary leaves the serial number out.</summary>
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public bool HasWarning => Warning is not null;
    public bool HasModel => !string.IsNullOrWhiteSpace(Model);
    /// <summary>Good below 75 percent used, Fair up to 90, Low above: the bar takes the color of the band.</summary>
    public string Band => UsedPercent >= 90 ? "Low" : UsedPercent >= 75 ? "Fair" : "Good";
    public string AutomationName => $"{Title}, {string.Join(", ", Chips.Select(c => c.Text))}" + (HasModel ? ", " + Model : "") + $", {UsedText}" + (Warning is null ? "" : ", " + Warning);
}

/// <summary>The first card of This PC: the device model and one chip for each platform and security fact.</summary>
public sealed record PlatformCard(string Title, string? Subtitle, IReadOnlyList<DriveChip> Chips)
{
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
    public string AutomationName => $"{Title}" + (HasSubtitle ? $", {Subtitle}" : "") + ": " + string.Join(", ", Chips.Select(c => c.Text));
}

/// <summary>A small label on a disk: SYSTEM, the bus (NVMe, SATA, USB) or the file system.</summary>
public sealed record DriveChip(string Text, bool Emphasis = false);

/// <summary>What Winora itself knows about its own state; filled in off the UI thread.</summary>
public sealed record WinoraSelfState(bool Elevated, bool UnsafeLocation, int CustomizeSettings, int OptimizeSettings, int PowerSettings);

/// <summary>
/// The system dashboard on the Overview page: Windows, processor, memory, graphics, drives, device, firmware, security, power plan and Winora's own state. It reads once when the page opens
/// and again when the user asks (the Refresh button or F5); there is no timer, so the page costs nothing while it sits idle. A part that cannot be read shows "Unknown".
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly DashboardService service;
    private readonly Func<WinoraSelfState> self;
    private readonly IClipboardService clipboard;
    private CancellationTokenSource? reading;
    private Task work = Task.CompletedTask;
    private SystemSnapshot? snapshot;
    private PlatformCard? platform;
    private WinoraSelfState? state;
    private bool isLoading;
    private string? copyNotice;
    private bool loadedOnce, failed;

    public DashboardViewModel(LocalizationService text, DashboardService service, Func<WinoraSelfState> self, IClipboardService clipboard)
    {
        this.text = text; this.service = service; this.self = self; this.clipboard = clipboard;
        RefreshCommand = new(() => work = RefreshAsync(), () => !IsLoading);
        CancelCommand = new(Cancel, () => IsLoading);
        CopyCommand = new(Copy, () => snapshot is not null);
        text.PropertyChanged += (_, _) => { Rebuild(); OnPropertyChanged(string.Empty); };
    }

    public ObservableCollection<DashboardCard> Cards { get; } = [];
    public ObservableCollection<DriveRow> Drives { get; } = [];
    public PlatformCard? Platform { get => platform; private set { if (SetProperty(ref platform, value)) OnPropertyChanged(nameof(HasPlatform)); } }
    public bool HasPlatform => platform is not null;
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CopyCommand { get; }

    public bool IsLoading { get => isLoading; private set { if (SetProperty(ref isLoading, value)) { RefreshCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(UpdatedText)); OnPropertyChanged(nameof(ShowLoading)); OnPropertyChanged(nameof(ShowFailure)); } } }
    /// <summary>The first read is running and nothing is shown yet: the tab shows the loading skeleton.</summary>
    public bool ShowLoading => IsLoading && snapshot is null;
    /// <summary>The read failed and nothing was read before: the tab shows the error state with Try again.</summary>
    public bool ShowFailure => failed && !IsLoading && snapshot is null;
    public bool HasData => snapshot is not null;
    /// <summary>The system drive of the last read, or null when nothing was read or Windows gave no drive list.</summary>
    public DriveEntry? SystemDrive => snapshot?.Drives?.FirstOrDefault(d => d.IsSystem);
    /// <summary>The time since Windows started, at the last read.</summary>
    public TimeSpan? OsUptime => snapshot?.Os?.Uptime;
    public DateTimeOffset? ReadUtc => snapshot?.CollectedUtc;
    public bool HasDrives => Drives.Count > 0;
    public string Unknown => text["DashUnknown"];
    public string UpdatedText => IsLoading ? text["DashReading"] : snapshot is { } s ? text.Format("DashUpdated", text.When(s.CollectedUtc.ToLocalTime(), "T")) : text["DashNotRead"];
    public string? CopyNotice { get => copyNotice; private set { if (SetProperty(ref copyNotice, value)) OnPropertyChanged(nameof(HasCopyNotice)); } }
    public bool HasCopyNotice => !string.IsNullOrEmpty(copyNotice);

    /// <summary>Reads on the first visit only; a later visit keeps what is shown until the user refreshes.</summary>
    public void Activate() { if (!loadedOnce) work = RefreshAsync(); }
    public void Cancel() => reading?.Cancel();
    public Task WhenIdleAsync() => work;

    public async Task RefreshAsync()
    {
        reading?.Cancel();
        var cts = reading = new CancellationTokenSource();
        loadedOnce = true; failed = false;
        IsLoading = true; CopyNotice = null;
        try
        {
            var read = service.CollectAsync(cts.Token);
            var own = Task.Run(self, CancellationToken.None);
            _ = own.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // observed even when the read above fails first
            snapshot = await read.ConfigureAwait(true);
            state = await own.ConfigureAwait(true);
            Rebuild();
        }
        catch (OperationCanceledException) { loadedOnce = snapshot is not null; }
        catch (Exception) { loadedOnce = snapshot is not null; failed = true; } // nobody awaits this refresh: a failed read leaves what is shown and the user can refresh again
        finally
        {
            if (ReferenceEquals(reading, cts)) { reading = null; IsLoading = false; }
            cts.Dispose();
        }
    }

    // ---- building the cards ----

    private string U(string? value) => string.IsNullOrWhiteSpace(value) ? Unknown : value;
    /// <summary>A value read from Windows or the hardware (names, versions, models): pinned left to right when it is Latin.</summary>
    private string? PlanText(SystemSnapshot s) => string.IsNullOrWhiteSpace(s.PowerPlan) ? null : PowerViewModel.PlanName(text, s.PowerPlanId, s.PowerPlan);
    private string V(string? value) => string.IsNullOrWhiteSpace(value) ? Unknown : text.Name(value);
    private string N(long value) => text.Number(value);
    private string Size(ulong? bytes) => bytes is { } b ? text.Size((long)Math.Min(b, long.MaxValue)) : Unknown;
    private string YesNo(bool value) => text[value ? "DashYes" : "DashNo"];
    private DashboardLine L(string labelKey, string value) => new(text[labelKey], value);

    private void Rebuild()
    {
        Cards.Clear(); Drives.Clear();
        Platform = snapshot is { } platformSource ? BuildPlatform(platformSource) : null;
        if (snapshot is { } s)
        {
            Cards.Add(WindowsCard(s.Os));
            Cards.Add(CpuCard(s.Cpu));
            Cards.Add(MemoryCard(s.Memory));
            Cards.Add(GraphicsCard(s.Gpus));
            Cards.Add(DeviceCard(s.Device, s.Battery));
            Cards.Add(PowerCard(PlanText(s), s.Battery));
            if (state is not null) Cards.Add(WinoraCard(state));
            foreach (var row in DriveRows(s.Drives)) Drives.Add(row);
        }
        CopyCommand.NotifyCanExecuteChanged();
        foreach (var name in new[] { nameof(HasData), nameof(HasDrives), nameof(UpdatedText), nameof(DrivesUnknown) }) OnPropertyChanged(name);
    }

    private PlatformCard BuildPlatform(SystemSnapshot s)
    {
        string Enabled(bool? on) => on is { } v ? text[v ? "DashEnabled" : "DashDisabled"] : Unknown;
        var chips = new List<DriveChip>
        {
            new($"{text["DashFirmwareChip"]}: " + (s.Firmware?.Mode switch { FirmwareMode.Uefi => text["DashUefi"], FirmwareMode.Bios => text["DashLegacy"], _ => Unknown })),
            new($"{text["DashSecureBoot"]}: " + (s.Firmware?.SecureBoot switch { SecureBootState.On => text["DashEnabled"], SecureBootState.Off => text["DashDisabled"], SecureBootState.NotSupported => text["DashNotSupported"], _ => Unknown })),
            new($"{text["DashTpm"]}: " + (s.Tpm is null ? Unknown : s.Tpm.Present ? (s.Tpm.Version is { } tv ? tv : text["DashEnabled"]) : text["DashTpmAbsent"])),
            new($"{text["DashVbsChip"]}: " + Enabled(s.Isolation?.VbsRunning)),
            new($"{text["DashMemoryIntegrityChip"]}: " + Enabled(s.Isolation?.MemoryIntegrityRunning)),
            new($"{text["DashPowerChip"]}: " + (PlanText(s) ?? Unknown)),
            new($"{text["DashUptime"]}: " + (s.Os?.Uptime is { } up ? Uptime(up) : Unknown)),
        };
        var model = string.Join(' ', new[] { s.Device?.Manufacturer, s.Device?.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new(text["DashPlatformTitle"], model.Length > 0 ? text.Name(model) : null, chips);
    }

    /// <summary>The drives as they are now, for pages that list disks.</summary>
    public IReadOnlyList<DriveEntry>? ReadDrivesNow() => service.ReadDrives();

    public bool DrivesUnknown => snapshot is not null && snapshot.Drives is null;

    private DashboardCard WindowsCard(OsInfo? os)
    {
        var lines = new List<DashboardLine>
        {
            L("DashName", V(os?.Name)), L("DashEdition", V(os?.Edition)), L("DashVersion", V(os?.DisplayVersion)),
            L("DashBuild", os?.Build is { } b ? text.Ltr(os.Ubr is { } u ? $"{b}.{u}" : b.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Unknown),
            L("DashInstalled", os?.InstalledUtc is { } i ? text.When(i.ToLocalTime(), "d") : Unknown),
            L("DashUptime", os?.Uptime is { } up ? Uptime(up) : Unknown), L("DashArch", V(Architecture(os?.Architecture))),
        };
        return new(text["DashOs"], IconKey.WindowDevTools24, lines);
    }

    /// <summary>Windows names the processor kinds "X64" and "X86"; the usual spelling is lower case.</summary>
    private static string? Architecture(string? value) => value is "X64" or "X86" ? value.ToLowerInvariant() : value;

    private string Uptime(TimeSpan span) => span.TotalDays >= 1
        ? text.Format("DashUptimeDays", (int)span.TotalDays, span.Hours)
        : text.Format("DashUptimeHours", span.Hours, span.Minutes);

    private DashboardCard CpuCard(CpuInfo? cpu) => new(text["DashCpu"], IconKey.DeveloperBoard24,
    [
        L("DashName", V(cpu?.Name)), L("DashCores", cpu?.Cores is { } c ? N(c) : Unknown), L("DashThreads", cpu?.Threads is { } t ? N(t) : Unknown),
        L("DashSpeed", cpu?.BaseMhz is { } m ? text.Format("DashMhz", N(m)) : Unknown),
    ]);

    private DashboardCard MemoryCard(MemoryInfo? memory)
    {
        var used = memory?.UsedBytes;
        var inUse = used is null ? Unknown : memory?.TotalBytes is { } total && total > 0 ? text.Format("DashSizePercent", Size(used), text.Percent(Math.Round(used.Value * 100.0 / total, MidpointRounding.AwayFromZero))) : Size(used);
        return new(text["DashMemory"], IconKey.Ram20,
        [
            L("DashTotal", Size(memory?.TotalBytes)), L("DashInUse", inUse), L("DashAvailable", Size(memory?.AvailableBytes)),
            L("DashMemSpeed", memory?.SpeedMts is { } s ? text.Format("DashMts", N(s)) : Unknown),
        ]);
    }

    private DashboardCard GraphicsCard(IReadOnlyList<GpuInfo>? gpus)
    {
        var lines = new List<DashboardLine>();
        if (gpus is null) lines.Add(L("DashName", Unknown));
        else if (gpus.Count == 0) lines.Add(L("DashName", text["DashNoGpu"]));
        else
            foreach (var g in gpus)
            {
                lines.Add(L("DashName", V(g.Name)));
                lines.Add(L("DashVendor", V(g.Vendor)));
                lines.Add(L("DashDriver", V(g.DriverVersion)));
                lines.Add(L("DashVideoMemory", Size(g.DedicatedBytes)));
            }
        return new(text["DashGpu"], IconKey.Games24, lines);
    }

    private DashboardCard DeviceCard(DeviceInfo? device, BatteryInfo? battery) => new(text["DashDevice"], IconKey.Laptop24,
    [
        L("DashManufacturer", V(device?.Manufacturer)), L("DashModel", V(device?.Model)), L("DashBios", JoinBios(device) ?? Unknown),
        L("DashBattery", battery is null ? Unknown : battery.Present ? text.Format("DashBatteryPercent", battery.Percent is { } p ? N(p) : "?") : text["DashBatteryNone"]),
    ]);

    /// <summary>Vendor and version are Latin data; the release date is a month/day/year text that is shown in the calendar and digits of the page.</summary>
    private string? JoinBios(DeviceInfo? d)
    {
        if (d is null) return null;
        var parts = new List<(string?, bool)> { (d.BiosVendor, true), (d.BiosVersion, true) };
        if (!string.IsNullOrWhiteSpace(d.BiosDate))
            parts.Add(DateTime.TryParseExact(d.BiosDate.Trim(), ["MM/dd/yyyy", "M/d/yyyy", "yyyyMMdd"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                ? (text.When(date, "d"), false) : (d.BiosDate, true));
        var joined = Bidi.Compose(text["ListSeparator"], text.Direction, [.. parts]);
        return joined.Length > 0 ? joined : null;
    }

    private DashboardCard PowerCard(string? plan, BatteryInfo? battery)
    {
        var lines = new List<DashboardLine> { L("DashPlan", plan ?? Unknown) };
        if (battery is { Present: true, OnMains: { } mains }) lines.Add(L("DashPowerSource", text[mains ? "DashOnMains" : "DashOnBatteryPower"]));
        return new(text["DashPower"], IconKey.PlugConnected24, lines);
    }

    private DashboardCard WinoraCard(WinoraSelfState s) => new(text["DashWinora"], IconKey.Shield24,
    [
        L("DashElevated", YesNo(s.Elevated)), L("DashLocation", YesNo(!s.UnsafeLocation)), L("DashCustomizeCount", N(s.CustomizeSettings)),
        L("DashOptimizeCount", N(s.OptimizeSettings)), L("DashPowerCount", N(s.PowerSettings)),
    ]);

    private IEnumerable<DriveRow> DriveRows(IReadOnlyList<DriveEntry>? drives)
    {
        if (drives is null) yield break;
        foreach (var d in drives.OrderByDescending(x => x.IsSystem).ThenBy(x => x.Letter, StringComparer.OrdinalIgnoreCase))
        {
            yield return DriveRowFactory.Build(d, text);
        }
    }

    // ---- summary ----

    /// <summary>The page as plain text in the language of the page, one fact per line, with no serial numbers or identifiers.</summary>
    public string SummaryText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(text["AppName"] + " - " + text["DashSummaryTitle"]);
        if (Platform is { } platformCard)
        {
            sb.AppendLine().AppendLine(platformCard.Title);
            if (platformCard.HasSubtitle) sb.Append("  ").AppendLine(platformCard.Subtitle);
            foreach (var chip in platformCard.Chips) sb.Append("  ").AppendLine(chip.Text);
        }
        foreach (var card in Cards)
        {
            sb.AppendLine().AppendLine(card.Title);
            foreach (var line in card.Lines) sb.Append("  ").Append(line.Label).Append(": ").AppendLine(line.Value);
        }
        if (Drives.Count > 0 || DrivesUnknown)
        {
            sb.AppendLine().AppendLine(text["DashStorage"]);
            if (DrivesUnknown) sb.Append("  ").AppendLine(Unknown);
            foreach (var d in Drives) sb.Append("  ").Append(d.Title).Append(": ").Append(string.Join(", ", d.Chips.Select(c => c.Text))).Append(d.HasModel ? ", " + d.Model : "").Append(", ").AppendLine(d.UsedText);
        }
        // Direction marks are display-only; the copied text carries the plain values.
        return Bidi.Strip(sb.ToString());
    }

    private void Copy() => CopyNotice = clipboard.TrySetText(SummaryText()) ? text["DashCopied"] : text["DashCopyFailed"];
}
