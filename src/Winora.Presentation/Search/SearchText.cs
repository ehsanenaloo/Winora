// SPDX-License-Identifier: GPL-3.0-only
using System.Text;

namespace Winora.Desktop.Search;

/// <summary>
/// How the search index and the query are made comparable: lower case, one form of the Persian letters, no zero-width joiner and no tatweel, and Latin digits
/// for Persian and Arabic-Indic digits. The map says where each normalized character came from, so a match can be shown in bold in the original text.
/// </summary>
public static class SearchText
{
    public static string Normalize(string? text) => Normalize(text, out _);

    public static string Normalize(string? text, out int[] map)
    {
        if (string.IsNullOrEmpty(text)) { map = []; return ""; }
        var sb = new StringBuilder(text.Length);
        var indexes = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsDropped(c)) continue;
            sb.Append(Map(c));
            indexes.Add(i);
        }
        map = [.. indexes];
        return sb.ToString();
    }

    private static bool IsDropped(char c) => c is '‌' or '‍' or '‎' or '‏' or 'ـ' or 'ٰ' || c is >= 'ً' and <= 'ٟ';

    private static char Map(char c) => c switch
    {
        'ي' or 'ى' => 'ی',
        'ك' => 'ک',
        >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
        >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
        _ => char.ToLowerInvariant(c),
    };

    /// <summary>The words of a text, normalized. A word is a run of letters and digits.</summary>
    public static string[] Words(string? text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return [];
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var inWord = i < normalized.Length && char.IsLetterOrDigit(normalized[i]);
            if (inWord && start < 0) start = i;
            else if (!inWord && start >= 0) { words.Add(normalized[start..i]); start = -1; }
        }
        return [.. words];
    }

    /// <summary>The text cut into parts, in order, with the parts that start a word matching one of the query words marked. Used to show the match in bold.</summary>
    public static IReadOnlyList<(string Text, bool Match)> Highlight(string text, IReadOnlyList<string> queryWords)
    {
        if (string.IsNullOrEmpty(text) || queryWords.Count == 0) return [(text ?? "", false)];
        var normalized = Normalize(text, out var map);
        var marked = new bool[text.Length];
        var wordStart = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var inWord = i < normalized.Length && char.IsLetterOrDigit(normalized[i]);
            if (inWord && wordStart < 0) wordStart = i;
            else if (!inWord && wordStart >= 0)
            {
                var word = normalized[wordStart..i];
                foreach (var q in queryWords)
                {
                    if (!word.StartsWith(q, StringComparison.Ordinal)) continue;
                    var last = wordStart + q.Length - 1;
                    for (var k = map[wordStart]; k <= map[last]; k++) marked[k] = true;
                    break;
                }
                wordStart = -1;
            }
        }
        var parts = new List<(string, bool)>();
        var from = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i < text.Length && marked[i] == marked[from]) continue;
            parts.Add((text[from..i], marked[from]));
            from = i;
        }
        return parts;
    }
}
