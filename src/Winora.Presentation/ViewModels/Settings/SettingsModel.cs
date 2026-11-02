// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

public enum ChangeKind { Apply, Reset, Revert, Bulk }

/// <summary>One requested change: a catalog setting and one of its authored choices. Never a path or a raw value.</summary>
public sealed record ChangeRequest(string SettingId, string ChoiceId);

/// <summary>One row of the review dialog. Location is a registry location shown as an LTR island.</summary>
public sealed record ChangeReviewItem(string SettingName, string Before, string After, string Arrow, string Location, string Risk, Tone RiskTone,
    string Recovery, string? RestartNote, bool WillChange, bool NeedsExplorer = false, bool NeedsSignOut = false, string? WriteNote = null, RecoveryKind? RecoveryValue = null)
{
    public RiskLevel RiskLevel => RiskTone switch { Tone.Success => RiskLevel.Low, Tone.Caution => RiskLevel.Medium, _ => RiskLevel.High };
    public bool HasRestartNote => RestartNote is not null;
    /// <summary>Set when the value is not stored now and applying creates it: the dialog must say that a value will be written.</summary>
    public bool HasWriteNote => WriteNote is not null;
    public bool IsNoChange => !WillChange;
}

/// <summary>Everything the review dialog shows. Applying is only possible by confirming this exact list.</summary>
public sealed record ChangeReview(ChangeKind Kind, IReadOnlyList<ChangeReviewItem> Items)
{
    public bool IsBulk => Items.Count > 1;
    public bool HasExplorerRestart => Items.Any(i => i.NeedsExplorer);
    public bool HasSignOut => Items.Any(i => i.NeedsSignOut);
    /// <summary>The dialog's "Restart Explorer so this takes effect now" choice. Checked by default; the dialog writes it back before confirming.</summary>
    public bool RestartExplorer { get; set; } = true;
}

/// <summary>Resource-backed names and values for catalog definitions.</summary>
public sealed class SettingFormat(LocalizationService text)
{
    private string Resource(string key, string fallback) => text.Has(key) ? text[key] : fallback;
    public string Title(SettingDefinition d) => Resource(d.TitleKey, d.Id);
    public string Description(SettingDefinition d) => Resource(d.DescriptionKey, "");
    public string ChoiceLabel(SettingChoice c) => Resource(c.LabelKey, c.Id);
    public string Choice(SettingDefinition d, string? choiceId) =>
        choiceId is null ? text["SetCustomValue"] : d.Choices.FirstOrDefault(c => c.Id == choiceId) is { } c ? ChoiceLabel(c) : text["SetCustomValue"];
    /// <summary>
    /// True when the authored source note documents that an absent value behaves as the default choice ("Absent behaves as..." or
    /// "absent on a default profile"). Only then does the UI show the default choice as current for an unset value; other unset values show "Not set".
    /// </summary>
    public static bool AbsentMeansDefault(SettingDefinition d) =>
        d.SourceNote.Contains("Absent behaves as", StringComparison.OrdinalIgnoreCase) || d.SourceNote.Contains("absent on a default profile", StringComparison.OrdinalIgnoreCase);

    /// <summary>The choice the user effectively has: the matched one, or the default when the value is absent and documented as behaving like it.</summary>
    public static string? EffectiveChoice(SettingDefinition d, RegistrySnapshot actual, string? matched) =>
        matched ?? (SettingSnapshots.IsUnset(d, actual) && AbsentMeansDefault(d) ? d.DefaultChoiceId : null);

    /// <summary>Label of the live value for the review dialog: a choice, the default marked as not set, "Not set" or "Custom value".</summary>
    public string LiveLabel(SettingDefinition d, RegistrySnapshot actual, string? matched)
    {
        if (matched is not null) return Choice(d, matched);
        if (!SettingSnapshots.IsUnset(d, actual)) return text["SetCustomValue"];
        return AbsentMeansDefault(d) ? text.Format("SetDefaultNotSetFormat", Choice(d, d.DefaultChoiceId)) : text["SetNotSet"];
    }

    /// <summary>Technical rendering of the value a choice would write, for example "DWORD 1". Never translated.</summary>
    public static string ValueText(RegistryValueSpec value) => SettingLocations.Describe(value);

