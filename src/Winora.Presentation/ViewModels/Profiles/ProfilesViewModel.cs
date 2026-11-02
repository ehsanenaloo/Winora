// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>A page that holds settings the user picked but has not applied yet. The Profiles page can save that selection as a custom profile.</summary>
public interface IPendingSelectionSource
{
    IReadOnlyList<ProfileSelection> PendingSelection();
}

/// <summary>One profile card: the name, what it is for, and what it would change on this PC.</summary>
/// <summary>One line of the "What changes" list of a card: a setting, its value now and the value the profile sets.</summary>
public sealed class ProfileCardChange(string title, string before, string after, string arrow)
{
    public string Title { get; } = title;
    public string Before { get; } = before;
    public string After { get; } = after;
    public string ChangeText => $"{Before} {arrow} {After}";
    public string AutomationName => $"{Title}, {Before} {arrow} {After}";
}

public sealed class ProfileCardRow : ObservableObject
{
    private readonly LocalizationService text;
    private readonly Func<ProfilePlan, IReadOnlyList<ProfileCardChange>>? changeBuilder;
    private ProfilePlan? plan;
    private bool chosen, expanded;

    public ProfileCardRow(Profile profile, LocalizationService text, RelayCommand<ProfileCardRow> review, RelayCommand<ProfileCardRow> export, RelayCommand<ProfileCardRow> delete,
        RelayCommand<ProfileCardRow>? choose = null, Func<ProfilePlan, IReadOnlyList<ProfileCardChange>>? changeBuilder = null)
    {
        Profile = profile; this.text = text;
        ReviewCommand = review; ExportCommand = export; DeleteCommand = delete; ChooseCommand = choose; this.changeBuilder = changeBuilder;
    }

    public RelayCommand<ProfileCardRow>? ChooseCommand { get; }
    /// <summary>The card the Review button at the bottom of the page works on. Exactly one built-in card is chosen at a time.</summary>
    public bool IsChosen { get => chosen; set { if (SetProperty(ref chosen, value)) { OnPropertyChanged(nameof(AutomationName)); OnPropertyChanged(nameof(IsChecked)); } } }
    /// <summary>The radio button state. UI Automation (a screen reader) selects the radio button without a click, so a check from the view chooses the card too.</summary>
    public bool IsChecked { get => chosen; set { if (value && !chosen) ChooseCommand?.Execute(this); else OnPropertyChanged(); } }
    /// <summary>The list under "What changes" is open.</summary>
    public bool IsExpanded { get => expanded; set => SetProperty(ref expanded, value); }
    public bool IsRecommended => Profile.Id == "balanced";
    public string RecommendedText => text["V2PfRecommended"];
    public IconKey IconSymbol => Profile.Id switch
    {
        "balanced" => IconKey.Sparkle24,
        "speed" => IconKey.Rocket24,
        "security" => IconKey.ShieldCheckmark24,
        "privacy" => IconKey.ShieldLock24,
        "gaming" => IconKey.Flash24,
        "minimal" => IconKey.PaintBrush24,
        _ => IconKey.Toolbox24,
    };
    public int ChangeCount => plan?.ChangeCount ?? 0;
    public bool HasPlan => plan is not null;
    public string ChangeCountText => plan is null ? text["ProfileChecking"] : plan.ChangeCount switch { 0 => text["V2PfNone"], 1 => text["V2PfChangesOne"], var n => text.Format("V2PfChanges", n) };
    public string AlreadySetText => plan is { AlreadySetCount: > 0 } p ? text.Format("V2PfAlreadySet", p.AlreadySetCount) : "";
    public bool HasAlreadySet => plan is { AlreadySetCount: > 0 };
    /// <summary>The highest risk among the changes. The shipped lists have no High change; a custom list could.</summary>
    public RiskLevel RiskLevel => plan is null ? RiskLevel.Low : plan.Entries.Any(e => e.Status == ProfileEntryStatus.WillChange && e.Risk == RiskLevel.High) ? RiskLevel.High : plan.MediumRiskChangeCount > 0 ? RiskLevel.Medium : RiskLevel.Low;
    public string RiskBadgeText => RiskLevel switch { RiskLevel.High => text["V2PfRiskHigh"], RiskLevel.Medium => text["V2PfRiskMedium"], _ => text["V2PfRiskLow"] };
    public IReadOnlyList<ProfileCardChange> Changes => plan is null || changeBuilder is null ? [] : changeBuilder(plan);
    public bool HasChanges => Changes.Count > 0;
    public bool HasNoChanges => plan is not null && Changes.Count == 0;
    public string WhatChangesText => text["V2PfWhat"];
    public string AllSetText => text["V2PfAllSet"];
    public string WhatChangesHelp => text.Format("V2PfWhatHelp", Name);

