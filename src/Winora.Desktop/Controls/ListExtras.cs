// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>
/// Content that belongs before or after the rows of a page's long list: the notices and states above the rows, the history of changes below them. A virtualized list is the
/// one scroll area of its page, and WPF cannot put anything around the items of a virtualizing list inside that scroll area, so these are items of the list itself. The content is
/// shown with <see cref="Context"/> as its data context (the page), so its bindings work as they would on the page.
/// </summary>
/// <remarks>
/// It implements <see cref="System.ComponentModel.INotifyPropertyChanged"/> only so WPF bindings listen through a weak event. On a plain object WPF uses a value-changed
/// handler that holds the object, and with it the page and its window, for the life of the process.
/// </remarks>
public sealed class ListExtra(object? content, object? context) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public object? Content { get; } = content;
    public object? Context { get; } = context;
    /// <summary>The extra below the rows (the history); the other one is above them.</summary>
    public bool IsFooter { get; init; }
    /// <summary>Row containers name themselves from their item. Without a name a screen reader would read the type name of the item.</summary>
    public string AutomationName => A11y.Names?[IsFooter ? "ListHistoryName" : "ListNoticesName"] ?? "";

    public override string ToString() => AutomationName;
    /// <summary>An extra never continues a card.</summary>
    public bool JoinsNext => false;
}

/// <summary>A read-only view of a source list with an extra item before it and one after it. The source's change notifications pass through with their indexes moved by the extra in front.</summary>
public sealed class ExtendedItems : IList, IReadOnlyList<object>, INotifyCollectionChanged, IDisposable
{
    private readonly IList source;
    private readonly ListExtra? head, tail;
    private readonly int offset;

    public ExtendedItems(IEnumerable? source, ListExtra? head, ListExtra? tail)
    {
        this.source = source as IList ?? source?.Cast<object>().ToList() ?? (IList)Array.Empty<object>();
        this.head = head; this.tail = tail; offset = head is null ? 0 : 1;
        if (this.source is INotifyCollectionChanged notify) notify.CollectionChanged += OnSourceChanged;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Stops following the source (the view that showed the list is gone).</summary>
    public void Dispose()
    {
        if (source is INotifyCollectionChanged notify) notify.CollectionChanged -= OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (offset == 0 || e.Action == NotifyCollectionChangedAction.Reset) { CollectionChanged?.Invoke(this, e); return; }
        CollectionChanged?.Invoke(this, e.Action switch
        {
            NotifyCollectionChangedAction.Add => new NotifyCollectionChangedEventArgs(e.Action, e.NewItems, e.NewStartingIndex + offset),
            NotifyCollectionChangedAction.Remove => new NotifyCollectionChangedEventArgs(e.Action, e.OldItems, e.OldStartingIndex + offset),
            NotifyCollectionChangedAction.Replace => new NotifyCollectionChangedEventArgs(e.Action, e.NewItems!, e.OldItems!, e.NewStartingIndex + offset),
            NotifyCollectionChangedAction.Move => new NotifyCollectionChangedEventArgs(e.Action, e.NewItems, e.NewStartingIndex + offset, e.OldStartingIndex + offset),
            _ => new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset),
        });
    }

    public int Count => offset + source.Count + (tail is null ? 0 : 1);
    object IReadOnlyList<object>.this[int index] => this[index]!;
    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (index < offset) return head;
            var inner = index - offset;
            return inner < source.Count ? source[inner] : tail;
        }
        set => throw new NotSupportedException();
    }

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int IndexOf(object? value)
    {
        if (head is not null && ReferenceEquals(value, head)) return 0;
        if (tail is not null && ReferenceEquals(value, tail)) return offset + source.Count;
        var at = source.IndexOf(value);
        return at < 0 ? -1 : at + offset;
    }
    public void CopyTo(Array array, int index) { foreach (var item in this) array.SetValue(item, index++); }
    public IEnumerator GetEnumerator()
    {
        if (head is not null) yield return head;
        foreach (var item in source) yield return item;
        if (tail is not null) yield return tail;
    }
    IEnumerator<object> IEnumerable<object>.GetEnumerator() { foreach (var item in this) yield return item!; }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>
