// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Winora.Desktop.Controls;

/// <summary>
/// The "more" button at the end of every row, on settings and list pages. List rows offer Details. Setting rows add Reset to Windows default, Revert last change and Copy setting name: an item
/// is shown only when the page supplies its command and text. The button is the same on every page; the page supplies the commands, the row and the texts. The row itself opens the same dialog
/// with Enter or a double click.
/// </summary>
public partial class RowMenuButton : UserControl
{
    public RowMenuButton()
    {
        InitializeComponent();
        Loaded += (_, _) => RetryBindings();
    }

    // A row realized before its list is attached to the page cannot find the page yet; the binding of the page's texts and commands stays in error. Once the row is in the tree it binds again.
    private void RetryBindings()
    {
        foreach (var property in new[] { DetailsCommandProperty, DetailsTextProperty, MoreHelpProperty, MoreNameProperty, ResetCommandProperty, ResetTextProperty, UndoCommandProperty, UndoTextProperty, CopyCommandProperty, CopyTextProperty })
            if (System.Windows.Data.BindingOperations.GetBindingExpression(this, property) is { Status: System.Windows.Data.BindingStatus.PathError or System.Windows.Data.BindingStatus.Unattached } binding) SetBinding(property, binding.ParentBinding);
    }

    /// <summary>False on a list whose rows are reached with the arrow keys: the button is then skipped by Tab and Enter on the row does the same.</summary>
    public static readonly DependencyProperty TabbableProperty = DependencyProperty.Register(nameof(Tabbable), typeof(bool), typeof(RowMenuButton), new PropertyMetadata(true));
    public bool Tabbable { get => (bool)GetValue(TabbableProperty); set => SetValue(TabbableProperty, value); }

    public static readonly DependencyProperty DetailsCommandProperty = Register(nameof(DetailsCommand), typeof(ICommand), (c, v) => c.Apply(c.DetailsItem, v as ICommand, null, c.DetailsText));
    public static readonly DependencyProperty DetailsParameterProperty = Register(nameof(DetailsParameter), typeof(object), (c, v) => c.DetailsItem.CommandParameter = v);
    public static readonly DependencyProperty DetailsTextProperty = Register(nameof(DetailsText), typeof(string), (c, v) => c.Apply(c.DetailsItem, c.DetailsCommand, null, (string?)v));
    public static readonly DependencyProperty ResetCommandProperty = Register(nameof(ResetCommand), typeof(ICommand), (c, v) => c.Apply(c.ResetItem, v as ICommand, null, c.ResetText));
    public static readonly DependencyProperty ResetTextProperty = Register(nameof(ResetText), typeof(string), (c, v) => c.Apply(c.ResetItem, c.ResetCommand, null, (string?)v));
    public static readonly DependencyProperty UndoCommandProperty = Register(nameof(UndoCommand), typeof(ICommand), (c, v) => c.Apply(c.UndoItem, v as ICommand, null, c.UndoText));
    public static readonly DependencyProperty UndoTextProperty = Register(nameof(UndoText), typeof(string), (c, v) => c.Apply(c.UndoItem, c.UndoCommand, null, (string?)v));
    public static readonly DependencyProperty CopyCommandProperty = Register(nameof(CopyCommand), typeof(ICommand), (c, v) => c.Apply(c.CopyItem, v as ICommand, c.CopyParameter, c.CopyText));
    public static readonly DependencyProperty CopyParameterProperty = Register(nameof(CopyParameter), typeof(object), (c, v) => c.CopyItem.CommandParameter = v);
    public static readonly DependencyProperty CopyTextProperty = Register(nameof(CopyText), typeof(string), (c, v) => c.Apply(c.CopyItem, c.CopyCommand, c.CopyParameter, (string?)v));
    public static readonly DependencyProperty MoreNameProperty = Register(nameof(MoreName), typeof(string), (c, v) => AutomationProperties.SetName(c.Kebab, (string?)v ?? ""));
    public static readonly DependencyProperty MoreHelpProperty = Register(nameof(MoreHelp), typeof(string), (c, v) => AutomationProperties.SetHelpText(c.Kebab, (string?)v ?? ""));

    /// <summary>Runs when the Details item is chosen. No command, no item.</summary>
    public ICommand? DetailsCommand { get => (ICommand?)GetValue(DetailsCommandProperty); set => SetValue(DetailsCommandProperty, value); }
    public object? DetailsParameter { get => GetValue(DetailsParameterProperty); set => SetValue(DetailsParameterProperty, value); }
    public string DetailsText { get => (string)GetValue(DetailsTextProperty); set => SetValue(DetailsTextProperty, value); }
    /// <summary>Puts the setting back to the Windows default (settings rows only).</summary>
    public ICommand? ResetCommand { get => (ICommand?)GetValue(ResetCommandProperty); set => SetValue(ResetCommandProperty, value); }
    public string ResetText { get => (string)GetValue(ResetTextProperty); set => SetValue(ResetTextProperty, value); }
    /// <summary>Reverts the last change of the setting (settings rows only).</summary>
    public ICommand? UndoCommand { get => (ICommand?)GetValue(UndoCommandProperty); set => SetValue(UndoCommandProperty, value); }
    public string UndoText { get => (string)GetValue(UndoTextProperty); set => SetValue(UndoTextProperty, value); }
    /// <summary>Copies the name of the setting (settings rows only).</summary>
    public ICommand? CopyCommand { get => (ICommand?)GetValue(CopyCommandProperty); set => SetValue(CopyCommandProperty, value); }
    public object? CopyParameter { get => GetValue(CopyParameterProperty); set => SetValue(CopyParameterProperty, value); }
    public string CopyText { get => (string)GetValue(CopyTextProperty); set => SetValue(CopyTextProperty, value); }
    /// <summary>The accessible name of the button: "More options for X".</summary>
    public string MoreName { get => (string)GetValue(MoreNameProperty); set => SetValue(MoreNameProperty, value); }
    public string MoreHelp { get => (string)GetValue(MoreHelpProperty); set => SetValue(MoreHelpProperty, value); }

    private void Apply(MenuItem item, ICommand? command, object? parameter, string? text)
    {
        item.Command = command;
        if (parameter is not null) item.CommandParameter = parameter;
        item.Header = text ?? "";
        item.Visibility = command is not null && !string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static DependencyProperty Register(string name, Type type, Action<RowMenuButton, object?> apply) =>
        DependencyProperty.Register(name, type, typeof(RowMenuButton), new PropertyMetadata(type == typeof(string) ? "" : null, (d, e) => apply((RowMenuButton)d, e.NewValue)));

    public Button Button => Kebab;
}