    public string Category(SettingCategory category) => text.Enum(category);
    public string Risk(RiskLevel risk) => text.Enum(risk);
    public string Recovery(RecoveryKind recovery) => text.Enum(recovery);
    public static Tone RiskTone(RiskLevel risk) => risk switch { RiskLevel.Low => Tone.Success, RiskLevel.Medium => Tone.Caution, _ => Tone.Danger };

    /// <summary>HKCU-relative location written in full for the review. Technical text, never translated.</summary>
    public static string Location(SettingDefinition d) => SettingLocations.Of(d);

    /// <summary>Plain statement of what must happen before the change is visible. Winora never restarts or signs out anything.</summary>
    public string? RestartNote(bool explorer, bool signOut) => (explorer, signOut) switch
    {
        (true, true) => text["SetRestartBoth"],
        (true, false) => text["SetRestartExplorer"],
        (false, true) => text["SetRestartSignOut"],
        _ => null
    };
}

/// <summary>
/// UI-thread-owned view of the real user-scope settings: states, history and the single operation that may be in flight. Every call into
/// <see cref="ISettingsService"/> runs off the UI thread with a cancellation token. Nothing is written without the review dialog being confirmed,
/// and Apply always uses the plan that was reviewed. Failures are classified, logged once and shown only through <see cref="ErrorPresenter"/> text.
/// </summary>
public sealed class SettingsModel : ObservableObject, IDisposable
{
    private readonly ISettingsService service;
    private readonly ErrorPresenter errors;
    private readonly LocalizationService text;
    private readonly IReviewPrompt prompt;
    private readonly IWindowsSession session;
    private readonly ILogger<SettingsModel> logger;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? current;
    private bool busy, awaitingReview, recovered, staleAfterRun;
    private JournalSignal? journal;
    private IReadOnlyList<SettingState> states = [];
    private IReadOnlyList<SettingOutcome> history = [];

    /// <summary>Shows the one read-only details dialog.</summary>
    public Task ShowDetailsAsync(DetailsDialog details) => prompt.ShowDetailsAsync(details);

    public SettingsModel(ISettingsService service, ErrorPresenter errors, LocalizationService text, IReviewPrompt prompt, IWindowsSession session, ILogger<SettingsModel> logger)
    {
        this.session = session; this.service = service; this.errors = errors; this.text = text; this.prompt = prompt; this.logger = logger;
        Format = new SettingFormat(text);
        text.PropertyChanged += (_, _) => { Notice.Refresh(); Raise(); };
    }

