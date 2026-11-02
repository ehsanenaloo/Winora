// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
namespace Winora.Infrastructure.Windows.Optimize;

/// <summary>The built-in profile files that ship inside the app (definitions/profiles). The text is parsed by the strict parser of the profile service.</summary>
public static class EmbeddedProfiles
{
    public const string ResourcePrefix = "Winora.Profiles.";

    public static IReadOnlyList<string> Load()
    {
        var assembly = typeof(EmbeddedProfiles).Assembly;
        var list = new List<string>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            list.Add(reader.ReadToEnd());
        }
        return list;
    }
}
