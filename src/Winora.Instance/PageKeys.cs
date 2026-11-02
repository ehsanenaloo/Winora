// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Instance;

/// <summary>The pages a jump list entry or a second start may open. Anything else is refused.</summary>
public static class PageKeys
{
    /// <summary>The longest key a message may carry.</summary>
    public const int MaxLength = 32;

    /// <summary>Fixed list, in the order of the jump list. Each key is the key of a navigation destination of the Desktop app.</summary>
    public static IReadOnlyList<string> Allowed { get; } = ["Overview", "Customize", "Cleanup", "Startup", "Applications", "History"];

    public static bool IsAllowed(string? key) => key is not null && Allowed.Contains(key, StringComparer.Ordinal);

    /// <summary>True for 1 to <see cref="MaxLength"/> ASCII letters. A well formed key can be logged and passed on a command line without quoting.</summary>
    public static bool IsWellFormed(string? key)
    {
        if (key is null || key.Length is 0 or > MaxLength) return false;
        foreach (var c in key) if (!char.IsAsciiLetter(c)) return false;
        return true;
    }
}
