// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.RegularExpressions;
using Winora.Domain;
namespace Winora.Application;

/// <summary>The removed program the leftover scan looks for: its name, publisher and recorded install folder, as the registration showed them before the removal.</summary>
public sealed record LeftoverTarget(string Key, string DisplayName, string? Publisher, string? InstallLocation, InstallationScope Scope);

/// <summary>Where a leftover folder was found.</summary>
public enum LeftoverPlace { InstallFolder, ProgramData, RoamingAppData, LocalAppData }

/// <summary>One folder the scan offers: its full path, where it is, and what is in it.</summary>
public sealed record LeftoverFolder(string Path, LeftoverPlace Place, long Bytes, int Files);

/// <summary>A folder the scan looked at and did not offer, with the stable reason (<see cref="LeftoverFolderReason"/>).</summary>
public sealed record LeftoverSkip(string Path, string Reason);

public sealed record LeftoverFindings(LeftoverTarget Target, IReadOnlyList<LeftoverFolder> Folders, IReadOnlyList<LeftoverSkip> Skipped);

public static class LeftoverFolderReason
{
    public const string NameTooShort = "name_too_short";
    public const string GenericName = "generic_name";
    public const string SharedName = "shared_name";
    public const string ProtectedFolder = "protected_folder";
    public const string OtherProgram = "other_program";
    public const string OutsideKnownPlaces = "outside_known_places";
    public const string LinkInside = "link_inside";
    public const string TooManyEntries = "too_many_entries";
    public const string Unreadable = "unreadable";
}

/// <summary>What a read-only walk of a folder found. The walk never enters a link or junction; it reports one.</summary>
public sealed record FolderFacts(long Bytes, int Files, int Entries, bool HasLink, bool TooMany, bool Unreadable);

public enum FolderKind { Missing, Plain, Link, Unreadable }

/// <summary>Read-only access to folders for the leftover scan. Nothing here changes the disk.</summary>
public interface ILeftoverFolderReader
{
    /// <summary><see cref="FolderKind.Link"/> when the folder or any folder above it is a link or junction; <see cref="FolderKind.Plain"/> for a real folder.</summary>
    FolderKind Probe(string path);

    /// <summary>Walks the folder without following links, up to <paramref name="maxEntries"/> entries and <see cref="LeftoverFolderScanner.MaxDepth"/> levels.</summary>
    FolderFacts Inspect(string path, int maxEntries, CancellationToken cancellationToken);
}

/// <summary>
/// Finds the folders a removed program left behind, in a few fixed places only: its recorded install folder if it is still there, and the folders under
/// ProgramData and under the AppData of the signed-in user that carry the program name exactly, directly or inside a folder named for its publisher. It is conservative on purpose:
/// a name that is short, generic or shared with another installed program, a folder of another program, a system or shared folder, and any folder that holds a link or junction are
/// not offered. Names are compared whole, after removing version numbers and architecture tags; there is no fuzzy matching. The scan reads only; it moves nothing.
/// </summary>
public sealed partial class LeftoverFolderScanner(ILeftoverFolderReader reader, CleanupEnvironment environment)
{
    public const int MaxEntries = 20_000;
    public const int MaxDepth = 32;
    public const int MinNameLength = 3;

