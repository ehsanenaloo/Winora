// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop;

/// <summary>Text shared by the row adapters.</summary>
internal static class RowText
{
    public static string SourceName(LocalizationService text, SourceClass? source) => source switch
    {
        SourceClass.A => text["FbSourceA"], SourceClass.B => text["FbSourceB"], SourceClass.C => text["FbSourceC"], _ => "",
    };

    public static string RiskDetail(LocalizationService text, string settingKey, RiskLevel risk)
    {
        if (risk == RiskLevel.Low) return "";
        var specific = $"Setting_{settingKey}_Risk";
        if (text.Has(specific)) return text[specific];
        return text[risk == RiskLevel.High ? "CfHighRiskConsequence" : "DetailsMediumRisk"];
    }

    public static string Automation(LocalizationService text, string title, bool available, string current, string status, string risk, string source, string restart) =>
        available ? text.Format("RowAutoFormat", title, current, risk, source, restart) : $"{title}, {status}";
}

/// <summary>A per-user setting of the Customize page as a row of the settings page. It wraps the card, so staging, review and undo run through the existing flow.</summary>
public sealed class CustomizeRow : ObservableObject, ISettingRow
{
    private readonly SettingCardViewModel card;
    private readonly SettingsModel model;
    private readonly LocalizationService text;
    private IReadOnlyList<RowChoice>? choices;

    public CustomizeRow(SettingCardViewModel card, SettingsModel model, LocalizationService text)
    {
        this.card = card; this.model = model; this.text = text;
        card.PropertyChanged += (_, _) => { choices = null; OnPropertyChanged(string.Empty); };
    }

