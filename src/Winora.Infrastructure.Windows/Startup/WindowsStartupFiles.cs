// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.RegularExpressions;
using Winora.Application;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// File facts for the startup page: whether the program a command points to exists, its size and last write time, and whether it is one of the files of the Windows image. It reads
/// file metadata only and never runs the file; the signature check is a separate step. Network paths, device paths and alternate data streams are not looked at. A command counts as part of
/// the Windows image only when its program is in the image and no argument names a path, a network path or an address outside it. Results are kept for a short time and the cache has a size cap.
/// </summary>
public sealed partial class WindowsStartupFiles : IStartupFiles
{
    private const int MaxCached = 1000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private static readonly string[] SystemSubFolders = ["SystemApps", "ImmersiveControlPanel"];

    private readonly string windowsFolder;
    private readonly string systemFolder;
    private readonly Func<string, string> expand;
    private readonly Func<long> ticks;
    private readonly Dictionary<string, (StartupFileFacts Facts, long At)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    // A drive path, a network path or an address inside the arguments of a command.
    [GeneratedRegex(@"[A-Za-z]:[\\/][^\s""',;|<>]*|\\\\[^\s""',;|<>]+|[A-Za-z][A-Za-z0-9+.\-]*://[^\s""']*", RegexOptions.CultureInvariant)]
    private static partial Regex PathInArguments();

    public WindowsStartupFiles() : this(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.System), Environment.ExpandEnvironmentVariables, static () => Stopwatch.GetTimestamp()) { }

    internal WindowsStartupFiles(string windowsFolder, string systemFolder, Func<string, string> expand, Func<long> ticks)
    {
        this.windowsFolder = windowsFolder; this.systemFolder = systemFolder; this.expand = expand; this.ticks = ticks;
    }

    public StartupFileFacts Inspect(string command, bool wow64)
    {
        if (string.IsNullOrWhiteSpace(command)) return StartupFileFacts.Unknown;
        var key = (wow64 ? "32|" : "64|") + command;
        lock (gate)
        {
            var now = ticks();
            if (cache.TryGetValue(key, out var hit) && (now - hit.At) < Lifetime.TotalSeconds * Stopwatch.Frequency) return hit.Facts;
            if (cache.Count >= MaxCached) cache.Clear();
            var facts = Read(command, wow64);
            cache[key] = (facts, now);
            return facts;
        }
    }

    private StartupFileFacts Read(string command, bool wow64)
    {
        try
        {
            var path = Resolve(command, wow64);
            if (path is null) return StartupFileFacts.Unknown;
            var info = new FileInfo(path);
            // Windows adds ".exe" to a program name that has no extension.
            if (!info.Exists && info.Extension.Length == 0) { var withExe = new FileInfo(path + ".exe"); if (withExe.Exists) { info = withExe; path = withExe.FullName; } }
            if (!info.Exists) return new(path, false, null, null, false);
            return new(path, true, info.Length, info.LastWriteTimeUtc.Ticks, InSystemImage(path) && !ArgumentsLeaveWindows(command));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return StartupFileFacts.Unknown;
        }
    }

    // A full local path, after the variables of the command are expanded. A bare file name is looked up in the Windows system folder only.
    internal string? Resolve(string command, bool wow64)
    {
        var program = StartupCommand.ExtractExecutable(command);
        if (string.IsNullOrWhiteSpace(program)) return null;
        if (wow64) program = program.Replace("%ProgramFiles%", "%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase).Replace("%CommonProgramFiles%", "%CommonProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase);
        string text;
        try { text = expand(program); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
        if (text.Contains('%') || text.Any(char.IsControl) || text.Length > 1024) return null;
        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal)) return null;
        if (!Path.IsPathRooted(text))
        {
            if (text.AsSpan().IndexOfAny('\\', '/') >= 0) return null;
            if (Path.GetExtension(text).Length == 0) text += ".exe";
            text = Path.Combine(systemFolder, text);
        }
        if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] is not ('\\' or '/')) return null;
        if (text.IndexOf(':', 2) >= 0) return null; // a later colon names an alternate data stream
        return Path.GetFullPath(text);
    }

    /// <summary>True when an argument names a network path, an address, or a drive path that is not a file or folder of the Windows image.</summary>
    internal bool ArgumentsLeaveWindows(string command)
    {
        string text;
        try { text = expand(StartupCommand.Arguments(command)); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return true; }
        foreach (Match match in PathInArguments().Matches(text))
        {
            var value = match.Value.TrimEnd('\\', '/', '.');
            if (value.Length < 3 || value[1] != ':' || value.IndexOf(':', 2) >= 0) return true;
            string full;
            try { full = Path.GetFullPath(value); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
            if (!InSystemImage(full) && !IsImageFolder(full)) return true;
        }
        return false;
    }

    private bool IsImageFolder(string folder)
    {
        var f = Path.TrimEndingDirectorySeparator(folder);
        return string.Equals(f, Path.TrimEndingDirectorySeparator(windowsFolder), StringComparison.OrdinalIgnoreCase) || string.Equals(f, Path.TrimEndingDirectorySeparator(systemFolder), StringComparison.OrdinalIgnoreCase)
            || string.Equals(f, Path.Combine(windowsFolder, "SysWOW64"), StringComparison.OrdinalIgnoreCase);
    }

    // Files that come with Windows and that a standard user cannot change: directly in the Windows folder or in System32 and SysWOW64, or below SystemApps and ImmersiveControlPanel.
    // Other folders below the Windows folder (Temp, Tasks, Debug and similar) can be written by users and are not part of this.
    private bool InSystemImage(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null) return false;
        string Trim(string p) => Path.TrimEndingDirectorySeparator(p);
        if (string.Equals(folder, Trim(windowsFolder), StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(folder, Trim(systemFolder), StringComparison.OrdinalIgnoreCase)) return true;
        var wow = Path.Combine(windowsFolder, "SysWOW64");
        if (string.Equals(folder, wow, StringComparison.OrdinalIgnoreCase)) return true;
        return SystemSubFolders.Any(sub =>
        {
            var root = Path.Combine(windowsFolder, sub);
            return folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }
}
