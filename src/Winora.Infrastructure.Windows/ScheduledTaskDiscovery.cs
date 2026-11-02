// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Windows;

/// <summary>Raw task facts as read from Task Scheduler (State uses the TASK_STATE numbering: 1 disabled, 2 queued, 3 ready, 4 running).</summary>
internal sealed record RawTask(string Path, string Name, int State, bool Hidden, string? Author, DateTime? LastRun, DateTime? NextRun,
    bool LogonOrBoot, string? ActionPath, string? ActionArguments);

/// <summary>One enumerated task or one failure. FailedPath is the task or folder path that could not be read.</summary>
internal sealed record RawTaskResult(RawTask? Task, string? FailedPath, Exception? Error);

internal interface ITaskSchedulerReader
{
    /// <summary>Walks all folders; the sink returns false to stop (item cap). Throws for source-level failure.</summary>
    void Enumerate(Func<RawTaskResult, bool> sink, CancellationToken cancellationToken);
}

/// <summary>
/// Late-bound Task Scheduler 2.0 COM reader (ProgID Schedule.Service, no package). Only Connect, GetFolder, GetFolders, GetTasks and
/// property getters are used. The first Exec action is read as text and never run.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ComTaskSchedulerReader : ITaskSchedulerReader
{
    private const int MaxDepth = 16;
    private const int TaskEnumHidden = 1;
    private const int TriggerBoot = 8, TriggerLogon = 9, ActionExec = 0;

    public void Enumerate(Func<RawTaskResult, bool> sink, CancellationToken ct)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", false) ?? throw new PlatformNotSupportedException();
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            dynamic root = service.GetFolder("\\");
            try { Walk(root, 0, sink, ct); } finally { Release(root); }
        }
        finally { Release(service); }
    }

    private static bool Walk(dynamic folder, int depth, Func<RawTaskResult, bool> sink, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string folderPath = "?";
        try { folderPath = (string)folder.Path; } catch (COMException) { }
        try
        {
            dynamic tasks = folder.GetTasks(TaskEnumHidden);
            foreach (dynamic task in (IEnumerable)tasks)
            {
                ct.ThrowIfCancellationRequested();
                try { if (!sink(Read(task))) return false; }
                finally { Release(task); }
            }
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            if (!sink(new(null, folderPath, e))) return false;
        }
        if (depth >= MaxDepth) return true;
        try
        {
            dynamic folders = folder.GetFolders(0);
            foreach (dynamic child in (IEnumerable)folders)
            {
                try { if (!Walk(child, depth + 1, sink, ct)) return false; }
                finally { Release(child); }
            }
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            if (!sink(new(null, folderPath, e))) return false;
        }
        return true;
    }

    private static RawTaskResult Read(dynamic task)
    {
        string path = "?";
        try
        {
            path = (string)task.Path;
            string name = (string)task.Name;
            int state = (int)task.State;
            DateTime last = task.LastRunTime, next = task.NextRunTime;
            dynamic definition = task.Definition;
            bool hidden = (bool)definition.Settings.Hidden;
            string? author = null;
            try { author = (string?)definition.RegistrationInfo.Author; } catch (COMException) { }
            var logonOrBoot = false;
            foreach (dynamic trigger in (IEnumerable)definition.Triggers)
            {
                int kind = (int)trigger.Type;
                if (kind is TriggerBoot or TriggerLogon) logonOrBoot = true;
            }
            string? actionPath = null, actionArgs = null;
            foreach (dynamic action in (IEnumerable)definition.Actions)
            {
                if ((int)action.Type != ActionExec) continue;
                actionPath = (string?)action.Path;
                actionArgs = (string?)action.Arguments;
                break;
            }
            return new(new RawTask(path, name, state, hidden, author, last, next, logonOrBoot, actionPath, actionArgs), null, null);
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return new(null, path, e);
        }
    }

    private static void Release(object? com)
    {
        try { if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); } catch (ArgumentException) { }
    }
}

/// <summary>Read-only scheduled task inventory over the Task Scheduler COM API. Works as a standard user; unreadable tasks become issues.</summary>
public sealed class ScheduledTaskDiscovery : IDiscoverySource<ScheduledTaskInfo>
{
    private static readonly string SourceName = nameof(ScheduledTaskInfo);
    private readonly ITaskSchedulerReader? reader;
    private readonly int maxItems;
    private readonly Func<DateTimeOffset> clock;

