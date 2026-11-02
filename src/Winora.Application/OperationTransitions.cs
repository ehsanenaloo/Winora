// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

/// <summary>The single table of legal journal transitions (technical-docs/operations.md). The store enforces it for every write.</summary>
public static class OperationTransitions
{
    private static readonly HashSet<(OperationState From, bool FromComp, OperationState To, bool ToComp)> Allowed =
    [
        (OperationState.Planned, false, OperationState.Prepared, false),
        (OperationState.Planned, false, OperationState.Unsupported, false),
        (OperationState.Planned, false, OperationState.AlreadyConfigured, false),
        (OperationState.Prepared, false, OperationState.Executing, false),
        (OperationState.Prepared, false, OperationState.Failed, false),
        (OperationState.Prepared, false, OperationState.Applied, false),
        (OperationState.Prepared, false, OperationState.Partial, false),
        (OperationState.Executing, false, OperationState.Applied, false),
        (OperationState.Executing, false, OperationState.Failed, false),
        (OperationState.Executing, false, OperationState.Partial, false),
        (OperationState.Executing, false, OperationState.PendingRestart, false),
        (OperationState.Applied, false, OperationState.Applied, true),
        (OperationState.Applied, false, OperationState.Partial, false),
        (OperationState.PendingRestart, false, OperationState.PendingRestart, true),
        (OperationState.PendingRestart, false, OperationState.Partial, false),
        // Shell refresh after a verified registry write (settings only): journaled intent, then outcome.
        (OperationState.PendingRestart, false, OperationState.Applied, false),
        (OperationState.PendingRestart, false, OperationState.PendingRestart, false),
        (OperationState.Reverted, false, OperationState.Reverted, false),
        (OperationState.Applied, true, OperationState.Applied, false),
        (OperationState.Applied, true, OperationState.Reverted, false),
        (OperationState.Applied, true, OperationState.Partial, false),
        (OperationState.PendingRestart, true, OperationState.PendingRestart, false),
        (OperationState.PendingRestart, true, OperationState.Reverted, false),
        (OperationState.PendingRestart, true, OperationState.Partial, false),
    ];
    public static bool IsAllowed(OperationState from, bool fromCompensation, OperationState to, bool toCompensation) =>
        Allowed.Contains((from, fromCompensation, to, toCompensation));
    public static bool IsAllowedInitial(OperationState state, bool compensationPrepared) =>
        !compensationPrepared && state is OperationState.Planned or OperationState.Prepared or OperationState.AlreadyConfigured or OperationState.Unsupported;
    /// <summary>Active rows block new work and need recovery.</summary>
    public static bool IsActive(OperationState state, bool compensationPrepared) =>
        compensationPrepared || state is OperationState.Prepared or OperationState.Executing;
}
