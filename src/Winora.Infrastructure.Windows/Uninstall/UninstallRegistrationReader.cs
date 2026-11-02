// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using Winora.Application;
using Winora.Domain;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// Reads the uninstall registration of one desktop program from the key it was discovered from. It opens keys with read access only, accepts only a key under the Uninstall path of the three
/// known roots and refuses a key name with a path separator. Variables in the values are expanded with machine values only (<see cref="SystemVariables"/>), never with the user's own. The discovery adapter itself never reads the commands; this reader does, at assessment and removal time only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UninstallRegistrationReader : IUninstallRegistrations
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly IRegistryReader? registry;
    private readonly Func<string, string> expand;

    public UninstallRegistrationReader() : this(HostReaders.Registry()) { }
    internal UninstallRegistrationReader(IRegistryReader? registry, Func<string, string>? expand = null)
    {
        this.registry = registry;
        this.expand = expand ?? (static text => SystemVariables.Machine().Expand(text));
    }

    public UninstallRegistration? Read(InstalledApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (registry is null || !TryParse(app, out var hive, out var view, out var key)) return null;
        var v = registry.GetValues(hive, view, UninstallPath + "\\" + key);
        if (v is null) return null;
        string? Text(string name) => v.GetValueOrDefault(name)?.AsString is { Length: > 0 } s ? expand(s.Trim()) : null;
        var name = v.GetValueOrDefault("DisplayName")?.AsString?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        return new(key, name, Text("Publisher"), Text("InstallLocation"), Text("UninstallString"), Text("QuietUninstallString"),
            v.GetValueOrDefault("WindowsInstaller")?.AsNumber == 1, v.GetValueOrDefault("NoRemove")?.AsNumber == 1, v.GetValueOrDefault("SystemComponent")?.AsNumber == 1);
    }

    /// <summary>Reads "registry:HKLM\64\key" and checks it against the identity of the application.</summary>
    internal static bool TryParse(InstalledApplication app, out RegistryHiveKind hive, out RegistryViewKind view, out string key)
    {
        hive = default; view = default; key = "";
        if (app.Identity.Kind == InstallationKind.Msix) return false;
        const string prefix = "registry:";
        var p = app.RegistrationProvenance;
        if (!p.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = p[prefix.Length..].Split('\\', 3);
        if (parts.Length != 3 || parts[2].Length is 0 or > 255 || parts[2].Any(c => char.IsControl(c) || c is '\\' or '/')) return false;
        if (parts[0] == "HKLM") { hive = RegistryHiveKind.LocalMachine; if (app.Identity.Scope != InstallationScope.Machine) return false; }
        else if (parts[0] == "HKCU") { hive = RegistryHiveKind.CurrentUser; if (app.Identity.Scope != InstallationScope.User) return false; }
        else return false;
        if (parts[1] == "64") view = RegistryViewKind.Registry64;
        else if (parts[1] == "32") view = RegistryViewKind.Registry32;
        else return false;
        if (app.Identity.RegistryView != view) return false;
        key = parts[2];
        return true;
    }
}
