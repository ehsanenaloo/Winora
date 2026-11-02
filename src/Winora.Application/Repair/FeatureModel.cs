// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace Winora.Application;

public enum FeatureState { Enabled, Disabled, EnablePending, DisablePending, Unknown }

public enum FeatureClass { Safe, Caution, Protected, Unknown }

/// <summary>One row of the table that dism.exe prints for /Get-Features.</summary>
public sealed record ParsedFeature(string Name, FeatureState State, string RawState);

public static class DismFeatureParser
{
    /// <summary>
    /// Reads the table of "dism /Online /English /Get-Features /Format:Table". Only lines with a feature name, a bar and a state count; headers, rules and package lines are skipped. A line whose name
    /// is not a plain feature name is dropped, so no odd text can reach a later command.
    /// </summary>
    public static IReadOnlyList<ParsedFeature> Parse(string output)
    {
        var result = new List<ParsedFeature>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (output ?? "").Split('\n'))
        {
            var parts = line.Split('|');
            if (parts.Length != 2) continue;
            var name = parts[0].Trim();
            var raw = parts[1].Trim();
            if (name.Length == 0 || raw.Length == 0 || name.StartsWith('-') || name.Equals("Feature Name", StringComparison.OrdinalIgnoreCase)) continue;
            if (!RepairRules.FeatureName.IsMatch(name) || !seen.Add(name)) continue;
            result.Add(new(name, StateOf(raw), raw));
        }
        return result;
    }

    private static FeatureState StateOf(string raw) => new string(raw.Where(char.IsLetter).ToArray()).ToLowerInvariant() switch
    {
        "enabled" => FeatureState.Enabled,
        "disabled" or "disabledwithpayloadremoved" => FeatureState.Disabled,
        "enablepending" => FeatureState.EnablePending,
        "disablepending" => FeatureState.DisablePending,
        _ => FeatureState.Unknown,
    };
}

/// <summary>The reviewed facts about one feature name (or a name prefix). <see cref="DisableBlockedBy"/> lists features that must be off before this one may be turned off.</summary>
public sealed record FeatureClassEntry(string Name, bool IsPrefix, FeatureClass Class, string Description, string Source, IReadOnlyList<string> DisableBlockedBy);

/// <summary>The feature list that ships in the app (definitions/features/FeatureClasses.v1.json). A file that fails the strict parser is an error, never a partial list.</summary>
public sealed class FeatureClassCatalog
{
    private const int MaxEntries = 400;
    public IReadOnlyList<FeatureClassEntry> Entries { get; }

    public FeatureClassCatalog(IReadOnlyList<FeatureClassEntry> entries) { Entries = entries; }

    /// <summary>An exact name wins over a prefix; the longest prefix wins over a shorter one. Null means the feature is not in the reviewed list.</summary>
    public FeatureClassEntry? Find(string name)
    {
        var exact = Entries.FirstOrDefault(e => !e.IsPrefix && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return Entries.Where(e => e.IsPrefix && name.StartsWith(e.Name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Name.Length).FirstOrDefault();
    }

    public static FeatureClassCatalog Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException e) { throw new FormatException("The feature file is not valid JSON.", e); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1) throw new FormatException("Unsupported feature file version.");
            if (!root.TryGetProperty("features", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() is 0 or > MaxEntries) throw new FormatException("The feature list is missing or too long.");
            var list = new List<FeatureClassEntry>();
            foreach (var e in arr.EnumerateArray())
            {
                var name = Str(e, "name");
                var prefix = e.TryGetProperty("prefix", out var p) && p.ValueKind == JsonValueKind.True;
                if (!RepairRules.FeatureName.IsMatch(name) || list.Any(x => x.IsPrefix == prefix && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new FormatException($"Invalid or duplicate feature name '{name}'.");
                var cls = Str(e, "class") switch { "safe" => FeatureClass.Safe, "caution" => FeatureClass.Caution, "protected" => FeatureClass.Protected, _ => throw new FormatException($"Invalid class for '{name}'.") };
                var description = Str(e, "description");
                var source = Str(e, "source");
                if (description.Length is 0 or > 400 || source.Length is 0 or > 300) throw new FormatException($"Invalid text for '{name}'.");
                var blocked = new List<string>();
                if (e.TryGetProperty("disableBlockedBy", out var b))
                {
                    if (b.ValueKind != JsonValueKind.Array) throw new FormatException($"Invalid blocker list for '{name}'.");
                    foreach (var item in b.EnumerateArray())
                    {
                        var other = item.GetString() ?? "";
                        if (!RepairRules.FeatureName.IsMatch(other)) throw new FormatException($"Invalid blocker for '{name}'.");
                        blocked.Add(other);
                    }
                }
                list.Add(new(name, prefix, cls, description, source, blocked));
            }
            return new(list);
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : throw new FormatException($"Missing '{name}'.");
}