    public ScheduledTaskDiscovery() : this(OperatingSystem.IsWindows() ? new ComTaskSchedulerReader() : null,
        DiscoverySupport.DefaultMaxItems, () => DateTimeOffset.UtcNow) { }

    internal ScheduledTaskDiscovery(ITaskSchedulerReader? reader, int maxItems, Func<DateTimeOffset> clock)
    {
        this.reader = reader; this.maxItems = maxItems; this.clock = clock;
    }

    public Task<DiscoveryResult<ScheduledTaskInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader is null) return Task.FromResult(DiscoverySupport.Unavailable<ScheduledTaskInfo>(clock()));
        return Task.Run(() => Collect(reader, cancellationToken), cancellationToken);
    }

    private DiscoveryResult<ScheduledTaskInfo> Collect(ITaskSchedulerReader source, CancellationToken ct)
    {
        var items = new List<ScheduledTaskInfo>();
        var issues = new List<DiscoveryIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var capped = false;
        try
        {
            source.Enumerate(result =>
            {
                ct.ThrowIfCancellationRequested();
                if (result.Task is null)
                {
                    issues.Add(DiscoverySupport.Issue(SourceName, result.Error is null ? ErrorCode.DiscoveryItemUnreadable : DiscoverySupport.Classify(result.Error), result.FailedPath));
                    return true;
                }
                try
                {
                    var info = Map(result.Task);
                    if (!seen.Add(info.StableId)) return true;
                    if (items.Count >= maxItems)
                    {
                        capped = true;
                        return false;
                    }
                    items.Add(info);
                }
                catch (Exception e) when (!DiscoverySupport.IsCancellation(e))
                {
                    issues.Add(DiscoverySupport.FromException(SourceName, e, result.Task.Path));
                }
                return true;
            }, ct);
        }
        catch (Exception e) when (!DiscoverySupport.IsCancellation(e))
        {
            // Source-level failure (service unavailable, COM not registered, denied). Keep what was read.
            var code = DiscoverySupport.Classify(e);
            issues.Add(DiscoverySupport.Issue(SourceName, code == ErrorCode.DiscoveryItemUnreadable ? ErrorCode.DiscoveryUnavailable : code));
        }
        if (capped) issues.Add(DiscoverySupport.Issue(SourceName, ErrorCode.DiscoveryItemUnreadable, "item-cap"));
        items.Sort((a, b) => string.CompareOrdinal(a.StableId, b.StableId));
        return new(items, issues, clock());
    }

    internal static ScheduledTaskInfo Map(RawTask t)
    {
        var path = DiscoverySupport.Cap(t.Path, 1024)!;
        var command = t.ActionPath is null ? null
            : DiscoverySupport.Cap(string.IsNullOrWhiteSpace(t.ActionArguments) ? t.ActionPath : t.ActionPath + " " + t.ActionArguments);
        var author = string.IsNullOrWhiteSpace(t.Author) ? null : DiscoverySupport.Cap(t.Author.Trim(), 256);
        return new ScheduledTaskInfo("task:" + DiscoverySupport.Fold(path), path, DiscoverySupport.Cap(t.Name, 256)!, MapState(t.State), t.Hidden, author,
            ToUtc(t.LastRun), ToUtc(t.NextRun), t.LogonOrBoot, command, IsMicrosoft(path, author));
    }

    internal static TaskRunState MapState(int state) => state switch
    {
        1 => TaskRunState.Disabled, 2 => TaskRunState.Queued, 3 => TaskRunState.Ready, 4 => TaskRunState.Running, _ => TaskRunState.Unknown,
    };

    /// <summary>The scheduler reports "never" as a date in 1899/1900 (an unrun task has a last run time of 1999-11-30). Unspecified kinds are treated as local time.</summary>
    internal static DateTimeOffset? ToUtc(DateTime? value)
    {
        if (value is not { } v || v.Year < 2000 || v.Year > 9000) return null;
        try { return new DateTimeOffset(v.Kind == DateTimeKind.Utc ? v : v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Local) : v).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static bool IsMicrosoft(string path, string? author) =>
        path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ||
        (author is not null && author.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase));
}
