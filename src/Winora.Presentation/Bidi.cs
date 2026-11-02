// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;

namespace Winora.Desktop;

/// <summary>
/// Bidirectional text helpers. In a right-to-left page WPF places a digit that follows a Latin word and a space on the wrong side of the word
/// ("Publisher 1" shows as "1 Publisher"), and it ignores the explicit embedding characters. A left-to-right mark in front of the Latin token and
/// before each number inside it keeps the token in its own order while the page direction still orders the tokens. Marks are display-only:
/// stored values, search text and copied text stay untouched (use <see cref="Strip"/> to compare).
/// </summary>
public static partial class Bidi
{
    public const char LeftToRightMark = '‎', RightToLeftMark = '‏';

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>
    /// The token pinned to left-to-right order when the page is right-to-left; unchanged otherwise. Each run of digits gets a mark on both sides:
    /// WPF gives a space or punctuation between a digit and a letter the page direction ("1394 OHCI" shows as "OHCI 1394", "i7-1065G7" splits at the dash),
    /// and a mark next to it keeps it in the token.
    /// </summary>
    public static string Ltr(string? token, TextDirection direction)
    {
        if (direction != TextDirection.RightToLeft || string.IsNullOrEmpty(token)) return token ?? "";
        var mark = LeftToRightMark.ToString();
        var marked = mark + Digits().Replace(token, m => mark + m.Value + mark) + mark;
        return marked.Replace(mark + mark, mark, StringComparison.Ordinal);
    }

    /// <summary>
    /// Joins display parts. Left-to-right parts (publisher, version) are pinned; the separator is wrapped in right-to-left marks so it cannot glue two
    /// pinned parts into one left-to-right run, which would put the first part on the wrong side in a right-to-left row.
    /// </summary>
    public static string Compose(string separator, TextDirection direction, params (string? Text, bool Ltr)[] parts)
    {
        var items = parts.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Ltr ? Ltr(p.Text, direction) : p.Text!);
        var glue = direction == TextDirection.RightToLeft ? RightToLeftMark + separator + RightToLeftMark : separator;
        return string.Join(glue, items);
    }

    public static string Join(string separator, TextDirection direction, params string?[] ltrParts) =>
        Compose(separator, direction, ltrParts.Select(p => (p, true)).ToArray());

    /// <summary>True when the text has a Hebrew, Arabic or Persian letter, so it orders itself in a right-to-left row.</summary>
    public static bool HasRightToLeft(string text) => text.Any(c => c is (>= (char)0x0590 and <= (char)0x08FF) or (>= (char)0xFB1D and <= (char)0xFDFF) or (>= (char)0xFE70 and <= (char)0xFEFC));

    public static string Strip(string text) => text.Replace(LeftToRightMark.ToString(), "").Replace(RightToLeftMark.ToString(), "");
}
