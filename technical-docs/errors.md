<!-- SPDX-License-Identifier: GPL-3.0-only -->
# Error catalog

Source of truth: `src/Winora.Domain/Errors.cs` (`ErrorCode`, `ErrorCatalog`). Codes are stable strings; persisted outcome codes keep their pre-catalog spelling. Resource key = `Code_` + code with dots as underscores, present in every host's neutral `Strings.resx` (enforced by tests). Retryable means the same request may succeed unchanged after a short wait or a fresh preview; it never means automatic retry of a mutation.

| Code | Category | Severity | Retryable | Resx key | User guidance |
|---|---|---|---|---|---|
| `operation.recovery_required` | Conflict | Warning | no | `Code_operation_recovery_required` | An earlier operation needs recovery first. Restart Winora or run recover so it can be reconciled. |
| `operation.not_reversible` | Unsupported | Warning | no | `Code_operation_not_reversible` | This operation cannot be reverted. |
| `operation.superseded` | Conflict | Warning | no | `Code_operation_superseded` | A newer operation has changed this value, so this older operation can no longer be reverted. Nothing was changed. |
| `operation.revision_unknown` | Unsupported | Warning | no | `Code_operation_revision_unknown` | The journal has no record of the exact state this operation produced, so it cannot be reverted safely. |
| `operation.resource_changed` | Conflict | Warning | yes | `Code_operation_resource_changed` | The value changed while the action was starting. Nothing was changed; review the current value and try again. |
| `operation.external_conflict` | Conflict | Warning | no | `Code_operation_external_conflict` | The value was changed by something else, so Winora did not overwrite it. Review the current value. |
| `operation.verification_failed` | Conflict | Error | no | `Code_operation_verification_failed` | The value was written but could not be verified. Review is required. |
| `operation.reconciled_partial` | Conflict | Error | no | `Code_operation_reconciled_partial` | After an interruption the value matched neither the original nor the requested value. Review is required. |
| `operation.rollback_uncertain` | Conflict | Error | no | `Code_operation_rollback_uncertain` | The revert could not be confirmed. Review is required. |
| `operation.rollback_failed` | Conflict | Error | no | `Code_operation_rollback_failed` | The revert did not restore the previous value. Review is required. |
| `operation.cancelled` | Cancelled | Info | no | `Code_operation_cancelled` | The action was cancelled before it changed anything. |
| `plan.stale` | Conflict | Warning | yes | `Code_plan_stale` | The value changed after you reviewed it. Nothing was applied; review the change again. |
| `plan.duplicate_feature` | Validation | Error | no | `Code_plan_duplicate_feature` | The plan lists the same feature more than once. |
| `plan.missing_dependency` | Validation | Error | no | `Code_plan_missing_dependency` | The plan depends on a feature that is not part of it. |
| `plan.resource_conflict` | Validation | Error | no | `Code_plan_resource_conflict` | Two features in the plan change the same resource. |
| `plan.dependency_cycle` | Validation | Error | no | `Code_plan_dependency_cycle` | The plan has features that depend on each other in a cycle. |
| `journal.revision_conflict` | Conflict | Warning | yes | `Code_journal_revision_conflict` | The record was changed by another action. Refresh and try again. |
| `journal.operation_missing` | Validation | Error | no | `Code_journal_operation_missing` | The operation record could not be found. |
| `journal.invalid_transition` | Corruption | Error | no | `Code_journal_invalid_transition` | That change of state is not allowed for this operation. |
| `journal.invalid` | Corruption | Critical | no | `Code_journal_invalid` | Winora's local journal could not be read safely, so it stopped to protect your data. No Windows setting was changed. |
| `journal.schema_unsupported` | Unsupported | Critical | no | `Code_journal_schema_unsupported` | The local journal was created by a newer version of Winora and was not opened. |
| `journal.schema_corrupt` | Corruption | Critical | no | `Code_journal_schema_corrupt` | The local journal structure is damaged and was not opened. |
| `journal.migration_conflict` | Corruption | Critical | no | `Code_journal_migration_conflict` | The local journal could not be upgraded safely and was not changed. |
| `journal.untrusted_definition` | Corruption | Critical | no | `Code_journal_untrusted_definition` | A journal entry did not match any known definition and was ignored. |
| `store.busy` | Transient | Warning | yes | `Code_store_busy` | Winora's local storage is busy. Nothing was changed; try again in a moment. |
| `store.unavailable` | Transient | Error | yes | `Code_store_unavailable` | Winora's local storage could not be reached. Nothing was changed; check disk access and try again. |
| `store.failure` | Internal | Error | no | `Code_store_failure` | Winora's local storage reported an error. Review storage before retrying. |
| `protocol.size` | Validation | Error | no | `Code_protocol_size` | The request was too large and was rejected. |
| `protocol.version` | Validation | Error | no | `Code_protocol_version` | The request uses an unsupported protocol version and was rejected. |
| `protocol.operation` | Validation | Error | no | `Code_protocol_operation` | The request has no valid operation identifier and was rejected. LEGACY (D-16): nothing raises it; kept so old data stays readable. |
| `protocol.command` | Validation | Error | no | `Code_protocol_command` | The request command is not supported and was rejected. LEGACY (D-16): nothing raises it; kept so old data stays readable. |
| `protocol.feature` | Validation | Error | no | `Code_protocol_feature` | The request names a feature that is not supported and was rejected. LEGACY (D-16): nothing raises it; kept so old data stays readable. |
| `worker.unimplemented` | Unsupported | Warning | no | `Code_worker_unimplemented` | This action is not available yet. Nothing was changed. |
| `profile.size` | Validation | Error | no | `Code_profile_size` | The profile file is too large and was not imported. |
| `profile.invalid` | Validation | Error | no | `Code_profile_invalid` | The profile is not valid and was not imported. |
| `profile.duplicate_property` | Validation | Error | no | `Code_profile_duplicate_property` | The profile repeats a setting name and was not imported. |
| `access.denied` | Denied | Error | no | `Code_access_denied` | Access was denied. Nothing was changed. |
| `error.unexpected` | Internal | Critical | no | `Code_error_unexpected` | Something unexpected happened. Nothing was reported as successful. You can copy the reference when asking for help. |

