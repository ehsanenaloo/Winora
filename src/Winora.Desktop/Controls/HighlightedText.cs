// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Documents;

namespace Winora.Desktop.Controls;

/// <summary>A text block that shows the parts marked as a match in bold. The text of the block stays the plain title for assistive technology.</summary>
public sealed class HighlightedText : System.Windows.Controls.TextBlock
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(nameof(Parts), typeof(IReadOnlyList<(string Text, bool Match)>), typeof(HighlightedText),
        new FrameworkPropertyMetadata(null, (d, e) => ((HighlightedText)d).Rebuild()));

    public IReadOnlyList<(string Text, bool Match)>? Parts { get => (IReadOnlyList<(string Text, bool Match)>?)GetValue(PartsProperty); set => SetValue(PartsProperty, value); }

    private void Rebuild()
    {
        Inlines.Clear();
        foreach (var (text, match) in Parts ?? [])
            Inlines.Add(match ? new Bold(new Run(text)) : new Run(text));
    }
}
