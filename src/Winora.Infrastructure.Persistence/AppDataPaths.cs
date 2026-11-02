// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Infrastructure.Persistence;

/// <summary>
/// The application-owned data locations: %LocalAppData%\Winora (journal, preferences) and its "logs" folder.
/// Tests and hosts may pass an explicit root; the default is the same root the journal databases already use.
/// </summary>
public sealed class AppDataPaths
{
    public const string LogsFolderName = "logs";
    public string Root { get; }
    public string LogsFolder { get; }

    public AppDataPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root", nameof(root));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        LogsFolder = Path.Combine(Root, LogsFolderName);
    }

    public static AppDataPaths Default() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winora"));

    /// <summary>True when <paramref name="fullPath"/> is the data root or lies beneath it (case-insensitive, separator-normalised).</summary>
    public bool IsInside(string fullPath)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        return candidate.Equals(Root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
