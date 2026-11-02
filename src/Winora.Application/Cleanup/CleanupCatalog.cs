// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>The folders the catalog needs, as the host reports them. Everything else is derived, so the catalog stays testable on any machine.</summary>
public sealed record CleanupEnvironment(string TempFolder, string LocalAppData, string UserProfile, string WindowsFolder, IReadOnlyList<string> ProgramFiles, string ProgramData,
    bool IsElevated = false, IReadOnlyList<string>? BrowserCacheFolders = null);

/// <summary>
/// Decides whether a folder may be the root of an automatic cleanup. The check is about the folder only: it must be a real folder deep enough not to be a drive, and it must not be,
/// contain or sit inside a system or program folder. A temp folder that the user moved elsewhere is accepted only when it is named like one.
/// </summary>
public static class CleanupRootGuard
{
    public static bool IsSafe(string? root, CleanupEnvironment env, bool mustBeInsideLocalAppData)
    {
        // With administrator rights a root is accepted only inside the local application data folder: a temp-named folder elsewhere could be steered by the environment.
        if (env.IsElevated) mustBeInsideLocalAppData = true;
        if (string.IsNullOrWhiteSpace(root) || root.Length > 1024 || root.Any(char.IsControl) || !Path.IsPathFullyQualified(root)) return false;
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal)) return false;
        if (root.Split('\\', '/').Any(s => s is ".." or ".")) return false;
        var full = Normalize(root);
        var driveRoot = Path.GetPathRoot(full)?.TrimEnd('\\') ?? "";
        if (full.Equals(driveRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var depth = full[driveRoot.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        var leaf = Path.GetFileName(full);
        var tempNamed = leaf.Equals("temp", StringComparison.OrdinalIgnoreCase) || leaf.Equals("tmp", StringComparison.OrdinalIgnoreCase);
        if (depth < 1 || (depth < 2 && !tempNamed)) return false;

        var protectedFolders = new List<string> { env.WindowsFolder, env.ProgramData, env.UserProfile, env.LocalAppData };
        protectedFolders.AddRange(env.ProgramFiles);
        var usersRoot = Path.GetDirectoryName(Normalize(env.UserProfile));
        if (usersRoot is not null) protectedFolders.Add(usersRoot);
        foreach (var folder in protectedFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Normalize))
        {
            if (full.Equals(folder, StringComparison.OrdinalIgnoreCase)) return false;
            if (folder.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return false; // the root would contain a protected folder
        }
        foreach (var folder in new[] { env.WindowsFolder }.Concat(env.ProgramFiles).Where(f => !string.IsNullOrWhiteSpace(f)).Select(Normalize))
            if (full.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) return false;

        var local = Normalize(env.LocalAppData);
        if (full.StartsWith(local + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (mustBeInsideLocalAppData) return false;
        // A user's own folders (Documents, Desktop, Downloads) can have a folder called Temp; only the local application data folder is a place for caches.
        if (full.StartsWith(Normalize(env.UserProfile) + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        return tempNamed;
    }

    public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}

/// <summary>
/// The cleanable categories (decision DEC-IW-020). The folders follow the Windows Disk Cleanup handlers for temporary files, thumbnails, temporary internet files, the DirectX
/// shader cache and error reports, and the default crash dump folder of Windows Error Reporting. Roots that fail <see cref="CleanupRootGuard"/> are left out.
/// </summary>
public sealed class CleanupCatalog(CleanupEnvironment env) : ICleanupCatalog
{
    /// <summary>The default age: Disk Cleanup handles temporary files that were not used for 7 days (value LastAccess of its Temporary Files handler).</summary>
    public const int DefaultMinAgeDays = 7;

    public IReadOnlyList<CleanupCategory> Categories { get; } = Build(env);

    private static List<CleanupCategory> Build(CleanupEnvironment env)
    {
        var local = env.LocalAppData;
        string L(params string[] parts) => Path.Combine(new[] { local }.Concat(parts).ToArray());
        string W(params string[] parts) => Path.Combine(new[] { env.WindowsFolder }.Concat(parts).ToArray());
        string D(params string[] parts) => Path.Combine(new[] { env.ProgramData }.Concat(parts).ToArray());
        CleanupLocation Location(string root, bool recurse, params string[] patterns) => new(root, recurse, patterns.Length == 0 ? ["*"] : patterns);

        var result = new List<CleanupCategory>();
        void Add(CleanupCategoryId id, IEnumerable<CleanupLocation> locations, int age, bool folders, string basis, bool insideLocal)
        {
            var safe = locations.Where(l => CleanupRootGuard.IsSafe(l.Root, env, insideLocal)).ToList();
            if (safe.Count > 0) result.Add(new(id, safe, age, folders, basis));
        }

        // With administrator rights the temp folder must be exactly <local application data>\\Temp: the caller derived both from the token, never from TEMP.
        if (!env.IsElevated || string.Equals(CleanupRootGuard.Normalize(env.TempFolder), CleanupRootGuard.Normalize(L("Temp")), StringComparison.OrdinalIgnoreCase))
        Add(CleanupCategoryId.UserTemp, [Location(env.TempFolder, true)], DefaultMinAgeDays, true, "Windows Disk Cleanup handler: Temporary Files (user temp folder, files not used for 7 days)", false);
        Add(CleanupCategoryId.ThumbnailCache, [Location(L("Microsoft", "Windows", "Explorer"), false, "thumbcache_*.db", "iconcache_*.db")], 0, false, "Windows Disk Cleanup handler: Thumbnail Cache", true);
        Add(CleanupCategoryId.InternetCache, [Location(L("Microsoft", "Windows", "INetCache"), true)], DefaultMinAgeDays, true, "Windows Disk Cleanup handler: Internet Cache Files", true);
        Add(CleanupCategoryId.ShaderCache, [Location(L("D3DSCache"), true)], 0, true, "Windows Disk Cleanup handler: D3D Shader Cache", true);
        Add(CleanupCategoryId.ErrorReports, [Location(L("Microsoft", "Windows", "WER", "ReportArchive"), true), Location(L("Microsoft", "Windows", "WER", "ReportQueue"), true)], DefaultMinAgeDays, true,
            "Windows Disk Cleanup handler: Windows Error Reporting Files (the per-user store)", true);
        Add(CleanupCategoryId.CrashDumps, [Location(L("CrashDumps"), false, "*.dmp")], DefaultMinAgeDays, false, "Default folder of Windows Error Reporting local dumps", true);
        Add(CleanupCategoryId.BrowserCaches, (env.BrowserCacheFolders ?? []).Select(f => Location(f, true)), 0, true,
            "Cache folders of Edge, Chrome, Brave (Chromium user data folder) and Firefox (profile cache2) in the user's local application data; cookies, history and passwords are not in these folders", true);
        result.Add(new(CleanupCategoryId.RecycleBin, [], 0, false, "The Recycle Bin of the signed-in user (Windows Disk Cleanup handler: Recycle Bin)", CleanupCategoryKind.RecycleBin));

        if (env.IsElevated)
        {
            // System folders are never accepted by the generic root guard. Each one below is an exact, fixed location, and only when the process has administrator rights.
            void AddSystem(CleanupCategoryId id, IEnumerable<CleanupLocation> locations, int age, bool folders, string basis)
            {
                var exact = locations.Where(l => IsKnownSystemLocation(l, env)).ToList();
                if (exact.Count > 0) result.Add(new(id, exact, age, folders, basis, CleanupCategoryKind.Files, RequiresAdministrator: true));
            }
            AddSystem(CleanupCategoryId.WindowsTemp, [Location(W("Temp"), true), Location(W("SystemTemp"), true)], DefaultMinAgeDays, true, "Windows Disk Cleanup handler: Temporary Files (Windows and system temp folders, files not used for 7 days)");
            AddSystem(CleanupCategoryId.UpdateCache, [Location(W("SoftwareDistribution", "Download"), true)], DefaultMinAgeDays, true, "Windows Update download cache; files that the update service has open are skipped");
            AddSystem(CleanupCategoryId.DeliveryOptimization, [Location(W("ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"), true)], DefaultMinAgeDays, true,
                "Windows Disk Cleanup handler: Delivery Optimization Files");
            AddSystem(CleanupCategoryId.SystemDumps, [Location(W("Minidump"), false, "*.dmp"), Location(env.WindowsFolder, false, "MEMORY.DMP")], DefaultMinAgeDays, false,
                "Windows Disk Cleanup handlers: System error minidump files and System error memory dump files");
            AddSystem(CleanupCategoryId.SystemErrorReports, [Location(D("Microsoft", "Windows", "WER", "ReportArchive"), true), Location(D("Microsoft", "Windows", "WER", "ReportQueue"), true)], DefaultMinAgeDays, true,
                "Windows Disk Cleanup handler: Windows Error Reporting Files (the machine-wide store)");
        }
        return result;
    }

    /// <summary>
    /// Accepts only the fixed system locations of the catalog, compared exactly (root, depth and file patterns). This is how a system folder can be a cleanup root at all: the generic
    /// guard refuses every folder inside the Windows folder, and the Windows folder itself is accepted only for the single file name MEMORY.DMP, never recursively.
    /// </summary>
    public static bool IsKnownSystemLocation(CleanupLocation location, CleanupEnvironment env)
    {
        if (!env.IsElevated || string.IsNullOrWhiteSpace(env.WindowsFolder) || string.IsNullOrWhiteSpace(location.Root) || !Path.IsPathFullyQualified(location.Root)) return false;
        if (location.Root.Split('\\', '/').Any(p => p is ".." or ".")) return false;
        var root = CleanupRootGuard.Normalize(location.Root);
        var windows = CleanupRootGuard.Normalize(env.WindowsFolder);
        var data = string.IsNullOrWhiteSpace(env.ProgramData) ? null : CleanupRootGuard.Normalize(env.ProgramData);
        bool Is(string path, bool recurse, params string[] patterns) =>
            string.Equals(root, CleanupRootGuard.Normalize(path), StringComparison.OrdinalIgnoreCase) && location.Recurse == recurse && location.Patterns.SequenceEqual(patterns, StringComparer.OrdinalIgnoreCase);
        if (Is(Path.Combine(windows, "Temp"), true, "*") || Is(Path.Combine(windows, "SystemTemp"), true, "*")) return true;
        if (Is(Path.Combine(windows, "SoftwareDistribution", "Download"), true, "*")) return true;
        if (Is(Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"), true, "*")) return true;
        if (Is(Path.Combine(windows, "Minidump"), false, "*.dmp") || Is(windows, false, "MEMORY.DMP")) return true;
        return data is not null && (Is(Path.Combine(data, "Microsoft", "Windows", "WER", "ReportArchive"), true, "*") || Is(Path.Combine(data, "Microsoft", "Windows", "WER", "ReportQueue"), true, "*"));
    }
}
