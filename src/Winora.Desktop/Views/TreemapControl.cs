// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Winora.Application;

namespace Winora.Desktop;

/// <summary>
/// Draws a nested treemap of one folder: folders are frames that hold the cells of their content, files are rectangles whose area follows their size and whose color follows their type,
/// with a light cushion shading on each. The map is a visual summary; every item it shows is also in the list beside it, which is the keyboard and screen reader path. The control always lays out
/// left to right. The layout is computed off the UI thread for big folders and the whole picture is one retained drawing, not one element per cell.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(nameof(Node), typeof(DiskNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreemapControl)d).Relayout()));
    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(nameof(SelectedNode), typeof(DiskNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty CellDescriberProperty = DependencyProperty.Register(nameof(CellDescriber), typeof(Func<TreemapCell, string>), typeof(TreemapControl), new PropertyMetadata(null));
    public static readonly DependencyProperty LabelerProperty = DependencyProperty.Register(nameof(Labeler), typeof(Func<DiskNode, string>), typeof(TreemapControl),
        new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Redraw()));
    public static readonly DependencyProperty SizeLabelerProperty = DependencyProperty.Register(nameof(SizeLabeler), typeof(Func<long, string>), typeof(TreemapControl),
        new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Redraw()));
    public static readonly DependencyProperty RestLabelerProperty = DependencyProperty.Register(nameof(RestLabeler), typeof(Func<int, string>), typeof(TreemapControl),
        new PropertyMetadata(null, (d, _) => ((TreemapControl)d).Redraw()));
    public static readonly DependencyProperty ShowPatternsProperty = DependencyProperty.Register(nameof(ShowPatterns), typeof(bool), typeof(TreemapControl),
        new PropertyMetadata(false, (d, _) => ((TreemapControl)d).Redraw()));

    private static Dictionary<DiskFileKind, Color>? palette;
    private static Dictionary<DiskFileKind, Color> Palette => palette ??= Enum.GetValues<DiskFileKind>().ToDictionary(k => k, KindColor);

    // The kind colors are tokens (Styles/Tokens.xaml). Code that runs before the application resources exist reads the token file itself.
    private static Color KindColor(DiskFileKind kind)
    {
        var key = "WinoraDiskKind" + kind;
        if (System.Windows.Application.Current?.TryFindResource(key) is Color color) return color;
        return (Color)new ResourceDictionary { Source = new Uri("/Winora.Desktop;component/Styles/Tokens.xaml", UriKind.Relative) }[key];
    }

    private const int MaxLabels = 400;
    /// <summary>Folders with at most this many files are laid out on the UI thread (it takes a few milliseconds); bigger ones in the background.</summary>
    private const long SynchronousFileLimit = 4000;
    private const double LabelSize = 11;

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface HeaderFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush Cushion = CreateCushion();
    private static readonly Dictionary<Color, SolidColorBrush> Brushes_ = [];

    private readonly record struct Theme(Color Surface, Color Ink, Color Border, bool HighContrast);
    /// <summary>Everything the painter needs, read on the UI thread; the painter touches nothing of the control.</summary>
    private sealed record Snapshot(IReadOnlyList<TreemapCell> Cells, Theme Theme, bool Patterns, DpiScale Dpi, double Width, double Height,
        Func<DiskNode, string>? Labeler, Func<long, string>? SizeLabeler, Func<int, string>? RestLabeler);

    /// <summary>Maps with at most this many cells are drawn on the UI thread (a few milliseconds); bigger ones on the painter thread.</summary>
    private const int SynchronousCellLimit = 1500;

    // One background thread with its own dispatcher: drawing visuals and text need a single-threaded apartment.
    private static readonly Lazy<Dispatcher> Painter = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => { dispatcher = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true, Name = "Treemap painter" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    private IReadOnlyList<TreemapCell> cells = [];
    private Dictionary<DiskNode, TreemapCell> byNode = [];
    // The cells drawn once into a bitmap. Thousands of shaded rectangles cost about 200 ms per frame with software rendering (a VM without a GPU, remote desktop),
    // so a hover change only paints the outline over this bitmap instead of drawing every cell again. A big map is drawn on the painter thread; the last
    // bitmap stays on screen until the new one is ready, so the page never waits for it.
    private ImageSource? shown;
    private bool fresh, painting;
    private int paintVersion;
    private TaskCompletionSource paintDone = Done();
    private TreemapCell? hover;
    private int layoutVersion;
    private CancellationTokenSource? layoutCts;
    private TaskCompletionSource layoutDone = Done();

    public TreemapControl()
    {
        Focusable = true;
        FlowDirection = FlowDirection.LeftToRight;
        SnapsToDevicePixels = true;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 250);
        FocusVisualStyle = null;
        MinHeight = 180;
        SizeChanged += (_, _) => Relayout();
        Loaded += (_, _) => { Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnThemeChanged; SystemParameters.StaticPropertyChanged += OnSystemChanged; Redraw(); };
        Unloaded += (_, _) => { Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnThemeChanged; SystemParameters.StaticPropertyChanged -= OnSystemChanged; layoutCts?.Cancel(); };
    }

    public DiskNode? Node { get => (DiskNode?)GetValue(NodeProperty); set => SetValue(NodeProperty, value); }
    public DiskNode? SelectedNode { get => (DiskNode?)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    /// <summary>Text for the tooltip of a cell.</summary>
    public Func<TreemapCell, string>? CellDescriber { get => (Func<TreemapCell, string>?)GetValue(CellDescriberProperty); set => SetValue(CellDescriberProperty, value); }
    /// <summary>Name drawn inside large cells.</summary>
    public Func<DiskNode, string>? Labeler { get => (Func<DiskNode, string>?)GetValue(LabelerProperty); set => SetValue(LabelerProperty, value); }
    /// <summary>Size text drawn under the name of large cells and after the name of folder frames.</summary>
    public Func<long, string>? SizeLabeler { get => (Func<long, string>?)GetValue(SizeLabelerProperty); set => SetValue(SizeLabelerProperty, value); }
    /// <summary>Name of the "smaller items" cell of a folder; the argument is the number of items it stands for.</summary>
    public Func<int, string>? RestLabeler { get => (Func<int, string>?)GetValue(RestLabelerProperty); set => SetValue(RestLabelerProperty, value); }
    /// <summary>Draws a line pattern per file type on top of the color, for people who cannot rely on color. Always on in a Windows high contrast theme.</summary>
    public bool ShowPatterns { get => (bool)GetValue(ShowPatternsProperty); set => SetValue(ShowPatternsProperty, value); }
    /// <summary>The localized control type a screen reader says, such as "disk usage map".</summary>
    public static readonly DependencyProperty TypeNameProperty = DependencyProperty.Register(nameof(TypeName), typeof(string), typeof(TreemapControl), new PropertyMetadata(""));
    public string TypeName { get => (string)GetValue(TypeNameProperty); set => SetValue(TypeNameProperty, value); }

    /// <summary>The cells of the current layout, for tests.</summary>
    public IReadOnlyList<TreemapCell> Cells => cells;
    /// <summary>The cell the pointer is over, for tests.</summary>
    public TreemapCell? HoverCell => hover;

    /// <summary>Raised on double click or Enter. The argument is the node under the pointer or the selected node.</summary>
    public event Action<DiskNode>? NodeInvoked;
    /// <summary>Raised on Backspace: the user wants the parent folder.</summary>
    public event Action? UpRequested;

    public static Color ColorOf(DiskFileKind kind) => Palette[kind];

    /// <summary>Completes when the layout for the current node and size is in place. Immediate for small folders.</summary>
    public async Task WhenLayoutAsync()
    {
        await layoutDone.Task;
        await paintDone.Task;
    }

    private static TaskCompletionSource Done() { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }

    private static Brush CreateCushion()
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.3, 0.25), Center = new Point(0.5, 0.5), RadiusX = 1.0, RadiusY = 1.0,
            GradientStops = [new(Color.FromArgb(0x4A, 255, 255, 255), 0), new(Color.FromArgb(0x00, 255, 255, 255), 0.55), new(Color.FromArgb(0x4A, 0, 0, 0), 1)],
        };
        brush.Freeze();
        return brush;
    }

    private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, Color accent) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Redraw));
    private void OnSystemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName is nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Redraw)); }

    private void Redraw() { fresh = false; Paint(); InvalidateVisual(); }

    private void Relayout()
    {
        hover = null; ToolTip = null;
        fresh = false;
        layoutCts?.Cancel();
        layoutCts = null;
        var version = ++layoutVersion;
        var node = Node;
        if (node is null || ActualWidth < 4 || ActualHeight < 4) { Apply([]); return; }
        var bounds = new RectD(0, 0, ActualWidth, ActualHeight);
        if (node.FileCount <= SynchronousFileLimit) { Apply(TreemapLayout.Compute(node, bounds)); return; }

        var cts = layoutCts = new CancellationTokenSource();
        var done = layoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cells = []; byNode = []; InvalidateVisual();
        Task.Run(() => TreemapLayout.Compute(node, bounds, null, cts.Token), cts.Token).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (version == layoutVersion && t.IsCompletedSuccessfully) Apply(t.Result);
                done.TrySetResult();
            });
        }, TaskScheduler.Default);
    }

    private void Apply(IReadOnlyList<TreemapCell> computed)
    {
        cells = computed;
        fresh = false; // a drawing made while the layout was still being computed is empty
        byNode = new Dictionary<DiskNode, TreemapCell>(computed.Count);
        foreach (var c in computed) if (!c.IsRest) byNode[c.Node] = c;
        layoutDone = Done();
        Paint();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    private Theme ReadTheme()
    {
        Color Get(string key, Color fallback) => TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;
        var highContrast = SystemParameters.HighContrast;
        if (highContrast) return new(SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.WindowTextColor, true);
        var ink = Get("TextFillColorPrimaryBrush", Colors.Black);
        var dark = (0.2126 * ink.R + 0.7152 * ink.G + 0.0722 * ink.B) / 255 > 0.5;
        // The card behind the map is translucent in the dark theme, so the map blends it over the page background to get an opaque base.
        var page = Get("ApplicationBackgroundBrush", dark ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White);
        var card = Get("CardBackgroundFillColorDefaultBrush", Color.FromArgb(0, 0, 0, 0));
        var alpha = card.A / 255.0;
        var surface = Color.FromRgb(
            (byte)Math.Round(page.R + (card.R - page.R) * alpha), (byte)Math.Round(page.G + (card.G - page.G) * alpha), (byte)Math.Round(page.B + (card.B - page.B) * alpha));
        ink = Color.FromRgb(ink.R, ink.G, ink.B);
        return new(surface, ink, Blend(surface, ink, 0.4), false);
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount), (byte)Math.Round(from.G + (to.G - from.G) * amount), (byte)Math.Round(from.B + (to.B - from.B) * amount));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (!fresh) Paint();
        if (shown is not null) dc.DrawImage(shown, new Rect(0, 0, ActualWidth, ActualHeight));

        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? SystemColors.HighlightBrush;
        var selected = SelectedNode;
        if (selected is not null && byNode.TryGetValue(selected, out var selectedCell))
            Outline(dc, selectedCell.Bounds, accent, 3);
        else if (selected is not null)
        {
            // The selection is inside a cell that is not drawn one by one: outline the nearest visible ancestor.
            for (var n = selected.Parent; n is not null; n = n.Parent)
                if (byNode.TryGetValue(n, out var ancestorCell)) { Outline(dc, ancestorCell.Bounds, accent, 3); break; }
        }
        if (hover is { } h)
        {
            var rect = new Rect(h.Bounds.X, h.Bounds.Y, h.Bounds.Width, h.Bounds.Height);
            if (!h.IsLeaf) rect.Height = Math.Min(rect.Height, Math.Max(h.Header, 4));
            if (h.IsLeaf && !SystemParameters.HighContrast) dc.DrawRectangle(Frozen(Color.FromArgb(0x38, 255, 255, 255)), null, rect);
            Outline(dc, h.Bounds, SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White, 2);
        }
        if (IsKeyboardFocused)
            dc.DrawRectangle(null, new Pen(SystemColors.WindowTextBrush, 2) { DashStyle = DashStyles.Dash }, new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
    }

    /// <summary>Brings the bitmap up to date: at once for a small map, on the painter thread for a big one. Only the newest request is shown.</summary>
    private void Paint()
    {
        // While a big layout is computed the old bitmap stays; there is nothing new to draw yet.
        if (fresh || painting || !layoutDone.Task.IsCompleted) return;
        if (ActualWidth < 1 || ActualHeight < 1) { shown = null; fresh = true; paintDone.TrySetResult(); return; }
        var snapshot = new Snapshot(cells, ReadTheme(), ShowPatterns || SystemParameters.HighContrast, VisualTreeHelper.GetDpi(this), ActualWidth, ActualHeight, Labeler, SizeLabeler, RestLabeler);
        var version = ++paintVersion;
        if (cells.Count <= SynchronousCellLimit) { shown = Render(snapshot); fresh = true; paintDone.TrySetResult(); return; }
        painting = true;
        if (paintDone.Task.IsCompleted) paintDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = paintDone;
        Painter.Value.InvokeAsync(() => Render(snapshot)).Task.ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            painting = false;
            if (t.IsCompletedSuccessfully && version == paintVersion && !fresh) { shown = t.Result; fresh = true; done.TrySetResult(); }
            else if (!fresh) Paint(); // the map changed while it was drawn: draw it again
            else done.TrySetResult();
            InvalidateVisual();
        }), TaskScheduler.Default);
    }

    /// <summary>Draws the cells into a bitmap at the screen's pixel size, so the map stays sharp at any display scale. Runs on either thread.</summary>
    private static ImageSource Render(Snapshot s)
    {
        var drawing = BuildDrawing(s);
        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        using (var c = visual.RenderOpen()) c.DrawDrawing(drawing);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(s.Width * s.Dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(s.Height * s.Dpi.DpiScaleY)), 96 * s.Dpi.DpiScaleX, 96 * s.Dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); Redraw(); }

    private static void Outline(DrawingContext dc, RectD bounds, Brush brush, double thickness)
    {
        var rect = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        rect.Inflate(-thickness / 2, -thickness / 2);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        dc.DrawRectangle(null, new Pen(Brushes.Black, thickness + 2), rect);
        dc.DrawRectangle(null, new Pen(brush, thickness), rect);
    }

    private static DrawingGroup BuildDrawing(Snapshot s)
    {
        var theme = s.Theme;
        var patterns = s.Patterns;
        var cells = s.Cells;
        var group = new DrawingGroup();
        RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
        var edge = new Pen(theme.HighContrast ? Frozen(theme.Ink) : Frozen(Color.FromArgb(110, 0, 0, 0)), 0.5);
        var frame = new Pen(Frozen(theme.Border), 1);
        edge.Freeze(); frame.Freeze();
        var dpi = s.Dpi.PixelsPerDip;
        var labeler = s.Labeler;
        var sizeLabeler = s.SizeLabeler;
        var restLabeler = s.RestLabeler;

        // Labels go to the biggest cells first, so the cap never takes a label from a large cell.
        var labelled = new HashSet<int>();
        if (labeler is not null)
        {
            var candidates = new List<(int Index, double Area)>();
            for (var i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                var fits = c.IsLeaf ? c.Bounds.Width >= 44 && c.Bounds.Height >= 15 : c.Header > 0;
                if (fits) candidates.Add((i, c.Bounds.Area));
            }
            foreach (var (index, _) in candidates.OrderByDescending(x => x.Area).ThenBy(x => x.Index).Take(MaxLabels)) labelled.Add(index);
        }

        using (var d = group.Open())
        {
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var rect = new Rect(cell.Bounds.X, cell.Bounds.Y, cell.Bounds.Width, cell.Bounds.Height);
                if (!cell.IsLeaf)
                {
                    var tint = theme.HighContrast ? theme.Surface : Blend(theme.Surface, theme.Ink, 0.05 + 0.035 * (cell.Depth % 3));
                    d.DrawRectangle(Frozen(tint), frame, rect);
                    if (labelled.Contains(i) && labeler is not null)
                    {
                        var name = labeler(cell.Node);
                        if (sizeLabeler is not null) name += "  " + sizeLabeler(cell.Node.Size);
                        DrawText(d, name, HeaderFace, theme.Ink, rect.X + 4, rect.Y + 1, rect.Width - 8, cell.Header, dpi);
                    }
                    continue;
                }

                var kind = cell.Kind;
                var fill = Palette[kind];
                d.DrawRectangle(patterns ? TreemapPatterns.BrushFor(kind, fill, theme.HighContrast) : Frozen(fill), edge, rect);
                if (!theme.HighContrast && rect.Width >= 3 && rect.Height >= 3) d.DrawRectangle(Cushion, null, rect);
                if (!labelled.Contains(i) || labeler is null) continue;

                var ink = theme.HighContrast ? SystemColors.ControlTextColor : TreemapPatterns.InkFor(fill);
                var text = cell.IsRest ? restLabeler?.Invoke(cell.RestItems) ?? "" : labeler(cell.Node);
                if (text.Length == 0) continue;
                var detail = cell.Bounds.Height >= 32 && cell.Bounds.Width >= 60 && sizeLabeler is not null ? "\n" + sizeLabeler(cell.IsRest ? cell.RestSize : cell.Node.Size) : "";
                if (patterns)
                {
                    // A small plate in the cell color keeps the label readable over the pattern.
                    var plate = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, LabelSize, Brushes.Black, dpi) { MaxTextWidth = Math.Max(1, rect.Width - 6), MaxTextHeight = 14, Trimming = TextTrimming.CharacterEllipsis };
                    d.DrawRectangle(Frozen(Color.FromArgb(235, fill.R, fill.G, fill.B)), null, new Rect(rect.X + 2, rect.Y + 1, Math.Min(plate.WidthIncludingTrailingWhitespace + 2, Math.Max(1, rect.Width - 4)), Math.Min(plate.Height, Math.Max(1, rect.Height - 2))));
                }
                DrawText(d, text + detail, LabelFace, ink, rect.X + 3, rect.Y + 1, rect.Width - 6, Math.Min(rect.Height - 2, detail.Length > 0 ? 30 : 14), dpi);
            }
        }
        group.Freeze();
        return group;
    }

    /// <summary>Draws text that never leaves the given box: it is trimmed at the right edge and cut at the bottom edge.</summary>
    private static void DrawText(DrawingContext d, string text, Typeface face, Color ink, double x, double y, double width, double height, double dpi)
    {
        if (width < 8 || height < 8) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, LabelSize, Frozen(ink), dpi)
        {
            MaxTextWidth = width, MaxTextHeight = height, Trimming = TextTrimming.CharacterEllipsis,
        };
        d.DrawText(formatted, new Point(x, y));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        lock (Brushes_)
        {
            if (!Brushes_.TryGetValue(color, out var brush)) { brush = new SolidColorBrush(color); brush.Freeze(); Brushes_[color] = brush; }
            return brush;
        }
    }

    // ---- pointer ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        SetHover(TreemapLayout.HitTest(cells, p.X, p.Y));
    }

    /// <summary>Moves the highlight to a cell (null clears it) and prepares its tooltip. Public for tests.</summary>
    public void SetHover(TreemapCell? cell)
    {
        if (cell == hover) return;
        hover = cell;
        ToolTip = cell is null || CellDescriber is not { } describe ? null : describe(cell);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        if (SelectAt(p.X, p.Y, e.ClickCount)) e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        SelectAt(p.X, p.Y, 1);
    }

    /// <summary>Selects the node at the point; a second click (click count 2) also raises <see cref="NodeInvoked"/>. False when nothing is there.</summary>
    public bool SelectAt(double x, double y, int clickCount)
    {
        if (TreemapLayout.HitTest(cells, x, y) is not { } cell) return false;
        SelectedNode = cell.Node;
        if (clickCount == 2) NodeInvoked?.Invoke(cell.Node);
        return true;
    }

    // ---- keyboard ----

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (HandleKey(e.Key)) e.Handled = true;
    }

    /// <summary>Arrow keys move the selection, Enter opens it, Backspace asks for the parent folder. True when the key was used.</summary>
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Left or Key.Right or Key.Up or Key.Down:
                return Move(key);
            case Key.Enter when SelectedNode is { } node:
                NodeInvoked?.Invoke(node);
                return true;
            case Key.Back:
                UpRequested?.Invoke();
                return true;
            default:
                return false;
        }
    }

    private TreemapCell? CellOfSelection()
    {
        for (var n = SelectedNode; n is not null; n = n.Parent) if (byNode.TryGetValue(n, out var c)) return c;
        return null;
    }

    private bool Move(Key key)
    {
        var leaves = cells.Where(c => c.IsLeaf).ToList();
        if (leaves.Count == 0) return false;
        var from = CellOfSelection();
        if (from is null) { SelectedNode = leaves.OrderByDescending(c => c.Bounds.Area).First().Node; return true; }

        double Cx(TreemapCell c) => c.Bounds.X + c.Bounds.Width / 2;
        double Cy(TreemapCell c) => c.Bounds.Y + c.Bounds.Height / 2;
        var best = leaves.Where(c => c.Node != from.Node && key switch
            {
                Key.Left => Cx(c) < Cx(from) - 0.5,
                Key.Right => Cx(c) > Cx(from) + 0.5,
                Key.Up => Cy(c) < Cy(from) - 0.5,
                _ => Cy(c) > Cy(from) + 0.5,
            })
            .OrderBy(c =>
            {
                var dx = Math.Abs(Cx(c) - Cx(from)); var dy = Math.Abs(Cy(c) - Cy(from));
                return key is Key.Left or Key.Right ? dx + 2 * dy : dy + 2 * dx;
            })
            .FirstOrDefault();
        if (best is null) return false;
        SelectedNode = best.Node;
        return true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreemapPeer(this);

    private sealed class TreemapPeer(TreemapControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TreemapControl);
        // A drawn map is neither an image nor a list: it is a custom control that holds folders, each of which can be selected and opened. The localized type says "disk usage map".
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetLocalizedControlTypeCore() => owner.TypeName is { Length: > 0 } name ? name : base.GetLocalizedControlTypeCore();
        // The name says which folder the map shows and its size (bound by the page); the selected folder is the item status.
        protected override string GetItemStatusCore() =>
            owner.SelectedNode is { } node && owner.Labeler is { } label ? owner.SizeLabeler is { } size ? $"{label(node)}, {size(node.Size)}" : label(node) : "";
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
