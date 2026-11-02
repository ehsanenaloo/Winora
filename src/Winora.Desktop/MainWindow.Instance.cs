// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
namespace Winora.Desktop;

/// <summary>What the window does when another start hands over a page.</summary>
public partial class MainWindow
{
    /// <summary>
    /// Brings the window to the front and, when <paramref name="pageKey"/> is given, opens that page. While a dialog is open the window only comes forward,
    /// so a page change never hides a question the user has not answered. The key was checked against the allow list before this call.
    /// </summary>
    public void ShowFromHandOver(string? pageKey)
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (pageKey is not null && !IsModalOpen) model.Navigate(pageKey);
        // The sender gave this process the right to take the foreground; the topmost toggle covers the case where it did not arrive.
        Activate();
        if (!IsActive) { Topmost = true; Topmost = false; }
        Focus();
    }
}
