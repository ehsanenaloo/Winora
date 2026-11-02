// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;

namespace Winora.Application;

/// <summary>The result of a signature check. Unknown means the check could not give an answer (see <see cref="SignatureProblem"/>).</summary>
public enum SignatureState { Valid, Invalid, Unsigned, Unknown }

/// <summary>The class of a failed or missing check, in terms a user can read. None goes with Valid and Unsigned.</summary>
public enum SignatureProblem { None, Changed, UntrustedPublisher, Expired, ChainIncomplete, Blocked, Other, FileMissing, CheckFailed, TooLarge }

/// <summary>
/// What the signature check found for one file. <see cref="Signer"/> is the name on the certificate that Windows verified (never a name read from the file alone) and is set only for Valid.
/// <see cref="ViaCatalog"/> is true when the signature is in a Windows catalog and not in the file. <see cref="HResult"/> is the raw result of the trust check, for the details.
/// </summary>
public sealed record SignatureInfo(SignatureState State, string? Signer, SignatureProblem Problem, bool ViaCatalog, int HResult = 0)
{
    public static SignatureInfo Valid(string? signer, bool viaCatalog) => new(SignatureState.Valid, signer, SignatureProblem.None, viaCatalog);
    public static SignatureInfo Unsigned { get; } = new(SignatureState.Unsigned, null, SignatureProblem.None, false);
    public static SignatureInfo Invalid(SignatureProblem problem, int hresult, bool viaCatalog) => new(SignatureState.Invalid, null, problem, viaCatalog, hresult);
    public static SignatureInfo Unknown(SignatureProblem problem) => new(SignatureState.Unknown, null, problem, false);
}

/// <summary>
/// Checks the digital signature of one file with the Windows trust check. Implementations must not run, load or change the file, must not use the network and must observe the token between steps.
/// A failure of the check becomes <see cref="SignatureState.Unknown"/>; the method throws only for cancellation.
/// </summary>
public interface IFileSignatureChecker
{
    SignatureInfo Check(string path, CancellationToken cancellationToken);
}

/// <summary>How many files one refresh checked now, took from the cache, and left for a later refresh because of the cap.</summary>
public sealed record SignatureBatch(int Checked, int Cached, int Deferred);

/// <summary>
/// Signature checks for the files that startup entries point to (decision DEC-IW-032). A result is kept by path, size and last write time, so a file that changed is checked again and a file that did not
/// is not (a file that could not be checked stays Unknown until it changes). One refresh checks at most <see cref="MaxPerRefresh"/> files that are not in the cache; the others wait for the next refresh. The work runs on a background thread and stops at a cancel.
/// </summary>
public sealed class SignatureService(IFileSignatureChecker checker, int maxPerRefresh = SignatureService.DefaultMaxPerRefresh, int maxCached = SignatureService.DefaultMaxCached)
{
    public const int DefaultMaxPerRefresh = 40;
    public const int DefaultMaxCached = 2000;

    private readonly Dictionary<(string Path, long Size, long Ticks), SignatureInfo> cache = new(new KeyComparer());
    private readonly object gate = new();

    public int MaxPerRefresh { get; } = Math.Max(1, maxPerRefresh);

    private sealed class KeyComparer : IEqualityComparer<(string Path, long Size, long Ticks)>
    {
        public bool Equals((string Path, long Size, long Ticks) a, (string Path, long Size, long Ticks) b) =>
            a.Size == b.Size && a.Ticks == b.Ticks && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Path, long Size, long Ticks) k) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path), k.Size, k.Ticks);
    }

    private static (string Path, long Size, long Ticks)? KeyOf(StartupFileFacts f) =>
        f is { TargetPath: { } path, Exists: true, Size: { } size, LastWriteTicks: { } ticks } ? (path, size, ticks) : null;

    /// <summary>The kept result for a file, Unknown (file missing) for a file that is not there, or null when the file was not checked yet.</summary>
    public SignatureInfo? Peek(StartupFileFacts file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Exists == false) return SignatureInfo.Unknown(SignatureProblem.FileMissing);
        if (KeyOf(file) is not { } key) return null;
        lock (gate) return cache.TryGetValue(key, out var hit) ? hit : null;
    }

    /// <summary>Checks the files that have no kept result, up to the cap. <paramref name="progress"/> gets the number of files checked so far in this call.</summary>
    public Task<SignatureBatch> CheckAsync(IEnumerable<StartupFileFacts> files, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var wanted = new List<(string Path, long Size, long Ticks)>();
        var seen = new HashSet<(string Path, long Size, long Ticks)>(new KeyComparer());
        var cached = 0;
        foreach (var file in files)
        {
            if (KeyOf(file) is not { } key || !seen.Add(key)) continue;
            lock (gate) { if (cache.ContainsKey(key)) { cached++; continue; } }
            wanted.Add(key);
        }
        var batch = wanted.Take(MaxPerRefresh).ToList();
        var deferred = wanted.Count - batch.Count;
        return Task.Run(() =>
        {
            var done = 0;
            foreach (var key in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = Run(key.Path, cancellationToken);
                lock (gate)
                {
                    if (cache.Count >= maxCached) cache.Clear();
                    cache[key] = info;
                }
                done++;
                progress?.Report(done);
            }
            return new SignatureBatch(done, cached, deferred);
        }, CancellationToken.None);
    }

    private SignatureInfo Run(string path, CancellationToken cancellationToken)
    {
        try { return checker.Check(path, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { return SignatureInfo.Unknown(SignatureProblem.CheckFailed); }
    }
}

/// <summary>Turns a scheduled task found by discovery into a read-only row for the Startup page. The Scheduled Tasks page stays the only place that changes a task.</summary>
public static class StartupTaskRows
{
    public const string IdPrefix = "task|";

    public static StartupItem From(ScheduledTaskInfo task) => new(
        IdPrefix + task.Path.ToLowerInvariant(), task.Name, task.Command ?? "", StartupSource.ScheduledTask, InstallationScope.Machine,
        task.State == TaskRunState.Disabled ? StartupApproval.Disabled : StartupApproval.Enabled, new("scheduledtask", "TaskScheduler", task.Path), task.Author);

    /// <summary>The tasks that start at logon or at boot, as rows.</summary>
    public static IEnumerable<StartupItem> FromLogonAndBoot(IEnumerable<ScheduledTaskInfo> tasks) => tasks.Where(t => t.RunsAtLogonOrBoot).Select(From);

    /// <summary>The task path of a task row (the Scheduled Tasks page identifies a task by it).</summary>
    public static string PathOf(StartupItem item) => item.Resource.Path;
}
