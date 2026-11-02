// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;

namespace Winora.Cli;

/// <summary>The version printed by <c>--version</c>, read from the assembly so it cannot differ from the package version.</summary>
internal static class VersionText
{
    public static string Current => FromInformationalVersion(typeof(VersionText).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The build adds "+&lt;source revision&gt;" to the informational version; only the part before it is the version.</summary>
    internal static string FromInformationalVersion(string? informational) =>
        string.IsNullOrWhiteSpace(informational) ? "unknown" : informational.Split('+')[0];
}
