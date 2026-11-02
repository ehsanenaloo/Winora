// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;

namespace Winora.Desktop.Controls;

/// <summary>Reads a margin or padding token (WinoraSpace.* in Styles/Tokens.xaml) for code that builds elements; code never writes a Thickness of its own.</summary>
internal static class Spacing
{
    public static Thickness Space(this FrameworkElement owner, string name) => (Thickness)owner.FindResource("WinoraSpace." + name);
}
