// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>
/// The "Change history" block of a page: a heading with the count, the latest rows of the History timeline, and a link to the History page for the area. Its data context is a <see cref="PageHistoryModel"/>.
/// </summary>
public partial class PageHistory : UserControl
{
    public PageHistory() => InitializeComponent();

    public HistoryTimeline TimelineControl => Timeline;
}
