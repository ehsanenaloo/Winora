// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Windows.Input;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop;

/// <summary>Whether Winora set the value that is in effect now. "Since" means Winora set it once and something else changed it after.</summary>
public enum ChangeOrigin { None, ByWinora, Since }

/// <summary>One choice of a setting row. ToString is the label because screen readers read the item text from it.</summary>
public sealed record RowChoice(string Id, string Label, bool? IsOn = null)
{
    public override string ToString() => Label;
}

/// <summary>
/// What the settings page needs from a setting, whichever catalog it comes from (the per-user settings of Customize or the machine settings of Optimize).
/// The row owns no state of its own: it wraps the page's own item, so staging, review and undo keep running through the existing flows.
/// </summary>
public interface ISettingRow : INotifyPropertyChanged
{
    /// <summary>Unique on the page. A setting that exists once per graphics card adds the card to the id.</summary>
    string Id { get; }
    string Title { get; }
    string Description { get; }
    /// <summary>Stable key of the category; used by the category rail and for grouping.</summary>
    string CategoryKey { get; }
    string CategoryLabel { get; }
    /// <summary>Position of the category in the rail; rows with the same order are sorted by label.</summary>
    int CategoryOrder { get; }
    RiskLevel Risk { get; }
    string RiskText { get; }
    /// <summary>The source class (A Microsoft, B vendor or expert, C community), or null when the row has none.</summary>
    SourceClass? Source { get; }
    string SourceText { get; }
    /// <summary>Where the setting is documented, as text (an id or a short reference). Technical, never translated.</summary>
    string SourceReference { get; }
    bool RequiresRestart { get; }
    string RestartText { get; }
    /// <summary>High risk: listed in the Advanced section and changed only from the details pane.</summary>
    bool IsAdvanced { get; }
    /// <summary>False when the setting is unsupported, unreadable or controlled by someone else; <see cref="StatusText"/> says why.</summary>
    bool IsAvailable { get; }
    /// <summary>True when Group Policy or device management controls the value.</summary>
    bool IsManaged { get; }
    string StatusText { get; }
    string CurrentLabel { get; }
    string CurrentHint { get; }
    /// <summary>The value column: "Now: Show", or "Now: Hide, after: Show" while a change is staged.</summary>
    string ValueText { get; }
    IReadOnlyList<RowChoice> Choices { get; }
    RowChoice? SelectedChoice { get; set; }
    bool CanChoose { get; }
    bool IsPending { get; }
    string PendingText { get; }
    bool DiffersFromDefault { get; }
    string DefaultLabel { get; }
    ChangeOrigin Origin { get; }
    bool IsChangedByWinora { get; }
    bool IsChangedSince { get; }
    /// <summary>The control sits on the card: available and not High risk.</summary>
    bool IsInlineChoice { get; }
    /// <summary>High risk and available: the card shows "Review change", the choice is made in the details pane.</summary>
    bool IsReviewOnly { get; }
    bool IsUnavailable { get; }
    bool HasRiskDetail { get; }
    /// <summary>A caution for settings whose value Windows may set back (updates, feature changes); empty for the others.</summary>
    string ConditionalNote { get; }
    bool HasConditionalNote { get; }
    bool HasCurrentHint { get; }
    /// <summary>Registry path or value names as an LTR island, or empty.</summary>
    string TechnicalText { get; }
    /// <summary>What can go wrong, for Medium and High risk; empty for Low.</summary>
    string RiskDetail { get; }
    /// <summary>The choice the catalog recommends, or null when it names none.</summary>
    string? RecommendedChoiceId { get; }
    /// <summary>The setting names a recommended choice, can be changed from the page, and its current or staged choice is another one. Set by <see cref="RecommendedRule.Gap"/>.</summary>
    bool IsRecommendedGap { get; }
    /// <summary>"Show file extensions, currently Show, Low risk, Microsoft source, no restart".</summary>
    string AutomationName { get; }
    /// <summary>Name of the card menu button: "More options for Show file extensions".</summary>
    string MoreName { get; }
    /// <summary>Name of the "Review change" button of a High risk card.</summary>
    string ReviewName { get; }
    ICommand DiscardCommand { get; }
    ICommand ResetCommand { get; }
    ICommand UndoCommand { get; }
    bool CanReset { get; }
    bool CanUndo { get; }
    string ResetHelp { get; }
    string UndoHelp { get; }
    /// <summary>Lower-case text the search looks in: title, description, category, id and the current value.</summary>
    string SearchHaystack { get; }
}

/// <summary>The one rule for "the recommended choice is not in place", shared by the row adapters and the page banner.</summary>
public static class RecommendedRule
{
    public static bool Gap(ISettingRow row) =>
        row.RecommendedChoiceId is { } id && row.IsInlineChoice && row.CanChoose && row.SelectedChoice?.Id != id && row.Choices.Any(c => c.Id == id);
}

/// <summary>Maps the source note of a per-user catalog row to a source class. The class follows the document the note cites.</summary>
public static class SettingSourceClasses
{
    // Rows that cite community scripts or guides are class C, except the ones whose own note cites a Microsoft page.
    private static readonly HashSet<string> MicrosoftW1 = ["SRC-W1-11", "SRC-W1-14", "SRC-W1-15", "SRC-W1-16", "SRC-W1-19"];

    public static SourceClass Of(string sourceNote)
    {
        var id = SourceId(sourceNote);
        if (id is null) return SourceClass.C;
        if (id.StartsWith("SRC-W1-", StringComparison.Ordinal)) return MicrosoftW1.Contains(id) ? SourceClass.A : SourceClass.C;
        return SourceClass.A;
    }

    /// <summary>The first "SRC-..." token of the note, or null.</summary>
    public static string? SourceId(string sourceNote)
    {
        var start = sourceNote.IndexOf("SRC-", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = start;
        while (end < sourceNote.Length && (char.IsLetterOrDigit(sourceNote[end]) || sourceNote[end] == '-')) end++;
        return sourceNote[start..end];
    }
}
