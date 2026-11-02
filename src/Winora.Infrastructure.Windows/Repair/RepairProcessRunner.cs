// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Repair;

/// <summary>What a tool's exit code and output mean.</summary>
internal readonly record struct ExitMeaning(RepairOutcome Outcome, string Code, bool RestartNeeded);

/// <summary>One closed-list entry: the file in the System32 folder, its fixed arguments, its time limit and how to read its result.</summary>
internal sealed record ToolSpec(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, Func<int, string, ExitMeaning> Interpret);

/// <summary>
/// The only place where Winora starts another program. It starts only the entries of <see cref="RepairTool"/>. Each entry is a program in the System32
/// folder of this Windows installation, found through the Windows folder, with a fixed argument list. The only values that are not fixed are a fixed-drive letter and a feature name that passed
/// <see cref="RepairRules.Validate"/>, and each is passed as its own argument. There is no shell, no command line text, no script host; the program gets the fixed environment of
/// <see cref="ChildEnvironment"/> (TEMP in a new admin-only folder) instead of the app's own, the working directory is the System32 folder, the standard input is closed, the output is captured up to a size cap, every run has a time limit from <see cref="WaitLimits"/>, a cancelled run ends the whole process tree, and a run that reaches its limit is left running: Winora stops waiting, reports
/// <see cref="RepairOutcome.StillRunning"/> and starts nothing new until that program has ended.
/// Only one run exists at a time. This file is covered by a source scan; do not add another place that starts a program.
/// </summary>
public sealed partial class RepairProcessRunner : IRepairRunner
{
    public const int MaxOutputChars = 256 * 1024;
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    private readonly string system32;
    private readonly Func<RepairRequest, ToolSpec> specs;
    private readonly Func<IReadOnlyList<string>> volumes;
    private readonly int outputCap;
    private readonly Func<IReadOnlyDictionary<string, string>?> environment;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<SlowToolWait> wait;
    private Process? straggler;

    /// <summary>Test seam: replaces the call that ends a program and its children.</summary>
    internal Action<Process>? KillOverride { get; init; }
    internal Func<Process, bool>? HasExitedOverride { get; init; }

    /// <param name="slowToolWait">The Settings choice "Wait for slow tools", read at the start of each run. Null is Normal.</param>
    public RepairProcessRunner(Func<SlowToolWait>? slowToolWait = null) : this(WindowsSystem32(), null, null, MaxOutputChars, static () => OperatingSystem.IsWindows() ? ChildEnvironment.ForChild() : null, slowToolWait) { }

    /// <summary>Test seam. The folder is always System32 of the running Windows; a test can only change which file in it is started, its arguments, limits and the child environment.</summary>
    internal RepairProcessRunner(string system32Folder, Func<RepairRequest, ToolSpec>? specSource, Func<IReadOnlyList<string>>? fixedVolumes, int outputCharCap,
        Func<IReadOnlyDictionary<string, string>?> childEnvironment, Func<SlowToolWait>? slowToolWait = null)
    {
        environment = childEnvironment;
        system32 = system32Folder;
        wait = slowToolWait ?? (static () => SlowToolWait.Normal);
        specs = specSource ?? (r => Production(system32Folder, r, wait()));
        volumes = fixedVolumes ?? FixedDriveLetters;
        outputCap = outputCharCap;
    }

    internal static string WindowsSystem32() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

