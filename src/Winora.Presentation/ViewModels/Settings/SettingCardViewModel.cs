// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>One selectable authored choice of a setting. The label follows the language.</summary>
public sealed class ChoiceItem(SettingChoice choice, SettingFormat format) : ObservableObject
{
    public string Id { get; } = choice.Id;
    public string Label => format.ChoiceLabel(choice);
    public void Refresh() => OnPropertyChanged(nameof(Label));
}

public enum CardAvailability { Ready, UnsupportedBuild, Unreadable, Unknown }

/// <summary>
/// One catalog setting as a card. Selecting a choice only stages it; nothing is written until the review dialog is confirmed.
/// A card whose build is unsupported or whose current value could not be read offers no way to change it.
/// </summary>
public sealed class SettingCardViewModel : ObservableObject
{
    private readonly SettingsModel model;
    private readonly LocalizationService text;
    private SettingState state;
    private string? stagedChoiceId;

    public SettingCardViewModel(SettingState state, SettingsModel model, LocalizationService text, Func<SettingCardViewModel, Task> reviewAsync)
    {
        this.state = state; this.model = model; this.text = text;
        Choices = state.Definition.Choices.Select(c => new ChoiceItem(c, model.Format)).ToList();
        ReviewCommand = new(() => reviewAsync(this), () => CanReview);
        DiscardCommand = new(() => StagedChoiceId = null, () => IsChangeStaged && !model.IsBusy);
        ResetCommand = new(ResetAsync, () => CanReset);
        RevertCommand = new(RevertAsync, () => CanRevert);
        model.Changed += (_, _) => NotifyCommands();
    }

    public LocalizationService Text => text;
    public SettingState State => state;
    public SettingDefinition Definition => state.Definition;
    public string Id => Definition.Id;
    public IReadOnlyList<ChoiceItem> Choices { get; }
    public bool IsTwoChoice => Choices.Count == 2;
    public bool HasManyChoices => Choices.Count > 2;

    public AsyncRelayCommand ReviewCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public AsyncRelayCommand RevertCommand { get; }

    public string Title => model.Format.Title(Definition);
    public string Description => model.Format.Description(Definition);
    public string CategoryLabel => model.Format.Category(Definition.Category);
    public string Location => SettingFormat.Location(Definition);
    /// <summary>A caution for rows whose value Windows may set back; empty for the others.</summary>
    public bool HasConditionalNote => Definition.Recovery == RecoveryKind.Conditional;
    public string ConditionalNote => HasConditionalNote ? text["SetConditionalNote"] : "";

    public CardAvailability Availability => state.Compatibility switch
    {
        SettingCompatibility.UnsupportedBuild => CardAvailability.UnsupportedBuild,
        SettingCompatibility.Unknown => CardAvailability.Unknown,
        _ => state.Issue is not null ? CardAvailability.Unreadable : CardAvailability.Ready,
    };
    public bool IsReady => Availability == CardAvailability.Ready;
    public bool IsNotReady => !IsReady;
    public bool CanChoose => IsReady && !model.IsBusy;

    /// <summary>The choice that is in effect: the matched one, or the default when the value is absent and documented as behaving like it.</summary>
    public string? EffectiveChoiceId => SettingFormat.EffectiveChoice(Definition, state.Actual, state.MatchedChoiceId);
    /// <summary>No value is stored. Distinct from a stored value that matches no authored choice.</summary>
    public bool IsNotSet => IsReady && SettingSnapshots.IsUnset(Definition, state.Actual) && state.MatchedChoiceId is null;
    /// <summary>Not set, and the catalog documents that Windows then behaves like the default choice.</summary>
    public bool IsWindowsDefaultNotSet => IsNotSet && EffectiveChoiceId is not null;
    /// <summary>Only a stored value that matches no authored choice is a custom value.</summary>
    public bool IsCustom => IsReady && !IsNotSet && state.MatchedChoiceId is null;
    public bool HasCurrentHint => IsNotSet;
    public string CurrentHint => !IsNotSet ? "" : IsWindowsDefaultNotSet ? text["SetWindowsDefaultHint"] : text["SetNotSetHint"];

    /// <summary>The label of the value in effect: a choice, "Not set" (no documented default) or "Custom value" (stored, unrecognized).</summary>
    public string CurrentLabel => !IsReady ? "" : IsNotSet && EffectiveChoiceId is null ? text["SetNotSet"] : model.Format.Choice(Definition, EffectiveChoiceId);
    public string CurrentText => IsReady ? text.Format("SetCurrentFormat", CurrentLabel) + (HasCurrentHint ? ", " + CurrentHint : "") : "";

    public string StatusTitle => Availability switch
    {
        CardAvailability.UnsupportedBuild => text["SetUnsupportedTitle"],
        CardAvailability.Unreadable => text["SetUnreadableTitle"],
        CardAvailability.Unknown => text["SetUnknownTitle"],
        _ => ""
    };
    public string StatusDetail => Availability switch
    {
        CardAvailability.UnsupportedBuild => BuildRequirement() is { } req ? $"{ErrorText(WinoraError.From(ErrorCode.SettingUnsupportedBuild))} {req}" : ErrorText(WinoraError.From(ErrorCode.SettingUnsupportedBuild)),
        CardAvailability.Unreadable => ErrorText(state.Issue!.Error),
        CardAvailability.Unknown => text["SetUnknownBody"],
        _ => ""
    };
    private string? BuildRequirement() => (Definition.MinBuild, Definition.MaxBuild) switch
    {
        ({ } min, { } max) => text.Format("SetBuildRange", min, max),
        ({ } min, null) => text.Format("SetBuildMin", min),
        (null, { } max) => text.Format("SetBuildMax", max),
        _ => null
    };
    private string ErrorText(WinoraError error) => text.Has(error.UserMessageKey) ? text[error.UserMessageKey] : text[ErrorCatalog.ResourceKey(ErrorCode.Unexpected)];

