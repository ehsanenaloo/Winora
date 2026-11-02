// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Width * Height;
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
    public RectD Deflate(double amount) => new(X + amount, Y + amount, Math.Max(0, Width - 2 * amount), Math.Max(0, Height - 2 * amount));
}

public sealed record TreemapOptions
{
    /// <summary>Folder levels drawn below the root; deeper folders are drawn as one cell.</summary>
    public int MaxDepth { get; init; } = 24;
    /// <summary>Items with less area than this are not drawn one by one; they are merged into one "smaller items" cell of their folder.</summary>
    public double MinCellArea { get; init; } = 12;
    /// <summary>A folder with less area than this is drawn as one cell and not opened.</summary>
    public double MinGroupArea { get; init; } = 150;
    /// <summary>Space kept around the children of a folder so the folder outline stays visible.</summary>
    public double Padding { get; init; } = 1;
    /// <summary>Height of the label strip at the top of a folder cell, when the folder is big enough to hold it. Zero turns the strip off.</summary>
    public double HeaderHeight { get; init; } = 16;
    public double MinHeaderWidth { get; init; } = 56;
    public double MinHeaderHeight { get; init; } = 36;
    /// <summary>Upper bound for the number of cells. The biggest folders are opened first, so the cap removes detail from the smallest areas only.</summary>
    public int MaxCells { get; init; } = 6000;
}

/// <summary>
/// One rectangle of the treemap. A folder that is opened produces a group cell (<see cref="IsLeaf"/> false) and then the cells of its children inside it.
/// A leaf is a file, an aggregate, a folder that is too small or too deep to open (its <see cref="Kind"/> is the type of its largest content), or the
/// "smaller items" cell of a folder (<see cref="IsRest"/>; its <see cref="Node"/> is that folder).
/// </summary>
public sealed record TreemapCell(DiskNode Node, RectD Bounds, int Depth, bool IsLeaf, DiskFileKind Kind = DiskFileKind.Folder, double Header = 0, int RestItems = 0, long RestSize = 0, long RestFiles = 0)
{
    public bool IsRest => RestItems > 0;
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing and van Wijk, 2000), nested: folders contain the cells of their children. Cell area is proportional to size, so with no padding, no header, no
/// minimum area and no cap the leaf cells tile the rectangle exactly. The output lists a folder before its children, so the last cell that contains a point is the deepest one. The result
/// depends only on the tree and the options.
/// </summary>
public static class TreemapLayout
{
    private readonly record struct Item(DiskNode? Node, long Size, int Count, long Files);

    public static IReadOnlyList<TreemapCell> Compute(DiskNode root, RectD bounds, TreemapOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new TreemapOptions();
        var cells = new List<TreemapCell>();
        if (bounds.Width <= 0 || bounds.Height <= 0 || root.Size <= 0) return cells;
        new Builder(options, cells, cancellationToken).Run(root, bounds);
        return cells;
    }

    /// <summary>The deepest cell that contains the point, or null.</summary>
    public static TreemapCell? HitTest(IReadOnlyList<TreemapCell> cells, double x, double y)
    {
        for (var i = cells.Count - 1; i >= 0; i--) if (cells[i].Bounds.Contains(x, y)) return cells[i];
        return null;
    }

    /// <summary>The type of the largest content of a folder, found by following the largest child down. Cheap, and good enough to color a folder that is not opened.</summary>
    public static DiskFileKind DominantKind(DiskNode node)
    {
        var current = node;
        while (current.IsDirectory)
        {
            DiskNode? next = null;
            foreach (var child in current.Children) if (child.Size > 0) { next = child; break; }
            if (next is null) break;
            current = next;
        }
        return current.IsDirectory ? DiskFileKind.Folder : DiskFileKinds.Classify(current);
    }

    private sealed class Builder(TreemapOptions options, List<TreemapCell> cells, CancellationToken token)
    {
        private readonly PriorityQueue<int, (double NegativeArea, int Index)> pending = new();

        public void Run(DiskNode root, RectD bounds)
        {
            Add(root, bounds, 0);
            while (pending.TryDequeue(out var index, out _))
            {
                token.ThrowIfCancellationRequested();
                if (cells.Count >= options.MaxCells) { Collapse(index); continue; }
                Expand(index);
            }
        }

        private void Collapse(int index)
        {
            var cell = cells[index];
            cells[index] = cell with { IsLeaf = true, Header = 0, Kind = DominantKind(cell.Node) };
        }

