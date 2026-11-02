// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;

namespace Winora.Application.Activity;

/// <summary>
/// One change that waits for review, as the Action center shows it. <see cref="Before"/> and <see cref="After"/> are display texts and may be empty for a change that has no
/// value pair (a ticked row). <see cref="Remove"/> takes the change out of the page's staging without applying anything; null when the page offers no single removal.
/// </summary>
public sealed record StagedChange(string Id, string Title, string Before, string After, RiskLevel? Risk, Action? Remove)
{
    public bool HasValues => Before.Length > 0 || After.Length > 0;
}

/// <summary>The words and actions of one page's staged changes. The page keeps its own words ("Review changes", "Clean selected", "Remove selected") and its own review dialog.</summary>
public sealed record StagingView(
    string PageKey, IReadOnlyList<StagedChange> Changes, string Summary, string ReviewText, string ReviewHelp, string DiscardText, string DiscardHelp,
    Func<bool> CanReview, Action Review, Func<bool> CanDiscard, Action Discard);
