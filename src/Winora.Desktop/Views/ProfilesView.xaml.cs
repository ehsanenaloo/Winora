// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using Winora.Desktop.Search;

namespace Winora.Desktop;

public partial class ProfilesView : UserControl, IHighlightTarget
{
    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(ProfilesView), new PropertyMetadata(2));
    /// <summary>Two columns of profile cards, one under 800 px of page width.</summary>
    public int CardColumns { get => (int)GetValue(CardColumnsProperty); set => SetValue(CardColumnsProperty, value); }

    public ProfilesView() => InitializeComponent();

    public static int ColumnsFor(double pageWidth) => pageWidth >= 800 ? 2 : 1;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => CardColumns = ColumnsFor(e.NewSize.Width);

    public bool Highlight(string targetId)
    {
        if (DataContext is not ProfilesViewModel page) return false;
        var row = page.BuiltInCards.FirstOrDefault(c => c.Profile.Id == targetId);
        return row is not null && BuiltInList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container && HighlightOutline.Show(container);
    }
}
