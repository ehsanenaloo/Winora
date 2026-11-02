// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Domain;

namespace Winora.Desktop;

public enum FirstRunStep { Welcome, RestorePoint, Profile, Review, Result }

/// <summary>One profile on the "Choose a profile" step. Choosing a card changes nothing.</summary>
public sealed class FirstRunProfileCard : ObservableObject
{
    private readonly LocalizationService text;
    private ProfilePlan? plan;
    private bool selected;

    private readonly Action<FirstRunProfileCard> choose;

    public FirstRunProfileCard(Profile profile, LocalizationService text, Action<FirstRunProfileCard> choose)
    {
        Profile = profile; this.text = text; this.choose = choose;
    }

    public Profile Profile { get; }
    public string Id => Profile.Id;
    public string Name => text[Profile.NameKey!];
    public string Description => text[Profile.DescriptionKey!];
    public bool IsRecommended => Profile.Id == "balanced";
    public string RecommendedText => text["FirstRunProfileRecommended"];
    public string CountText => Profile.Entries.Count == 1 ? text["ProfileCountOne"] : text.Format("ProfileCount", Profile.Entries.Count);
    public string RiskText => plan is null ? text["ProfileChecking"] : text.Format("ProfileRiskSummary", plan.LowRiskChangeCount, plan.MediumRiskChangeCount);
    public string NoHighRiskText => text["ProfileNoHighRisk"];
    /// <summary>The radio button writes true when the person picks the card; the guide then clears the other cards.</summary>
    public bool IsSelected { get => selected; set { if (value && !selected) choose(this); } }
    public void SetSelected(bool value) { if (SetProperty(ref selected, value, nameof(IsSelected))) OnPropertyChanged(nameof(AutomationName)); }
    public string AutomationName => $"{Name}, {Description} {CountText}, {RiskText} {NoHighRiskText}" + (IsRecommended ? ", " + RecommendedText : "");
    public string ChooseHelp => text.Format("FirstRunProfileChooseHelp", Name);
    public ProfilePlan? Plan => plan;
    public void SetPlan(ProfilePlan? value) { plan = value; Refresh(); }
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One change of the plan with the box that keeps or drops it. Nothing is applied until the person presses Apply.</summary>
public sealed class FirstRunRow : ObservableObject
{
    private bool isChecked = true;
    private readonly Action changed;

    public FirstRunRow(ProfilePlanEntry entry, string title, string before, string after, string reason, string risk, string scope, string? restart, Action changed)
    {
        Entry = entry; Title = title; Before = before; After = after; Reason = reason; Risk = risk; Scope = scope; Restart = restart; this.changed = changed;
    }

    public ProfilePlanEntry Entry { get; }
    public string Title { get; }
    public string Before { get; }
    public string After { get; }
    public string Reason { get; }
    public string Risk { get; }
    public string Scope { get; }
    public string? Restart { get; }
    public bool HasRestart => Restart is not null;
    public RiskLevel RiskLevel => Entry.Risk;
    public bool IsChecked { get => isChecked; set { if (SetProperty(ref isChecked, value)) { OnPropertyChanged(nameof(AutomationName)); changed(); } } }
    public string AutomationName => $"{Title}, {Before} - {After}, {Risk}, {Scope}";
}

public sealed class FirstRunGroup(string heading, IReadOnlyList<FirstRunRow> rows)
{
    public string Heading { get; } = heading;
    public IReadOnlyList<FirstRunRow> Rows { get; } = rows;
    public string AutomationName => $"{Heading}, {Rows.Count}";
}

/// <summary>
/// The first-run guide: welcome with language and theme, a plain check of administrator rights and install location, the restore point offer (through the session gate), a profile
/// choice, the review of the plan and the result. It changes nothing before the Apply button of the review step; that button is the one confirmation, and the apply goes through
/// <see cref="ProfileService"/> like every other profile run. Skipping or closing the window changes nothing.
/// </summary>
public sealed class FirstRunViewModel : ObservableObject
{
    public const int StepCount = 5;

