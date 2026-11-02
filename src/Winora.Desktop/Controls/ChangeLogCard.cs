// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// The list of recent changes at the bottom of a page: one card with a header (icon, title, count, a chevron that turns) that opens a short scrolling list. It is closed at first, so a page
/// with a long history keeps its shape; the list inside never grows the page beyond <c>MaxBodyHeight</c>. Items bring their own template (a compact row).
/// </summary>
public sealed class ChangeLogCard : HeaderedItemsControl
{
    static ChangeLogCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ChangeLogCard), new FrameworkPropertyMetadata(typeof(ChangeLogCard)));
        FocusableProperty.OverrideMetadata(typeof(ChangeLogCard), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ChangeLogCard), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(ChangeLogCard),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty MaxBodyHeightProperty = DependencyProperty.Register(nameof(MaxBodyHeight), typeof(double), typeof(ChangeLogCard), new FrameworkPropertyMetadata(280.0));
    public static readonly DependencyProperty ToggleNameProperty = DependencyProperty.Register(nameof(ToggleName), typeof(string), typeof(ChangeLogCard), new FrameworkPropertyMetadata(""));

    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    /// <summary>The list scrolls inside the card beyond this height.</summary>
    public double MaxBodyHeight { get => (double)GetValue(MaxBodyHeightProperty); set => SetValue(MaxBodyHeightProperty, value); }
    /// <summary>The accessible name of the header button (the title and the count).</summary>
    public string ToggleName { get => (string)GetValue(ToggleNameProperty); set => SetValue(ToggleNameProperty, value); }

    /// <summary>The number of entries, for the header.</summary>
    public int Count => ItemsSource is ICollection c ? c.Count : Items.Count;

    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        SetValue(CountTextPropertyKey, Count.ToString(System.Globalization.CultureInfo.CurrentCulture));
    }

    protected override void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        SetValue(CountTextPropertyKey, Count.ToString(System.Globalization.CultureInfo.CurrentCulture));
    }

    private static readonly DependencyPropertyKey CountTextPropertyKey = DependencyProperty.RegisterReadOnly(nameof(CountText), typeof(string), typeof(ChangeLogCard), new FrameworkPropertyMetadata("0"));
    public static readonly DependencyProperty CountTextProperty = CountTextPropertyKey.DependencyProperty;
    public string CountText => (string)GetValue(CountTextProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(ChangeLogCard owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(ChangeLogCard);
    }
}
