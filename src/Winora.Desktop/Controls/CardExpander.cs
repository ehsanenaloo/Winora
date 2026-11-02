// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// A card whose header opens and closes its content. The header is a <see cref="ChevronToggle"/> (the chevron turns through <see cref="Motion"/>); the content is collapsed while closed.
/// Use it for a section that most people do not need, such as an Advanced group.
/// </summary>
public sealed class CardExpander : HeaderedContentControl
{
    static CardExpander()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CardExpander), new FrameworkPropertyMetadata(typeof(CardExpander)));
        FocusableProperty.OverrideMetadata(typeof(CardExpander), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(CardExpander), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(CardExpander),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <summary>Text under the title while the section is closed or open (for example the count or a short warning).</summary>
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(CardExpander), new FrameworkPropertyMetadata(""));
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(Wpf.Ui.Controls.SymbolRegular), typeof(CardExpander),
        new FrameworkPropertyMetadata(Wpf.Ui.Controls.SymbolRegular.Empty));
    public Wpf.Ui.Controls.SymbolRegular Icon { get => (Wpf.Ui.Controls.SymbolRegular)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <summary>The warning inside the open section (a High-risk group tells people to read before changing anything). Empty for none.</summary>
    public static readonly DependencyProperty WarningProperty = DependencyProperty.Register(nameof(Warning), typeof(string), typeof(CardExpander), new FrameworkPropertyMetadata(""));
    public string Warning { get => (string)GetValue(WarningProperty); set => SetValue(WarningProperty, value); }
}
