// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop.Search;

/// <summary>
/// A page view that can show one of its items on request from the search: it scrolls the item into view, moves keyboard focus to it and keeps an outline on it until focus moves.
/// The window calls it after it opened the page. A page without it is only opened.
/// </summary>
public interface IHighlightTarget
{
    /// <summary>Highlights the item with this id (a setting id, a profile id, a tool id). Returns false when the page has no such item.</summary>
    bool Highlight(string targetId);
}