## Mapping rules

- `ErrorMapper` (Application) is the only exception-to-error translator. StoreException/OperationException/WinoraException keep their code; `SqliteException` is translated by Persistence into `store.busy` (SQLITE_BUSY/LOCKED) or `store.failure`; `InvalidDataException` whose message is a catalog code keeps it, otherwise `journal.invalid`; `JsonException` -> `journal.invalid`; `UnauthorizedAccessException` -> `access.denied`; other `IOException` -> `store.unavailable`; `OperationCanceledException` -> `operation.cancelled` (category Cancelled, severity Info, not a failure); anything else -> `error.unexpected` (Internal, Critical) with a fresh correlation id, reported once through `IErrorReporter` with full exception detail.
- `WinoraError.Detail` carries only codes and identifiers, never exception messages, paths or secrets.
- Success and informational outcome codes that are not errors (`operation.prepared`, `operation.executing`, `operation.applied`, `operation.already_configured`, `operation.reverted`, `operation.rollback_prepared`, `operation.rollback_abandoned`, `operation.reconciled_applied`, `operation.reconciled_unchanged`) are persisted record codes outside the error catalog; hosts localize them with the same key rule.
- CLI exit codes by category: Validation 2, Conflict 3, Unsupported 4, Denied 5, Transient 6, Corruption 7, Cancelled 130, Internal 1 (`ErrorCatalog.ExitCode`). The CLI does not adopt this yet.

## M2 additions

