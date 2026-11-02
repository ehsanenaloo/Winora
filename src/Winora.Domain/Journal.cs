// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Domain;

/// <summary>A domain-level refusal with a stable code (message equals the code). Derives from InvalidOperationException so existing handlers keep working.</summary>
public sealed class OperationException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
    public WinoraError Error => WinoraError.From(Code);
}

/// <summary>A persistence-layer failure (busy database, I/O or SQL error) with a stable code. Derives from IOException so the engine treats every store fault uniformly.</summary>
public sealed class StoreException(string code, Exception? inner = null) : IOException(code, inner)
{
    public string Code { get; } = code;
    public WinoraError Error => WinoraError.From(Code);
}

/// <summary>Actual simulated resource value plus its monotonically increasing write revision (detects change-away-and-back).</summary>
public sealed record ResourceState(bool Value, long Revision);

/// <summary>Binds a preview to the exact state it observed. Apply fails with plan.stale if the resource moved.</summary>
public sealed record PlanToken(bool Current, long Revision, bool Desired);

/// <summary>One row of the insert-only transition ledger.</summary>
public sealed record OperationEvent(long Sequence, Guid OperationId, long Revision, OperationState? FromState, bool? FromCompensationPrepared,
    OperationState ToState, bool ToCompensationPrepared, string Code, DateTimeOffset AtUtc);

/// <summary>An active (Prepared/Executing/compensating) journal row as stored; Record is null when the payload cannot be trusted.</summary>
public sealed record JournalEntry(Guid Id, long Revision, OperationRecord? Record, string? Fault);

public sealed record QuarantinedEntry(Guid Id, string Code, long Revision);
