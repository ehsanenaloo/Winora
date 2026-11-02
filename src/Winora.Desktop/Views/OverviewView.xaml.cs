// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using Winora.Desktop.Controls;
namespace Winora.Desktop;

public partial class OverviewView : UserControl
{
    private const double PageGutter = 64, MinCard = 232, StackBelow = 760;

    public static readonly DependencyProperty TileColumnsProperty = DependencyProperty.Register(nameof(TileColumns), typeof(int), typeof(OverviewView), new PropertyMetadata(4));
    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(OverviewView), new PropertyMetadata(4));

    public OverviewView() => InitializeComponent();

    /// <summary>Columns of the inventory tiles and of the cards. Cards share the page width equally instead of keeping a fixed size.</summary>
    public int TileColumns { get => (int)GetValue(TileColumnsProperty); set => SetValue(TileColumnsProperty, value); }
    public int CardColumns { get => (int)GetValue(CardColumnsProperty); set => SetValue(CardColumnsProperty, value); }

    public static int ColumnsFor(double pageWidth) => (int)Math.Clamp(Math.Floor(Math.Max(0, pageWidth - PageGutter) / MinCard), 1, 4);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = ColumnsFor(Math.Min(e.NewSize.Width, Page.MaxWidth + PageGutter));
        TileColumns = columns; CardColumns = columns;
        // On a narrow page the score body sits under the ring instead of beside it.
        var stacked = e.NewSize.Width < StackBelow;
        RingColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        BodyColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        BodyRow.Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(ScoreBody, stacked ? 0 : 1); Grid.SetRow(ScoreBody, stacked ? 1 : 0);
        RingBox.Margin = this.Space(stacked ? "B16" : "R24");
        RingBox.HorizontalAlignment = stacked ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }
}
