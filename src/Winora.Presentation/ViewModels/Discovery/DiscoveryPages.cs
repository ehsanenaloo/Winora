// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application.Activity;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop;


/// <summary>One file or leftover folder in quarantine with its two row actions. The buttons act on the row; the page asks before it deletes.</summary>
public sealed class QuarantineRow : DiscoveryRow
{
    public QuarantineRow(DiscoveryRow source, Guid id, string fileName, string folder, string whenText, string sizeText, bool canAct, RelayCommand<QuarantineRow> restore, RelayCommand<QuarantineRow> delete,
        string restoreLabel, string deleteLabel, string program = "", bool canRestore = true) : base(source)
    {
        Id = id; FileName = fileName; Folder = folder; WhenText = whenText; SizeText = sizeText; CanAct = canAct; RestoreCommand = restore; DeleteCommand = delete;
        RestoreLabel = restoreLabel; DeleteLabel = deleteLabel; Program = program; CanRestore = canRestore;
    }

    public Guid Id { get; }
    /// <summary>The program the item was left behind by, empty for a single file.</summary>
    public string Program { get; }
    public bool HasProgram => Program.Length > 0;
    public string FileName { get; }
    public string Folder { get; }
    public string WhenText { get; }
    public string SizeText { get; }
    /// <summary>False when the page has no restore and delete service (a view-only build): the buttons are hidden.</summary>
    public bool CanAct { get; }
    public RelayCommand<QuarantineRow> RestoreCommand { get; }
    public RelayCommand<QuarantineRow> DeleteCommand { get; }
    public string RestoreLabel { get; }
    public string DeleteLabel { get; }
    /// <summary>False when the quarantined copy is missing: there is nothing to put back, only the record to delete.</summary>
    public bool CanRestore { get; }
    // Two leftover folders often share a name (the install folder and the ProgramData folder), so the folder is part of the name.
    public string RestoreName => Folder.Length > 0 ? $"{RestoreLabel}: {FileName}, {Folder}" : $"{RestoreLabel}: {FileName}";
    public string DeleteName => Folder.Length > 0 ? $"{DeleteLabel}: {FileName}, {Folder}" : $"{DeleteLabel}: {FileName}";
}

/// <summary>The line above the list: how much is in quarantine, the button that empties it, and the result of the last action.</summary>
public sealed class QuarantineBannerPanel(QuarantineViewModel page) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string SummaryText => page.SummaryText;
    public bool HasSummary => page.SummaryText.Length > 0;
    public string NoteText => page.Text["V2ClQuarantineNote"];
    public string EmptyLabel => page.Text["V2ClEmptyAll"];
    public string EmptyHelp => page.Text["V2ClEmptyAllHelp"];
    public bool CanEmpty => page.CanAct && page.TotalCount > 0;
    public System.Windows.Input.ICommand EmptyCommand => page.EmptyCommand;
    public bool HasNotice => page.Notice is not null;
    public string? Notice => page.Notice;
    public BannerSeverity NoticeSeverity => page.NoticeIsError ? BannerSeverity.Warning : BannerSeverity.Success;
    public string NoticeTitle => page.Text[page.NoticeIsError ? "V2ClNoticeProblem" : "V2ClNoticeDone"];
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>The change history block below the quarantine list.</summary>
public sealed class QuarantineHistoryPanel(PageHistoryModel list) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public PageHistoryModel List => list;
    public void Refresh() { OnPropertyChanged(string.Empty); list.Refresh(); }
}

/// <summary>
/// The files and leftover folders Winora keeps in quarantine. Each can be restored to where it came from or deleted for good, and the whole list can be emptied. Restore goes only to a
/// place the action service allows; delete and empty ask first and cannot be undone. Items older than <see cref="LeftoverService.RetentionDays"/> days are deleted at app start.
/// Without the service (a view-only build) the list is read only.
/// </summary>
public sealed class QuarantineViewModel : DiscoveryPageViewModel<QuarantineEntry>
{
    private readonly IReviewPrompt? prompt;
    private readonly IQuarantineActions? actions;
    private readonly LeftoverService? leftovers;
    private readonly QuarantineBannerPanel banner;
    private readonly QuarantineHistoryPanel? history;
    private Task work = Task.CompletedTask;
    private bool busy;
    private Func<string>? noticeText;
    private bool noticeIsError;

    public override bool ShowTitle => false;

