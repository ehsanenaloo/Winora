// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Winora.Application;
using Winora.Domain;
using Winora.Infrastructure.Persistence;

namespace Winora.Desktop;

/// <summary>Result shown to the user after a simulated operation. Codes are stable; text is resolved from resources.</summary>
public sealed record OutcomeInfo(OperationState? State, string Code, bool IsError, Guid? OperationId, WinoraError? Error = null, Func<Task>? Retry = null);

/// <summary>
/// Shared, UI-thread-owned view of the simulation. Every blocking store call runs on the thread pool
/// with the engine API unchanged; only one operation may be in flight, which also prevents double apply.
/// </summary>
public sealed class SimulationModel(SqliteOperationStore store, SimulationEngine engine, ErrorPresenter errors, ILogger<SimulationModel> logger) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool actual;
    private bool? pending;
    private bool reviewed;
    private bool busy;
    private IReadOnlyList<OperationRecord> operations = [];
    private OutcomeInfo? outcome;
    private PlanToken? planToken;

    public bool Actual { get => actual; private set => SetProperty(ref actual, value); }
    public bool? Pending { get => pending; private set => SetProperty(ref pending, value); }
    public bool Reviewed { get => reviewed; private set => SetProperty(ref reviewed, value); }
    public bool IsBusy { get => busy; private set => SetProperty(ref busy, value); }
    /// <summary>Newest first.</summary>
    public IReadOnlyList<OperationRecord> Operations { get => operations; private set => SetProperty(ref operations, value); }
    public OutcomeInfo? Outcome { get => outcome; private set => SetProperty(ref outcome, value); }
    public bool Loaded { get; private set; }
    public OperationRecord? RevertCandidate => operations.FirstOrDefault(o => o.State is OperationState.Applied or OperationState.PendingRestart && !o.CompensationPrepared);
    public event EventHandler? Changed;

    public void Stage(bool desired) { if (busy) return; Pending = desired; Reviewed = false; planToken = null; Raise(); }
    public void Discard() { if (busy) return; Pending = null; Reviewed = false; planToken = null; Raise(); }
    /// <summary>Routes a failure from outside the model (dialog, command wiring) through the same classification and surface.</summary>
    public void Fail(Exception exception, string context)
    {
        var error = errors.Report(exception, context);
        if (error is not null) Outcome = new(null, error.Code, true, null, error);
        Raise();
    }
    public void DismissOutcome() { Outcome = null; Raise(); }

    /// <summary>Recovers interrupted operations, then reads the simulated value and journal off the UI thread.</summary>
    public Task LoadAsync() => RunAsync(async token =>
    {
        var snapshot = await Task.Run(() => { engine.Recover(); return Read(); }, token);
        Loaded = true;
        return (snapshot, null);
    });

    public Task RefreshAsync() => RunAsync(async token => (await Task.Run(Read, token), null));

    public async Task<SimulationPlan?> ReviewAsync()
    {
        if (busy || pending is not bool desired) return null;
        SimulationPlan? plan = null;
        await RunAsync(async token =>
        {
            plan = await Task.Run(() => engine.Plan(desired), token);
            planToken = plan.Token; // Apply fails with plan.stale if the value moved after this review.
            Reviewed = true;
            return (null, null);
        });
        return plan;
    }

    public Task ApplyAsync()
    {
        if (busy || pending is not bool desired || !reviewed) return Task.CompletedTask;
        return RunAsync(async token =>
        {
            var bound = planToken;
            var (snapshot, record) = await Task.Run(() => { var r = engine.Apply(desired, bound, token); return (Read(), r); }, token);
            Pending = null; Reviewed = false; planToken = null;
            logger.LogInformation("Simulation {OperationId} completed with {State}", record.Id, record.State);
            return (snapshot, ToOutcome(record));
        }, retryable: false);
    }

    public Task RevertAsync(Guid id)
    {
        if (busy) return Task.CompletedTask;
        return RunAsync(async token =>
        {
            var (snapshot, record) = await Task.Run(() => { var r = engine.Rollback(id); return (Read(), r); }, token);
            logger.LogInformation("Simulation rollback {OperationId} completed with {State}", record.Id, record.State);
            return (snapshot, ToOutcome(record));
        });
    }

    private OutcomeInfo ToOutcome(OperationRecord record)
    {
        var isError = record.State is OperationState.Failed or OperationState.Partial or OperationState.Unsupported;
        return new(record.State, record.Code, isError, record.Id, isError ? errors.ForCode(record.Code, record.Id) : null);
    }

    private (bool Actual, IReadOnlyList<OperationRecord> Operations) Read() =>
        (store.ReadActual(), store.ListOperations().OrderByDescending(o => o.CreatedAtUtc).ToList());

    private async Task RunAsync(Func<CancellationToken, Task<((bool Actual, IReadOnlyList<OperationRecord> Operations)? Snapshot, OutcomeInfo? Outcome)>> work, bool retryable = true)
    {
        if (busy) return;
        IsBusy = true; Raise();
        try
        {
            var (snapshot, result) = await work(lifetime.Token);
            if (snapshot is { } s) { Actual = s.Actual; Operations = s.Operations; OnPropertyChanged(nameof(RevertCandidate)); }
            if (result is not null) Outcome = result;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Classified, logged once with a reference id and localized by the presenter; cancellation is neutral.
            var error = errors.Report(ex, "Simulation action");
            if (error is not null)
                Outcome = new(null, error.Code, true, null, error, retryable && error.Retryable && !ErrorPresenter.OffersReview(error) ? () => RunAsync(work, retryable) : null);
            Reviewed = false; planToken = null;
            try { var s = await Task.Run(Read); Actual = s.Actual; Operations = s.Operations; OnPropertyChanged(nameof(RevertCandidate)); }
            catch (Exception readFailure) { logger.LogError(readFailure, "Simulation state could not be re-read after a failure"); }
        }
        finally { IsBusy = false; Raise(); }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
