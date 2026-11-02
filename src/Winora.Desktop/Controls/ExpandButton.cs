// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace Winora.Desktop.Controls;

/// <summary>
/// A Winora button that opens and closes something (the changes of a history entry). It looks like any button and still presses with Invoke; it also reports
/// whether the thing it controls is expanded or collapsed (ExpandCollapse), so a screen reader says the state instead of repeating "show" or "hide".
/// </summary>
public sealed class ExpandButton : Wpf.Ui.Controls.Button
{
    static ExpandButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ExpandButton), new FrameworkPropertyMetadata(typeof(Wpf.Ui.Controls.Button)));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(ExpandButton),
        new FrameworkPropertyMetadata(false, (d, e) => ((ExpandButton)d).OnExpandedChanged((bool)e.NewValue)));

    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    private void OnExpandedChanged(bool expanded)
    {
        if (UIElementAutomationPeer.FromElement(this) is ExpandButtonPeer peer) peer.Raise(expanded);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ExpandButtonPeer(this);

    private sealed class ExpandButtonPeer(ExpandButton owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.ExpandCollapse ? this : base.GetPattern(pattern);
        protected override string GetClassNameCore() => nameof(ExpandButton);
        public ExpandCollapseState ExpandCollapseState => owner.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        // The command of the button changes the state; the pattern presses it only when the state would change.
        public void Expand() { if (!owner.IsExpanded) Invoke(); }
        public void Collapse() { if (owner.IsExpanded) Invoke(); }
        private void Invoke() => ((IInvokeProvider)this).Invoke();

        internal void Raise(bool expanded) =>
            RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                expanded ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded, expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }
}
