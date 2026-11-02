// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// A button with a menu: the main part (icon and text) runs the command, the small arrow part opens the menu below it. The two parts look like one control, as in the Windows 11 split button.
/// The menu is a normal <see cref="ContextMenu"/>; its items bind to the data context of the button.
/// </summary>
[TemplatePart(Name = "PART_Main", Type = typeof(ButtonBase))]
[TemplatePart(Name = "PART_Drop", Type = typeof(ButtonBase))]
public sealed class MenuSplitButton : Control
{
    static MenuSplitButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(MenuSplitButton), new FrameworkPropertyMetadata(typeof(MenuSplitButton)));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MenuSplitButton), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(MenuSplitButton), new FrameworkPropertyMetadata(SymbolRegular.Empty));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(System.Windows.Input.ICommand), typeof(MenuSplitButton), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(MenuSplitButton), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty MenuProperty = DependencyProperty.Register(nameof(Menu), typeof(ContextMenu), typeof(MenuSplitButton), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty MainNameProperty = DependencyProperty.Register(nameof(MainName), typeof(string), typeof(MenuSplitButton), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty MenuNameProperty = DependencyProperty.Register(nameof(MenuName), typeof(string), typeof(MenuSplitButton), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty MainHelpProperty = DependencyProperty.Register(nameof(MainHelp), typeof(string), typeof(MenuSplitButton), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty MenuHelpProperty = DependencyProperty.Register(nameof(MenuHelp), typeof(string), typeof(MenuSplitButton), new FrameworkPropertyMetadata(""));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public SymbolRegular Symbol { get => (SymbolRegular)GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public System.Windows.Input.ICommand? Command { get => (System.Windows.Input.ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    public ContextMenu? Menu { get => (ContextMenu?)GetValue(MenuProperty); set => SetValue(MenuProperty, value); }
    /// <summary>The name of the main part for assistive technology (for example "Clean Temporary files").</summary>
    public string MainName { get => (string)GetValue(MainNameProperty); set => SetValue(MainNameProperty, value); }
    public string MenuName { get => (string)GetValue(MenuNameProperty); set => SetValue(MenuNameProperty, value); }
    public string MainHelp { get => (string)GetValue(MainHelpProperty); set => SetValue(MainHelpProperty, value); }
    public string MenuHelp { get => (string)GetValue(MenuHelpProperty); set => SetValue(MenuHelpProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Drop") is ButtonBase drop) drop.Click += (_, _) => OpenMenu(drop);
    }

    private void OpenMenu(FrameworkElement target)
    {
        if (Menu is not { } menu) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
