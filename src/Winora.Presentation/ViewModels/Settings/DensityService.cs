// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>The two densities of the interface and the preference that stores the choice. Winora.Desktop swaps the size resources (<c>DensityResources</c>).</summary>
public static class DensityService
{
    public const string Comfortable = "Comfortable";
    public const string Compact = "Compact";
    public const string PreferenceKey = "density";

    public static string Normalize(string? code) => code == Compact ? Compact : Comfortable;
}
