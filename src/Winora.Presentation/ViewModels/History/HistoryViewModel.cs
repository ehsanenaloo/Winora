// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application.Activity;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>The result filter of the page: one chip each. "Not applied" covers entries that failed.</summary>
public enum HistoryChipKind { All, Applied, Undone, NotApplied }

public sealed class HistoryChip(HistoryChipKind kind, Func<string> label) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Never raised. It lets WPF bindings listen through a weak event; on a plain object they hold the object, and with it the page, for the life of the process.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public HistoryChipKind Kind { get; } = kind;
    public string Label => label();
    public string Key => "V2HiChip" + Kind;
    public string AutomationName => Label;
    public override string ToString() => Label;
}

public sealed class HistoryFilterOption<T>(T value, Func<string> label)
{
    public T Value { get; } = value;
    public string Label => label();
    public override string ToString() => Label;
}

/// <summary>One member of a batch, shown under its batch row.</summary>
public sealed class HistoryMemberRow(HistoryEntry entry, LocalizationService text, AsyncRelayCommand<HistoryMemberRow> undo)
{
    public HistoryEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public string ResultLabel => text.Enum(Entry.Result);
    public Tone Tone => HistoryRowViewModel.ToneOf(Entry.Result);
    public string TimeText => text.When(Entry.AtUtc.ToLocalTime(), "t");
    public bool CanUndo => Entry.CanUndo;
    public string? ReasonText => Entry.UndoReason is { } key && text.Has(key) ? text[key] : null;
    public bool HasReason => ReasonText is not null;
    public AsyncRelayCommand<HistoryMemberRow> UndoCommand { get; } = undo;
    public string UndoLabel => text["HistUndoButton"];
    public string UndoName => text.Format("HistUndoName", Entry.Title);
    public string AutomationName => $"{TimeText}, {ResultLabel}, {Title}" + (CanUndo ? ", " + text["HistCanUndo"] : "");
}

/// <summary>What a page of its own can change on a row of the shared timeline: its state word (for example "Interrupted"), its undo button text and help, a detail line, the result code, and a time that names the day.</summary>
public sealed record HistoryRowOptions(string? UndoLabel = null, string? UndoHelp = null, string? Detail = null, bool WithDay = false);

