# Dependency-ordered implementation backlog

Reconciled 2026-10-09: FND-001 is implemented (see [verification](verification.md)); FND-002 was executed as the reduced milestone M0 and its remaining items are tracked under M4; discovery (M1), 14 per-user settings (M2), diagnostics and packaging (M3) exist in the code; the elevation channel is code-complete as M4a and unproven on a VM. The per-feature coverage tasks below are NOT started unless they are covered by those milestones; their original status line is kept for history. Estimates are engineering effort, not calendar promises; assume one developer and code review, official documentation access and the stated test equipment. No privileged testing on the host.

Execution order: FND-002 first; FND-003/004/005 and COMP-001 next as dependencies allow; SEC-001/002 before any machine mutation; FEAT-001 before leaf setting tasks; service/power/uninstall/measurement streams thereafter; REL-001 last. Per-feature tasks are added when they are planned. Added service/power/uninstall tasks are adapter/UX enablers, not substitutes for leaf acceptance.

## FND-002 — Harden simulated planning and recovery

- Purpose/requirements: WR-JOURNAL, WR-CONFLICT, WR-DRIFT, WR-PREVIEW.
- Dependencies: FND-001.
- Components: Domain, Application, Persistence, engine tests.
- Scope: Add immutable per-step intent/outcome events, schema migration fixtures, resource revision checks and serialized cross-process simulation ownership. Add typed compatibility and validated state-transition rules. Keep all Windows operations unavailable.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Crash before/after every durable boundary reconciles correctly after reopening; two concurrent clients cannot interleave plans; ABA external changes cannot be overwritten; malformed/future journals fail closed; cancellation and failed outcome writes never imply success.
- Verification: Fault-injection matrix, two-process SQLite tests, migration fixtures, definition-tamper tests and Release build.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 3–6 engineer-days; one developer, existing local SQLite simulation; no Windows adapter.

## FND-003 — Complete desktop accessibility and localization review

- Purpose/requirements: WR-L10N, WR-ACCESS.
- Dependencies: FND-002.
- Components: Desktop, desktop tests, localization documentation.
- Scope: Review all eleven destinations, state/result presentation and theme changes. Add culture-aware timestamp/size rendering when real values exist. Exercise system/light/dark and high contrast, persisted preferences, focus and mixed-direction technical fields.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Keyboard reaches every available command; screen reader announces purpose and outcomes; Persian RTL preserves technical LTR; missing Persian entries use English; 100/150/200 percent DPI has no clipping; theme follows system and high contrast dynamically.
- Verification: Windows UI/STA tests plus documented human keyboard, screen reader, DPI and high-contrast screenshots.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 2–4 engineer-days; Windows desktop and screen-reader access available.

## FND-004 — Add reviewed desired-only profiles

- Purpose/requirements: WR-PROFILES, WR-PREVIEW.
- Dependencies: FND-002, FND-003.
- Components: Application, Domain, Desktop, CLI, tests.
- Scope: Versioned profile file import/export, strict size/type/duplicate checks, trusted definition lookup and a review UI. Profiles carry desired values only, never before-state or recovery targets.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Unknown definitions/versions, duplicate keys, invalid types, extra recovery fields and oversized inputs fail without staging; a valid profile round-trips and only stages; preview invalidates on compatibility/current-state changes.
- Verification: Adversarial JSON fixtures, round-trip tests and UI stage/review tests.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 2–4 engineer-days; simulation catalog first; no Windows mutation.

## FND-005 — Unify structured and readable operation reports

- Purpose/requirements: WR-REPORTS, WR-CLI.
- Dependencies: FND-002.
- Components: Application, CLI, Desktop, Persistence.
- Scope: Define shared versioned reports with stable codes and structured parameters, operation/step IDs, simulation labels, timing, verification and recovery availability. Localized renderers are separate from stored identity.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: JSON and readable reports describe identical outcomes; failure/partial/unsupported produce non-success exits; no simulated value is a hardware measurement; report schema compatibility has fixtures.
- Verification: CLI process tests, report schema/golden semantic checks and culture fallback tests.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 1–3 engineer-days; no network, telemetry or measurement collector.

## SEC-001 — Authenticate the typed elevation transport

- Status (2026-10-11): channel implemented and tested without elevation (the protocol library, the worker logic, the client side); one real elevated run of the self-test and several VM experiments recorded in [verification.md](verification.md); the independent review of the channel is still open. No setting uses the channel yet.