    private readonly LocalizationService text;
    private readonly SettingsViewModel settings;
    private readonly ProfileService service;
    private readonly IRestorePointGate? gate;
    private readonly RestorePointService? restorePoints;
    private readonly RestorePointActions actions;
    private readonly IElevationState elevation;
    private readonly IReviewPrompt prompt;
    private readonly Func<FeedbackCenter?> feedback;
    private readonly Action<string> navigate;
    private bool active, busy, applying, protectionOff, canCreate, noProfile, doneMarked;
    private FirstRunStep step;
    private string restoreLine = "", restoreOutcome = "", resultHeading = "", resultBody = "", progressText = "", notice = "";
    private bool restoreOutcomeIsProblem, noticeIsProblem;
    private double progress;
    private FirstRunProfileCard? selected;
    private ProfilePlan? plan;
    private ResultReportModel? report;
    private Guid? lastGroup;
    private CancellationTokenSource? work;
    private Task running = Task.CompletedTask;

    public FirstRunViewModel(LocalizationService text, SettingsViewModel settings, ProfileService service, IRestorePointGate? gate, RestorePointService? restorePoints, RestorePointActions actions,
        IElevationState elevation, IReviewPrompt prompt, Func<FeedbackCenter?> feedback, Action<string> navigate)
    {
        this.text = text; this.settings = settings; this.service = service; this.gate = gate; this.restorePoints = restorePoints; this.actions = actions;
        this.elevation = elevation; this.prompt = prompt; this.feedback = feedback; this.navigate = navigate;
        ContinueCommand = new(() => running = ContinueAsync(), () => CanContinue);
        BackCommand = new(() => running = BackAsync(), () => CanGoBack);
        SkipCommand = new(() => running = SkipAsync(), () => IsActive && !applying);
        CreateRestorePointCommand = new(() => running = CreateRestorePointAsync(), () => step == FirstRunStep.RestorePoint && !busy && canCreate);
        SkipRestorePointCommand = new(() => running = SkipRestorePointAsync(), () => step == FirstRunStep.RestorePoint && !busy);
        OpenProtectionCommand = new(actions.OpenProtection);
        CancelCommand = new(() => work?.Cancel(), () => busy);
        OverviewCommand = new(() => running = FinishAsync("Overview"), () => step == FirstRunStep.Result && !applying);
        ProfilesCommand = new(() => running = FinishAsync("Profiles"), () => step == FirstRunStep.Result && !applying);
        text.PropertyChanged += (_, _) => OnLanguageChanged();
    }

    public SettingsViewModel Settings => settings;
    public LocalizationService Text => text;
    public ObservableCollection<FirstRunProfileCard> Cards { get; } = [];
    public ObservableCollection<FirstRunGroup> Groups { get; } = [];
    public RelayCommand ContinueCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand CreateRestorePointCommand { get; }
    public RelayCommand SkipRestorePointCommand { get; }
    public RelayCommand OpenProtectionCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OverviewCommand { get; }
    public RelayCommand ProfilesCommand { get; }
    public Task WhenIdleAsync() => running;

    // ---- state ----

