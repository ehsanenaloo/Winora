// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Winora.Desktop.Controls;

/// <summary>
/// Access keys (Alt plus an underlined letter) for buttons whose text comes from a resource. The letter is chosen from the text at run time, so each language
/// keeps its own wording: the first Latin letter that no other button of the same group already uses. Text without a Latin letter (Persian) gets no access key;
/// an Alt+letter on a Persian keyboard layout is not a habit users have, and an underline inside joined Persian letters breaks the word shape.
/// </summary>
public static class AccessKeys
{
    /// <summary>The first A-Z letter of the text that is not in <paramref name="taken"/>, in upper case; null when there is none.</summary>
    public static char? Pick(string? text, ICollection<char>? taken = null)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var c in text)
        {
            var upper = char.ToUpperInvariant(c);
            if (upper is < 'A' or > 'Z') continue;
            if (taken is not null && taken.Contains(upper)) continue;
            return upper;
        }
        return null;
    }

    /// <summary>The access text for a label: the first matching letter gets the underscore. Underscores of the label itself are doubled.</summary>
    public static string Mark(string text, char key)
    {
        var at = text.IndexOf(key.ToString(), StringComparison.OrdinalIgnoreCase);
        if (at < 0) return text.Replace("_", "__");
        return text[..at].Replace("_", "__") + "_" + text[at] + text[(at + 1)..].Replace("_", "__");
    }

    /// <summary>The label without any access key marker; plain text passes through.</summary>
    public static string Plain(object? content) => content switch
    {
        AccessText a => a.Text.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_"),
        string s => s,
        _ => "",
    };

    /// <summary>The letter of the access key a button shows, or null.</summary>
    public static char? KeyOf(object? content)
    {
        if (content is not AccessText a) return null;
        for (var i = 0; i < a.Text.Length - 1; i++)
        {
            if (a.Text[i] != '_') continue;
            if (a.Text[i + 1] == '_') { i++; continue; }
            return char.ToUpperInvariant(a.Text[i + 1]);
        }
        return null;
    }

    /// <summary>Shows <paramref name="text"/> on the button; with a key the matching letter is underlined and Alt+key presses the button. The accessible name stays the plain text.</summary>
    public static void Apply(ContentControl button, string text, char? key)
    {
        if (key is not { } k || text.IndexOf(k.ToString(), StringComparison.OrdinalIgnoreCase) < 0) { button.Content = text; return; }
        button.Content = new AccessText { Text = Mark(text, k) };
        if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)))
            System.Windows.Automation.AutomationProperties.SetName(button, text);
    }

    /// <summary>True when the element reads left to right: access keys are offered there only.</summary>
    public static bool Offered(FrameworkElement element) => element.FlowDirection == FlowDirection.LeftToRight;


    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.RegisterAttached("SourceText", typeof(string), typeof(AccessKeys), new PropertyMetadata(null, OnSourceTextChanged));
    private static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached("Group", typeof(Group), typeof(AccessKeys));

    /// <summary>The plain label a followed button shows. The button's own Content binding is moved here, so the label keeps following the language.</summary>
    public static string? GetSourceText(DependencyObject d) => (string?)d.GetValue(SourceTextProperty);
    public static void SetSourceText(DependencyObject d, string? value) => d.SetValue(SourceTextProperty, value);

    private static void OnSourceTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => (d.GetValue(GroupProperty) as Group)?.Refresh();

    /// <summary>The buttons under <paramref name="root"/> (itself included) in visual order.</summary>
    public static List<Button> ButtonsIn(DependencyObject? root)
    {
        var found = new List<Button>();
        if (root is null) return found;
        if (root is Button own) found.Add(own);
        Collect(root, found);
        return found;
    }

    /// <summary>
    /// Buttons whose text comes from a binding or a literal, in one place of the window (a page header). Each gets its own letter, in order.
    /// The group follows text changes (a language switch) and the direction of its owner.
    /// </summary>
    public sealed class Group(FrameworkElement owner)
    {
        private readonly List<Button> buttons = [];

        public void Add(IEnumerable<Button> candidates)
        {
            foreach (var button in candidates)
            {
                if (buttons.Contains(button) || !(button.Content is string || BindingOperations.GetBinding(button, ContentControl.ContentProperty) is not null)) continue;
                buttons.Add(button);
                button.SetValue(GroupProperty, this);
                if (BindingOperations.GetBinding(button, ContentControl.ContentProperty) is { } binding) button.SetBinding(SourceTextProperty, Clone(binding));
                else button.SetValue(SourceTextProperty, (string)button.Content);
            }
            Refresh();
        }

        public void Refresh() =>
            Assign(Offered(owner), buttons.Where(b => !string.IsNullOrEmpty(GetSourceText(b))).Select(b => ((ContentControl)b, GetSourceText(b)!)).ToList());

        private static Binding Clone(Binding b)
        {
            var copy = new Binding { Path = b.Path, Mode = BindingMode.OneWay, Converter = b.Converter, ConverterParameter = b.ConverterParameter, StringFormat = b.StringFormat };
            if (b.Source is not null) copy.Source = b.Source;
            if (b.RelativeSource is not null) copy.RelativeSource = b.RelativeSource;
            if (b.ElementName is not null) copy.ElementName = b.ElementName;
            return copy;
        }
    }
    /// <summary>
    /// Gives each button of a group its own letter, in order, and shows the text. A button whose text has no free letter shows plain text.
    /// <paramref name="reserved"/> holds letters already used elsewhere (they are skipped).
    /// </summary>
    public static void Assign(bool enabled, IEnumerable<(ContentControl Button, string Text)> group, ICollection<char>? reserved = null)
    {
        var taken = new HashSet<char>(reserved ?? []);
        foreach (var (button, text) in group)
        {
            var key = enabled ? Pick(text, taken) : null;
            if (key is { } k) taken.Add(k);
            Apply(button, text, key);
        }
    }

    /// <summary>
    /// The buttons of a dialog (primary, secondary, close) get their own letters. Buttons are found by their text; those inside the dialog content are left alone.
    /// </summary>
    public static void ForDialog(Wpf.Ui.Controls.ContentDialog dialog, bool enabled)
    {
        var texts = new[] { dialog.PrimaryButtonText, dialog.SecondaryButtonText, dialog.CloseButtonText }.Where(t => !string.IsNullOrEmpty(t)).ToList();
        var buttons = new List<Button>();
        Collect(dialog, buttons);
        var content = dialog.Content as DependencyObject;
        var group = new List<(ContentControl, string)>();
        foreach (var text in texts)
            if (buttons.FirstOrDefault(b => Plain(b.Content) == text && !IsInside(content, b)) is { } button) group.Add((button, text));
        Assign(enabled, group);
    }

    private static bool IsInside(DependencyObject? ancestor, DependencyObject node)
    {
        if (ancestor is null) return false;
        for (var current = node; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static void Collect(DependencyObject root, List<Button> into)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button b) into.Add(b);
            Collect(child, into);
        }
    }
}
