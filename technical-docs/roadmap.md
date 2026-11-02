# Product roadmap

Status: ADOPTED under the owner's delegation (proposed 2026-10-08; progress column reconciled with the repository on 2026-10-09). Derived from three independent reviews (correctness, security, product/architecture) recorded in internal review notes that are not published yet. Where this file conflicts with AGENTS.md or technical-docs/backlog.md, those win until the owner approves an amendment.

## Goal

Grow Winora milestone by milestone toward broad coverage of Windows settings, services, power, cleanup and uninstall tasks with better safety, verification and localization, plus the extras in [extra-features.md](extra-features.md). Breadth is a goal, not a claim. Independence rules in AGENTS.md stay absolute: upstream is behavior evidence only; no source, XAML, assets, identifiers, service tables or power files are copied.

## Principles

1. Safety before breadth: no feature ships without preview, journal-before-mutation, conditional rollback and a real-resource test.
2. Value early: read-only discovery ships before any mutation.
3. Low blast radius first: non-elevated HKCU, then services/HKLM behind SEC gates, then file deletion/uninstall last.
4. Data over code: settings are catalog rows (key, value, type, OS range, risk, recovery kind), not per-setting classes. The 86 optimization/customization registrations become about 10 reviewable catalog batches.
5. Honest status: technical-docs/verification.md is updated with evidence per milestone; nothing is "supported" without a VM matrix run.

## Milestones

| ID | Name | Contents | Exit evidence | Status |
|----|------|----------|---------------|--------|
| M0 | Core hardening (reduced FND-002) | `BEGIN IMMEDIATE` on all writes; revision column and revision-guarded Save; atomic Prepare (active check + read + insert in one transaction); real `state` columns with partial unique index for single-active rule; schema v1->v2 migration with fixtures; stable codes for BUSY; remove `WriteActual` from the Application port; Recover returns interrupted-compensation to Applied; stale-operation rollback blocked by resource revision; plan token binding Plan to Apply | Two-process test, ABA test, fault injection on every durable boundary, migration fixtures, locked Release build | Done. Evidence in verification.md (M0 core hardening evidence) |
| M1 | Read-only discovery | Async ports; ViewModel depends on an Application port; generic `ResourceRef` + typed snapshot + revision; read-only adapters: startup (Run keys, Startup folders), scheduled tasks, services, installed apps; shown in existing destinations | Fixture tests plus read-only host smoke test; Persian/RTL review of new strings | Done in code with automated tests and a read-only host smoke test (verification.md M1 sections). Counts were not compared with Windows tools |
| M2 | First real mutations (non-elevated HKCU) | Registry mutator (missing vs present, type, 32/64 view), compare-and-swap, journal-before-mutation, preview/apply/revert in UI and CLI; 10-15 shell/privacy catalog rows; desired-only profiles (FND-004); sandbox HKCU test subkey | Mutation lifecycle tests against isolated subkey; no host-wide HKCU edits in CI | Done in code and automated tests; real effect of the 14 settings on a live shell is NOT verified (needs VMs) |
| M3 | Non-elevated release candidate | Accessibility/DPI/high-contrast pass (FND-003); packaging ADR (unpackaged signed installer plus portable zip, not MSIX); first-run/upgrade/uninstall of Winora; diagnostics log export; VM matrix on 2-3 Windows builds; honest limits statement | Manual checklists, screenshots, VM report | Done; manual screen-reader, high-contrast, DPI, MSI install and VM checks are NOT verified |
| M4 | Privilege foundation | Two stages (decisions D-01..D-22 in an unpublished design note; plan in [m4-implementation-plan.md](m4-implementation-plan.md)). **M4a** (about 30 engineer-days): dev-host guards first (no script or test executes the worker), NativeAOT spike, then the full channel (pipe, attestation, handshake, framing) carrying ONE harmless typed no-op, plus the VM experiments that gate the design, before journal v4, definitions or the installer. **Full M4** (75-90 engineer-days): SEC-001 protocol, SEC-002 trust root, signed definitions with anti-rollback, journal v4, per-machine installer and signing, consent UI, credential-switch VM test. Zero machine definitions ship in M4; the first real machine-scope definition (M5) waits for the gate in [trust.md](trust.md) 13.4 (external channel review, VM experiments, definition-signature layer, per-definition checklist, policy awareness, two reviewers) | M4a: unelevated tests green on the dev host, VM results recorded, documents corrected. Full: ACL/pipe tests, two-build VM matrix, external security review sign-off | Design done; M4a code complete and unit-tested, NOT proven on a VM; the rest of M4 not started |
| M5 | Elevated features | HKLM tweaks, services, power plans (original authored plans), update settings, network tweaks | Per-batch tests, VM matrix | Not started |
| M6 | Uninstall and cleanup | Handle-based deletion, quarantine, ownership evidence, MSI/Win32/MSIX adapters, leftover scan | Adversarial junction/hardlink/TOCTOU tests | Not started |
| M7 | Hardware-dependent and long tail | Vendor GPU settings, measurement/benchmarks, anything needing vendor evidence | Evidence gate per item | Not started |

Deferred behind evidence gates: vendor GPU items (FEAT-O-005..012), update peer sharing, WebDAV, hibernation.

## Work-package ownership for subagents

Disjoint ownership per package; every contributor reads AGENTS.md first.

- Persistence: schema, migrations, SQL, store tests.
- Application: engine, ports, planning, state-transition table.
- Windows adapters: read-only discovery, then mutators, behind Application ports.
- Desktop: ViewModels, localization, accessibility.
- CLI/Worker: shared engine consumers; Worker stays inert until M4.
- Docs/verification: technical-docs/verification.md, backlog and coverage updates.

## Governance changes (done)

AGENTS.md was updated on 2026-10-09 to the current milestone scope with the safety rules for sandboxed registry tests, the fixed shell actions, the worker composition-root exception and the ban on executing the worker image. The development host's real settings are still never mutated by tests.

## Open decisions

Target Windows versions and editions; installer technology and signing authority; where signed definitions live; Persian translation review process; restore-point/export-before-change policy; Winora's own update channel.
