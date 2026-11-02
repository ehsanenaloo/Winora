// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>Whether and how a change can be taken back. Shown as the answer to "Can it be undone?".</summary>
public enum ConfirmUndo { Unspecified, Exact, WhileUnchanged, No, Reinstall }

/// <summary>
/// One item of "What will change". With a before and an after value it is shown as a diff; without them it is a plain line (a file group, an app).
/// <paramref name="Technical"/> holds the registry path or command as an LTR island behind "Technical details".
/// </summary>
public sealed record ConfirmChange(string Name, string? Before = null, string? After = null, string? Technical = null, string? Note = null)
{
    public bool HasDiff => Before is not null || After is not null;
    /// <summary>When set, the row has a box the person ticks; the page reads <see cref="ConfirmChoice.IsChecked"/> after the dialog closes. The primary button needs at least one ticked box.</summary>
    public ConfirmChoice? Choice { get; init; }
}

/// <summary>The tick box of one row of the confirmation dialog.</summary>
public sealed class ConfirmChoice(bool isChecked) : ObservableObject
{
    private bool value = isChecked;
    public bool IsChecked { get => value; set => SetProperty(ref this.value, value); }
}

/// <summary>What the confirmation dialog shows before a change that cannot be taken back or that needs a clear yes (files removed for good, a power change).</summary>
public sealed record ActionConfirmation(string Title, string Body, IReadOnlyList<string> Lines, string Warning, string Primary)
{
    /// <summary>Before and after rows. When empty, <see cref="Lines"/> are shown as plain lines.</summary>
    public IReadOnlyList<ConfirmChange> Changes { get; init; } = [];
    /// <summary>The highest risk of the items. Null hides the risk block.</summary>
    public RiskLevel? Risk { get; init; }
    public ConfirmUndo Undo { get; init; }
    /// <summary>True when a restart is needed, false when none is, null when the page does not know.</summary>
    public bool? Restart { get; init; }
    /// <summary>The page asks the restore point gate after this dialog, so the dialog shows the restore point status line.</summary>
    public bool UsesRestorePoint { get; init; }
    /// <summary>Files or data are removed for good: the button is red and Cancel is the default answer.</summary>
    public bool Irreversible { get; init; }
    /// <summary>Names of the High risk items; the dialog lists them by name.</summary>
    public IReadOnlyList<string> HighRiskNames { get; init; } = [];
}

/// <summary>Binding model of the one confirmation dialog. Every confirmation in the app is built into this model.</summary>
public sealed class ConfirmDialogModel : ObservableObject
{
    public const int VisibleRows = 5;
    private bool expanded;
    private bool acknowledged;
    private string? restorePointLine;

    public ConfirmDialogModel(LocalizationService text)
    {
        Text = text;
        ToggleRowsCommand = new(() => { Expanded = !Expanded; });
    }

    public LocalizationService Text { get; }
    public string Title { get; init; } = "";
    public string Intro { get; init; } = "";
    public IReadOnlyList<ConfirmChange> Changes { get; init; } = [];
    public RiskLevel? Risk { get; init; }
    public IReadOnlyList<string> HighRiskNames { get; init; } = [];
    public ConfirmUndo Undo { get; init; }
    public bool? Restart { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string Warning { get; init; } = "";
    public string PrimaryText { get; init; } = "";
    public bool Irreversible { get; init; }
    public bool UsesRestorePoint { get; init; }
    /// <summary>Review of real settings: carries the "restart Explorer now" option back to the engine call.</summary>
    public ChangeReview? Review { get; init; }
    /// <summary>A second explicit step for a choice that needs one beyond High risk (administrator change when Windows will not prompt).</summary>
    public string? ExtraAckLabel { get; init; }
    /// <summary>Extra blocks of the machine consent window (scope, digest, publisher). Plain text lines.</summary>
    public IReadOnlyList<ConfirmDetail> Details { get; init; } = [];

    public RelayCommand ToggleRowsCommand { get; }

    public bool IsHighRisk => Risk == RiskLevel.High;
    public bool HasRisk => Risk is not null;
    public RiskLevel RiskValue => Risk ?? RiskLevel.Low;
    public string RiskText => Risk is { } r ? Text.Enum(r) : "";
    public bool HasHighRiskNames => IsHighRisk && HighRiskNames.Count > 0;
    public string HighRiskNamesText => HighRiskNames.Count == 0 ? "" : Text.Format("CfHighRiskNames", string.Join(", ", HighRiskNames));
    public string HighRiskConsequence => Text["CfHighRiskConsequence"];

    // ---- What will change ----
    private IReadOnlyList<ConfirmRow>? allRows;
    public IReadOnlyList<ConfirmRow> AllRows => allRows ??= Changes.Select(c => new ConfirmRow(c, Text)).ToList();
    public IReadOnlyList<ConfirmRow> Rows => expanded || AllRows.Count <= VisibleRows + 1 ? AllRows : AllRows.Take(VisibleRows).ToList();
    public int HiddenCount => expanded || AllRows.Count <= VisibleRows + 1 ? 0 : AllRows.Count - VisibleRows;
    public bool CanToggleRows => AllRows.Count > VisibleRows + 1;
    public string ToggleRowsText => expanded ? Text["CfShowFewer"] : Text.Format("CfShowMore", AllRows.Count - VisibleRows);
    public bool Expanded { get => expanded; private set { if (SetProperty(ref expanded, value)) { OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(HiddenCount)); OnPropertyChanged(nameof(ToggleRowsText)); } } }
    public bool HasRows => Changes.Count > 0;
    public string WhatWillChange => Text["CfWhatWillChange"];

