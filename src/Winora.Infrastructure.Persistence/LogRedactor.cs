// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;
namespace Winora.Infrastructure.Persistence;

/// <summary>
/// Identity strings that must never appear in a log line or diagnostics archive. Injectable so tests can use fake tokens;
/// <see cref="FromEnvironment"/> reads the real values at the composition root.
/// </summary>
public sealed record RedactionContext(string? UserProfile, string? UserName, string? MachineName, string? UserDomain = null)
{
    public static RedactionContext FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName, Environment.UserDomainName);
}

/// <summary>
/// Replaces user-identifying text with fixed tokens: &lt;home&gt; (profile path, drive-letter user paths), &lt;user&gt;, &lt;machine&gt;,
/// &lt;unc&gt; (UNC paths), &lt;sid&gt; and &lt;email&gt;. It is idempotent, case-insensitive, handles forward/back/double/percent-encoded
/// separators, preserves all other text (including Persian), bounds output length and, if a pattern ever times out, returns a
/// fixed placeholder rather than the unredacted text.
/// </summary>
public sealed class LogRedactor
{
    public const int MaxLength = 4096;
    public const string Failed = "<redaction-failed>";
    private const string Sep = @"(?:\\{1,2}|/+|%5C|%2F)";
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(2000);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Sid = new(@"\bS-1-\d{1,3}(?:-\d{1,12}){1,15}\b", Options, Timeout);
    private static readonly Regex Email = new(@"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)+", Options, Timeout);
    // Drive-letter user paths: C:\Users\name, C:/Users/name, C:\\Users\\name, C%3A%5CUsers%5Cname, file:///C:/Users/name.
    private static readonly Regex UserPath = new(@"(?:file:/{2,3})?[A-Z](?::|%3A)" + Sep + "+Users" + Sep + @"+(?:[^\\/\s""'<>|%]|%(?!5C|2F))+", Options, Timeout);
    private static readonly Regex Unc = new(@"(?<![\\\p{L}\p{N}])\\{2,4}(?![?.]\\)[^\\/\s""'<>|]+(?:\\+[^\s""'<>|\\]*)*", Options, Timeout);

    private readonly (Regex Pattern, string Token)[] identity;

    public LogRedactor(RedactionContext context)
    {
        var list = new List<(Regex, string)>();
        foreach (var variant in PathVariants(context.UserProfile)) list.Add((Needle(Regex.Escape(variant)), "<home>"));
        foreach (var name in Names(context.UserName)) list.Add((NameNeedle(name), "<user>"));
        foreach (var name in Names(context.MachineName)) list.Add((NameNeedle(name), "<machine>"));
        foreach (var name in Names(context.UserDomain)) list.Add((NameNeedle(name), "<machine>"));
        // Longest needles first so a profile path is consumed whole before its user-name fragment.
        identity = [.. list.OrderByDescending(item => item.Item1.ToString().Length)];
    }

    private static Regex Needle(string escaped) => new(escaped, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private static IEnumerable<string> PathVariants(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Length < 3) yield break;
        var trimmed = profile.TrimEnd('\\', '/');
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            trimmed, trimmed.Replace('\\', '/'), trimmed.Replace("\\", "\\\\"), trimmed.Replace("\\", "%5C"), trimmed.Replace("\\", "%5C").Replace(":", "%3A"),
            trimmed.Replace('\\', '/').Replace("/", "%2F"), Uri.EscapeDataString(trimmed), Uri.EscapeDataString(trimmed.Replace('\\', '/')),
        };
        foreach (var form in forms.Where(f => f.Length >= 3).OrderByDescending(f => f.Length)) yield return form;
    }

    private static IEnumerable<string> Names(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2) yield break;
        var trimmed = name.Trim();
        yield return trimmed;
        var encoded = Uri.EscapeDataString(trimmed);
        if (!encoded.Equals(trimmed, StringComparison.Ordinal)) yield return encoded;
    }

    // Names of three or more characters match anywhere (a leaked prefix is still a leak); two-character names need token boundaries so ordinary words survive.
    private static Regex NameNeedle(string name) => name.Length >= 3
        ? Needle(Regex.Escape(name))
        : Needle(@"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])");

    public string Redact(string? text, int maxLength = MaxLength)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try
        {
            var value = text.Length > maxLength * 4 ? text[..(maxLength * 4)] : text;
            foreach (var (pattern, token) in identity) value = pattern.Replace(value, token);
            value = UserPath.Replace(value, "<home>");
            value = Unc.Replace(value, "<unc>");
            value = Email.Replace(value, "<email>");
            value = Sid.Replace(value, "<sid>");
            if (value.Length > maxLength) value = value[..maxLength] + "...[truncated]";
            return value;
        }
        catch (RegexMatchTimeoutException) { return Failed; }
    }

    /// <summary>Redacts a single line of a possibly large text without bounding the line count; each line is length-capped.</summary>
    public string RedactLines(string text, int maxLineLength = 16384) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(line => Redact(line, maxLineLength)));
}