    public static IReadOnlyList<string> FixedDriveLetters()
    {
        var result = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.DriveType == DriveType.Fixed && d.IsReady) result.Add(d.Name.TrimEnd('\\')); }
            catch (IOException) { /* a drive that cannot be read is not offered */ }
        }
        return result;
    }

    public IReadOnlyList<string> FixedVolumes() => volumes();

    public string? Preview(RepairRequest request)
    {
        if (RepairRules.Validate(request, volumes()) is not null) return null;
        var spec = specs(Normalize(request));
        return string.Join(' ', new[] { spec.FileName }.Concat(spec.Arguments).Select(Quote));
    }

    /// <summary>The time limit of a tool of the closed list. A slow machine needs minutes for the feature list, so a limit below ten minutes cuts a working run.</summary>
    internal static TimeSpan TimeoutOf(RepairRequest request, SlowToolWait choice = SlowToolWait.Normal) => Production(WindowsSystem32(), request, choice).Timeout;

    private static string Quote(string part) => part.Contains(' ') ? "\"" + part + "\"" : part;

    private static RepairRequest Normalize(RepairRequest r) => r.Tool == RepairTool.DiskCheck ? r with { Argument = RepairRules.NormalizeVolume(r.Argument) } : r;

    // ---- the closed list ----

    private static ToolSpec Production(string dir, RepairRequest r, SlowToolWait choice)
    {
        string File(string name) => Path.Combine(dir, name);
        var limit = WaitLimits.For(r.Tool, choice);
        const string Dism = "dism.exe";
        string[] Online(string action, params string[] more) => new[] { "/Online", "/English", action }.Concat(more).ToArray();
        return r.Tool switch
        {
            RepairTool.SystemFileCheck => new(File("sfc.exe"), ["/scannow"], limit, SfcMeaning),
            RepairTool.DiskCheck => new(File("chkdsk.exe"), [r.Argument!, "/scan", "/perf"], limit, DiskMeaning),
            RepairTool.WinsockReset => new(File("netsh.exe"), ["winsock", "reset"], limit, NetshMeaning(true)),
            RepairTool.IpReset => new(File("netsh.exe"), ["int", "ip", "reset"], limit, NetshMeaning(true)),
            RepairTool.Ipv6Reset => new(File("netsh.exe"), ["int", "ipv6", "reset"], limit, NetshMeaning(true)),
            RepairTool.WinHttpProxyReset => new(File("netsh.exe"), ["winhttp", "reset", "proxy"], limit, NetshMeaning(false)),
            RepairTool.StoreCacheReset => new(File("wsreset.exe"), [], limit, SimpleMeaning),
            RepairTool.ComponentCheckHealth => new(File(Dism), ["/Online", "/English", "/Cleanup-Image", "/CheckHealth"], limit, ComponentMeaning),
            RepairTool.ComponentScanHealth => new(File(Dism), ["/Online", "/English", "/Cleanup-Image", "/ScanHealth"], limit, ComponentMeaning),
            RepairTool.ComponentRestoreHealth => new(File(Dism), ["/Online", "/English", "/Cleanup-Image", "/RestoreHealth"], limit, ComponentMeaning),
            RepairTool.FeatureList => new(File(Dism), Online("/Get-Features", "/Format:Table"), limit, SimpleMeaning),
            RepairTool.FeatureEnable => new(File(Dism), Online("/Enable-Feature", "/FeatureName:" + r.Argument, "/NoRestart"), limit, FeatureMeaning),
            RepairTool.FeatureDisable => new(File(Dism), Online("/Disable-Feature", "/FeatureName:" + r.Argument, "/NoRestart"), limit, FeatureMeaning),
            _ => throw new ArgumentOutOfRangeException(nameof(r), "Not a tool of the closed list."),
        };
    }

    // ---- exit code tables (one per tool family) ----

    private static bool Has(string output, string text) => output.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>sfc: the program documents no exit codes, so the English result lines decide. Without a known line, exit 0 is "no verdict" and any other code is "found problems".</summary>
    internal static ExitMeaning SfcMeaning(int exit, string output)
    {
        if (Has(output, "system repair pending")) return new(RepairOutcome.Failed, RepairCode.SfcRebootPending, true);
        if (Has(output, "unable to fix some of them")) return new(RepairOutcome.FoundProblems, RepairCode.SfcUnrepaired, false);
        if (Has(output, "could not perform the requested operation")) return new(RepairOutcome.Failed, RepairCode.SfcCouldNotRun, false);
        if (Has(output, "successfully repaired them")) return new(RepairOutcome.Succeeded, RepairCode.SfcRepaired, false);
        if (Has(output, "did not find any integrity violations")) return new(RepairOutcome.Succeeded, RepairCode.SfcClean, false);
        return exit == 0 ? new(RepairOutcome.Succeeded, RepairCode.SfcNoVerdict, false) : new(RepairOutcome.FoundProblems, RepairCode.ExitNonZero, false);
    }

    /// <summary>chkdsk: 0 no errors, 1 errors found and fixed, 2 clean-up done or not done, 3 the disk could not be checked or errors were not fixed (documented exit codes).</summary>
    internal static ExitMeaning DiskMeaning(int exit, string output) => exit switch
    {
        0 => new(RepairOutcome.Succeeded, RepairCode.DiskClean, false),
        1 => new(RepairOutcome.Succeeded, RepairCode.DiskFixed, false),
        2 => new(RepairOutcome.FoundProblems, RepairCode.DiskProblems, false),
        3 => new(RepairOutcome.FoundProblems, RepairCode.DiskProblems, false),
        _ => new(RepairOutcome.Failed, RepairCode.ExitNonZero, false),
    };

    /// <summary>netsh: 0 is success. A reset of the Winsock catalog or the IP stack takes effect after a restart.</summary>
    private static Func<int, string, ExitMeaning> NetshMeaning(bool restart) => (exit, _) =>
        exit == 0 ? new(RepairOutcome.Succeeded, restart ? RepairCode.RestartNeeded : RepairCode.Completed, restart) : new(RepairOutcome.Failed, RepairCode.ExitNonZero, false);

    internal static ExitMeaning SimpleMeaning(int exit, string output) =>
        exit == 0 ? new(RepairOutcome.Succeeded, RepairCode.Completed, false) : new(RepairOutcome.Failed, RepairCode.ExitNonZero, false);

    private const int ErrorSuccessRebootRequired = 3010;
    private const int SourceFilesNotFound = unchecked((int)0x800F081F);
    private const int SourceBlockedByPolicy = unchecked((int)0x800F0907);

    /// <summary>dism /Cleanup-Image: 0 success (also when the check finds damage; the English text says so), 3010 success with a restart, 0x800F081F the repair source was not found, 0x800F0907 policy blocks the source.</summary>
    internal static ExitMeaning ComponentMeaning(int exit, string output)
    {
        if (exit == ErrorSuccessRebootRequired) return new(RepairOutcome.Succeeded, RepairCode.RestartNeeded, true);
        if (exit == SourceFilesNotFound) return new(RepairOutcome.Failed, RepairCode.ComponentSourceMissing, false);
        if (exit == SourceBlockedByPolicy) return new(RepairOutcome.Failed, RepairCode.ComponentSourceBlocked, false);
        if (exit != 0) return new(RepairOutcome.Failed, RepairCode.ExitNonZero, false);
        if (Has(output, "not repairable") || Has(output, "cannot be repaired")) return new(RepairOutcome.FoundProblems, RepairCode.ComponentNotRepairable, false);
        if (Has(output, "is repairable")) return new(RepairOutcome.FoundProblems, RepairCode.ComponentRepairable, false);
        if (Has(output, "no component store corruption detected")) return new(RepairOutcome.Succeeded, RepairCode.ComponentHealthy, false);
        return new(RepairOutcome.Succeeded, RepairCode.Completed, false);
    }

    /// <summary>dism feature actions: 0 success, 3010 success with a restart.</summary>
    internal static ExitMeaning FeatureMeaning(int exit, string output) =>
        exit == 0 ? new(RepairOutcome.Succeeded, RepairCode.Completed, false)
        : exit == ErrorSuccessRebootRequired ? new(RepairOutcome.Succeeded, RepairCode.RestartNeeded, true)
        : new(RepairOutcome.Failed, RepairCode.ExitNonZero, false);

    // ---- the run ----

    public async Task<RepairRun> RunAsync(RepairRequest request, IProgress<RepairProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (RepairRules.Validate(request, volumes()) is not null) return NotStarted(RepairCode.InvalidRequest);
        if (!gate.Wait(0)) return NotStarted(RepairCode.Busy);
        try
        {
            if (StragglerRuns()) return NotStarted(RepairCode.PreviousRunning);
            var spec = specs(Normalize(request));
            if (!IsInSystem32(spec.FileName)) return NotStarted(RepairCode.InvalidRequest);
            if (!File.Exists(spec.FileName)) return NotStarted(RepairCode.ToolMissing);
            if (environment() is not { } variables) return NotStarted(RepairCode.StartFailed);
            if (cancellationToken.IsCancellationRequested) return NotStarted(RepairCode.Cancelled);
            var info = new ProcessStartInfo(spec.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = system32,
            };
            foreach (var a in spec.Arguments) info.ArgumentList.Add(a);
            ChildEnvironment.Apply(info.Environment, variables);

            Process? process;
            try { process = Process.Start(info); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException) { return NotStarted(RepairCode.StartFailed); }
            if (process is null) return NotStarted(RepairCode.StartFailed);
            var run = await Supervise(process, spec, progress, cancellationToken).ConfigureAwait(false);
            if (run.Outcome == RepairOutcome.StillRunning) straggler = process;
            else process.Dispose();
            return run;
        }
        finally { gate.Release(); }
    }

    /// <summary>True while a program that reached its limit still runs. Once it has ended it is let go.</summary>
    private bool StragglerRuns()
    {
        if (straggler is not { } p) return false;
        if (!HasEnded(p)) return true;
        p.Dispose();
        straggler = null;
        return false;
    }

    private bool IsInSystem32(string file)
    {
        if (!Path.IsPathFullyQualified(file)) return false;
        var folder = Path.GetDirectoryName(Path.GetFullPath(file));
        return folder is not null && string.Equals(folder.TrimEnd('\\'), Path.GetFullPath(system32).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static RepairRun NotStarted(string code) => new(RepairOutcome.NotStarted, null, code, "", "", false, false);

    private async Task<RepairRun> Supervise(Process process, ToolSpec spec, IProgress<RepairProgress>? progress, CancellationToken cancellationToken)
    {
        try { process.StandardInput.Close(); }
        catch (IOException) { /* the tool already closed it */ }
        var reporter = new Reporter(progress);
        var output = new Collector(outputCap, reporter);
        var errors = new Collector(outputCap, null);
        var pumps = Task.WhenAll(Pump(process.StandardOutput.BaseStream, output), Pump(process.StandardError.BaseStream, errors));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(spec.Timeout);
        var stopped = false;
        var limitReached = false;
        try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // The person asked to stop: the whole tree ends.
                stopped = true;
                (KillOverride ?? KillTree)(process);
                await Task.Run(() => process.WaitForExit((int)KillWait.TotalMilliseconds), CancellationToken.None).ConfigureAwait(false);
                // Only an exit that is confirmed is a cancel. A program that could not be ended keeps being watched.
                if (!HasEnded(process)) { stopped = false; limitReached = true; }
            }
            else limitReached = !HasEnded(process);
        }
        if (!limitReached) await Task.WhenAny(pumps, Task.Delay(DrainWait, CancellationToken.None)).ConfigureAwait(false);

        var text = output.Text() + errors.Text();
        var truncated = output.Truncated || errors.Truncated;
        var summary = Summarize(text);
        if (stopped) return new(RepairOutcome.Cancelled, null, RepairCode.Cancelled, summary, text, truncated, false);
        // The limit only ends the wait. The program keeps running and its result is not known, so the run is not a failure.
        if (limitReached) return new(RepairOutcome.StillRunning, null, RepairCode.StillRunning, summary, text, truncated, false);
        int exit;
        try { exit = process.ExitCode; }
        catch (InvalidOperationException) { return new(RepairOutcome.Failed, null, RepairCode.StartFailed, summary, text, truncated, false); }
        var meaning = spec.Interpret(exit, text);
        return new(meaning.Outcome, exit, meaning.Code, summary, text, truncated, meaning.RestartNeeded);
    }

    /// <summary>True only when the exit is confirmed. A state that cannot be read counts as still running, so the guard is never released on a guess.</summary>
    private bool HasEnded(Process process)
    {
        try { return (HasExitedOverride ?? (p => p.HasExited))(process); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { return false; }
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { /* it ended on its own */ }
    }

    private static async Task Pump(Stream stream, Collector sink)
    {
        var buffer = new byte[8192];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0) sink.Add(buffer.AsSpan(0, n));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { /* the pipe closed */ }
    }

    [GeneratedRegex(@"^\s*[\[=\s\-]*\d{1,3}(\.\d)?\s*%[\]\s=\-]*$|^\s*Verification\s+\d+%\s+complete", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProgressOnly();

    internal static string Summarize(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0 && !ProgressOnly().IsMatch(l)).ToList();
        var tail = string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - 12)));
        return tail.Length <= 1500 ? tail : tail[^1500..];
    }

    /// <summary>Throttles progress reports so a chatty tool cannot flood the page.</summary>
    internal sealed class Reporter(IProgress<RepairProgress>? progress)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long lastMs = -1000;
        private int? lastPercent;

        public void Report(int? percent, string line)
        {
            if (progress is null) return;
            var now = clock.ElapsedMilliseconds;
            if (percent == lastPercent && now - lastMs < 400) return;
            if (now - lastMs < 150) return;
            lastMs = now; lastPercent = percent;
            progress.Report(new(percent, line));
        }
    }

    /// <summary>Decodes the bytes of one stream (the programs write the console code page, or UTF-16 when the output is a pipe), keeps the text up to the cap and notes the last progress.</summary>
    internal sealed partial class Collector(int cap, Reporter? reporter)
    {
        private readonly StringBuilder text = new();
        private readonly object gate = new();
        private Decoder? decoder;
        private bool first = true;

        public bool Truncated { get; private set; }

        public string Text() { lock (gate) return text.ToString(); }

        public void Add(ReadOnlySpan<byte> bytes)
        {
            lock (gate)
            {
                if (decoder is null) decoder = Choose(bytes, out first);
                if (first && bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) bytes = bytes[2..];
                first = false;
                var chars = new char[decoder.GetCharCount(bytes, false)];
                var count = decoder.GetChars(bytes, chars, false);
                var segment = new string(chars, 0, count).Replace("\0", "");
                if (segment.Length == 0) return;
                var room = cap - text.Length;
                if (room > 0) text.Append(segment.Length <= room ? segment : segment[..room]);
                if (segment.Length > room) Truncated = true;
                if (reporter is not null) Notify(segment);
            }
        }

        private void Notify(string segment)
        {
            var line = segment.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault() ?? "";
            if (line.Length > 200) line = line[..200];
            int? percent = null;
            var match = PercentRegex().Matches(segment).LastOrDefault();
            if (match is not null && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100) percent = (int)value;
            reporter!.Report(percent, line);
        }

        [GeneratedRegex(@"(\d{1,3}(?:\.\d)?)\s*%", RegexOptions.CultureInvariant)]
        private static partial Regex PercentRegex();

        private static Decoder Choose(ReadOnlySpan<byte> bytes, out bool first)
        {
            first = true;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetDecoder();
            var zerosAtOdd = 0;
            for (var i = 1; i < bytes.Length; i += 2) if (bytes[i] == 0) zerosAtOdd++;
            if (bytes.Length >= 4 && zerosAtOdd * 4 >= bytes.Length) return Encoding.Unicode.GetDecoder();
            return Oem().GetDecoder();
        }

        private static Encoding Oem()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { return Encoding.UTF8; }
        }
    }
}
