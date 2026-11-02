// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// The frame every page is built in. The header, the tab rail and the toolbar sit in a fixed band at the top; only the part below them scrolls, and it is the
/// only scroll area of the page. The slots are <see cref="Header"/>, <see cref="Tabs"/>, <see cref="Toolbar"/>, <see cref="Banners"/>, <see cref="Body"/> and
/// <see cref="Footer"/>; banners, body and footer scroll together. A page whose body is one long virtualized list sets <see cref="BodyScrolls"/> to false: the
/// body then gets the whole height under the band and its list is the one scroll area (the footer goes in as the last item of the list). The page edge follows
/// <see cref="PageInset"/>; the body is always as wide as the window.
/// A page is never a bare workspace: it is one of the five templates in <c>PageTemplates.cs</c>, and each template says which slots it owns (<see cref="Slots"/>).
/// A slot the template does not own, a header that is not a <see cref="PageHeader"/> and a scroll viewer as body are refused when they are set.
/// </summary>
public abstract class PageWorkspace : Control
{
    static PageWorkspace()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PageWorkspace), new FrameworkPropertyMetadata(typeof(PageWorkspace)));
        FocusableProperty.OverrideMetadata(typeof(PageWorkspace), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(PageWorkspace), new FrameworkPropertyMetadata(false));
    }

    /// <summary>The fixed band (with its edge) never takes more than this share of the height, so a short window or large text still leaves room for the body.</summary>
    public const double MaxBandShare = 0.55;
    /// <summary>A window shorter than this hides the page sentence and the About disclosure (<see cref="PageHeader.IsShort"/>).</summary>
    public const double ShortHeight = 650;

    private static DependencyProperty Slot(string name, PageSlots slot, bool band = false) => DependencyProperty.Register(name, typeof(object), typeof(PageWorkspace),
        new FrameworkPropertyMetadata(null, (d, e) =>
        {
            var workspace = (PageWorkspace)d;
            workspace.CheckSlot(name, slot, e.NewValue);
            if (band) workspace.UpdateBand();
        }));

    public static readonly DependencyProperty HeaderProperty = Slot(nameof(Header), PageSlots.Header, band: true);
    public static readonly DependencyProperty TabsProperty = Slot(nameof(Tabs), PageSlots.Tabs, band: true);
    public static readonly DependencyProperty ToolbarProperty = Slot(nameof(Toolbar), PageSlots.Toolbar, band: true);
    private static readonly DependencyPropertyKey HasBandPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasBand), typeof(bool), typeof(PageWorkspace), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasBandProperty = HasBandPropertyKey.DependencyProperty;
    public static readonly DependencyProperty PadBodyProperty = DependencyProperty.Register(nameof(PadBody), typeof(bool), typeof(PageWorkspace), new FrameworkPropertyMetadata(true, (d, _) => ((PageWorkspace)d).ApplySize()));
    public static readonly DependencyProperty BannersProperty = Slot(nameof(Banners), PageSlots.Banners);
    public static readonly DependencyProperty BodyProperty = Slot(nameof(Body), PageSlots.Body);
    public static readonly DependencyProperty FooterProperty = Slot(nameof(Footer), PageSlots.Footer);
    public static readonly DependencyProperty BodyScrollsProperty = DependencyProperty.Register(nameof(BodyScrolls), typeof(bool), typeof(PageWorkspace), new FrameworkPropertyMetadata(true, (d, _) => ((PageWorkspace)d).ApplyMode()));
    private static readonly DependencyPropertyKey InsetPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Inset), typeof(Thickness), typeof(PageWorkspace), new FrameworkPropertyMetadata(default(Thickness)));
    public static readonly DependencyProperty InsetProperty = InsetPropertyKey.DependencyProperty;

    private ScrollViewer? bodyScroll, bandScroll;
    private Grid? bodyGrid;
    private RowDefinition? bodyRow;
    private Border? band, bodyInset;

    private protected PageWorkspace()
    {
        SizeChanged += (_, _) => ApplySize();
        Loaded += (_, _) => ApplySize();
    }

    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    /// <summary>The category tab rail.</summary>
    public object? Tabs { get => GetValue(TabsProperty); set => SetValue(TabsProperty, value); }
    /// <summary>Search, sort, Filters and the filter chips.</summary>
    public object? Toolbar { get => GetValue(ToolbarProperty); set => SetValue(ToolbarProperty, value); }
    /// <summary>Notices at the top of the body.</summary>
    public object? Banners { get => GetValue(BannersProperty); set => SetValue(BannersProperty, value); }
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    /// <summary>Content below the body, for example the history of changes.</summary>
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    /// <summary>True (default): banners, body and footer scroll as one page. False: the body is one list that scrolls by itself and takes the height under the band.</summary>
    public bool BodyScrolls { get => (bool)GetValue(BodyScrollsProperty); set => SetValue(BodyScrollsProperty, value); }
    /// <summary>The widest the body gets, centered; infinity for full width.</summary>
    /// <summary>The page edge for the current width (the token of <see cref="PageInset"/>).</summary>
    public Thickness Inset => (Thickness)GetValue(InsetProperty);

    /// <summary>The fixed band has something in it (a header, a tab rail or a toolbar); a page without them has no band and no line under it.</summary>
    public bool HasBand => (bool)GetValue(HasBandProperty);
    /// <summary>False when the body brings its own page edge, for example a tab whose content is a workspace of its own.</summary>
    public bool PadBody { get => (bool)GetValue(PadBodyProperty); set => SetValue(PadBodyProperty, value); }

    private void UpdateBand() => SetValue(HasBandPropertyKey, Header is not null || Tabs is not null || Toolbar is not null);

    /// <summary>The slots this template owns. A page can fill these and no others.</summary>
    public abstract PageSlots Slots { get; }

    private void CheckSlot(string name, PageSlots slot, object? value)
    {
        if (value is null) return;
        if (!Slots.HasFlag(slot)) throw new InvalidOperationException($"{GetType().Name} has no {name} slot.");
        if (slot == PageSlots.Header && value is not PageHeader) throw new InvalidOperationException($"The Header slot of {GetType().Name} takes a PageHeader.");
        if (slot == PageSlots.Body && value is ScrollViewer) throw new InvalidOperationException($"The body of {GetType().Name} is the one scroll area: do not put a ScrollViewer around it.");
    }

    /// <summary>The scroll viewer of the body; the one scroll area of the page.</summary>
    public ScrollViewer? BodyScroller => bodyScroll;
    /// <summary>The scroll viewer around the fixed band; it scrolls only when the band would take more than <see cref="MaxBandShare"/> of the height.</summary>
    public ScrollViewer? BandScroller => bandScroll;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        bodyScroll = GetTemplateChild("PART_Scroll") as ScrollViewer;
        bandScroll = GetTemplateChild("PART_BandScroll") as ScrollViewer;
        bodyGrid = GetTemplateChild("PART_BodyGrid") as Grid;
        bodyRow = GetTemplateChild("PART_BodyRow") as RowDefinition;
        band = GetTemplateChild("PART_Band") as Border;
        bodyInset = GetTemplateChild("PART_BodyInset") as Border;
        ApplyMode();
        ApplySize();
    }

    private void ApplyMode()
    {
        if (bodyScroll is null) return;
        bodyScroll.VerticalScrollBarVisibility = BodyScrolls ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        ApplySize();
        if (bodyRow is not null) bodyRow.Height = BodyScrolls ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }

    private void ApplySize()
    {
        var width = ActualWidth;
        if (width > 0 && TryFindResource(PageInset.KeyFor(width)) is Thickness edge)
        {
            if (edge != Inset) SetValue(InsetPropertyKey, edge);
            // The band keeps the top and the sides of the page edge, the body the sides and the bottom.
            if (band is not null) band.Padding = new Thickness(edge.Left, edge.Top, edge.Right, this.Space("None").Bottom);
            if (bodyInset is not null) bodyInset.Padding = !PadBody ? default : new Thickness(edge.Left, HasBand ? this.Space("A16").Top : edge.Top, edge.Right, BodyScrolls ? edge.Bottom : this.Space("None").Bottom);
        }
        if (Header is PageHeader header) header.IsShort = ActualHeight > 0 && ActualHeight < ShortHeight;
        if (bandScroll is not null && ActualHeight > 0)
        {
            var chrome = band is null ? 0 : band.Padding.Top + band.Padding.Bottom + band.BorderThickness.Bottom;
            var max = Math.Max(96, Math.Floor(ActualHeight * MaxBandShare - chrome));
            if (Math.Abs(bandScroll.MaxHeight - max) > 0.5) bandScroll.MaxHeight = max;
        }
    }
}

/// <summary>The places of a page. A template owns some of them.</summary>
[Flags]
public enum PageSlots
{
    None = 0,
    Header = 1,
    Tabs = 2,
    Toolbar = 4,
    Banners = 8,
    Body = 16,
    Footer = 32,
}
