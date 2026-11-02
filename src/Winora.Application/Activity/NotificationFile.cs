// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Winora.Application.Activity;

/// <summary>
/// The stored form of the notification list: version, then per item text, severity, time and page key. Nothing else is written and nothing else is read, so an edited file can
/// change what a notification says but never attach an action. Anything that does not fit is dropped item by item; a file that does not parse gives an empty list.
/// </summary>
public static class NotificationFile
{
    public const int MaxItems = 200;
    public const int MaxAgeDays = 30;
    public const int MaxTextLength = 300;
    public const int MaxBytes = 256 * 1024;
    private const int Version = 1;

    public sealed record Item(NotificationSeverity Severity, string Text, string PageKey, DateTimeOffset At);

    public static string CleanText(string? text)
    {
        var sb = new StringBuilder();
        foreach (var c in text ?? "")
        {
            if (char.IsControl(c)) { if (c is '\r' or '\n' or '\t') sb.Append(' '); continue; }
            sb.Append(c);
            if (sb.Length >= MaxTextLength) break;
        }
        return sb.ToString().Trim();
    }

    public static string CleanPageKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 64) return "";
        foreach (var c in key) if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return "";
        return key;
    }

    public static string Serialize(IEnumerable<Item> items)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteStartArray("items");
            foreach (var i in items)
            {
                w.WriteStartObject();
                w.WriteString("text", i.Text);
                w.WriteString("severity", i.Severity.ToString());
                w.WriteString("at", i.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                w.WriteString("page", i.PageKey);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The valid items, oldest first, limited to <see cref="MaxItems"/> and <see cref="MaxAgeDays"/> counted from <paramref name="now"/>.</summary>
    public static IReadOnlyList<Item> Parse(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes) return [];
        var result = new List<Item>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != Version
                || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
            var oldest = now - TimeSpan.FromDays(MaxAgeDays);
            var newest = now + TimeSpan.FromDays(1);
            var seen = 0;
            foreach (var e in items.EnumerateArray())
            {
                if (++seen > MaxItems * 2) break;
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (Str(e, "text") is not { } text || Str(e, "severity") is not { } sev || Str(e, "at") is not { } at) continue;
                if (!Enum.TryParse<NotificationSeverity>(sev, ignoreCase: false, out var severity) || !Enum.IsDefined(severity)) continue;
                if (!DateTimeOffset.TryParseExact(at, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) continue;
                var clean = CleanText(text);
                if (clean.Length == 0 || time < oldest || time > newest) continue;
                result.Add(new Item(severity, clean, CleanPageKey(Str(e, "page")), time));
            }
        }
        catch (JsonException) { return []; }
        return result.OrderBy(i => i.At).TakeLast(MaxItems).ToList();
    }

    private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
