// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Winora.Elevation.Windows;

/// <summary>PROCESS_MITIGATION_POLICY values used by the table (winnt.h).</summary>
public enum MitigationPolicy
{
    DynamicCode = 2, StrictHandleCheck = 3, SystemCallDisable = 4, ExtensionPointDisable = 6, ImageLoad = 10, RedirectionTrust = 16,
}

/// <summary>When an entry applies: always, or only for the NativeAOT runtime model (dynamic code cannot be prohibited under a JIT).</summary>
public enum MitigationCondition { Always, NativeAotOnly }

/// <summary>One row of the committed table <c>mitigations.v1</c>. <c>VerifiedBuilds</c> stays empty until a VM run records a build.</summary>
public sealed record MitigationEntry(
    string Name, MitigationPolicy Policy, uint Flags, bool Required, int MinimumBuild, string ExpectedResult, MitigationCondition Condition, IReadOnlyList<int> VerifiedBuilds);

public sealed record MitigationOutcome(string Name, bool Attempted, bool Succeeded, int Win32Error);

/// <summary>
/// The process-mitigation table as data. Every entry is a candidate marked "unverified" by, so none is Required yet:
/// entries become required only with VM evidence. <see cref="Describe"/> is a dry run; <see cref="Apply"/> changes the calling process
/// and must only be called by the worker's hardening bootstrap (never by tests).
/// </summary>
public sealed class MitigationPlan
{
    public const string TableName = "mitigations.v1";
    public const int Windows11Build22000 = 22000;

    private MitigationPlan(IReadOnlyList<MitigationEntry> entries) => Entries = entries;

    public IReadOnlyList<MitigationEntry> Entries { get; }

    /// <summary>A table with the given rows (used by the worker tests to model a required row; the shipped table is <see cref="V1"/>).</summary>
    public static MitigationPlan FromEntries(IEnumerable<MitigationEntry> entries) => new(entries.ToArray());

    public static MitigationPlan V1 { get; } = new(new MitigationEntry[]
    {
        new("extension_points_disabled", MitigationPolicy.ExtensionPointDisable, 0x1, false, Windows11Build22000, "succeeds", MitigationCondition.Always, Array.Empty<int>()),
        new("image_load_restricted", MitigationPolicy.ImageLoad, 0x1 | 0x2 | 0x4, false, Windows11Build22000, "succeeds", MitigationCondition.Always, Array.Empty<int>()),
        new("strict_handle_checks", MitigationPolicy.StrictHandleCheck, 0x1 | 0x2, false, Windows11Build22000, "succeeds", MitigationCondition.Always, Array.Empty<int>()),
        new("win32k_calls_disallowed", MitigationPolicy.SystemCallDisable, 0x1, false, Windows11Build22000, "may_be_refused_after_start", MitigationCondition.Always, Array.Empty<int>()),
        new("redirection_trust_enforced", MitigationPolicy.RedirectionTrust, 0x1, false, Windows11Build22000, "succeeds", MitigationCondition.Always, Array.Empty<int>()),
        new("dynamic_code_prohibited", MitigationPolicy.DynamicCode, 0x1, false, Windows11Build22000, "succeeds_native_aot_only", MitigationCondition.NativeAotOnly, Array.Empty<int>()),
    });

    /// <summary>Dry run: one stable line per entry, no process state is touched.</summary>
    public IReadOnlyList<string> Describe(bool nativeAot) =>
        Entries.Select(e => string.Create(CultureInfo.InvariantCulture,
            $"{TableName}:{e.Name} policy={(int)e.Policy} flags=0x{e.Flags:X} required={(e.Required ? "yes" : "no")} minBuild={e.MinimumBuild} expect={e.ExpectedResult} applies={AppliesTo(e, nativeAot)} verified={string.Join(',', e.VerifiedBuilds)}")).ToList();