| Code | Category | Severity | Retryable | Resx key | User guidance |
|---|---|---|---|---|---|
| `setting.state_unrecognized` | Unsupported | Warning | no | `Code_setting_state_unrecognized` | The setting holds a value of a kind Winora does not manage, so it is not changed. |
| `shell.refresh_failed` | Transient | Warning | yes | `Code_shell_refresh_failed` | The setting was changed but Windows could not be told to refresh; apply again from History. |
| `shell.restart_failed` | Transient | Warning | yes | `Code_shell_restart_failed` | The setting was changed but Explorer could not be restarted; it takes effect after sign-out or restart; try again from History. |

Persisted informational outcome codes added for settings (outside the error catalog): `operation.refresh_pending`, `operation.restart_pending`, `operation.signout_pending`, `operation.shell_refreshed`.

## M3 diagnostics additions

| Code | Category | Severity | Retryable | Resx key | User guidance |
|---|---|---|---|---|---|
| `diagnostics.destination_invalid` | Validation | Error | no | `Code_diagnostics_destination_invalid` | The chosen folder cannot be used for the diagnostics archive. Pick a normal local folder outside the Winora data folder. Nothing was created. |
| `diagnostics.export_failed` | Transient | Error | yes | `Code_diagnostics_export_failed` | The diagnostics archive could not be created. Nothing was uploaded and no partial file was kept; check disk space and folder access, then try again. |

CLI exit codes: `diagnostics.destination_invalid` exits 2, `diagnostics.export_failed` exits 6. Both keys exist in the Desktop, CLI and Worker `Strings.resx` and `Strings.fa.resx`.

## M4a elevation additions

Source: technical-docs/protocol.md 17.1 (status PROPOSED there until the M4a exit review). Only the codes that an M4a path raises are in the catalog; every other code of protocol.md 17.1 stays PROPOSED and is added by the work package that raises it (for example `definition.revoked`, `definition.signature_invalid`, `definition.rollback`, `definition.restore_incompatible`, `definition.restore_revoked`, `trust.store_insecure`, `elevation.batch_aborted` and the planned `setting.managed_by_policy`). Each code below has its `Code_` key in the Desktop, CLI and Worker `Strings.resx` and `Strings.fa.resx` (the Persian text is machine drafted). The worker shows no user text; it carries the Worker keys only because the catalog test requires them (the excluded-key rule of protocol.md 17.4 is not adopted yet).

