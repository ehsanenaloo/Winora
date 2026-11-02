// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>The one mapping from the meaning of a banner message (<see cref="BannerSeverity"/>) to the severity of the banner control and to how it is announced.</summary>
public static class BannerLook
{
    public static InfoBarSeverity SeverityOf(BannerSeverity severity) => severity switch
    {
        BannerSeverity.Success => InfoBarSeverity.Success,
        BannerSeverity.Warning => InfoBarSeverity.Warning,
        BannerSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public static BannerSeverity Of(InfoBarSeverity severity) => severity switch
    {
        InfoBarSeverity.Success => BannerSeverity.Success,
        InfoBarSeverity.Warning => BannerSeverity.Warning,
        InfoBarSeverity.Error => BannerSeverity.Error,
        _ => BannerSeverity.Informational,
    };

    public static AutomationLiveSetting LiveOf(bool urgent) => urgent ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;

    /// <summary>For a binding from a <see cref="BannerSeverity"/> to the severity of a banner.</summary>
    public static IValueConverter Severity { get; } = new Map(value => value is BannerSeverity s ? SeverityOf(s) : InfoBarSeverity.Informational);

    /// <summary>For a binding from "the message is urgent" to the live setting of a banner.</summary>
    public static IValueConverter Live { get; } = new Map(value => LiveOf(value is true));

    private sealed class Map(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => convert(value);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