    // ---- Facts ----
    public string RiskLabel => Text["CfRiskLabel"];
    public string UndoLabel => Text["CfUndoLabel"];
    public bool HasUndo => Undo != ConfirmUndo.Unspecified;
    public string UndoText => Undo switch
    {
        ConfirmUndo.Exact => Text["CfUndoExact"],
        ConfirmUndo.WhileUnchanged => Text["CfUndoWhile"],
        ConfirmUndo.No => Text["CfUndoNo"],
        ConfirmUndo.Reinstall => Text["CfUndoReinstall"],
        _ => "",
    };
    public string RestartLabel => Text["CfRestartLabel"];
    public bool HasRestart => Restart is not null;
    public string RestartText => Restart == true ? Text["CfRestartAfter"] : Text["CfRestartNone"];
    public bool HasWarning => Warning.Length > 0;
    public bool HasIntro => Intro.Length > 0;

    // ---- Restore point ----
    public bool HasRestorePointLine => restorePointLine is not null;
    public string? RestorePointLine
    {
        get => restorePointLine;
        set { if (SetProperty(ref restorePointLine, value)) OnPropertyChanged(nameof(HasRestorePointLine)); }
    }

    // ---- Real settings review extras (restart Explorer, sign out) ----
    public bool ShowRestartOption => Review is { Kind: not ChangeKind.Revert } r && r.HasExplorerRestart;
    public bool RestartExplorer { get => Review?.RestartExplorer ?? false; set { if (Review is not null) Review.RestartExplorer = value; } }
    public string RestartOptionLabel => Text["SetRestartOption"];
    public string RestartWarning => Text["SetRestartWarning"];
    public bool ShowSignOutNote => Review?.HasSignOut ?? false;
    public string SignOutNote => Text["SetSignOutNote"];

    // ---- Buttons ----
    /// <summary>The second step for High risk: a checkbox inside the same dialog. Without it the primary button stays disabled.</summary>
    public bool NeedsAcknowledge => IsHighRisk || ExtraAckLabel is not null;
    public string AckLabel => ExtraAckLabel ?? Text["CfAckHigh"];
    public bool Acknowledged { get => acknowledged; set { if (SetProperty(ref acknowledged, value)) { OnPropertyChanged(nameof(CanConfirm)); if (value) AckMissing = false; } } }
    private bool ackMissing;
    /// <summary>The primary button was pressed before the box was ticked: the dialog stays open and says so.</summary>
    public bool AckMissing { get => ackMissing; set => SetProperty(ref ackMissing, value); }
    public string AckNeededText => Text["CfAckNeeded"];
    public bool CanConfirm => (!NeedsAcknowledge || acknowledged) && (!HasChoices || AnyChosen);

    // ---- Tick boxes ----
    public bool HasChoices => Changes.Any(c => c.Choice is not null);
    public bool AnyChosen => Changes.Any(c => c.Choice?.IsChecked == true);
    private bool choiceMissing;
    /// <summary>The primary button was pressed with no box ticked: the dialog stays open and says so.</summary>
    public bool ChoiceMissing { get => choiceMissing; set => SetProperty(ref choiceMissing, value); }
    public string ChoiceNeededText => Text["CfChoiceNeeded"];

    /// <summary>Says why the primary button did nothing: the High risk box first, then the tick boxes.</summary>
    public void MarkMissing()
    {
        if (NeedsAcknowledge && !acknowledged) AckMissing = true;
        else if (HasChoices && !AnyChosen) ChoiceMissing = true;
    }