    /// <summary>A profile run or its undo tells the other pages through this (the shell sets the signal).</summary>
    public JournalReader Journal { get; } = new();
    public bool IsActive { get => active; private set { if (SetProperty(ref active, value)) OnPropertyChanged(nameof(ShowShell)); NotifyCommands(); } }
    /// <summary>The navigation and the page are hidden while the guide shows.</summary>
    public bool ShowShell => !active;
    public FirstRunStep Step { get => step; private set { if (SetProperty(ref step, value)) NotifyStep(); } }
    public int StepNumber => (int)step + 1;
    public string StepLabel => text.Format("FirstRunStepFormat", StepNumber, StepCount);
    public double StepFraction => StepNumber / (double)StepCount;
    public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) NotifyCommands(); } }
    public bool IsApplying { get => applying; private set { if (SetProperty(ref applying, value)) { OnPropertyChanged(nameof(ShowResultBody)); NotifyCommands(); } } }

    public bool IsWelcome => step == FirstRunStep.Welcome;
    public bool IsRestorePoint => step == FirstRunStep.RestorePoint;
    public bool IsProfile => step == FirstRunStep.Profile;
    public bool IsReview => step == FirstRunStep.Review;
    public bool IsResult => step == FirstRunStep.Result;

    public string Title => step switch
    {
        FirstRunStep.Welcome => text["WelcomeTitle"],
        FirstRunStep.RestorePoint => text["FirstRunRestoreTitle"],
        FirstRunStep.Profile => text["FirstRunProfileTitle"],
        FirstRunStep.Review => text["FirstRunReviewTitle"],
        _ => IsApplying ? text["FirstRunApplying"] : resultHeading.Length > 0 ? text["FirstRunResultTitle"] : text["FirstRunResultTitle"],
    };

    public bool CanGoBack => active && !busy && !applying && step is FirstRunStep.RestorePoint or FirstRunStep.Profile or FirstRunStep.Review;
    public bool CanContinue => active && !busy && !applying && step switch
    {
        FirstRunStep.Welcome or FirstRunStep.RestorePoint => true,
        FirstRunStep.Profile => noProfile || selected is not null,
        FirstRunStep.Review => CheckedCount > 0,
        _ => false,
    };

    public string ContinueLabel => step switch
    {
        FirstRunStep.Profile => noProfile ? text["FirstRunContinue"] : text["FirstRunReviewPlan"],
        FirstRunStep.Review => text.Plural("ProfileApplyPrimary", CheckedCount, CheckedCount),
        _ => text["FirstRunContinue"],
    };

    public string ContinueHelp => step == FirstRunStep.Review ? text["FirstRunApplyHelp"] : text["FirstRunContinueHelp"];
    public bool ShowContinue => active && step != FirstRunStep.Result;
    public bool ShowBack => active && step is FirstRunStep.RestorePoint or FirstRunStep.Profile or FirstRunStep.Review;
    public string Notice { get => notice; private set { if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => notice.Length > 0;
    public bool NoticeIsProblem { get => noticeIsProblem; private set => SetProperty(ref noticeIsProblem, value); }

    // ---- step 1: welcome ----

    public string DataFolder => settings.DataFolder;
    public string AdminLine => text[elevation.IsElevated ? "FirstRunAdminYes" : "FirstRunAdminNo"];
    public string LocationLine => text[elevation.UnsafeLocation ? "AppLocationUnsafe" : "FirstRunLocationOk"];
    public IconKey AdminSymbol => elevation.IsElevated ? IconKey.CheckmarkCircle16 : IconKey.Info16;
    public IconKey LocationSymbol => elevation.UnsafeLocation ? IconKey.Warning16 : IconKey.CheckmarkCircle16;
    public bool EnvironmentIsFine => elevation.IsElevated && !elevation.UnsafeLocation;

    // ---- step 2: restore point ----

    public string RestoreLine { get => restoreLine; private set => SetProperty(ref restoreLine, value); }
    public bool ProtectionOff { get => protectionOff; private set => SetProperty(ref protectionOff, value); }
    public bool CanCreateRestorePoint => canCreate;
    public string RestoreOutcome { get => restoreOutcome; private set { if (SetProperty(ref restoreOutcome, value)) OnPropertyChanged(nameof(HasRestoreOutcome)); } }
    public bool HasRestoreOutcome => restoreOutcome.Length > 0;
    public bool RestoreOutcomeIsProblem { get => restoreOutcomeIsProblem; private set => SetProperty(ref restoreOutcomeIsProblem, value); }

    // ---- step 3: profile ----

    /// <summary>The radio button "Don't apply a profile". Picking it clears the cards.</summary>
    public bool NoProfileSelected { get => noProfile; set { if (value && !noProfile) ChooseNoProfile(); } }

    // ---- step 4: review ----

    public ProfilePlan? Plan => plan;
    public int CheckedCount => Groups.SelectMany(g => g.Rows).Count(r => r.IsChecked);
    public int ChangeCount => plan?.ChangeCount ?? 0;
    public string ReviewHeader => plan is null ? "" : plan.SkippedCount > 0
        ? text.Format("FirstRunReviewHeaderSkipped", plan.ChangeCount, plan.AlreadySetCount, plan.SkippedCount)
        : text.Format("FirstRunReviewHeader", plan.ChangeCount, plan.AlreadySetCount);
    public bool NothingToChange => plan is { ChangeCount: 0 };
    public string ReviewProfileName => selected?.Name ?? "";

    // ---- step 5: result ----

    public bool ShowResultBody => step == FirstRunStep.Result && !applying;
    public string ProgressText { get => progressText; private set => SetProperty(ref progressText, value); }
    public double ProgressFraction { get => progress; private set => SetProperty(ref progress, value); }
    public string ResultHeading { get => resultHeading; private set => SetProperty(ref resultHeading, value); }
    public string ResultBody { get => resultBody; private set => SetProperty(ref resultBody, value); }
    public ResultReportModel? Report { get => report; private set { if (SetProperty(ref report, value)) OnPropertyChanged(nameof(HasReport)); } }
    public bool HasReport => report is not null;

    // ---- start and finish ----

    /// <summary>Opens the guide at the first step. Nothing is read or changed besides the page itself.</summary>
    public void Begin()
    {
        work?.Cancel();
        Cards.Clear(); Groups.Clear();
        plan = null; selected = null; noProfile = false; lastGroup = null; Report = null;
        RestoreOutcome = ""; RestoreOutcomeIsProblem = false; Notice = ""; ResultHeading = ""; ResultBody = "";
        doneMarked = false; applying = false; busy = false;
        Step = FirstRunStep.Welcome;
        IsActive = true;
        OnPropertyChanged(string.Empty);
    }

    private async Task SkipAsync()
    {
        work?.Cancel();
        await MarkDoneAsync().ConfigureAwait(true);
        IsActive = false;
    }

    private async Task FinishAsync(string page)
    {
        await MarkDoneAsync().ConfigureAwait(true);
        IsActive = false;
        navigate(page);
    }

    private async Task MarkDoneAsync()
    {
        if (doneMarked) return;
        doneMarked = true;
        await settings.MarkWelcomeSeenAsync().ConfigureAwait(true);
    }

    // ---- navigation between steps ----

    private async Task ContinueAsync()
    {
        Notice = "";
        switch (step)
        {
            case FirstRunStep.Welcome:
                Step = FirstRunStep.RestorePoint;
                await LoadRestoreAsync().ConfigureAwait(true);
                break;
            case FirstRunStep.RestorePoint:
                Step = FirstRunStep.Profile;
                await LoadCardsAsync().ConfigureAwait(true);
                break;
            case FirstRunStep.Profile:
                if (noProfile) { await ShowNoProfileResultAsync().ConfigureAwait(true); break; }
                if (selected is null) break;
                await BuildReviewAsync(selected).ConfigureAwait(true);
                break;
            case FirstRunStep.Review:
                await ApplyAsync().ConfigureAwait(true);
                break;
        }
    }

    private Task BackAsync()
    {
        Notice = "";
        Step = step switch { FirstRunStep.Review => FirstRunStep.Profile, FirstRunStep.Profile => FirstRunStep.RestorePoint, _ => FirstRunStep.Welcome };
        return Task.CompletedTask;
    }

    private void NotifyStep()
    {
        foreach (var name in new[] { nameof(StepNumber), nameof(StepLabel), nameof(StepFraction), nameof(IsWelcome), nameof(IsRestorePoint), nameof(IsProfile), nameof(IsReview), nameof(IsResult), nameof(Title),
                     nameof(ContinueLabel), nameof(ContinueHelp), nameof(ShowContinue), nameof(ShowBack), nameof(ShowResultBody), nameof(CanContinue), nameof(CanGoBack) }) OnPropertyChanged(name);
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanContinue)); OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(ShowContinue)); OnPropertyChanged(nameof(ShowBack));
        foreach (var c in new IRelayCommand[] { ContinueCommand, BackCommand, SkipCommand, CreateRestorePointCommand, SkipRestorePointCommand, CancelCommand, OverviewCommand, ProfilesCommand }) c.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged()
    {
        if (!active) return;
        foreach (var card in Cards) card.Refresh();
        if (plan is not null && step == FirstRunStep.Review && selected is not null) BuildGroups(plan);
        OnPropertyChanged(string.Empty);
    }

    // ---- step 2 ----

    private async Task LoadRestoreAsync()
    {
        RestoreLine = "";
        RestorePointStatus? status = null;
        if (restorePoints is not null)
        {
            try { status = await Task.Run(restorePoints.Status).ConfigureAwait(true); }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { status = null; }
        }
        ProtectionOff = status?.Protection == RestoreProtection.Disabled;
        canCreate = gate is not null && restorePoints is not null && status is { CanCreate: true } && !ProtectionOff;
        RestoreLine = status switch
        {
            null => text["FirstRunRestoreUnknown"],
            { Protection: RestoreProtection.Disabled } => text["FirstRunRestoreOff"],
            { CanCreate: false } => text["FirstRunRestoreNoAdmin"],
            { LastCreatedUtc: { } at } => text.Format("FirstRunRestoreLast", text.When(at.ToLocalTime(), "g")),
            _ => text["FirstRunRestoreNever"],
        };
        OnPropertyChanged(nameof(CanCreateRestorePoint));
        NotifyCommands();
    }

    private async Task CreateRestorePointAsync()
    {
        if (gate is null) return;
        IsBusy = true;
        work = new CancellationTokenSource();
        RestoreOutcome = ""; ProgressText = text["FirstRunRestoreWorking"];
        try
        {
            var result = await gate.BeforeChangeAsync("First run", work.Token).ConfigureAwait(true);
            if (!result.Proceed) { RestoreOutcome = text["FirstRunRestoreCancelled"]; RestoreOutcomeIsProblem = false; return; }
            var line = RestorePointText.Notice(text, result);
            RestoreOutcome = line ?? text[result.Asked ? "FirstRunRestoreSkipped" : "FirstRunRestoreAsked"];
            RestoreOutcomeIsProblem = result.Created is { Ok: false };
        }
        catch (OperationCanceledException) { RestoreOutcome = text["FirstRunRestoreCancelled"]; RestoreOutcomeIsProblem = false; }
        catch (Exception e) when (e is not OutOfMemoryException) { RestoreOutcome = text["ActionFailedUnexpected"]; RestoreOutcomeIsProblem = true; }
        finally { IsBusy = false; ProgressText = ""; }
    }

    private Task SkipRestorePointAsync()
    {
        RestoreOutcome = text["FirstRunRestoreSkipped"]; RestoreOutcomeIsProblem = false;
        Step = FirstRunStep.Profile;
        return LoadCardsAsync();
    }

    // ---- step 3 ----

    private async Task LoadCardsAsync()
    {
        if (Cards.Count > 0) { NotifyCommands(); return; }
        IsBusy = true;
        try
        {
            var catalog = await Task.Run(service.Load).ConfigureAwait(true);
            var order = new[] { "balanced", "speed", "security", "privacy", "gaming", "minimal" };
            foreach (var profile in catalog.BuiltIn.OrderBy(p => Array.IndexOf(order, p.Id) is var i and >= 0 ? i : 99).ThenBy(p => p.Id, StringComparer.Ordinal)) Cards.Add(new(profile, text, Choose));
            Choose(Cards.FirstOrDefault(c => c.IsRecommended) ?? Cards.FirstOrDefault());
            foreach (var card in Cards)
            {
                try { card.SetPlan(await service.PlanAsync(card.Profile, CancellationToken.None).ConfigureAwait(true)); }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { card.SetPlan(null); }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { Notice = text["ProfileLoadErrorBuiltIn"]; NoticeIsProblem = true; }
        finally { IsBusy = false; }
    }

    private void Choose(FirstRunProfileCard? card)
    {
        noProfile = false;
        OnPropertyChanged(nameof(NoProfileSelected));
        selected = card;
        foreach (var c in Cards) c.SetSelected(c == card);
        OnPropertyChanged(nameof(ReviewProfileName));
        NotifyCommands();
        OnPropertyChanged(nameof(CanContinue));
    }

    private void ChooseNoProfile()
    {
        selected = null;
        foreach (var c in Cards) c.SetSelected(false);
        noProfile = true;
        OnPropertyChanged(nameof(NoProfileSelected)); OnPropertyChanged(nameof(ContinueLabel)); OnPropertyChanged(nameof(CanContinue));
        NotifyCommands();
    }

    // ---- step 4 ----

    private async Task BuildReviewAsync(FirstRunProfileCard card)
    {
        IsBusy = true;
        try
        {
            plan = await service.PlanAsync(card.Profile, CancellationToken.None).ConfigureAwait(true);
            BuildGroups(plan);
            Step = FirstRunStep.Review;
            OnPropertyChanged(nameof(Plan)); OnPropertyChanged(nameof(ReviewHeader)); OnPropertyChanged(nameof(NothingToChange)); OnPropertyChanged(nameof(ReviewProfileName));
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { Notice = text["ActionFailedUnexpected"]; NoticeIsProblem = true; }
        finally { IsBusy = false; }
    }

    private string TitleOf(ProfilePlanEntry e) => text.Has(e.TitleKey) ? text[e.TitleKey] : e.Entry.Id;
    private string LabelOf(string? key, string? id) => key is not null && text.Has(key) ? text[key] : id ?? text["ProfileOtherValue"];

    private void BuildGroups(ProfilePlan value)
    {
        Groups.Clear();
        foreach (var group in value.Entries.Where(e => e.Status == ProfileEntryStatus.WillChange).GroupBy(e => e.Category).OrderBy(g => text.Enum(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var rows = group.OrderBy(TitleOf, StringComparer.CurrentCultureIgnoreCase).Select(e => new FirstRunRow(e, TitleOf(e), LabelOf(e.BeforeLabelKey, e.BeforeChoiceId), LabelOf(e.AfterLabelKey, e.Entry.ChoiceId),
                e.Entry.ReasonKey is { } k && text.Has(k) ? text[k] : text["ProfileReasonCustom"], text.Enum(e.Risk), text[e.Entry.Kind == ProfileEntryKind.User ? "ProfileScopeUser" : "ProfileScopeMachine"],
                e.NeedsRestart ? text["BadgeRestart"] : null, OnCheckedChanged)).ToList();
            Groups.Add(new(text.Enum(group.Key), rows));
        }
        OnCheckedChanged();
    }

    private void OnCheckedChanged()
    {
        OnPropertyChanged(nameof(CheckedCount)); OnPropertyChanged(nameof(ContinueLabel)); OnPropertyChanged(nameof(CanContinue));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    // ---- step 5 ----

    private async Task ShowNoProfileResultAsync()
    {
        Report = null; lastGroup = null;
        ResultHeading = text["FirstRunResultNoneTitle"];
        ResultBody = text["FirstRunResultNoProfileBody"];
        Step = FirstRunStep.Result;
        await MarkDoneAsync().ConfigureAwait(true);
    }

    private string ReasonText(string code)
    {
        foreach (var key in new[] { "ProfileCode_" + code.Replace('.', '_'), "OptimizeCode_" + code.Replace('.', '_'), "Code_" + code.Replace('.', '_') })
            if (text.Has(key)) return text[key];
        return text["ProfileCode_profile_failed"];
    }

    private async Task ApplyAsync()
    {
        if (plan is null) return;
        var chosen = Groups.SelectMany(g => g.Rows).Where(r => r.IsChecked).Select(r => r.Entry).ToHashSet();
        var filtered = plan with { Entries = plan.Entries.Where(e => e.Status != ProfileEntryStatus.WillChange || chosen.Contains(e)).ToList() };
        if (filtered.ChangeCount == 0) return;
        Report = null; ResultHeading = ""; ResultBody = "";
        Step = FirstRunStep.Result;
        IsApplying = true; IsBusy = true; ProgressFraction = 0; ProgressText = text["ProfileStarting"];
        work = new CancellationTokenSource();
        var progressSink = new Progress<ProfileProgress>(p =>
        {
            ProgressFraction = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = p.Current is null ? text["ProfileFinishing"] : text.Format("ProfileProgress", Math.Min(p.Done + 1, p.Total), p.Total);
        });
        try
        {
            var result = await service.ApplyAsync(filtered, progressSink, work.Token).ConfigureAwait(true);
            ShowResult(filtered, result);
        }
        catch (OperationCanceledException) { ResultHeading = text["ProfileCancelled"]; ResultBody = text["FirstRunWhereBody"]; }
        catch (Exception e) when (e is not OutOfMemoryException) { ResultHeading = text["ActionFailedUnexpected"]; ResultBody = text["FirstRunWhereBody"]; }
        finally { IsApplying = false; IsBusy = false; ProgressText = ""; }
        Journal.Wrote(HistoryArea.Profiles);
        await MarkDoneAsync().ConfigureAwait(true);
    }

    private void ShowResult(ProfilePlan applied, ProfileApplyResult result)
    {
        lastGroup = result.Group;
        var heading = result.Cancelled
            ? text.Format("ProfileResultCancelled", result.Applied, result.NotRun)
            : result.Failed == 0 ? text.Plural("ProfileResultDone", result.Applied, result.Applied) : text.Format("ProfileResultPartial", result.Applied, result.Failed);
        ResultHeading = heading;
        ResultBody = text["FirstRunWhereBody"];
        var failures = result.Entries.Where(r => r.Outcome == ProfileEntryOutcome.Failed)
            .Select(r => new ResultFailure(applied.Entries.FirstOrDefault(e => e.Entry == r.Entry) is { } fe ? TitleOf(fe) : r.Entry.Id, ReasonText(r.Code), text["ReportFailedNext"])).ToList();
        var already = result.Entries.Count(r => r.Outcome == ProfileEntryOutcome.AlreadySet);
        Func<Task>? undo = result.Applied > 0 ? UndoAsync : null;
        Report = feedback()?.CreateReport(heading, result.Applied, already, failures, undo, () => Report = null) ?? new ResultReportModel(text, heading, result.Applied, already, failures, undo, null, () => Report = null);
    }

    private async Task UndoAsync()
    {
        if (lastGroup is not { } group) return;
        var name = selected?.Name ?? "";
        var confirmation = new ActionConfirmation(text.Format("ProfileUndoTitle", name), text.Format("ProfileUndoBody", Report?.Applied ?? 0), [], text["ProfileUndoNote"], text["ProfileUndoPrimary"])
        {
            Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, UsesRestorePoint = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        IsBusy = true;
        try
        {
            var undone = await service.UndoAsync(group, CancellationToken.None).ConfigureAwait(true);
            // Declining the restore point stops the undo before anything is put back: the report and its Undo button stay.
            if (undone.Cancelled && undone.Reverted == 0) { feedback()?.Warning(text["ProfileCancelled"]); return; }
            var line = undone.Left == 0 ? text.Plural("ProfileUndoneAll", undone.Reverted, undone.Reverted) : text.Format("ProfileUndonePartial", undone.Reverted, undone.Left);
            ResultHeading = line;
            if (undone.Left == 0) { Report = null; lastGroup = null; feedback()?.Success(line); }
            else feedback()?.Warning(line);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { feedback()?.Error(text["ActionFailedUnexpected"]); }
        finally { IsBusy = false; Journal.Wrote(HistoryArea.Profiles); }
    }
}
