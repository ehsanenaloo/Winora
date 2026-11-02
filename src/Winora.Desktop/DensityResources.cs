// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;

namespace Winora.Desktop;

/// <summary>
/// Comfortable or compact sizes. Swaps Styles/DensityComfortable.xaml or DensityCompact.xaml in the application resources.
/// Elements that read the keys through DynamicResource follow at once; elements that use StaticResource keep the value they were created with.
/// </summary>
public static class DensityResources
{
    private static ResourceDictionary? active;

    public static string Current { get; private set; } = DensityService.Comfortable;

    public static Uri SourceOf(string code) => new($"pack://application:,,,/Winora.Desktop;component/Styles/Density{DensityService.Normalize(code)}.xaml");

    public static void Apply(string? code)
    {
        Current = DensityService.Normalize(code);
        var app = System.Windows.Application.Current;
        if (app is null) return;
        var merged = app.Resources.MergedDictionaries;
        var index = merged.Count;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var source = merged[i].Source?.OriginalString ?? "";
            if (!ReferenceEquals(merged[i], active) && !source.Contains("Styles/Density", StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            merged.RemoveAt(i);
        }
        active = new ResourceDictionary { Source = SourceOf(Current) };
        merged.Insert(Math.Min(index, merged.Count), active);
    }
}