        private bool CanOpen(DiskNode node, RectD rect, int depth)
        {
            if (!node.IsDirectory || depth >= options.MaxDepth || node.Children.Count == 0 || node.Size <= 0) return false;
            if (depth > 0 && rect.Area < options.MinGroupArea) return false;
            var inner = rect.Deflate(options.Padding);
            return inner.Width >= 2 && inner.Height >= 2;
        }

        private void Add(DiskNode node, RectD rect, int depth)
        {
            if (CanOpen(node, rect, depth))
            {
                cells.Add(new(node, rect, depth, false));
                pending.Enqueue(cells.Count - 1, (-rect.Area, cells.Count - 1));
            }
            else
            {
                cells.Add(new(node, rect, depth, true, node.IsDirectory ? DominantKind(node) : DiskFileKinds.Classify(node)));
            }
        }

        private void Expand(int index)
        {
            var cell = cells[index];
            var node = cell.Node;
            var inner = cell.Bounds.Deflate(options.Padding);
            var header = cell.Depth > 0 && options.HeaderHeight > 0 && cell.Bounds.Width >= options.MinHeaderWidth && cell.Bounds.Height >= options.MinHeaderHeight ? options.HeaderHeight : 0;
            if (header > 0) inner = new RectD(inner.X, inner.Y + header, inner.Width, Math.Max(0, inner.Height - header));
            if (inner.Width < 2 || inner.Height < 2) { Collapse(index); return; }

            double total = 0;
            foreach (var c in node.Children) if (c.Size > 0) total += c.Size;
            if (total <= 0) { Collapse(index); return; }
            var scale = inner.Area / total;

            // Items too small to draw are merged into one cell, so a folder never shows as an empty frame and the folder is always covered. The list is ordered by size, so they are the tail.
            var allowed = Math.Max(16, options.MaxCells - cells.Count);
            var items = new List<Item>();
            long restSize = 0, restFiles = 0; var restCount = 0;
            foreach (var c in node.Children)
            {
                if (c.Size <= 0) continue;
                if (items.Count < allowed - 1 && c.Size * scale >= options.MinCellArea) items.Add(new(c, c.Size, 0, c.FileCount));
                else { restSize += c.Size; restFiles += c.FileCount; restCount++; }
            }
            if (items.Count == 0) { Collapse(index); return; }
            if (restCount > 0)
            {
                var at = items.Count;
                while (at > 0 && items[at - 1].Size < restSize) at--;
                items.Insert(at, new(null, restSize, restCount, restFiles));
            }

            var first = cells.Count;
            cells[index] = cell with { Header = header };
            Squarify(node, items, scale, inner, cell.Depth + 1);
            if (cells.Count == first + 1 && cells[first].IsRest) { cells.RemoveAt(first); Collapse(index); }
        }

        private void Squarify(DiskNode folder, List<Item> items, double scale, RectD rect, int depth)
        {
            var index = 0;
            while (index < items.Count)
            {
                var side = Math.Min(rect.Width, rect.Height);
                if (side <= 0) return;
                var rowStart = index;
                double rowSum = items[index].Size * scale, rowMin = rowSum, rowMax = rowSum;
                var worst = Worst(rowSum, rowMin, rowMax, side);
                index++;
                while (index < items.Count)
                {
                    var area = items[index].Size * scale;
                    var sum = rowSum + area;
                    var min = Math.Min(rowMin, area);
                    var max = Math.Max(rowMax, area);
                    var candidate = Worst(sum, min, max, side);
                    if (candidate > worst) break;
                    rowSum = sum; rowMin = min; rowMax = max; worst = candidate; index++;
                }

                // Lay the row along the shorter side: a column when the rectangle is wide, a strip when it is tall.
                var horizontalStrip = rect.Width < rect.Height;
                var thickness = rowSum / side;
                var offset = 0.0;
                for (var i = rowStart; i < index; i++)
                {
                    var length = items[i].Size * scale / thickness;
                    var bounds = horizontalStrip
                        ? new RectD(rect.X + offset, rect.Y, length, thickness)
                        : new RectD(rect.X, rect.Y + offset, thickness, length);
                    offset += length;
                    if (items[i].Node is { } child) Add(child, bounds, depth);
                    else cells.Add(new(folder, bounds, depth, true, DiskFileKind.Unlisted, 0, items[i].Count, items[i].Size, items[i].Files));
                }
                rect = horizontalStrip
                    ? new RectD(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness))
                    : new RectD(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height);
            }
        }

        private static double Worst(double sum, double min, double max, double side)
        {
            var s2 = sum * sum;
            var w2 = side * side;
            return Math.Max(w2 * max / s2, s2 / (w2 * min));
        }
    }
}
