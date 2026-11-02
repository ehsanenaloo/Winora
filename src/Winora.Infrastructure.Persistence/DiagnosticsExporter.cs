// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Persistence;

/// <summary>Facts written to the archive manifest. Injectable so tests can prove the final redaction pass covers them.</summary>
public sealed record DiagnosticsEnvironment(string AppVersion, string OsDescription, int OsBuild, string Architecture, string RuntimeVersion, string Culture)
{
    public static DiagnosticsEnvironment FromRuntime() => new(
        (typeof(DiagnosticsExporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0],
        RuntimeInformation.OSDescription, Environment.OSVersion.Version.Build, RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription, CultureInfo.CurrentUICulture.Name);
}

/// <summary>
/// Builds the local, redacted diagnostics archive <c>winora-diagnostics-&lt;yyyyMMdd-HHmmss&gt;.zip</c> in a folder the user chose.
/// Contents: manifest.json, error-codes.json, operations.json (simulation: id prefix, state, code, time), setting-operations.json
/// (setting id, choice id, state, code, times; no registry values), redacted log tail files and README.txt. Nothing is uploaded.
/// The archive is written to a temporary name in the destination and moved into place without overwriting; any failure removes the
/// temporary file and surfaces as <see cref="WinoraException"/> (diagnostics.destination_invalid / diagnostics.export_failed).
/// </summary>
public sealed class DiagnosticsExporter : IDiagnosticsExporter
{
    public const int MaxArchiveBytes = 5 * 1024 * 1024;
    public const int MaxContentBytes = 5 * 1024 * 1024;
    public const int MaxEntries = 24;
    public const int MaxOperationRecords = 50;
    public const int MaxSettingRecords = 100;
    public const int MaxDestinationLength = 200;
    private const int MaxLogFiles = 10;
    private static readonly Regex SafeToken = new(@"^[A-Za-z0-9_.\-]{1,64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IOperationStore operations;
    private readonly ISettingOperationStore settings;
    private readonly AppDataPaths paths;
    private readonly LogRedactor redactor;
    private readonly DiagnosticsEnvironment environment;
    private readonly TimeProvider clock;
    private readonly IReadOnlyList<DiagnosticsJournalSource> journals;

    public DiagnosticsExporter(IOperationStore operations, ISettingOperationStore settings, AppDataPaths paths, LogRedactor redactor,
        DiagnosticsEnvironment? environment = null, TimeProvider? clock = null, IReadOnlyList<DiagnosticsJournalSource>? journals = null)
    {
        this.journals = journals ?? [];
        this.operations = operations; this.settings = settings; this.paths = paths; this.redactor = redactor;
        this.environment = environment ?? DiagnosticsEnvironment.FromRuntime();
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Convenience for hosts: the SQLite store serves both journals; paths and redaction default to the real application data and identity.</summary>
    public DiagnosticsExporter(SqliteOperationStore store, AppDataPaths? paths = null, RedactionContext? redaction = null, IReadOnlyList<DiagnosticsJournalSource>? journals = null)
        : this(store, store, paths ?? AppDataPaths.Default(), new LogRedactor(redaction ?? RedactionContext.FromEnvironment()), journals: journals) { }

    public Task<DiagnosticsExportResult> ExportAsync(string destinationFolder, CancellationToken cancellationToken) =>
        Task.Run(() => Export(destinationFolder, cancellationToken), cancellationToken);

    private DiagnosticsExportResult Export(string destinationFolder, CancellationToken ct)
    {
        var destination = ValidateDestination(destinationFolder);
        ct.ThrowIfCancellationRequested();
        // The folder is held until the archive is in place, so it cannot be swapped for a link between the temp file and the move.
        using var pin = VerifiedFile.Pin(destination, chosenByUser: true);
        var created = clock.GetUtcNow();
        var temp = Path.Combine(destination, ".winora-diagnostics-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var entries = BuildEntries(created, ct);
            ct.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (name, bytes) in entries)
                    {
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                        entry.LastWriteTime = created < new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : created;
                        using var target = entry.Open();
                        target.Write(bytes);
                    }
                }
                stream.Flush(true);
                if (stream.Length > MaxArchiveBytes) throw new InvalidDataException("diagnostics.too_large");
            }
            ct.ThrowIfCancellationRequested();
            var final = MoveIntoPlace(temp, destination, created);
            return new DiagnosticsExportResult(final, entries.Count, new FileInfo(final).Length, created);
        }
        catch (Exception e)
        {
            TryDelete(temp);
            if (e is OperationCanceledException or WinoraException) throw;
            throw new WinoraException(WinoraError.From(ErrorCode.DiagnosticsExportFailed), e);
        }
    }