- Purpose/requirements: WR-SECURITY.
- Dependencies: FND-002.
- Components: Worker, Windows adapter, Domain, security tests.
- Scope: Implement the channel specified in protocol.md and ADR-010/014/015/016 (M4 design, decisions D-01..D-22): on-demand UAC worker launched with runas (manifest asInvoker, Windows subsystem, no script or test may execute the worker image), logon-SID pipe DACL, attestation record, transcript-bound key exchange, framing, typed closed message set, one batch of 1-8 operations per worker lifetime, trusted definition lookup. Stages: dev-host guards first (WP-0), then milestone M4a (the full channel carrying one harmless typed no-op, about 30 engineer-days, before journal v4, definitions or the installer), then the rest of M4. Define UAC launch from verified executable location.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Arbitrary paths/scripts/commands cannot enter messages; forged clients, wrong session, replay, oversized/unknown requests and altered worker binaries fail closed; no worker remains idle indefinitely; initiating-user scope is preserved under alternate UAC credentials.
- Verification: Threat-model review, malicious-client tests and opt-in disposable Windows VM evidence only.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: replaced 2026-10-09 (the earlier 4-8 day figure is withdrawn). Per an unpublished design note section 9.1 and technical-docs/m4-implementation-plan.md: SEC-001 packages (WP-0, S0, A, B, D0/D, E, J) about 42 engineer-days, of which M4a about 30; shared verification, VM runs, docs and external review about 24; with SEC-002 the full M4 is about 88 (range 75-90). Prerequisites: signing route decided (D-07, application pending), dev-host guards landed, no host elevation tests.
- Gate before any real machine-scope definition ships (M5): trust.md 13.4 (external channel review, VM experiments, definition-signature layer, per-definition checklist, policy awareness, two reviewers).

## SEC-002 — Protect journal and worker trust boundaries

- Status (2026-10-11): journal schema 4 with machine rows and recovery, the definition parser and signature verifier, the anti-rollback logic (client reader only), the per-machine package with the native worker (unsigned) and the machine change commands are implemented and tested. Not done: signing, the floor writer in the worker, a definition key and any definition, the second reviewer. The machine-scope tier stays closed.

- Purpose/requirements: WR-SECURITY, WR-JOURNAL.
- Dependencies: SEC-001.
- Components: Persistence, Worker, packaging.
- Scope: Implement the trust machinery of trust.md and ADR-011/012/013/018 (decisions D-05..D-10, D-14..D-18): signed per-machine package (elevation only there; portable, per-user and unsigned never elevate), signer pin and administrator-only install root, COSE_Sign1 (ES256) signed definitions with a one-value anti-rollback floor created by the worker (definition-signature layer after M4a), machine-row journal state machine and read-only crash recovery, journal v4 migration with a backup, Event Log audit, per-definition review checklist and two-reviewer rule. Make profiles/journal records untrusted input and bind privileged outcomes to approved intents.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Different-user credentials cannot redirect HKCU or journal ownership; tampered intent cannot authorize a target; reparse/TOCTOU deployment cases fail safely; logs omit authentication material.
- Verification: Disposable VM ACL/credential-switch/tamper tests and security review report.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: replaced 2026-10-09 (the earlier 3-6 day figure is withdrawn). SEC-002 packages (C1/C2, F, G) about 23 engineer-days within the combined M4 estimate of about 88 (range 75-90, see SEC-001); needs the SignPath grant (D-07) or a fallback certificate, and the per-machine installer. With one maintainer the machine-scope tier stays closed (D-17): no real machine definition ships until a second reviewer is enabled.

## COMP-001 — Establish feature compatibility gates

- Purpose/requirements: All WR-O/WR-C/WR-T IDs, WR-SERVICES, WR-POWER.
- Dependencies: FND-002.
- Components: Domain, Windows adapter, compatibility fixtures.
- Scope: Read-only OS build/edition/architecture/hardware capability discovery and per-feature supported/unsupported reasons. Verify each candidate mechanism using Microsoft/vendor primary documentation before mutation tasks.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Missing service/device/policy is explicit; Windows 10 support is a documented decision; ARM64 and registry views are accounted for; undocumented GPU settings remain blocked without sufficient evidence.
- Verification: Read-only discovery tests, official source register, build/edition/architecture VM matrix.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 3–6 engineer-days; disposable VMs and representative devices; no host mutations.

## FEAT-001 — Implement typed Windows settings adapters

- Purpose/requirements: WR-JOURNAL, WR-DRIFT and selected WR-C IDs.
- Dependencies: FND-002, SEC-002, COMP-001.
- Components: Windows adapter, Application, Worker.
- Scope: Add narrowly allowlisted registry/policy read/write adapters with absent/key/value/type/view snapshots and scope handling; implement exactly one documented low-risk setting as the first vertical feature.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Before-state is durable before adapter call; actual policy effect is distinguished from registry write; user/machine views correct; external drift blocks compensation; unimplemented definitions rejected.
- Verification: Fake-adapter fault tests and opt-in disposable VM apply/reboot/rollback evidence.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 3–5 engineer-days; one Microsoft-documented reversible setting chosen.

