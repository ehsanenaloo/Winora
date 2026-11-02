// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// Two stacked panels, "Before" with a minus marker and "After" with a plus marker, on the sunken surface. The markers are text, so colour is never the only cue.
/// One group for assistive technology: "Change: now X, after Y".
/// </summary>
public sealed class DiffView : Control
{
    static DiffView()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DiffView), new FrameworkPropertyMetadata(typeof(DiffView)));
        FocusableProperty.OverrideMetadata(typeof(DiffView), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(DiffView), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(nameof(Before), typeof(string), typeof(DiffView), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(nameof(After), typeof(string), typeof(DiffView), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty BeforeLabelProperty = DependencyProperty.Register(nameof(BeforeLabel), typeof(string), typeof(DiffView), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty AfterLabelProperty = DependencyProperty.Register(nameof(AfterLabel), typeof(string), typeof(DiffView), new FrameworkPropertyMetadata(""));

    public string Before { get => (string)GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public string After { get => (string)GetValue(AfterProperty); set => SetValue(AfterProperty, value); }
    public string BeforeLabel { get => (string)GetValue(BeforeLabelProperty); set => SetValue(BeforeLabelProperty, value); }
    public string AfterLabel { get => (string)GetValue(AfterLabelProperty); set => SetValue(AfterLabelProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => new DiffPeer(this);

    private sealed class DiffPeer(DiffView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(DiffView);
        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : $"{owner.BeforeLabel}: {owner.Before}, {owner.AfterLabel}: {owner.After}";
        protected override bool IsControlElementCore() => true;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