    private void WatchChoices()
    {
        foreach (var choice in Changes.Select(c => c.Choice).OfType<ConfirmChoice>())
            choice.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(CanConfirm)); OnPropertyChanged(nameof(AnyChosen)); if (AnyChosen) ChoiceMissing = false; };
    }
    /// <summary>High risk and irreversible choices make Cancel the default answer; the primary button is red.</summary>
    public bool DefaultIsCancel => IsHighRisk || Irreversible;
    public bool PrimaryIsDanger => IsHighRisk || Irreversible;
    public string CancelText => Text["Back"];
    public string AutomationName => Title;

    // ---- Builders ----

    public static ConfirmDialogModel From(ActionConfirmation c, LocalizationService text)
    {
        var changes = c.Changes.Count > 0 ? c.Changes : c.Lines.Select(l => new ConfirmChange(l)).ToList();
        var high = c.HighRiskNames.Count > 0 ? c.HighRiskNames : [];
        var model = new ConfirmDialogModel(text)
        {
            Title = c.Title, Intro = c.Body, Changes = changes, Risk = c.Risk, HighRiskNames = high, Undo = c.Undo, Restart = c.Restart,
            Warning = c.Warning, PrimaryText = PrimaryFor(c.Risk, c.Irreversible, c.Primary, text), Irreversible = c.Irreversible, UsesRestorePoint = c.UsesRestorePoint,
        };
        model.WatchChoices();
        return model;
    }

    public static ConfirmDialogModel From(ChangeReview review, LocalizationService text)
    {
        var title = review.Kind switch
        {
            ChangeKind.Revert => review.IsBulk ? text.Plural("SetReviewTitleRevertBulk", review.Items.Count, review.Items.Count) : text["SetReviewTitleRevert"],
            ChangeKind.Reset => text["SetReviewTitleReset"],
            _ => review.IsBulk ? text.Format("SetReviewTitleBulk", review.Items.Count) : text["SetReviewTitle"],
        };
        var primary = review.Kind == ChangeKind.Revert ? (review.IsBulk ? text.Plural("SetRevertAllButton", review.Items.Count, review.Items.Count) : text["SetRevertButton"]) : review.IsBulk ? text.Format("SetApplyAllButton", review.Items.Count) : text["SetApplyButton"];
        var risk = review.Items.Count == 0 ? RiskLevel.Low : review.Items.Max(i => i.RiskLevel);
        var changes = review.Items.Select(i => new ConfirmChange(i.SettingName, i.Before, i.After, i.Location, i.IsNoChange ? text["SetReviewNoChange"] : i.WriteNote)).ToList();
        var undo = review.Kind == ChangeKind.Revert ? ConfirmUndo.Exact
            : review.Items.Any(i => i.RecoveryValue == RecoveryKind.Irreversible) ? ConfirmUndo.No
            : review.Items.Any(i => i.RecoveryValue == RecoveryKind.Conditional) ? ConfirmUndo.WhileUnchanged
            : ConfirmUndo.Exact;
        var notes = new List<string> { text["SetReviewRecoveryNote"] };
        if (review.IsBulk) notes.Add(text["SetReviewBulkNote"]);
        var restartNotes = review.Items.Where(i => i.HasRestartNote).Select(i => i.RestartNote!).Distinct().ToList();
        return new(text)
        {
            Title = title, Intro = text[review.Kind == ChangeKind.Revert ? "SetReviewIntroRevert" : "SetReviewIntro"], Changes = changes, Risk = risk,
            HighRiskNames = review.Items.Where(i => i.RiskLevel == RiskLevel.High).Select(i => i.SettingName).ToList(),
            Undo = undo, Restart = review.Items.Any(i => i.HasRestartNote), Notes = notes.Concat(restartNotes).ToList(), PrimaryText = PrimaryFor(risk, false, primary, text), Review = review,
        };
    }

    public static ConfirmDialogModel From(ReviewSummary s, LocalizationService text) => new(text)
    {
        Title = text["ReviewDialogTitle"], Intro = text["ReviewDialogIntro"], Changes = [new(s.SettingName, s.Before, s.After, null, s.WillChange ? null : text["ReviewNoChange"])],
        Risk = RiskLevel.Low, Undo = ConfirmUndo.Exact, Restart = false, Notes = [text["ReviewRecoveryNote"]], PrimaryText = text["ApplySimulation"],
    };

    public static ConfirmDialogModel Simple(string title, string body, string primary, LocalizationService text) => new(text) { Title = title, Intro = body, PrimaryText = primary };

    private static string PrimaryFor(RiskLevel? risk, bool irreversible, string given, LocalizationService text) =>
        risk == RiskLevel.High && !irreversible ? text["CfApplyHigh"] : given;
}

/// <summary>An extra line of the machine consent window, for example the batch code. Mono lines stay left to right.</summary>
public sealed record ConfirmDetail(string Label, string Value, bool Mono);

/// <summary>One row of "What will change" for the dialog.</summary>
public sealed class ConfirmRow(ConfirmChange change, LocalizationService text) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Never raised. It lets WPF bindings listen through a weak event; on a plain object they hold the object, and with it the text service, for the life of the process.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public string Name => change.Name;
    public bool HasChoice => change.Choice is not null;
    public bool ShowName => change.Choice is null;
    public bool IsChecked { get => change.Choice?.IsChecked ?? false; set { if (change.Choice is { } c) c.IsChecked = value; } }
    public bool HasDiff => change.HasDiff;
    public string Before => change.Before ?? "";
    public string After => change.After ?? "";
    public string BeforeLabel => text["DiffBefore"];
    public string AfterLabel => text["DiffAfter"];
    public bool HasTechnical => !string.IsNullOrEmpty(change.Technical);
    public string Technical => change.Technical ?? "";
    public string TechnicalLabel => text["CfTechnical"];
    public string RegistryName => text["CfRegistryDetails"];
    public bool HasNote => !string.IsNullOrEmpty(change.Note);
    public string Note => change.Note ?? "";
    public string AutomationName => change.HasDiff ? text.Format("DiffName", change.Name, Before, After) : change.Name;
}
