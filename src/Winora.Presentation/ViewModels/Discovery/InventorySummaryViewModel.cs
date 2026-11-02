// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>One category tile on Overview. Reads the shared slot; never scans by itself.</summary>
public sealed class InventoryTile : ObservableObject
{
    private readonly LocalizationService text;
    private readonly Func<(int Count, int Issues, DateTimeOffset At)?> read;
    private WinoraError? scanError;

    public InventoryTile(string key, IconKey icon, LocalizationService text, Func<(int Count, int Issues, DateTimeOffset At)?> read)
    {
        Key = key; Icon = icon; this.text = text; this.read = read;
    }
    public string Key { get; }
    public IconKey Icon { get; }
    public string Name => text[Key];
    public DateTimeOffset? ScannedAt => read()?.At;
    public bool HasCount => read() is { } r && !(r.Count == 0 && r.Issues > 0);
    public string CountText => read() is { } r && HasCount ? text.Number(r.Count) : text["InvNoCount"];
    public string Caption => read() is { } r
        ? (r.Count == 0 && r.Issues > 0 ? text["InvCouldNotRead"] : r.Issues > 0 ? text.Format("InvUnreadable", text.Number(r.Issues)) : r.Count == 0 ? text["InvNothingFound"] : text["InvReadOk"])
        : scanError is not null ? text["InvCouldNotRead"] : text["InvNotScanned"];
    public string AutomationName => $"{Name}: {CountText}, {Caption}";
    public void SetError(WinoraError? error) { scanError = error; Refresh(); }
    public void Refresh()
    {
        foreach (var name in new[] { nameof(Name), nameof(ScannedAt), nameof(HasCount), nameof(CountText), nameof(Caption), nameof(AutomationName) }) OnPropertyChanged(name);
    }
}

/// <summary>
/// "System inventory" on Overview: counts from the last read-only scan. Nothing is scanned until the user asks, and leaving
/// Overview cancels a scan that is still running.
/// </summary>
public sealed class InventorySummaryViewModel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly InventoryModel inventory;
    private readonly ErrorPresenter errors;
    private CancellationTokenSource? scan;
    private bool isScanning;

    public InventorySummaryViewModel(LocalizationService text, InventoryModel inventory, ErrorPresenter errors)
    {
        this.text = text; this.inventory = inventory; this.errors = errors;
        Tiles =
        [
            Tile("Startup", IconKey.Rocket24, inventory.Startup),
            Tile("ScheduledTasks", IconKey.CalendarClock24, inventory.Tasks),
            Tile("Services", IconKey.Wrench24, inventory.Services),
            Tile("Applications", IconKey.Apps24, inventory.Applications),
        ];
        ScanCommand = new(ScanAsync, () => !IsScanning);
        CancelCommand = new(Cancel, () => IsScanning);
        text.PropertyChanged += (_, _) => { foreach (var t in Tiles) t.Refresh(); OnPropertyChanged(string.Empty); };
    }

    private InventoryTile Tile<T>(string key, IconKey icon, InventorySlot<T> slot)
    {
        var tile = new InventoryTile(key, icon, text, () => slot.Last is { } r ? (r.Items.Count, r.Issues.Count, r.CollectedAtUtc) : null);
        slot.Changed += (_, _) => { tile.SetError(null); OnPropertyChanged(nameof(LastScannedText)); OnPropertyChanged(nameof(HasScanned)); };
        return tile;
    }

    public IReadOnlyList<InventoryTile> Tiles { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public bool IsScanning
    {
        get => isScanning;
        private set
        {
            if (!SetProperty(ref isScanning, value)) return;
            ScanCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        }
    }
    public bool HasScanned => Tiles.Any(t => t.ScannedAt is not null);
    public string LastScannedText
    {
        get
        {
            var latest = Tiles.Select(t => t.ScannedAt).Where(d => d is not null).Max();
            return latest is { } at ? text.Format("DiscLastScanned", text.When(at.ToLocalTime(), "g")) : text["InvNeverScanned"];
        }
    }

    public void Cancel() => scan?.Cancel();

    /// <summary>Reads all four categories concurrently. A failure in one never hides the others.</summary>
    public async Task ScanAsync()
    {
        scan?.Cancel();
        var cts = scan = new CancellationTokenSource();
        IsScanning = true;
        try
        {
            await Task.WhenAll(
                Run(Tiles[0], inventory.Startup, cts.Token), Run(Tiles[1], inventory.Tasks, cts.Token),
                Run(Tiles[2], inventory.Services, cts.Token), Run(Tiles[3], inventory.Applications, cts.Token));
        }
        finally
        {
            if (ReferenceEquals(scan, cts)) { scan = null; IsScanning = false; }
            cts.Dispose();
        }
    }

    private async Task Run<T>(InventoryTile tile, InventorySlot<T> slot, CancellationToken ct)
    {
        try { await slot.ScanAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { tile.SetError(errors.Report(ex, "Inventory " + tile.Key) ?? WinoraError.From(ErrorCode.Unexpected)); }
    }
}
