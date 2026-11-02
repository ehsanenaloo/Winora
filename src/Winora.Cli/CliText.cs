// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Resources;
namespace Winora.Cli;

/// <summary>Localized CLI text from Strings.resx (neutral English) and Strings.fa.resx, resolved against the current UI culture.</summary>
internal static class CliText
{
    private static readonly ResourceManager Manager = new("Winora.Cli.Resources.Strings", typeof(CliText).Assembly);

    public static string T(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? Manager.GetString(key, CultureInfo.InvariantCulture) ?? key;

    public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
}