    private static string MoveIntoPlace(string temp, string destination, DateTimeOffset created)
    {
        var stem = "winora-diagnostics-" + created.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var attempt = 1; attempt <= 99; attempt++)
        {
            var final = Path.Combine(destination, attempt == 1 ? stem + ".zip" : $"{stem}-{attempt}.zip");
            if (File.Exists(final)) continue; // never overwrite an existing file
            try { File.Move(temp, final, overwrite: false); return final; }
            catch (IOException) when (File.Exists(final)) { /* lost a race for this name; try the next */ }
        }
        throw new IOException("diagnostics.no_free_name");
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { /* best effort; the name is a dot-prefixed temp */ } }

    // ---- destination ----------------------------------------------------------------------------

    private string ValidateDestination(string destination)
    {
        WinoraException Invalid(Exception? inner = null) => new(WinoraError.From(ErrorCode.DiagnosticsDestinationInvalid), inner);
        if (string.IsNullOrWhiteSpace(destination) || destination.Length > MaxDestinationLength || destination.IndexOfAny([.. Path.GetInvalidPathChars(), '*', '?']) >= 0) throw Invalid();
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or SecurityException) { throw Invalid(e); }
        // UNC and device paths could leave the machine or bypass checks; only local drive-letter folders are accepted.
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length < 3 || full[1] != ':' || paths.IsInside(full)) throw Invalid();
        try
        {
            var root = Path.GetPathRoot(full);
            if (root is not null && new DriveInfo(root).DriveType == DriveType.Network) throw Invalid();
            if (File.Exists(full)) throw Invalid();
            for (var p = (string?)full; p is not null; p = Path.GetDirectoryName(p))
            {
                if (!Directory.Exists(p)) continue;
                if (!File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) continue;
                if (p == full) throw Invalid();
                // A linked ancestor may only be used when its resolved target is outside the application data folder.
                var target = new DirectoryInfo(p).ResolveLinkTarget(true);
                if (target is null || paths.IsInside(target.FullName)) throw Invalid();
            }
            Directory.CreateDirectory(full);
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint) || !Directory.Exists(full)) throw Invalid();
        }
        catch (WinoraException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException) { throw Invalid(e); }
        return full;
    }

    // ---- content ---------------------------------------------------------------------------------

    private List<(string Name, byte[] Bytes)> BuildEntries(DateTimeOffset created, CancellationToken ct)
    {
        var list = new List<(string, byte[])>();
        var total = 0L;
        void Add(string name, string text, bool bom = false)
        {
            var bytes = new UTF8Encoding(false).GetBytes(redactor.RedactLines(text));
            if (bom) bytes = [.. Encoding.UTF8.GetPreamble(), .. bytes];
            if (list.Count >= MaxEntries || total + bytes.Length > MaxContentBytes) throw new InvalidDataException("diagnostics.too_large");
            list.Add((name, bytes)); total += bytes.Length;
        }

        var (simulation, simulationFailure) = Guarded(() => operations.ListOperations());
        var (settingRecords, settingFailure) = Guarded(() => settings.ListSettingOperations());
        ct.ThrowIfCancellationRequested();

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string code) { var key = Token(code); counts[key] = counts.GetValueOrDefault(key) + 1; }
        foreach (var r in simulation ?? []) Count(r.Code);
        foreach (var r in settingRecords ?? []) Count(r.Code);

        var simulationRows = (simulation ?? []).TakeLast(MaxOperationRecords).Reverse().Select(r => new
        { id = Prefix(r.Id), state = r.State.ToString(), code = Token(r.Code), createdUtc = Utc(r.CreatedAtUtc) }).ToList();
        var settingRows = (settingRecords ?? []).TakeLast(MaxSettingRecords).Reverse().Select(r => new
        {
            id = Prefix(r.Id), settingId = Token(r.SettingId), choiceId = Token(r.ChoiceId), state = r.State.ToString(), code = Token(r.Code),
            compensationPrepared = r.CompensationPrepared, needsExplorerRestart = r.NeedsExplorerRestart, needsSignOut = r.NeedsSignOut,
            createdUtc = Utc(r.CreatedAtUtc), updatedUtc = Utc(r.UpdatedAtUtc),
        }).ToList();

        Add("error-codes.json", JsonSerializer.Serialize(new { schemaVersion = 1, counts, total = counts.Values.Sum() }, Json));
        Add("operations.json", JsonSerializer.Serialize(new
        { schemaVersion = 1, available = simulationFailure is null, failureCode = simulationFailure, totalRecords = simulation?.Count, included = simulationRows.Count, records = simulationRows }, Json));
        Add("setting-operations.json", JsonSerializer.Serialize(new
        { schemaVersion = 1, available = settingFailure is null, failureCode = settingFailure, totalRecords = settingRecords?.Count, included = settingRows.Count, records = settingRows }, Json));

        if (journals.Count > 0)
        {
            var areas = journals.Select(j =>
            {
                var (rows, failure) = Guarded(j.Read);
                var kept = (rows ?? []).Take(DiagnosticsJournals.PerArea).Select(r => new
                { id = Prefix(r.Id), what = Token(r.What), state = Token(r.State), code = Token(r.Code), atUtc = Utc(r.AtUtc) }).ToList();
                foreach (var r in rows ?? []) Count(r.Code);
                return new { area = Token(j.Area), available = failure is null, failureCode = failure, included = kept.Count, records = kept };
            }).ToList();
            Add("journals.json", JsonSerializer.Serialize(new { schemaVersion = 1, areas }, Json));
        }

        var logs = ReadLogs(MaxContentBytes - total - 64 * 1024, ct);
        foreach (var (name, text) in logs) Add("logs/" + name, text);

        Add("README.txt", Readme(), bom: true);
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            generatedUtc = Utc(created),
            statement = "This archive was created locally. Nothing was uploaded or sent anywhere; it is only a file in the folder you chose.",
            app = new { version = Token(environment.AppVersion) },
            journal = new { schemaVersion = SqliteOperationStore.CurrentSchemaVersion, settingRecordSchemaVersion = SettingOperationRecord.CurrentSchemaVersion },
            os = new { description = environment.OsDescription, build = environment.OsBuild },
            architecture = environment.Architecture,
            runtime = environment.RuntimeVersion,
            culture = environment.Culture,
            files = list.Select(e => e.Item1).Append("manifest.json").OrderBy(n => n, StringComparer.Ordinal).ToList(),
            limits = new { maxArchiveBytes = MaxArchiveBytes, maxOperationRecords = MaxOperationRecords, maxSettingRecords = MaxSettingRecords },
        }, Json);
        Add("manifest.json", manifest);
        return list;
    }

    private static (T? Value, string? FailureCode) Guarded<T>(Func<T> read) where T : class
    {
        try { return (read(), null); }
        catch (Exception e) { return (null, ErrorMapper.Classify(e)?.Code ?? ErrorCode.Unexpected); }
    }

    private static string Prefix(Guid id) => id.ToString("N")[..8];
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    /// <summary>Journal text is untrusted: only short, identifier-shaped values are copied into the archive.</summary>
    private static string Token(string? value) => value is not null && SafeToken.IsMatch(value) ? value : "<invalid>";

    private List<(string Name, string Text)> ReadLogs(long budget, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        if (budget <= 0 || !Directory.Exists(paths.LogsFolder)) return result;
        try
        {
            if (File.GetAttributes(paths.LogsFolder).HasFlag(FileAttributes.ReparsePoint)) return result;
            for (var i = 0; i < MaxLogFiles && budget > 0; i++)
            {
                ct.ThrowIfCancellationRequested();
                var name = FileLoggerProvider.FileName(i);
                var path = Path.Combine(paths.LogsFolder, name);
                if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
                using var file = VerifiedFile.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Keep the newest part of the file when the budget is smaller than the file; the cut line is dropped.
                var take = (int)Math.Min(Math.Min(file.Length, 2 * 1024 * 1024), budget);
                if (take <= 0) continue;
                file.Seek(-take, SeekOrigin.End);
                var buffer = new byte[take];
                file.ReadExactly(buffer);
                var text = Encoding.UTF8.GetString(buffer);
                if (take < file.Length) { var cut = text.IndexOf('\n'); text = cut < 0 ? "" : text[(cut + 1)..]; }
                text = redactor.RedactLines(text);
                budget -= Encoding.UTF8.GetByteCount(text);
                result.Add((name, text));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* unreadable logs are simply omitted */ }
        return result;
    }

    private static string Readme() => """
        Winora diagnostics archive
        ==========================

        This file was created on your computer, at your request. Winora did not upload it and has no
        telemetry. It is only useful if YOU choose to share it, for example when reporting a problem.

        Contents
          manifest.json           Winora version, journal version, Windows description and build, architecture,
                                  .NET runtime, language, creation time (UTC), and the list of files.
          error-codes.json        How many times each stable result code appears in Winora's records.
          operations.json         The most recent simulation records: short id prefix, state, result code, time.
          setting-operations.json The most recent settings operations: setting id, choice id, state, result code,
                                  times. Registry values and raw data are NOT included.
          journals.json           A short summary of the change journals of the system-changing features: kind of change,
                                  state, result code, time, and a short id prefix. No names, paths or values.
          logs/                   The most recent Winora log files, redacted again when this archive was made.
                                  User profile paths, user and computer names, UNC paths, SIDs and e-mail
                                  addresses are replaced with <home>, <user>, <machine>, <unc>, <sid>, <email>.

        Please review the files before you share them. Redaction is automatic and best effort: text that
        looks like none of the patterns above is kept as written.

        ---------------------------------------------------------------------------------------------

        بایگانی عیب‌یابی وینورا

        این فایل روی رایانه‌ی شما و به درخواست شما ساخته شده است. وینورا چیزی ارسال نکرده و داده‌های
        استفاده را جمع‌آوری نمی‌کند. فقط اگر خودتان بخواهید (مثلاً هنگام گزارش مشکل) آن را به اشتراک
        می‌گذارید. محتوا: مشخصات نسخه و سیستم، شمارش کدهای نتیجه، فهرست کوتاه عملیات اخیر و گزارش‌های
        اخیر. مقدارهای رجیستری گنجانده نمی‌شوند و مسیر نمایه‌ی کاربر، نام کاربر و نام رایانه حذف می‌شوند.
        پیش از اشتراک‌گذاری، فایل‌ها را بازبینی کنید؛ حذف خودکار تلاش بهینه است و تضمین کامل نیست.
        """;
}
