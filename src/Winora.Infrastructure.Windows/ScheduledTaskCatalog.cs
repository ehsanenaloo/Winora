// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Windows;

/// <summary>Maps Task Scheduler errors to the stable codes of <see cref="ScheduledTaskCode"/>.</summary>
[SupportedOSPlatform("windows")]
internal static class TaskSchedulerErrors
{
    private const int AccessDenied = unchecked((int)0x80070005), FileNotFound = unchecked((int)0x80070002), PathNotFound = unchecked((int)0x80070003), AlreadyExists = unchecked((int)0x800700B7), FileExists = unchecked((int)0x80070050);

    // Late-bound COM calls surface an HRESULT as different exception types (a missing file becomes FileNotFoundException, access denied becomes UnauthorizedAccessException), so the code is read from the HRESULT too.
    public static string Code(Exception e) => e switch
    {
        UnauthorizedAccessException => ScheduledTaskCode.AccessDenied,
        _ when e.HResult == AccessDenied => ScheduledTaskCode.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => ScheduledTaskCode.NotFound,
        _ when e.HResult is FileNotFound or PathNotFound => ScheduledTaskCode.NotFound,
        _ when e.HResult is AlreadyExists or FileExists => ScheduledTaskCode.Exists,
        _ => ScheduledTaskCode.Failed,
    };

    public static bool IsExpected(Exception e) => e is COMException or UnauthorizedAccessException or IOException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    public static void Release(object? com)
    {
        try { if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); } catch (ArgumentException) { }
    }

    /// <summary>The folder part and the name of a task path (<c>\Folder\Name</c> gives <c>\Folder</c> and <c>Name</c>; a task in the root gives <c>\</c>).</summary>
    public static (string Folder, string Name) Split(string path)
    {
        var cut = path.LastIndexOf('\\');
        return (cut <= 0 ? "\\" : path[..cut], path[(cut + 1)..]);
    }
}

