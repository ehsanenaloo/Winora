// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// The top of every page: title (the Level 1 heading that takes keyboard focus after navigation), subtitle, an action area at the end side and a meta line
/// such as "Last scanned 10:42". Narrow headers move the actions below the text.
/// </summary>
public sealed class PageHeader : Control
{
    static PageHeader()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PageHeader), new FrameworkPropertyMetadata(typeof(PageHeader)));
        FocusableProperty.OverrideMetadata(typeof(PageHeader), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(PageHeader), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Below this width the actions wrap under the text.</summary>
    public const double CompactWidth = 520;

    public static readonly DependencyProperty BreadcrumbProperty = DependencyProperty.Register(nameof(Breadcrumb), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata("", (d, e) => ((PageHeader)d).OnSubtitleChanged((string?)e.NewValue ?? "")));
    private static readonly DependencyPropertyKey SummaryTextPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SummaryText), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty SummaryTextProperty = SummaryTextPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey AboutTextPropertyKey = DependencyProperty.RegisterReadOnly(nameof(AboutText), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty AboutTextProperty = AboutTextPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey HasAboutPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasAbout), typeof(bool), typeof(PageHeader), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasAboutProperty = HasAboutPropertyKey.DependencyProperty;
    public static readonly DependencyProperty IsAboutOpenProperty = DependencyProperty.Register(nameof(IsAboutOpen), typeof(bool), typeof(PageHeader), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty AboutLabelProperty = DependencyProperty.Register(nameof(AboutLabel), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty AboutHelpProperty = DependencyProperty.Register(nameof(AboutHelp), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty MetaTextProperty = DependencyProperty.Register(nameof(MetaText), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata("", (d, e) => d.SetValue(HasMetaPropertyKey, !string.IsNullOrEmpty((string?)e.NewValue))));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));
    private static readonly DependencyPropertyKey HasSubtitlePropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasSubtitle), typeof(bool), typeof(PageHeader), new FrameworkPropertyMetadata(false));
    private static readonly DependencyPropertyKey HasMetaPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasMeta), typeof(bool), typeof(PageHeader), new FrameworkPropertyMetadata(false));
    private static readonly DependencyPropertyKey IsCompactPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsCompact), typeof(bool), typeof(PageHeader), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(PageHeader), new FrameworkPropertyMetadata(SymbolRegular.Empty, (d, e) => d.SetValue(HasIconPropertyKey, (SymbolRegular)e.NewValue != SymbolRegular.Empty)));
    private static readonly DependencyPropertyKey HasIconPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasIcon), typeof(bool), typeof(PageHeader), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasIconProperty = HasIconPropertyKey.DependencyProperty;
    public static readonly DependencyProperty HasSubtitleProperty = HasSubtitlePropertyKey.DependencyProperty;
    public static readonly DependencyProperty HasMetaProperty = HasMetaPropertyKey.DependencyProperty;
    public static readonly DependencyProperty IsCompactProperty = IsCompactPropertyKey.DependencyProperty;

    /// <summary>The page icon shown in the tile before the title. Filled from the page view model the header sits in (<see cref="PageIcons"/>); empty means none.</summary>
    public SymbolRegular Icon { get => (SymbolRegular)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool HasIcon => (bool)GetValue(HasIconProperty);
    /// <summary>Where the page is ("Tune › Customize"). Filled from the page view model the header sits in; empty means none.</summary>
    public string Breadcrumb { get => (string)GetValue(BreadcrumbProperty); set => SetValue(BreadcrumbProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>The text under the title. Its first sentence is the subtitle; the rest sits behind the "About this page" disclosure.</summary>
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    /// <summary>The first sentence of <see cref="Subtitle"/>.</summary>
    public string SummaryText => (string)GetValue(SummaryTextProperty);
    /// <summary>The rest of <see cref="Subtitle"/>, shown when the disclosure is open.</summary>
    public string AboutText => (string)GetValue(AboutTextProperty);
    public bool HasAbout => (bool)GetValue(HasAboutProperty);
    public bool IsAboutOpen { get => (bool)GetValue(IsAboutOpenProperty); set => SetValue(IsAboutOpenProperty, value); }
    /// <summary>Name of the disclosure button; bound to the page's language text.</summary>
    public string AboutLabel { get => (string)GetValue(AboutLabelProperty); set => SetValue(AboutLabelProperty, value); }
    public string AboutHelp { get => (string)GetValue(AboutHelpProperty); set => SetValue(AboutHelpProperty, value); }

    private void OnSubtitleChanged(string text)
    {
        SetValue(HasSubtitlePropertyKey, !string.IsNullOrEmpty(text));
        var (summary, rest) = SplitSubtitle(text);
        SetValue(SummaryTextPropertyKey, summary);
        SetValue(AboutTextPropertyKey, rest);
        SetValue(HasAboutPropertyKey, rest.Length > 0);
        if (rest.Length == 0) SetCurrentValue(IsAboutOpenProperty, false);
    }

    /// <summary>Splits a page text after its first sentence (a full stop, question or exclamation mark, also Arabic and Chinese forms, followed by a space). A text of one sentence has no rest.</summary>
    public static (string Summary, string Remainder) SplitSubtitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("", "");
        text = text.Trim();
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] is '.' or '!' or '?' or '؟' or '。' && char.IsWhiteSpace(text[i + 1]) && i >= 12)
            {
                var rest = text[(i + 1)..].Trim();
                return rest.Length == 0 ? (text, "") : (text[..(i + 1)], rest);
            }
        }
        return (text, "");
    }
    public string MetaText { get => (string)GetValue(MetaTextProperty); set => SetValue(MetaTextProperty, value); }
    /// <summary>One primary button, optional secondary buttons.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    /// <summary>False when the page sits inside another page that already has the Level 1 heading (a tab of Cleanup).</summary>
    public static readonly DependencyProperty ShowTitleProperty = DependencyProperty.Register(nameof(ShowTitle), typeof(bool), typeof(PageHeader), new FrameworkPropertyMetadata(true));
    public bool ShowTitle { get => (bool)GetValue(ShowTitleProperty); set => SetValue(ShowTitleProperty, value); }
    public bool HasSubtitle => (bool)GetValue(HasSubtitleProperty);
    public bool HasMeta => (bool)GetValue(HasMetaProperty);
    public bool IsCompact => (bool)GetValue(IsCompactProperty);
    /// <summary>Set by the page workspace when the window is short: the subtitle and the About disclosure are hidden so the fixed band leaves room for the page.</summary>
    public static readonly DependencyProperty IsShortProperty = DependencyProperty.Register(nameof(IsShort), typeof(bool), typeof(PageHeader), new FrameworkPropertyMetadata(false));
    public bool IsShort { get => (bool)GetValue(IsShortProperty); set => SetValue(IsShortProperty, value); }

    private System.ComponentModel.INotifyPropertyChanged? watched;
    private AccessKeys.Group? accessKeys;

    public PageHeader()
    {
        SetBinding(AboutLabelProperty, new System.Windows.Data.Binding("Text[PageAbout]") { FallbackValue = "" });
        SetBinding(AboutHelpProperty, new System.Windows.Data.Binding("Text[PageAboutHelp]") { FallbackValue = "" });
        DataContextChanged += (_, e) =>
        {
            if (watched is not null) watched.PropertyChanged -= OnPageChanged;
            watched = e.NewValue as PageViewModel;
            if (watched is not null) watched.PropertyChanged += OnPageChanged;
            RefreshBreadcrumb();
        };
        Unloaded += (_, _) => { if (watched is not null) watched.PropertyChanged -= OnPageChanged; };
        Loaded += (_, _) =>
        {
            // Alt+letter for the page actions (Refresh, Scan, Export). The buttons belong to the page; the header only gives them letters.
            accessKeys ??= new AccessKeys.Group(this);
            accessKeys.Add(AccessKeys.ButtonsIn(Actions as DependencyObject));
        };
        Loaded += (_, _) => { if (watched is not null) { watched.PropertyChanged -= OnPageChanged; watched.PropertyChanged += OnPageChanged; } RefreshBreadcrumb(); };
    }

    private void OnPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName is nameof(PageViewModel.Breadcrumb) or "" or null) RefreshBreadcrumb(); }
    private void RefreshBreadcrumb()
    {
        Breadcrumb = DataContext is PageViewModel page ? page.Breadcrumb : "";
        if (DataContext is PageViewModel p && ReadLocalValue(IconProperty) == DependencyProperty.UnsetValue) SetCurrentValue(IconProperty, IconKeys.SymbolOf(PageIcons.For(p.Key)));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var compact = !double.IsInfinity(constraint.Width) && constraint.Width < CompactWidth;
        if (compact != IsCompact) SetValue(IsCompactPropertyKey, compact);
        return base.MeasureOverride(constraint);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HeaderPeer(this);

    private sealed class HeaderPeer(PageHeader owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(PageHeader);
        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.Title;
        protected override string GetHelpTextCore() => AutomationProperties.GetHelpText(owner) is { Length: > 0 } help ? help : owner.Subtitle;
        protected override bool IsControlElementCore() => true;
    }
}

