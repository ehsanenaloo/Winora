<!-- SPDX-License-Identifier: GPL-3.0-only -->
# Local diagnostics (M3)

No telemetry. Nothing is uploaded; the log is a local file and the export is a local zip that the user may choose to share after reviewing it.

## Log

- Folder: `%LocalAppData%\Winora\logs` (`AppDataPaths.LogsFolder`, same root as the journal). Files: `winora.log` (newest), `winora.1.log` .. `winora.4.log`. Defaults: 1 MiB per file, 5 files, Information level. Rotation drops the oldest file and shifts the others up.
- Format: one UTF-8 JSON object per line with `ts` (UTC), `level`, `category`, `event` (+`eventName`), `cid` (correlation id), `code` (stable error code), `msg`, `fields` (other state values, max 16), and for exceptions `exception` (type), `inner` (types), `frames` (`Type.Method` only, max 20). Log state keys named `CorrelationId` and `Code`/`ErrorCode` are promoted to `cid` and `code`.
- Safety: thread-safe (single writer lock), never throws into the caller, drops an entry on any I/O failure and increments `FileLoggerProvider.DroppedCount`. Entries over 16 KiB become a minimal placeholder line. Exception messages are never logged; do not put exception text into a message template (the redactor only masks identity patterns).
- Wiring: `loggingBuilder.AddWinoraFileLogger(new FileLoggerOptions(AppDataPaths.Default().LogsFolder, new LogRedactor(RedactionContext.FromEnvironment())))`. The CLI does this by default; disable with `--no-log` or `WINORA_NO_LOG=1`. `WINORA_DATA_DIR` redirects the data root (tests). The CLI logs only the command name and exit code, never arguments.

## Redaction (`LogRedactor`)

Tokens: `<home>`, `<user>`, `<machine>`, `<unc>`, `<sid>`, `<email>`. Case-insensitive; handles backslash, slash, doubled (JSON-escaped) and percent-encoded separators; user and machine names of 3+ characters match anywhere, 2-character names only as whole tokens. Output is capped at 4096 characters per value (16 KiB per exported log line); a regex timeout yields `<redaction-failed>` rather than raw text. Persian and other text is preserved. Limit: arbitrary secrets that match none of these patterns are not detected.

## Export

`winora diagnostics export [--to FOLDER] [--json]` (default folder: the current directory; `--database PATH` is for tests) and `IDiagnosticsExporter.ExportAsync(folder)` create `winora-diagnostics-<yyyyMMdd-HHmmss>.zip` (UTC; a `-2` to `-99` suffix is used if the name exists, so existing files are never overwritten). `winora diagnostics log-path` prints the log folder. Entries: `manifest.json`, `error-codes.json`, `operations.json` (last 50 simulation records: id prefix, state, code, time), `setting-operations.json` (last 100: setting id, choice id, state, code, flags, times; no registry data), `logs/*` (redacted again, newest first, tail kept when over budget), `README.txt` (English and Persian). Caps: 5 MiB archive, 5 MiB content, 24 entries. The archive also holds a bounded summary (newest 50 rows: kind, outcome code, time; no names, paths or values) for ten system-changing areas: Optimize, Power, Network, Permissions, Startup, Services, Scheduled tasks, Repair, Uninstall and Preinstalled apps. Quarantine and Restore point records are left out on purpose: their records name files and paths, and the support path for them is the screen of the feature itself. If a journal cannot be read the section reports `available:false` and a stable `failureCode`; the export still succeeds.

Destination rules (`diagnostics.destination_invalid`): non-empty, at most 200 characters, no wildcards, local drive-letter path (UNC, device paths and network drives are refused because they could leave the machine), not a file, not Winora's data folder or beneath it, not a reparse point, no linked ancestor that resolves into the data folder; a missing folder is created. Anything else that goes wrong (disk full, locked folder, no free name, size cap) removes the temp file and raises `diagnostics.export_failed`. Cancellation propagates as `OperationCanceledException`.

## Limits

Reparse checks are point-in-time (a race between validation and the move is not closed). Redaction is best effort. The Desktop export action and its logging registration belong to the Desktop host.