/// <summary>One row of the timeline. The automation name carries the day, the result, the change and whether it can be undone.</summary>
public sealed class HistoryRowViewModel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly HistoryRowOptions options;
    private bool expanded;

    public HistoryRowViewModel(TimelineItem item, LocalizationService text, AsyncRelayCommand<HistoryMemberRow>? undoMember, System.Windows.Input.ICommand? pageUndo = null, HistoryRowOptions? options = null)
    {
        Item = item; this.text = text; this.options = options ?? new();
        PageUndo = pageUndo;
        undoMember ??= new(_ => Task.CompletedTask);
        Members = [.. item.Members.Select(m => new HistoryMemberRow(m, text, undoMember))];
    }

    /// <summary>The undo of a row that belongs to a page's own journal (null on the History page, which undoes through its own command).</summary>
    public System.Windows.Input.ICommand? PageUndo { get; }
    public TimelineItem Item { get; }
    public HistoryEntry Entry => Item.Entry;
    public string Key => Entry.Key;
    public bool IsBatch => Item.IsBatch;
    public IReadOnlyList<HistoryMemberRow> Members { get; }
    public bool IsExpanded { get => expanded; set { if (SetProperty(ref expanded, value)) { OnPropertyChanged(nameof(ExpandSymbol)); OnPropertyChanged(nameof(ExpandName)); OnPropertyChanged(nameof(ShowMembers)); OnPropertyChanged(nameof(ShowDetails)); OnPropertyChanged(nameof(DetailsLabel)); OnPropertyChanged(nameof(DetailsName)); } } }
    public bool ShowMembers => IsBatch && expanded;
    public IconKey ExpandSymbol => expanded ? IconKey.ChevronDown16 : text.Direction == TextDirection.RightToLeft ? IconKey.ChevronLeft16 : IconKey.ChevronRight16;
    public string ExpandName => text.Format(expanded ? "HistCollapseName" : "HistExpandName", Entry.Title);

    public string Day
    {
        get
        {
            var date = Entry.AtUtc.ToLocalTime().Date;
            var today = DateTime.Now.Date;
            return date == today ? text["HistToday"] : date == today.AddDays(-1) ? text["HistYesterday"] : text.When(date, "D");
        }
    }

    public string Title => Entry.Title;
    public string TimeText
    {
        get
        {
            var local = Entry.AtUtc.ToLocalTime();
            var time = text.When(local, "t");
            if (!options.WithDay) return time;
            var today = DateTime.Now.Date;
            var day = local.Date == today ? text["HistToday"] : local.Date == today.AddDays(-1) ? text["HistYesterday"] : text.When(local, "d");
            return text.Format("HistDayTime", day, time);
        }
    }
    public string When => text.When(Entry.AtUtc.ToLocalTime(), "g");
    public string ResultLabel => text.Enum(Entry.Result);
    // Windows Update modes are kept in the Optimize journal; their rows name the Windows Update page.
    public string AreaLabel => Entry.BatchKind == HistoryBatchKind.Updates ? text["Updates"] : text.Enum(Entry.Area);
    public Tone Tone => ToneOf(Entry.Result);
    public IconKey ResultSymbol => Entry.Result switch
    {
        HistoryResult.Applied or HistoryResult.Removed => IconKey.CheckmarkCircle20,
        HistoryResult.Undone or HistoryResult.Restored => IconKey.ArrowUndo20,
        HistoryResult.Cancelled or HistoryResult.Dismissed => IconKey.Prohibited20,
        HistoryResult.ChangedSince or HistoryResult.Unconfirmed => IconKey.Warning20,
        _ => IconKey.DismissCircle20,
    };
    public bool RestartNeeded => Entry.RestartNeeded;
    public string RestartText => text["HistRestartPending"];
    public string Outcome => Entry.Outcome;
    public bool HasOutcome => Entry.Outcome.Length > 0;
    public bool CanUndo => Entry.CanUndo;
    public string? ReasonText => options.Detail ?? (Entry.UndoReason is { } key && text.Has(key) ? text[key] : null);
    public bool HasReason => ReasonText is not null;
    public string MembersText => text.Format(Members.Count == 1 ? "HistMembersOne" : "HistMembers", Members.Count);
    public string Id => Entry.Id.ToString();
    public string ShortId => Entry.Id.ToString("N")[..8];
    public string Code => Entry.Code;
    public string UndoLabel => options.UndoLabel ?? text[IsBatch ? "HistUndoAllButton" : "HistUndoButton"];
    public string UndoName => text.Format("HistUndoName", Entry.Title);
    /// <summary>The Undo button is shown for entries that can be undone and for entries whose value was changed by another program (then it is disabled and the reason is shown).</summary>
    public bool ShowUndo => CanUndo || Entry.Result == HistoryResult.ChangedSince;
    /// <summary>An unconfirmed record (begun, never finished) offers two ways to close it: keep the current value, or put the value from before back.</summary>
    public bool ShowDismiss => Entry.CanDismiss && !IsBatch;
    public bool ShowRestoreBefore => Entry.CanRestoreBefore && !IsBatch;
    public string DismissLabel => text["HistDismissButton"];
    public string DismissName => text.Format("HistDismissName", Entry.Title);
    public string DismissHelp => text["HistDismissHelp"];
    public string RestoreBeforeLabel => text["HistRestoreBeforeButton"];
    public string RestoreBeforeName => text.Format("HistRestoreBeforeName", Entry.Title);
    public string RestoreBeforeHelp => text["HistRestoreBeforeHelp"];
    public string UndoHelp => options.UndoHelp ?? (CanUndo || ReasonText is null ? text["HistUndoHelp"] : ReasonText);
    public bool ShowDetails => expanded && !IsBatch;
    public string DetailsLabel => text[expanded ? "V2HiHide" : "V2HiDetails"];
    public string DetailsName => text.Format("HistDetailsName", Entry.Title);
    public string CountText => text.Format(Members.Count == 1 ? "HistMembersOne" : "HistMembers", Members.Count);
    public string AutomationName => $"{(options.WithDay ? "" : Day + " ")}{TimeText}, {ResultLabel}, {Title}" + (RestartNeeded ? ", " + RestartText : "") + (IsBatch ? ", " + MembersText : "") + (CanUndo ? ", " + text["HistCanUndo"] : "");

    public static Tone ToneOf(HistoryResult result) => result switch
    {
        HistoryResult.Applied or HistoryResult.Removed or HistoryResult.Restored => Tone.Success,
        HistoryResult.Undone or HistoryResult.Dismissed => Tone.Info,
        HistoryResult.ChangedSince or HistoryResult.Cancelled or HistoryResult.Unconfirmed => Tone.Caution,
        _ => Tone.Danger,
    };

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The History page: every journal in one timeline, newest first, with profile runs and Windows Update modes folded into one row. It reads the journals when the page opens and after a change; it never
/// polls. Undo asks once through the review prompt, then runs the service of the area, which compares first and refuses when the state changed since Winora set it.
/// </summary>
public sealed class HistoryViewModel : PageViewModel
{
    /// <summary>Rows on one page of the timeline.</summary>
    public const int PageSize = 25;
    private readonly HistoryFeed feed;
    private readonly IReviewPrompt prompt;
    private readonly ErrorPresenter errors;
    private readonly SettingsModel settings;
    private HistorySnapshot snapshot = HistorySnapshot.Empty;
    private HistoryRowViewModel? selected;
    private bool loaded, busy, refreshing, dirty;
    private Task loadTask = Task.CompletedTask;
    private HistoryFilterOption<HistoryArea?> area;
    private HistoryFilterOption<HistoryResult?> result;
    private HistoryFilterOption<int?> date;
    private string? notice;
    private BannerSeverity noticeSeverity = BannerSeverity.Informational;
    private int page, matchCount;
    private List<TimelineItem> matches = [];
    private HistoryChip chip;