    public Profile Profile { get; }
    public bool IsCustom => !Profile.IsBuiltIn;
    public string Name => Profile.IsBuiltIn ? text[Profile.NameKey!] : Profile.Name ?? Profile.Id;
    public string Description => Profile.IsBuiltIn ? text[Profile.DescriptionKey!] : text["ProfileCustomDescription"];
    public string CountText => Profile.Entries.Count == 1 ? text["ProfileCountOne"] : text.Format("ProfileCount", Profile.Entries.Count);
    public string PreviewText => plan is null ? text["ProfileChecking"] : plan.ChangeCount == 0
        ? text.Format("ProfileNothingToChange", plan.AlreadySetCount, plan.SkippedCount)
        : text.Format("ProfileWouldChange", plan.ChangeCount, plan.AlreadySetCount, plan.SkippedCount);
    public string RiskText => plan is null ? "" : text.Format("ProfileRiskSummary", plan.LowRiskChangeCount, plan.MediumRiskChangeCount) + " " + text["ProfileNoHighRisk"];
    public bool HasRisk => plan is not null;
    /// <summary>The areas the profile touches, in alphabetical order of the shown names. Empty until the plan is read.</summary>
    public IReadOnlyList<string> Categories => plan is null ? [] : plan.Entries.Select(e => e.Category).Distinct().Select(c => text.Enum(c)).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    public bool HasCategories => Categories.Count > 0;
    public RelayCommand<ProfileCardRow> ReviewCommand { get; }
    public RelayCommand<ProfileCardRow> ExportCommand { get; }
    public RelayCommand<ProfileCardRow> DeleteCommand { get; }
    public string ReviewLabel => text["ProfileReview"];
    public string ReviewHelp => text.Format("ProfileReviewHelp", Name);
    public string AutomationName => $"{Name}, {ChangeCountText}, {RiskBadgeText}";

    public void SetPlan(ProfilePlan? value) { plan = value; Refresh(); }
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class ProfilePlanRowView(string title, string before, string after, string reason, string status, bool isChange, bool isSkipped, string risk, string scope, string? restart, string arrow, RiskLevel riskLevel = RiskLevel.Low)
{
    public RiskLevel RiskLevel { get; } = riskLevel;
    public string Title { get; } = title;
    public string Before { get; } = before;
    public string After { get; } = after;
    public string Reason { get; } = reason;
    public string Status { get; } = status;
    public bool IsChange { get; } = isChange;
    public bool IsSkipped { get; } = isSkipped;
    public string Risk { get; } = risk;
    public string Scope { get; } = scope;
    public string? Restart { get; } = restart;
    public bool HasRestart => Restart is not null;
    public string ChangeText => $"{Before} {arrow} {After}";
    public string AutomationName => IsChange ? $"{Title}, {Before} {arrow} {After}, {Risk}, {Scope}" : $"{Title}, {Status}";
}

public sealed class ProfilePlanGroupView(string heading, IReadOnlyList<ProfilePlanRowView> rows)
{
    public string Heading { get; } = heading;
    public IReadOnlyList<ProfilePlanRowView> Rows { get; } = rows;
    public string AutomationName => $"{Heading}, {Rows.Count}";
}

public sealed class ProfileResultRowView(string title, string outcome, string detail, bool isProblem)
{
    public string Title { get; } = title;
    public string Outcome { get; } = outcome;
    public string Detail { get; } = detail;
    public bool IsProblem { get; } = isProblem;
    public bool HasDetail => Detail.Length > 0;
    public string AutomationName => Detail.Length == 0 ? $"{Title}, {Outcome}" : $"{Title}, {Outcome}, {Detail}";
}

/// <summary>One line of the comparison table: a setting and what each built-in profile sets it to (a dash when the profile does not touch it).</summary>
public sealed class ProfileCompareRow(string title, IReadOnlyList<string> cells)
{
    public string Title { get; } = title;
    public IReadOnlyList<string> Cells { get; } = cells;
    public string AutomationName => $"{Title}: " + string.Join("; ", Cells);
}

public sealed class ProfileHistoryRow(ProfileHistoryEntry entry, string name, string description, string when, bool canUndo, RelayCommand<ProfileHistoryRow> undo)
{
    public ProfileHistoryEntry Entry { get; } = entry;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string When { get; } = when;
    public bool CanUndo { get; } = canUndo;
    public RelayCommand<ProfileHistoryRow> UndoCommand { get; } = undo;
    public string AutomationName => $"{Name}, {Description}, {When}";
}

/// <summary>
/// The Profiles page: named lists of existing settings that are applied together. The page only shows and sequences; every change runs through the same services as the Customize and Optimize pages
/// (journal line first, compare-and-set, read-back, undo while nothing else changed the value), with one confirmation for the whole list (decision DEC-IW-170).
/// </summary>
public sealed class ProfilesViewModel : PageViewModel, INoticePage
{
    private const int HistoryLimit = 20;
    private readonly ProfileService service;
    private readonly IReviewPrompt prompt;
    private readonly IElevationState elevation;
    private readonly IProfileFileAccess files;
    private readonly IReadOnlyList<IPendingSelectionSource> sources;
    private readonly Dictionary<string, ProfilePlan> plans = [];
    private CancellationTokenSource? work;
    private Task running = Task.CompletedTask;
    private bool loaded, busy, applying, undoing, pageFailed;
    private string? notice, loadError, saveName = "", progressText = "";
    private bool noticeIsError;
    private double progressValue;
    private ProfilePlan? current;
    private ProfileCardRow? currentCard;
    private string resultHeading = "";

