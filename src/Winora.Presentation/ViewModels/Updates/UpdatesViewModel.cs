// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop;

/// <summary>A number of days in a combo box. ToString is the label because screen readers read the item text from it.</summary>
public sealed record UpdateDaysItem(int Days, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One mode of the Updates page with its options. A mode that cannot be offered shows why instead of the button.</summary>
public sealed class UpdateModeCard : ObservableObject
{
    private UpdateDaysItem? feature, quality;
    private bool excludeDrivers;
    private bool active;

    public UpdateModeCard(UpdateModeId id, string name, string description, string riskText, bool available, string? unavailableText, bool isActive,
        IReadOnlyList<UpdateDaysItem> featureDays, IReadOnlyList<UpdateDaysItem> qualityDays, RelayCommand<UpdateModeCard> apply, string applyName)
    {
        Id = id; Name = name; Description = description; RiskText = riskText; Available = available; UnavailableText = unavailableText; active = isActive;
        FeatureDays = featureDays; QualityDays = qualityDays; ApplyCommand = apply; ApplyName = applyName;
    }

    public UpdateModeId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string RiskText { get; }
    public RiskLevel Risk { get; init; }
    public bool Available { get; }
    public string? UnavailableText { get; }
    public bool HasUnavailableText => UnavailableText is not null;
    public bool IsAdvanced => UpdateModes.IsAdvanced(Id);
    public bool ShowDays => Available && UpdateModes.OffersDaysOptions(Id);
    public bool ShowDriverOption => Available && UpdateModes.OffersDriverOption(Id);
    public bool IsActive { get => active; internal set { if (SetProperty(ref active, value)) { OnPropertyChanged(nameof(AutomationName)); OnPropertyChanged(nameof(CanPick)); } } }
    public string ApplyName { get; }
    public RelayCommand<UpdateModeCard> ApplyCommand { get; }
    public IReadOnlyList<UpdateDaysItem> FeatureDays { get; }
    public IReadOnlyList<UpdateDaysItem> QualityDays { get; }
    public UpdateDaysItem? SelectedFeatureDays { get => feature; set => SetProperty(ref feature, value); }
    public UpdateDaysItem? SelectedQualityDays { get => quality; set => SetProperty(ref quality, value); }
    public bool ExcludeDrivers { get => excludeDrivers; set => SetProperty(ref excludeDrivers, value); }
    /// <summary>Set for the Pin mode: the release the PC would stay on.</summary>
    public string? ReleaseText { get; init; }
    public bool HasReleaseText => ReleaseText is not null;
    public string ActiveText { get; init; } = "";

    private bool picked;
    /// <summary>The card chosen in the list. Setting it to true picks the mode; the page clears the other cards.</summary>
    public bool IsPicked { get => picked; set { if (value && !picked) Pick?.Invoke(this); } }
    internal void SetPicked(bool value) => SetProperty(ref picked, value, nameof(IsPicked));
    internal Action<UpdateModeCard>? Pick { get; set; }
    /// <summary>A mode that cannot be set and is not in effect cannot be picked.</summary>
    public bool CanPick => Available || IsActive;
    public string AutomationName => IsActive ? $"{Name}, {ActiveText}, {RiskText}" : $"{Name}, {RiskText}";

    public UpdateModeOptions Options => new(SelectedFeatureDays?.Days ?? 365, SelectedQualityDays?.Days ?? 4, ExcludeDrivers);
}

public sealed record UpdatePolicyRowItem(string Text, string SourceText, string AutomationName);

public sealed record UpdateFactItem(string Label, string Value)
{
    public string AutomationName => $"{Label}: {Value}";
}

public sealed class UpdateHistoryRow(UpdateChangeGroup group, string description, string when, bool canRevert, RelayCommand<UpdateHistoryRow> revert)
{
    public UpdateChangeGroup Group { get; } = group;
    public string Description { get; } = description;
    public string When { get; } = when;
    public bool CanRevert { get; } = canRevert;
    public RelayCommand<UpdateHistoryRow> RevertCommand { get; } = revert;
    public string AutomationName => $"{Description}, {When}";
}

/// <summary>
/// The Updates page: modes of Windows Update, each a full set of policy values from the machine catalog, applied as one named group with a preview of every value, one confirmation,
/// a journal line before each write, compare-and-set, read-back and a one-step undo (decision DEC-IW-150). It also shows what Windows is set to: policy values with their source,
/// the build, the last check and install when Windows records them, a pending restart, and a pause set in Windows Settings. Services and the update cache are never touched.
/// </summary>
public sealed class UpdatesViewModel : PageViewModel, INoticePage
{
    private int selectedTab;
    /// <summary>The tab of the page, kept while the app runs.</summary>
    public int SelectedTab { get => selectedTab; set => SetProperty(ref selectedTab, value); }

    private const int HistoryLimit = 20;
    private readonly UpdatesService service;
    private readonly IReviewPrompt prompt;
    private readonly IElevationState elevation;
    private readonly IRestorePointGate? restorePoint;
    private readonly IWindowsSettingsLauncher launcher;
    private Task work = Task.CompletedTask;
    private UpdatesState? state;
    private string? pinRelease;
    private bool busy, loaded, noticeIsError;
    private string? notice;
    private string? restoreNote;
    private string? loadError;

    public UpdatesViewModel(LocalizationService text, SimulationModel model, UpdatesService service, IReviewPrompt prompt, IElevationState elevation, IWindowsSettingsLauncher launcher, IRestorePointGate? restorePoint = null) : base("Updates", text, model)
    {
        HistoryList = new(text, HistoryArea.Optimize, () => OpenHistory, HistoryBatchKind.Updates);
        this.service = service; this.prompt = prompt; this.elevation = elevation; this.launcher = launcher; this.restorePoint = restorePoint;
        RefreshCommand = new(() => work = ReloadAsync(), () => !Busy);
        ApplyMode = new(card => { if (card is not null && CanApplyMode(card)) work = ApplyAsync(card); }, card => card is not null && CanApplyMode(card));
        RevertHistory = new(row => { if (row is not null && !Busy) work = RevertAsync(row); });
        OpenSettingsCommand = new(OpenSettings);
        DiscardCommand = new(Discard, () => PendingCount > 0 && !Busy);
    }

    // ---- the mode picker: the mode cards are one radio group; the picked card shows the before and after view and its options ----

    private UpdateModeCard? pickerCard;
    private IReadOnlyList<RowChoice> modeChoices = [];
    public IReadOnlyList<RowChoice> ModeChoices { get => modeChoices; private set => SetProperty(ref modeChoices, value); }
    public UpdateModeCard? SelectedCard => pickerCard;
    public bool HasPicker => pickerCard is not null;
    public RowChoice? SelectedModeChoice
    {
        get => pickerCard is null ? null : ModeChoices.FirstOrDefault(c => c.Id == pickerCard.Id.ToString());
        set
        {
            var card = value is null ? null : Modes.Concat(AdvancedModes).FirstOrDefault(c => c.Id.ToString() == value.Id);
            if (card is null || ReferenceEquals(card, pickerCard)) return;
            pickerCard = card;
            RaisePicker();
        }
    }
    /// <summary>The mode in effect now, as the "Now" side of the before and after view.</summary>
    public string PickerBefore => state switch
    {
        null => "",
        { CatalogAvailable: false } => Text["UpdatesCurrentUnknown"],
        { ActiveMode: { } m } => ModeName(m),
        { IsCustom: true } => Text["UpdatesCustomShort"],
        _ => ModeName(UpdateModeId.WindowsDefault),
    };
    public string PickerAfter => pickerCard?.Name ?? "";
    public string PickerRiskText => pickerCard?.RiskText ?? "";
    public RiskLevel PickerRisk => pickerCard?.Risk ?? RiskLevel.Low;
    public string PickerDescription => pickerCard?.Description ?? "";
    public string PickerUnavailable => pickerCard?.UnavailableText ?? "";
    public bool PickerChanges => pickerCard is { IsActive: false };
    public string PickerApplyName => pickerCard?.ApplyName ?? Text["UpdatesReviewApply"];
    private RelayCommand? pickerApply;
    public RelayCommand DiscardCommand { get; }

    /// <summary>The staged change: the picked mode when it is not the mode in effect. Its options belong to it and are listed with it.</summary>
    public override int PendingCount => state is not null && pickerCard is { IsActive: false } ? 1 : 0;
    public string ActionBarSummary => Text["ABPendingOne"];
    public override StagingView? Staging => PendingCount == 0 ? null : new(Key,
        [new StagedChange("updates.mode", Text["UpdatesPickerTitle"], PickerBefore, PickerAfter, PickerRisk, () => { if (!Busy) Discard(); })],
        ActionBarSummary, Text["ABReview"], Text["UpdatesApplyHelp"], Text["Discard"], Text["UpdatesDiscardHelp"],
        () => PickerApplyCommand.CanExecute(null), () => PickerApplyCommand.Execute(null), () => DiscardCommand.CanExecute(null), () => DiscardCommand.Execute(null));
    public IReadOnlyList<string> PendingLines
    {
        get
        {
            if (PendingCount == 0 || pickerCard is not { } card) return [];
            var lines = new List<string> { Text.Format("OptimizeChangeLine", Text["UpdatesPickerTitle"], PickerBefore, PickerAfter) };
            if (card.ShowDays)
            {
                lines.Add(Text.Format("ABLineFormat", Text["UpdatesFeatureDays"], card.SelectedFeatureDays?.Label ?? ""));
                lines.Add(Text.Format("ABLineFormat", Text["UpdatesQualityDays"], card.SelectedQualityDays?.Label ?? ""));
            }
            if (card.ShowDriverOption && card.ExcludeDrivers) lines.Add(Text["UpdatesExcludeDrivers"]);
            return lines;
        }
    }

    /// <summary>Puts the picker back on the mode in effect and the options back to their defaults.</summary>
    private void Discard()
    {
        if (state is null) return;
        pickerCard = null;
        Rebuild(state);
        Notice = null;
    }

    public RelayCommand PickerApplyCommand => pickerApply ??= new(() => { if (pickerCard is not null && CanApplyMode(pickerCard)) work = ApplyAsync(pickerCard); }, () => pickerCard is not null && CanApplyMode(pickerCard));

    private void RaisePicker()
    {
        foreach (var card in Modes.Concat(AdvancedModes)) card.SetPicked(ReferenceEquals(card, pickerCard));
        foreach (var n in new[] { nameof(SelectedCard), nameof(HasPicker), nameof(SelectedModeChoice), nameof(PickerBefore), nameof(PickerAfter), nameof(PickerRiskText), nameof(PickerRisk), nameof(PickerDescription), nameof(PickerUnavailable), nameof(PickerChanges), nameof(PickerApplyName), nameof(PendingCount), nameof(ActionBarSummary), nameof(PendingLines) }) OnPropertyChanged(n);
        PickerApplyCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged();
    }

    private void BuildPicker()
    {
        var all = Modes.Concat(AdvancedModes).Where(c => c.Available || c.IsActive).ToList();
        var keep = pickerCard?.Id;
        ModeChoices = all.Select(c => new RowChoice(c.Id.ToString(), c.Name)).ToList();
        pickerCard = all.FirstOrDefault(c => c.Id == keep) ?? all.FirstOrDefault(c => c.IsActive) ?? all.FirstOrDefault();
        RaisePicker();
    }

    public ObservableCollection<UpdateModeCard> Modes { get; } = [];
    public ObservableCollection<UpdateModeCard> AdvancedModes { get; } = [];
    public ObservableCollection<UpdatePolicyRowItem> PolicyRows { get; } = [];
    public ObservableCollection<UpdateFactItem> Facts { get; } = [];
    public ObservableCollection<UpdateHistoryRow> History { get; } = [];
    public PageHistoryModel HistoryList { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand<UpdateModeCard> ApplyMode { get; }
    public RelayCommand<UpdateHistoryRow> RevertHistory { get; }
    public RelayCommand OpenSettingsCommand { get; }

    public override bool IsBusy => Busy;
    private bool Busy { get => busy; set { if (SetProperty(ref busy, value)) { OnPropertyChanged(nameof(IsBusy)); Notify(); } } }
    public bool IsElevated => elevation.IsElevated;
    public bool ShowNeedsAdministrator => !elevation.IsElevated;
    public string NeedsAdministratorTitle => elevation.UnsafeLocation ? Text["AppLocationUnsafeTitle"] : Text["AdminNoticeTitle"];
    public string NeedsAdministratorText => elevation.UnsafeLocation ? Text["AppLocationUnsafe"] : Text["AdminNoticeMessage"];
    public bool ShowAdminHowTo => !elevation.UnsafeLocation;
    public string? Notice { get => notice; private set { if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool NoticeIsError { get => noticeIsError; private set { if (SetProperty(ref noticeIsError, value)) OnPropertyChanged(nameof(NoticeSeverity)); } }
    public BannerSeverity NoticeSeverity => noticeIsError ? BannerSeverity.Warning : BannerSeverity.Success;
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool HasAdvanced => AdvancedModes.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool HasPolicyRows => PolicyRows.Count > 0;
    public bool ShowNoPolicyRows => loaded && PolicyRows.Count == 0;
    public bool ShowCatalogMissing => loaded && state is { CatalogAvailable: false };
    public string? LoadError => loadError;
    public bool HasLoadError => loadError is not null;
    public bool ShowLoading => Busy && !loaded;
    public bool ShowContent => !ShowLoading && !HasLoadError;

    // ---- banners and the current mode ----

    public bool ShowOrganization => state is { ManagedByOrganization: true };
    public bool ShowPaused => state is { Pause.IsPaused: true };
    public string PausedText => state is null ? "" : string.Join(" ", new[]
    {
        state.Pause.SecurityUntilUtc is { } s ? Text.Format("UpdatesPausedSecurity", FormatDate(s)) : null,
        state.Pause.FeatureUntilUtc is { } f && state.Pause.SecurityUntilUtc is null ? Text.Format("UpdatesPausedFeature", FormatDate(f)) : null,
    }.Where(x => x is not null));
    public bool ShowRestart => state is { Facts.PendingRestartSources.Count: > 0 };
    public string CurrentModeText => state switch
    {
        null => "",
        { CatalogAvailable: false } => Text["UpdatesCurrentUnknown"],
        { ActiveMode: { } m } => Text.Format("UpdatesCurrentMode", ModeName(m)),
        { IsCustom: true } => Text["UpdatesCurrentCustom"],
        _ => Text.Format("UpdatesCurrentMode", ModeName(UpdateModeId.WindowsDefault)),
    };

    private string FormatDate(DateTimeOffset value) => Text.When(value.ToLocalTime(), "d");
    private string FormatTime(DateTimeOffset value) => Text.When(value.ToLocalTime(), "g");
    private string ModeName(UpdateModeId id) => Text["UpdatesMode_" + id];

    private void Notify()
    {
        RefreshCommand.NotifyCanExecuteChanged(); ApplyMode.NotifyCanExecuteChanged(); pickerApply?.NotifyCanExecuteChanged();
        foreach (var n in new[] { nameof(HasAdvanced), nameof(HasHistory), nameof(HasPolicyRows), nameof(ShowNoPolicyRows), nameof(ShowCatalogMissing), nameof(ShowLoading), nameof(ShowContent), nameof(HasLoadError), nameof(LoadError), nameof(ShowOrganization), nameof(ShowPaused), nameof(PausedText), nameof(ShowRestart), nameof(CurrentModeText) })
            OnPropertyChanged(n);
    }

    public override void Activate() { if ((!loaded || Journal.IsStale) && !Busy) work = ReloadAsync(); }
    public override Task WhenIdleAsync() => work;
    public override System.Windows.Input.ICommand? ShortcutRefresh() => RefreshCommand;
    protected override void OnLanguageChanged() { base.OnLanguageChanged(); if (loaded && state is not null) Rebuild(state); }

    // ---- loading ----

    private async Task ReloadAsync()
    {
        Journal.MarkRead();
        Busy = true;
        try
        {
            (state, pinRelease) = await Task.Run(() => (service.Read(), service.PinTargetRelease())).ConfigureAwait(true);
            loadError = null;
            loaded = true;
            Rebuild(state);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            loadError = Text["UpdatesLoadFailed"]; loaded = true;
        }
        finally { Busy = false; Notify(); }
        RefreshHistory();
    }

    private void Rebuild(UpdatesState s)
    {
        Modes.Clear(); AdvancedModes.Clear(); PolicyRows.Clear(); Facts.Clear();
        var release = pinRelease;
        foreach (var id in UpdateModes.Normal.Concat(UpdateModes.Advanced))
        {
            var info = s.Modes.FirstOrDefault(m => m.Id == id);
            var card = MakeCard(id, info, s, release);
            card.Pick = c => SelectedModeChoice = ModeChoices.FirstOrDefault(x => x.Id == c.Id.ToString());
            card.PropertyChanged += (_, _) => { if (ReferenceEquals(card, pickerCard)) OnPropertyChanged(nameof(PendingLines)); };
            (card.IsAdvanced ? AdvancedModes : Modes).Add(card);
        }
        foreach (var r in s.PolicyRows)
        {
            var text = $"{r.Name} = {r.Data}";
            var source = Text["UpdatesSource_" + r.Source];
            PolicyRows.Add(new(text, source, $"{text}, {source}"));
        }
        BuildFacts(s);
        BuildPicker();
        OnPropertyChanged(nameof(HasPolicyRows)); Notify();
    }

    private UpdateModeCard MakeCard(UpdateModeId id, UpdateModeState? info, UpdatesState s, string? release)
    {
        var available = s.CatalogAvailable && info is { Available: true };
        string? why = !s.CatalogAvailable ? ProblemText(OptimizeCode.NoCatalog) : info?.UnavailableCode is { } code ? ProblemText(code) : null;
        var feature = UpdateModeOptions.FeatureDaysSteps.Select(d => new UpdateDaysItem(d, Text.Format("UpdatesDays", d))).ToList();
        var quality = UpdateModeOptions.QualityDaysSteps.Select(d => new UpdateDaysItem(d, Text.Format("UpdatesDays", d))).ToList();
        var riskLevel = id == UpdateModeId.AutomaticOff ? RiskLevel.High : id == UpdateModeId.WindowsDefault ? RiskLevel.Low : RiskLevel.Medium;
        var risk = Text.Enum(riskLevel);
        var name = ModeName(id);
        var description = id != UpdateModeId.Pin ? Text["UpdatesModeDesc_" + id] : release is not null ? Text.Format("UpdatesModeDesc_Pin", release) : Text["UpdatesModeDesc_PinNoRelease"];
        var card = new UpdateModeCard(id, name, description, risk, available, why, info?.IsActive == true,
            feature, quality, ApplyMode, Text.Format("UpdatesApplyNamed", name)) { Risk = riskLevel, ActiveText =Text["UpdatesActive"], ReleaseText = id == UpdateModeId.Pin && release is not null ? Text.Format("UpdatesPinRelease", release) : null };
        var options = s.ActiveMode == id ? s.ActiveOptions : null;
        var defaults = new UpdateModeOptions();
        card.SelectedFeatureDays = feature.FirstOrDefault(d => d.Days == (options?.FeatureDays ?? defaults.FeatureDays));
        card.SelectedQualityDays = quality.FirstOrDefault(d => d.Days == (options?.QualityDays ?? defaults.QualityDays));
        card.ExcludeDrivers = options?.ExcludeDrivers ?? false;
        return card;
    }

    private void BuildFacts(UpdatesState s)
    {
        var none = Text["UpdatesFactsNotAvailable"];
        Facts.Add(new(Text["UpdatesFactsBuild"], s.Build is null ? none : s.UpdateRevision is { } ubr ? $"{s.Build}.{ubr}" : s.Build));
        Facts.Add(new(Text["UpdatesFactsRelease"], s.DisplayVersion ?? none));
        Facts.Add(new(Text["UpdatesFactsLastCheck"], s.Facts.LastCheckUtc is { } check ? FormatTime(check) : none));
        Facts.Add(new(Text["UpdatesFactsLastInstall"], s.Facts.LastInstallUtc is { } install ? FormatTime(install) : none));
        Facts.Add(new(Text["UpdatesFactsRestart"], s.Facts.PendingRestartSources.Count == 0 ? Text["UpdatesNo"] : string.Join(", ", s.Facts.PendingRestartSources.Select(x => Text["UpdatesRestartSource_" + x]))));
        Facts.Add(new(Text["UpdatesFactsLatest"], s.Facts.GetUpdatesAsSoonAsAvailable is { } on ? Text[on ? "UpdatesOn" : "UpdatesOff"] : Text["UpdatesNotSet"]));
    }

    public string ProblemText(string code)
    {
        var key = "UpdatesCode_" + code.Replace('.', '_');
        if (Text.Has(key)) return Text[key];
        key = "OptimizeCode_" + code.Replace('.', '_');
        return Text.Has(key) ? Text[key] : Text["OptimizeCode_optimize_failed"];
    }

    // ---- apply ----

    private bool CanApplyMode(UpdateModeCard card) => IsElevated && !Busy && card.Available && !card.IsActive;

    private string LabelOf(MachineSettingDefinition d, string? choiceId, StoredValue? value)
    {
        var choice = choiceId is null ? null : d.Choices.FirstOrDefault(c => c.Id == choiceId);
        if (choice is null && value?.ToValue() is { } v) choice = d.Choices.FirstOrDefault(c => c.EffectiveValue is { } ev && ev.Equals(v));
        return choice is null ? Text["OptimizeOtherValue"] : Text.Has(choice.LabelKey) ? Text[choice.LabelKey] : choice.Id;
    }

    private string SettingTitle(MachineSettingDefinition d) => Text.Has(d.TitleKey) ? Text[d.TitleKey] : d.Id;

    private async Task ApplyAsync(UpdateModeCard card)
    {
        Notice = null;
        var options = card.Options;
        var preview = await Task.Run(() => service.Preview(card.Id, options)).ConfigureAwait(true);
        if (preview.IsRefused) { Fail(preview.RefusalCode!, preview.RefusalDefinitionId); return; }
        if (!preview.WillChange) { Notice = Text["UpdatesAlready"]; NoticeIsError = false; OnPropertyChanged(nameof(NoticeIsError)); return; }
        var changing = preview.Lines.Where(l => l.WillChange).ToList();
        var lines = changing.Select(l => Text.Format("OptimizeChangeLine", SettingTitle(l.Definition), LabelOf(l.Definition, l.BeforeChoiceId, l.Before), LabelOf(l.Definition, l.AfterChoiceId, null))).ToList();
        var confirmation = new ActionConfirmation(Text.Format("UpdatesApplyTitle", card.Name), Text["UpdatesApplyBody"], lines, Text["UpdatesWarn_" + card.Id] + " " + Text["PowerUndoNote"], Text["UpdatesApplyPrimary"])
        {
            Changes = changing.Select(l => new ConfirmChange(SettingTitle(l.Definition), LabelOf(l.Definition, l.BeforeChoiceId, l.Before), LabelOf(l.Definition, l.AfterChoiceId, null))).ToList(),
            Risk = card.Risk, Undo = ConfirmUndo.WhileUnchanged, Restart = changing.Any(l => l.Definition.RequiresRestart), UsesRestorePoint = restorePoint is not null,
            HighRiskNames = card.IsAdvanced ? [card.Name] : [],
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        using var run = Runs.Begin(Text.Format("ACRunApply", Title), true);
        if (!run.Started) return;
        if (!await GateAsync().ConfigureAwait(true)) return;
        Busy = true;
        try
        {
            var result = await service.ApplyAsync(card.Id, options).ConfigureAwait(true);
            if (result.Ok)
            {
                Notice = Text.Plural("UpdatesApplied", result.Applied, card.Name, result.Applied) + Suffix();
                NoticeIsError = false;
            }
            else
            {
                Notice = (result.Applied > 0 ? Text.Format("UpdatesPartial", result.Applied, ProblemText(result.Code)) : ProblemText(result.Code)) + Suffix();
                NoticeIsError = true;
            }
            OnPropertyChanged(nameof(NoticeIsError));
            run.Finish(result.Ok ? RunResult.Ok : RunResult.Warning, Notice);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title)); }
        finally { Busy = false; }
        Journal.Wrote(HistoryArea.Optimize);
        await ReloadAsync().ConfigureAwait(true);
    }

    private string Suffix() => restoreNote is null ? "" : " " + restoreNote;

    private async Task<bool> GateAsync()
    {
        restoreNote = null;
        if (restorePoint is null) return true;
        var gate = await restorePoint.BeforeChangeAsync("Updates").ConfigureAwait(true);
        if (!gate.Proceed) return false;
        restoreNote = RestorePointText.Notice(Text, gate);
        return true;
    }

    private void Fail(string code, string? definitionId)
    {
        var text = ProblemText(code);
        Notice = definitionId is null ? text : Text.Format("UpdatesRefusedFor", DefinitionTitle(definitionId), text);
        NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError));
    }

    private string DefinitionTitle(string id) => Text.Has("Machine_" + id.Replace('.', '_') + "_Title") ? Text["Machine_" + id.Replace('.', '_') + "_Title"] : id;

    // ---- undo and history ----

    private async Task RevertAsync(UpdateHistoryRow row)
    {
        var confirmation = new ActionConfirmation(Text["UpdatesRevertTitle"], Text["UpdatesRevertBody"], [row.Description], Text["PowerUndoNote"], Text["PowerRevertPrimary"])
        {
            Changes = [new ConfirmChange(row.Description)], Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        using var run = Runs.Begin(Text.Format("ACRunRevert", Title), true);
        if (!run.Started) return;
        if (!await GateAsync().ConfigureAwait(true)) return;
        Busy = true; Notice = null;
        try
        {
            var result = await service.RevertAsync(row.Group.Group).ConfigureAwait(true);
            if (result.Ok && result.Skipped == 0) { Notice = Text.Plural("UpdatesReverted", result.Reverted, result.Reverted) + Suffix(); NoticeIsError = false; }
            else if (result.Ok) { Notice = Text.Format("UpdatesRevertedPartial", result.Reverted, result.Skipped) + Suffix(); NoticeIsError = false; }
            else { Notice = ProblemText(result.Code) + Suffix(); NoticeIsError = true; }
            OnPropertyChanged(nameof(NoticeIsError));
            run.Finish(result.Ok ? RunResult.Ok : RunResult.Warning, Notice);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title)); }
        finally { Busy = false; }
        Journal.Wrote(HistoryArea.Optimize);
        pickerCard = null; // the mode in effect changed: the picker follows it
        await ReloadAsync().ConfigureAwait(true);
    }

    private void RefreshHistory()
    {
        History.Clear();
        var shown = new List<HistoryRowViewModel>();
        IReadOnlyList<UpdateChangeGroup> groups;
        try { groups = service.History(HistoryLimit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { groups = []; }
        foreach (var g in groups)
        {
            var name = g.Mode is { } m ? ModeName(m) : Text["OptimizeOtherValue"];
            var line = Text.Plural("UpdatesHistoryLine", g.Changes, name, g.Changes) + (g.IsUndo ? " " + Text["PowerUndoSuffix"] : "");
            History.Add(new(g, line, FormatTime(g.AtUtc), IsElevated && g.CanRevert, RevertHistory));
            var h = History[^1];
            shown.Add(HistoryList.Row(g.Group, g.AtUtc, HistoryResult.Applied, h.Description, h.CanRevert, h.RevertCommand, h, new(Text["PowerUndo"], Text["UpdatesUndoHelp"])));
        }
        HistoryList.Set(shown);
        OnPropertyChanged(nameof(HasHistory));
    }

    private void OpenSettings()
    {
        if (!launcher.OpenWindowsUpdate()) { Notice = Text["UpdatesOpenSettingsFailed"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); }
    }
}