| Code | Category | Severity | Retryable | Resx key | User guidance |
|---|---|---|---|---|---|
| `elevation.unavailable` | Unsupported | Warning | no | `Code_elevation_unavailable` | Administrator changes are not available in this session or installation. |
| `elevation.unsigned_build` | Unsupported | Warning | no | `Code_elevation_unsigned_build` | This build is not signed by the expected publisher, so administrator changes are turned off. |
| `elevation.install_untrusted` | Denied | Error | no | `Code_elevation_install_untrusted` | Winora's installation location or files cannot be trusted for administrator changes. |
| `elevation.uac_disabled` | Unsupported | Warning | no | `Code_elevation_uac_disabled` | User Account Control is turned off, so administrator changes are not available. |
| `elevation.client_elevated` | Unsupported | Warning | no | `Code_elevation_client_elevated` | Run Winora without administrator rights to make machine changes. |
| `elevation.declined` | Cancelled | Info | no | `Code_elevation_declined` | The administrator prompt was declined. Nothing changed. |
| `elevation.launch_failed` | Transient | Error | yes | `Code_elevation_launch_failed` | The administrator helper could not be started. Nothing changed. |
| `elevation.blocked` | Denied | Error | no | `Code_elevation_blocked` | Windows or your organization blocked the administrator helper. Nothing changed. |
| `elevation.timeout` | Transient | Warning | yes | `Code_elevation_timeout` | The administrator helper did not respond in time. Nothing changed; trying again shows the prompt again. |
| `elevation.server_untrusted` | Denied | Critical | no | `Code_elevation_server_untrusted` | The administrator helper could not be verified, so nothing was sent to it. |
| `elevation.client_untrusted` | Denied | Error | no | `Code_elevation_client_untrusted` | The helper refused the requesting program because it could not be verified. |
| `elevation.channel_failed` | Internal | Error | no | `Code_elevation_channel_failed` | The secure channel to the administrator helper failed. Review the change before trying again. |
| `elevation.confirmation_required` | Denied | Warning | no | `Code_elevation_confirmation_required` | Administrator changes need an interactive confirmation. Run this in an interactive window. |
| `elevation.platform_unsupported` | Unsupported | Warning | no | `Code_elevation_platform_unsupported` | Administrator changes are not supported on this version of Windows. |
| `elevation.platform_unverified` | Unsupported | Warning | no | `Code_elevation_platform_unverified` | Administrator changes have not been verified on this Windows configuration, so they are turned off. |
| `definition.unknown` | Validation | Error | no | `Code_definition_unknown` | The change is not in Winora's trusted list. |
| `definition.set_mismatch` | Corruption | Critical | no | `Code_definition_set_mismatch` | The app and the helper disagree about the trusted list. |
| `definition.revoked` | Unsupported | Warning | no | `Code_definition_revoked` | The change was withdrawn and is no longer available. |
| `definition.signature_invalid` | Corruption | Critical | no | `Code_definition_signature_invalid` | Winora's trusted list failed verification. |
| `definition.rollback` | Denied | Error | no | `Code_definition_rollback` | An older trusted list was refused. |
| `trust.store_insecure` | Corruption | Critical | no | `Code_trust_store_insecure` | The machine trust record has unsafe permissions. |
| `setting.managed_by_policy` | Denied | Warning | no | `Code_setting_managed_by_policy` | This setting is managed by a policy. Winora does not change it. |
| `worker.busy` | Transient | Warning | yes | `Code_worker_busy` | Another administrator helper is running. Try again in a moment. |
| `worker.hardening_failed` | Internal | Error | no | `Code_worker_hardening_failed` | The administrator helper could not start safely on this system. Nothing changed. |

Detail tokens (protocol.md 17.2) are a closed list per code in `Winora.Domain.Elevation.ElevationDetail`; they are persisted subcodes, never free text, and are shown in diagnostics only.

### Disposition of the foundation validator stub codes (D-16)

`WorkerRequest`, `WorkerCommand` and `WorkerProtocol` were removed. `protocol.size` and `protocol.version` stay in use by the wire protocol (oversize frame header; negotiation failure and bad arguments). `protocol.operation`, `protocol.command` and `protocol.feature` are LEGACY: nothing raises them, they remain in the catalog with their resource keys so that any persisted record or log that carries them stays readable and localizable (`ErrorCatalog.LegacyCodes`, enforced by a compatibility test). They may be deleted only after a migration test over real journals proves that none exists.

## Rows recorded for completeness (codes introduced with M1 and M2 and never listed above)

| Code | Category | Severity | Retryable | Resx key | User guidance |
|---|---|---|---|---|---|
| `discovery.unavailable` | Unsupported | Warning | no | `Code_discovery_unavailable` | This information is not available on this computer. |
| `discovery.item_unreadable` | Validation | Info | no | `Code_discovery_item_unreadable` | Some entries could not be read and were skipped. |
| `setting.unknown` | Validation | Error | no | `Code_setting_unknown` | This setting is not part of Winora's catalog. Nothing was changed. |
| `setting.choice_invalid` | Validation | Error | no | `Code_setting_choice_invalid` | That choice is not valid for this setting. Nothing was changed. |
| `setting.unsupported_build` | Unsupported | Warning | no | `Code_setting_unsupported_build` | This setting is not supported on this version of Windows. Nothing was changed. |
| `target.not_allowed` | Denied | Critical | no | `Code_target_not_allowed` | Winora refused a change outside its own catalog. Nothing was changed. |
| `target.kind_unsupported` | Unsupported | Warning | no | `Code_target_kind_unsupported` | The administrator helper does not support this kind of setting. Nothing was changed. |
