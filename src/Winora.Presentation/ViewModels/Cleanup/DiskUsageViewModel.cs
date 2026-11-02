// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>Human-readable byte counts in the selected culture, with the same unit labels as the other pages. A whole value has no decimals ("16 GB", "1.5 KB").</summary>
public static class SizeFormat
{
    public static string Bytes(LocalizationService text, long bytes)
    {
        if (bytes < 1024) return text.Format("SizeBytes", text.Number(bytes));
        double value = bytes / 1024.0;
        string[] units = ["SizeKB", "SizeMB", "SizeGB"];
        for (var i = 0; i < units.Length; i++)
        {
            var digits = value < 10 ? 1 : 0;
            if (i == 2) digits = value < 10 ? 2 : 1;
            // A value that rounds to 1024 belongs to the next unit ("1,024 KB" is "1 MB").
            if (Math.Round(value, digits, MidpointRounding.AwayFromZero) < 1024 || i == units.Length - 1 && value < 1024) return text.Format(units[i], Value(text, value, digits));
            value /= 1024;
        }
        return text.Format("SizeTB", Value(text, value, 2));
    }

    private static string Value(LocalizationService text, double value, int digits)
    {
        var rounded = Math.Round(value, digits, MidpointRounding.AwayFromZero);
        return text.Number(rounded, rounded == Math.Floor(rounded) ? 0 : digits);
    }
}

public sealed record ScanLocation(string Path, string Label)
{
    /// <summary>The label as shown: a drive letter or a path is Latin data and keeps its order in a right-to-left page (the marks do nothing in a left-to-right one).</summary>
    public string Display => Bidi.Ltr(Label, TextDirection.RightToLeft);
}

/// <summary>One disk shown as a card at the top of the Disk usage page. Choosing it selects the disk and scans it.</summary>
public sealed class DiskCard : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool isSelected;

    private readonly Func<string, string> markSelected;

    public DiskCard(DriveRow row, ScanLocation location, Action<DiskCard> choose, Func<string, string>? markSelected = null)
    {
        Row = row; Location = location; this.markSelected = markSelected ?? (name => name);
        ChooseCommand = new(() => choose(this));
    }

    public DriveRow Row { get; }
    public ScanLocation Location { get; }
    public bool IsSelected { get => isSelected; set { if (SetProperty(ref isSelected, value)) OnPropertyChanged(nameof(AutomationName)); } }
    public CommunityToolkit.Mvvm.Input.RelayCommand ChooseCommand { get; }
    /// <summary>The name with the state: the chosen disk says so, so it does not depend on the border colour.</summary>
    public string AutomationName => IsSelected ? markSelected(Row.AutomationName) : Row.AutomationName;
}

/// <summary>One file type of the map legend: its name, colour, line pattern and, for the folder on screen, the size and share as text. The table beside the map is the accessible form of the same numbers.</summary>
public sealed record LegendItem(string Label)
{
    public DiskFileKind Kind { get; init; } = DiskFileKind.Other;
    public string SizeText { get; init; } = "";
    public string PercentText { get; init; } = "";
    public bool ShowPattern { get; init; }
    public string PatternName { get; init; } = "";
    public bool HasNumbers => SizeText.Length > 0;
    public string Caption => HasNumbers ? $"{Label} · {SizeText} · {PercentText}" : Label;
    public string AutomationName => HasNumbers ? $"{Label}, {PatternName}, {SizeText}, {PercentText}" : $"{Label}, {PatternName}";
}

/// <summary>One step of the path from the scan root to the folder being shown.</summary>
/// <summary>The two ways to read the folder: the map, or the list that says the same in words.</summary>
public sealed class DiskViewChoice(string key, string label)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public override string ToString() => Label;
    public override bool Equals(object? obj) => obj is DiskViewChoice other && other.Key == Key;
    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
}

public sealed class BreadcrumbItem(DiskNode node, string label, RelayCommand<DiskNode> navigate)
{
    public DiskNode Node { get; } = node;
    public string Label { get; } = label;
    public RelayCommand<DiskNode> Navigate { get; } = navigate;
}

