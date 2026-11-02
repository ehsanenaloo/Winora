// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using Winora.Desktop.Controls;
using System.Windows.Automation;
using Wpf.Ui;
using Wpf.Ui.Extensions;
using Wpf.Ui.Controls;

namespace Winora.Desktop;

/// <summary>The short "How to start as administrator" dialog that the administrator notice of every page opens.</summary>
public partial class MainWindow : Controls.IAdminHelpHost
{
    public Task ShowAdminHelpAsync() => OnUi(() =>
    {
        var panel = new System.Windows.Controls.StackPanel { MinWidth = 400, MaxWidth = 460 };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Text["AdminHelpIntro"], Style = (Style)FindResource("WinoraBody") });
        var steps = new System.Windows.Controls.StackPanel { Margin = this.Space("T12") };
        string[] keys = ["AdminHelpStep1", "AdminHelpStep2", "AdminHelpStep3"];
        for (var i = 0; i < keys.Length; i++)
            steps.Children.Add(new System.Windows.Controls.TextBlock { Text = model.Text.Format("AdminHelpStep", i + 1, model.Text[keys[i]]), Style = (Style)FindResource("WinoraBody"), Margin = this.Space("B4") });
        panel.Children.Add(steps);
        AutomationProperties.SetName(panel, model.Text["AdminHelpTitle"]);
        return ShowDialogAsync(new SimpleContentDialogCreateOptions { Title = model.Text["AdminHelpTitle"], Content = panel, CloseButtonText = model.Text["Close"] });
    });
}