## FEAT-003 — Manage services with dependency visibility

- Purpose/requirements: WR-SERVICES, WR-O-004.
- Dependencies: FEAT-001.
- Components: Windows adapter, Worker, Application, Desktop.
- Scope: Read service dependencies/dependents, startup and runtime state; stage individual supported changes with dependency warnings. Derive recommendations independently, never import third-party tables.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Missing/protected/shared services explicit; delayed start preserved; dependent services visible before review; per-service outcome and before-state verified; no mass disable default.
- Verification: SCM fixtures plus disposable VM dependency/startup/permission/restart matrix.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 4–7 engineer-days; no third-party curated recommendations reused.

## FEAT-004 — Manage independent power schemes and AC/DC settings

- Purpose/requirements: WR-POWER, WR-O-020 through WR-O-023, WR-A-007.
- Dependencies: FEAT-001.
- Components: Windows adapter, Worker, Application, Desktop.
- Scope: Read schemes/groups/supported value metadata; review AC/DC choices; create/export/import independent desired profiles; protect active/system schemes; no embedded third-party plan.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Both AC/DC values verified; unsupported sleep/USB/device settings explicit; active scheme conflict prevents destructive rollback; import carries desired definitions only; laptops and modern standby assessed.
- Verification: Power API mocks and disposable hardware/VM scheme/restart tests.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 4–8 engineer-days; hardware/driver support available.

## FEAT-005 — Discover uninstall metadata and review leftover evidence

- Purpose/requirements: WR-UNINSTALL, WR-T-005.
- Dependencies: FEAT-001, FND-003.
- Components: Domain, Application, Windows adapter, Desktop.
- Scope: First implement read-only MSI/Win32/MSIX discovery and ownership evidence UX; split official uninstall, candidate scan and cleanup into separately reviewed child tasks before any mutation code.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Name similarity cannot select a candidate; shared data and user documents unselected; user/machine/views handled; no reparse traversal; cleanup is unavailable until target-revalidation/quarantine/retention contracts and VM tests pass.
- Verification: Synthetic install ownership fixtures and disposable VM discovery; further child task approval required before cleanup implementation.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 4–7 engineer-days for discovery/review only; real uninstall and cleanup are separate 5–10 day slices.

## FEAT-006 — Collect comparable before/after measurements

- Purpose/requirements: WR-MEASURE, WR-REPORTS, WR-T-001.
- Dependencies: FND-005, COMP-001.
- Components: Windows adapter, Application, Desktop, CLI.
- Scope: Opt-in local measurements with protocol, duration, sample counts, environment/workload context and uncertainty; no health score or fabricated improvement.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Unavailable sensors and confounding changes explicit; report includes raw comparable data; no causal performance claim without controlled evidence; simulation excluded from measurement totals.
- Verification: Collector fixtures, repeatability experiments and documented hardware runs.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 3–6 engineer-days; target metric/workload selected; no telemetry.

## REL-001 — Prepare a licensed and verified Windows release

- Purpose/requirements: All implemented requirements, WR-SECURITY, WR-ACCESS.
- Dependencies: FND-003, FND-004, FND-005, SEC-002, COMP-001; each shipping feature task.
- Components: CI, packaging, notices, release docs.
- Scope: Package signed Windows hosts, resolved third-party licenses/notices, source availability, SPDX conventions, migration/backup guide and truthful support matrix. Require all implemented coverage rows to carry evidence.
- Exclusions: unrelated cleanup/refactors, undocumented fallbacks, claims of unavailable feature success, and host Windows mutations.
- Acceptance: Locked clean build; reproducible dependency inventory; authentic GPLv3 and third-party texts; clean VM install/upgrade/uninstall; no telemetry/cloud requirement; no unsupported success; manual accessibility gates recorded.
- Verification: Windows CI artifacts, SBOM/license review, signed-package verification and clean disposable VM release checklist.
- Compatibility/failure: retain explicit unsupported, denied, external-conflict, partial and restart outcomes; persistence failure prohibits mutation.
- Completion evidence: reviewed diff, passing focused tests, build log, fixture/VM report where relevant, source links, coverage child status and documented residual limits.
- Effort: 3–6 engineer-days; signing/packaging and supported architectures resolved.

# Per-feature tasks

Per-feature implementation tasks are planned from [roadmap.md](roadmap.md) and are not published here yet. Each one needs its own WR-* requirement IDs, acceptance criteria and evidence before work starts, and no feature is complete without a passing test and, for machine-wide changes, a disposable-VM report.
