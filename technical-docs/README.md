# Winora technical documentation

Index of the technical documentation. Winora is an independent, offline, GPL-3.0-only Windows 11 settings manager. Start with the project [README](../README.md) and the contributor rules in [AGENTS.md](../AGENTS.md), then use the sections below. Each document states its own status; claims that could not be verified are marked as unverified, and the [verification log](verification.md) records what was actually checked.

## Product and planning

| Document | Responsibility |
|---|---|
| [product.md](product.md) | Scope and acceptance of the product |
| [roadmap.md](roadmap.md) | Milestones M0 to M7 and their current state |
| [backlog.md](backlog.md) | Dependency-ordered tasks with acceptance criteria |
| [extra-features.md](extra-features.md) | Differentiating features beyond baseline parity |
| [release-limits.md](release-limits.md) | What the current 0.x release does and does not do |

## Architecture and subsystems

| Document | Responsibility |
|---|---|
| [architecture.md](architecture.md) | Layers, boundaries and composition roots |
| [adrs.md](adrs.md) | Architecture decision records |
| [operations.md](operations.md) | Operation lifecycle, journal and recovery invariants |
| [errors.md](errors.md) | Stable error and outcome codes |
| [discovery.md](discovery.md) | Read-only discovery: scope, output schema, limits |
| [scheduled-tasks.md](scheduled-tasks.md) | Scheduled Tasks page: actions, safety rules, protected tasks |
| [services.md](services.md) | Services page: start, stop, restart, startup type, locked services, errors |
| [activity.md](activity.md) | Activity service: runs, notifications, the one-system-change rule and the Action center |
| [settings-catalog.md](settings-catalog.md) | The authored per-user setting catalog |
| [settings-cli.md](settings-cli.md) | Settings command line and its JSON schema |
| [diagnostics.md](diagnostics.md) | Local log and redacted diagnostics export |
| [bloatware.md](bloatware.md) | Microsoft Store (MSIX) package list, classes, removal and limits |
| [network.md](network.md) | Network page: adapters, DNS providers, DNS over HTTPS, safety rules and limits |
| [profiles.md](profiles.md) | Profiles page: built-in and custom profiles, plan, apply, undo, file format and limits |
| [tools.md](tools.md) | Tools page: repair tools, the closed program list, optional Windows features, safety rules and limits |
| [permissions.md](permissions.md) | Permissions page: app permissions per capability for the signed-in account, quick camera and microphone switch, privacy lockdown, safety rules and limits |
| [updates.md](updates.md) | Updates page: Windows Update modes, facts, safety rules and limits |
| [uninstall.md](uninstall.md) | Uninstall, leftover folders and quarantine: policy and what is implemented |
| [localization.md](localization.md), [accessibility.md](accessibility.md) | Languages, RTL, accessibility coverage and gaps |
| [compatibility.md](compatibility.md) | Supported platforms and compatibility notes |

## Security and the elevation channel

| Document | Responsibility |
|---|---|
| [security.md](security.md) | Threat model and security design |
| [protocol.md](protocol.md) | Elevation channel protocol (SEC-001): status DESIGN, partly implemented |
| [trust.md](trust.md) | Trust root, install policy, definitions (SEC-002): status DESIGN |
| [m4-implementation-plan.md](m4-implementation-plan.md) | Work packages and milestone M4a |
| [signing.md](signing.md) | Code signing policy and plan (nothing is signed yet) |
| [../.github/SECURITY.md](../.github/SECURITY.md) | How to report a vulnerability |

## Development, test and release

| Document | Responsibility |
|---|---|
| [dev-setup.md](dev-setup.md) | Prerequisites, commands and contributor rules |
| [testing.md](testing.md) | Test strategy |
| [verification.md](verification.md) | Append-only log of verification evidence and limits |
| [manual-test-plan.md](manual-test-plan.md) | Procedures that need a disposable VM or a person |
| [release.md](release.md) | Packaging, reproducibility and release process |
| [sources.md](sources.md) | Primary sources for mechanisms the product relies on |
| [persian-review.md](persian-review.md) | Process for reviewing the Persian text |

## Spikes

| Document | Responsibility |
|---|---|
| [spikes/s0-nativeaot.md](spikes/s0-nativeaot.md) | NativeAOT feasibility for the elevated worker |

## Maintenance rules

- A new, moved, replaced or archived document is added to or removed from this index in the same change.
- Documents separate existing behavior from planned behavior and mark unverified claims.
- Superseded documents are marked and linked to their replacement; history is not silently deleted.
- Relative links must resolve.
