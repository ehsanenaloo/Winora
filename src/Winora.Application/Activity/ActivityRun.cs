// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application.Activity;

/// <summary>
/// The handle a page keeps for one run in the <see cref="ActivityService"/>. Disposing a handle that was not finished ends it as Ok without a notification, so a failure path that
/// forgets to finish never leaves a run behind. A handle without a service, or one that was refused, does nothing.
/// </summary>
public sealed class ActivityRun : IDisposable
{
    private readonly ActivityService? service;
    private readonly Guid id;
    private bool finished;

    internal ActivityRun(ActivityService service, RunStartResult start)
    {
        this.service = service;
        Started = start.Started;
        if (start.Run is { } run) id = run.Id;
        else finished = true;
        Blocking = start.Blocking;
    }

    private ActivityRun() { Started = true; finished = true; }

    /// <summary>A handle for a page that has no service (a page created alone). It is always "started" and does nothing.</summary>
    public static ActivityRun None { get; } = new();

    /// <summary>False when another run that changes the system is in the way. The caller then does not start its work.</summary>
    public bool Started { get; }

    public RunSnapshot? Blocking { get; }

    public Guid Id => id;

    public bool Report(double? share) => service is not null && !finished && service.Report(id, share);

    /// <summary>Ends the run. <paramref name="message"/> is the notification text (the title when empty). <paramref name="quiet"/> ends without any notification.</summary>
    public void Finish(RunResult result, string? message = null, bool quiet = false)
    {
        if (finished) return;
        finished = true;
        service?.Finish(id, result, message, notify: !quiet && result != RunResult.Stopped);
    }

    /// <summary>A handle that is let go without a result was abandoned (an early return, a cancelled question): it ends as stopped, never as a success the owner did not state.</summary>
    public void Dispose() => Finish(RunResult.Stopped, quiet: true);
}
