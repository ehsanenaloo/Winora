// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using System.Text;
using Winora.Domain;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// Environment variables of the machine only. The elevated app inherits the user's own variables (HKCU\Environment, for example TEMP or a new ProgramFiles), and a program of the
/// user can change those. Text that the elevated app runs or trusts is therefore expanded with these values only: the Windows folders that Windows reports, and the system
/// variables read from <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment</c>. A name that is not known stays as it is, so a path that needs it does not resolve.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SystemVariables
{
    private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private static readonly Lazy<SystemVariables> Current = new(() => From(HostReaders.Registry(), Environment.GetFolderPath));

    private readonly Dictionary<string, string> values;

    internal SystemVariables(IReadOnlyDictionary<string, string> values) { this.values = new(values, StringComparer.OrdinalIgnoreCase); }

    /// <summary>The values of this machine, read once per process.</summary>
    public static SystemVariables Machine() => Current.Value;

    public IReadOnlyDictionary<string, string> Values => values;

    public string? Get(string name) => values.GetValueOrDefault(name);

    internal static SystemVariables From(IRegistryReader? registry, Func<Environment.SpecialFolder, string> folder)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Put(string name, string? value) { if (!string.IsNullOrEmpty(value)) known[name] = value; }
        var windows = folder(Environment.SpecialFolder.Windows);
        Put("SystemRoot", windows);
        Put("windir", windows);
        Put("SystemDrive", windows.Length >= 2 && windows[1] == ':' ? windows[..2] : null);
        Put("ProgramFiles", folder(Environment.SpecialFolder.ProgramFiles));
        Put("ProgramW6432", folder(Environment.SpecialFolder.ProgramFiles));
        Put("ProgramFiles(x86)", folder(Environment.SpecialFolder.ProgramFilesX86));
        Put("CommonProgramFiles", folder(Environment.SpecialFolder.CommonProgramFiles));
        Put("CommonProgramW6432", folder(Environment.SpecialFolder.CommonProgramFiles));
        Put("CommonProgramFiles(x86)", folder(Environment.SpecialFolder.CommonProgramFilesX86));
        Put("ProgramData", folder(Environment.SpecialFolder.CommonApplicationData));
        Put("ALLUSERSPROFILE", folder(Environment.SpecialFolder.CommonApplicationData));
        var documents = folder(Environment.SpecialFolder.CommonDocuments);
        Put("PUBLIC", documents.Length > 0 ? Path.GetDirectoryName(documents) : null);

        var result = new Dictionary<string, string>(known, StringComparer.OrdinalIgnoreCase);
        var stored = registry?.GetValues(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, MachineEnvironment);
        if (stored is not null)
        {
            var first = new SystemVariables(known);
            foreach (var (name, data) in stored)
            {
                // The folders Windows reports win over a stored value of the same name.
                if (known.ContainsKey(name) || data.AsString is not { Length: > 0 and <= 4096 } text || text.Any(char.IsControl)) continue;
                result[name] = data.Kind == RegistryDataKind.ExpandString ? first.Expand(text) : text;
            }
        }
        return new(result);
    }

    /// <summary>Replaces each <c>%NAME%</c> whose name is known. Other text, a lone percent sign and unknown names stay unchanged.</summary>
    public string Expand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf('%', i);
            if (start < 0) { builder.Append(text, i, text.Length - i); break; }
            var end = text.IndexOf('%', start + 1);
            if (end < 0) { builder.Append(text, i, text.Length - i); break; }
            builder.Append(text, i, start - i);
            var name = text[(start + 1)..end];
            if (name.Length > 0 && values.TryGetValue(name, out var value)) { builder.Append(value); i = end + 1; }
            else { builder.Append('%').Append(name); i = end; }
        }
        return builder.ToString();
    }
}