/// <summary>
/// One row of the list that shows the same items as the treemap. The list is the keyboard and screen reader equivalent of the map.
/// The texts are made when the row is first shown: a folder like WinSxS has thousands of rows and the list shows a few at a time.
/// </summary>
public sealed class DiskItemRow(DiskNode node, string name, double percent, DiskFileKind kind, LocalizationService text)
{
    private string? sizeText, percentText, filesText, kindText;
    public DiskNode Node { get; } = node;
    public string Name { get; } = name;
    public string SizeText => sizeText ??= SizeFormat.Bytes(text, Node.Size);
    public string PercentText => percentText ??= text.Format("DiskLegendPercent", text.Number(Percent, 1));
    public double Percent { get; } = percent;
    public string FilesText => filesText ??= text.Format("DiskRowFiles", text.Number(Node.FileCount));
    public string KindText => kindText ??= text.Enum(Kind);
    public DiskFileKind Kind { get; } = kind;
    public bool CanOpen => Node.IsDirectory && Node.Children.Count > 0;
    public IconKey Symbol => DiskIcons.For(Kind);
    public string AutomationName => $"{Name}, {SizeText}, {PercentText}, {KindText}, {FilesText}";
}

/// <summary>The icon of a file type in the list.</summary>
public static class DiskIcons
{
    public static IconKey For(DiskFileKind kind) => kind switch
    {
        DiskFileKind.Folder => IconKey.Folder24,
        DiskFileKind.Documents => IconKey.Document24,
        DiskFileKind.Images => IconKey.Image24,
        DiskFileKind.Video => IconKey.Video24,
        DiskFileKind.Audio => IconKey.MusicNote224,
        DiskFileKind.Archives => IconKey.FolderZip24,
        DiskFileKind.Code => IconKey.Code24,
        DiskFileKind.Programs => IconKey.AppGeneric24,
        DiskFileKind.SystemData => IconKey.Database24,
        DiskFileKind.Unlisted => IconKey.DocumentMultiple24,
        DiskFileKind.Unreadable => IconKey.FolderProhibited24,
        _ => IconKey.Document24,
    };
}

public enum DiskSortKey { Size, Name, Files }

/// <summary>One sort button above the list. The arrow shows the direction of the active one.</summary>
public sealed class DiskSortHeader(DiskSortKey key, string label, string automationName, bool isActive, bool descending, RelayCommand<DiskSortKey> command)
{
    public DiskSortKey Key { get; } = key;
    public string Label { get; } = label;
    public string AutomationName { get; } = automationName;
    public bool IsActive { get; } = isActive;
    public string Arrow { get; } = isActive ? descending ? "↓" : "↑" : "";
    public RelayCommand<DiskSortKey> Command { get; } = command;
}

public enum DiskMenuAction { ZoomIn, ZoomOut, ShowInExplorer, CopyPath, CopyDetails, ScanFolder }

/// <summary>One entry of the context menu of the map or of a list row. A separator has no action.</summary>
public sealed class DiskMenuEntry
{
    public DiskMenuEntry(DiskMenuAction? action, string label, bool isEnabled = true, System.Windows.Input.ICommand? command = null) { Action = action; Label = label; IsEnabled = isEnabled; Command = command; }
    public DiskMenuAction? Action { get; }
    public string Label { get; }
    public bool IsEnabled { get; }
    public System.Windows.Input.ICommand? Command { get; }
    public bool IsSeparator => Action is null;
    public static DiskMenuEntry Separator() => new(null, "");
}