    public HistoryViewModel(LocalizationService text, SimulationModel model, SettingsModel settings, HistoryFeed feed, IReviewPrompt prompt, ErrorPresenter errors) : base("History", text, model)
    {
        this.feed = feed; this.prompt = prompt; this.errors = errors; this.settings = settings;
        UndoMemberCommand = new(m => m is null ? Task.CompletedTask : UndoAsync(FindItem(m.Entry.Key), m.Entry), m => m is not null && m.CanUndo && !IsBusy);
        UndoCommand = new(() => UndoAsync(selected?.Item, null), () => selected is { CanUndo: true } && !IsBusy);
        UndoRowCommand = new(r => r is null ? Task.CompletedTask : UndoAsync(r.Item, null), r => r is { CanUndo: true } && !IsBusy);
        DismissRowCommand = new(r => r is null ? Task.CompletedTask : ResolveAsync(r.Entry, restore: false), r => r is { ShowDismiss: true } && !IsBusy);
        RestoreBeforeRowCommand = new(r => r is null ? Task.CompletedTask : ResolveAsync(r.Entry, restore: true), r => r is { ShowRestoreBefore: true } && !IsBusy);
        Chips = [new(HistoryChipKind.All, () => Text["HistAll"]), new(HistoryChipKind.Applied, () => Text["V2HiChipApplied"]), new(HistoryChipKind.Undone, () => Text["V2HiChipUndone"]), new(HistoryChipKind.NotApplied, () => Text["V2HiChipNotApplied"])];
        chip = Chips[0];
        UndoNewestCommand = new(UndoNewestAsync, () => !IsBusy);
        RefreshHistoryCommand = new(() => RefreshAsync(), () => !IsBusy);
        ClearFiltersCommand = new(ClearFilters, () => FiltersActive);
        ToggleExpandCommand = new(r => { if (r is not null) r.IsExpanded = !r.IsExpanded; });
        DismissNoticeCommand = new(() => Notice = null);
        NewerCommand = new(() => ShowPage(page - 1), () => page > 0);
        OlderCommand = new(() => ShowPage(page + 1), () => page < PageCount - 1);
        AreaOptions = [new(null, () => Text["HistAll"]), .. Enum.GetValues<HistoryArea>().Where(a => a != HistoryArea.Simulation || SimulationAvailable).Select(a => new HistoryFilterOption<HistoryArea?>(a, () => Text.Enum(a)))];
        ResultOptions = [new(null, () => Text["HistAll"]), .. Enum.GetValues<HistoryResult>().Select(r => new HistoryFilterOption<HistoryResult?>(r, () => Text.Enum(r)))];
        DateOptions = [new(null, () => Text["HistDateAny"]), new(1, () => Text["HistDateToday"]), new(7, () => Text["HistDate7"]), new(30, () => Text["HistDate30"])];
        area = AreaOptions[0]; result = ResultOptions[0]; date = DateOptions[0];
        settings.Changed += (_, _) => { if (loaded) RequestRefresh(); };
    }

