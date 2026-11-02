// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json;
using Winora.Application;
using Winora.Domain;
using static Winora.Cli.CliText;
namespace Winora.Cli;

internal enum DiagnosticsAction { Export, LogPath }
internal sealed record DiagnosticsOptions(DiagnosticsAction Action, string? Folder, bool Json, string? Database = null);

/// <summary>
/// `winora diagnostics export [--to FOLDER] [--json]` and `winora diagnostics log-path`. Export writes one local zip to FOLDER
/// (default: the current directory, never a location the user did not name) and prints its full path. Nothing is uploaded.
/// </summary>
internal static class DiagnosticsCommand
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool TryParse(IReadOnlyList<string> args, out DiagnosticsOptions options)
    {
        options = new(DiagnosticsAction.Export, null, false);
        if (args.Count == 0) return false;
        if (args[0] == "log-path") { if (args.Count != 1) return false; options = new(DiagnosticsAction.LogPath, null, false); return true; }
        if (args[0] != "export") return false;
        string? folder = null, database = null; var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--json" when !json: json = true; break;
                case "--to" when folder is null && i + 1 < args.Count && args[i + 1].Trim().Length > 0: folder = args[++i]; break;
                case "--database" when database is null && i + 1 < args.Count && args[i + 1].Trim().Length > 0: database = args[++i]; break;
                default: return false;
            }
        }
        options = new(DiagnosticsAction.Export, folder, json, database);
        return true;
    }

    public static async Task<int> RunAsync(DiagnosticsOptions options, Func<IDiagnosticsExporter> exporterFactory, string logFolder, string currentDirectory, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (options.Action == DiagnosticsAction.LogPath) { stdout.WriteLine(logFolder); return 0; }
        try
        {
            var result = await exporterFactory().ExportAsync(options.Folder ?? currentDirectory, ct).ConfigureAwait(false);
            if (options.Json)
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, path = result.ArchivePath, fileCount = result.FileCount, bytes = result.Bytes, createdUtc = result.CreatedUtc.UtcDateTime, uploaded = false,
                }, JsonOptions));
            }
            else
            {
                stdout.WriteLine(F("DiagExported", result.ArchivePath));
                stdout.WriteLine(string.Format(CultureInfo.CurrentCulture, T("DiagSummary"), result.FileCount, result.Bytes));
                stdout.WriteLine(T("DiagNothingUploaded"));
                stdout.WriteLine(T("DiagReview"));
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine(T("DiscoverCancelled"));
            return ErrorCatalog.ExitCode(ErrorCategory.Cancelled);
        }
        catch (Exception e)
        {
            // Never print exception text: only the localized guidance, the stable code and the reference.
            var error = new ErrorMapper().Map(e);
            stderr.WriteLine(F("DiscoverError", T(error.UserMessageKey), error.Code) + (error.CorrelationId is null ? "" : " " + F("DiscoverReference", error.CorrelationId)));
            return error.ExitCode;
        }
    }
}
