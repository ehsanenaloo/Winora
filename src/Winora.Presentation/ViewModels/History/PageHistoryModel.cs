// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Winora.Desktop;

/// <summary>
/// The "Change history" block of a page. It holds the rows of the page's own journal as rows of the History timeline (same row type, so the same icon, chip, time and undo button), the count for
/// the heading, and the link that opens the History page for the area of the page. Undo runs through the page's own command, so the page keeps its review and its service.
/// The block starts closed: its header shows the count and the newest change. Open, it shows one page of <see cref="VisibleRows"/> rows with Newer and Older buttons, so a long
/// history never makes the page long.
/// </summary>
public sealed class PageHistoryModel(LocalizationService text, HistoryArea area, Func<Action<HistoryArea>?> openHistory, HistoryBatchKind kind = HistoryBatchKind.None) : ObservableObject
{
    public const int VisibleRows = 5;
    private HistoryRowViewModel? selected;
    private int total, page;
    private bool expanded;
    private IReadOnlyList<HistoryRowViewModel> all = [];

    public LocalizationService Text => text;
    public HistoryArea Area => area;
    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];
    public bool GroupByDay => false;
    public HistoryRowViewModel? Selected { get => selected; set => SetProperty(ref selected, value); }
    /// <summary>The row button of the timeline: runs the command of the page that owns the row.</summary>
    public RelayCommand<HistoryRowViewModel> UndoRowCommand => undoRow ??= new(r => { if (r?.PageUndo is { } u && u.CanExecute(r)) u.Execute(r); }, r => r?.PageUndo?.CanExecute(r) ?? false);
    private RelayCommand<HistoryRowViewModel>? undoRow;
    // The page commands are as long-lived as this block, so each gets one handler for good; rows come and go without adding handlers.
    private readonly HashSet<System.Windows.Input.ICommand> watched = [];
    public RelayCommand<HistoryRowViewModel> ToggleExpandCommand { get; } = new(r => { if (r is not null) r.IsExpanded = !r.IsExpanded; });
    public int Total { get => total; private set { if (SetProperty(ref total, value)) { OnPropertyChanged(nameof(Heading)); OnPropertyChanged(nameof(HasRows)); } } }
    /// <summary>The block folds on every page; the Overview's Recent changes tab, which shows nothing else, uses another model that never folds.</summary>
    public bool CanCollapse => true;
    public bool ShowPlainHeading => false;
    public bool IsExpanded { get => expanded; set { if (SetProperty(ref expanded, value)) OnPropertyChanged(nameof(ShowBody)); } }
    public bool ShowBody => expanded;
    public string ToggleHelp => text["PageHistoryToggleHelp"];
    /// <summary>The newest change, shown in the closed header.</summary>
    public string LatestText => all.Count == 0 ? "" : text.Format("PageHistoryLatest", all[0].Title, all[0].TimeText);
    public int PageIndex => page;
    public int PageCount => Math.Max(1, (all.Count + VisibleRows - 1) / VisibleRows);
    public bool HasPages => PageCount > 1;
    public string PageText => text.Format("PageHistoryPage", page + 1, PageCount);
    public RelayCommand NewerCommand => newer ??= new(() => ShowPage(page - 1), () => page > 0);
    public RelayCommand OlderCommand => older ??= new(() => ShowPage(page + 1), () => page < PageCount - 1);
    private RelayCommand? newer, older;
    public string NewerText => text["PageHistoryNewer"];
    public string OlderText => text["PageHistoryOlder"];
    public string NewerHelp => text["PageHistoryNewerHelp"];
    public string OlderHelp => text["PageHistoryOlderHelp"];
    public bool HasRows => total > 0;
    public string Heading => text.Format("PageHistoryHeading", total);
    /// <summary>An optional line under the heading (Preinstalled apps explains that a removal has no undo).</summary>
    public Func<string?>? NoteSource { get; set; }
    public string? Note => NoteSource?.Invoke();
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public string ShowAllText => text["PageHistoryShowAll"];
    public string ShowAllHelp => text.Format("PageHistoryShowAllHelp", text.Enum(area));
    public string ShowAllName => text.Format("PageHistoryShowAllName", text.Enum(area));
    public RelayCommand ShowAllCommand => showAll ??= new(() => openHistory()?.Invoke(area));
    private RelayCommand? showAll;

    /// <summary>Replaces the rows, newest first, and shows the first page (a new change is on it). The heading counts all of them.</summary>
    public void Set(IReadOnlyList<HistoryRowViewModel> rows)
    {
        all = [.. rows];
        foreach (var undo in all.Select(r => r.PageUndo).OfType<PageUndo>().Select(u => u.Inner).Distinct())
            if (watched.Add(undo)) undo.CanExecuteChanged += OnPageUndoChanged;
        Total = all.Count;
        ShowPage(0);
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(LatestText));
    }

    /// <summary>Shows one page of rows (page 0 holds the newest).</summary>
    public void ShowPage(int index)
    {
        page = Math.Clamp(index, 0, PageCount - 1);
        Rows.Clear();
        foreach (var row in all.Skip(page * VisibleRows).Take(VisibleRows)) Rows.Add(row);
        Selected = null;
        foreach (var name in new[] { nameof(PageIndex), nameof(PageCount), nameof(HasPages), nameof(PageText) }) OnPropertyChanged(name);
        NewerCommand.NotifyCanExecuteChanged();
        OlderCommand.NotifyCanExecuteChanged();
    }

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in all) row.Refresh();
    }

    /// <summary>One row for a record of a page's journal. <paramref name="undo"/> is the page's own command; it gets <paramref name="parameter"/> when the button is pressed.</summary>
    public HistoryRowViewModel Row(Guid id, DateTimeOffset at, HistoryResult result, string title, bool canUndo, System.Windows.Input.ICommand? undo, object? parameter, HistoryRowOptions? options = null)
    {
        var entry = new HistoryEntry($"{area}:{id:N}", area, id, at, result, title, "", "", false, false, canUndo, null, BatchKind: kind);
        return new(new(entry, []), text, null, undo is null ? null : new PageUndo(undo, parameter), (options ?? new()) with { WithDay = true });
    }

    private void OnPageUndoChanged(object? sender, EventArgs e)
    {
        foreach (var row in all) (row.PageUndo as PageUndo)?.Raise();
        UndoRowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The undo of one row: the page command with the page's own row. It runs only while the page command allows it (the page is not busy).</summary>
    private sealed class PageUndo(System.Windows.Input.ICommand inner, object? parameter) : System.Windows.Input.ICommand
    {
        public System.Windows.Input.ICommand Inner => inner;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? _) => inner.CanExecute(parameter);
        public void Execute(object? _) { if (inner.CanExecute(parameter)) inner.Execute(parameter); }
        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
