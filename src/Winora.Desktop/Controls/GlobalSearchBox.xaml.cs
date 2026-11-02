// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Winora.Desktop.Search;

namespace Winora.Desktop.Controls;

/// <summary>
/// The search box in the title bar. Keyboard focus stays in the text box while the arrow keys move through the results; the chosen row is announced. Enter opens the selected
/// row, or the first one. Escape closes the list, clears the text and gives focus back to the control that had it before Ctrl+K.
/// </summary>
public partial class GlobalSearchBox : UserControl
{
    private UIElement? returnTo;

    public GlobalSearchBox()
    {
        InitializeComponent();
        Box.LostKeyboardFocus += OnBoxLostFocus;
        Loaded += (_, _) => { if (DataContext is GlobalSearchModel model) model.PropertyChanged += OnModelChanged; };
    }

    private GlobalSearchModel? Model => DataContext as GlobalSearchModel;

    public TextBox TextBox => Box;
    public System.Windows.Controls.Primitives.Popup Popup => Results;

    /// <summary>Moves keyboard focus into the box (Ctrl+K). The control that had focus gets it back after Escape.</summary>
    public bool FocusBox(UIElement? previous)
    {
        returnTo = previous;
        var ok = Box.Focus();
        Box.SelectAll();
        if (Model is { HasQuery: true } model) model.IsOpen = true;
        return ok;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GlobalSearchModel.Selected) && Model?.Selected is { } row)
            Announcement.Text = row.AutomationName;
        else if (e.PropertyName == nameof(GlobalSearchModel.StatusText))
            Announcement.Text = Model?.StatusText ?? "";
    }

    private void OnBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OldFocus is UIElement old && !IsAncestorOf(old)) returnTo ??= old;
        if (Model is { HasQuery: true } model) model.IsOpen = true;
    }

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && (IsAncestorOf(next) || Results.Child is { } child && IsInside(child, next))) return;
        if (Model is { } model) model.IsOpen = false;
    }

    /// <summary>Tab from the box goes into the results: the first "Show all" (or "Clear search") button that is showing. False when there is none.</summary>
    public bool FocusFlyout()
    {
        if (!Results.IsOpen || Results.Child is not { } child) return false;
        foreach (var button in AccessKeys.ButtonsIn(child))
            if (button.IsVisible && button.IsEnabled && button.Focusable && button.Focus()) return true;
        return false;
    }

    /// <summary>Escape inside the results closes them and puts focus back in the box (the list stays closed until the next key).</summary>
    private void OnFlyoutKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Box.Focus();
        if (Model is { } model) model.IsOpen = false;
        e.Handled = true;
    }

    private void OnFlyoutLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not DependencyObject next || Results.Child is not { } child) return;
        if (IsInside(child, next) || ReferenceEquals(next, Box) || Box.IsAncestorOf(next)) return;
        if (Model is { } model) model.IsOpen = false;
    }

    private static bool IsInside(DependencyObject ancestor, DependencyObject node)
    {
        for (var current = node; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;
        switch (e.Key)
        {
            case Key.Down: model.IsOpen = model.HasQuery; model.Move(1); e.Handled = true; break;
            case Key.Up: model.IsOpen = model.HasQuery; model.Move(-1); e.Handled = true; break;
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None && FocusFlyout(): e.Handled = true; break;
            case Key.Enter: if (model.ActivateSelectedOrFirst()) { returnTo = null; } e.Handled = true; break;
            case Key.Escape:
                model.Clear();
                var target = returnTo; returnTo = null;
                if (target is { IsVisible: true, IsEnabled: true } && target.Focus()) { }
                else if (Window.GetWindow(this) is MainWindow main) main.FocusPageHeading();
                e.Handled = true;
                break;
        }
    }

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchRow row } && Model is { } model) { returnTo = null; model.Activate(row); }
    }
}
