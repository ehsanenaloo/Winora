// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using Winora.Instance;
namespace Winora.Launch;

/// <summary>What the launcher does for one start. The Desktop path is searched in the install layout and in a flat folder.</summary>
internal static class Launcher
{
    internal static readonly TimeSpan ConnectWait = TimeSpan.FromMilliseconds(400), ReplyWait = TimeSpan.FromSeconds(5);

    /// <summary>The page key of <c>--page &lt;key&gt;</c>, or null. A key that is not plain letters is dropped; it is never put on a command line.</summary>
    internal static string? ParseKey(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], "--page", StringComparison.Ordinal)) return PageKeys.IsWellFormed(args[i + 1]) ? args[i + 1] : null;
        return null;
    }

    internal static string? FindDesktop(string baseDirectory)
    {
        foreach (var candidate in new[] { Path.Combine(baseDirectory, "Winora.Desktop.exe"), Path.Combine(baseDirectory, "..", "desktop", "Winora.Desktop.exe") })
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>0 when the request was handed over or Winora was started, 1 when it could not be done.</summary>
    internal static int Run(string[] args, string baseDirectory, Func<string, string?, HandOverResult>? send = null, Func<string, string?, bool>? start = null)
    {
        var key = ParseKey(args);
        var desktop = FindDesktop(baseDirectory);
        if (desktop is null) return 1;
        string pipe;
        try { pipe = InstanceChannel.CurrentPipeName(); }
        catch (InvalidOperationException) { return 1; }
        var result = send is null ? InstanceClient.Send(pipe, key, desktop, ConnectWait, ReplyWait) : send(pipe, key);
        switch (result)
        {
            case HandOverResult.Delivered or HandOverResult.Refused: return 0;
            case HandOverResult.Failed: return 1;
            default:
                return (start ?? StartDesktop)(desktop, key) ? 0 : 1;
        }
    }

    // The shell starts the program, so Windows shows its normal consent prompt for the administrator manifest of Winora.
    private static bool StartDesktop(string desktop, string? key)
    {
        var info = new ProcessStartInfo(desktop) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(desktop)! };
        if (key is not null) info.Arguments = "--page " + key;
        try { Process.Start(info)?.Dispose(); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; } // a declined consent prompt or a failed start
    }
}

internal static class Program
{
    private static int Main(string[] args) => Launcher.Run(args, AppContext.BaseDirectory);
}
