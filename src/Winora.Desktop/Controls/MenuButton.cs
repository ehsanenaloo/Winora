// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Winora.Desktop.Controls;

/// <summary>
/// Makes a button open its own context menu with a click or with Enter and Space (a context menu opens only on a right click by default, which most people never try).
/// The menu opens below the button.
/// </summary>
public static class MenuButton
{
    public static readonly DependencyProperty OpensMenuOnClickProperty = DependencyProperty.RegisterAttached("OpensMenuOnClick", typeof(bool), typeof(MenuButton), new PropertyMetadata(false, OnChanged));

    public static bool GetOpensMenuOnClick(DependencyObject element) => (bool)element.GetValue(OpensMenuOnClickProperty);
    public static void SetOpensMenuOnClick(DependencyObject element, bool value) => element.SetValue(OpensMenuOnClickProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OnClick;
        if ((bool)e.NewValue) button.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
