// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// The content area of a page whose tabs are drawn by a <see cref="TabRail"/> in the fixed header. It switches the content with the selected tab and is not a tab list of its own:
/// a screen reader finds the one tab list (the rail) and the content under it.
/// </summary>
public sealed class TabBody : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new TabBodyPeer(this);

    private sealed class TabBodyPeer(TabBody owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(TabBody);
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}
