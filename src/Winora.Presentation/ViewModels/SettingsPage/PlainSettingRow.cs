// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop;

/// <summary>
/// A setting row for a page that is not the settings page (Power, Network): it fills the members of <see cref="ISettingRow"/> that such a row does not have with plain defaults, so a page only
/// supplies a title, the choices, the staged choice and what is pending. The row owns no state: the page keeps staging, review and undo.
/// </summary>
public abstract class PlainSettingRow(LocalizationService text) : ObservableObject, ISettingRow
{
    protected LocalizationService Text { get; } = text;
    private static readonly RelayCommand Never = new(() => { }, () => false);

    public abstract string Id { get; }
    public abstract string Title { get; }
    public virtual string Description => "";
    public virtual string CategoryKey => "";
    public virtual string CategoryLabel => "";
    public virtual int CategoryOrder => 0;
    public virtual RiskLevel Risk => RiskLevel.Low;
    public string RiskText => Text.Enum(Risk);
    public SourceClass? Source => null;
    public string SourceText => "";
    public string SourceReference => "";
    public virtual bool RequiresRestart => false;
    public virtual string RestartText => "";
    public bool IsAdvanced => false;
    public virtual bool IsAvailable => true;
    public virtual bool IsManaged => false;
    public virtual string StatusText => "";
    public abstract string CurrentLabel { get; }
    public virtual string CurrentHint => "";
    public virtual string ValueText => !IsAvailable ? "" : IsPending ? Text.Format("RowNowAfter", CurrentLabel, SelectedChoice?.Label ?? "") : Text.Format("RowNow", CurrentLabel);
    public abstract IReadOnlyList<RowChoice> Choices { get; }
    public abstract RowChoice? SelectedChoice { get; set; }
    public virtual bool CanChoose => IsAvailable;
    public abstract bool IsPending { get; }
    public virtual string PendingText => IsPending ? Text.Format("SetStagedFormat", SelectedChoice?.Label ?? "") : "";
    public virtual bool DiffersFromDefault => false;
    public virtual string DefaultLabel => "";
    public ChangeOrigin Origin => ChangeOrigin.None;
    public bool IsChangedByWinora => false;
    public bool IsChangedSince => false;
    public string? RecommendedChoiceId => null;
    public bool IsRecommendedGap => false;
    public bool IsInlineChoice => IsAvailable;
    public bool IsReviewOnly => false;
    public bool IsUnavailable => !IsAvailable;
    public bool HasRiskDetail => RiskDetail.Length > 0;
    public virtual string ConditionalNote => "";
    public bool HasConditionalNote => ConditionalNote.Length > 0;
    public bool HasCurrentHint => CurrentHint.Length > 0;
    public virtual string TechnicalText => "";
    public virtual string RiskDetail => "";
    public virtual string AutomationName => RowText.Automation(Text, Title, IsAvailable, CurrentLabel, StatusText, RiskText, "", RequiresRestart ? RestartText : Text["RowNoRestart"]);
    public string MoreName => Text.Format("RowMoreName", Title);
    public string ReviewName => Text.Format("RowReviewName", Title);
    public virtual ICommand DiscardCommand => Never;
    public virtual ICommand ResetCommand => Never;
    public virtual ICommand UndoCommand => Never;
    public virtual bool CanReset => false;
    public virtual bool CanUndo => false;
    public virtual string ResetHelp => "";
    public virtual string UndoHelp => Text["SetRevertUnavailableHelp"];
    public virtual string SearchHaystack => string.Join('\n', Title, Description, CurrentLabel);

    /// <summary>Tells the card that something it shows changed.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
