// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Winora.Desktop.Controls;

/// <summary>
/// The one row of Winora: settings, inventory lists, power plans, cleanup groups, quarantine items. A card with a 3 px stripe at the Start side and a hover tint. The stripe shows the accent
/// while the row is selected in its list or marked (a ticked box), and the pending tint (with a tinted background) while a change is staged. Anatomy, from Start to End: <see cref="Lead"/> (outline icon,
/// app icon or letter avatar), <see cref="ContentControl.Content"/> (title with its badges, description, meta line), <see cref="Control"/> (switch, segments, list, button, box) and <see cref="Menu"/>
/// (the kebab). Rows narrower than <see cref="WideWidth"/> put the control under the text. The rows of one group card share the card edge: <see cref="IsFirst"/> and <see cref="IsLast"/> say which
/// edges and corners a row draws; a row with both set is a card of its own. Height and padding follow the density tokens. The row is not a tab stop; its list item or its own controls take focus.
/// </summary>
public sealed class InventoryRow : ContentControl
{
    /// <summary>Rows narrower than this stack the control under the text.</summary>
    public const double WideWidth = 640;

    static InventoryRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(InventoryRow), new FrameworkPropertyMetadata(typeof(InventoryRow)));
        FocusableProperty.OverrideMetadata(typeof(InventoryRow), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(InventoryRow), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty IsMarkedProperty = DependencyProperty.Register(nameof(IsMarked), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty IsPendingProperty = DependencyProperty.Register(nameof(IsPending), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty IsSelectedInListProperty = DependencyProperty.Register(nameof(IsSelectedInList), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty StacksWhenNarrowProperty = DependencyProperty.Register(nameof(StacksWhenNarrow), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty IsFirstProperty = DependencyProperty.Register(nameof(IsFirst), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(true));
    public static readonly DependencyProperty IsLastProperty = DependencyProperty.Register(nameof(IsLast), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(true));
    public static readonly DependencyProperty LeadProperty = DependencyProperty.Register(nameof(Lead), typeof(object), typeof(InventoryRow), new FrameworkPropertyMetadata(null, OnSlotChanged));
    public static readonly DependencyProperty ControlProperty = DependencyProperty.Register(nameof(Control), typeof(object), typeof(InventoryRow), new FrameworkPropertyMetadata(null, OnSlotChanged));
    public static readonly DependencyProperty MenuProperty = DependencyProperty.Register(nameof(Menu), typeof(object), typeof(InventoryRow), new FrameworkPropertyMetadata(null, OnSlotChanged));
    private static readonly DependencyPropertyKey HasLeadPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasLead), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasLeadProperty = HasLeadPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey HasControlPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasControl), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasControlProperty = HasControlPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey HasMenuPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasMenu), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty HasMenuProperty = HasMenuPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey IsWidePropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsWide), typeof(bool), typeof(InventoryRow), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty IsWideProperty = IsWidePropertyKey.DependencyProperty;

    /// <summary>The row is chosen without list selection, for example its box is ticked.</summary>
    public bool IsMarked { get => (bool)GetValue(IsMarkedProperty); set => SetValue(IsMarkedProperty, value); }
    /// <summary>A change of this row is staged; the stripe takes the pending tint and the row a tinted background.</summary>
    public bool IsPending { get => (bool)GetValue(IsPendingProperty); set => SetValue(IsPendingProperty, value); }
    /// <summary>Follows <see cref="ListBoxItem.IsSelected"/> of the list item that holds the row; false outside a list.</summary>
    public bool IsSelectedInList { get => (bool)GetValue(IsSelectedInListProperty); private set => SetValue(IsSelectedInListProperty, value); }
    /// <summary>False keeps the control beside the text at any width (rows of a narrow side list).</summary>
    public bool StacksWhenNarrow { get => (bool)GetValue(StacksWhenNarrowProperty); set => SetValue(StacksWhenNarrowProperty, value); }
    /// <summary>The first row of its group card: draws the top edge and the top corners.</summary>
    public bool IsFirst { get => (bool)GetValue(IsFirstProperty); set => SetValue(IsFirstProperty, value); }
    /// <summary>The last row of its group card: draws the bottom edge and the bottom corners.</summary>
    public bool IsLast { get => (bool)GetValue(IsLastProperty); set => SetValue(IsLastProperty, value); }
    /// <summary>The outline icon, app icon, letter avatar or box at the Start side.</summary>
    public object? Lead { get => GetValue(LeadProperty); set => SetValue(LeadProperty, value); }
    /// <summary>What changes the row at the End side: switch with its state word, segments, list, button, box.</summary>
    public object? Control { get => GetValue(ControlProperty); set => SetValue(ControlProperty, value); }
    /// <summary>The kebab at the very End.</summary>
    public object? Menu { get => GetValue(MenuProperty); set => SetValue(MenuProperty, value); }
    public bool HasLead => (bool)GetValue(HasLeadProperty);
    public bool HasControl => (bool)GetValue(HasControlProperty);
    public bool HasMenu => (bool)GetValue(HasMenuProperty);
    /// <summary>The row is wide enough to keep the control beside the text.</summary>
    public bool IsWide => (bool)GetValue(IsWideProperty);

    private ListBoxItem? container;
    private readonly List<object> slotChildren = [];

    // The slots are logical children, so bindings in them see the data context of the row.
    protected override IEnumerator LogicalChildren
    {
        get
        {
            var inherited = base.LogicalChildren;
            var all = new List<object>();
            while (inherited is not null && inherited.MoveNext()) all.Add(inherited.Current);
            all.AddRange(slotChildren);
            return all.GetEnumerator();
        }
    }

    private static void OnSlotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (InventoryRow)d;
        if (e.OldValue is not null && row.slotChildren.Remove(e.OldValue)) row.RemoveLogicalChild(e.OldValue);
        if (e.NewValue is UIElement or ContentElement) { row.slotChildren.Add(e.NewValue); row.AddLogicalChild(e.NewValue); }
        row.SetValue(HasLeadPropertyKey, row.Lead is not null);
        row.SetValue(HasControlPropertyKey, row.Control is not null);
        row.SetValue(HasMenuPropertyKey, row.Menu is not null);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this);

    // A row is a group in the automation tree. It is a control element only when the page names it; an unnamed row (the card of a setting inside its list item) adds no node.
    private sealed class RowPeer(InventoryRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(InventoryRow);
        protected override bool IsControlElementCore() => !string.IsNullOrEmpty(AutomationProperties.GetName(owner));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var wide = !StacksWhenNarrow || (!double.IsInfinity(constraint.Width) && constraint.Width >= WideWidth);
        if (wide != IsWide) SetValue(IsWidePropertyKey, wide);
        return base.MeasureOverride(constraint);
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        Attach();
    }

    // A recycled row can move to another list item, so the binding follows the current parent chain.
    private void Attach()
    {
        DependencyObject? node = VisualTreeHelper.GetParent(this);
        while (node is not null and not ListBoxItem) node = VisualTreeHelper.GetParent(node);
        var item = node as ListBoxItem;
        if (ReferenceEquals(item, container)) return;
        container = item;
        if (item is null) { BindingOperations.ClearBinding(this, IsSelectedInListProperty); IsSelectedInList = false; }
        else SetBinding(IsSelectedInListProperty, new Binding(nameof(ListBoxItem.IsSelected)) { Source = item, Mode = BindingMode.OneWay });
    }
}
