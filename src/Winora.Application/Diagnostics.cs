// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>Result of a local diagnostics export. Path is the created archive; nothing is ever uploaded.</summary>
public sealed record DiagnosticsExportResult(string ArchivePath, int FileCount, long Bytes, DateTimeOffset CreatedUtc);

/// <summary>
/// Builds a redacted support archive in a folder the user chooses: app/OS/.NET versions, schema version, error-code counts,
/// recent log lines, operation history codes and states. It must contain NO user paths beyond the application data folder name,
/// no registry values the user set, no usernames, machine names, or raw exception messages. Failure surfaces as WinoraException.
/// </summary>
public interface IDiagnosticsExporter
{
    Task<DiagnosticsExportResult> ExportAsync(string destinationFolder, CancellationToken cancellationToken);
}
