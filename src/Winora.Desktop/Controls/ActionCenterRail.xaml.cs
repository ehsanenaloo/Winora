// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Controls;
using System.Windows.Shapes;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>The Action center while it is closed: a narrow strip at the inline end of the window. It only shows <see cref="ActionCenterViewModel"/>.</summary>
public partial class ActionCenterRail : UserControl
{
    public ActionCenterRail() => InitializeComponent();

    public System.Windows.Controls.Button Button => RailButton;
    public SymbolIcon IconControl => Icon;
    public System.Windows.Controls.Border BadgeControl => Badge;
    public SymbolIcon SpinnerControl => Spinner;
    public SymbolIcon SucceededControl => Succeeded;
    public SymbolIcon WarningControl => Warning;
    public Ellipse UnreadDot => Unread;
    public System.Windows.Controls.TextBlock LabelBlock => Label;
}
