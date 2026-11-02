// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>The status strip under the page, inside the content area. It shows <see cref="ActionCenterViewModel"/> and is gone when there is nothing to say; toasts sit above it because it has its own row.</summary>
public partial class ActionCenterStrip : UserControl
{
    public ActionCenterStrip() => InitializeComponent();
}
