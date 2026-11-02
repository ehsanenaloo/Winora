// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>What the page shows before the one confirmation: the exact command, in plain text.</summary>
public sealed record RepairPreview(RepairRequest Request, string? Command, RepairToolInfo? Info, string? RefusalCode)
{
    public bool IsRefused => RefusalCode is not null;
}

/// <param name="RecordLost">The run ended but its end line could not be written; the history shows it as interrupted.</param>
public sealed record RepairResult(RepairOutcome Outcome, string Code, RepairRun? Run, Guid? Id, bool RecordLost = false)
{
    public bool Started => Run is not null && Run.Outcome != RepairOutcome.NotStarted;
    public bool Ok => Outcome == RepairOutcome.Succeeded;
}

/// <summary>
/// Runs one repair tool at a time (decision DEC-IW-041). A run has a preview, a journal line before the program starts and one after it ends, a timeout and cancellation in the runner, and no undo:
/// repairs have no earlier state to go back to. The service never starts two runs together, and it does nothing without administrator rights.
/// </summary>
public sealed class RepairService(IRepairRunner runner, IRepairJournal journal, IElevationState elevation, TimeProvider? clock = null)
{
    private const int HistoryRead = int.MaxValue;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);

    public bool IsElevated => elevation.IsElevated;
    public bool UnsafeLocation => elevation.UnsafeLocation;
    public bool IsRunning => gate.CurrentCount == 0;
    public IReadOnlyList<string> FixedVolumes() => runner.FixedVolumes();
    public IReadOnlyList<RepairRecord> History(int max) => journal.History(max);
    public RepairRecord? Find(Guid id) => journal.History(HistoryRead).FirstOrDefault(r => r.Begin.Id == id);

    public RepairPreview Preview(RepairRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invalid = RepairRules.Validate(request, runner.FixedVolumes());
        if (invalid is not null) return new(request, null, null, invalid);
        var command = runner.Preview(request);
        return command is null ? new(request, null, null, RepairCode.InvalidRequest) : new(request, command, RepairTools.Find(request.Tool), null);
    }

    /// <summary>
    /// Runs the request that the caller has shown and the user has confirmed. <paramref name="previousState"/> and <paramref name="revertOf"/> are recorded for feature actions so that an undo can find them.
    /// The journal line is written first; when it cannot be written, nothing starts. <paramref name="verify"/> checks a successful run against the machine before the end line is written (it gets a function that lists the features while the run still holds the gate):
    /// the end line is then the verified result, never a provisional success.
    /// </summary>
    public async Task<RepairResult> RunAsync(RepairRequest request, IProgress<RepairProgress>? progress = null, string? previousState = null, Guid? revertOf = null, CancellationToken cancellationToken = default, Func<RepairRun, Func<Task<RepairRun>>, Task<RepairRun>>? verify = null)
    {
        var preview = Preview(request);
        if (preview.IsRefused) return Refused(preview.RefusalCode!);
        if (!elevation.IsElevated) return Refused(RepairCode.NeedsAdmin);
        if (!gate.Wait(0)) return Refused(RepairCode.Busy);
        try
        {
            if (cancellationToken.IsCancellationRequested) return Refused(RepairCode.Cancelled);
            var begin = new RepairBegin(Guid.NewGuid(), request.Tool, request.Argument, preview.Command!, previousState, revertOf);
            var journaled = request.Tool != RepairTool.FeatureList;
            if (journaled)
            {
                try { journal.Begin(begin, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Refused(RepairCode.JournalFailed); }
            }
            RepairRun run;
            try { run = await runner.RunAsync(request, progress, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                run = new(RepairOutcome.Failed, null, RepairCode.StartFailed, "", "", false, false);
            }
            if (verify is not null && run.Outcome == RepairOutcome.Succeeded)
            {
                try { run = await verify(run, () => runner.RunAsync(new(RepairTool.FeatureList), null, CancellationToken.None)).ConfigureAwait(false); }
                catch (Exception e) when (e is not OutOfMemoryException) { run = run with { Outcome = RepairOutcome.Failed, Code = RepairCode.VerifyFailed }; }
            }
            var lost = false;
            if (journaled)
            {
                var end = new RepairEnd(run.Outcome, run.ExitCode, run.Code, run.RestartNeeded, Clip(run.Summary));
                try { journal.Complete(begin.Id, end, clock.GetUtcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    try { journal.Complete(begin.Id, end, clock.GetUtcNow()); }
                    catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { lost = true; } // the history shows the run as interrupted
                }
            }
            return new(run.Outcome, run.Code, run, begin.Id, lost);
        }
        finally { gate.Release(); }
    }

    private static string Clip(string text) => text.Length <= 2000 ? text : text[..2000];

    private static RepairResult Refused(string code) => new(RepairOutcome.NotStarted, code, null, null);
}
