// SPDX-License-Identifier: GPL-3.0-only

namespace Winora.Desktop;

/// <summary>The choice of a High risk setting inside the details dialog. The staged value goes to the row, like a choice made on a card; the page applies it through its one review.</summary>
public sealed class SettingChoiceEditor(ISettingRow row, LocalizationService text) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Never raised. It lets WPF bindings listen through a weak event; on a plain object they hold the object, and with it the page, for the life of the process.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public ISettingRow Row { get; } = row;
    public LocalizationService Text { get; } = text;
}

/// <summary>Builds the details dialog of a setting: header card with the tags, description, current value, why it is offered, technical details and, for a High risk setting, the choice.</summary>
public static class SettingDetails
{
    public static DetailsDialog Build(LocalizationService text, Func<string, bool> copy, ISettingRow row)
    {
        List<DetailField> fields = [];
        if (row.Description.Length > 0) fields.Add(new(text["FieldDescription"], row.Description, false, DetailGroup.Description));
        if (row.HasConditionalNote) fields.Add(new(text["DetailsNote"], row.ConditionalNote, false, DetailGroup.Description));
        fields.Add(new(text["DetailsCurrent"], row.IsAvailable ? row.ValueText : row.StatusText));
        if (row.DefaultLabel.Length > 0) fields.Add(new(text["DetailsDefault"], row.DefaultLabel));
        if (row.HasRiskDetail) fields.Add(new(text["DetailsWhatCanGoWrong"], row.RiskDetail));
        if (row.Source is not null) fields.Add(new(text["DetailsWhy"], row.SourceText));
        if (row.SourceReference.Length > 0) fields.Add(new(text["DetailsSourceRef"], row.SourceReference, true));
        if (row.TechnicalText.Length > 0) fields.Add(new(text["CfRegistryDetails"], row.TechnicalText, true));
        if (row.IsChangedByWinora) fields.Add(new(text["DetailsLastChange"], text["RowChangedByWinora"], false, DetailGroup.Run));
        else if (row.IsChangedSince) fields.Add(new(text["DetailsLastChange"], text["RowChangedSince"], false, DetailGroup.Run));

        List<BadgeModel> chips = [BadgeModel.ForRisk(row.RiskText, row.Risk)];
        if (row.Source is { } source) chips.Add(BadgeModel.ForSource(row.SourceText, source));
        if (row.RequiresRestart) chips.Add(new(row.RestartText, Tone.Caution));
        if (row.IsManaged) chips.Add(new(text["RowManaged"], Tone.Info));
        if (row.IsChangedByWinora) chips.Add(new(text["RowChangedByWinora"], Tone.Success));
        if (row.IsChangedSince) chips.Add(new(text["RowChangedSince"], Tone.Caution));
        if (row.DiffersFromDefault) chips.Add(new(text["RowDiffers"], Tone.Neutral));
        if (row.IsPending) chips.Add(new(text["RowPending"], Tone.Caution));

        // The row menu (Reset, Revert last) is not reachable by keyboard inside the list, so the dialog offers the same actions.
        List<DetailsAction> actions = [];
        if (!row.IsReviewOnly && row.ResetCommand.CanExecute(null))
            actions.Add(new(text["SetReset"], text["SetReset"], text.Format("SetResetHelp", row.DefaultLabel), () => { if (row.ResetCommand.CanExecute(null)) row.ResetCommand.Execute(null); return Task.CompletedTask; }));
        if (row.UndoCommand is { } undo && row.IsAvailable)
            actions.Add(new(text["SetRevertLast"], text["SetRevertLast"], row.UndoHelp, () => { if (undo.CanExecute(null)) undo.Execute(null); return Task.CompletedTask; }, IsEnabled: undo.CanExecute(null)));

        return DetailsDialogBuilder.Build(text, copy, row.Title, fields, chips, "", false, actions, row.IsReviewOnly ? new SettingChoiceEditor(row, text) : null);
    }
}
