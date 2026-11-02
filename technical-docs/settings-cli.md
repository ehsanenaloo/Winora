# Settings CLI (M2)

`winora settings <verb>` reads and changes the real, user-scope Windows settings in the authored catalog (technical-docs/settings-catalog.md). Every write goes through `ISettingsService` (catalog allowlist, journal before mutation, compare-and-set); the CLI has no registry code of its own. Text is English or Persian from the UI culture; ids, choice ids, registry locations and codes are never translated.

## Verbs

| Verb | Writes | Notes |
| --- | --- | --- |
| `list [--json] [--category Explorer\|Taskbar\|Privacy\|Search\|Appearance]` | no | Current choice, status (ok, custom value, unsupported on this build, unreadable). |
| `show <id> [--json]` | no | Definition, location, risk, recovery, choices, default. |
| `preview <id> <choice> [--json]` | no | Plan only: before, after, `willChange`. Nothing is written. |
| `apply <id> <choice> [--yes] [--json] [--no-refresh] [--no-restart-explorer] [--restart-explorer]` | yes | Prints the review (before, after, location, risk, Explorer warning). Without `--yes` it needs an interactive `y`. |
| `revert <operation-id> [--yes] [--json]` | yes | Reviewed first; never overwrites a newer outside change. |
| `complete <operation-id> [--yes] [--json]` | shell only | Runs the pending shell refresh (restarts Explorer) for an operation left `PendingRestart`. |
| `history [--json]` | no | Recorded settings outcomes, newest first. |
| `recover [--json]` | journal | Reconciles interrupted operations. |

Apply options: the default mirrors the Desktop default (`SettingApplyOptions.Default`: broadcast and restart Explorer when the setting needs it). `--no-refresh` is write only (`WriteOnly`). `--no-restart-explorer` broadcasts but does not restart Explorer, so a setting that needs it ends `PendingRestart`. `--restart-explorer` states the default explicitly. `--no-refresh` cannot be combined with the other two; `--yes` is accepted only by apply, revert and complete.

## Confirmation rule

`apply`, `revert` and `complete` print their review and then:

- with `--yes`: proceed;
- on a terminal: ask `[y/N]`; anything but `y` or `yes` (including end of input) declines, exit 1, nothing written;
- without a terminal (stdin redirected) and without `--yes`: print an explanation to stderr, exit 2, and do nothing at all (no read, no plan, no journal entry).

With `--json`, the review and the prompt go to stderr so stdout is only the JSON document.

## Exit codes

`0` success (including `AlreadyConfigured` and `PendingRestart`; the text and `tookEffect` say which), `1` unexpected error or declined review, `2` bad arguments or validation (`setting.unknown`, `setting.choice_invalid`), `3` conflict (`plan.stale`, `operation.external_conflict`), `4` unsupported (`setting.unsupported_build`), `5` denied, `6` transient, `7` corruption, `130` cancelled (Ctrl+C). These are `ErrorCatalog.ExitCode` of the error category; an outcome with state `Failed`, `Partial` or `Unsupported` uses the category of its code.

## JSON (schemaVersion 1)

Property names are camelCase, enum values are Domain names, key order is stable, nullable fields are always present. Adding a property is compatible; renaming or removing one needs `schemaVersion` 2.

- `settings.list`: `{ schemaVersion, kind, count, items[] }`; `settings.show`: `{ schemaVersion, kind, item }`. Item: `id, category, risk, recovery, requiresExplorerRestart, requiresSignOut, compatibility (Supported|UnsupportedBuild|Unknown), currentChoiceId (null = custom or unreadable), custom, choices[], defaultChoiceId, recommendedChoiceId, location, issue ({code, category} or null)`. For a grouped row `location` lists every member, separated by "; ", and `currentChoiceId` is null (custom) when only some members match a choice.
- `settings.preview`: `{ schemaVersion, kind, writes: false, plan: { settingId, choiceId, currentChoiceId, willChange, definitionVersion, location, restartExplorer, signOut } }`.
- `settings.apply`, `settings.revert`, `settings.complete`: `{ schemaVersion, kind, outcome }`. Outcome: `operationId, settingId, choiceId, state, code, beforeChoiceId, afterChoiceId, needsExplorerRestart, needsSignOut, tookEffect, atUtc`. `tookEffect` is true only for Applied, Reverted and AlreadyConfigured; `PendingRestart` means written but not yet in effect.
- `settings.history` and `settings.recover`: `{ schemaVersion, kind, count, items[] }` of outcomes.

Errors are plain localized text on stderr with the stable code, for example `(code: plan.stale)`; raw exception text is never printed.

## Test-only options

`--database <path>` selects the journal file. The default depends on the rights of the console: not elevated, `%LOCALAPPDATA%\Winora\simulation.db`; elevated, the same admin-only journal the Desktop uses (`%ProgramData%\Winora\journals\operations-<SID>.db`), and the command stops with a plain reason when that folder is refused. `--sandbox-root <name>` is hidden from help and exists only for tests: it makes the CLI compose `RegistrySettingsAccess` with the root `HKCU\Software\WinoraTests\<name>` and a no-op shell refresh. The name must be one registry path segment of `[A-Za-z0-9-]`, at most 64 characters; anything else exits 2 before anything runs. Process tests use it with a temp journal and delete the sandbox key on dispose. It is not a user feature and gives no way to reach any other location.

## Limits

No claim is made that each setting takes visible effect on every Windows build; see technical-docs/verification.md. Persian text is machine drafted and needs native review.
