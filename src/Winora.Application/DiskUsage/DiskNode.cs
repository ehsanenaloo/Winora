// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public enum DiskNodeKind
{
    File,
    Directory,
    /// <summary>The files of one folder that are not listed one by one (the folder keeps only its largest files). Its size and count are exact.</summary>
    Aggregate,
    /// <summary>A folder that could not be opened. Its size is unknown and counts as zero.</summary>
    Unreadable,
}

/// <summary>
/// One file, folder or aggregate in a disk usage tree. A scanner builds the tree with <see cref="Add"/> and then calls <see cref="DiskTree.Finish"/>, which computes the sizes of
/// folders from their children and orders the children by size. After that the tree is read-only by convention.
/// </summary>
public sealed class DiskNode
{
    private readonly List<DiskNode> children = [];

    public DiskNode(string name, DiskNodeKind kind, long size = 0, long fileCount = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name; Kind = kind; Size = size < 0 ? 0 : size;
        FileCount = kind == DiskNodeKind.File ? 1 : fileCount;
    }

    /// <summary>The file or folder name. For the root it is the full path of the scanned folder.</summary>
    public string Name { get; }
    public DiskNodeKind Kind { get; private set; }
    /// <summary>Logical size in bytes, including everything below a folder.</summary>
    public long Size { get; internal set; }
    /// <summary>Number of files in the subtree (an aggregate carries the number of files it stands for).</summary>
    public long FileCount { get; internal set; }
    public DiskNode? Parent { get; private set; }
    public IReadOnlyList<DiskNode> Children => children;
    public bool IsDirectory => Kind == DiskNodeKind.Directory;

    /// <summary>The extension with its dot in lower case, or an empty string for folders, aggregates and files without one.</summary>
    public string Extension
    {
        get
        {
            if (Kind != DiskNodeKind.File) return "";
            var dot = Name.LastIndexOf('.');
            return dot <= 0 || dot == Name.Length - 1 ? "" : Name[dot..].ToLowerInvariant();
        }
    }

    public void Add(DiskNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (Kind != DiskNodeKind.Directory) throw new InvalidOperationException("Only a folder can have children.");
        if (child.Parent is not null) throw new InvalidOperationException("The node already has a parent.");
        child.Parent = this;
        children.Add(child);
    }

    /// <summary>Marks a folder that could not be listed and has nothing below it. The folder keeps its place in the tree with size zero.</summary>
    public void MarkUnreadable()
    {
        if (Kind != DiskNodeKind.Directory || children.Count > 0) throw new InvalidOperationException("Only an empty folder can be marked unreadable.");
        Kind = DiskNodeKind.Unreadable;
    }

    /// <summary>The full path of the node. An aggregate has the path of its folder.</summary>
    public string FullPath
    {
        get
        {
            var parts = new List<string>();
            for (var n = this; n is not null; n = n.Parent)
            {
                if (n.Kind == DiskNodeKind.Aggregate) continue;
                parts.Add(n.Name);
            }
            parts.Reverse();
            var path = parts[0];
            for (var i = 1; i < parts.Count; i++) path = path.EndsWith('\\') || path.EndsWith('/') ? path + parts[i] : path + "\\" + parts[i];
            return path;
        }
    }

    internal void SortChildren() => children.Sort(static (a, b) =>
    {
        var bySize = b.Size.CompareTo(a.Size);
        return bySize != 0 ? bySize : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    });
}

public static class DiskTree
{
    /// <summary>
    /// Computes the size and file count of every folder from its children, then orders the children of every folder by size (largest first, ties by name). Iterative, so a very deep
    /// tree cannot overflow the stack.
    /// </summary>
    public static void Finish(DiskNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var order = new List<DiskNode>();
        var stack = new Stack<DiskNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            foreach (var child in node.Children) if (child.Kind == DiskNodeKind.Directory) stack.Push(child);
        }
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var folder = order[i];
            long size = 0, files = 0;
            foreach (var child in folder.Children) { size += child.Size; files += child.FileCount; }
            folder.Size = size; folder.FileCount = files;
            folder.SortChildren();
        }
    }
}
