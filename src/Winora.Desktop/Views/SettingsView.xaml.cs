// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
namespace Winora.Desktop;
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Winora opens no browser: the address of the project goes to the clipboard.</summary>
    private void OnCopyLink(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string address }) return;
        try { Clipboard.SetDataObject(address, true); } catch (System.Runtime.InteropServices.ExternalException) { /* another program holds the clipboard: nothing was copied */ }
    }
}