    public SettingCardViewModel Card => card;
    public string Id => card.Id;
    public string Title => card.Title;
    public string Description => card.Description;
    public string CategoryKey => card.Definition.Category.ToString();
    public string CategoryLabel => card.CategoryLabel;
    public int CategoryOrder => (int)card.Definition.Category;
    public RiskLevel Risk => card.Definition.Risk;
    public string RiskText => model.Format.Risk(card.Definition.Risk);
    public SourceClass? Source => SettingSourceClasses.Of(card.Definition.SourceNote);
    public string SourceText => RowText.SourceName(text, Source);
    public string SourceReference => SettingSourceClasses.SourceId(card.Definition.SourceNote) ?? "";
    public bool RequiresRestart => card.Definition.RequiresExplorerRestart || card.Definition.RequiresSignOut;
    public string RestartText => card.Definition.RequiresExplorerRestart && card.Definition.RequiresSignOut ? text["SetRestartBoth"]
        : card.Definition.RequiresExplorerRestart ? text["SetBadgeRestartExplorer"] : card.Definition.RequiresSignOut ? text["SetBadgeSignOut"] : "";
    public bool IsAdvanced => card.Definition.Risk == RiskLevel.High;
    public bool IsAvailable => card.IsReady;
    public bool IsManaged => false;
    public string StatusText => card.IsReady ? "" : $"{card.StatusTitle}. {card.StatusDetail}";
    public string CurrentLabel => card.CurrentLabel;
    public string CurrentHint => card.CurrentHint;
    public string ValueText => !card.IsReady ? "" : card.IsChangeStaged
        ? text.Format("RowNowAfter", card.CurrentLabel, model.Format.Choice(card.Definition, card.StagedChoiceId))
        : text.Format("RowNow", card.CurrentLabel);
    public IReadOnlyList<RowChoice> Choices => choices ??= card.Choices.Select(c => new RowChoice(c.Id, c.Label)).ToList();
    public RowChoice? SelectedChoice
    {
        get => card.SelectedChoice is { } c ? Choices.FirstOrDefault(x => x.Id == c.Id) : null;
        set { if (value is not null && card.Choices.FirstOrDefault(c => c.Id == value.Id) is { } choice) card.SelectedChoice = choice; else OnPropertyChanged(nameof(SelectedChoice)); }
    }
    public bool CanChoose => card.CanChoose;
    public bool IsPending => card.IsChangeStaged;
    public string PendingText => card.StagedText;
    public bool DiffersFromDefault => card.IsReady && card.EffectiveChoiceId != card.Definition.DefaultChoiceId;
    public string DefaultLabel => model.Format.Choice(card.Definition, card.Definition.DefaultChoiceId);
    public ChangeOrigin Origin
    {
        get
        {
            var last = model.History.FirstOrDefault(o => o.SettingId == card.Id && o.State is OperationState.Applied or OperationState.PendingRestart or OperationState.Reverted);
            if (last is null || last.State == OperationState.Reverted || !card.IsReady) return ChangeOrigin.None;
            return last.AfterChoiceId == card.EffectiveChoiceId ? ChangeOrigin.ByWinora : ChangeOrigin.Since;
        }
    }
    public bool IsChangedByWinora => Origin == ChangeOrigin.ByWinora;
    public bool IsChangedSince => Origin == ChangeOrigin.Since;
    public string? RecommendedChoiceId => card.Definition.RecommendedChoiceId;
    public bool IsRecommendedGap => RecommendedRule.Gap(this);
    public bool IsInlineChoice => IsAvailable && !IsAdvanced;
    public bool IsReviewOnly => IsAvailable && IsAdvanced;
    public bool IsUnavailable => !IsAvailable;
    public bool HasRiskDetail => RiskDetail.Length > 0;
    public bool HasCurrentHint => card.HasCurrentHint;
    public string ConditionalNote => card.ConditionalNote;
    public bool HasConditionalNote => card.HasConditionalNote;
    public string TechnicalText => card.Location;
    public string RiskDetail => RowText.RiskDetail(text, card.Id.Replace('.', '_'), card.Definition.Risk);
    public string AutomationName => RowText.Automation(text, Title, IsAvailable, card.IsReady ? card.CurrentLabel : "", StatusText, RiskText, SourceText, RequiresRestart ? RestartText : text["RowNoRestart"]);
    public string MoreName => text.Format("RowMoreName", Title);
    public string ReviewName => text.Format("RowReviewName", Title);
    public ICommand DiscardCommand => card.DiscardCommand;
    public ICommand ResetCommand => card.ResetCommand;
    public ICommand UndoCommand => card.RevertCommand;
    public bool CanReset => card.CanReset;
    public bool CanUndo => card.CanRevert;
    public string ResetHelp => card.ResetHelp;
    public string UndoHelp => card.RevertHelp;
    public string SearchHaystack => string.Join('\n', Title, Description, Id, CategoryLabel, card.IsReady ? card.CurrentLabel : card.StatusTitle);
}

/// <summary>A machine setting of the Optimize page as a row of the settings page.</summary>
public sealed class OptimizeSettingRow : ObservableObject, ISettingRow
{
    private readonly OptimizeRow row;
    private readonly OptimizeViewModel page;
    private readonly LocalizationService text;
    private IReadOnlyList<RowChoice>? choices;

    public OptimizeSettingRow(OptimizeRow row, OptimizeViewModel page, LocalizationService text)
    {
        this.row = row; this.page = page; this.text = text;
        DiscardCommand = new RelayCommand(row.Discard);
        ResetCommand = new RelayCommand(() => { if (DefaultChoice is { } c) SelectedChoice = c; }, () => CanReset);
        UndoCommand = new RelayCommand(() => page.UndoLatest(row), () => CanUndo);
        row.PropertyChanged += (_, _) => Refresh();
    }

    public OptimizeRow Row => row;
    public void Refresh() { OnPropertyChanged(string.Empty); (ResetCommand as RelayCommand)?.NotifyCanExecuteChanged(); (UndoCommand as RelayCommand)?.NotifyCanExecuteChanged(); }

