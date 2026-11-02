// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>The window that can show the short "How to start as administrator" help (the main window).</summary>
public interface IAdminHelpHost
{
    Task ShowAdminHelpAsync();
}

/// <summary>
/// The one administrator notice: a single line ("Administrator rights needed. You can look, but changes are off.") with a "How to start as administrator" link that opens a short help dialog.
/// A page that has its own effect passes its own title and message; the long how-to text lives only in the dialog. With <see cref="ShowLink"/> false (the app runs from an unsafe folder,
/// where administrator rights do not help) the notice is the message alone. A warning, so it is announced assertively once when it opens.
/// </summary>
public sealed class AdminNotice : Control
{
    static AdminNotice()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(AdminNotice), new FrameworkPropertyMetadata(typeof(AdminNotice)));
        FocusableProperty.OverrideMetadata(typeof(AdminNotice), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(AdminNotice), new FrameworkPropertyMetadata(false));
        VisibilityProperty.OverrideMetadata(typeof(AdminNotice), new FrameworkPropertyMetadata(Visibility.Collapsed));
    }

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(AdminNotice),
        new FrameworkPropertyMetadata(false, (d, e) => ((AdminNotice)d).SetCurrentValue(VisibilityProperty, (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed)));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(AdminNotice), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(AdminNotice), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty LinkTextProperty = DependencyProperty.Register(nameof(LinkText), typeof(string), typeof(AdminNotice), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty LinkHelpProperty = DependencyProperty.Register(nameof(LinkHelp), typeof(string), typeof(AdminNotice), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty ShowLinkProperty = DependencyProperty.Register(nameof(ShowLink), typeof(bool), typeof(AdminNotice), new FrameworkPropertyMetadata(true));

    public static readonly RoutedEvent HowToRequestedEvent = EventManager.RegisterRoutedEvent(nameof(HowToRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(AdminNotice));
    public event RoutedEventHandler HowToRequested { add => AddHandler(HowToRequestedEvent, value); remove => RemoveHandler(HowToRequestedEvent, value); }

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string LinkText { get => (string)GetValue(LinkTextProperty); set => SetValue(LinkTextProperty, value); }
    public string LinkHelp { get => (string)GetValue(LinkHelpProperty); set => SetValue(LinkHelpProperty, value); }
    public bool ShowLink { get => (bool)GetValue(ShowLinkProperty); set => SetValue(ShowLinkProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Link") is System.Windows.Controls.Primitives.ButtonBase link) link.Click += (_, _) => OpenHowTo();
    }

    /// <summary>Opens the help dialog through the window that hosts the page, and tells listeners that it was asked for.</summary>
    public void OpenHowTo()
    {
        RaiseEvent(new RoutedEventArgs(HowToRequestedEvent, this));
        if (Window.GetWindow(this) is IAdminHelpHost host) _ = host.ShowAdminHelpAsync();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NoticePeer(this);

    private sealed class NoticePeer(AdminNotice owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(AdminNotice);
        protected override string GetNameCore() => (owner.Title + " " + owner.Message).Trim();
        protected override bool IsControlElementCore() => true;
    }
}
