// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// The title of a grouped list, drawn as the top of its card: a title, an optional one-line description and an optional action at the end. The rows of the group follow
/// directly under it and share its edge (<see cref="HasRows"/>: the bottom corners and the bottom edge are left to the rows). A foldable header has a chevron that turns.
/// </summary>
public sealed class CardHeader : Control
{
    static CardHeader()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CardHeader), new FrameworkPropertyMetadata(typeof(CardHeader)));
        FocusableProperty.OverrideMetadata(typeof(CardHeader), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(CardHeader), new FrameworkPropertyMetadata(false));
    }

    private static DependencyProperty Prop<T>(string name, T value = default!, FrameworkPropertyMetadataOptions options = FrameworkPropertyMetadataOptions.None, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(CardHeader), new FrameworkPropertyMetadata(value, options, changed));

    private static readonly DependencyPropertyKey HasDescriptionPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasDescription), typeof(bool), typeof(CardHeader), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasDescriptionProperty = HasDescriptionPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey HasCountPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasCount), typeof(bool), typeof(CardHeader), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasCountProperty = HasCountPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey HasActionsPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasActions), typeof(bool), typeof(CardHeader), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasActionsProperty = HasActionsPropertyKey.DependencyProperty;

    public static readonly DependencyProperty TitleProperty = Prop(nameof(Title), "", FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty DescriptionProperty = Prop(nameof(Description), "", FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => d.SetValue(HasDescriptionPropertyKey, !string.IsNullOrEmpty((string?)e.NewValue)));
    public static readonly DependencyProperty CountProperty = Prop(nameof(Count), "", FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => d.SetValue(HasCountPropertyKey, !string.IsNullOrEmpty((string?)e.NewValue)));
    public static readonly DependencyProperty ActionsProperty = Prop<object?>(nameof(Actions), null, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => d.SetValue(HasActionsPropertyKey, e.NewValue is not null));
    public static readonly DependencyProperty IsFoldableProperty = Prop(nameof(IsFoldable), false);
    public static readonly DependencyProperty IsOpenProperty = Prop(nameof(IsOpen), true);
    public static readonly DependencyProperty HasRowsProperty = Prop(nameof(HasRows), true);
    public static readonly DependencyProperty HeadingLevelProperty = Prop(nameof(IsHeading), true);
    public static readonly DependencyProperty ActionsBelowProperty = Prop(nameof(ActionsBelow), false);

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>One line under the title.</summary>
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    /// <summary>A short count at the end of the title row, for example "12 settings".</summary>
    public string Count { get => (string)GetValue(CountProperty); set => SetValue(CountProperty, value); }
    /// <summary>One control at the end of the header (a button or a menu).</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    /// <summary>The header folds its group: a chevron shows at the start.</summary>
    public bool IsFoldable { get => (bool)GetValue(IsFoldableProperty); set => SetValue(IsFoldableProperty, value); }
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    /// <summary>Rows follow under the header inside the same card: it keeps only its top corners and its top and side edges.</summary>
    public bool HasRows { get => (bool)GetValue(HasRowsProperty); set => SetValue(HasRowsProperty, value); }
    /// <summary>The title is a Level 2 heading (default).</summary>
    public bool IsHeading { get => (bool)GetValue(HeadingLevelProperty); set => SetValue(HeadingLevelProperty, value); }
    /// <summary>The actions take their own line under the text instead of sitting at the end (several wide buttons).</summary>
    public bool ActionsBelow { get => (bool)GetValue(ActionsBelowProperty); set => SetValue(ActionsBelowProperty, value); }
    public bool HasDescription => (bool)GetValue(HasDescriptionProperty);
    public bool HasCount => (bool)GetValue(HasCountProperty);
    public bool HasActions => (bool)GetValue(HasActionsProperty);
}
