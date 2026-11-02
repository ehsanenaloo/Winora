// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>
/// Single place that classifies, logs once (with a correlation id) and localizes every failure shown to the user.
/// Text always comes from resources through WinoraError.UserMessageKey; raw exception text never reaches the UI.
/// </summary>
public sealed class ErrorPresenter(LocalizationService text, ILogger<ErrorPresenter> logger)
{
    /// <summary>Raised on the reporting thread for failures that must block the user (critical severity, unhandled crashes).</summary>
    public event Action<WinoraError>? BlockingRequested;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Details = new();

    /// <summary>
    /// The exception type names and the first Winora method names of the stack, for the diagnostics text. No message, no path, no value: those can hold personal data. Kept for the last 50 failures.
    /// </summary>
    private static void Remember(string? id, Exception? exception)
    {
        if (id is null || exception is null) return;
        var types = new List<string>();
        for (var e = exception; e is not null && types.Count < 4; e = e.InnerException) types.Add(e.GetType().Name);
        var inner = exception;
        while (inner.InnerException is not null) inner = inner.InnerException;
        var frames = new System.Diagnostics.StackTrace(inner, false).GetFrames()
            .Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.FullName?.StartsWith("Winora", StringComparison.Ordinal) == true)
            .Take(4).Select(m => $"{m!.DeclaringType!.Name}.{m.Name}");
        Details[id] = string.Join(">", types) + " at " + string.Join(" < ", frames);
        if (Details.Count > 50) foreach (var key in Details.Keys.Take(Details.Count - 50)) Details.TryRemove(key, out _);
    }

    public static string NewCorrelationId() => Guid.NewGuid().ToString("N")[..8];

    public static bool IsBlocking(WinoraError error) => error.Severity == ErrorSeverity.Critical;
    public static bool OffersReview(WinoraError error) => error.Category == ErrorCategory.Conflict;

    /// <summary>Classifies an exception. Returns null for cancellation, which is neutral and never shown.</summary>
    public WinoraError? Classify(Exception exception)
    {
        var id = NewCorrelationId();
        if (exception is OperationCanceledException) return null;
        // Shared classification lives in Application; only the SQLite provider exception is Desktop-side knowledge.
        var error = exception is Microsoft.Data.Sqlite.SqliteException ? WinoraError.From(ErrorCode.StoreFailure)
            : ErrorMapper.Classify(exception) ?? WinoraError.From(ErrorCode.Unexpected);
        return error with { CorrelationId = error.CorrelationId ?? id };
    }

    /// <summary>Classifies a stable outcome code that did not come with an exception (for example an external conflict).</summary>
    public WinoraError ForCode(string code, Guid? operationId = null) => WinoraError.From(code, null, operationId, NewCorrelationId());

    /// <summary>Logs the failure once with its correlation id and raises the blocking request when needed.</summary>
    public WinoraError? Report(Exception exception, string context)
    {
        var error = Classify(exception);
        if (error is null) return null;
        Remember(error.CorrelationId, exception);
        logger.LogError(exception, "{Context} failed: {Code} ({Category}), reference {CorrelationId}", context, error.Code, error.Category, error.CorrelationId);
        if (IsBlocking(error)) BlockingRequested?.Invoke(error);
        return error;
    }

    /// <summary>Last-chance handler for exceptions nobody caught. Always blocking, never shows raw text.</summary>
    public WinoraError ReportUnhandled(Exception? exception, string source)
    {
        var error = WinoraError.From(ErrorCode.Unexpected, null, null, NewCorrelationId());
        Remember(error.CorrelationId, exception);
        logger.LogCritical(exception, "Unhandled exception from {Source}, reference {CorrelationId}", source, error.CorrelationId);
        BlockingRequested?.Invoke(error);
        return error;
    }

    public string Message(WinoraError error) => text.Has(error.UserMessageKey) ? text[error.UserMessageKey] : text[ErrorCatalog.ResourceKey(ErrorCode.Unexpected)];
    public string Reference(WinoraError error) => text.Format("ErrorReferenceFormat", error.CorrelationId ?? "-");

    /// <summary>Safe to paste into a bug report: stable code, reference, version and, when known, the exception type names and the Winora methods where it happened.</summary>
    public static string Diagnostics(WinoraError error) =>
        $"code={error.Code}\nreference={error.CorrelationId}\nversion={typeof(ErrorPresenter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0"}"
        + (error.CorrelationId is { } id && Details.TryGetValue(id, out var detail) ? $"{(char)10}detail={detail}" : "");
}