public enum BannerKind { Auto, AdminNeeded, UnsafeLocation, ProtectedByPolicy, RestartNeeded }

/// <summary>
/// A tinted notice under the page header. It is a WPF-UI info bar with the Winora status colours, a status icon and a named close button.
/// Info and success are polite live regions; warning and error are assertive. The kind picks the icon of the standard notices (administrator needed, unsafe location,
/// managed by policy, restart needed); the page sets the severity.
/// </summary>
public sealed class InfoBanner : InfoBar
{
    static InfoBanner()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(InfoBanner), new FrameworkPropertyMetadata(typeof(InfoBanner)));
        SeverityProperty.OverrideMetadata(typeof(InfoBanner), new FrameworkPropertyMetadata(InfoBarSeverity.Informational, (d, e) => ApplyLive(d, (InfoBarSeverity)e.NewValue)));
    }

    public InfoBanner() => ApplyLive(this, Severity);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(BannerKind), typeof(InfoBanner),
        new FrameworkPropertyMetadata(BannerKind.Auto));
    public static readonly DependencyProperty CloseTextProperty = DependencyProperty.Register(nameof(CloseText), typeof(string), typeof(InfoBanner), new FrameworkPropertyMetadata(""));

    public BannerKind Kind { get => (BannerKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    /// <summary>Accessible name of the close button.</summary>
    public string CloseText { get => (string)GetValue(CloseTextProperty); set => SetValue(CloseTextProperty, value); }

    private static void ApplyLive(DependencyObject d, InfoBarSeverity severity) =>
        d.SetCurrentValue(AutomationProperties.LiveSettingProperty, severity is InfoBarSeverity.Warning or InfoBarSeverity.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);

    protected override AutomationPeer OnCreateAutomationPeer() => new BannerPeer(this);

    private sealed class BannerPeer(InfoBanner owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(InfoBanner);
        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.Title;
    }
}

/// <summary>Nothing to show: an icon, a headline, one sentence and at most one action.</summary>
public sealed class EmptyState : Control
{
    static EmptyState()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));
        FocusableProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(EmptyState), new FrameworkPropertyMetadata(SymbolRegular.Info24));
    public static readonly DependencyProperty HeadlineProperty = DependencyProperty.Register(nameof(Headline), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata("", (d, e) => d.SetValue(HasMessagePropertyKey, !string.IsNullOrEmpty((string?)e.NewValue))));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata("", (d, e) => d.SetValue(HasActionPropertyKey, !string.IsNullOrEmpty((string?)e.NewValue))));
    public static readonly DependencyProperty ActionHelpProperty = DependencyProperty.Register(nameof(ActionHelp), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(EmptyState), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty ActionIconProperty = DependencyProperty.Register(nameof(ActionIcon), typeof(SymbolRegular), typeof(EmptyState), new FrameworkPropertyMetadata(SymbolRegular.Empty));
    private static readonly DependencyPropertyKey HasMessagePropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasMessage), typeof(bool), typeof(EmptyState), new FrameworkPropertyMetadata(false));
    private static readonly DependencyPropertyKey HasActionPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasAction), typeof(bool), typeof(EmptyState), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasMessageProperty = HasMessagePropertyKey.DependencyProperty;
    public static readonly DependencyProperty HasActionProperty = HasActionPropertyKey.DependencyProperty;

    public SymbolRegular Icon { get => (SymbolRegular)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Headline { get => (string)GetValue(HeadlineProperty); set => SetValue(HeadlineProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public string ActionHelp { get => (string)GetValue(ActionHelpProperty); set => SetValue(ActionHelpProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
    public SymbolRegular ActionIcon { get => (SymbolRegular)GetValue(ActionIconProperty); set => SetValue(ActionIconProperty, value); }
    public bool HasMessage => (bool)GetValue(HasMessageProperty);
    public bool HasAction => (bool)GetValue(HasActionProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new StatePeer(this, nameof(EmptyState), static o => ((EmptyState)o).Headline);
}

/// <summary>A read or a page failed: warning icon, plain headline, cause, one next step and optional technical details (reference code).</summary>
public sealed class ErrorState : Control
{
    static ErrorState()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ErrorState), new FrameworkPropertyMetadata(typeof(ErrorState)));
        FocusableProperty.OverrideMetadata(typeof(ErrorState), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ErrorState), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty HeadlineProperty = DependencyProperty.Register(nameof(Headline), typeof(string), typeof(ErrorState), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(ErrorState), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(ErrorState), new FrameworkPropertyMetadata("", (d, e) => d.SetValue(HasActionPropertyKey, !string.IsNullOrEmpty((string?)e.NewValue))));
    public static readonly DependencyProperty ActionHelpProperty = DependencyProperty.Register(nameof(ActionHelp), typeof(string), typeof(ErrorState), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(ErrorState), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty DetailsProperty = DependencyProperty.Register(nameof(Details), typeof(string), typeof(ErrorState), new FrameworkPropertyMetadata("", (d, e) => d.SetValue(HasDetailsPropertyKey, !string.IsNullOrEmpty((string?)e.NewValue))));
    public static readonly DependencyProperty DetailsHeaderProperty = DependencyProperty.Register(nameof(DetailsHeader), typeof(string), typeof(ErrorState), new FrameworkPropertyMetadata(""));
    private static readonly DependencyPropertyKey HasActionPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasAction), typeof(bool), typeof(ErrorState), new FrameworkPropertyMetadata(false));
    private static readonly DependencyPropertyKey HasDetailsPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasDetails), typeof(bool), typeof(ErrorState), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasActionProperty = HasActionPropertyKey.DependencyProperty;
    public static readonly DependencyProperty HasDetailsProperty = HasDetailsPropertyKey.DependencyProperty;

    public string Headline { get => (string)GetValue(HeadlineProperty); set => SetValue(HeadlineProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public string ActionHelp { get => (string)GetValue(ActionHelpProperty); set => SetValue(ActionHelpProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
    /// <summary>Technical text such as the reference code. Shown selectable, left to right, under an expander.</summary>
    public string Details { get => (string)GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }
    public string DetailsHeader { get => (string)GetValue(DetailsHeaderProperty); set => SetValue(DetailsHeaderProperty, value); }
    public bool HasAction => (bool)GetValue(HasActionProperty);
    public bool HasDetails => (bool)GetValue(HasDetailsProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new StatePeer(this, nameof(ErrorState), static o => ((ErrorState)o).Headline);
}

internal sealed class StatePeer(FrameworkElement element, string className, Func<FrameworkElement, string> name) : FrameworkElementAutomationPeer(element)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    protected override string GetClassNameCore() => className;
    protected override string GetNameCore() => AutomationProperties.GetName((FrameworkElement)Owner) is { Length: > 0 } explicitName ? explicitName : name((FrameworkElement)Owner);
    protected override bool IsControlElementCore() => true;
}

/// <summary>
/// Placeholder blocks shaped like a setting card while a read is running. The text is always visible and carries the meaning; the blocks pulse for at most five
/// seconds and only appear after <see cref="ShowDelay"/>. With reduced motion they are static. Nothing runs while the control is hidden.
/// </summary>
public sealed class LoadingSkeleton : Control
{
    static LoadingSkeleton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(LoadingSkeleton), new FrameworkPropertyMetadata(typeof(LoadingSkeleton)));
        FocusableProperty.OverrideMetadata(typeof(LoadingSkeleton), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(LoadingSkeleton), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(LoadingSkeleton), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(int), typeof(LoadingSkeleton), new FrameworkPropertyMetadata(3, (d, _) => ((LoadingSkeleton)d).Rebuild()));
    public static readonly DependencyProperty ShowDelayProperty = DependencyProperty.Register(nameof(ShowDelay), typeof(TimeSpan), typeof(LoadingSkeleton), new FrameworkPropertyMetadata(TimeSpan.FromMilliseconds(250)));

    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public int Rows { get => (int)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public TimeSpan ShowDelay { get => (TimeSpan)GetValue(ShowDelayProperty); set => SetValue(ShowDelayProperty, value); }

    /// <summary>True once the delay has passed; the blocks are drawn only then.</summary>
    public bool BlocksShown => blocks?.Visibility == Visibility.Visible;

    private Panel? blocks;
    private DispatcherTimer? delay;
    private Action stopPulse = static () => { };

    public LoadingSkeleton()
    {
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Begin(); else End(); };
        Unloaded += (_, _) => End();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        blocks = GetTemplateChild("PART_Blocks") as Panel;
        Rebuild();
        if (IsVisible) Begin();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StatePeer(this, nameof(LoadingSkeleton), static o => ((LoadingSkeleton)o).Message);

    private void Rebuild()
    {
        if (blocks is null) return;
        blocks.Children.Clear();
        for (var i = 0; i < Math.Max(1, Rows); i++)
        {
            var row = new Border { Margin = this.Space("B8"), Padding = this.Space("A16"), CornerRadius = (CornerRadius)FindResource("WinoraCornerRadius") };
            row.SetResourceReference(Border.BackgroundProperty, "WinoraSurfaceSunkenBrush");
            var stack = new StackPanel();
            stack.Children.Add(Block(180, 14));
            stack.Children.Add(Block(320, 10, "T8"));
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = this.Space("T8") };
            chips.Children.Add(Block(72, 18, "R8")); chips.Children.Add(Block(72, 18));
            stack.Children.Add(chips);
            row.Child = stack;
            blocks.Children.Add(row);
        }
    }

    private Border Block(double width, double height, string margin = "None")
    {
        var block = new Border { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left, Margin = this.Space(margin), CornerRadius = (CornerRadius)FindResource("WinoraCornerRadiusSmall") };
        block.SetResourceReference(Border.BackgroundProperty, "WinoraCardBorderBrush");
        return block;
    }

    private void Begin()
    {
        End();
        if (blocks is null) return;
        blocks.Visibility = Visibility.Collapsed;
        if (ShowDelay <= TimeSpan.Zero || !Motion.Enabled) { ShowBlocks(); return; }
        delay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ShowDelay };
        delay.Tick += (_, _) => { delay?.Stop(); delay = null; ShowBlocks(); };
        delay.Start();
    }

    private void ShowBlocks()
    {
        if (blocks is null || !IsVisible) return;
        blocks.Visibility = Visibility.Visible;
        stopPulse = Motion.Pulse(blocks);
    }

    private void End()
    {
        delay?.Stop(); delay = null;
        stopPulse(); stopPulse = static () => { };
    }
}