    public static bool SimulationAvailable =>
#if DEBUG
        true;
#else
        false;
#endif

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];
    /// <summary>The timeline groups the rows under a heading for each day.</summary>
    public bool GroupByDay => true;
    public IReadOnlyList<HistoryFilterOption<HistoryArea?>> AreaOptions { get; }
    public IReadOnlyList<HistoryFilterOption<HistoryResult?>> ResultOptions { get; }
    public IReadOnlyList<HistoryFilterOption<int?>> DateOptions { get; }
    public AsyncRelayCommand<HistoryMemberRow> UndoMemberCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public AsyncRelayCommand<HistoryRowViewModel> UndoRowCommand { get; }
    public AsyncRelayCommand<HistoryRowViewModel> DismissRowCommand { get; }
    public AsyncRelayCommand<HistoryRowViewModel> RestoreBeforeRowCommand { get; }
    public IReadOnlyList<HistoryChip> Chips { get; }
    public HistoryChip SelectedChip { get => chip; set { if (SetProperty(ref chip, value ?? Chips[0])) Apply(0); } }
    public AsyncRelayCommand UndoNewestCommand { get; }
    public AsyncRelayCommand RefreshHistoryCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand<HistoryRowViewModel> ToggleExpandCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }
    public RelayCommand NewerCommand { get; }
    public RelayCommand OlderCommand { get; }
    public int PageIndex => page;
    public int PageCount => Math.Max(1, (matchCount + PageSize - 1) / PageSize);
    public bool HasPages => PageCount > 1;
    public string PageText => Text.Format("PageHistoryPage", page + 1, PageCount);
    public string NewerText => Text["PageHistoryNewer"];
    public string OlderText => Text["PageHistoryOlder"];
    public string NewerHelp => Text["PageHistoryNewerHelp"];
    public string OlderHelp => Text["PageHistoryOlderHelp"];

    public SettingsModel Settings => settings;
    public HistorySnapshot Snapshot => snapshot;
    public event EventHandler? SnapshotChanged;
    public bool IsLoaded => loaded;
    public override System.Windows.Input.ICommand? ShortcutRefresh() => RefreshHistoryCommand;
    public override bool IsBusy => busy || refreshing || Model.IsBusy || settings.IsBusy;

    public HistoryFilterOption<HistoryArea?> SelectedArea { get => area; set { if (SetProperty(ref area, value ?? AreaOptions[0])) Apply(0); } }
    public HistoryFilterOption<HistoryResult?> SelectedResult { get => result; set { if (SetProperty(ref result, value ?? ResultOptions[0])) Apply(0); } }
    public HistoryFilterOption<int?> SelectedDate { get => date; set { if (SetProperty(ref date, value ?? DateOptions[0])) Apply(0); } }
    public bool FiltersActive => area.Value is not null || result.Value is not null || date.Value is not null || chip.Kind != HistoryChipKind.All || search.Trim().Length > 0;
    private string search = "";
    /// <summary>Text to find in the title of a change (or of a member of a batch).</summary>
    public string SearchText { get => search; set { if (SetProperty(ref search, value ?? "")) Apply(0); } }
    /// <summary>The Filters button: "Filters" or "Filters (n)" for the area and date filters that are set.</summary>
    public string FiltersText { get { var n = (area.Value is not null ? 1 : 0) + (date.Value is not null ? 1 : 0); return n == 0 ? Text["HistFilters"] : Text.Format("DiscFiltersActive", n); } }

    public HistoryRowViewModel? Selected
    {
        get => selected;
        set
        {
            if (!SetProperty(ref selected, value)) return;
            OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(HasNoSelection));
            UndoCommand.NotifyCanExecuteChanged();
        }
    }
    public bool HasSelection => selected is not null;
    public bool HasNoSelection => selected is null && Rows.Count > 0;
    public bool IsEmpty => loaded && snapshot.Items.Count == 0;
    public bool HasItems => snapshot.Items.Count > 0;
    public bool ShowList => Rows.Count > 0;
    public bool NoMatches => loaded && snapshot.Items.Count > 0 && Rows.Count == 0;

    public string SummaryText => snapshot.Counts.ChangedSince > 0
        ? Text.Format("HistSummaryChanged", snapshot.Counts.Total, snapshot.Counts.Applied, snapshot.Counts.Undone, snapshot.Counts.Failed, snapshot.Counts.ChangedSince)
        : Text.Format("HistSummary", snapshot.Counts.Total, snapshot.Counts.Applied, snapshot.Counts.Undone, snapshot.Counts.Failed);
    /// <summary>"1 to 25 of 262 changes": the rows of the page among all rows that pass the filters (a batch is one row).</summary>
    public string ShowingText => matchCount == 1 ? Text["HistRangeOne"] : Text.Format("HistRange", page * PageSize + 1, Math.Min(matchCount, (page + 1) * PageSize), matchCount);
    public bool ShowCount => matchCount > 0;
    public string UnreadableText => snapshot.Unreadable.Count == 0 ? "" : Text.Format("HistUnreadable", string.Join(", ", snapshot.Unreadable.Select(a => Text.Enum(a))));
    public bool HasUnreadable => snapshot.Unreadable.Count > 0;
    public string RevertHelp => selected is { CanUndo: true } ? Text["HistUndoHelp"] : selected?.ReasonText ?? Text["HistUndoHelp"];

    public string? Notice { get => notice; private set { if (SetProperty(ref notice, value)) { OnPropertyChanged(nameof(HasNotice)); OnPropertyChanged(nameof(NoticeOpen)); } } }
    public bool HasNotice => notice is not null;
    /// <summary>Two-way for the banner: closing it clears the notice.</summary>
    public bool NoticeOpen { get => notice is not null; set { if (!value) Notice = null; } }
    public BannerSeverity NoticeSeverity { get => noticeSeverity; private set => SetProperty(ref noticeSeverity, value); }

    public override void Activate() => loadTask = RefreshAsync();
    public override Task WhenIdleAsync() => loadTask;

    /// <summary>Reads the journals if that did not happen yet (the Overview page uses the same list).</summary>
    public Task EnsureLoadedAsync() => loaded ? Task.CompletedTask : (refreshing ? loadTask : loadTask = RefreshAsync());

    public void RequestRefresh()
    {
        if (refreshing) { dirty = true; return; }
        loadTask = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (refreshing) { dirty = true; await loadTask.ConfigureAwait(true); return; }
        refreshing = true; OnPropertyChanged(nameof(IsBusy)); NotifyCommands();
        try
        {
            do
            {
                dirty = false;
                var read = await Task.Run(feed.Read).ConfigureAwait(true);
                snapshot = read; loaded = true;
                Apply(page);
                SnapshotChanged?.Invoke(this, EventArgs.Empty);
            }
            while (dirty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Report(ex, "History read");
            loaded = true;
            Notice = Text["HistReadFailed"]; NoticeSeverity = BannerSeverity.Warning;
        }
        finally { refreshing = false; OnPropertyChanged(nameof(IsBusy)); NotifyCommands(); }
    }

    private void NotifyCommands()
    {
        UndoCommand.NotifyCanExecuteChanged(); UndoNewestCommand.NotifyCanExecuteChanged(); RefreshHistoryCommand.NotifyCanExecuteChanged(); UndoMemberCommand.NotifyCanExecuteChanged(); UndoRowCommand.NotifyCanExecuteChanged(); DismissRowCommand.NotifyCanExecuteChanged(); RestoreBeforeRowCommand.NotifyCanExecuteChanged();
        foreach (var row in Rows) foreach (var m in row.Members) m.UndoCommand.NotifyCanExecuteChanged();
    }

    private bool Passes(TimelineItem item)
    {
        bool InArea(HistoryEntry e) => area.Value is not { } a || e.Area == a;
        bool HasResult(HistoryEntry e) => result.Value is not { } r || e.Result == r;
        if (area.Value is not null && !InArea(item.Entry) && !item.Members.Any(InArea)) return false;
        if (result.Value is not null && !HasResult(item.Entry) && !item.Members.Any(HasResult)) return false;
        bool InChip(HistoryEntry e) => chip.Kind switch
        {
            HistoryChipKind.Applied => e.Result.IsSuccess() || e.Result == HistoryResult.ChangedSince,
            HistoryChipKind.Undone => e.Result == HistoryResult.Undone,
            HistoryChipKind.NotApplied => e.Result.IsNotApplied(),
            _ => true,
        };
        if (chip.Kind != HistoryChipKind.All && !InChip(item.Entry) && !item.Members.Any(InChip)) return false;
        if (date.Value is { } days && item.AtUtc.ToLocalTime().Date < DateTime.Now.Date.AddDays(1 - days)) return false;
        var query = search.Trim();
        bool Has(string title) => Text.Culture.CompareInfo.IndexOf(title, query, System.Globalization.CompareOptions.IgnoreCase) >= 0;
        if (query.Length > 0 && !Has(item.Entry.Title) && !item.Members.Any(m => Has(m.Title))) return false;
        return true;
    }

    /// <summary>Filters the journals and shows one page. A filter change passes page 0; a new read keeps the page the person is on (clamped when rows went away).</summary>
    private void Apply(int pageIndex)
    {
        matches = [.. snapshot.Items.Where(Passes)];
        matchCount = matches.Count;
        page = Math.Clamp(pageIndex, 0, PageCount - 1);
        FillPage();
    }

    private void FillPage()
    {
        var keep = selected?.Key;
        var expandedKeys = Rows.Where(r => r.IsExpanded).Select(r => r.Key).ToHashSet();
        Rows.Clear();
        foreach (var item in matches.Skip(page * PageSize).Take(PageSize)) Rows.Add(new(item, Text, UndoMemberCommand) { IsExpanded = expandedKeys.Contains(item.Entry.Key) });
        Selected = Rows.FirstOrDefault(r => r.Key == keep) ?? Rows.FirstOrDefault();
        ClearFiltersCommand.NotifyCanExecuteChanged();
        NewerCommand.NotifyCanExecuteChanged();
        OlderCommand.NotifyCanExecuteChanged();
        foreach (var name in new[] { nameof(IsEmpty), nameof(HasItems), nameof(NoMatches), nameof(ShowList), nameof(HasNoSelection), nameof(SummaryText), nameof(ShowingText), nameof(ShowCount), nameof(PageIndex), nameof(PageCount), nameof(HasPages), nameof(PageText), nameof(UnreadableText), nameof(HasUnreadable), nameof(FiltersActive), nameof(FiltersText), nameof(RevertHelp) })
            OnPropertyChanged(name);
    }

    /// <summary>Shows one page of the filtered rows (page 0 holds the newest).</summary>
    public void ShowPage(int index)
    {
        var next = Math.Clamp(index, 0, PageCount - 1);
        if (next == page) return;
        page = next;
        FillPage();
    }

    private void ClearFilters() { area = AreaOptions[0]; result = ResultOptions[0]; date = DateOptions[0]; chip = Chips[0]; search = ""; OnPropertyChanged(string.Empty); Apply(0); }

    /// <summary>Shows one area only (the "Show all in History" link of a page). Null clears the area filter.</summary>
    public void ShowArea(HistoryArea? value) => SelectedArea = AreaOptions.FirstOrDefault(o => o.Value == value) ?? AreaOptions[0];

    private TimelineItem? FindItem(string memberKey) => snapshot.Items.FirstOrDefault(i => i.Entry.Key == memberKey || i.Members.Any(m => m.Key == memberKey));

    /// <summary>Ctrl+Z: selects the newest entry that can be undone and opens its undo review. It never undoes silently.</summary>
    private async Task UndoNewestAsync()
    {
        var row = Rows.FirstOrDefault(r => r.CanUndo);
        if (row is null) { Notice = Text["HistNothingToUndo"]; NoticeSeverity = BannerSeverity.Informational; return; }
        Selected = row;
        await UndoAsync(row.Item, null).ConfigureAwait(true);
    }

    /// <summary>Undoes one entry (a member of a batch, or a single change) or a whole batch after one review. Settings of the current user ask in their own review.</summary>
    public async Task UndoAsync(TimelineItem? item, HistoryEntry? member)
    {
        if (item is null || busy) return;
        var entry = member ?? item.Entry;
        var whole = member is null && item.IsBatch;
        if (!entry.CanUndo) { Notice = entry.UndoReason is { } key && Text.Has(key) ? Text[key] : Text["HistUndoNotPossible"]; NoticeSeverity = BannerSeverity.Informational; return; }
        busy = true; OnPropertyChanged(nameof(IsBusy)); NotifyCommands();
        var wrote = false;
        var run = ActivityRun.None;
        try
        {
            if (whole || !entry.UsesOwnReview)
            {
                var confirmation = new ActionConfirmation(whole ? Text.Plural("HistUndoAllTitle", item.Members.Count(m => m.CanUndo), item.Members.Count(m => m.CanUndo)) : Text["HistUndoTitle"], Text["HistUndoBody"], [entry.Title], Text["HistUndoWarning"], Text[whole ? "HistUndoAllButton" : "HistUndoButton"])
                {
                    Changes = [new ConfirmChange(entry.Title)], Risk = Winora.Domain.RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged,
                };
                if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
            }
            run = Runs.Begin(Text.Format("ACRunUndo", entry.Title), true);
            if (!run.Started) return;
            wrote = true;
            var outcome = whole ? await feed.UndoAsync(item, CancellationToken.None).ConfigureAwait(true) : await feed.UndoAsync(entry, CancellationToken.None).ConfigureAwait(true);
            if (outcome.Cancelled) { run.Finish(RunResult.Stopped); return; }
            if (!outcome.Ok) { Notice = outcome.Message ?? Text["HistUndoFailed"]; NoticeSeverity = BannerSeverity.Warning; }
            else if (outcome.Skipped > 0) { Notice = Text.Format("HistUndoPartial", outcome.Done, outcome.Skipped); NoticeSeverity = BannerSeverity.Warning; }
            else if (whole && outcome.Done == 0) { Notice = Text["HistUndoNothingDone"]; NoticeSeverity = BannerSeverity.Informational; }
            else { Notice = Text.Format("HistUndoDone", Math.Max(1, outcome.Done)) + (outcome.RecordLost ? " " + Text["RecordLostNote"] : ""); NoticeSeverity = outcome.RecordLost ? BannerSeverity.Warning : BannerSeverity.Success; }
            run.Finish(!outcome.Ok ? RunResult.Error : outcome.Skipped > 0 ? RunResult.Warning : RunResult.Ok, Notice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Report(ex, "History undo");
            Notice = Text["HistUndoFailed"]; NoticeSeverity = BannerSeverity.Warning;
            run.Finish(RunResult.Error, Notice);
        }
        finally
        {
            run.Dispose();
            busy = false; OnPropertyChanged(nameof(IsBusy));
            // The pages that read once read again when they open next; the settings model reads again now (a profile batch has user settings in it).
            if (wrote) Journal.Wrote(whole && item.Entry.BatchKind == HistoryBatchKind.Profile ? HistoryArea.Profiles : entry.Area);
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Closes an unconfirmed record after one question. Keeping the current value writes nothing. Putting the value from before back is an ordinary change with its own record and undo; it
    /// is written only while the value is still the one Winora read. Neither choice makes the record Winora's change.
    /// </summary>
    public async Task ResolveAsync(HistoryEntry entry, bool restore)
    {
        if (busy || !(restore ? entry.CanRestoreBefore : entry.CanDismiss)) return;
        busy = true; OnPropertyChanged(nameof(IsBusy)); NotifyCommands();
        var wrote = false;
        var run = ActivityRun.None;
        try
        {
            RecoveryPlan? plan = null;
            if (restore)
            {
                plan = await feed.PlanRestoreBeforeAsync(entry, CancellationToken.None).ConfigureAwait(true);
                if (plan is null) { Notice = Text["HistResolveFailed"]; NoticeSeverity = BannerSeverity.Warning; return; }
            }
            var confirmation = restore
                ? new ActionConfirmation(Text["HistRestoreBeforeTitle"], Text["HistRestoreBeforeBody"], plan!.Lines.Select(l => l.Resource).ToList(), Text["HistRestoreBeforeWarning"], Text["HistRestoreBeforeButton"]) { Changes = plan.Lines.Select(l => new ConfirmChange(l.Resource, l.Current, l.RestoreTo)).ToList(), Risk = Winora.Domain.RiskLevel.Medium, Undo = ConfirmUndo.WhileUnchanged }
                : new ActionConfirmation(Text["HistDismissTitle"], Text["HistDismissBody"], [entry.Title], Text["HistDismissWarning"], Text["HistDismissButton"]) { Changes = [new ConfirmChange(entry.Title)], Risk = Winora.Domain.RiskLevel.Low, Undo = ConfirmUndo.No };
            if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
            run = Runs.Begin(Text.Format(restore ? "ACRunRestoreBefore" : "ACRunDismiss", entry.Title), restore);
            if (!run.Started) return;
            wrote = restore;
            var outcome = restore ? await feed.RestoreBeforeAsync(entry, CancellationToken.None, plan!.Token).ConfigureAwait(true) : await feed.DismissAsync(entry, CancellationToken.None).ConfigureAwait(true);
            if (outcome.Ok) { Notice = Text[restore ? "HistRestoreBeforeDone" : "HistDismissDone"] + (outcome.RecordLost ? " " + Text["RecordLostNote"] : ""); NoticeSeverity = outcome.RecordLost ? BannerSeverity.Warning : BannerSeverity.Success; }
            else { Notice = outcome.Message ?? Text[outcome.Code.EndsWith(".stale", StringComparison.Ordinal) ? "HistRestoreChanged" : "HistResolveFailed"]; NoticeSeverity = BannerSeverity.Warning; }
            run.Finish(outcome.Ok ? RunResult.Ok : RunResult.Warning, Notice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Report(ex, "History resolve");
            Notice = Text["HistResolveFailed"]; NoticeSeverity = BannerSeverity.Warning;
            run.Finish(RunResult.Error, Notice);
        }
        finally
        {
            run.Dispose();
            busy = false; OnPropertyChanged(nameof(IsBusy));
            if (wrote) Journal.Wrote(entry.Area);
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    protected override void OnLanguageChanged()
    {
        if (loaded) { _ = RefreshAfterLanguageAsync(); }
        base.OnLanguageChanged();
    }

    private async Task RefreshAfterLanguageAsync()
    {
        try { await RefreshAsync().ConfigureAwait(true); }
        catch (ObjectDisposedException) { }
    }

    protected override void OnModelChanged() { base.OnModelChanged(); if (loaded) RequestRefresh(); }
}
