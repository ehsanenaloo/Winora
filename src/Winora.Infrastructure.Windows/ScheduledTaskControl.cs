// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using Winora.Application;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// The one file that changes Task Scheduler. It uses the late-bound Schedule.Service COM object: enable or disable by the Enabled property, run, stop, delete one task
/// and register a task from its definition. A task path is a COM argument only; it is never part of a command line and no program is started from here. The callers (the service in the
/// Application layer) have already checked the safety policy, the journal and the compare-and-set; this class does no policy of its own. A source scan keeps these calls out of every other file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskControl : IScheduledTaskControl
{
    private const int TaskCreate = 2;
    private const int LogonFromDefinition = 0;

    public TaskControlResult SetEnabled(string path, bool enabled, string? expectedFingerprint = null) => OnTask(path, task => { Check(task, expectedFingerprint); task.Enabled = enabled; });

    public TaskControlResult Run(string path) => OnTask(path, task =>
    {
        object? instance = null;
        try { instance = task.Run(null); } finally { TaskSchedulerErrors.Release(instance); }
    });

    public TaskControlResult Stop(string path) => OnTask(path, task => { task.Stop(0); });

    public TaskControlResult Delete(string path, string? expectedFingerprint = null)
    {
        var (folderPath, name) = TaskSchedulerErrors.Split(path);
        return WithService(service =>
        {
            dynamic folder = service.GetFolder(folderPath);
            try
            {
                if (expectedFingerprint is not null)
                {
                    dynamic task = folder.GetTask(name);
                    try { Check(task, expectedFingerprint); } finally { TaskSchedulerErrors.Release(task); }
                }
                folder.DeleteTask(name, 0);
            }
            finally { TaskSchedulerErrors.Release(folder); }
        });
    }

    public TaskControlResult Register(string path, string xml) => WithService(service =>
    {
        dynamic root = service.GetFolder("\\");
        object? registered = null;
        try
        {
            // TASK_CREATE fails when the path is taken, so a task is never replaced. The principal comes from the definition itself.
            registered = root.RegisterTask(path, xml, TaskCreate, null, null, LogonFromDefinition, null);
        }
        finally { TaskSchedulerErrors.Release(registered); TaskSchedulerErrors.Release(root); }
    });

    private TaskControlResult OnTask(string path, Action<dynamic> action)
    {
        var (folderPath, name) = TaskSchedulerErrors.Split(path);
        return WithService(service =>
        {
            dynamic folder = service.GetFolder(folderPath);
            try
            {
                dynamic task = folder.GetTask(name);
                try { action(task); } finally { TaskSchedulerErrors.Release(task); }
            }
            finally { TaskSchedulerErrors.Release(folder); }
        });
    }

    private sealed class StaleTaskException : Exception { }

    /// <summary>The definition must still be the one that was reviewed, read at the moment of the change.</summary>
    private static void Check(dynamic task, string? expectedFingerprint)
    {
        if (expectedFingerprint is null) return;
        string xml = task.Xml;
        if (ScheduledTaskService.Fingerprint(xml) != expectedFingerprint) throw new StaleTaskException();
    }

    private static TaskControlResult WithService(Action<dynamic> work)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", false) ?? throw new PlatformNotSupportedException();
            dynamic service = Activator.CreateInstance(type)!;
            try
            {
                service.Connect();
                work(service);
                return TaskControlResult.Success;
            }
            finally { TaskSchedulerErrors.Release(service); }
        }
        catch (StaleTaskException) { return TaskControlResult.Fail(ScheduledTaskCode.Stale); }
        catch (Exception e) when (TaskSchedulerErrors.IsExpected(e) || e is PlatformNotSupportedException)
        {
            return TaskControlResult.Fail(TaskSchedulerErrors.Code(e));
        }
    }
}
