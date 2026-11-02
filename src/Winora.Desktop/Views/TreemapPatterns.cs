// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Media;
using Winora.Application;

namespace Winora.Desktop;

/// <summary>
/// One pattern per file type, drawn as a small repeating tile in a contrasting ink on top of the type's colour. The map cells and the legend swatches use the same brush. In a Windows high contrast
/// theme the fill is the system control colour and the ink is the system text colour, so the patterns carry the whole distinction.
/// </summary>
public static class TreemapPatterns
{
    private const double Tile = 8;
    private static readonly Dictionary<(DiskFileKind, bool), Brush> Cache = [];

    public static LegendPattern For(DiskFileKind kind) => LegendPatterns.For(kind);

    /// <summary>Ink that reads on the colour: black on light colours, white on dark ones.</summary>
    public static Color InkFor(Color fill) => (0.2126 * fill.R + 0.7152 * fill.G + 0.0722 * fill.B) / 255 > 0.55 ? Colors.Black : Colors.White;

    public static Brush BrushFor(DiskFileKind kind, Color fill, bool highContrast)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((kind, highContrast), out var cached)) return cached;
            var pattern = For(kind);
            var baseColor = highContrast ? SystemColors.ControlColor : fill;
            var ink = highContrast ? SystemColors.WindowTextColor : InkFor(fill);
            Brush brush;
            if (pattern == LegendPattern.Solid && !highContrast)
            {
                brush = new SolidColorBrush(baseColor);
            }
            else
            {
                var inkBrush = new SolidColorBrush(Color.FromArgb(highContrast ? (byte)255 : (byte)150, ink.R, ink.G, ink.B));
                inkBrush.Freeze();
                var pen = new Pen(inkBrush, 1);
                pen.Freeze();
                var group = new DrawingGroup();
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, Tile, Tile))));
                var shapes = Shapes(pattern, pen, inkBrush);
                foreach (var shape in shapes) group.Children.Add(shape);
                group.Freeze();
                brush = new DrawingBrush(group)
                {
                    TileMode = TileMode.Tile, Viewport = new Rect(0, 0, Tile, Tile), ViewportUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, Tile, Tile), ViewboxUnits = BrushMappingMode.Absolute,
                };
            }
            brush.Freeze();
            Cache[(kind, highContrast)] = brush;
            return brush;
        }
    }

    private static IEnumerable<Drawing> Shapes(LegendPattern pattern, Pen pen, Brush ink)
    {
        static Drawing Line(Pen pen, double x1, double y1, double x2, double y2) => new GeometryDrawing(null, pen, new LineGeometry(new Point(x1, y1), new Point(x2, y2)));
        static Drawing Dot(Brush ink, double x, double y, double r) => new GeometryDrawing(ink, null, new EllipseGeometry(new Point(x, y), r, r));
        switch (pattern)
        {
            case LegendPattern.Horizontal: yield return Line(pen, 0, 4, Tile, 4); break;
            case LegendPattern.Vertical: yield return Line(pen, 4, 0, 4, Tile); break;
            case LegendPattern.DiagonalUp: yield return Line(pen, 0, Tile, Tile, 0); yield return Line(pen, -4, 4, 4, -4); yield return Line(pen, 4, 12, 12, 4); break;
            case LegendPattern.DiagonalDown: yield return Line(pen, 0, 0, Tile, Tile); yield return Line(pen, -4, 4, 4, 12); yield return Line(pen, 4, -4, 12, 4); break;
            case LegendPattern.Cross: yield return Line(pen, 0, 4, Tile, 4); yield return Line(pen, 4, 0, 4, Tile); break;
            case LegendPattern.DiagonalCross: yield return Line(pen, 0, 0, Tile, Tile); yield return Line(pen, 0, Tile, Tile, 0); break;
            case LegendPattern.Dots: yield return Dot(ink, 2, 2, 1.1); yield return Dot(ink, 6, 6, 1.1); break;
            case LegendPattern.SparseDots: yield return Dot(ink, 4, 4, 1); break;
            case LegendPattern.Checker: yield return new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(0, 0, 4, 4))); yield return new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(4, 4, 4, 4))); break;
            case LegendPattern.DenseCross: yield return Line(pen, 0, 2, Tile, 2); yield return Line(pen, 0, 6, Tile, 6); yield return Line(pen, 2, 0, 2, Tile); yield return Line(pen, 6, 0, 6, Tile); break;
        }
    }
}
