// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Controls;
using System.Windows.Input;

namespace Winora.Desktop;

public partial class PermissionsView : UserControl
{
    public PermissionsView() => InitializeComponent();
    public void FocusSearch() => PageView.FocusSearch();
    private void OnFind(object sender, ExecutedRoutedEventArgs e) { if (PageView.IsVisible) FocusSearch(); }
    public Controls.SettingsPageView Page => PageView;
}
