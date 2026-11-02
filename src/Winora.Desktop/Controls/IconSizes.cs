// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// The icon sizes of the app: 10 inside a badge on a navigation item, 14 inside chips and small marks, 16 in buttons, menus and the title bar, 24 as the icon of a row, card or heading, 32 and more for the picture of an empty state
/// or a plate. A test checks that the XAML uses only these. A button with text gets 20 (like the Windows 11 buttons with an icon), an icon-only button 16, unless the icon states its own size.
/// </summary>
public static class IconSizes
{
    public const double Badge = 10, Inline = 14, Control = 16, Button = 20, Lead = 24, Hero = 32;
    public static readonly IReadOnlyList<double> All = [Badge, Inline, Control, Button, Lead, Hero, 40, 48, 64];

    private static bool registered;

    /// <summary>Sizes the icon of every Wpf.Ui button (and of every menu item) once it is loaded.</summary>
    public static void Register()
    {
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(Wpf.Ui.Controls.Button), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Wpf.Ui.Controls.Button { Icon: SymbolIcon icon, Content: not null }) Size(icon, Button); else if (s is Wpf.Ui.Controls.Button { Icon: SymbolIcon plain }) Size(plain, Control);
        }));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.MenuItem), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is System.Windows.Controls.MenuItem { Icon: SymbolIcon icon }) Size(icon, Control);
        }));
    }

    private static void Size(SymbolIcon icon, double size)
    {
        var source = DependencyPropertyHelper.GetValueSource(icon, System.Windows.Documents.TextElement.FontSizeProperty).BaseValueSource;
        if (source is BaseValueSource.Default or BaseValueSource.Inherited or BaseValueSource.Style or BaseValueSource.DefaultStyle or BaseValueSource.ImplicitStyleReference) icon.FontSize = size;
    }
}
