<!-- SPDX-License-Identifier: GPL-3.0-only -->
# Read-only discovery (M1)

Discovery lists what already exists on this computer: startup entries, scheduled tasks, services and installed applications. It is strictly read-only. No discovery path starts, stops, enables, disables, deletes, uninstalls or edits anything, and none of it needs elevation. Status and evidence live in [verification.md](verification.md) and [backlog.md](backlog.md) (DISC-001..DISC-009). The Scheduled Tasks page of the app is not part of this: it has its own adapters and can change tasks, see [scheduled-tasks.md](scheduled-tasks.md). The Services page of the app can change services, see [services.md](services.md).

## Scope

| Kind | Port item (Domain) | Contents |
|---|---|---|
| `startup` | `StartupItem` | Registry Run/RunOnce values and Startup folder entries, with scope (user/machine) and approval state |
| `tasks` | `ScheduledTaskInfo` | Task path, name, state, hidden flag, author, last/next run, whether it runs at logon or boot, command, vendor-Microsoft flag |
| `services` | `ServiceInfo` | Display name, start mode, run state, kind, account, image path, description |
| `apps` | `InstalledApplication` | Identity (kind, stable id, scope, registry view, publisher), display name, version, install date, size, location, provenance |

Out of scope for M1: any change operation, ownership or leftover analysis, package (MSIX) removal review, and enumeration of items the current user cannot read (these become issues, not failures).

## Ports and layering

`IDiscoverySource<T>` (Application) is the only entry point. Adapters in `Winora.Infrastructure.Windows` implement it; hosts compose `DiscoveryService` through DI. Implementations must honor cancellation, return partial results with `DiscoveryIssue` entries for per-item failures, and throw only for cancellation. `DiscoveryService` converts any other thrown exception into a single classified issue for that source, so one broken source never blanks the others.

## Partial results

A source returns `Items` plus `Issues`. An issue is `{source, code, category, detail}`; the code is a stable catalog code (`discovery.unavailable`, `discovery.item_unreadable`, `access.denied`, `error.unexpected`, ...). `detail` is safe text (codes, key names), never a raw exception message, and is control-character stripped and truncated by the CLI. Hosts show the items that were read and state how many entries could not be read.

## CLI

```
winora discover <startup|tasks|services|apps|all> [--json] [--filter TEXT] [--limit N]
```

- `--filter TEXT`: case-insensitive substring match over the item's name, command/path, publisher/account and stable id. Must not be empty or blank.
- `--limit N`: 1 to 100000. Applied per kind after filtering and ordering. `all` therefore shows up to N entries of each kind.
- Ordering: display name (case-insensitive), then stable id (ordinal). Deterministic for a given data set.
- Human output: a read-only banner, one table per kind (localized headers; en and fa), then a summary `S of T entries shown; N entries could not be read.` followed by one line per issue with its source, stable code and localized guidance. Enum cells (for example `RegistryRun`, `Running`) are shown as their stable technical names and are not translated. Long cells are truncated at 40 characters and control characters are replaced by spaces.
- Output encoding is UTF-8 without BOM.

### Exit codes

| Exit | Meaning |
|---|---|
| 0 | Success, or partial success: at least one requested source produced data, or the only issues are `discovery.item_unreadable`. Issues are still reported in the output. |
| 2 | Bad arguments (usage printed to stderr, nothing on stdout). |
| `ErrorCatalog.ExitCode(category)` | Total failure: every requested source returned no entries and reported a real issue. The code comes from the most severe issue (Validation 2, Conflict 3, Unsupported 4, Denied 5, Transient 6, Corruption 7, Internal 1). JSON is still written to stdout. |
| 130 | Cancelled (Ctrl+C). Nothing is written to stdout; the process stops cleanly. |

An empty result with no issues is success. The `simulate` commands now also report failures as `<localized summary>`, then `<guidance> (code: <code>)`, with the category exit code (previously always 1; unchanged for failed or partial operation outcomes, which still exit 1).

### JSON schema (schemaVersion 1)

```
{
  "schemaVersion": 1,
  "readOnly": true,
  "collectedAtUtc": "2026-10-08T12:00:00+00:00",   // latest source timestamp
  "kind": "startup|tasks|services|apps|all",
  "totalCount": 123,                                // entries matching the filter, before --limit
  "items": [ { "kind": "...", ... } ],
  "issues": [ { "source": "...", "code": "...", "category": "...", "detail": null } ]
}
```

Key order is stable. Every item has `kind` (`startup`, `tasks`, `services`, `apps`) so `all` can mix kinds. Property names are camelCase; enum values are the Domain enum names. Nullable fields are always present and `null` when unknown. Adding a property is a compatible change; renaming or removing one requires `schemaVersion` 2.

Item properties:

- startup: `id, name, command, source (RegistryRun|RegistryRunOnce|StartupFolder), scope (User|Machine), approval (Unknown|Enabled|Disabled), publisher`
- tasks: `id, path, name, state (Unknown|Ready|Running|Disabled|Queued), hidden, author, lastRunUtc, nextRunUtc, runsAtLogonOrBoot, command, isVendorMicrosoft`
- services: `id, displayName, startMode, runState, serviceType, account, imagePath, description`
- apps: `id, displayName, version, publisher, installationKind (Msi|Win32|Msix), scope, registryView, installLocation, installedOn (yyyy-MM-dd), estimatedSizeKb, isSystemComponent, provenance`

## Privacy

Discovery data stays on this computer. Nothing is uploaded, no network call is made, and the CLI writes nothing to disk (it prints to stdout) and does not persist discovery results. Output can contain user names inside paths and command lines, so review it before sharing it yourself.

## Limits

- Results are a point-in-time read; entries can change while reading, and sources are not read atomically with each other.
- Without elevation, some machine-wide entries may be unreadable. This is reported as issues, not hidden.
- Windows only; adapters are expected to report `discovery.unavailable` elsewhere (verify per adapter in verification.md).
- Enum values are not localized in CLI tables; Persian strings are machine-assisted drafts pending native review (DISC-009).
- No claim of parity with any other tool is made.
