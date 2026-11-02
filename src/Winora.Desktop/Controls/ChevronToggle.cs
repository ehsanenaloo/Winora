// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Winora.Desktop.Controls;

/// <summary>
/// A text button with a chevron that turns when the button is checked (a "details" toggle). The turn follows <see cref="Motion"/>: it is instant when Windows animations are off.
/// The template names the element to turn <c>PART_Chevron</c>.
/// </summary>
public sealed class ChevronToggle : ToggleButton
{
    static ChevronToggle() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ChevronToggle), new FrameworkPropertyMetadata(typeof(ChevronToggle)));

    private RotateTransform? turn;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Chevron") is UIElement chevron)
        {
            turn = new RotateTransform(IsChecked == true ? 180 : 0);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = turn;
        }
    }

    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); Turn(180); RaiseState(true); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); Turn(0); RaiseState(false); }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ChevronTogglePeer(this);

    /// <summary>Reports expanded or collapsed (ExpandCollapse), not pressed (Toggle): the control opens and closes a section.</summary>
    private sealed class ChevronTogglePeer(ChevronToggle owner) : System.Windows.Automation.Peers.ToggleButtonAutomationPeer(owner), System.Windows.Automation.Provider.IExpandCollapseProvider
    {
        public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface pattern) => pattern switch
        {
            System.Windows.Automation.Peers.PatternInterface.ExpandCollapse => this,
            System.Windows.Automation.Peers.PatternInterface.Toggle => null,
            _ => base.GetPattern(pattern),
        };

        protected override string GetClassNameCore() => nameof(ChevronToggle);

        public System.Windows.Automation.ExpandCollapseState ExpandCollapseState =>
            owner.IsChecked == true ? System.Windows.Automation.ExpandCollapseState.Expanded : System.Windows.Automation.ExpandCollapseState.Collapsed;

        public void Expand() { if (owner.IsEnabled) owner.IsChecked = true; }
        public void Collapse() { if (owner.IsEnabled) owner.IsChecked = false; }

        internal void Raise(bool expanded) =>
            RaisePropertyChangedEvent(System.Windows.Automation.ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                expanded ? System.Windows.Automation.ExpandCollapseState.Collapsed : System.Windows.Automation.ExpandCollapseState.Expanded,
                expanded ? System.Windows.Automation.ExpandCollapseState.Expanded : System.Windows.Automation.ExpandCollapseState.Collapsed);
    }

    private void RaiseState(bool expanded)
    {
        if (System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(this) is ChevronTogglePeer peer) peer.Raise(expanded);
    }

    /// <summary>The angle the chevron has or is turning to.</summary>
    public double TargetAngle { get; private set; }

    private void Turn(double angle)
    {
        TargetAngle = angle;
        if (turn is null) return;
        if (!Motion.Enabled) { turn.BeginAnimation(RotateTransform.AngleProperty, null); turn.Angle = angle; return; }
        turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, Motion.Normal) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
