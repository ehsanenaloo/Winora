// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using Winora.Domain;
namespace Winora.Application;

/// <summary>
/// Logging contract for composition roots (implement over ILogger with structured fields code, category, severity,
/// correlationId, operationId). Called once per unexpected exception with full detail; Domain never logs.
/// </summary>
public interface IErrorReporter
{
    void Report(WinoraError error, Exception? exception);
}

public sealed class NullErrorReporter : IErrorReporter
{
    public static NullErrorReporter Instance { get; } = new();
    public void Report(WinoraError error, Exception? exception) { }
}

/// <summary>
/// The single place exceptions become <see cref="WinoraError"/>. Persistence translates SqliteException into StoreException
/// (store.busy / store.failure) at the port boundary, so this mapper never needs a SQLite dependency.
/// Cancellation is Cancelled (not a failure); unknown exceptions are Internal, get a correlation id and are reported once.
/// </summary>
public sealed class ErrorMapper(IErrorReporter? reporter = null)
{
    private readonly IErrorReporter reporter = reporter ?? NullErrorReporter.Instance;

    public WinoraError Map(Exception exception, Guid? operationId = null)
    {
        var known = Classify(exception, operationId);
        if (known is not null) return known;
        var correlation = Guid.NewGuid().ToString("N");
        var error = WinoraError.From(ErrorCode.Unexpected, exception.GetType().Name, operationId, correlation);
        reporter.Report(error, exception);
        return error;
    }

    /// <summary>Pure classification of expected exception types; null means unexpected. Detail never contains exception messages.</summary>
    public static WinoraError? Classify(Exception exception, Guid? operationId = null) => exception switch
    {
        WinoraException w => w.Error with { OperationId = w.Error.OperationId ?? operationId },
        OperationException o => WinoraError.From(o.Code, null, operationId),
        StoreException s => WinoraError.From(s.Code, null, operationId),
        OperationCanceledException => WinoraError.From(ErrorCode.Cancelled, null, operationId),
        // Journal/schema faults throw InvalidDataException whose message is already a catalog code.
        InvalidDataException d => WinoraError.From(ErrorCatalog.IsKnown(d.Message) ? d.Message : ErrorCode.JournalInvalid, null, operationId),
        JsonException => WinoraError.From(ErrorCode.JournalInvalid, null, operationId),
        UnauthorizedAccessException => WinoraError.From(ErrorCode.AccessDenied, null, operationId),
        IOException => WinoraError.From(ErrorCode.StoreUnavailable, null, operationId),
        _ => null,
    };

    /// <summary>Runs an action and turns expected and unexpected failures into a value; cancellation is returned as Cancelled, never swallowed silently as success.</summary>
    public Result<T> Run<T>(Func<T> action, Guid? operationId = null)
    {
        try { return Result<T>.Ok(action()); }
        catch (Exception e) { return Result<T>.Fail(Map(e, operationId)); }
    }
}
