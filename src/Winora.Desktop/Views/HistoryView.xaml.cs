// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Controls;
namespace Winora.Desktop;

/// <summary>The History page: a workspace whose body (the timeline, 25 changes at a time, and the pager) is the one scroll area.</summary>
public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();
}
