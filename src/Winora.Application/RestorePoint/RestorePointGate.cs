// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public enum RestorePointChoice { CreateAndContinue, ContinueWithout, Cancel }

/// <summary>What the question shows. <see cref="CanCreate"/> is false when Winora cannot create a restore point now (no administrator rights or System Protection is off by policy); the question then offers only to continue or to cancel.</summary>
public sealed record RestorePointAsk(string Area, RestorePointStatus Status)
{
    public bool CanCreate => Status.CanCreate && Status.Protection != RestoreProtection.Disabled;
}

/// <summary>Asks the user. The Desktop window implements it with a dialog; a host without a dialog answers ContinueWithout.</summary>
public interface IRestorePointPrompt
{
    Task<RestorePointChoice> AskAsync(RestorePointAsk ask, CancellationToken cancellationToken);
}

/// <param name="Proceed">False only when the user cancelled.</param>
/// <param name="Created">The result of a restore point made now; null when none was requested.</param>
public sealed record RestorePointGateResult(bool Proceed, RestorePointResult? Created, bool Asked)
{
    public static RestorePointGateResult Go { get; } = new(true, null, false);
}

/// <summary>Called by a page before its first change of the machine in a session.</summary>
public interface IRestorePointGate
{
    /// <summary>Asks once per session, creates a restore point if the user wants one and returns whether the change may go on. A restore point that fails never stops the change by itself.</summary>
    Task<RestorePointGateResult> BeforeChangeAsync(string area, CancellationToken cancellationToken = default);
}

/// <summary>
/// The session rule of decision DEC-IW-029: the first change of the machine in a session asks whether to create a restore point first. "Create" and "Continue without" are remembered for the rest of the
/// session; "Cancel" is not (it stops only that change). A failed restore point is reported to the caller and does not ask again.
/// </summary>
public sealed class SessionRestorePointGate(RestorePointService service, IRestorePointPrompt prompt) : IRestorePointGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile bool decided;

    public bool HasDecided => decided;

    public async Task<RestorePointGateResult> BeforeChangeAsync(string area, CancellationToken cancellationToken = default)
    {
        if (decided) return RestorePointGateResult.Go;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (decided) return RestorePointGateResult.Go;
            var status = await Task.Run(service.Status, cancellationToken).ConfigureAwait(false);
            var choice = await prompt.AskAsync(new RestorePointAsk(area, status), cancellationToken).ConfigureAwait(false);
            switch (choice)
            {
                case RestorePointChoice.Cancel:
                    return new(false, null, true);
                case RestorePointChoice.ContinueWithout:
                    decided = true;
                    return new(true, null, true);
                default:
                    var result = await service.CreateAsync("before changes on the " + area + " page", cancellationToken).ConfigureAwait(false);
                    decided = true;
                    return new(true, result, true);
            }
        }
        finally { gate.Release(); }
    }
}
