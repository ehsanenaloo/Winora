// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// Makes <see cref="AutomationProperties.LiveSettingProperty"/> work. WPF only reports the setting; a screen reader announces a live region when the
/// element raises LiveRegionChanged, which nothing did, so banners, results, progress lines and toasts were silent. Every loaded element with a live
/// setting raises the event when it becomes visible and when its text or name changes.
/// </summary>
public static class LiveRegions
{
    private static bool registered;
    private static readonly DependencyPropertyDescriptor TextDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    private static readonly DependencyPropertyDescriptor NameDescriptor = DependencyPropertyDescriptor.FromProperty(AutomationProperties.NameProperty, typeof(FrameworkElement));

    /// <summary>Raised for each announcement, with the element; for tests.</summary>
    public static event Action<UIElement>? Announced;

    public static void Register()
    {
        if (registered) return;
        registered = true;
        // Loaded reaches only elements with their own handler, so the hook is the live setting itself: it is set (from XAML, a style or code)
        // on every element that should speak.
        AutomationProperties.LiveSettingProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(AutomationLiveSetting.Off, OnLiveSettingChanged));
    }

    private static bool IsLive(DependencyObject d) => AutomationProperties.GetLiveSetting(d) != AutomationLiveSetting.Off;

    private static void OnLiveSettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.IsVisibleChanged -= OnVisibleChanged; element.Loaded -= OnLoaded; element.Unloaded -= OnUnloaded;
        Unwatch(element);
        if (!IsLive(element)) return;
        element.IsVisibleChanged += OnVisibleChanged;
        // Value-changed subscriptions are held by a static table, so they exist only while the element is loaded (no leak of closed pages).
        element.Loaded += OnLoaded; element.Unloaded += OnUnloaded;
        if (element.IsLoaded) Watch(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) { if (sender is FrameworkElement element) { Unwatch(element); Watch(element); } }
    private static void OnUnloaded(object sender, RoutedEventArgs e) { if (sender is FrameworkElement element) Unwatch(element); }

    private static void Watch(FrameworkElement element)
    {
        if (element is TextBlock) TextDescriptor.AddValueChanged(element, OnChanged);
        NameDescriptor.AddValueChanged(element, OnChanged);
    }

    private static void Unwatch(FrameworkElement element)
    {
        if (element is TextBlock) TextDescriptor.RemoveValueChanged(element, OnChanged);
        NameDescriptor.RemoveValueChanged(element, OnChanged);
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) { if (e.NewValue is true) Announce(sender as UIElement); }
    private static void OnChanged(object? sender, EventArgs e) => Announce(sender as UIElement);

    /// <summary>Raises LiveRegionChanged for a visible live element; the element can also be announced from code.</summary>
    public static void Announce(UIElement? element)
    {
        if (element is null || !element.IsVisible || !IsLive(element)) return;
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer is null) return;
        Announced?.Invoke(element);
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