/// <summary>
/// Read side of the Task Scheduler 2.0 COM API (late bound, ProgID Schedule.Service, no package). It lists every task with its triggers, actions and run-as account, and looks up one task
/// for the compare-and-set of a change. Nothing here changes a task and no action is ever started.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskCatalog : IScheduledTaskCatalog
{
    private const string SourceName = "ScheduledTaskEntry";
    private const int MaxDepth = 16;
    private const int TaskEnumHidden = 1;
    private const int ActionExec = 0, ActionComHandler = 5, ActionEmail = 6, ActionMessage = 7;
    private const int RunLevelHighest = 1;
    private readonly Func<DateTimeOffset> clock;

    public ScheduledTaskCatalog() : this(() => DateTimeOffset.UtcNow) { }

    internal ScheduledTaskCatalog(Func<DateTimeOffset> clock) { this.clock = clock; }

    public Task<DiscoveryResult<ScheduledTaskEntry>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Collect(cancellationToken), cancellationToken);
    }

    private DiscoveryResult<ScheduledTaskEntry> Collect(CancellationToken ct)
    {
        var items = new List<ScheduledTaskEntry>();
        var issues = new List<DiscoveryIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", false) ?? throw new PlatformNotSupportedException();
            dynamic service = Activator.CreateInstance(type)!;
            try
            {
                service.Connect();
                dynamic root = service.GetFolder("\\");
                try { Walk(root, 0, items, issues, seen, ct); } finally { TaskSchedulerErrors.Release(root); }
            }
            finally { TaskSchedulerErrors.Release(service); }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var code = DiscoverySupport.Classify(e);
            issues.Add(DiscoverySupport.Issue(SourceName, code == ErrorCode.DiscoveryItemUnreadable ? ErrorCode.DiscoveryUnavailable : code));
        }
        items.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new(items, issues, clock());
    }

    private static void Walk(dynamic folder, int depth, List<ScheduledTaskEntry> items, List<DiscoveryIssue> issues, HashSet<string> seen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var folderPath = "?";
        try { folderPath = (string)folder.Path; } catch (COMException) { }
        try
        {
            dynamic tasks = folder.GetTasks(TaskEnumHidden);
            foreach (dynamic task in (IEnumerable)tasks)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var entry = Read((object)task, out string failedPath);
                    if (entry is null) issues.Add(DiscoverySupport.Issue(SourceName, ErrorCode.DiscoveryItemUnreadable, failedPath));
                    else if (seen.Add(entry.Path) && items.Count < DiscoverySupport.DefaultMaxItems) items.Add(entry);
                }
                finally { TaskSchedulerErrors.Release(task); }
            }
        }
        catch (Exception e) when (TaskSchedulerErrors.IsExpected(e))
        {
            issues.Add(DiscoverySupport.FromException(SourceName, e, folderPath));
        }
        if (depth >= MaxDepth) return;
        try
        {
            dynamic folders = folder.GetFolders(0);
            foreach (dynamic child in (IEnumerable)folders)
            {
                try { Walk(child, depth + 1, items, issues, seen, ct); }
                finally { TaskSchedulerErrors.Release(child); }
            }
        }
        catch (Exception e) when (TaskSchedulerErrors.IsExpected(e))
        {
            issues.Add(DiscoverySupport.FromException(SourceName, e, folderPath));
        }
    }

    private static ScheduledTaskEntry? Read(dynamic task, out string failedPath)
    {
        failedPath = "?";
        try
        {
            string path = DiscoverySupport.Cap((string)task.Path, 1024)!;
            failedPath = path;
            int state = (int)task.State;
            bool enabled = (bool)task.Enabled;
            DateTime last = task.LastRunTime, next = task.NextRunTime;
            int result = (int)task.LastTaskResult;
            string xml = (string)task.Xml;
            dynamic definition = task.Definition;
            bool hidden = (bool)definition.Settings.Hidden;
            string? author = null, description = null, runAs = null;
            var elevated = false;
            try { author = (string?)definition.RegistrationInfo.Author; } catch (COMException) { }
            try { description = (string?)definition.RegistrationInfo.Description; } catch (COMException) { }
            try
            {
                dynamic principal = definition.Principal;
                runAs = (string?)principal.UserId;
                if (string.IsNullOrWhiteSpace(runAs)) runAs = (string?)principal.GroupId;
                elevated = (int)principal.RunLevel == RunLevelHighest;
            }
            catch (COMException) { }
            var triggers = new List<TaskTriggerInfo>();
            foreach (dynamic trigger in (IEnumerable)definition.Triggers)
            {
                bool triggerEnabled = true;
                try { triggerEnabled = (bool)trigger.Enabled; } catch (COMException) { }
                string? at = null;
                try { at = TimeOfDay((string?)trigger.StartBoundary); } catch (COMException) { }
                triggers.Add(new(TriggerKind((int)trigger.Type), triggerEnabled, at));
            }
            var actions = new List<TaskActionInfo>();
            foreach (dynamic action in (IEnumerable)definition.Actions)
            {
                var type = (int)action.Type;
                if (type == ActionExec) actions.Add(new("exec", DiscoverySupport.Cap((string?)action.Path), DiscoverySupport.Cap((string?)action.Arguments)));
                else actions.Add(new(type switch { ActionComHandler => "com_handler", ActionEmail => "email", ActionMessage => "message", _ => "other" }, null, null));
            }
            return new ScheduledTaskEntry(path, DiscoverySupport.Cap((string)task.Name, 256)!, ScheduledTaskDiscovery.MapState(state), enabled, hidden,
                string.IsNullOrWhiteSpace(author) ? null : DiscoverySupport.Cap(author.Trim(), 256), string.IsNullOrWhiteSpace(description) ? null : DiscoverySupport.Cap(description.Trim(), 2048),
                ScheduledTaskDiscovery.ToUtc(last), ScheduledTaskDiscovery.ToUtc(next), result, string.IsNullOrWhiteSpace(runAs) ? null : DiscoverySupport.Cap(runAs, 256), elevated, triggers, actions, xml.Length);
        }
        catch (Exception e) when (TaskSchedulerErrors.IsExpected(e)) { return null; }
    }

    /// <summary>The time of day written in a start boundary such as 2024-01-01T09:00:00 or 2024-01-01T09:00:00+01:00, as HH:mm, without any time zone conversion; null when there is none.</summary>
    internal static string? TimeOfDay(string? boundary)
    {
        if (boundary is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(boundary, @"T([01]\d|2[0-3]):([0-5]\d)", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        return match.Success ? match.Groups[1].Value + ":" + match.Groups[2].Value : null;
    }

    internal static string TriggerKind(int type) => type switch
    {
        0 => "event", 1 => "time", 2 => "daily", 3 => "weekly", 4 => "monthly", 5 => "monthly_dow", 6 => "idle", 7 => "registration", 8 => "boot", 9 => "logon", 11 => "session_state", _ => "custom",
    };

    public TaskLookup Observe(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", false) ?? throw new PlatformNotSupportedException();
            dynamic service = Activator.CreateInstance(type)!;
            try
            {
                service.Connect();
                var (folderPath, name) = TaskSchedulerErrors.Split(path);
                dynamic folder = service.GetFolder(folderPath);
                try
                {
                    dynamic task = folder.GetTask(name);
                    try { return TaskLookup.Of(new((bool)task.Enabled, ScheduledTaskDiscovery.MapState((int)task.State), (string)task.Xml)); }
                    finally { TaskSchedulerErrors.Release(task); }
                }
                finally { TaskSchedulerErrors.Release(folder); }
            }
            finally { TaskSchedulerErrors.Release(service); }
        }
        catch (Exception e) when (TaskSchedulerErrors.IsExpected(e) || e is PlatformNotSupportedException)
        {
            var code = TaskSchedulerErrors.Code(e);
            return code == ScheduledTaskCode.NotFound ? TaskLookup.Missing : TaskLookup.Fail(code == ScheduledTaskCode.Exists ? ScheduledTaskCode.Failed : code);
        }
    }
}