    public QuarantineViewModel(LocalizationService text, SimulationModel model, ErrorPresenter errors, InventoryModel inventory, IReviewPrompt? prompt = null, IQuarantineActions? actions = null,
        LeftoverService? leftovers = null)
        : base("Cleanup", IconKey.Broom24, text, model, errors, inventory.Quarantine)
    {
        this.prompt = prompt; this.actions = actions; this.leftovers = leftovers;
        banner = new(this);
        if (leftovers is not null) history = new(new PageHistoryModel(text, HistoryArea.Cleanup, () => OpenHistory));
        AddSort("SortName", i => Path.GetFileName(i.OriginalPath));
        AddSort("SortQuarantined", i => i.QuarantinedAtUtc);
        AddSort("SortSize", i => i.Fingerprint.Size);
        RestoreCommand = new(row => { if (row is not null) work = Run(() => RestoreAsync(row)); }, row => row is { CanRestore: true } && CanAct);
        DeleteCommand = new(row => { if (row is not null) work = Run(() => DeleteAsync(row)); }, row => row is not null && CanAct);
        EmptyCommand = new(() => work = Run(EmptyAsync), () => CanAct && TotalCount > 0);
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Rows)) { banner.Refresh(); EmptyCommand.NotifyCanExecuteChanged(); } };
    }

    public RelayCommand<QuarantineRow> RestoreCommand { get; }
    public RelayCommand<QuarantineRow> DeleteCommand { get; }
    public RelayCommand EmptyCommand { get; }
    protected override IReviewPrompt? DetailsHost => prompt;

    /// <summary>Restore and Delete in the details dialog: the row buttons are skipped by Tab, so this is their keyboard path.</summary>
    protected override IReadOnlyList<DetailsAction> DetailActions(DiscoveryRow row) =>
        row is QuarantineRow q && CanAct
            ?
            [
                .. q.CanRestore ? new DetailsAction[] { new(q.RestoreLabel, q.RestoreName, Text["V2ClRestoreHelp"], () => { RestoreCommand.Execute(q); return Task.CompletedTask; }, IsPrimary: true) } : [],
                new(q.DeleteLabel, q.DeleteName, Text["V2ClDeleteHelp"], () => { DeleteCommand.Execute(q); return Task.CompletedTask; }, IsDanger: true),
            ]
            : [];
    public override object? BannerPanel => banner;
    public override object? FooterPanel => history;
    public PageHistoryModel? HistoryList => history?.List;
    public override bool ShowReadOnlyNotice => false;
    protected override bool SummaryHasCount => true;
    public override bool IsBusy => base.IsBusy || busy;
    public bool CanAct => actions is not null && prompt is not null && !busy && !base.IsBusy;
    public string? Notice => noticeText?.Invoke();
    public bool NoticeIsError => noticeIsError;

    public string SummaryText
    {
        get
        {
            var all = CurrentResult?.Items;
            if (all is null || all.Count == 0) return "";
            return Text.Format(all.Count == 1 ? "V2ClSummaryOne" : "V2ClSummary", all.Count, Size(all.Sum(e => e.Fingerprint.Size)));
        }
    }

    public override async Task WhenIdleAsync() { await base.WhenIdleAsync(); await work; }

    protected override void OnLanguageChanged() { base.OnLanguageChanged(); banner.Refresh(); RefreshHistory(); }

    protected override void OnResultApplied(DiscoveryResult<QuarantineEntry> value) { base.OnResultApplied(value); RefreshHistory(); }

    /// <summary>Leftovers moved on the Apps page since the last read show up when the tab opens again.</summary>
    public override void Activate()
    {
        if (Journal.IsStale && CurrentResult is not null && !IsScanning) { Journal.MarkRead(); work = ScanAsync(); return; }
        base.Activate();
    }

    /// <summary>Reads the quarantine journal again into the change history block.</summary>
    public void RefreshHistory()
    {
        if (history is null || leftovers is null) return;
        IReadOnlyList<QuarantineRecord> records;
        try { records = leftovers.History(200); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { records = []; }
        history.List.Set([.. records.Select(r => history.List.Row(r.Change.Id, r.Change.AtUtc, QuarantineHistoryText.Result(r), QuarantineHistoryText.Title(Text, r.Change), false, null, null))]);
        history.Refresh();
    }

    private void SetNotice(Func<string>? value, bool isError = false)
    {
        noticeText = value; noticeIsError = isError;
        if (value is not null && !isError) Feedback.Success(value());
        banner.Refresh();
    }

    private async Task Run(Func<Task> action)
    {
        using var run = Runs.Begin(Text.Format("ACRunApply", Title), true);
        if (!run.Started) return;
        busy = true; OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); SetNotice(null);
        try { await action().ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { SetNotice(() => Text["ActionFailedUnexpected"], true); }
        finally { busy = false; OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); }
        run.Finish(noticeIsError ? RunResult.Error : RunResult.Ok, noticeText?.Invoke());
        Journal.Wrote(HistoryArea.Cleanup);
        await ScanAsync().ConfigureAwait(true);
        RefreshHistory();
    }

    private void RefreshCommands() { RestoreCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); EmptyCommand.NotifyCanExecuteChanged(); banner.Refresh(); }

    private string ReasonText(string reason)
    {
        var key = "V2ClReason_" + reason;
        return Text.Has(key) ? Text[key] : Text["V2ClReason_failed"];
    }

    private async Task RestoreAsync(QuarantineRow row)
    {
        var result = await Task.Run(() => actions!.Restore(row.Id, CancellationToken.None)).ConfigureAwait(true);
        if (result.Ok) SetNotice(() => Text.Format("V2ClRestored", row.FileName));
        else SetNotice(() => Text.Format("V2ClRestoreFailed", row.FileName, ReasonText(result.Reason)), true);
    }

    private async Task DeleteAsync(QuarantineRow row)
    {
        var confirmation = new ActionConfirmation(Text.Format("V2ClDeleteTitle", row.FileName), Text["V2ClDeleteBody"], [row.FileName], Text["V2ClDeleteWarning"], Text["V2ClDeletePrimary"])
        {
            Changes = [new ConfirmChange(row.FileName, row.SizeText, Text["V2ClGone"], row.Folder)],
            Risk = RiskLevel.Medium, Undo = ConfirmUndo.No, Restart = false, Irreversible = true,
        };
        if (!await prompt!.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        var result = await Task.Run(() => actions!.Delete(row.Id)).ConfigureAwait(true);
        if (result.Ok) SetNotice(() => Text.Format("V2ClDeleted", row.FileName));
        else SetNotice(() => Text.Format("V2ClDeleteFailed", row.FileName, ReasonText(result.Reason)), true);
    }

    private async Task EmptyAsync()
    {
        var entries = CurrentResult?.Items.ToList() ?? [];
        if (entries.Count == 0) return;
        var total = Size(entries.Sum(e => e.Fingerprint.Size));
        var confirmation = new ActionConfirmation(Text["V2ClEmptyTitle"], Text.Format("V2ClEmptyBody", entries.Count, total), entries.Select(e => Path.GetFileName(e.OriginalPath)).ToList(), Text["V2ClDeleteWarning"], Text["V2ClEmptyPrimary"])
        {
            Changes = entries.Select(e => new ConfirmChange(Path.GetFileName(e.OriginalPath), Size(e.Fingerprint.Size), Text["V2ClGone"], e.OriginalPath)).ToList(),
            Risk = RiskLevel.Medium, Undo = ConfirmUndo.No, Restart = false, Irreversible = true,
        };
        if (!await prompt!.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        var done = 0; string? failure = null;
        foreach (var entry in entries)
        {
            var result = await Task.Run(() => actions!.Delete(entry.Id)).ConfigureAwait(true);
            if (result.Ok) done++; else failure ??= result.Reason;
        }
        if (failure is null) SetNotice(() => Text.Format("V2ClEmptied", done));
        else SetNotice(() => Text.Format("V2ClEmptiedSome", done, entries.Count, ReasonText(failure)), true);
    }

    protected override IEnumerable<string?> SearchFields(QuarantineEntry i) => [Path.GetFileName(i.OriginalPath), i.OriginalPath, i.Program];

    private string Size(long bytes) => SizeFormat.Bytes(Text, bytes);

    protected override DiscoveryRow BuildRow(QuarantineEntry i)
    {
        var name = Path.GetFileName(i.OriginalPath);
        var folder = Path.GetDirectoryName(i.OriginalPath) ?? "";
        var program = i.Program ?? "";
        var purge = i.QuarantinedAtUtc.AddDays(LeftoverService.RetentionDays);
        List<BadgeModel> badges = [Neutral(Size(i.Fingerprint.Size))];
        if (i.ContentMissing) badges.Insert(0, new(Text["V2ClMissingBadge"], Tone.Caution));
        if (i.IsFolder) badges.Insert(0, Neutral(Text.Format(i.Files == 1 ? "V2ClFolderFilesOne" : "V2ClFolderFiles", Text.Number(i.Files))));
        var plain = new DiscoveryRow(i.Id.ToString("N"), name, folder, true, "", badges,
            () =>
            [
                new(Text["FieldName"], name),
                .. program.Length > 0 ? new DetailField[] { new(Text["FieldLeftoverOf"], program) } : [],
                new(Text["FieldOriginalPath"], i.OriginalPath, true),
                new(Text["FieldQuarantinedAt"], When(i.QuarantinedAtUtc)),
                new(Text["FieldPurgedAt"], When(purge)),
                new(Text[i.IsFolder ? "FieldFolderSize" : "FieldFileSize"], Size(i.Fingerprint.Size)),
                .. i.IsFolder ? new DetailField[] { new(Text["FieldFileCount"], Text.Number(i.Files)) } : [new(Text["FieldSha256"], i.Sha256, true)],
                new(Text["FieldIdentifier"], i.Id.ToString("N"), true),
            ]);
        return new QuarantineRow(plain, i.Id, name, folder, When(i.QuarantinedAtUtc), Size(i.Fingerprint.Size), actions is not null && prompt is not null, RestoreCommand, DeleteCommand, Text["V2ClRestore"], Text["V2ClDelete"],
            program.Length > 0 ? Text.Format("V2ClLeftoverOf", program) : "", canRestore: !i.ContentMissing);
    }
}
