// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// The stat strip of a list page: a few figures, each with its label directly under it, in one card above the toolbar. The items need <c>Value</c>, <c>Label</c> and <c>AutomationName</c>
/// (a <c>SummaryStat</c>). The strip wraps on a narrow page and is read as one group with the figures as its name.
/// </summary>
public sealed class StatStrip : ItemsControl
{
    static StatStrip()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatStrip), new FrameworkPropertyMetadata(typeof(StatStrip)));
        FocusableProperty.OverrideMetadata(typeof(StatStrip), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(StatStrip), new FrameworkPropertyMetadata(false));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StripPeer(this);

    private sealed class StripPeer(StatStrip owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(StatStrip);
        protected override string GetNameCore() => AutomationProperties.GetName(owner);
        protected override bool IsControlElementCore() => true;
    }
}
