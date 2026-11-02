// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace Winora.Desktop.Controls;

/// <summary>
/// The one pager: "Newer", "Page 1 of 3", "Older". The page history block of a page and the History page use it. The two buttons are in the Tab order, the page text is a polite
/// live region, and the buttons keep their commands' enabled state. With <see cref="IsPaged"/> false (one page) it is collapsed.
/// </summary>
public sealed class Pager : Control
{
    static Pager()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Pager), new FrameworkPropertyMetadata(typeof(Pager)));
        FocusableProperty.OverrideMetadata(typeof(Pager), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Pager), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty PageTextProperty = DependencyProperty.Register(nameof(PageText), typeof(string), typeof(Pager), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty NewerTextProperty = DependencyProperty.Register(nameof(NewerText), typeof(string), typeof(Pager), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty OlderTextProperty = DependencyProperty.Register(nameof(OlderText), typeof(string), typeof(Pager), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty NewerHelpProperty = DependencyProperty.Register(nameof(NewerHelp), typeof(string), typeof(Pager), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty OlderHelpProperty = DependencyProperty.Register(nameof(OlderHelp), typeof(string), typeof(Pager), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty NewerCommandProperty = DependencyProperty.Register(nameof(NewerCommand), typeof(ICommand), typeof(Pager), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty OlderCommandProperty = DependencyProperty.Register(nameof(OlderCommand), typeof(ICommand), typeof(Pager), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty IsPagedProperty = DependencyProperty.Register(nameof(IsPaged), typeof(bool), typeof(Pager), new FrameworkPropertyMetadata(true, (d, e) => ((Pager)d).Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public string PageText { get => (string)GetValue(PageTextProperty); set => SetValue(PageTextProperty, value); }
    public string NewerText { get => (string)GetValue(NewerTextProperty); set => SetValue(NewerTextProperty, value); }
    public string OlderText { get => (string)GetValue(OlderTextProperty); set => SetValue(OlderTextProperty, value); }
    public string NewerHelp { get => (string)GetValue(NewerHelpProperty); set => SetValue(NewerHelpProperty, value); }
    public string OlderHelp { get => (string)GetValue(OlderHelpProperty); set => SetValue(OlderHelpProperty, value); }
    public ICommand? NewerCommand { get => (ICommand?)GetValue(NewerCommandProperty); set => SetValue(NewerCommandProperty, value); }
    public ICommand? OlderCommand { get => (ICommand?)GetValue(OlderCommandProperty); set => SetValue(OlderCommandProperty, value); }
    /// <summary>False when there is only one page: the pager is then collapsed.</summary>
    public bool IsPaged { get => (bool)GetValue(IsPagedProperty); set => SetValue(IsPagedProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => new PagerPeer(this);

    private sealed class PagerPeer(Pager owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(Pager);
        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.PageText;
        protected override bool IsControlElementCore() => true;
    }
}
