// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Cleanup;

/// <summary>
/// Append-only JSON Lines record of cleanup runs in the user's own application data folder. A "begin" line is written and flushed to disk before the first removal of a run; an "end"
/// line follows the run. A begin line without an end line is a run that was interrupted. Nothing is ever rewritten or removed, and nothing leaves the machine.
/// </summary>
public sealed class FileCleanupJournal : ICleanupJournal
{
    private readonly Func<string> pathProvider;

    public FileCleanupJournal(string path) : this(() => path) { }

    /// <summary>The path is asked for on every use, so a folder that stops being trustworthy is refused each time (see <see cref="AdminOnlyFolder"/>).</summary>
    public FileCleanupJournal(Func<string> pathProvider) { this.pathProvider = pathProvider; }

    private string path => pathProvider();

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };
    private readonly object gate = new();

    private sealed record Line(string Type, CleanupRunIntent? Intent, CleanupRunResult? Result);

    public void Begin(CleanupRunIntent intent) => Append(new("begin", intent, null));

    public void Complete(CleanupRunResult result) => Append(new("end", null, result));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(path, bytes); }
    }

    public IReadOnlyList<CleanupRunRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, CleanupRunIntent>();
        var ends = new Dictionary<Guid, CleanupRunResult>();
        var order = new List<Guid>();
        lock (gate)
        {
            string file;
            try { file = pathProvider(); } catch (IOException) { return []; }
            if (!File.Exists(file)) return [];
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } text)
                {
                    if (text.Length == 0 || text.Length > 1_000_000) continue;
                    Line? line;
                    try { line = JsonSerializer.Deserialize<Line>(text, Json); }
                    catch (JsonException) { continue; } // a damaged line is skipped, the rest of the history stays readable
                    if (line is { Type: "begin", Intent: { } intent } && begins.TryAdd(intent.RunId, intent)) order.Add(intent.RunId);
                    else if (line is { Type: "end", Result: { } result }) ends[result.RunId] = result;
                }
            }
            catch (IOException) { return []; }
        }
        order.Reverse();
        return order.Take(max).Select(id => new CleanupRunRecord(begins[id], ends.GetValueOrDefault(id))).ToList();
    }
}

/// <summary>
/// The folders the catalog works on. Without administrator rights they come from Windows as usual. With them, TEMP, TMP and the user's own shell folder settings are NOT trusted (the user can
/// change them in the registry and the environment): the profile folder comes from the token (the machine's profile list) and the local data and temp folders are derived from it.
/// </summary>
public static class WindowsCleanupEnvironment
{
    public static CleanupEnvironment Create(bool? elevated = null)
    {
        var admin = elevated ?? ProcessElevationState.Current;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var temp = Path.GetTempPath().TrimEnd('\\');
        if (admin && OperatingSystem.IsWindows() && AdminOnlyFolder.ProfileFolderOfToken() is { Length: > 0 } tokenProfile)
        {
            profile = tokenProfile;
            local = Path.Combine(profile, "AppData", "Local");
            temp = Path.Combine(local, "Temp");
        }
        return new(
            LongPath(temp),
            LongPath(local),
            LongPath(profile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            admin,
            BrowserCacheFolders(LongPath(local)));
    }

    /// <summary>The long form of a path (8.3 short names expanded), so that it equals the final path Windows reports for an opened handle. A path that does not exist is returned as it is.</summary>
    public static string LongPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsWindows()) return path;
        var builder = new System.Text.StringBuilder(1024);
        var length = GetLongPathName(path, builder, (uint)builder.Capacity);
        return length > 0 && length < builder.Capacity ? builder.ToString() : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint length);

    /// <summary>
    /// The cache folders of Chromium browsers (Edge, Chrome, Brave: Cache, Code Cache and GPUCache of the Default and numbered profiles) and the cache2 folder of each Firefox profile.
    /// Cookies, history, passwords and extensions live in other folders and are never listed. Folders that are links are left out.
    /// </summary>
    public static IReadOnlyList<string> BrowserCacheFolders(string localAppData)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(localAppData)) return result;
        foreach (var userData in new[] { Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), Path.Combine(localAppData, "Google", "Chrome", "User Data"), Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data") })
            foreach (var profile in Subfolders(userData).Where(p => Path.GetFileName(p) is { } n && (n.Equals("Default", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))))
                foreach (var name in new[] { "Cache", "Code Cache", "GPUCache" })
                    if (Path.Combine(profile, name) is var folder && IsPlainFolder(folder)) result.Add(folder);
        foreach (var profile in Subfolders(Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles")))
            if (Path.Combine(profile, "cache2") is var folder && IsPlainFolder(folder)) result.Add(folder);
        return result;
    }

    private static IEnumerable<string> Subfolders(string folder)
    {
        try { return IsPlainFolder(folder) ? Directory.EnumerateDirectories(folder).Where(IsPlainFolder).ToList() : []; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return []; }
    }

    private static bool IsPlainFolder(string path)
    {
        try { var info = new DirectoryInfo(path); return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or ArgumentException) { return false; }
    }
}
