// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows.Shell;
using Winora.Instance;
namespace Winora.Desktop;

/// <summary>The command line of Winora: only <c>--page &lt;key&gt;</c> is known. Anything else is ignored.</summary>
public sealed record StartupArguments(string? Page, int Ignored)
{
    public static StartupArguments Parse(string[] args)
    {
        string? page = null;
        var ignored = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--page", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                if (PageKeys.IsWellFormed(args[i + 1])) page = args[i + 1]; else ignored++;
                i++;
            }
            else ignored++;
        }
        return new StartupArguments(page, ignored);
    }
}

/// <summary>The pages of the taskbar jump list. Each entry starts the launcher with a page key from the fixed list; no entry changes the system.</summary>
public static class JumpListService
{
    public sealed record Entry(string Key, string Title, string Description, string Arguments);

    /// <summary>One entry per allowed page key, named like the page in the navigation.</summary>
    public static IReadOnlyList<Entry> Entries(LocalizationService text) =>
        [.. PageKeys.Allowed.Select(key => new Entry(key, text[key], text.Format("JumpPageHelp", text[key]), "--page " + key))];

    /// <summary>The launcher beside the Desktop exe (flat folder) or in the sibling folder of the install layout. Null when it is not there.</summary>
    public static string? FindLauncher(string baseDirectory)
    {
        foreach (var candidate in new[] { Path.Combine(baseDirectory, "Winora.Launch.exe"), Path.Combine(baseDirectory, "..", "launch", "Winora.Launch.exe") })
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public static JumpList Build(LocalizationService text, string launcherPath, string iconPath)
    {
        var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        foreach (var entry in Entries(text))
            list.JumpItems.Add(new JumpTask
            {
                Title = entry.Title, Description = entry.Description, ApplicationPath = launcherPath, Arguments = entry.Arguments,
                WorkingDirectory = Path.GetDirectoryName(launcherPath), IconResourcePath = iconPath, IconResourceIndex = 0,
            });
        return list;
    }

    /// <summary>Sets the list now and again when the language changes. A missing launcher or a shell that refuses the list leaves the taskbar as it was.</summary>
    public static void Install(System.Windows.Application app, LocalizationService text, string baseDirectory, string iconPath, Action<string> log)
    {
        var launcher = FindLauncher(baseDirectory);
        if (launcher is null) { log("The launcher was not found; the jump list was not set"); return; }
        void Apply()
        {
            try { var list = Build(text, launcher, iconPath); JumpList.SetJumpList(app, list); list.Apply(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException) { log("The jump list could not be set"); }
        }
        Apply();
        text.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LocalizationService.Language)) Apply(); };
    }
}
