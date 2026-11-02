// SPDX-License-Identifier: GPL-3.0-only
using System.Text;

namespace Winora.Application;

/// <summary>
/// Readable text for app packages. A package's publisher is the subject of its signing certificate and a package without a resolved display name only has
/// its package name; neither is meant for people. Display only: identities and searches keep the raw values.
/// </summary>
public static class PackageText
{
    /// <summary>The organisation ("O=") of a certificate subject, else its common name ("CN="); a value that is not a subject is returned as it is.</summary>
    public static string? Organisation(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var parts = SplitSubject(subject);
        if (parts.Count == 0 || parts.All(p => p.Key.Length == 0)) return subject.Trim();
        var organisation = parts.FirstOrDefault(p => p.Key.Equals("O", StringComparison.OrdinalIgnoreCase)).Value;
        var common = parts.FirstOrDefault(p => p.Key.Equals("CN", StringComparison.OrdinalIgnoreCase)).Value;
        var chosen = !string.IsNullOrWhiteSpace(organisation) ? organisation : common;
        return string.IsNullOrWhiteSpace(chosen) ? subject.Trim() : chosen;
    }

    /// <summary>
    /// A readable name from a package name: the vendor prefix is dropped and words are split ("Microsoft.BingNews" is "Bing News",
    /// "Microsoft.WindowsAppRuntime.2" is "Windows App Runtime 2"). A GUID stays as it is.
    /// </summary>
    public static string FriendlyName(string packageName)
    {
        var name = packageName.Trim();
        if (name.Length == 0 || Guid.TryParse(name, out _)) return name;
        var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var words = segments.Length > 1 ? segments.Skip(1) : segments;
        var text = string.Join(' ', words.Select(SplitWords)).Trim();
        return text.Length == 0 ? name : text;
    }

    /// <summary>True when the name is a GUID, which some system packages use as their only name.</summary>
    public static bool IsGuidName(string? name) => name is not null && Guid.TryParse(name.Trim(), out _);

    private static string SplitWords(string segment)
    {
        var builder = new StringBuilder(segment.Length + 4);
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(segment[i - 1]) || i + 1 < segment.Length && char.IsUpper(segment[i - 1]) && char.IsLower(segment[i + 1]))) builder.Append(' ');
            builder.Append(c == '_' || c == '-' ? ' ' : c);
        }
        return builder.ToString();
    }

    /// <summary>Splits "CN=A, O=\"B, Inc.\", C=US" into key and value pairs; quoted values may hold commas.</summary>
    private static List<KeyValuePair<string, string>> SplitSubject(string subject)
    {
        var result = new List<KeyValuePair<string, string>>();
        var current = new StringBuilder();
        var quoted = false;
        void Flush()
        {
            var part = current.ToString().Trim();
            current.Clear();
            if (part.Length == 0) return;
            var eq = part.IndexOf('=');
            result.Add(eq <= 0 ? new("", part) : new(part[..eq].Trim(), part[(eq + 1)..].Trim().Trim('"')));
        }
        foreach (var c in subject)
        {
            if (c == '"') { quoted = !quoted; current.Append(c); }
            else if (c == ',' && !quoted) Flush();
            else current.Append(c);
        }
        Flush();
        return result;
    }
}