    public static bool AppliesTo(MitigationEntry entry, bool nativeAot) => entry.Condition == MitigationCondition.Always || nativeAot;

    /// <summary>True when every entry that would be applied and is required succeeded (the worker then continues); false means exit with the trust-failure code and the hardening reason.</summary>
    public static bool RequiredEntriesSatisfied(IEnumerable<MitigationEntry> entries, IEnumerable<MitigationOutcome> outcomes)
    {
        var byName = outcomes.ToDictionary(o => o.Name, StringComparer.Ordinal);
        foreach (var e in entries)
            if (e.Required && (!byName.TryGetValue(e.Name, out var o) || !o.Attempted || !o.Succeeded)) return false;
        return true;
    }

    /// <summary>Applies every applicable entry to the CURRENT process, in table order. Changes process state; not called by tests.</summary>
    public unsafe IReadOnlyList<MitigationOutcome> Apply(bool nativeAot = true)
    {
        var results = new List<MitigationOutcome>(Entries.Count);
        foreach (var e in Entries)
        {
            if (!OperatingSystem.IsWindows() || !AppliesTo(e, nativeAot) || Environment.OSVersion.Version.Build < e.MinimumBuild)
            {
                results.Add(new MitigationOutcome(e.Name, false, false, 0));
                continue;
            }
            uint flags = e.Flags;
            bool ok = Native.SetProcessMitigationPolicy((int)e.Policy, &flags, sizeof(uint));
            results.Add(new MitigationOutcome(e.Name, true, ok, ok ? 0 : Marshal.GetLastPInvokeError()));
        }
        return results;
    }

    /// <summary>True when the process runs without a JIT (NativeAOT), which is what permits the dynamic-code row.</summary>
    public static bool IsNativeAotRuntime => !RuntimeFeature.IsDynamicCodeSupported;
}

/// <summary>
/// Job object limits of step 5 as data. A job object limits the active-process count, kills on close and caps process memory;
/// it has no handle or thread limit, so the two ceilings are self-checks the worker performs, not kernel limits.
/// </summary>
public sealed record JobLimits
{
    public const uint LimitProcessMemory = 0x100, LimitActiveProcess = 0x8, LimitKillOnJobClose = 0x2000;
    public const int ExtendedLimitInformationClass = 9;

    public uint ActiveProcessLimit { get; init; } = 1;
    public bool KillOnJobClose { get; init; } = true;
    public ulong ProcessMemoryLimitBytes { get; init; } = 64UL * 1024 * 1024;
    /// <summary>Self-imposed ceilings.</summary>
    public uint HandleCeiling { get; init; } = 256;
    public uint ThreadCeiling { get; init; } = 32;

    public static JobLimits Default { get; } = new();

    public uint LimitFlags => LimitProcessMemory | LimitActiveProcess | (KillOnJobClose ? LimitKillOnJobClose : 0);

    /// <summary>
    /// Creates an unnamed job object with these limits and returns the limits read back from the kernel. The job is closed again and no process
    /// is assigned, so the calling process is unaffected. Used to prove the limit encoding.
    /// </summary>
    public unsafe JobLimitsReadBack CreateAndReadBackWithoutAssigning()
    {
        if (!OperatingSystem.IsWindows()) return new JobLimitsReadBack(false, 0, 0, 0);
        using var job = Native.CreateJobObjectW(0, null);
        if (job.IsInvalid) return new JobLimitsReadBack(false, 0, 0, 0);
        if (!SetLimits(job)) return new JobLimitsReadBack(false, 0, 0, 0);
        JobExtendedLimit q = default;
        return Native.QueryInformationJobObject(job, ExtendedLimitInformationClass, &q, (uint)sizeof(JobExtendedLimit), out _)
            ? new JobLimitsReadBack(true, q.Basic.LimitFlags, q.Basic.ActiveProcessLimit, q.ProcessMemoryLimit)
            : new JobLimitsReadBack(false, 0, 0, 0);
    }

