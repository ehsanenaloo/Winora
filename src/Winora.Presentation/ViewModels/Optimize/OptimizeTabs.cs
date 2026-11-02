// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>
/// The tabs of the Optimize page. A machine setting belongs to one tab by what it is about, not by the catalog file it comes from. The key is also the end of the resource name of the tab title.
/// </summary>
public static class OptimizeTabs
{
    public static readonly IReadOnlyList<string> Order = ["Performance", "SecurityPrivacy", "Gpu", "Bloat", "Experience", "Ai", "System"];

    public static string KeyOf(string definitionId)
    {
        var id = definitionId.StartsWith("machine.", StringComparison.Ordinal) ? definitionId["machine.".Length..] : definitionId;
        if (id.StartsWith("ai.", StringComparison.Ordinal)) return "Ai";
        if (id.StartsWith("graphics.", StringComparison.Ordinal) || id.StartsWith("system.nvidia", StringComparison.Ordinal)) return "Gpu";
        if (id.StartsWith("services.", StringComparison.Ordinal) || id.StartsWith("edge.", StringComparison.Ordinal) || id.StartsWith("apps.", StringComparison.Ordinal)) return "Bloat";
        if (id.StartsWith("privacy.", StringComparison.Ordinal) || id.StartsWith("search.", StringComparison.Ordinal) || id.StartsWith("security.", StringComparison.Ordinal)
            || id.StartsWith("network.", StringComparison.Ordinal)) return "SecurityPrivacy";
        if (id.StartsWith("explorer.", StringComparison.Ordinal) || id.StartsWith("start.", StringComparison.Ordinal) || id.StartsWith("taskbar.", StringComparison.Ordinal)
            || id.StartsWith("shell.", StringComparison.Ordinal) || id is "system.settings_home" or "system.developer_mode" or "system.sideload_apps") return "Experience";
        if (id.StartsWith("performance.", StringComparison.Ordinal) || id is "system.responsiveness" or "system.fast_startup") return "Performance";
        return "System";
    }

    public static int OrderOf(string key) => Math.Max(0, ((List<string>)[.. Order]).IndexOf(key));
}