    public SettingFormat Format { get; }
    public SettingsNotice Notice { get; } = new();
    public IReadOnlyList<SettingState> States { get => states; private set => SetProperty(ref states, value); }
    /// <summary>Real settings outcomes, newest first.</summary>
    public IReadOnlyList<SettingOutcome> History { get => history; private set => SetProperty(ref history, value); }
    public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) OnPropertyChanged(nameof(ShowProgress)); } }
    /// <summary>True while the review dialog is open: the single-operation gate stays closed but no progress is shown.</summary>
    public bool AwaitingReview { get => awaitingReview; private set { if (SetProperty(ref awaitingReview, value)) { OnPropertyChanged(nameof(ShowProgress)); Raise(); } } }
    public bool ShowProgress => busy && !awaitingReview;
    public bool Loaded { get; private set; }
    /// <summary>Set when the first read failed; the page then shows a failure state with Try again instead of an empty catalog.</summary>
    public WinoraError? LoadError { get; private set; }
    public event EventHandler? Changed;

    public SettingState? StateOf(string settingId) => states.FirstOrDefault(s => s.Definition.Id == settingId);
    public SettingDefinition? DefinitionOf(string settingId) => StateOf(settingId)?.Definition;
    public bool IsRevertible(SettingOutcome o) => o.State is OperationState.Applied or OperationState.PendingRestart && StateOf(o.SettingId) is not null;
    /// <summary>The newest outcome of a setting that can still be reverted.</summary>
    public SettingOutcome? RevertCandidate(string settingId) => history.FirstOrDefault(o => o.SettingId == settingId && IsRevertible(o));

    /// <summary>Runs the notice's own button (Review again, Apply now, Sign out now) with the same failure handling as every other action.</summary>
    public async Task RunNoticeActionAsync(bool secondary)
    {
        try { await (secondary ? Notice.RunSecondaryAsync() : Notice.RunActionAsync()); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = errors.Report(ex, "Notice action");
            if (error is not null) Notice.Show(BannerSeverity.Error, () => text["ErrorTitleFailed"], () => $"{errors.Message(error)} {errors.Reference(error)}", error.Code);
        }
    }

    /// <summary>Asks the running operation to stop. The model re-reads current values afterwards and never reports a cancelled write as success.</summary>
    public void Cancel() { try { current?.Cancel(); } catch (ObjectDisposedException) { } }

    /// <summary>Where the read and the apply register their runs in the Action center (page Customize). The shell sets the service.</summary>
    public RunSource Runs { get; } = new("Customize");

    /// <summary>Reconciles interrupted operations once, then reads every setting and the history.</summary>
    public Task LoadAsync() => RunAsync("Settings read", async token =>
    {
        if (!recovered)
        {
            recovered = true;
            try { await Task.Run(() => service.RecoverAsync(token), token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Report(ex, "Settings recovery"); }
        }
        await ReadCoreAsync(token);
    }, retry: LoadAsync, resetLoadError: true);

    public Task RefreshAsync() => RunAsync("Settings read", ReadCoreAsync, retry: RefreshAsync, resetLoadError: true);

    /// <summary>
    /// The shared signal. A write here is told to the other pages. A profile run (Profiles page, first run, or its undo on the History page) writes user settings through the service directly, so then
    /// the model reads the settings and the history again; the notice of the page is kept.
    /// </summary>
    public JournalSignal? Journal
    {
        get => journal;
        set
        {
            if (journal is not null) journal.Changed -= OnJournalChanged;
            journal = value;
            if (value is not null) value.Changed += OnJournalChanged;
        }
    }

    private void OnJournalChanged(HistoryArea area)
    {
        if (area != HistoryArea.Profiles || !Loaded) return;
        if (busy) { staleAfterRun = true; return; }
        readAgain = ReadAgainAsync();
    }

    private Task readAgain = Task.CompletedTask;
    /// <summary>Completes when the read that a profile run started has ended (tests wait for it).</summary>
    public Task WhenReadAgainAsync() => readAgain;

    private Task ReadAgainAsync() => RunAsync("Settings read", ReadCoreAsync, retry: RefreshAsync, resetLoadError: true, keepNotice: true);

    private async Task ReadCoreAsync(CancellationToken token)
    {
        var read = await Task.Run(() => service.ReadAllAsync(token), token);
        var outcomes = await Task.Run(() => service.HistoryAsync(token), token);
        States = read;
        History = outcomes.OrderByDescending(o => o.AtUtc).ToList();
        Loaded = true; LoadError = null;
    }

    /// <summary>Plans every request, shows ONE review dialog, then applies the reviewed plans one after another. Stops at the first failure.</summary>
    public Task ReviewAndApplyAsync(IReadOnlyList<ChangeRequest> requests, ChangeKind kind)
    {
        if (requests.Count == 0) return Task.CompletedTask;
        return RunAsync("Settings apply", async token =>
        {
            var plans = new List<(SettingPlan Plan, SettingDefinition Definition)>();
            // Reading the current values for the review is a read-only run of its own, so it can be stopped from the Action center.
            using (var planning = Runs.Begin(text.Format("ACRunRead", text["Customize"]), false, Cancel))
            {
                try
                {
                    foreach (var request in requests)
                    {
                        var definition = DefinitionOf(request.SettingId) ?? throw new WinoraException(WinoraError.From(ErrorCode.SettingUnknown));
                        var plan = await Task.Run(() => service.PlanAsync(request.SettingId, request.ChoiceId, token), token);
                        plans.Add((plan, definition));
                    }
                }
                finally { planning.Finish(token.IsCancellationRequested ? RunResult.Stopped : RunResult.Ok, quiet: true); }
            }
            var review = new ChangeReview(requests.Count > 1 ? ChangeKind.Bulk : kind, plans.Select(p => ReviewItem(p.Plan, p.Definition)).ToList());
            if (!await ConfirmAsync(review, token)) return; // nothing was written
            token.ThrowIfCancellationRequested();
            await ApplyReviewedAsync(plans, review.RestartExplorer, token);
            await ReReadAfterFailureAsync();
        }, retry: null);
    }

    /// <summary>Review, then revert one recorded operation. Revert compares with the journal and never overwrites a newer outside change.</summary>
    public Task ReviewAndRevertAsync(Guid operationId)
    {
        RevertConfirmed = false;
        return RunAsync("Settings revert", async token =>
        {
            var outcome = history.FirstOrDefault(o => o.OperationId == operationId);
            var definition = outcome is null ? null : DefinitionOf(outcome.SettingId);
            if (outcome is null || definition is null) throw new WinoraException(WinoraError.From(ErrorCode.OperationMissing));
            var live = StateOf(definition.Id);
            var before = live is null ? Format.Choice(definition, null) : Format.LiveLabel(definition, live.Actual, live.MatchedChoiceId);
            var after = outcome.BeforeChoiceId is null ? text["SetPreviousValue"] : Format.Choice(definition, outcome.BeforeChoiceId);
            var item = new ChangeReviewItem(Format.Title(definition), before, after, text["ArrowGlyph"], SettingFormat.Location(definition), Format.Risk(definition.Risk),
                SettingFormat.RiskTone(definition.Risk), Format.Recovery(definition.Recovery), Format.RestartNote(definition.RequiresExplorerRestart, definition.RequiresSignOut), true, false, false, null, definition.Recovery);
            if (!await ConfirmAsync(new ChangeReview(ChangeKind.Revert, [item]), token)) return;
            RevertConfirmed = true;
            token.ThrowIfCancellationRequested();
            var result = await Task.Run(() => service.RevertAsync(operationId, token), token);
            logger.LogInformation("Settings revert {OperationId} finished with {State}", result.OperationId, result.State);
            PresentOutcome(result, definition, reverted: true);
            await ReReadAfterFailureAsync();
        }, retry: null);
    }

    /// <summary>The person confirmed the last single undo (false when the review was declined or never shown).</summary>
    public bool RevertConfirmed { get; private set; }

    /// <summary>
    /// Review and undo several recorded operations with ONE confirmation that lists every reversed value. They are undone one after another and the run stops at the first one that
    /// cannot be undone; nothing is overwritten when something else changed the value since.
    /// </summary>
    public Task ReviewAndRevertManyAsync(IReadOnlyList<Guid> operationIds)
    {
        if (operationIds.Count == 0) return Task.CompletedTask;
        if (operationIds.Count == 1) return ReviewAndRevertAsync(operationIds[0]);
        return RunAsync("Settings revert", async token =>
        {
            var targets = new List<(SettingOutcome Outcome, SettingDefinition Definition)>();
            foreach (var id in operationIds)
            {
                var outcome = history.FirstOrDefault(o => o.OperationId == id);
                var definition = outcome is null ? null : DefinitionOf(outcome.SettingId);
                if (outcome is null || definition is null) throw new WinoraException(WinoraError.From(ErrorCode.OperationMissing));
                targets.Add((outcome, definition));
            }
            var items = targets.Select(t =>
            {
                var live = StateOf(t.Definition.Id);
                var before = live is null ? Format.Choice(t.Definition, null) : Format.LiveLabel(t.Definition, live.Actual, live.MatchedChoiceId);
                var after = t.Outcome.BeforeChoiceId is null ? text["SetPreviousValue"] : Format.Choice(t.Definition, t.Outcome.BeforeChoiceId);
                return new ChangeReviewItem(Format.Title(t.Definition), before, after, text["ArrowGlyph"], SettingFormat.Location(t.Definition), Format.Risk(t.Definition.Risk),
                    SettingFormat.RiskTone(t.Definition.Risk), Format.Recovery(t.Definition.Recovery), Format.RestartNote(t.Definition.RequiresExplorerRestart, t.Definition.RequiresSignOut), true, false, false, null, t.Definition.Recovery);
            }).ToList();
            if (!await ConfirmAsync(new ChangeReview(ChangeKind.Revert, items), token)) return;
            var undone = 0;
            (SettingDefinition Definition, SettingOutcome Outcome)? stopped = null;
            foreach (var (outcome, definition) in targets)
            {
                token.ThrowIfCancellationRequested();
                var result = await Task.Run(() => service.RevertAsync(outcome.OperationId, token), token);
                logger.LogInformation("Settings revert {OperationId} finished with {State}", result.OperationId, result.State);
                if (result.State is OperationState.Reverted or OperationState.Applied or OperationState.PendingRestart) { undone++; continue; }
                stopped = (definition, result);
                break;
            }
            if (stopped is null)
            {
                Feedback?.ShowReport(text.Format("ReportUndoneFormat", undone, targets.Count), undone, 0, []);
                Notice.Show(BannerSeverity.Success, () => text["StateReverted"], () => text.Format("SetNoticeRevertedMany", undone), "bulk.reverted");
            }
            else
            {
                var name = Format.Title(stopped.Value.Definition);
                var code = stopped.Value.Outcome.Code;
                Notice.Show(BannerSeverity.Warning, () => text["SetBulkStoppedTitle"], () => text.Format("SetRevertStopped", undone, targets.Count, name, ReasonText(code, null)), code);
                Feedback?.ShowReport(text.Format("ReportUndoneFormat", undone, targets.Count), undone, 0, [new ResultFailure(name, ReasonText(code, null), text["ReportFailedNext"])]);
            }
            await ReReadAfterFailureAsync();
        }, retry: null);
    }

    /// <summary>The shell's feedback center. Multi-item results are shown as a report; the notice stays the record for assistive technology and tests.</summary>
    public FeedbackCenter? Feedback { get; set; }

    private ChangeReviewItem ReviewItem(SettingPlan plan, SettingDefinition d)
    {
        var before = Format.LiveLabel(d, plan.Observed, plan.CurrentChoiceId);
        var after = Format.Choice(d, plan.ChoiceId);
        // The engine decides AlreadyConfigured from the stored snapshot. An absent value differs from every authored choice, so choosing the
        // default of an unset value still writes a value; the dialog says so instead of implying that nothing is stored.
        string? writeNote = null;
        if (plan.WillChange && SettingSnapshots.IsUnset(d, plan.Observed) && d.Choices.FirstOrDefault(c => c.Id == plan.ChoiceId) is { } choice)
        {
            var sameBehavior = SettingFormat.AbsentMeansDefault(d) && plan.ChoiceId == d.DefaultChoiceId;
            writeNote = text.Format(sameBehavior ? "SetReviewWritesSame" : "SetReviewCreates", SettingLocations.Names(d), SettingFormat.ValueText(choice.Value));
        }
        return new(Format.Title(d), before, after, text["ArrowGlyph"], SettingFormat.Location(d), Format.Risk(d.Risk), SettingFormat.RiskTone(d.Risk),
            Format.Recovery(d.Recovery), Format.RestartNote(d.RequiresExplorerRestart, d.RequiresSignOut), plan.WillChange, d.RequiresExplorerRestart, d.RequiresSignOut, writeNote, d.Recovery);
    }

    private async Task<bool> ConfirmAsync(ChangeReview review, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        AwaitingReview = true;
        try { return await prompt.ConfirmChangesAsync(review); }
        finally { AwaitingReview = false; }
    }

    private async Task ApplyReviewedAsync(List<(SettingPlan Plan, SettingDefinition Definition)> plans, bool restartExplorer, CancellationToken token)
    {
        // Bulk choice: every item broadcasts the change, but Explorer is restarted once, with the last item that needs it, so open File Explorer
        // windows close at most once. Earlier Explorer-dependent items stay PendingRestart in the journal; that restart makes them effective too.
        using var run = Runs.Begin(text.Format("ACRunApply", text["Customize"]), true, Cancel);
        if (!run.Started) return;
        var lastExplorer = plans.FindLastIndex(p => p.Definition.RequiresExplorerRestart);
        var index = -1;
        var done = new List<(SettingOutcome Outcome, SettingDefinition Definition)>();
        (SettingDefinition Definition, WinoraError? Error, SettingOutcome? Outcome)? stop = null;
        foreach (var (plan, definition) in plans)
        {
            index++;
            if (token.IsCancellationRequested) { stop = (definition, WinoraError.From(ErrorCode.Cancelled), null); break; }
            var options = new SettingApplyOptions(true, restartExplorer && index == lastExplorer);
            try
            {
                var result = await Task.Run(() => service.ApplyAsync(plan, options, token), token);
                logger.LogInformation("Settings apply {OperationId} finished with {State}", result.OperationId, result.State);
                done.Add((result, definition));
                if (result.State is OperationState.Failed or OperationState.Partial or OperationState.Unsupported) { stop = (definition, null, result); break; }
            }
            catch (OperationCanceledException) { stop = (definition, WinoraError.From(ErrorCode.Cancelled), null); break; }
            catch (Exception ex)
            {
                var error = errors.Report(ex, "Settings apply") ?? WinoraError.From(ErrorCode.Cancelled);
                stop = (definition, error, null);
                break;
            }
        }

        var failedOutcome = done.Any(d => d.Outcome.State is OperationState.Failed or OperationState.Partial or OperationState.Unsupported);
        if (stop is null && !failedOutcome) run.Finish(RunResult.Ok, text.Format("ACRunApplied", text["Customize"], done.Count));
        else if (stop is { Error.Code: ErrorCode.Cancelled }) run.Finish(RunResult.Stopped);
        else run.Finish(stop is { Error: not null } ? RunResult.Error : RunResult.Warning, text.Format("ACRunPartial", text["Customize"], done.Count, plans.Count));

        if (plans.Count == 1)
        {
            if (done.Count == 1) PresentOutcome(done[0].Outcome, done[0].Definition, reverted: false);
            else if (stop is { Error: { } e } s) PresentError(e, s.Definition);
            return;
        }
        PresentBulk(plans, done, stop);
    }

    private void PresentBulk(List<(SettingPlan Plan, SettingDefinition Definition)> plans, List<(SettingOutcome Outcome, SettingDefinition Definition)> done,
        (SettingDefinition Definition, WinoraError? Error, SettingOutcome? Outcome)? stop)
    {
        var applied = done.Count(d => d.Outcome.State is OperationState.Applied or OperationState.AlreadyConfigured or OperationState.PendingRestart or OperationState.Reverted);
        var pendingRestart = done.Any(d => d.Outcome.State == OperationState.PendingRestart);
        if (stop is null)
        {
            Feedback?.ShowReport(text.Format("ReportHeadlineFormat", applied, plans.Count), applied, 0, []);
            Notice.Show(BannerSeverity.Success, () => text["SetBulkDoneTitle"],
                () => text.Format("SetBulkDone", applied) + (pendingRestart ? " " + text["SetBulkRestartNote"] : ""), "bulk.applied");
            return;
        }
        var notAttempted = plans.Count - done.Count - (stop.Value.Error is not null ? 1 : 0);
        var total = plans.Count;
        var name = Format.Title(stop.Value.Definition);
        var reasonCode = stop.Value.Error?.Code ?? stop.Value.Outcome!.Code;
        var error = stop.Value.Error;
        var reference = error?.CorrelationId;
        Notice.Show(BannerSeverity.Warning, () => text["SetBulkStoppedTitle"],
            () => text.Format("SetBulkStopped", applied, total, name, ReasonText(reasonCode, reference), Math.Max(0, notAttempted)), reasonCode);
    }

    private string ReasonText(string code, string? reference)
    {
        if (code == ErrorCode.Cancelled) return text["SetCancelledShort"];
        var error = errors.ForCode(ErrorCatalog.IsKnown(code) ? code : ErrorCode.Unexpected);
        var message = errors.Message(error);
        return reference is null ? message : $"{message} {text.Format("ErrorReferenceFormat", reference)}";
    }

    private void PresentError(WinoraError error, SettingDefinition definition)
    {
        var severity = error.Severity is ErrorSeverity.Warning or ErrorSeverity.Info ? BannerSeverity.Warning : BannerSeverity.Error;
        if (error.Code == ErrorCode.Cancelled)
        {
            Notice.Show(BannerSeverity.Informational, () => text["SetCancelledTitle"], () => text["SetCancelled"], error.Code);
            return;
        }
        Func<Task>? action = null; Func<string>? label = null;
        if (ErrorPresenter.OffersReview(error)) { label = () => text["ReviewAgain"]; action = () => ReviewAgainAsync(definition.Id); }
        else if (error.Retryable) { label = () => text["TryAgain"]; action = () => ReviewAgainAsync(definition.Id); }
        Notice.Show(severity, () => text[severity == BannerSeverity.Warning ? "ErrorTitleWarning" : "ErrorTitleFailed"],
            () => $"{errors.Message(error)} {errors.Reference(error)}", error.Code, label, action);
    }

    /// <summary>Raised by Notice actions so the page can rebuild the same request; the model only knows which setting needs a new review.</summary>
    public event Action<string>? ReviewAgainRequested;
    private Task ReviewAgainAsync(string settingId)
    {
        Notice.Clear();
        ReviewAgainRequested?.Invoke(settingId);
        return Task.CompletedTask;
    }

    /// <summary>Turns one recorded outcome into honest text: applied, already configured, pending restart, external conflict or failure.</summary>
    private void PresentOutcome(SettingOutcome outcome, SettingDefinition definition, bool reverted)
    {
        var name = Format.Title(definition);
        var code = outcome.Code;
        switch (outcome.State)
        {
            case OperationState.PendingRestart:
                PresentPending(outcome, reverted, name);
                break;
            case OperationState.Applied or OperationState.Reverted:
                Notice.Show(BannerSeverity.Success, () => text[reverted ? "StateReverted" : "StateApplied"],
                    () => reverted ? text.Format("SetNoticeReverted", name) : text.Format("SetNoticeApplied", name, Format.Choice(definition, outcome.AfterChoiceId)), code);
                break;
            case OperationState.AlreadyConfigured:
                Notice.Show(BannerSeverity.Informational, () => text["StateAlreadyConfigured"], () => text.Format("SetNoticeAlreadyConfigured", name), code);
                break;
            default:
                var conflict = code == ErrorCode.ExternalConflict || code == ErrorCode.Superseded || code == ErrorCode.PlanStale || code == ErrorCode.ResourceChanged;
                var severity = outcome.State == OperationState.Partial || conflict ? BannerSeverity.Warning : BannerSeverity.Error;
                Func<string> title = () => code == ErrorCode.ExternalConflict ? text["SetConflictTitle"] : text["State" + outcome.State];
                Func<string> message = () => code == ErrorCode.ExternalConflict
                    ? text.Format("SetConflictBody", name)
                    : text.Format("SetNoticeNotChanged", name) + " " + ReasonText(code, null);
                Func<string>? label = null; Func<Task>? action = null;
                if (conflict && code != ErrorCode.Superseded && !reverted) { label = () => text["ReviewAgain"]; action = () => ReviewAgainAsync(definition.Id); }
                Notice.Show(severity, title, message, code, label, action);
                break;
        }
    }

    private void PresentPending(SettingOutcome outcome, bool reverted, string name)
    {
        Func<string>? label = null, label2 = null; Func<Task>? action = null, action2 = null;
        // "Apply now" re-runs only the fixed shell refresh for this operation, after its own confirmation.
        if (outcome.NeedsExplorerRestart && !reverted) { label = () => text["SetApplyNow"]; action = () => CompleteAsync(outcome.OperationId); }
        if (outcome.NeedsSignOut) { label2 = () => text["SetSignOutNow"]; action2 = SignOutAsync; }
        var code = outcome.Code;
        Notice.Show(BannerSeverity.Informational, () => text["StatePendingRestart"], () => PendingText(name, outcome, reverted) + FailureSuffix(code), code, label, action, label2, action2);
    }

    private string FailureSuffix(string code)
    {
        if (ErrorCatalog.IsKnown(code)) return " " + ReasonText(code, null);
        return code.StartsWith("shell.", StringComparison.Ordinal) ? " " + text.Format("SetShellFailedFormat", code) : "";
    }

    /// <summary>Runs the shell refresh left over from an earlier apply, after the user confirms the Explorer warning.</summary>
    public Task CompleteAsync(Guid operationId) => RunAsync("Settings complete", async token =>
    {
        var outcome = history.FirstOrDefault(o => o.OperationId == operationId);
        var definition = outcome is null ? null : DefinitionOf(outcome.SettingId);
        if (outcome is null || definition is null) throw new WinoraException(WinoraError.From(ErrorCode.OperationMissing));
        AwaitingReview = true;
        bool ok;
        try { ok = await prompt.ConfirmExplorerRestartAsync(); } finally { AwaitingReview = false; }
        if (!ok) { PresentPending(outcome, false, Format.Title(definition)); return; } // declined: the pending notice and its button stay
        token.ThrowIfCancellationRequested();
        var result = await Task.Run(() => service.CompleteAsync(operationId, token), token);
        logger.LogInformation("Settings complete {OperationId} finished with {State}", result.OperationId, result.State);
        PresentOutcome(result, definition, reverted: false);
        await ReReadAfterFailureAsync();
    }, retry: null);

    /// <summary>Ends the user's session only after an explicit confirmation; never part of an apply.</summary>
    public async Task SignOutAsync()
    {
        if (busy) return;
        if (!await prompt.ConfirmSignOutAsync()) return;
        try { if (!await session.SignOutAsync()) Notice.Show(BannerSeverity.Warning, () => text["ErrorTitleWarning"], () => text["SetSignOutFailed"], "session.signout_failed"); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = errors.Report(ex, "Sign out");
            if (error is not null) Notice.Show(BannerSeverity.Error, () => text["ErrorTitleFailed"], () => $"{errors.Message(error)} {errors.Reference(error)}", error.Code);
        }
    }

    private string PendingText(string name, SettingOutcome o, bool reverted) => (o.NeedsExplorerRestart, o.NeedsSignOut) switch
    {
        (true, true) => text.Format(reverted ? "SetPendingRevertBoth" : "SetPendingBoth", name),
        (true, false) => text.Format(reverted ? "SetPendingRevertExplorer" : "SetPendingExplorer", name),
        (false, true) => text.Format(reverted ? "SetPendingRevertSignOut" : "SetPendingSignOut", name),
        _ => text.Format(reverted ? "SetPendingRevertGeneric" : "SetPendingGeneric", name),
    };

    /// <summary>Runs one operation at a time. Cancellation and failures never throw out of the model; the notice says what happened.</summary>
    private async Task RunAsync(string context, Func<CancellationToken, Task> work, Func<Task>? retry, bool resetLoadError = false, bool keepNotice = false)
    {
        if (busy) return;
        using var readRun = context == "Settings read" ? Runs.Begin(text.Format("ACRunRead", text["Customize"]), false, Cancel) : ActivityRun.None;
        IsBusy = true;
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        current = source;
        if (!keepNotice) Notice.Clear();
        Raise();
        try { await work(source.Token); readRun.Finish(RunResult.Ok, quiet: true); }
        catch (OperationCanceledException)
        {
            readRun.Finish(RunResult.Stopped, quiet: true);
            if (!lifetime.IsCancellationRequested) Notice.Show(BannerSeverity.Informational, () => text["SetCancelledTitle"], () => text["SetCancelled"], ErrorCode.Cancelled);
            await ReReadAfterFailureAsync();
        }
        catch (Exception ex)
        {
            readRun.Finish(RunResult.Error, quiet: true);
            var error = errors.Report(ex, context);
            if (error is not null)
            {
                if (resetLoadError && !Loaded) LoadError = error;
                var severity = error.Severity is ErrorSeverity.Warning or ErrorSeverity.Info ? BannerSeverity.Warning : BannerSeverity.Error;
                Func<string>? label = null; Func<Task>? action = null;
                if (retry is not null && error.Retryable) { label = () => text["TryAgain"]; action = () => { Notice.Clear(); return retry(); }; }
                else if (ErrorPresenter.OffersReview(error) && context == "Settings apply") { label = () => text["DiscRefresh"]; action = () => { Notice.Clear(); return RefreshAsync(); }; }
                Notice.Show(severity, () => text[severity == BannerSeverity.Warning ? "ErrorTitleWarning" : "ErrorTitleFailed"],
                    () => $"{errors.Message(error)} {errors.Reference(error)}", error.Code, label, action);
            }
            await ReReadAfterFailureAsync();
        }
        finally
        {
            current = null; source.Dispose();
            IsBusy = false; AwaitingReview = false; Raise();
            if (context != "Settings read") journal?.Raise(HistoryArea.Settings);
        }
        if (staleAfterRun && !lifetime.IsCancellationRequested) { staleAfterRun = false; await (readAgain = ReadAgainAsync()); }
    }

    /// <summary>After a failure or cancellation the shown values are re-read so the page never keeps a guess about the registry.</summary>
    private async Task ReReadAfterFailureAsync()
    {
        if (lifetime.IsCancellationRequested || !Loaded) return;
        try { await ReadCoreAsync(lifetime.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Settings could not be re-read after a failure"); }
        catch (OperationCanceledException) { }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
