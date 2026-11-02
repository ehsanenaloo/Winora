// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Winora.Application;

/// <summary>A profile file that fails the strict parser. The code is a <see cref="ProfileCode"/>; the message names what is wrong and is for logs and tests, never for the screen.</summary>
public sealed class ProfileFormatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The strict reader and writer of profile files. A file is never trusted: it is size-limited, must be exactly the documented shape (no unknown or duplicate members), and every entry must name
/// an id that exists in the catalogs with one of its own choices. A High risk entry is refused. There is no member that could hold a path, a value, a command or free text other than a display name.
/// </summary>
public static partial class ProfileFile
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxEntries = 300;
    public const int MaxNameLength = 60;
    public const string CustomPrefix = "custom.";
    private const string FileKind = "winora-profile";

    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)] private static partial Regex BuiltInIdPattern();
    [GeneratedRegex("^custom\\.[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)] private static partial Regex CustomIdPattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)] private static partial Regex EntryIdPattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)] private static partial Regex ChoiceIdPattern();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,99}$", RegexOptions.CultureInvariant)] private static partial Regex KeyPattern();

    public static bool IsCustomId(string? id) => id is not null && CustomIdPattern().IsMatch(id);

    // ---- reading ----

    /// <summary>A built-in profile file: {"version":1,"profile":{"id","nameKey","descriptionKey","entries":[{"kind","id","choice","reasonKey"}]}}.</summary>
    public static Profile ParseBuiltIn(string json, IProfileReferences references)
    {
        using var doc = Open(json);
        var root = Members(doc.RootElement, "file", "version", "profile");
        Version(root);
        var p = Members(Require(root, "profile"), "profile", "id", "nameKey", "descriptionKey", "entries");
        var id = Text(p, "id");
        if (!BuiltInIdPattern().IsMatch(id)) throw Bad($"profile id '{Clip(id)}' is not valid");
        var nameKey = Key(p, "nameKey");
        var descriptionKey = Key(p, "descriptionKey");
        return new(id, nameKey, descriptionKey, null, Entries(Require(p, "entries"), references, builtIn: true));
    }

    /// <summary>A custom profile file: {"version":1,"kind":"winora-profile","name":"...","entries":[{"kind","id","choice"}]}. The id is assigned by the caller, never read from the file.</summary>
    public static Profile ParseCustom(string json, string id, IProfileReferences references)
    {
        if (!IsCustomId(id)) throw Bad("the profile id is not valid");
        using var doc = Open(json);
        var root = Members(doc.RootElement, "file", "version", "kind", "name", "entries");
        Version(root);
        if (Text(root, "kind") != FileKind) throw Bad("the file is not a Winora profile");
        var name = CleanName(Text(root, "name"));
        return new(id, null, null, name, Entries(Require(root, "entries"), references, builtIn: false));
    }

    private static JsonDocument Open(string json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new ProfileFormatException(ProfileCode.TooLarge, "the file is too large");
        try { return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 6, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException e) { throw Bad("the file is not valid JSON: " + e.GetType().Name); }
    }

    private static void Version(Dictionary<string, JsonElement> root)
    {
        var v = Require(root, "version");
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n != 1) throw Bad("unsupported file version");
    }

    private static IReadOnlyList<ProfileEntry> Entries(JsonElement array, IProfileReferences references, bool builtIn)
    {
        if (array.ValueKind != JsonValueKind.Array) throw Bad("entries must be an array");
        var count = array.GetArrayLength();
        if (count is 0) throw Bad("a profile needs at least one entry");
        if (count > MaxEntries) throw new ProfileFormatException(ProfileCode.TooMany, "too many entries");
        var list = new List<ProfileEntry>(count);
        var seen = new HashSet<(ProfileEntryKind, string)>();
        foreach (var element in array.EnumerateArray())
        {
            var m = builtIn ? Members(element, "entry", "kind", "id", "choice", "reasonKey") : Members(element, "entry", "kind", "id", "choice");
            var kind = Text(m, "kind") switch { "user" => ProfileEntryKind.User, "machine" => ProfileEntryKind.Machine, _ => throw Bad("entry kind must be 'user' or 'machine'") };
            var id = Text(m, "id");
            var choice = Text(m, "choice");
            if (!EntryIdPattern().IsMatch(id)) throw Bad($"entry id '{Clip(id)}' is not valid");
            if (!ChoiceIdPattern().IsMatch(choice)) throw Bad($"choice '{Clip(choice)}' is not valid");
            if (!seen.Add((kind, id))) throw Bad($"entry '{id}' is listed twice");
            var reference = references.Find(kind, id) ?? throw Bad($"entry '{id}' is not in the catalog");
            if (reference.Risk == Domain.RiskLevel.High) throw new ProfileFormatException(ProfileCode.HighRisk, $"entry '{id}' has risk High");
            if (!reference.ChoiceIds.Contains(choice)) throw Bad($"entry '{id}' has no choice '{choice}'");
            list.Add(new(kind, id, choice, builtIn ? Key(m, "reasonKey") : null));
        }
        return list;
    }

    // ---- writing ----

    /// <summary>The file text of a custom profile.</summary>
    public static string WriteCustom(Profile profile)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("kind", FileKind);
            w.WriteString("name", profile.Name ?? profile.Id);
            w.WriteStartArray("entries");
            foreach (var e in profile.Entries)
            {
                w.WriteStartObject();
                w.WriteString("kind", e.Kind == ProfileEntryKind.User ? "user" : "machine");
                w.WriteString("id", e.Id);
                w.WriteString("choice", e.ChoiceId);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---- names ----

    /// <summary>The display name: trimmed, 1 to 60 characters, no control or direction-control characters. Throws when nothing usable is left.</summary>
    public static string CleanName(string? name)
    {
        var s = (name ?? "").Trim();
        if (s.Length is 0 or > MaxNameLength) throw new ProfileFormatException(ProfileCode.EmptyName, "the profile name must have 1 to 60 characters");
        foreach (var c in s)
            if (char.IsControl(c) || c is '‎' or '‏' or '؜' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') || char.IsSurrogate(c))
                throw new ProfileFormatException(ProfileCode.EmptyName, "the profile name has a character that is not allowed");
        return s;
    }

    /// <summary>A custom id from a display name: lower case ASCII letters and digits with single dashes. Falls back to "profile" when the name has none (for example Persian text).</summary>
    public static string SlugFor(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 32) break;
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "profile" : slug;
    }

    // ---- helpers ----

    private static ProfileFormatException Bad(string message) => new(ProfileCode.Invalid, message);

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "...";

    /// <summary>The members of an object as a map. A member that appears twice, a member that is not allowed or a value that is not an object is refused.</summary>
    private static Dictionary<string, JsonElement> Members(JsonElement element, string what, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Bad($"{what} must be an object");
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0) throw Bad($"{what} has an unknown member '{Clip(property.Name)}'");
            if (!map.TryAdd(property.Name, property.Value)) throw new ProfileFormatException(ProfileCode.Invalid, $"{what} repeats the member '{property.Name}'");
        }
        return map;
    }

    private static JsonElement Require(Dictionary<string, JsonElement> map, string name) => map.TryGetValue(name, out var v) ? v : throw Bad($"the member '{name}' is missing");

    private static string Text(Dictionary<string, JsonElement> map, string name)
    {
        var v = Require(map, name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : throw Bad($"'{name}' must be text");
    }

    private static string Key(Dictionary<string, JsonElement> map, string name)
    {
        var key = Text(map, name);
        return KeyPattern().IsMatch(key) ? key : throw Bad($"'{name}' is not a valid resource key");
    }
}
