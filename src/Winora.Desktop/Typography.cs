// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;

namespace Winora.Desktop;

/// <summary>Persian keeps the type sizes but needs 10% more line height for its taller marks. The styles read the line height keys through DynamicResource.</summary>
public static class Typography
{
    public const double PersianLineHeightScale = 1.1;

    private static readonly (string Key, double Base)[] Lines =
    [
        ("WinoraLineCaption", 16), ("WinoraLineBody", 20), ("WinoraLineSection", 22), ("WinoraLineSubtitle", 28),
        ("WinoraLineValue", 32), ("WinoraLineTitle", 36), ("WinoraLineDisplay", 40),
    ];

    public static double ScaleFor(string language) => language == "fa" ? PersianLineHeightScale : 1.0;

    public static void Apply(string language)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        var scale = ScaleFor(language);
        void Write()
        {
            app.Resources["WinoraLineHeightScale"] = scale;
            foreach (var (key, size) in Lines) app.Resources[key] = Math.Round(size * scale, 1);
        }
        if (app.Dispatcher.CheckAccess()) Write(); else app.Dispatcher.BeginInvoke(Write);
    }
}
