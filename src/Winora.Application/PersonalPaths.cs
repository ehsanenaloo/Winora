// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;

namespace Winora.Application;

/// <summary>
/// Removes the user's own folder from text that goes into a journal. Journals are kept and may be shared for support, and a profile path holds the account name. The profile folder
/// (in any case) and any <c>X:\Users\&lt;name&gt;</c> path become <c>&lt;home&gt;</c>; the rest of the path stays, so the line still says which kind of file it was.
/// </summary>
public static partial class PersonalPaths
{
    public const string Home = "<home>";

    [GeneratedRegex(@"[A-Za-z]:[\\/]+Users[\\/]+[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsersFolder();

    /// <summary>The profile folder of the account this process runs as, as Windows reports it.</summary>
    public static string? CurrentProfile() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } p ? p : null;

    public static string Redact(string? text, string? profileFolder)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = text;
        if (profileFolder?.TrimEnd('\\', '/') is { Length: >= 3 } profile)
        {
            result = result.Replace(profile, Home, StringComparison.OrdinalIgnoreCase);
            result = result.Replace(profile.Replace('\\', '/'), Home, StringComparison.OrdinalIgnoreCase);
        }
        return UsersFolder().Replace(result, Home);
    }
}