    public string Id => row.Definition.Id + (row.Item.Adapter is { } a ? "@" + a.InstanceId : "");
    public string Title => row.DisplayTitle;
    public string Description => row.Description;
    public string CategoryKey => OptimizeTabs.KeyOf(row.Definition.Id);
    public string CategoryLabel => text["OptTab_" + CategoryKey];
    public int CategoryOrder => OptimizeTabs.OrderOf(CategoryKey);
    public RiskLevel Risk => row.Risk;
    public string RiskText => row.RiskText;
    public SourceClass? Source => row.SourceClassValue;
    public string SourceText => RowText.SourceName(text, row.SourceClassValue);
    public string SourceReference => row.Source;
    public bool RequiresRestart => row.HasRestart;
    public string RestartText => row.RestartText ?? "";
    public bool IsAdvanced => row.IsAdvanced;
    public bool IsAvailable => row.IsAvailable;
    public bool IsManaged => row.Item.Problem == "optimize.managed_by_policy";
    public string StatusText => row.ProblemText ?? "";
    public string CurrentLabel => row.Current?.Label ?? "";
    public string CurrentHint => "";
    public string ValueText => !row.IsAvailable ? "" : row.IsDirty
        ? text.Format("RowNowAfter", CurrentLabel, row.SelectedChoice!.Label) : text.Format("RowNow", CurrentLabel);
    public IReadOnlyList<RowChoice> Choices => choices ??= row.Choices.Select(c => new RowChoice(c.Id, c.Label)).ToList();
    public RowChoice? SelectedChoice
    {
        get => row.SelectedChoice is { } c ? Choices.FirstOrDefault(x => x.Id == c.Id) : null;
        set { if (value is not null && row.Choices.FirstOrDefault(c => c.Id == value.Id) is { } choice) row.SelectedChoice = choice; else OnPropertyChanged(nameof(SelectedChoice)); }
    }
    public bool CanChoose => row.IsAvailable && page.IsElevated && !page.IsBusy;
    public bool IsPending => row.IsDirty;
    public string PendingText => row.IsDirty ? text.Format("SetStagedFormat", row.SelectedChoice!.Label) : "";
    private RowChoice? DefaultChoice => Choices.FirstOrDefault(c => c.Id == row.Definition.DefaultChoiceId);
    public bool DiffersFromDefault => row.IsAvailable && row.Item.CurrentChoiceId is { } id && id != row.Definition.DefaultChoiceId;
    public string DefaultLabel => DefaultChoice?.Label ?? "";
    public ChangeOrigin Origin => page.OriginOf(row);
    public bool IsChangedByWinora => Origin == ChangeOrigin.ByWinora;
    public bool IsChangedSince => Origin == ChangeOrigin.Since;
    public string? RecommendedChoiceId => row.Definition.RecommendedChoiceId;
    public bool IsRecommendedGap => RecommendedRule.Gap(this);
    public bool IsInlineChoice => IsAvailable && !IsAdvanced;
    public bool IsReviewOnly => IsAvailable && IsAdvanced;
    public bool IsUnavailable => !IsAvailable;
    public bool HasRiskDetail => RiskDetail.Length > 0;
    public bool HasCurrentHint => false;
    public string ConditionalNote => "";
    public bool HasConditionalNote => false;
    public string TechnicalText => row.Definition.Registry is { } r ? @"HKLM\" + r.SubKey.Trim('\\') + @"\" + r.ValueName : row.Definition.Service is { } s ? s.ServiceName : "";
    public string RiskDetail => RowText.RiskDetail(text, row.Definition.Id.Replace('.', '_'), row.Risk);
    public string AutomationName => RowText.Automation(text, Title, IsAvailable, CurrentLabel, StatusText, RiskText, SourceText, RequiresRestart ? RestartText : text["RowNoRestart"]);
    public string MoreName => text.Format("RowMoreName", Title);
    public string ReviewName => text.Format("RowReviewName", Title);
    public ICommand DiscardCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand UndoCommand { get; }
    public bool CanReset => row.IsAvailable && page.IsElevated && DiffersFromDefault;
    public bool CanUndo => page.CanUndoLatest(row);
    public string ResetHelp => text.Format("SetResetHelp", DefaultLabel);
    public string UndoHelp => text[CanUndo ? "SetRevertHelp" : "SetRevertUnavailableHelp"];
    public string SearchHaystack => string.Join('\n', Title, Description, row.Definition.Id, CategoryLabel, CurrentLabel, StatusText);
}
