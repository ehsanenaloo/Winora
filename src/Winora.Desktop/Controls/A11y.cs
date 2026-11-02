// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;

namespace Winora.Desktop.Controls;

/// <summary>
/// The accessible name of a control that repeats on every row: "label, item" (for example "Export, Gaming"). A screen reader otherwise hears the
/// same "Export" on every card. Set <see cref="LabelProperty"/> and <see cref="ItemProperty"/> instead of AutomationProperties.Name; the name
/// follows both, so it changes with the language.
/// </summary>
public static class A11y
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached("Label", typeof(string), typeof(A11y), new PropertyMetadata(null, OnChanged));
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached("Item", typeof(string), typeof(A11y), new PropertyMetadata(null, OnChanged));

    public static string? GetLabel(DependencyObject d) => (string?)d.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject d, string? value) => d.SetValue(LabelProperty, value);
    public static string? GetItem(DependencyObject d) => (string?)d.GetValue(ItemProperty);
    public static void SetItem(DependencyObject d, string? value) => d.SetValue(ItemProperty, value);

    /// <summary>The composed name: both parts when there are both, otherwise the one that is there.</summary>
    public static string Compose(string? label, string? item) =>
        string.IsNullOrWhiteSpace(item) ? label ?? "" : string.IsNullOrWhiteSpace(label) ? item : $"{label}, {item}";

    private static LocalizationService? names;
    internal static LocalizationService? Names => names;

    /// <summary>Set by the window so the name follows the language. A bar takes it when it loads.</summary>
    public static void NameUnnamedProgress(LocalizationService text) => names = text;

    public static readonly DependencyProperty AutoNameProperty = DependencyProperty.RegisterAttached("AutoName", typeof(bool), typeof(A11y), new PropertyMetadata(false, OnAutoNameChanged));
    public static bool GetAutoName(DependencyObject d) => (bool)d.GetValue(AutoNameProperty);
    public static void SetAutoName(DependencyObject d, bool value) => d.SetValue(AutoNameProperty, value);

    /// <summary>
    /// Names the progress bars that no page named: "Progress", and the value is read with it. (Busy rings have no automation peer in WPF-UI and every ring in the pages is named; AccessibilityTests checks that.) Bars in a row sit next to text
    /// that already says the number (a row percent, a disk usage line), so the generic name is enough there. The implicit style in App.xaml turns this on.
    /// </summary>
    private static void OnAutoNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        element.Loaded += OnProgressLoaded;
    }

    private static void OnProgressLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject d || names is null) return;
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(d)) || AutomationProperties.GetLabeledBy(d) is not null) return;
        AutomationProperties.SetName(d, names["ProgressName"]);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(AutomationProperties.NameProperty, Compose(GetLabel(d), GetItem(d)));
}
