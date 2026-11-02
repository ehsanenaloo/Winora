// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// The environment of a program the elevated app starts (repair tools, uninstallers). The program does not inherit the app's own environment, which holds the user's variables:
/// a user can point TEMP at a folder of their own, and installers and dism run code they unpack there. Instead it gets a fixed set of machine values, a fixed search path inside the
/// Windows folder, the profile folders of the token, and TEMP and TMP in a new folder that only administrators can change.
/// </summary>
internal static class ChildEnvironment
{
    private static readonly string[] Copied =
    [
        "SystemRoot", "windir", "SystemDrive", "PATHEXT", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)",
        "CommonProgramFiles", "CommonProgramW6432", "CommonProgramFiles(x86)", "ProgramData", "ALLUSERSPROFILE", "PUBLIC",
    ];

    /// <summary>The variables for one child. <paramref name="tempFolder"/> must be a fresh folder that only administrators can change.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyDictionary<string, string> Build(SystemVariables system, string tempFolder, string? profileFolder)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempFolder);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Copied)
            if (system.Get(name) is { Length: > 0 } value) result[name] = value;
        if (system.Get("SystemRoot") is { Length: > 0 } windows)
        {
            var tools = Path.Combine(windows, "System32");
            result["Path"] = string.Join(';', tools, windows, Path.Combine(tools, "Wbem"));
        }
        result["TEMP"] = tempFolder;
        result["TMP"] = tempFolder;
        if (profileFolder is { Length: > 0 } profile && Path.IsPathFullyQualified(profile))
        {
            result["USERPROFILE"] = profile;
            result["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
            result["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
        }
        return result;
    }

    /// <summary>Replaces every variable of <paramref name="target"/> (the environment of the start information) with <paramref name="environment"/>.</summary>
    public static void Apply(IDictionary<string, string?> target, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(environment);
        target.Clear();
        foreach (var (name, value) in environment) target[name] = value;
    }

    /// <summary>
    /// The production environment: a new folder <c>%ProgramData%\Winora\temp\&lt;id&gt;</c> created with a protected ACL, the machine values and the profile folder of the token.
    /// Null when the folder cannot be created safely; the caller then starts nothing.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyDictionary<string, string>? ForChild()
    {
        try
        {
            var temp = AdminOnlyFolder.Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Winora", "temp", Guid.NewGuid().ToString("N")));
            return Build(SystemVariables.Machine(), temp, AdminOnlyFolder.ProfileFolderOfToken());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
