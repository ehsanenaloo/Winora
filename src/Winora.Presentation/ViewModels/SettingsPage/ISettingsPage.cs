// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>What the settings page needs from its view model: the list model, the staged-changes bar and a few labels.</summary>
public interface ISettingsPage
{
    SettingsListModel List { get; }
    string Title { get; }
    LocalizationService Text { get; }
    int PendingCount { get; }
    /// <summary>The list is shown (not a loading, failure or empty state).</summary>
    bool ShowList { get; }
}
