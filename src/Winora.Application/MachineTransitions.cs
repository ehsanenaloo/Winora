// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

/// <summary>
/// The legal journal transitions of machine rows (technical-docs/trust.md 11). A state is the triple (state, compensation prepared, compensation committed);
/// the committed flag exists only together with the prepared flag. The store enforces this table for every write.
/// </summary>
public static class MachineTransitions
{
    private readonly record struct S(OperationState State, bool Prepared, bool Committed);

    private static S Row(OperationState state) => new(state, false, false);
    private static S Prep(OperationState state) => new(state, true, false);
    private static S Commit(OperationState state) => new(state, true, true);

    private static readonly HashSet<(S From, S To)> Allowed =
    [
        // forward path (11.2); Prepared -> AlreadyConfigured when the worker reports willChange = 0 or recovery resolves B5
        (Row(OperationState.Prepared), Row(OperationState.Executing)),
        (Row(OperationState.Prepared), Row(OperationState.Failed)),
        (Row(OperationState.Prepared), Row(OperationState.AlreadyConfigured)),
        (Row(OperationState.Prepared), Row(OperationState.Partial)),
        (Row(OperationState.Executing), Row(OperationState.Applied)),
        (Row(OperationState.Executing), Row(OperationState.PendingRestart)),
        (Row(OperationState.Executing), Row(OperationState.Failed)),
        (Row(OperationState.Executing), Row(OperationState.Partial)),
        // drift check and review (11.6, 11.5)
        (Row(OperationState.Applied), Row(OperationState.Partial)),
        (Row(OperationState.PendingRestart), Row(OperationState.Partial)),
        (Row(OperationState.AlreadyConfigured), Row(OperationState.Partial)),
        // revert path (11.3): C1 prepare, C3 commit flag, C4/C5 outcome, abandonment
        (Row(OperationState.Applied), Prep(OperationState.Applied)),
        (Row(OperationState.PendingRestart), Prep(OperationState.PendingRestart)),
        (Prep(OperationState.Applied), Commit(OperationState.Applied)),
        (Prep(OperationState.PendingRestart), Commit(OperationState.PendingRestart)),
        (Prep(OperationState.Applied), Row(OperationState.Applied)),
        (Prep(OperationState.PendingRestart), Row(OperationState.PendingRestart)),
        (Prep(OperationState.Applied), Row(OperationState.Partial)),
        (Prep(OperationState.PendingRestart), Row(OperationState.Partial)),
        (Commit(OperationState.Applied), Row(OperationState.Applied)),
        (Commit(OperationState.PendingRestart), Row(OperationState.PendingRestart)),
        (Commit(OperationState.Applied), Row(OperationState.Reverted)),
        (Commit(OperationState.PendingRestart), Row(OperationState.Reverted)),
        (Commit(OperationState.Applied), Row(OperationState.Partial)),
        (Commit(OperationState.PendingRestart), Row(OperationState.Partial)),
    ];

    public static bool IsAllowed(OperationState from, bool fromPrepared, bool fromCommitted, OperationState to, bool toPrepared, bool toCommitted) =>
        IsConsistent(fromPrepared, fromCommitted) && IsConsistent(toPrepared, toCommitted) &&
        Allowed.Contains((new S(from, fromPrepared, fromCommitted), new S(to, toPrepared, toCommitted)));

    /// <summary>The committed flag never exists without the prepared flag.</summary>
    public static bool IsConsistent(bool prepared, bool committed) => prepared || !committed;

    /// <summary>A machine row is created at Prepared (the first operation's row is committed before the worker is launched, trust.md 11.1 rule 2).</summary>
    public static bool IsAllowedInitial(OperationState state, bool prepared, bool committed) =>
        state == OperationState.Prepared && !prepared && !committed;

    /// <summary>Active rows block new work and need recovery (the global single-active rule spans user and machine rows).</summary>
    public static bool IsActive(OperationState state, bool compensationPrepared) =>
        compensationPrepared || state is OperationState.Prepared or OperationState.Executing;
}
