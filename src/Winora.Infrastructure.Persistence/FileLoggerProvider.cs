// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Winora.Application;
namespace Winora.Infrastructure.Persistence;

/// <summary>Settings for <see cref="FileLoggerProvider"/>. Defaults: 1 MiB per file, 5 files, Information and above.</summary>
public sealed record FileLoggerOptions(string Directory, LogRedactor Redactor)
{
    public const string FileStem = "winora";
    public long MaxFileBytes { get; init; } = 1024 * 1024;
    public int MaxFiles { get; init; } = 5;
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Bounded rolling local log: <c>winora.log</c> (newest) plus <c>winora.1.log</c>.. <c>winora.{MaxFiles-1}.log</c>, UTF-8 JSON lines
/// (one entry per line). Every message and structured state value is redacted before it is written; exception messages are never
/// logged, only the exception type, the stable error code and type.method frames. Thread-safe, never throws into the caller, and on any
/// I/O failure drops the entry and counts it in <see cref="DroppedCount"/>. Nothing is ever sent anywhere.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int MaxFields = 16, MaxFieldLength = 200, MaxFrames = 20, MaxLineBytes = 16 * 1024;
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly FileLoggerOptions options;
    private readonly object gate = new();
    private long dropped;
    private bool disposed;

    public FileLoggerProvider(FileLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxFileBytes < 1024 || options.MaxFiles < 1) throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
    }

    /// <summary>Number of entries dropped because the folder could not be written (read-only, missing, locked, disk full).</summary>
    public long DroppedCount => Interlocked.Read(ref dropped);
    public string CurrentFilePath => Path.Combine(options.Directory, FileName(0));
    public static string FileName(int index) => index == 0 ? $"{FileLoggerOptions.FileStem}.log" : $"{FileLoggerOptions.FileStem}.{index}.log";

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { lock (gate) disposed = true; }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= options.MinimumLevel;

    internal void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            if (!IsEnabled(level)) return;
            var line = Format(category, level, eventId, state, exception, formatter);
            lock (gate)
            {
                if (disposed) return;
                try { Append(line); }
                catch (Exception) { Interlocked.Increment(ref dropped); }
            }
        }
        catch (Exception) { Interlocked.Increment(ref dropped); }
    }

    private string Timestamp() => options.Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private string Format<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var redactor = options.Redactor;
        string? correlation = null, code = null;
        var fields = new List<KeyValuePair<string, string>>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var (key, value) in pairs)
            {
                if (key == "{OriginalFormat}") continue;
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                if (key.Equals("CorrelationId", StringComparison.OrdinalIgnoreCase)) correlation ??= redactor.Redact(text, 64);
                else if (key.Equals("Code", StringComparison.OrdinalIgnoreCase) || key.Equals("ErrorCode", StringComparison.OrdinalIgnoreCase)) code ??= redactor.Redact(text, 64);
                else if (fields.Count < MaxFields) fields.Add(new(redactor.Redact(key, 64), redactor.Redact(text, MaxFieldLength)));
            }
        }
        string message;
        try { message = formatter(state, null); } catch (Exception) { message = "<unformattable>"; }
        string? errorCode = null, exceptionType = null; List<string>? frames = null; List<string>? inner = null;
        if (exception is not null)
        {
            exceptionType = exception.GetType().FullName;
            errorCode = ErrorMapper.Classify(exception)?.Code;
            frames = Frames(exception);
            // A task fault arrives wrapped (AggregateException has no frames of its own): the frames of the innermost exception show where it started.
            var deepest = exception;
            while (deepest.InnerException is { } next) deepest = next;
            if (!ReferenceEquals(deepest, exception)) { frames.Add("<inner>"); frames.AddRange(Frames(deepest)); }
            inner = [];
            for (var e = exception.InnerException; e is not null && inner.Count < 3; e = e.InnerException) inner.Add(e.GetType().FullName ?? "?");
        }
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("ts", Timestamp());
            json.WriteString("level", level.ToString());
            json.WriteString("category", redactor.Redact(category, 200));
            json.WriteNumber("event", eventId.Id);
            if (!string.IsNullOrEmpty(eventId.Name)) json.WriteString("eventName", redactor.Redact(eventId.Name, 100));
            if (correlation is not null) json.WriteString("cid", correlation);
            if (code is not null || errorCode is not null) json.WriteString("code", code ?? errorCode);
            json.WriteString("msg", redactor.Redact(message));
            if (fields.Count > 0)
            {
                json.WriteStartObject("fields");
                foreach (var (key, value) in fields) json.WriteString(key, value);
                json.WriteEndObject();
            }
            if (exceptionType is not null)
            {
                json.WriteString("exception", redactor.Redact(exceptionType, 200));
                if (inner!.Count > 0) { json.WriteStartArray("inner"); foreach (var t in inner) json.WriteStringValue(redactor.Redact(t, 200)); json.WriteEndArray(); }
                json.WriteStartArray("frames");
                foreach (var frame in frames!) json.WriteStringValue(redactor.Redact(frame, 300));
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        var bytes = stream.ToArray();
        if (bytes.Length > MaxLineBytes)
        {
            // Oversized entries are replaced by a minimal one rather than cut mid-JSON.
            return JsonSerializer.Serialize(new { ts = Timestamp(), level = level.ToString(), category = redactor.Redact(category, 200), @event = eventId.Id, msg = "<entry too large; dropped>" });
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static List<string> Frames(Exception exception)
    {
        var list = new List<string>();
        try
        {
            foreach (var frame in new StackTrace(exception, false).GetFrames())
            {
                var method = frame.GetMethod();
                if (method is null) continue;
                list.Add((method.DeclaringType?.FullName ?? "?") + "." + method.Name);
                if (list.Count >= MaxFrames) break;
            }
        }
        catch (Exception) { /* frames are best effort */ }
        return list;
    }

    // Called under the gate.
    private void Append(string line)
    {
        System.IO.Directory.CreateDirectory(options.Directory);
        RefuseLink(options.Directory);
        var payload = Encoding.UTF8.GetBytes(line + "\n");
        var current = CurrentFilePath;
        RefuseLink(current);
        long size = 0;
        try { size = new FileInfo(current).Length; } catch (FileNotFoundException) { }
        if (size > 0 && size + payload.Length > options.MaxFileBytes) Rotate();
        using var file = VerifiedFile.Open(current, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.Write(payload);
    }

    /// <summary>
    /// The log folder is in the user's profile, which other programs of the user can change, while the app may run with administrator rights. A folder or file that has been replaced by a
    /// link is not written, moved or deleted; the entry is dropped.
    /// </summary>
    private static void RefuseLink(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; }
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("The log path is a link.");
    }

    private void Rotate()
    {
        // Drop the oldest, shift the rest up. Any failure aborts this entry (counted as dropped by the caller).
        if (options.MaxFiles == 1) { File.Delete(CurrentFilePath); return; }
        var oldest = Path.Combine(options.Directory, FileName(options.MaxFiles - 1));
        RefuseLink(oldest);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = options.MaxFiles - 2; i >= 0; i--)
        {
            var from = Path.Combine(options.Directory, FileName(i));
            RefuseLink(from);
            if (File.Exists(from)) File.Move(from, Path.Combine(options.Directory, FileName(i + 1)), true);
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Write(category, logLevel, eventId, state, exception, formatter);
    }
}

public static class FileLoggerExtensions
{
    /// <summary>Adds the redacting rolling file logger. Hosts call this at the composition root; there is no remote sink.</summary>
    public static ILoggingBuilder AddWinoraFileLogger(this ILoggingBuilder builder, FileLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddProvider(new FileLoggerProvider(options));
        return builder;
    }
}
