// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
namespace Winora.Application;

/// <summary>How risky it is to remove a package. Protected is never removed by Winora; Unknown is shown but never selected.</summary>
public enum BloatClass { Protected, Safe, Caution, Unknown }

public enum RuleMatch { Exact, Prefix }

public enum RuleBasis { Docs, Observed }

/// <summary>
/// One reviewed rule. <see cref="Name"/> is compared with the whole package name (exact) or with its start (prefix, which must end in a dot); both ignore case. A prefix rule and every
/// Safe or Caution rule also need the publisher id, so a package of another publisher with a similar name never matches. <see cref="Group"/> selects the plain-language reason
/// (resource key <c>Bloat_Reason_{Group}</c>); <see cref="Reference"/> says where the rule comes from.
/// </summary>
public sealed record BloatRule(string Id, RuleMatch Match, string Name, string? PublisherId, BloatClass Class, string Group, RuleBasis Basis, string Reference, IReadOnlyList<string> Except)
{
    public bool Matches(string name, string publisherId)
    {
        if (PublisherId is not null && !string.Equals(PublisherId, publisherId, StringComparison.OrdinalIgnoreCase)) return false;
        if (Match == RuleMatch.Exact) return string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
        return name.StartsWith(Name, StringComparison.OrdinalIgnoreCase) && !Except.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record Classification(BloatClass Class, BloatRule? Rule);

/// <summary>The reviewed rule list. Parsing is strict: an unknown field, a missing publisher id, a wildcard in a name or a Safe or Caution rule that a Protected rule also covers is an error.</summary>
public sealed class BloatRuleSet
{
    public static BloatRuleSet Empty { get; } = new([]);

    private readonly IReadOnlyList<BloatRule> protectedRules;
    private readonly IReadOnlyList<BloatRule> otherRules;

    public BloatRuleSet(IReadOnlyList<BloatRule> rules)
    {
        Rules = rules;
        protectedRules = rules.Where(r => r.Class == BloatClass.Protected).ToList();
        otherRules = rules.Where(r => r.Class != BloatClass.Protected).ToList();
    }

    public IReadOnlyList<BloatRule> Rules { get; }

    /// <summary>Protected rules are checked first and win over everything. When a Safe and a Caution rule both match, the more careful one wins.</summary>
    public Classification Classify(string name, string publisherId)
    {
        if (protectedRules.FirstOrDefault(r => r.Matches(name, publisherId)) is { } p) return new(BloatClass.Protected, p);
        var hits = otherRules.Where(r => r.Matches(name, publisherId)).ToList();
        if (hits.Count == 0) return new(BloatClass.Unknown, null);
        var best = hits.FirstOrDefault(r => r.Class == BloatClass.Caution) ?? hits[0];
        return new(best.Class, best);
    }

    private static readonly HashSet<string> RuleFields = ["id", "match", "name", "publisherId", "class", "group", "basis", "reference", "except"];

    public static BloatRuleSet Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The rule file must be an object.");
        foreach (var p in root.EnumerateObject()) if (p.Name is not ("version" or "rules")) throw new FormatException($"Unknown field '{p.Name}'.");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1) throw new FormatException("Unsupported rule file version.");
        if (!root.TryGetProperty("rules", out var array) || array.ValueKind != JsonValueKind.Array) throw new FormatException("The rule file has no rule list.");
        var rules = new List<BloatRule>();
        foreach (var item in array.EnumerateArray()) rules.Add(ParseRule(item));
        var set = new BloatRuleSet(rules);
        set.Validate();
        return set;
    }

    private static string Text(JsonElement e, string field, bool required = true)
    {
        if (!e.TryGetProperty(field, out var v)) return required ? throw new FormatException($"Missing '{field}'.") : "";
        if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())) throw new FormatException($"'{field}' must be a non-empty string.");
        return v.GetString()!;
    }

    private static BloatRule ParseRule(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException("A rule must be an object.");
        foreach (var p in e.EnumerateObject()) if (!RuleFields.Contains(p.Name)) throw new FormatException($"Unknown rule field '{p.Name}'.");
        var match = Text(e, "match") switch { "exact" => RuleMatch.Exact, "prefix" => RuleMatch.Prefix, _ => throw new FormatException("Unknown match kind.") };
        var cls = Text(e, "class") switch { "protected" => BloatClass.Protected, "safe" => BloatClass.Safe, "caution" => BloatClass.Caution, _ => throw new FormatException("Unknown class.") };
        var basis = Text(e, "basis") switch { "docs" => RuleBasis.Docs, "observed" => RuleBasis.Observed, _ => throw new FormatException("Unknown basis.") };
        var publisher = Text(e, "publisherId", required: false);
        var except = new List<string>();
        if (e.TryGetProperty("except", out var ex))
        {
            if (ex.ValueKind != JsonValueKind.Array) throw new FormatException("'except' must be a list.");
            foreach (var x in ex.EnumerateArray()) except.Add(x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()) ? x.GetString()! : throw new FormatException("Bad 'except' entry."));
        }
        return new(Text(e, "id"), match, Text(e, "name"), publisher.Length == 0 ? null : publisher, cls, Text(e, "group"), basis, Text(e, "reference"), except);
    }

    private void Validate()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rules)
        {
            if (!ids.Add(r.Id)) throw new FormatException($"Duplicate rule id '{r.Id}'.");
            if (r.Name.IndexOfAny(['*', '?', '|', '\\', '/', ' ', '^', '$', '[', ']', '(', ')']) >= 0) throw new FormatException($"Rule '{r.Id}' has a pattern character in its name.");
            if (r.Match == RuleMatch.Prefix && (!r.Name.EndsWith('.') || r.PublisherId is null)) throw new FormatException($"Prefix rule '{r.Id}' must end in a dot and name a publisher id.");
            if (r.Match == RuleMatch.Exact && r.Except.Count > 0) throw new FormatException($"Rule '{r.Id}': only prefix rules have exceptions.");
            if (r.Class != BloatClass.Protected && r.PublisherId is null) throw new FormatException($"Rule '{r.Id}' needs a publisher id.");
            if (r.PublisherId is not null && r.PublisherId.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new FormatException($"Rule '{r.Id}' has a bad publisher id.");
            if (r.Except.Any(x => !x.StartsWith(r.Name, StringComparison.OrdinalIgnoreCase))) throw new FormatException($"Rule '{r.Id}': an exception is outside its prefix.");
        }
        // A Safe or Caution rule must never be covered by a Protected rule: the Protected rule would hide it for good.
        foreach (var r in Rules.Where(r => r.Class != BloatClass.Protected))
            if (r.Match == RuleMatch.Exact && protectedRules.FirstOrDefault(p => p.Matches(r.Name, r.PublisherId!)) is { } cover)
                throw new FormatException($"Rule '{r.Id}' is covered by the protected rule '{cover.Id}'.");
    }
}
