// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol;

/// <summary>Closed detail tokens used by the protocol layer.</summary>
public static class ProtocolDetail
{
    public const string Handshake = "handshake";
    public const string Squatted = "squatted";
    public const string Framing = "framing";
    public const string Size = "size";
    public const string Counter = "counter";
    public const string Tag = "tag";
    public const string State = "state";
    public const string Code = "code";
    public const string Flavor = "flavor";
    public const string Version = "version";
    public const string BudgetExhausted = "budget_exhausted";
}

/// <summary>
/// A typed protocol failure: a catalog code and a closed detail token. Hostile input never produces an exception, only one of these.
/// The code is a string constant of <see cref="ErrorCode"/>; some codes named by the design (for example definition.revoked) are not in
/// the catalog yet, so <see cref="TryToResult"/> reports false for them instead of throwing.
/// </summary>
public sealed record ProtocolFailure(string Code, string Detail = "")
{
    public static ProtocolFailure Channel(string detail) => new(ErrorCode.ElevationChannelFailed, detail);
    public static ProtocolFailure Timeout(string detail) => new(ErrorCode.ElevationTimeout, detail);
    public static ProtocolFailure ClientUntrusted(string detail) => new(ErrorCode.ElevationClientUntrusted, detail);
    public static ProtocolFailure ServerUntrusted() => new(ErrorCode.ElevationServerUntrusted);

    /// <summary>Builds the wire Result block; false when the code is not in the local catalog or the detail is not allowed for it.</summary>
    public bool TryToResult(out ElevationResult result)
    {
        result = null!;
        if (!OutcomeCodes.IsInformational(Code) && !ErrorCatalog.IsKnown(Code)) return false;
        if (!ElevationDetail.IsAllowed(Code, Detail) || (Detail.Length != 0 && !ElevationIdentifier.IsValidToken(Detail))) return false;
        result = ElevationResult.From(Code, Detail);
        return true;
    }

    /// <summary>The Result block, or a generic internal error block when this failure cannot be expressed on the wire.</summary>
    public ElevationResult ToResultOrUnexpected() => TryToResult(out var r) ? r : ElevationResult.From(ErrorCode.Unexpected);
}

/// <summary>Outcome of a strict decoder: a value or a typed failure; decoders never throw for hostile input.</summary>
public readonly record struct DecodeResult<T> where T : class
{
    public T? Value { get; init; }
    public ProtocolFailure? Failure { get; init; }
    public bool IsSuccess => Value is not null;
    public static DecodeResult<T> Ok(T value) => new() { Value = value };
    public static DecodeResult<T> Fail(ProtocolFailure failure) => new() { Failure = failure };
    public static DecodeResult<T> Framing() => Fail(ProtocolFailure.Channel(ProtocolDetail.Framing));
    public bool TryGet(out T value, out ProtocolFailure failure)
    {
        value = Value!; failure = Failure!;
        return Value is not null;
    }
}
