# Differentiating features (proposed)

Beyond baseline parity. All are original designs, offline, no accounts or telemetry. Each needs its own backlog entry, requirement IDs and acceptance before work starts. Order follows [roadmap.md](roadmap.md).

## Safety and trust (core differentiators)

- **Preview diff for every change**: exact before/after per resource, risk code, restart/Explorer-refresh flag.
- **Universal journal and per-change revert**: conditional rollback that never overwrites external changes; clear "cannot undo" labeling for irreversible items.
- **Drift detection**: detect when Windows updates or other tools revert applied settings; offer re-apply with review.
- **Export before change**: optional reg export / restore-point reminder, stored locally.
- **Dry-run CLI** with machine-readable JSON, usable in scripts and CI.
- **Desired-only profiles**: share a profile file safely; import shows diff and confirms; no before-state or paths in profiles.
- **Compatibility gating**: each catalog row declares OS build range; unsupported rows are hidden or shown as unsupported, never silently skipped.

## Product value

- **Read-only system overview**: startup impact, scheduled tasks, services, installed apps with source and publisher.
- **Startup manager** with per-item reasons and reversible disable.
- **Safe-removal ratings** for services/tasks from authored, evidence-backed notes (no copied tables).
- **Uninstall with verified leftover scan**: ownership evidence per candidate, quarantine with restore, shared/user data excluded by default.
- **Reports**: before/after summary exportable as HTML/JSON; support bundle with redaction.
- **Measurement protocol** (later): repeatable boot/idle/memory samples with honest variance, no marketing claims of speedups.
- **Persian-first quality**: complete RTL, LTR islands for technical strings, culture-aware dates and sizes.
- **Accessibility**: keyboard-complete, screen-reader tested, high-contrast and DPI verified.
- **Portable and installed modes**, signed builds, SBOM, reproducible build notes.
- **Scheduled safe checks** (drift report only, never silent mutation).

## Not planned

Telemetry, accounts, ads, bundled third-party installers, "one-click boost" claims, registry cleaners, automatic mutation without review.