/// <summary>
/// Disk usage page model: scan a drive or folder (read-only), show the largest items as a treemap and a list, drill into folders and go back up. Nothing here changes a file.
/// </summary>
public sealed class DiskUsageViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService text;
    private readonly IDiskScanner scanner;
    private readonly IFolderPicker picker;
    private readonly IFileLocationOpener? opener;
    private readonly IClipboardService? clipboard;
    private readonly Func<IReadOnlyList<ScanLocation>> locations;
    private CancellationTokenSource? scanCts;
    private Task scanTask = Task.CompletedTask;
    private DiskScanResult? result;
    private readonly Func<IReadOnlyList<DriveEntry>?>? drives;
    private DiskNode? current;
    private DiskNode? selectedNode;
    private ScanLocation? selectedLocation;
    private string progressText = "";
    private string? errorText;
    private bool isScanning;
    private bool showPatterns;
    private string statusText = "";
    private DiskSortKey sortKey = DiskSortKey.Size;
    private bool sortDescending = true;

    public DiskUsageViewModel(LocalizationService text, IDiskScanner scanner, IFolderPicker picker, Func<IReadOnlyList<ScanLocation>>? locations = null, Func<IReadOnlyList<DriveEntry>?>? drives = null, IFileLocationOpener? opener = null, IClipboardService? clipboard = null)
    {
        this.drives = drives; this.text = text; this.scanner = scanner; this.picker = picker; this.locations = locations ?? DefaultLocations; this.opener = opener; this.clipboard = clipboard;
        ScanCommand = new(ScanSelectedAsync, () => !IsScanning && SelectedLocation is not null);
        CancelCommand = new(CancelScan, () => IsScanning);
        ChooseFolderCommand = new(ChooseFolderAsync, () => !IsScanning);
        UpCommand = new(Up, () => Current?.Parent is not null);
        OpenCommand = new(OpenSelected, () => OpenTarget() is not null);
        NavigateCommand = new(node => { if (node is not null) NavigateTo(node); });
        SortCommand = new(SortBy);
        text.PropertyChanged += (_, _) => { RebuildItems(); RebuildBreadcrumbs(); RebuildSortHeaders(); BuildDiskCards(); OnPropertyChanged(string.Empty); };
        Refresh();
        RebuildSortHeaders();
    }

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand ChooseFolderCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand<DiskNode> NavigateCommand { get; }
    public RelayCommand<DiskSortKey> SortCommand { get; }

    public ObservableCollection<ScanLocation> Locations { get; } = [];
    public ObservableCollection<DiskCard> Disks { get; } = [];
    public bool HasDisks => Disks.Count > 0;
    /// <summary>The rows of the open folder. Replaced as a whole: a folder like WinSxS has thousands of children, and adding them one by one updates the list thousands of times.</summary>
    public ObservableCollection<DiskItemRow> Items { get; private set; } = [];
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];
    public ObservableCollection<DiskSortHeader> SortHeaders { get; } = [];

    public LocalizationService Text => text;
    public ScanLocation? SelectedLocation
    {
        get => selectedLocation;
        set { if (SetProperty(ref selectedLocation, value)) ScanCommand.NotifyCanExecuteChanged(); }
    }

    public bool IsScanning
    {
        get => isScanning;
        private set
        {
            if (!SetProperty(ref isScanning, value)) return;
            ScanCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ChooseFolderCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ShowEmptyState)); OnPropertyChanged(nameof(ShowResult)); OnPropertyChanged(nameof(IsNotScanning)); OnPropertyChanged(nameof(ShowScanSkeleton)); OnPropertyChanged(nameof(ShowScanRing));
        }
    }

    public string ProgressText { get => progressText; private set => SetProperty(ref progressText, value); }
    public string? ErrorText { get => errorText; private set { if (SetProperty(ref errorText, value)) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ShowEmptyState)); } } }
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public DiskScanResult? Result => result;
    public DiskNode? Root => result?.Root;
    public DiskNode? Current
    {
        get => current;
        private set
        {
            if (!SetProperty(ref current, value)) return;
            UpCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CurrentSummary)); OnPropertyChanged(nameof(MapAutomationName)); OnPropertyChanged(nameof(Legend));
        }
    }

    /// <summary>The node selected in the map or in the list. It is always inside <see cref="Current"/>.</summary>
    public DiskNode? SelectedNode
    {
        get => selectedNode;
        set
        {
            if (!SetProperty(ref selectedNode, value)) return;
            OnPropertyChanged(nameof(SelectedSummary)); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SelectedRow));
            OpenCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The list row that stands for the selection: the row of the item that contains the selected node.</summary>
    public DiskItemRow? SelectedRow
    {
        get => Items.FirstOrDefault(r => Contains(r.Node, SelectedNode));
        set { if (value is not null) SelectedNode = value.Node; }
    }

    public bool HasSelection => SelectedNode is not null;
    public bool IsNotScanning => !IsScanning;
    public Func<TreemapCell, string> DescribeCellFunc => DescribeCell;
    public Func<long, string> SizeTextFunc => bytes => SizeFormat.Bytes(text, bytes);
    public Func<int, string> RestFunc => count => text.Format("DiskRestItems", text.Number(count));

    /// <summary>Draws a line pattern per file type on the map and in the legend, for people who cannot rely on color.</summary>
    public bool ShowPatterns
    {
        get => showPatterns;
        set { if (SetProperty(ref showPatterns, value)) OnPropertyChanged(nameof(Legend)); }
    }

    /// <summary>The result of the last menu action (copied, could not open), read out by screen readers.</summary>
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }

    private string viewKey = "map";
    /// <summary>Map and List. The list is the text alternative of the map: it names every item with its size and share.</summary>
    public IReadOnlyList<DiskViewChoice> ViewChoices => [new("map", text["V2ClViewMap"]), new("list", text["V2ClViewList"])];
    public DiskViewChoice SelectedView
    {
        get => ViewChoices.First(c => c.Key == viewKey);
        set { if (value is not null && value.Key != viewKey) { viewKey = value.Key; OnPropertyChanged(nameof(SelectedView)); OnPropertyChanged(nameof(ShowMap)); } }
    }
    public bool ShowMap => viewKey == "map";
    public string ViewName => text["V2ClViewName"];
    public Func<DiskNode, string> DescribeFunc => Describe;
    public Func<DiskNode, string> DisplayFunc => Display;
    public Func<DiskNode, string> SizeFunc => n => SizeFormat.Bytes(text, n.Size);

    private static readonly DiskFileKind[] LegendKinds =
        [DiskFileKind.Documents, DiskFileKind.Images, DiskFileKind.Video, DiskFileKind.Audio, DiskFileKind.Archives, DiskFileKind.Code, DiskFileKind.Programs, DiskFileKind.SystemData, DiskFileKind.Other, DiskFileKind.Unlisted];
    public IReadOnlyList<LegendItem> Legend
    {
        get
        {
            var (totals, all) = Current is { } folder ? TotalsOf(folder) : ([], 0);
            return LegendKinds.Select(k =>
            {
                var size = totals.GetValueOrDefault(k);
                return new LegendItem(text.Enum(k))
                {
                    Kind = k, ShowPattern = showPatterns, PatternName = text["Pattern_" + LegendPatterns.For(k)],
                    SizeText = all > 0 ? SizeFormat.Bytes(text, size) : "", PercentText = all > 0 ? text.Format("DiskLegendPercent", text.Number((100.0 * size / all), 1)) : "",
                };
            }).ToList();
        }
    }

    /// <summary>Folders with more files than this get their legend totals on a worker thread; the legend fills in when they are ready.</summary>
    private const long SynchronousLegendFiles = 20_000;
    // Totals per folder of the current scan: the legend is read again on every navigation, language change and pattern switch, and a walk of a whole drive takes over 100 ms.
    private readonly Dictionary<DiskNode, (Dictionary<DiskFileKind, long> Totals, long All)> legendTotals = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DiskNode> legendPending = new(ReferenceEqualityComparer.Instance);

    private (Dictionary<DiskFileKind, long> Totals, long All) TotalsOf(DiskNode folder)
    {
        if (legendTotals.TryGetValue(folder, out var known)) return known;
        if (folder.FileCount <= SynchronousLegendFiles) return legendTotals[folder] = Count(folder);
        if (legendPending.Add(folder))
            _ = Task.Run(() => Count(folder)).ContinueWith(t =>
            {
                legendPending.Remove(folder);
                if (!t.IsCompletedSuccessfully || result?.Root is not { } root || !IsUnder(folder, root)) return;
                legendTotals[folder] = t.Result;
                if (ReferenceEquals(Current, folder)) OnPropertyChanged(nameof(Legend));
            }, SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext());
        return ([], 0);
    }

    private static bool IsUnder(DiskNode node, DiskNode root)
    {
        for (var n = node; n is not null; n = n.Parent) if (ReferenceEquals(n, root)) return true;
        return false;
    }

    private static (Dictionary<DiskFileKind, long> Totals, long All) Count(DiskNode folder)
    {
        var totals = new Dictionary<DiskFileKind, long>();
        long all = 0;
        Sum(folder, totals, ref all);
        return (totals, all);
    }

    /// <summary>Adds the sizes of the files below a node to the totals of their type. A collapsed group of small files counts as "Unlisted".</summary>
    private static void Sum(DiskNode node, Dictionary<DiskFileKind, long> totals, ref long all)
    {
        if (node.IsDirectory) { foreach (var child in node.Children) Sum(child, totals, ref all); return; }
        var kind = DiskFileKinds.Classify(node);
        totals[kind] = totals.GetValueOrDefault(kind) + node.Size;
        all += node.Size;
    }

    public bool ShowEmptyState => result is null && !IsScanning && !HasError;
    /// <summary>The first scan of a location is running: the page shows the loading skeleton where the map will be.</summary>
    public bool ShowScanSkeleton => result is null && IsScanning;
    /// <summary>A scan runs while a result is on screen: a busy ring next to the progress line.</summary>
    public bool ShowScanRing => result is not null && IsScanning;
    public bool ShowResult => result is not null;

    public string CurrentSummary => Current is null ? "" : text.Format("DiskCurrentSummary", SizeFormat.Bytes(text, Current.Size), text.Number(Current.FileCount));
    public string SelectedSummary => SelectedNode is null ? text["DiskNothingSelected"] : $"{Describe(SelectedNode)}\n{SelectedNode.FullPath}";
    public string MapAutomationName => Current is null ? text["DiskMapEmpty"] : text.Format("DiskMapAutomation", Display(Current), SizeFormat.Bytes(text, Current.Size));

    public string ResultSummary => result is null ? "" : text.Format("DiskResultSummary", SizeFormat.Bytes(text, result.Bytes), text.Number(result.Files), text.Number(result.Folders), text.Number(result.Duration.TotalSeconds, 1));
    public bool HasInaccessible => result is { Inaccessible: > 0 };
    public string InaccessibleText => result is null ? "" : text.Format("DiskInaccessible", text.Number(result.Inaccessible));
    public bool HasSkipped => result is { ReparsePointsSkipped: > 0 };
    public string SkippedText => result is null ? "" : text.Format("DiskSkippedLinks", text.Number(result.ReparsePointsSkipped));

    /// <summary>Resolves the display name of a node. The root shows its path, an aggregate and an unreadable folder show a translated label.</summary>
    public string Display(DiskNode node) => node.Kind switch
    {
        DiskNodeKind.Aggregate => text.Format("DiskOtherFiles", text.Number(node.FileCount)),
        DiskNodeKind.Unreadable => node.Name + " (" + text["DiskUnreadable"] + ")",
        _ => node.Name,
    };

    public string Describe(DiskNode node)
    {
        var parent = node.Parent?.Size ?? node.Size;
        var percent = parent > 0 ? node.Size * 100.0 / parent : 100.0;
        return text.Format("DiskNodeDescription", Display(node), text.Enum(DiskFileKinds.Classify(node)), SizeFormat.Bytes(text, node.Size), text.Number(percent, 1), text.Number(node.FileCount));
    }

    /// <summary>The tooltip of a map cell: name, full path, size and share of the scanned folder, file count and type.</summary>
    public string DescribeCell(TreemapCell cell)
    {
        var node = cell.Node;
        var size = cell.IsRest ? cell.RestSize : node.Size;
        var files = cell.IsRest ? cell.RestFiles : node.FileCount;
        var whole = Root?.Size ?? 0;
        var share = text.Format("DiskLegendPercent", whole > 0 ? text.Number((size * 100.0 / whole), 1) : "100");
        var name = cell.IsRest ? RestFunc(cell.RestItems) : Display(node);
        var kind = text.Enum(cell.IsRest ? DiskFileKind.Unlisted : DiskFileKinds.Classify(node));
        return string.Join("\n", name, node.FullPath, text.Format("DiskTipSize", SizeFormat.Bytes(text, size), share), text.Format("DiskRowFiles", text.Number(files)), kind);
    }

    // ---- context menu ----

    /// <summary>
    /// The context menu of a map cell or a list row. A folder can be opened in the map, shown in File Explorer, copied, scanned on its own and zoomed out of; a file or a group of small files can
    /// show its folder and be copied. Nothing here changes, runs or deletes a file.
    /// </summary>
    public IReadOnlyList<DiskMenuEntry> MenuFor(DiskNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        DiskMenuEntry Entry(DiskMenuAction action, string key, bool enabled = true) =>
            new(action, text[key], enabled, new AsyncRelayCommand(() => ExecuteAsync(action, node), () => enabled));
        var list = new List<DiskMenuEntry>();
        var canZoomOut = Current?.Parent is not null;
        switch (node.Kind)
        {
            case DiskNodeKind.Directory:
                if (node.Children.Count > 0) list.Add(Entry(DiskMenuAction.ZoomIn, "DiskMenuZoomIn", node != Current));
                list.Add(Entry(DiskMenuAction.ShowInExplorer, "DiskMenuShowFolder"));
                list.Add(DiskMenuEntry.Separator());
                list.Add(Entry(DiskMenuAction.CopyPath, "DiskMenuCopyPath"));
                list.Add(Entry(DiskMenuAction.CopyDetails, "DiskMenuCopyDetails"));
                list.Add(DiskMenuEntry.Separator());
                list.Add(Entry(DiskMenuAction.ScanFolder, "DiskMenuScanFolder", !IsScanning));
                break;
            case DiskNodeKind.Unreadable:
                list.Add(Entry(DiskMenuAction.ShowInExplorer, "DiskMenuShowFolder"));
                list.Add(DiskMenuEntry.Separator());
                list.Add(Entry(DiskMenuAction.CopyPath, "DiskMenuCopyPath"));
                list.Add(Entry(DiskMenuAction.CopyDetails, "DiskMenuCopyDetails"));
                break;
            default:
                list.Add(Entry(DiskMenuAction.ShowInExplorer, "DiskMenuShowContaining"));
                list.Add(DiskMenuEntry.Separator());
                list.Add(Entry(DiskMenuAction.CopyPath, "DiskMenuCopyPath"));
                list.Add(Entry(DiskMenuAction.CopyDetails, "DiskMenuCopyDetails"));
                break;
        }
        list.Add(DiskMenuEntry.Separator());
        list.Add(Entry(DiskMenuAction.ZoomOut, "DiskMenuZoomOut", canZoomOut));
        return list;
    }

    /// <summary>Runs one menu action on a node. The only side effects are changing the view, the clipboard, File Explorer showing a path (through the one reviewed opener) and a new scan.</summary>
    public async Task ExecuteAsync(DiskMenuAction action, DiskNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        switch (action)
        {
            case DiskMenuAction.ZoomIn:
                if (node.IsDirectory && node.Children.Count > 0) NavigateTo(node);
                break;
            case DiskMenuAction.ZoomOut:
                Up();
                break;
            case DiskMenuAction.ShowInExplorer:
                StatusText = opener is not null && opener.Reveal(node.FullPath) ? "" : text["DiskStatusRevealFailed"];
                break;
            case DiskMenuAction.CopyPath:
                StatusText = clipboard is not null && clipboard.TrySetText(node.FullPath) ? text["DiskStatusCopied"] : text["DiskStatusCopyFailed"];
                break;
            case DiskMenuAction.CopyDetails:
                StatusText = clipboard is not null && clipboard.TrySetText(Describe(node) + "\n" + node.FullPath) ? text["DiskStatusCopied"] : text["DiskStatusCopyFailed"];
                break;
            case DiskMenuAction.ScanFolder:
                if (node.IsDirectory && !IsScanning) await ScanFolderAsync(node.FullPath).ConfigureAwait(true);
                break;
        }
    }

    private Task ScanFolderAsync(string path)
    {
        var existing = Locations.FirstOrDefault(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) { existing = new ScanLocation(path, path); Locations.Add(existing); }
        SelectedLocation = existing;
        return ScanSelectedAsync();
    }

    // ---- sorting ----

    /// <summary>Sorts the list by the key. A second request for the same key reverses the order; a new key starts largest first (size, files) or A to Z (name).</summary>
    public void SortBy(DiskSortKey key)
    {
        if (key == sortKey) sortDescending = !sortDescending;
        else { sortKey = key; sortDescending = key != DiskSortKey.Name; }
        RebuildItems(); RebuildSortHeaders();
    }

    public DiskSortKey SortKey => sortKey;
    public bool SortDescending => sortDescending;

    private void RebuildSortHeaders()
    {
        SortHeaders.Clear();
        foreach (var (key, label) in new[] { (DiskSortKey.Name, text["DiskSortName"]), (DiskSortKey.Size, text["DiskSortSize"]), (DiskSortKey.Files, text["DiskSortFiles"]) })
        {
            var active = key == sortKey;
            var automation = text.Format("DiskSortAutomation", label, text[active ? sortDescending ? "DiskSortDesc" : "DiskSortAsc" : "DiskSortNone"]);
            SortHeaders.Add(new(key, label, automation, active, sortDescending, SortCommand));
        }
    }

    // ---- scanning ----

    public void Refresh()
    {
        var found = locations();
        Locations.Clear();
        foreach (var l in found) Locations.Add(l);
        SelectedLocation ??= Locations.FirstOrDefault();
        BuildDiskCards();
    }

    private void BuildDiskCards()
    {
        Disks.Clear();
        IReadOnlyList<DriveEntry>? entries = null;
        try { entries = drives?.Invoke(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        foreach (var d in (entries ?? []).OrderByDescending(x => x.IsSystem).ThenBy(x => x.Letter, StringComparer.OrdinalIgnoreCase))
        {
            var root = d.Letter.TrimEnd('\\') + "\\";
            var location = Locations.FirstOrDefault(l => string.Equals(l.Path, root, StringComparison.OrdinalIgnoreCase)) ?? new ScanLocation(root, d.Letter);
            Disks.Add(new(DriveRowFactory.Build(d, text), location, Choose, name => text.Format("DiskCardSelectedName", name)) { IsSelected = ReferenceEquals(SelectedLocation, location) });
        }
        OnPropertyChanged(nameof(HasDisks));
    }

    private void Choose(DiskCard card)
    {
        if (IsScanning) return;
        if (!Locations.Contains(card.Location)) Locations.Add(card.Location);
        SelectedLocation = card.Location;
        foreach (var d in Disks) d.IsSelected = ReferenceEquals(d, card);
        _ = ScanSelectedAsync();
    }

    private static IReadOnlyList<ScanLocation> DefaultLocations()
    {
        var list = new List<ScanLocation>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                list.Add(new(d.RootDirectory.FullName, d.Name + (string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : " " + d.VolumeLabel)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) list.Add(new(profile, profile));
        return list;
    }

    private async Task ChooseFolderAsync()
    {
        var folder = await picker.PickFolderAsync(text["DiskChooseFolderTitle"], CancellationToken.None);
        if (string.IsNullOrWhiteSpace(folder)) return;
        var existing = Locations.FirstOrDefault(l => string.Equals(l.Path, folder, StringComparison.OrdinalIgnoreCase));
        if (existing is null) { existing = new ScanLocation(folder, folder); Locations.Add(existing); }
        SelectedLocation = existing;
        await ScanSelectedAsync();
    }

    private Task ScanSelectedAsync()
    {
        if (SelectedLocation is not { } location || IsScanning) return Task.CompletedTask;
        scanCts?.Dispose();
        scanCts = new CancellationTokenSource();
        var token = scanCts.Token;
        IsScanning = true; ErrorText = null; ProgressText = text["DiskScanning"];
        var progress = new Progress<DiskScanProgress>(p => { if (IsScanning) ProgressText = text.Format("DiskProgress", text.Number(p.Files), SizeFormat.Bytes(text, p.Bytes), p.CurrentFolder); });
        return scanTask = RunScanAsync(location, progress, token, Runs.Begin(text["ACRunDiskScan"], false, CancelScan));
    }

    /// <summary>Where the scan registers its run; the Cleanup page sets it.</summary>
    public RunSource Runs { get; set; } = new("Cleanup");

    private async Task RunScanAsync(ScanLocation location, IProgress<DiskScanProgress> progress, CancellationToken token, ActivityRun run)
    {
        using var _ = run;
        try
        {
            var scanned = await scanner.ScanAsync(location.Path, new DiskScanOptions(), progress, token).ConfigureAwait(true);
            result = scanned;
            legendTotals.Clear();
            SelectedNode = null;
            Current = scanned.Root;
            RebuildItems(); RebuildBreadcrumbs();
            ProgressText = "";
            OnPropertyChanged(nameof(Result)); OnPropertyChanged(nameof(Root)); OnPropertyChanged(nameof(ResultSummary)); OnPropertyChanged(nameof(HasInaccessible)); OnPropertyChanged(nameof(InaccessibleText));
            OnPropertyChanged(nameof(HasSkipped)); OnPropertyChanged(nameof(SkippedText)); OnPropertyChanged(nameof(ShowResult)); OnPropertyChanged(nameof(ShowEmptyState)); OnPropertyChanged(nameof(ShowScanSkeleton)); OnPropertyChanged(nameof(ShowScanRing));
            run.Finish(RunResult.Ok, quiet: true);
        }
        catch (OperationCanceledException) { ProgressText = text["DiskScanCancelled"]; run.Finish(RunResult.Stopped); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ErrorText = e is DirectoryNotFoundException ? text["DiskFolderMissing"] : e is UnauthorizedAccessException ? text["DiskAccessDenied"] : text["DiskScanFailed"];
            ProgressText = "";
            run.Finish(RunResult.Error, ErrorText);
        }
        finally { IsScanning = false; }
    }

    private void CancelScan() => scanCts?.Cancel();

    /// <summary>Completes when no scan is running. Tests and the shell wait on it.</summary>
    public Task WhenIdleAsync() => scanTask;

    /// <summary>Stops a running scan. Called when the user leaves the page.</summary>
    public void Stop() => scanCts?.Cancel();

    public void Dispose() { scanCts?.Cancel(); scanCts?.Dispose(); }

    // ---- navigation ----

    public void NavigateTo(DiskNode node)
    {
        if (Root is null || !node.IsDirectory || !IsInTree(node)) return;
        SelectedNode = null;
        Current = node;
        RebuildItems(); RebuildBreadcrumbs();
    }

    /// <summary>Double click or Enter on a map cell: a folder opens, a file opens the folder it is in.</summary>
    public void Invoke(DiskNode node)
    {
        var target = node.IsDirectory && node.Children.Count > 0 ? node : node.Parent;
        if (target is not null && target != Current) { var keep = node; NavigateTo(target); if (!keep.IsDirectory) SelectedNode = keep; }
    }

    private DiskNode? OpenTarget()
    {
        var row = SelectedRow;
        return row is { CanOpen: true } ? row.Node : null;
    }

    private void OpenSelected() { if (OpenTarget() is { } node) NavigateTo(node); }
    private void Up() { if (Current?.Parent is { } parent) NavigateTo(parent); }

    private bool IsInTree(DiskNode node)
    {
        for (var n = node; n is not null; n = n.Parent) if (n == Root) return true;
        return false;
    }

    private static bool Contains(DiskNode ancestor, DiskNode? node)
    {
        for (var n = node; n is not null; n = n.Parent) if (n == ancestor) return true;
        return false;
    }

    /// <summary>
    /// The list shows at most this many rows in the chosen order; the map still shows everything. A folder like WinSxS has tens of thousands of children, and a list
    /// that big made the page stop for seconds when a screen reader or another UI Automation client was running (it reads every row).
    /// </summary>
    public const int MaxListRows = 1000;
    public bool IsListCapped => Current is { } folder && folder.Children.Count > MaxListRows;
    public string ListCapText => Current is { } folder && folder.Children.Count > MaxListRows ? text.Format("DiskListCapped", text.Number(MaxListRows), text.Number(folder.Children.Count)) : "";

    private void RebuildItems()
    {
        var rows = new List<DiskItemRow>();
        if (Current is null) { SetItems(rows); return; }
        var total = Current.Size;
        IEnumerable<DiskNode> ordered = sortKey switch
        {
            DiskSortKey.Name => sortDescending ? Current.Children.OrderByDescending(c => Display(c), StringComparer.CurrentCultureIgnoreCase) : Current.Children.OrderBy(c => Display(c), StringComparer.CurrentCultureIgnoreCase),
            DiskSortKey.Files => sortDescending ? Current.Children.OrderByDescending(c => c.FileCount).ThenByDescending(c => c.Size) : Current.Children.OrderBy(c => c.FileCount).ThenBy(c => c.Size),
            _ => sortDescending ? Current.Children : Current.Children.Reverse(),
        };
        foreach (var child in ordered.Take(MaxListRows))
        {
            var percent = total > 0 ? child.Size * 100.0 / total : 0;
            var kind = DiskFileKinds.Classify(child);
            rows.Add(new(child, Display(child), percent, kind, text));
        }
        SetItems(rows);
    }

    private void SetItems(List<DiskItemRow> rows)
    {
        Items = new ObservableCollection<DiskItemRow>(rows);
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(IsListCapped));
        OnPropertyChanged(nameof(ListCapText));
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (Current is null) return;
        var chain = new List<DiskNode>();
        for (var n = Current; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        foreach (var node in chain) Breadcrumbs.Add(new(node, node.Name, NavigateCommand));
    }
}
