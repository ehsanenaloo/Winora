// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Controls;
using System.Windows.Input;
namespace Winora.Desktop;

public partial class CustomizeView : UserControl, Search.IHighlightTarget
{
    public CustomizeView() => InitializeComponent();
    public void FocusSearch() => PageView.FocusSearch();
    private void OnFind(object sender, ExecutedRoutedEventArgs e) { if (PageView.IsVisible) FocusSearch(); }
    public Controls.SettingsPageView Page => PageView;

    /// <summary>A search result: opens the group of the setting, scrolls the virtualized list to it and puts keyboard focus on its card (the focus outline stays until focus moves).</summary>
    public bool Highlight(string targetId)
    {
        if (DataContext is not ISettingsPage page) return false;
        var row = page.List.Rows.FirstOrDefault(r => r.Id == targetId) ?? page.List.Rows.FirstOrDefault(r => r.Id.StartsWith(targetId + "@", StringComparison.Ordinal));
        return row is not null && PageView.Reveal(row);
    }
}
