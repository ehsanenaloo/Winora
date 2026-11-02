// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop.Search;

/// <summary>The result groups, in the order they are shown.</summary>
public enum SearchGroup { Settings, Pages, Actions }

/// <summary>Text of the app in a language, for the index (it holds both languages) and for what is shown.</summary>
public interface ISearchText
{
    /// <summary>The text of a key in the language of the window.</summary>
    string Current(string key);
    /// <summary>The text of a key in one language ("en" or "fa"), or null when there is none.</summary>
    string? In(string key, string language);
}

/// <summary>One thing the search can open. The keys are resource keys; the index looks them up in both languages.</summary>
/// <param name="TargetId">What the page should highlight (a setting id, a profile id, a tool id); null opens the page only.</param>
/// <param name="BreadcrumbKeys">Resource keys of the path under the title, for example the page and the category.</param>
public sealed record SearchSeed(string Id, SearchGroup Group, string TitleKey, string? DescriptionKey, string PageKey, string? TargetId, IReadOnlyList<string> BreadcrumbKeys,
    IconKey Icon, RiskLevel? Risk = null, SourceClass? Source = null, IReadOnlyList<string>? KeywordKeys = null, IReadOnlyList<string>? Keywords = null);

/// <summary>A match, with the text for the window language.</summary>
public sealed record SearchHit(SearchSeed Seed, string Title, string Breadcrumb, int Rank)
{
    public string Id => Seed.Id;
    public SearchGroup Group => Seed.Group;
}

public sealed record SearchGroupResult(SearchGroup Group, IReadOnlyList<SearchHit> Hits);

/// <summary>
/// The search over settings, pages and actions: built in memory from the catalogs, both languages at once (a Persian user can type an English name). Every word of the query must
/// begin a word of the title, the description or the keywords. The best field decides the rank: exact title, title start, title word, description, keyword. No fuzzy matching.
/// The index reads and writes nothing outside the process.
/// </summary>
public sealed class SearchIndex
{
    public const int TitleExact = 0, TitlePrefix = 1, TitleWord = 2, Description = 3, Keyword = 4;

    private sealed record Entry(SearchSeed Seed, string[] Titles, string[][] TitleWords, string[] DescriptionWords, string[] KeywordWords, int Order);

    private readonly List<Entry> entries = [];
    private readonly ISearchText text;

    public SearchIndex(IEnumerable<SearchSeed> seeds, ISearchText text)
    {
        this.text = text;
        var order = 0;
        foreach (var seed in seeds)
        {
            var titles = new List<string>(); var titleWords = new List<string[]>(); var descriptions = new List<string>(); var keywords = new List<string>();
            foreach (var language in new[] { "en", "fa" })
            {
                if (text.In(seed.TitleKey, language) is { Length: > 0 } title && !titles.Contains(SearchText.Normalize(title))) { titles.Add(SearchText.Normalize(title)); titleWords.Add(SearchText.Words(title)); }
                if (seed.DescriptionKey is not null && text.In(seed.DescriptionKey, language) is { } description) descriptions.Add(description);
                foreach (var key in seed.KeywordKeys ?? []) if (text.In(key, language) is { } word) keywords.Add(word);
                foreach (var key in seed.BreadcrumbKeys) if (text.In(key, language) is { } crumb) keywords.Add(crumb);
            }
            keywords.AddRange(seed.Keywords ?? []);
            keywords.Add(seed.Id);
            entries.Add(new(seed, [.. titles], [.. titleWords], descriptions.SelectMany(SearchText.Words).Distinct().ToArray(), keywords.SelectMany(SearchText.Words).Distinct().ToArray(), order++));
        }
    }

    public int Count => entries.Count;

    /// <summary>The matches by group, in the fixed group order, each group best first. Empty groups are left out. A blank query has no result.</summary>
    public IReadOnlyList<SearchGroupResult> Search(string? query)
    {
        var words = SearchText.Words(query);
        if (words.Length == 0) return [];
        var whole = string.Join(' ', words);
        var hits = new List<(SearchHit Hit, int Length, int Order)>();
        foreach (var entry in entries)
        {
            var rank = RankOf(entry, words, whole);
            if (rank < 0) continue;
            var title = text.Current(entry.Seed.TitleKey);
            hits.Add((new(entry.Seed, title, string.Join(" > ", entry.Seed.BreadcrumbKeys.Select(text.Current)), rank), title.Length, entry.Order));
        }
        return hits.GroupBy(h => h.Hit.Group).OrderBy(g => g.Key)
            .Select(g => new SearchGroupResult(g.Key, [.. g.OrderBy(h => h.Hit.Rank).ThenBy(h => h.Length).ThenBy(h => h.Order).Select(h => h.Hit)])).ToList();
    }

    private static int RankOf(Entry entry, string[] words, string whole)
    {
        var worst = -1;
        foreach (var word in words)
        {
            var best = int.MaxValue;
            foreach (var titleWords in entry.TitleWords) if (titleWords.Any(w => w.StartsWith(word, StringComparison.Ordinal))) best = Math.Min(best, TitleWord);
            if (best == int.MaxValue && entry.DescriptionWords.Any(w => w.StartsWith(word, StringComparison.Ordinal))) best = Description;
            if (best == int.MaxValue && entry.KeywordWords.Any(w => w.StartsWith(word, StringComparison.Ordinal))) best = Keyword;
            if (best == int.MaxValue) return -1;
            worst = Math.Max(worst, best);
        }
        if (worst == TitleWord)
        {
            // Words that all match the title can still rank higher when the whole query is the title or begins it.
            var joined = entry.TitleWords.Select(w => string.Join(' ', w)).ToArray();
            if (joined.Any(t => t == whole)) return TitleExact;
            if (joined.Any(t => t.StartsWith(whole, StringComparison.Ordinal))) return TitlePrefix;
        }
        return worst;
    }
}