    /// <summary>Creates the job, sets the limits and assigns the CURRENT process. The handle is returned to the caller, who must keep it for the process lifetime (closing it kills the process). Not called by tests.</summary>
    internal unsafe Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid? ApplyToCurrentProcess()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = Native.CreateJobObjectW(0, null);
        if (job.IsInvalid || !SetLimits(job) || !Native.AssignProcessToJobObject(job, Native.CurrentProcessPseudoHandle)) { job.Dispose(); return null; }
        return job;
    }

    private unsafe bool SetLimits(SafeKernelHandle job)
    {
        JobExtendedLimit info = default;
        info.Basic.LimitFlags = LimitFlags;
        info.Basic.ActiveProcessLimit = ActiveProcessLimit;
        info.ProcessMemoryLimit = (nuint)ProcessMemoryLimitBytes;
        return Native.SetInformationJobObject(job, ExtendedLimitInformationClass, &info, (uint)sizeof(JobExtendedLimit));
    }
}

public sealed record JobLimitsReadBack(bool Ok, uint LimitFlags, uint ActiveProcessLimit, nuint ProcessMemoryLimit);

public sealed record TripwireResult(bool Tripped, IReadOnlyList<string> Offending);

/// <summary>
/// Environment-hook detector of. It looks at variable NAMES only and never reads a value for behavior (section 20).
/// The rule: STARTUP_HOOKS, ADDITIONAL_DEPS, SHARED_STORE, DOTNET_ROOT*, *_ENABLE_PROFILING, *_PROFILER*, any COMPlus_, CORECLR_ or COR_ prefix,
/// and any DOTNET_ runtime knob except the closed benign list (DOTNET_CLI_*, DOTNET_NOLOGO, DOTNET_SKIP_FIRST_TIME_EXPERIENCE).
/// The benign list is provisional until experiment X1 dumps the environment that really arrives.
/// </summary>
public static class EnvironmentTripwire
{
    private static readonly string[] BenignExact = { "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE" };
    private const string BenignPrefix = "DOTNET_CLI_";

    public static bool IsOffending(string? name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '=') return false; // "=C:" style per-drive entries are not variables
        var n = name;
        if (Has(n, "STARTUP_HOOKS") || Has(n, "ADDITIONAL_DEPS") || Has(n, "SHARED_STORE")) return true;
        if (n.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.EndsWith("_ENABLE_PROFILING", StringComparison.OrdinalIgnoreCase) || Has(n, "_PROFILER")) return true;
        if (n.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase))
        {
            if (n.StartsWith(BenignPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var b in BenignExact) if (n.Equals(b, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        return false;
    }

    public static TripwireResult Scan(IEnumerable<string> names)
    {
        var offending = new List<string>();
        foreach (var name in names) if (IsOffending(name)) offending.Add(name);
        return new TripwireResult(offending.Count > 0, offending);
    }

    /// <summary>Enumerates the names in the process environment block (values are skipped) and scans them. The single allow-listed environment access of the worker.</summary>
    public static unsafe TripwireResult ScanCurrentEnvironment() => Scan(EnumerateNames());

    private static unsafe List<string> EnumerateNames()
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;
        char* block = Native.GetEnvironmentStringsW();
        if (block is null) return names;
        try
        {
            for (char* p = block; *p != '\0';)
            {
                char* start = p;
                while (*p != '\0') p++;
                var entry = new ReadOnlySpan<char>(start, (int)(p - start));
                int skip = entry.Length > 0 && entry[0] == '=' ? 1 : 0;
                int eq = entry[skip..].IndexOf('=');
                if (eq >= 0) eq += skip;
                if (eq > 0) names.Add(new string(entry[..eq]));
                p++;
            }
        }
        finally { Native.FreeEnvironmentStringsW(block); }
        return names;
    }

    private static bool Has(string name, string fragment) => name.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