    public ProfilesViewModel(LocalizationService text, SimulationModel model, ProfileService service, IReviewPrompt prompt, IElevationState elevation, IProfileFileAccess files,
        IReadOnlyList<IPendingSelectionSource>? sources = null) : base("Profiles", text, model)
    {
        HistoryList = new(text, HistoryArea.Profiles, () => OpenHistory);
        this.service = service; this.prompt = prompt; this.elevation = elevation; this.files = files; this.sources = sources ?? [];
        RefreshCommand = new(() => running = LoadAsync(), () => !Busy);
        ReviewCardCommand = new(card => { if (card is not null) running = ReviewAsync(card); }, card => !Busy && card is not null);
        ChooseCommand = new(card => { if (card is not null) Choose(card); });
        ReviewChosenCommand = new(() => { if (chosenCard is not null) running = ReviewAsync(chosenCard); }, () => !Busy && chosenCard is { ChangeCount: > 0 });
        ApplyCommand = new(() => running = ApplyAsync(), () => CanApply);
        CancelCommand = new(() => work?.Cancel(), () => Applying);
        CloseCommand = new(ClosePlan);
        ExportCommand = new(card => { if (card is not null) running = ExportAsync(card); }, card => !Busy && card is not null);
        DeleteCommand = new(card => { if (card is not null) running = DeleteAsync(card); }, card => !Busy && card is not null);
        UndoCommand = new(row => { if (row is not null && !Busy) running = UndoAsync(row); }, row => !Busy && row is not null);
        SavePendingCommand = new(() => running = SavePendingAsync(), () => !Busy);
        SaveChangedCommand = new(() => running = SaveChangedAsync(), () => !Busy);
        ImportCommand = new(() => running = ImportAsync(), () => !Busy);
    }

    public ObservableCollection<ProfileCardRow> BuiltInCards { get; } = [];
    public ObservableCollection<ProfileCardRow> CustomCards { get; } = [];
    public ObservableCollection<ProfilePlanGroupView> PlanGroups { get; } = [];
    public ObservableCollection<ProfileResultRowView> ResultRows { get; } = [];
    public ObservableCollection<ProfileHistoryRow> History { get; } = [];
    public PageHistoryModel HistoryList { get; }
    public ObservableCollection<ProfileCompareRow> CompareRows { get; } = [];
    public IReadOnlyList<string> CompareHeaders { get; private set; } = [];
    private bool showCompare;
    public RelayCommand ToggleCompareCommand => toggleCompare ??= new(() => ShowCompare = !ShowCompare);
    private RelayCommand? toggleCompare;
    public bool ShowCompare { get => showCompare; private set { if (SetProperty(ref showCompare, value)) { OnPropertyChanged(nameof(CompareLabel)); } } }
    public string CompareLabel => Text[showCompare ? "ProfileCompareHide" : "ProfileCompareShow"];
    /// <summary>The newest profile run that still has changes in place, for the line under the cards.</summary>
    public ProfileHistoryRow? LastApplied => History.FirstOrDefault();
    public bool HasLastApplied => LastApplied is not null;
    public string LastAppliedText => LastApplied is { } row ? Text.Plural("ProfileLastApplied", row.Entry.Changed, row.Name, row.When, row.Entry.Changed) : Text["ProfileLastAppliedNone"];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand<ProfileCardRow> ReviewCardCommand { get; }
    public RelayCommand<ProfileCardRow> ChooseCommand { get; }
    public RelayCommand ReviewChosenCommand { get; }
    private ProfileCardRow? chosenCard;
    public ProfileCardRow? ChosenCard => chosenCard;
    /// <summary>"Review 9 changes", or "No changes" when the chosen profile has nothing to do on this PC.</summary>
    public string ReviewChosenLabel => chosenCard is null ? Text["V2PfNone"] : chosenCard.ChangeCount == 0 ? Text["V2PfNone"] : chosenCard.ChangeCount == 1 ? Text["V2PfReviewOne"] : Text.Format("V2PfReview", chosenCard.ChangeCount);
    public string ReviewChosenHelp => chosenCard is null ? Text["V2PfNone"] : Text.Format("ProfileReviewHelp", chosenCard.Name);

    private void Choose(ProfileCardRow card)
    {
        if (ReferenceEquals(chosenCard, card)) return;
        chosenCard = card;
        foreach (var c in BuiltInCards) c.IsChosen = ReferenceEquals(c, card);
        NotifyChosen();
    }

    private void NotifyChosen()
    {
        OnPropertyChanged(nameof(ChosenCard)); OnPropertyChanged(nameof(ReviewChosenLabel)); OnPropertyChanged(nameof(ReviewChosenHelp));
        ReviewChosenCommand.NotifyCanExecuteChanged();
    }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand<ProfileCardRow> ExportCommand { get; }
    public RelayCommand<ProfileCardRow> DeleteCommand { get; }
    public RelayCommand<ProfileHistoryRow> UndoCommand { get; }
    public RelayCommand SavePendingCommand { get; }
    public RelayCommand SaveChangedCommand { get; }
    public RelayCommand ImportCommand { get; }

