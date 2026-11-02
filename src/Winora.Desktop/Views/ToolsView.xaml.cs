// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using Winora.Desktop.Search;

namespace Winora.Desktop;

public partial class ToolsView : UserControl, IHighlightTarget
{
    public ToolsView() => InitializeComponent();

    public bool Highlight(string targetId)
    {
        if (DataContext is not ToolsViewModel page) return false;
        var row = page.Tools.FirstOrDefault(t => t.Info.Id == targetId);
        return row is not null && ToolList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container && HighlightOutline.Show(container);
    }
}