/// Declares the extra content of a list in XAML (<c>ListExtras.Head</c>, shown before the rows), keeps the extra items from being selected by a click on their empty parts
/// (a click on a button or a toggle inside them works as usual) and gives the Tab key its order through them. The list is one tab stop (its rows are walked with the arrow keys),
/// which would hide the buttons of the extras, so Tab and Shift+Tab go from the controls above the rows to the rows, from the rows to the controls below them and back.
/// </summary>
public static class ListExtras
{
    public static readonly DependencyProperty HeadProperty = DependencyProperty.RegisterAttached("Head", typeof(object), typeof(ListExtras), new PropertyMetadata(null));
    public static object? GetHead(DependencyObject d) => d.GetValue(HeadProperty);
    public static void SetHead(DependencyObject d, object? value) => d.SetValue(HeadProperty, value);

    public static readonly DependencyProperty GuardedProperty = DependencyProperty.RegisterAttached("Guarded", typeof(bool), typeof(ListExtras), new PropertyMetadata(false, OnGuarded));
    public static bool GetGuarded(DependencyObject d) => (bool)d.GetValue(GuardedProperty);
    public static void SetGuarded(DependencyObject d, bool value) => d.SetValue(GuardedProperty, value);

    private static void OnGuarded(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;
        list.PreviewMouseLeftButtonDown -= OnMouseDown;
        list.PreviewKeyDown -= OnTab;
        if (e.NewValue is true) { list.PreviewMouseLeftButtonDown += OnMouseDown; list.PreviewKeyDown += OnTab; }
    }

    private static ListBoxItem? ContainerOf(ListBox list, DependencyObject? source)
    {
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) == list) return item;
        return null;
    }

    // The controls inside a container that Tab stops at, in reading order.
    private static List<UIElement> Stops(DependencyObject container)
    {
        var stops = new List<UIElement>();
        void Walk(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element && KeyboardNavigation.GetIsTabStop(element)) stops.Add(element);
                Walk(child);
            }
        }
        Walk(container);
        return stops;
    }

    private static ListBoxItem? Realize(ListBox list, object? item)
    {
        if (item is null) return null;
        list.ScrollIntoView(item);
        list.UpdateLayout();
        return list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
    }

    private static bool FocusStop(ListBoxItem? container, bool last)
    {
        if (container is null) return false;
        var stops = Stops(container);
        if (stops.Count == 0) return false;
        return (last ? stops[^1] : stops[0]).Focus();
    }

    /// <summary>The modifier keys held down; a test replaces it because a test cannot press Shift.</summary>
    public static Func<ModifierKeys> Modifiers { get; set; } = static () => Keyboard.Modifiers;

    private static void OnTab(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || (Modifiers() & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        var list = (ListBox)sender;
        var head = list.Items.OfType<ListExtra>().FirstOrDefault(x => !x.IsFooter);
        var footer = list.Items.OfType<ListExtra>().FirstOrDefault(x => x.IsFooter);
        if ((head is null && footer is null) || ContainerOf(list, Keyboard.FocusedElement as DependencyObject) is not { } container) return;
        var forward = (Modifiers() & ModifierKeys.Shift) == 0;
        var zone = container.Content is ListExtra x ? (x.IsFooter ? 2 : 0) : 1;

        bool Within()
        {
            var stops = Stops(container);
            var at = stops.IndexOf((UIElement)Keyboard.FocusedElement);
            var next = at + (forward ? 1 : -1);
            return next >= 0 && next < stops.Count && stops[next].Focus();
        }
        bool Rows()
        {
            var entry = list.SelectedItem is { } selected && list.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem chosen ? chosen
                : FirstRow(list);
            return entry?.Focus() == true;
        }
        bool Head() => head is not null && FocusStop(Realize(list, head), last: true);
        bool Footer() => footer is not null && FocusStop(Realize(list, footer), last: false);

        var moved = zone switch
        {
            0 => forward ? Within() || Rows() || Footer() : Within(),
            1 => forward ? Footer() : Head(),
            _ => forward ? Within() : Within() || Rows() || Head(),
        };
        if (moved) e.Handled = true;
    }

    private static ListBoxItem? FirstRow(ListBox list)
    {
        foreach (var item in list.Items)
            if (item is not ListExtra && list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem { Focusable: true } container) return container;
        return null;
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or TextBoxBase or Selector or Thumb or Expander) return;
            if (node is ListBoxItem item) { if (item.Content is ListExtra) e.Handled = true; return; }
        }
    }
}