    public override bool IsBusy => Busy;
    private bool Busy { get => busy; set { if (SetProperty(ref busy, value)) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(ShowLoading)); Notify(); } } }
    public bool Applying { get => applying; private set { if (SetProperty(ref applying, value)) Notify(); } }
    public bool ShowNeedsAdministrator => !elevation.IsElevated;
    public string NeedsAdministratorTitle => elevation.UnsafeLocation ? Text["AppLocationUnsafeTitle"] : Text["ProfileNeedsAdminTitle"];
    public string NeedsAdministratorText => elevation.UnsafeLocation ? Text["AppLocationUnsafe"] : Text["ProfileNeedsAdminBody"];
    public bool ShowAdminHowTo => !elevation.UnsafeLocation;
    public bool HasCustom => CustomCards.Count > 0;
    /// <summary>The read finished and there is no profile of the user's own: the custom section shows its empty state above the save form.</summary>
    public bool ShowNoCustom => loaded && CustomCards.Count == 0;
    public bool HasHistory => History.Count > 0;
    public bool HasLoadError => !string.IsNullOrEmpty(loadError);
    public string? LoadError { get => loadError; private set { if (SetProperty(ref loadError, value)) { OnPropertyChanged(nameof(HasLoadError)); OnPropertyChanged(nameof(ShowLoadFailed)); OnPropertyChanged(nameof(LoadFailedText)); } } }
    /// <summary>The first read is running and nothing is shown yet: the page shows the loading skeleton.</summary>
    public bool ShowLoading => Busy && !loaded && !ShowLoadFailed;
    /// <summary>A profile file could not be used or the first read failed: the page shows the error state with Try again.</summary>
    public bool ShowLoadFailed => HasLoadError || pageFailed;
    public string LoadFailedText => LoadError ?? (pageFailed ? Text["ActionFailedUnexpected"] : "");
    public string? Notice { get => notice; private set { if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool NoticeIsError { get => noticeIsError; private set { if (SetProperty(ref noticeIsError, value)) OnPropertyChanged(nameof(NoticeSeverity)); } }
    public BannerSeverity NoticeSeverity => NoticeIsError ? BannerSeverity.Warning : BannerSeverity.Success;
    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public string SaveName { get => saveName ?? ""; set => SetProperty(ref saveName, value); }
    public string ProgressText { get => progressText ?? ""; private set => SetProperty(ref progressText, value); }
    public double ProgressValue { get => progressValue; private set { if (SetProperty(ref progressValue, value)) OnPropertyChanged(nameof(ProgressFraction)); } }
    public double ProgressFraction => progressValue / 100d;

    // ---- the open plan ----

    public bool HasPlan => current is not null;
    public ProfilePlan? Plan => current;
    public string PlanTitle => currentCard is null ? "" : Text.Format("ProfilePlanTitle", currentCard.Name);
    public string PlanSummary => current is null ? "" : current.ChangeCount == 0
        ? Text.Format("ProfileNothingToChange", current.AlreadySetCount, current.SkippedCount)
        : Text.Format("ProfilePlanSummary", current.ChangeCount, current.AlreadySetCount, current.SkippedCount);
    public bool CanApply => current is { ChangeCount: > 0 } && !Busy && ResultRows.Count == 0;
    public string ApplyLabel => current is null ? Text["ProfileApply"] : Text.Plural("ProfileApplyPrimary", current.ChangeCount, current.ChangeCount);
    public bool HasResult => ResultRows.Count > 0;
    public bool HasReport => report is not null;
    public bool ShowUndoResult => HasResult && report is null;
    private ResultReportModel? report;
    /// <summary>The report of the last apply: counts, the items that failed, Undo all, Copy report and Close.</summary>
    public ResultReportModel? Report { get => report; private set { if (SetProperty(ref report, value)) { OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(ShowUndoResult)); } } }
    public string ResultHeading { get => resultHeading; private set => SetProperty(ref resultHeading, value); }

    private void Notify()
    {
        foreach (var c in new IRelayCommand[] { RefreshCommand, ReviewCardCommand, ReviewChosenCommand, ApplyCommand, CancelCommand, ExportCommand, DeleteCommand, UndoCommand, SavePendingCommand, SaveChangedCommand, ImportCommand })
            c.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(Applying));
    }

    public override void Activate() { if ((!loaded || Journal.IsStale) && !busy) running = LoadAsync(); }

    /// <summary>Opens the plan of one built-in or custom profile by id (Overview and first run). Waits for the first load when it is still running.</summary>
    public void ReviewProfile(string profileId) => running = ReviewByIdAsync(profileId);

    private async Task ReviewByIdAsync(string profileId)
    {
        if (!loaded && !busy) await LoadAsync().ConfigureAwait(true);
        else await running.ConfigureAwait(true);
        if (BuiltInCards.Concat(CustomCards).FirstOrDefault(c => c.Profile.Id == profileId) is { } card) await ReviewAsync(card).ConfigureAwait(true);
    }
    /// <summary>Leaving the page stops a read; a write (an apply or an undo) runs to its end.</summary>
    public override void Deactivate() { if (!Applying && !undoing) work?.Cancel(); }
    public override Task WhenIdleAsync() => running;
    public override System.Windows.Input.ICommand? ShortcutRefresh() => RefreshCommand;

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var card in BuiltInCards.Concat(CustomCards)) card.Refresh();
        if (current is not null) BuildPlanViews(current);
        BuildCompare();
    }

    // ---- loading ----

    private void SetPageFailed(bool value)
    {
        if (pageFailed == value) return;
        pageFailed = value;
        foreach (var n in new[] { nameof(ShowLoadFailed), nameof(LoadFailedText), nameof(ShowLoading) }) OnPropertyChanged(n);
    }

    private async Task LoadAsync(bool keepNotice = false)
    {
        Journal.MarkRead();
        SetPageFailed(false);
        Busy = true;
        if (!keepNotice) Notice = null;
        try
        {
            work?.Cancel();
            work = new CancellationTokenSource();
            var ct = work.Token;
            var catalog = await Task.Run(service.Load, ct).ConfigureAwait(true);
            plans.Clear();
            BuiltInCards.Clear(); CustomCards.Clear();
            chosenCard = null;
            foreach (var p in catalog.BuiltIn.OrderBy(p => Array.IndexOf(CardOrder, p.Id) is var i and >= 0 ? i : CardOrder.Length).ThenBy(p => p.Id, StringComparer.Ordinal)) BuiltInCards.Add(NewCard(p));
            foreach (var p in catalog.Custom) CustomCards.Add(NewCard(p));
            LoadError = catalog.Errors.Count == 0 ? null : catalog.Errors.Any(e => e.Source == "built-in") ? Text["ProfileLoadErrorBuiltIn"] : Text.Format("ProfileLoadErrorCustom", catalog.Errors.Count);
            OnPropertyChanged(nameof(HasCustom)); OnPropertyChanged(nameof(ShowNoCustom));
            foreach (var card in BuiltInCards.Concat(CustomCards))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var plan = await service.PlanAsync(card.Profile, ct).ConfigureAwait(true);
                    plans[card.Profile.Id] = plan;
                    card.SetPlan(plan);
                }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { card.SetPlan(null); }
            }
            await RefreshHistoryAsync(ct).ConfigureAwait(true);
            if ((BuiltInCards.FirstOrDefault(c => c.IsRecommended) ?? BuiltInCards.FirstOrDefault()) is { } first) Choose(first);
            else NotifyChosen();
            loaded = true;
            OnPropertyChanged(nameof(ShowLoading)); OnPropertyChanged(nameof(ShowNoCustom));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Before the first good read there is nothing to show, so the whole page is the error; later a failed refresh keeps the cards and says so in the notice.
            if (loaded) { Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; }
            else SetPageFailed(true);
        }
        finally { Busy = false; OnPropertyChanged(nameof(NoticeIsError)); }
    }

    private static readonly string[] CardOrder = ["balanced", "speed", "security", "privacy", "gaming", "minimal"];

    private ProfileCardRow NewCard(Profile profile) => new(profile, Text, ReviewCardCommand, ExportCommand, DeleteCommand, ChooseCommand, CardChanges);

    // The "What changes" list of a card: the settings that would change on this PC, by name.
    private IReadOnlyList<ProfileCardChange> CardChanges(ProfilePlan plan) => plan.Entries.Where(e => e.Status == ProfileEntryStatus.WillChange)
        .Select(e => new ProfileCardChange(TitleOf(e), LabelOf(e.BeforeLabelKey, e.BeforeChoiceId), LabelOf(e.AfterLabelKey, e.Entry.ChoiceId), Text["ArrowGlyph"]))
        .OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    private async Task RefreshHistoryAsync(CancellationToken ct)
    {
        History.Clear();
        var shown = new List<HistoryRowViewModel>();
        IReadOnlyList<ProfileHistoryEntry> entries;
        try { entries = await service.HistoryAsync(ct).ConfigureAwait(true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WinoraException) { entries = []; }
        // Windows Update modes are applied as groups too, but they belong to the Windows Update page: listed here they showed raw ids and became the target of Undo all.
        foreach (var e in entries.Where(e => HistoryText.KindOf(e.Label) == HistoryBatchKind.Profile).Take(HistoryLimit))
        {
            History.Add(new(e, NameOf(e.Label), Text.Plural("ProfileHistoryLine", e.Changed, e.Changed, e.StillApplied), Text.When(e.AtUtc.ToLocalTime(), "g"), e.StillApplied > 0, UndoCommand));
            var h = History[^1];
            shown.Add(HistoryList.Row(e.Group, e.AtUtc, HistoryResult.Applied, h.Name, h.CanUndo, h.UndoCommand, h, new(Text["ProfileUndo"], Text["ProfileUndoHelp"], h.Description)));
        }
        HistoryList.Set(shown);
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(LastApplied)); OnPropertyChanged(nameof(HasLastApplied)); OnPropertyChanged(nameof(LastAppliedText));
        BuildCompare();
    }

    private void BuildCompare()
    {
        var cards = BuiltInCards.ToList();
        CompareHeaders = cards.Select(c => c.Name).ToList();
        CompareRows.Clear();
        var perCard = cards.Select(c => plans.TryGetValue(c.Profile.Id, out var p) ? p.Entries.ToDictionary(e => (e.Entry.Kind, e.Entry.Id)) : []).ToList();
        var keys = perCard.SelectMany(d => d.Keys).Distinct().ToList();
        var rows = keys.Select(key =>
        {
            var first = perCard.Select(d => d.GetValueOrDefault(key)).First(e => e is not null)!;
            var cells = perCard.Select((d, i) => cards[i].Name + ": " + (d.TryGetValue(key, out var e) ? LabelOf(e.AfterLabelKey, e.Entry.ChoiceId) : Text["ProfileCompareNone"])).ToList();
            return new ProfileCompareRow(TitleOf(first), cells);
        }).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase);
        foreach (var row in rows) CompareRows.Add(row);
        OnPropertyChanged(nameof(CompareHeaders)); OnPropertyChanged(nameof(HasCompare));
    }

    public bool HasCompare => CompareRows.Count > 0;

    private string NameOf(string label)
    {
        var card = BuiltInCards.Concat(CustomCards).FirstOrDefault(c => c.Profile.Id == label);
        return card?.Name ?? (label.Length > 0 ? label : Text["ProfileUnknownName"]);
    }

    // ---- review ----

    private async Task ReviewAsync(ProfileCardRow card)
    {
        Busy = true; Notice = null;
        try
        {
            work?.Cancel(); work = new CancellationTokenSource();
            var plan = await service.PlanAsync(card.Profile, work.Token).ConfigureAwait(true);
            plans[card.Profile.Id] = plan;
            card.SetPlan(plan);
            ResultRows.Clear(); Report = null; ResultHeading = "";
            current = plan; currentCard = card;
            BuildPlanViews(plan);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); }
        finally { Busy = false; NotifyPlan(); }
    }

    private void ClosePlan()
    {
        current = null; currentCard = null; PlanGroups.Clear(); ResultRows.Clear(); Report = null; ResultHeading = "";
        NotifyPlan();
    }

    private void NotifyPlan()
    {
        foreach (var n in new[] { nameof(HasPlan), nameof(Plan), nameof(PlanTitle), nameof(PlanSummary), nameof(CanApply), nameof(ApplyLabel), nameof(HasResult), nameof(HasReport), nameof(ShowUndoResult) }) OnPropertyChanged(n);
        ApplyCommand.NotifyCanExecuteChanged();
        NotifyChosen();
    }

    private string TitleOf(ProfilePlanEntry e) => Text.Has(e.TitleKey) ? Text[e.TitleKey] : e.Entry.Id;
    private string UndoTitleOf(ProfileUndoEntry e) => service.TitleKeyOf(e.Kind, e.Id) is { } key && Text.Has(key) ? Text[key] : e.Id;
    private string LabelOf(string? key, string? id) => key is not null && Text.Has(key) ? Text[key] : id ?? Text["ProfileOtherValue"];
    private string ScopeOf(ProfileEntryKind kind) => Text[kind == ProfileEntryKind.User ? "ProfileScopeUser" : "ProfileScopeMachine"];

    public string ReasonText(string code)
    {
        foreach (var key in new[] { "ProfileCode_" + code.Replace('.', '_'), "OptimizeCode_" + code.Replace('.', '_'), "Code_" + code.Replace('.', '_') })
            if (Text.Has(key)) return Text[key];
        return Text["ProfileCode_profile_failed"];
    }

    private void BuildPlanViews(ProfilePlan plan)
    {
        PlanGroups.Clear();
        foreach (var group in plan.Entries.GroupBy(e => e.Category).OrderBy(g => Text.Enum(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var rows = group.OrderBy(e => e.Status == ProfileEntryStatus.WillChange ? 0 : e.Status == ProfileEntryStatus.AlreadySet ? 1 : 2).ThenBy(TitleOf, StringComparer.CurrentCultureIgnoreCase).Select(e =>
            {
                var change = e.Status == ProfileEntryStatus.WillChange;
                var reason = e.Entry.ReasonKey is { } k && Text.Has(k) ? Text[k] : Text["ProfileReasonCustom"];
                var status = e.Status switch
                {
                    ProfileEntryStatus.WillChange => Text["ProfileStatusChange"],
                    ProfileEntryStatus.AlreadySet => Text["ProfileStatusAlreadySet"],
                    _ => Text.Format("ProfileStatusSkipped", ReasonText(e.SkipCode ?? ProfileCode.Failed)),
                };
                return new ProfilePlanRowView(TitleOf(e), LabelOf(e.BeforeLabelKey, e.BeforeChoiceId), LabelOf(e.AfterLabelKey, e.Entry.ChoiceId), reason, status, change, e.Status == ProfileEntryStatus.Skipped,
                    Text.Enum(e.Risk), ScopeOf(e.Entry.Kind), change && e.NeedsRestart ? Text["BadgeRestart"] : null, Text["ArrowGlyph"], e.Risk);
            }).ToList();
            PlanGroups.Add(new(Text.Enum(group.Key), rows));
        }
    }

    // ---- apply ----

    private async Task ApplyAsync()
    {
        if (current is not { ChangeCount: > 0 } plan || currentCard is null) return;
        var changes = plan.Entries.Where(e => e.Status == ProfileEntryStatus.WillChange).ToList();
        var lines = changes.Select(e => Text.Format("OptimizeChangeLine", TitleOf(e), LabelOf(e.BeforeLabelKey, e.BeforeChoiceId), LabelOf(e.AfterLabelKey, e.Entry.ChoiceId))).ToList();
        var machine = changes.Any(e => e.Entry.Kind == ProfileEntryKind.Machine);
        var confirmation = new ActionConfirmation(Text.Format("ProfileApplyTitle", currentCard.Name), Text.Plural("ProfileApplyBody", changes.Count, changes.Count), lines,
            Text[machine ? "ProfileApplyNoteMachine" : "ProfileApplyNote"], Text.Plural("ProfileApplyPrimary", changes.Count, changes.Count))
        {
            Changes = changes.Select(e => new ConfirmChange(TitleOf(e), LabelOf(e.BeforeLabelKey, e.BeforeChoiceId), LabelOf(e.AfterLabelKey, e.Entry.ChoiceId))).ToList(),
            Risk = changes.Max(e => e.Risk), Undo = ConfirmUndo.WhileUnchanged, Restart = changes.Any(e => e.NeedsRestart), UsesRestorePoint = true,
            HighRiskNames = changes.Where(e => e.Risk == RiskLevel.High).Select(TitleOf).ToList(),
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;

        using var run = Runs.Begin(Text.Format("ACRunProfile", currentCard.Name), true, () => work?.Cancel());
        if (!run.Started) return;
        Busy = true; Applying = true; Notice = null; ProgressValue = 0; ProgressText = Text["ProfileStarting"];
        work?.Cancel();
        work = new CancellationTokenSource();
        var progress = new Progress<ProfileProgress>(p =>
        {
            ProgressValue = p.Total == 0 ? 0 : 100d * p.Done / p.Total;
            run.Report(p.Total == 0 ? null : (double)p.Done / p.Total);
            ProgressText = p.Current is null ? Text["ProfileFinishing"] : Text.Format("ProfileProgress", Math.Min(p.Done + 1, p.Total), p.Total);
        });
        try
        {
            var result = await service.ApplyAsync(plan, progress, work.Token).ConfigureAwait(true);
            ShowResult(plan, result);
            run.Finish(result.Cancelled ? RunResult.Stopped : result.Failed > 0 ? RunResult.Warning : RunResult.Ok, ResultHeading);
        }
        catch (OperationCanceledException) { Notice = Text["ProfileCancelled"]; NoticeIsError = false; OnPropertyChanged(nameof(NoticeIsError)); run.Finish(RunResult.Stopped); }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); run.Finish(RunResult.Error, Text.Format("ACRunFailedText", currentCard.Name)); }
        finally { Applying = false; Busy = false; ProgressText = ""; }
        Journal.Wrote(HistoryArea.Profiles);
        await ReloadStateAsync().ConfigureAwait(true);
    }

    private void ShowResult(ProfilePlan plan, ProfileApplyResult result)
    {
        ResultRows.Clear(); Report = null;
        foreach (var r in result.Entries)
        {
            if (r.Outcome == ProfileEntryOutcome.AlreadySet) continue;
            var title = plan.Entries.FirstOrDefault(e => e.Entry == r.Entry) is { } e ? TitleOf(e) : r.Entry.Id;
            var outcome = Text["ProfileOutcome_" + r.Outcome];
            var detail = r.Outcome is ProfileEntryOutcome.Failed or ProfileEntryOutcome.Skipped or ProfileEntryOutcome.NotRun ? ReasonText(r.Code) : r.NeedsRestart ? Text["ProfileNeedsRestart"] : "";
            ResultRows.Add(new(title, outcome, detail, r.Outcome is ProfileEntryOutcome.Failed));
        }
        ResultHeading = result.Cancelled
            ? Text.Format("ProfileResultCancelled", result.Applied, result.NotRun)
            : result.Failed == 0 ? Text.Plural("ProfileResultDone", result.Applied, result.Applied) : Text.Format("ProfileResultPartial", result.Applied, result.Failed);
        var failures = result.Entries.Where(r => r.Outcome == ProfileEntryOutcome.Failed)
            .Select(r => new ResultFailure(plan.Entries.FirstOrDefault(e => e.Entry == r.Entry) is { } fe ? TitleOf(fe) : r.Entry.Id, ReasonText(r.Code), Text["ReportFailedNext"])).ToList();
        var alreadySet = result.Entries.Count(r => r.Outcome == ProfileEntryOutcome.AlreadySet);
        Report = Feedback.CreateReport(ResultHeading, result.Applied, alreadySet, failures, result.Applied > 0 ? UndoLatestAsync : null, () => CloseCommand.Execute(null));
        var note = result.Entries.Any(r => r.NeedsRestart && r.Outcome == ProfileEntryOutcome.Applied) ? " " + Text["ProfileRestartNote"] : "";
        var rp = result.RestorePoint is { } gate ? RestorePointText.Notice(Text, gate) : null;
        Notice = ResultHeading + note + (rp is null ? "" : " " + rp);
        NoticeIsError = result.Failed > 0;
        OnPropertyChanged(nameof(NoticeIsError));
        // The plan described the state before the run ("Nothing has changed yet"); the report replaces it.
        current = null; currentCard = null; PlanGroups.Clear();
        NotifyPlan();
    }

    /// <summary>Undo all in the report: the review of the newest history entry, which is the run that was just made.</summary>
    private Task UndoLatestAsync() => History.FirstOrDefault() is { } row ? UndoAsync(row) : Task.CompletedTask;

    private async Task ReloadStateAsync()
    {
        // The page stays busy until the cards and the history are read again, so Refresh cannot clear the cards under this loop.
        Busy = true;
        try
        {
            using var cts = new CancellationTokenSource();
            foreach (var card in BuiltInCards.Concat(CustomCards).ToList())
            {
                try { var plan = await service.PlanAsync(card.Profile, cts.Token).ConfigureAwait(true); plans[card.Profile.Id] = plan; card.SetPlan(plan); }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { card.SetPlan(null); }
            }
            await RefreshHistoryAsync(cts.Token).ConfigureAwait(true);
            NotifyChosen();
        }
        catch (OperationCanceledException) { }
        finally { Busy = false; }
    }

    // ---- undo ----

    private async Task UndoAsync(ProfileHistoryRow row)
    {
        // A history row or the report can ask while an apply runs; that apply must finish, not be cancelled by this undo.
        if (Busy) return;
        var confirmation = new ActionConfirmation(Text.Format("ProfileUndoTitle", row.Name), Text.Format("ProfileUndoBody", row.Entry.StillApplied), [row.Description], Text["ProfileUndoNote"], Text["ProfileUndoPrimary"])
        {
            Changes = [new ConfirmChange(row.Description)], Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, UsesRestorePoint = true,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true) || Busy) return;
        using var run = Runs.Begin(Text.Format("ACRunUndo", row.Name), true, () => work?.Cancel());
        if (!run.Started) return;
        Busy = true; undoing = true; Notice = null;
        try
        {
            work?.Cancel(); work = new CancellationTokenSource();
            var result = await service.UndoAsync(row.Entry.Group, work.Token).ConfigureAwait(true);
            ResultRows.Clear(); Report = null;
            foreach (var e in result.Entries.Where(e => e.Outcome != ProfileUndoOutcome.Reverted))
                ResultRows.Add(new(UndoTitleOf(e), Text["ProfileUndoOutcome_" + e.Outcome], ReasonText(e.Code), e.Outcome == ProfileUndoOutcome.Failed));
            Notice = result.Cancelled ? Text["ProfileCancelled"] : result.Left == 0 ? Text.Plural("ProfileUndoneAll", result.Reverted, result.Reverted) : Text.Format("ProfileUndonePartial", result.Reverted, result.Left);
            NoticeIsError = result.Entries.Any(e => e.Outcome == ProfileUndoOutcome.Failed);
            ResultHeading = Notice;
            // The settings left alone are named in the shared report, the same block the apply uses; there is nothing to undo again from it.
            var left = ResultRows.Select(r => new ResultFailure(r.Title, r.HasDetail ? r.Outcome + " " + r.Detail : r.Outcome, null)).ToList();
            Report = Feedback.CreateReport(ResultHeading, result.Reverted, 0, left, null, () => CloseCommand.Execute(null));
            run.Finish(result.Cancelled ? RunResult.Stopped : NoticeIsError ? RunResult.Warning : RunResult.Ok, Notice);
        }
        catch (OperationCanceledException) { Notice = Text["ProfileCancelled"]; NoticeIsError = false; run.Finish(RunResult.Stopped); }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; run.Finish(RunResult.Error, Notice); }
        finally { undoing = false; Busy = false; OnPropertyChanged(nameof(NoticeIsError)); NotifyPlan(); }
        Journal.Wrote(HistoryArea.Profiles);
        await ReloadStateAsync().ConfigureAwait(true);
    }

    // ---- custom profiles ----

    private string FormatText(ProfileFormatException e) => Text.Has("ProfileCode_" + e.Code.Replace('.', '_')) ? Text["ProfileCode_" + e.Code.Replace('.', '_')] : Text["ProfileCode_profile_invalid"];

    private void Fail(string message) { Notice = message; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); }

    private async Task SaveAsync(ProfileDraft draft)
    {
        if (draft.Entries.Count == 0) { Fail(Text["ProfileNothingToSave"]); return; }
        try
        {
            var saved = await Task.Run(() => service.SaveCustom(SaveName, draft.Entries)).ConfigureAwait(true);
            var left = draft.RefusedHighRisk + draft.RefusedUnknown;
            Notice = Text.Format("ProfileSaved", saved.Name!, draft.Entries.Count) + (left > 0 ? " " + Text.Format("ProfileSavedLeftOut", left) : "");
            NoticeIsError = false; OnPropertyChanged(nameof(NoticeIsError));
            SaveName = "";
            await LoadCardsOnlyAsync().ConfigureAwait(true);
        }
        catch (ProfileFormatException e) { Fail(FormatText(e)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(Text["ProfileCode_profile_store_failed"]); }
    }

    private async Task LoadCardsOnlyAsync()
    {
        work?.Cancel(); work = new CancellationTokenSource();
        await LoadAsync(keepNotice: true).ConfigureAwait(true);
    }

    private async Task SavePendingAsync()
    {
        Busy = true; Notice = null;
        try
        {
            var picks = sources.SelectMany(s => s.PendingSelection()).ToList();
            await SaveAsync(service.FromSelection(picks)).ConfigureAwait(true);
        }
        finally { Busy = false; }
    }

    private async Task SaveChangedAsync()
    {
        Busy = true; Notice = null;
        try
        {
            using var cts = new CancellationTokenSource();
            await SaveAsync(await service.CaptureChangedAsync(cts.Token).ConfigureAwait(true)).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException and not ProfileFormatException) { Fail(Text["ActionFailedUnexpected"]); }
        finally { Busy = false; }
    }

    private async Task ImportAsync()
    {
        Busy = true; Notice = null;
        try
        {
            var read = await files.ReadAsync(Text["ProfileImportDialog"], CancellationToken.None).ConfigureAwait(true);
            if (read.Cancelled) return;
            if (read.TooLarge) { Fail(Text["ProfileCode_profile_too_large"]); return; }
            if (read.Failed || read.Text is null) { Fail(Text["ProfileFileUnreadable"]); return; }
            var profile = service.ImportCustom(read.Text);
            Notice = Text.Format("ProfileImported", profile.Name!, profile.Entries.Count);
            NoticeIsError = false; OnPropertyChanged(nameof(NoticeIsError));
            await LoadCardsOnlyAsync().ConfigureAwait(true);
        }
        catch (ProfileFormatException e) { Fail(FormatText(e)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(Text["ProfileCode_profile_store_failed"]); }
        finally { Busy = false; }
    }

    private async Task ExportAsync(ProfileCardRow card)
    {
        Busy = true; Notice = null;
        try
        {
            var written = await files.WriteAsync(Text["ProfileExportDialog"], card.Profile.Id + ".winora-profile.json", service.Export(card.Profile), CancellationToken.None).ConfigureAwait(true);
            if (written is true) { Notice = Text.Format("ProfileExported", card.Name); NoticeIsError = false; OnPropertyChanged(nameof(NoticeIsError)); }
            else if (written is false) Fail(Text["ProfileFileNotWritten"]);
        }
        finally { Busy = false; }
    }

    private async Task DeleteAsync(ProfileCardRow card)
    {
        var confirmation = new ActionConfirmation(Text.Format("ProfileDeleteTitle", card.Name), Text["ProfileDeleteBody"], [card.Name], Text["ProfileDeleteNote"], Text["ProfileDeletePrimary"])
        {
            Changes = [new ConfirmChange(card.Name)], Risk = RiskLevel.Low, Undo = ConfirmUndo.No, Restart = false, Irreversible = true,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        Busy = true;
        try
        {
            service.DeleteCustom(card.Profile.Id);
            Notice = Text.Format("ProfileDeleted", card.Name); NoticeIsError = false; OnPropertyChanged(nameof(NoticeIsError));
            if (currentCard == card) ClosePlan();
            await LoadCardsOnlyAsync().ConfigureAwait(true);
        }
        catch (ProfileFormatException e) { Fail(FormatText(e)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(Text["ProfileCode_profile_store_failed"]); }
        finally { Busy = false; }
    }
}