    // ---- Staging ----
    public string? StagedChoiceId
    {
        get => stagedChoiceId;
        set
        {
            if (value is not null && (!IsReady || Choices.All(c => c.Id != value))) value = null;
            if (value == EffectiveChoiceId) value = null; // choosing what is already in effect stages nothing
            if (!SetProperty(ref stagedChoiceId, value)) return;
            OnPropertyChanged(nameof(SelectedChoice)); OnPropertyChanged(nameof(IsChangeStaged)); OnPropertyChanged(nameof(StagedText));
            OnPropertyChanged(nameof(Badges)); OnPropertyChanged(nameof(AutomationName)); OnPropertyChanged(nameof(StageStatusText));
            StagedChanged?.Invoke(this, EventArgs.Empty);
            NotifyCommands();
        }
    }
    public event EventHandler? StagedChanged;
    public bool IsChangeStaged => stagedChoiceId is not null;
    public string StagedText => stagedChoiceId is null ? "" : text.Format("SetStagedFormat", model.Format.Choice(Definition, stagedChoiceId));
    public string StageStatusText => IsChangeStaged ? StagedText : IsReady ? text["SetNotStaged"] : "";

    /// <summary>The selector shows the staged choice, or the current one. Null clears are ignored: a refresh must never stage a change.</summary>
    public ChoiceItem? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Id == (stagedChoiceId ?? EffectiveChoiceId));
        set { if (value is not null && IsReady && !model.IsBusy) StagedChoiceId = value.Id; else OnPropertyChanged(nameof(SelectedChoice)); }
    }

    public void Update(SettingState next)
    {
        state = next;
        if (stagedChoiceId is not null && (stagedChoiceId == EffectiveChoiceId || !IsReady)) { stagedChoiceId = null; StagedChanged?.Invoke(this, EventArgs.Empty); }
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var c in Choices) c.Refresh();
        NotifyCommands();
    }

    // ---- Badges and names ----
    public IReadOnlyList<BadgeModel> Badges
    {
        get
        {
            var list = new List<BadgeModel>
            {
                BadgeModel.ForRisk(model.Format.Risk(Definition.Risk), Definition.Risk),
                new(model.Format.Recovery(Definition.Recovery), Definition.Recovery == RecoveryKind.Reversible ? Tone.Info : Tone.Caution),
            };
            if (Definition.RequiresExplorerRestart) list.Add(new(text["SetBadgeRestartExplorer"], Tone.Caution));
            if (Definition.RequiresSignOut) list.Add(new(text["SetBadgeSignOut"], Tone.Caution));
            if (!Definition.RequiresExplorerRestart && !Definition.RequiresSignOut) list.Add(new(text["SetBadgeNoRestart"], Tone.Neutral));
            if (Availability == CardAvailability.UnsupportedBuild) list.Add(new(text["SetBadgeUnsupported"], Tone.Danger));
            else if (Availability == CardAvailability.Unreadable) list.Add(new(text["SetBadgeUnreadable"], Tone.Danger));
            else if (Availability == CardAvailability.Unknown) list.Add(new(text["SetBadgeUnknown"], Tone.Caution));
            if (IsCustom) list.Add(new(text["SetCustomValue"], Tone.Neutral));
            else if (IsNotSet) list.Add(new(IsWindowsDefaultNotSet ? text["SetWindowsDefaultHint"] : text["SetNotSet"], Tone.Neutral));
            if (IsChangeStaged) list.Add(new(text["StatusStaged"], Tone.Info));
            return list;
        }
    }
    public string AutomationName => $"{Title}, {(IsReady ? CurrentLabel : StatusTitle)}" + (IsWindowsDefaultNotSet ? ", " + CurrentHint : "") + (IsChangeStaged ? ", " + StagedText : "");

    // ---- Flows (each ends in the review dialog; none applies directly) ----
    public bool CanReview => IsReady && IsChangeStaged && !model.IsBusy;
    public bool CanReset => IsReady && !model.IsBusy && EffectiveChoiceId != Definition.DefaultChoiceId;
    public SettingOutcome? RevertTarget => model.RevertCandidate(Id);
    public bool CanRevert => RevertTarget is not null && !model.IsBusy;
    public string RevertHelp => CanRevert ? text["SetRevertHelp"] : text["SetRevertUnavailableHelp"];
    public string ResetHelp => text.Format("SetResetHelp", model.Format.Choice(Definition, Definition.DefaultChoiceId));

    private Task ResetAsync() => !CanReset ? Task.CompletedTask : model.ReviewAndApplyAsync([new(Id, Definition.DefaultChoiceId)], ChangeKind.Reset);
    private Task RevertAsync() => RevertTarget is { } t ? model.ReviewAndRevertAsync(t.OperationId) : Task.CompletedTask;

    private void NotifyCommands()
    {
        ReviewCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged(); ResetCommand.NotifyCanExecuteChanged(); RevertCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanChoose)); OnPropertyChanged(nameof(CanRevert)); OnPropertyChanged(nameof(RevertHelp)); OnPropertyChanged(nameof(CanReview)); OnPropertyChanged(nameof(CanReset));
    }
}
