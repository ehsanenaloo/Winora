// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>
/// THE one place that calls the shell launch API (technical-docs/protocol.md 4.1, 20 item 6; a source-scan test enforces it). Internal and sealed, with no
/// InternalsVisibleTo to any elevation test: tests use a recording fake, and a scan test proves no test file names this type. It starts exactly
/// what <see cref="WorkerLauncher"/> hands it with the <c>runas</c> verb; it refuses any other verb. It is constructed only when a launch is
/// really about to happen, so every refusal path leaves <see cref="InstancesCreated"/> at zero.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RunasShellExecutor : IShellExecutor
{
    private const uint SeeMaskNoAsync = 0x00000100, SeeMaskFlagNoUi = 0x00000400;
    private const int SwHide = 0, SwShowNormal = 1;

    /// <summary>Number of instances ever created in this process (diagnostic; the default composition must leave it at zero when a gate refuses).</summary>
    internal static int InstancesCreated;

    public RunasShellExecutor() => Interlocked.Increment(ref InstancesCreated);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfoW
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfoW info);

    public RunasResult Execute(RunasRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Verb, WorkerLauncher.Verb, StringComparison.Ordinal)) throw new InvalidOperationException("only the runas verb is allowed");
        var removed = new List<KeyValuePair<string, string?>>();
        try
        {
            foreach (var name in request.ScrubEnvironmentNames)
            {
                removed.Add(new(name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, null);
            }
            var info = new ShellExecuteInfoW
            {
                cbSize = (uint)Marshal.SizeOf<ShellExecuteInfoW>(), fMask = SeeMaskNoAsync | SeeMaskFlagNoUi, hwnd = 0, lpVerb = request.Verb, lpFile = request.File,
                lpParameters = request.Arguments, lpDirectory = request.WorkingDirectory, nShow = request.HideWindow ? SwHide : SwShowNormal,
            };
            var ok = ShellExecuteExW(ref info);
            return new RunasResult(ok, ok ? 0 : Marshal.GetLastPInvokeError());
        }
        finally
        {
            foreach (var (name, value) in removed) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
