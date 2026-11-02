// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text;

namespace Winora.Desktop;

/// <summary>
/// The digit policy (decision DEC-IW-120 item 4): counts, dates and sizes in running Persian text use Persian digits; paths, versions, registry values, addresses and units stay Latin.
/// The .NET <c>fa</c> culture keeps Latin digits, so the mapping is done here. Search accepts both digit sets through <see cref="ToLatin"/>.
/// </summary>
public static class DigitConverter
{
    private const char PersianZero = '۰', ArabicIndicZero = '٠';

    public static string ToPersian(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) builder.Append(c is >= '0' and <= '9' ? (char)(PersianZero + (c - '0')) : c);
        return builder.ToString();
    }

    /// <summary>Persian (U+06F0) and Arabic-Indic (U+0660) digits become Latin digits; everything else is unchanged.</summary>
    public static string ToLatin(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(c is >= '۰' and <= '۹' ? (char)('0' + (c - PersianZero)) : c is >= '٠' and <= '٩' ? (char)('0' + (c - ArabicIndicZero)) : c);
        return builder.ToString();
    }

    /// <summary>True for the number types whose text is a count, size or date part and so follows the digit policy.</summary>
    public static bool IsNumber(object? value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    public static string Format(object value, CultureInfo culture) => ToPersian(Convert.ToString(value, culture) ?? "");
}
