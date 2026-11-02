// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Infrastructure.Windows;

/// <summary>Text-only helpers for startup commands. Nothing here runs a command or touches a file.</summary>
internal static class StartupCommand
{
    private static readonly string[] ProgramExtensions = [".exe", ".com", ".cmd", ".bat"];

    /// <summary>
    /// The program part of a command line: the quoted text, the text up to the first program extension that ends a word, or the first word. An extension inside a name or followed by more of
    /// the path (<c>my.executor\app.exe</c>, <c>tool.exe\..\x.exe</c>) does not end the program.
    /// </summary>
    internal static string? ExtractExecutable(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var cut = -1;
        foreach (var ext in ProgramExtensions)
        {
            for (var i = text.IndexOf(ext, StringComparison.OrdinalIgnoreCase); i > 0; i = text.IndexOf(ext, i + 1, StringComparison.OrdinalIgnoreCase))
            {
                var after = i + ext.Length;
                if (after < text.Length && !char.IsWhiteSpace(text[after])) continue;
                if (cut < 0 || after < cut) cut = after;
                break;
            }
        }
        if (cut > 0) return text[..cut];
        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>The text after the program part of a command (see <see cref="ExtractExecutable"/>), trimmed.</summary>
    internal static string Arguments(string command)
    {
        var text = command.Trim();
        if (text.Length > 0 && text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[(end + 1)..].Trim() : "";
        }
        var program = ExtractExecutable(text);
        return program is null ? "" : text[program.Length..].Trim();
    }
}