    /// <summary>Folder names that many programs share or that Windows owns. A program with one of these names gets no name match.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Microsoft Shared", "Microsoft.NET", "dotnet", "Packages", "Package Cache", "Temp", "Tmp", "Programs", "Program Files",
        "Application Data", "AppData", "Local", "LocalLow", "Roaming", "Desktop", "Documents", "Downloads", "Start Menu", "Startup", "Users", "Public", "Default", "System",
        "System32", "SysWOW64", "WindowsApps", "Installer", "Setup", "Update", "Updater", "Uninstall", "Cache", "Caches", "Logs", "Log", "Data", "Config", "Settings", "Tools",
        "Bin", "Lib", "Shared", "Common", "Runtime", "Runtimes", "Drivers", "Driver", "Fonts", "Help", "Plugins", "Plugin", "Crash Reports", "CrashDumps", "Diagnostics",
        "Google", "Mozilla", "Apple", "Adobe", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Oracle", "Java", "Python", "Steam", "Package", "Packages Cache", "Comms",
        "ConnectedDevicesPlatform", "D3DSCache", "PlaceholderTileLogoFolder", "Publishers", "IsolatedStorage", "VirtualStore", "History", "Sun", "Unity",
    };

    private string Roaming => Path.Combine(environment.UserProfile, "AppData", "Roaming");

    /// <summary>True when the folder is still there as a plain folder. An uninstaller often removes its own folder a moment after it exits, so a found folder can vanish before the move.</summary>
    public bool StillThere(string path) => reader.Probe(path) == FolderKind.Plain;

    public LeftoverFindings Find(LeftoverTarget target, IReadOnlyCollection<InstalledApplication> stillInstalled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var offered = new List<LeftoverFolder>();
        var skipped = new List<LeftoverSkip>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var others = stillInstalled.Where(a => !string.Equals(a.DisplayName, target.DisplayName, StringComparison.OrdinalIgnoreCase) || !string.Equals(a.Identity.Publisher, target.Publisher, StringComparison.OrdinalIgnoreCase)).ToList();
        var otherLocations = others.Select(a => a.InstallLocation).Where(Usable).Select(l => Normalize(l!)).ToList();

        void Consider(string path, LeftoverPlace place)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Normalize(path);
            if (!seen.Add(full)) return;
            switch (reader.Probe(full))
            {
                case FolderKind.Missing: return; // a folder that is not there is not worth a line
                case FolderKind.Link: skipped.Add(new(full, LeftoverFolderReason.LinkInside)); return;
                case FolderKind.Unreadable: skipped.Add(new(full, LeftoverFolderReason.Unreadable)); return;
            }
            if (IsProtected(full)) { skipped.Add(new(full, LeftoverFolderReason.ProtectedFolder)); return; }
            if (otherLocations.Any(o => Same(o, full) || Inside(o, full) || Inside(full, o))) { skipped.Add(new(full, LeftoverFolderReason.OtherProgram)); return; }
            var facts = reader.Inspect(full, MaxEntries, cancellationToken);
            if (facts.Unreadable) { skipped.Add(new(full, LeftoverFolderReason.Unreadable)); return; }
            if (facts.HasLink) { skipped.Add(new(full, LeftoverFolderReason.LinkInside)); return; }
            if (facts.TooMany) { skipped.Add(new(full, LeftoverFolderReason.TooManyEntries)); return; }
            offered.Add(new(full, place, facts.Bytes, facts.Files));
        }

        // 1. The install folder the registration recorded, when it still exists in a place Winora knows.
        if (Usable(target.InstallLocation))
        {
            var location = Normalize(target.InstallLocation!);
            if (!InKnownPlace(location)) skipped.Add(new(location, LeftoverFolderReason.OutsideKnownPlaces));
            else Consider(location, LeftoverPlace.InstallFolder);
        }

        // 2. Folders named exactly for the program, alone or under a folder named for the publisher.
        var names = NamesOf(target.DisplayName);
        if (names.Count == 0) skipped.Add(new(target.DisplayName, NameReason(target.DisplayName)));
        else if (others.Any(o => NamesOf(o.DisplayName).Overlaps(names))) skipped.Add(new(target.DisplayName, LeftoverFolderReason.SharedName));
        else
        {
            var publishers = PublishersOf(target.Publisher);
            // Programs installed for the whole machine keep settings in the AppData of each user too, so both scopes look in all three places.
            (string Root, LeftoverPlace Place)[] places = [(environment.ProgramData, LeftoverPlace.ProgramData), (Roaming, LeftoverPlace.RoamingAppData), (environment.LocalAppData, LeftoverPlace.LocalAppData)];
            foreach (var (root, place) in places)
            {
                if (!Usable(root) || reader.Probe(root) != FolderKind.Plain) continue;
                foreach (var name in names)
                {
                    Consider(Path.Combine(root, name), place);
                    foreach (var publisher in publishers.Where(p => !names.Contains(p))) Consider(Path.Combine(root, publisher, name), place);
                }
            }
        }

        // An empty folder is still offered: it is a leftover too.
        return new(target, offered, skipped);
    }

    // ---- names ----

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();

    [GeneratedRegex(@"(\s+|-|_)(v(ersion)?\s*)?\d+([.\-_]\d+)*[a-z]?\b.*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"\s+(x64|x86|amd64|arm64|64-bit|32-bit|64 bit|32 bit|win64|win32)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Architecture();

    [GeneratedRegex(@"[,.]?\s+(inc|incorporated|llc|ltd|limited|corp|corporation|co|company|gmbh|ag|sa|srl|bv|b\.v|s\.a|pty|plc|oy|ab|as|kg)\.?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();

    /// <summary>The folder names a program name maps to: the name itself and the name without version, architecture and bracketed parts. Unusable names give an empty set.</summary>
    public static IReadOnlySet<string> NamesOf(string? displayName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(displayName)) return result;
        var raw = displayName.Trim();
        var bare = Architecture().Replace(Brackets().Replace(raw, " "), "").Trim();
        bare = TrailingVersion().Replace(bare, "").Trim(' ', '-', '_', '.');
        foreach (var candidate in new[] { raw, bare })
            if (Valid(candidate)) result.Add(candidate);
        return result;
    }

    private static string NameReason(string? displayName)
    {
        var bare = (displayName ?? "").Trim();
        return bare.Length < MinNameLength ? LeftoverFolderReason.NameTooShort : LeftoverFolderReason.GenericName;
    }

    /// <summary>The publisher folder names: the publisher and the publisher without a company suffix ("Contoso, Inc." gives "Contoso, Inc." and "Contoso").</summary>
    public static IReadOnlySet<string> PublishersOf(string? publisher)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(publisher)) return result;
        var raw = publisher.Trim();
        var bare = raw;
        for (var i = 0; i < 2; i++) bare = CompanySuffix().Replace(bare, "").Trim(' ', ',', '.');
        foreach (var candidate in new[] { raw, bare })
            if (Valid(candidate)) result.Add(candidate);
        return result;
    }

    private static bool Valid(string name) =>
        name.Length >= MinNameLength && name.Length <= 120 && !Generic.Contains(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && name.Any(char.IsLetter)
        && !name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase);

    // ---- places ----

    private bool InKnownPlace(string path) =>
        environment.ProgramFiles.Any(p => Inside(path, Normalize(p))) || Inside(path, Normalize(environment.ProgramData)) || Inside(path, Normalize(environment.UserProfile));

    /// <summary>The roots themselves, the Windows folder, and folders that hold shared parts of many programs or of the user.</summary>
    private bool IsProtected(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || Path.GetPathRoot(path) is not { } root || Same(parent, root)) return true;
        if (Usable(environment.WindowsFolder) && (Same(path, environment.WindowsFolder) || Inside(path, Normalize(environment.WindowsFolder)))) return true;
        string[] roots = [.. environment.ProgramFiles, environment.ProgramData, environment.UserProfile, environment.LocalAppData, Roaming, Path.Combine(environment.UserProfile, "AppData"),
            Path.Combine(environment.UserProfile, "AppData", "LocalLow"), Path.Combine(environment.LocalAppData, "Programs")];
        if (roots.Where(Usable).Any(r => Same(path, r))) return true;
        if (Inside(path, Normalize(environment.UserProfile)) && !Inside(path, Normalize(Path.Combine(environment.UserProfile, "AppData")))) return true;
        if (Usable(environment.TempFolder) && (Same(path, environment.TempFolder) || Inside(path, Normalize(environment.TempFolder)))) return true;
        var segments = path.Split('\\');
        if (segments.Any(s => s.Equals("Common Files", StringComparison.OrdinalIgnoreCase) || s.Equals("Microsoft Shared", StringComparison.OrdinalIgnoreCase) || s.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Package Cache", StringComparison.OrdinalIgnoreCase) || s.Equals("Packages", StringComparison.OrdinalIgnoreCase))) return true;
        // A folder whose own name is generic (for example a publisher folder holding several products) is never offered.
        return Generic.Contains(Path.GetFileName(path)) || (Path.GetFileName(parent) is { } up && (up.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) || up.Equals("Windows", StringComparison.OrdinalIgnoreCase)) && !environment.ProgramFiles.Any(p => Same(parent, p)));
    }

    private static bool Usable(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length < 1024 && !path.StartsWith(@"\\", StringComparison.Ordinal) && Path.IsPathFullyQualified(path.Trim().Trim('"'));

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));

    private static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="folder"/>, compared by whole segments.</summary>
    private static bool Inside(string path, string folder) =>
        path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] == '\\';
}
