<!-- SPDX-License-Identifier: GPL-3.0-only -->
# M4 privilege foundation: phase 2 implementation plan

Status: PLAN ONLY (2026-10-09; revised the same day to apply the owner-delegated decisions D-01..D-22 of an unpublished design note, which override earlier text, and the findings of an unpublished design note). Phase 1 (the design documents) is complete only when protocol.md, trust.md and ADR-010..ADR-018 have been reviewed against the review; phase 2 starts only after the owner applies the AGENTS.md exception of ADR-015 and the dev-host guards (WP-0) have landed. No work package below has started. Nothing here authorizes running any elevated code on the development host (AGENTS.md); only unelevated tests run there, VM tests run on disposable VMs through the marker-guarded VM kit.

M4 delivers the channel, trust machinery and tests with ZERO shipped machine definitions in the release set. The first real machine setting is M5, behind the gate in [trust.md](trust.md) 13.4. The feature therefore stays disabled in release builds at the end of M4 and the documentation must say so.

Two milestones (D-14):

- **M4a** (about 30 engineer-days): the full channel carrying ONE harmless typed no-op command (`winora.selftest.noop`, VmTrust flavor only; release worker rejects everything with `worker.unimplemented`) plus the VM experiments that gate the design, before journal v4, definitions, the definition-signature layer or the installer change. CLI-only; no consent window.
- **Full M4** (75 to 90 engineer-days): everything else in section 4. Then the gate list of trust.md 13.4 applies before M5 ships a real machine definition.

## 1. Rules for every work package

1. Read AGENTS.md, protocol.md, trust.md, errors.md, operations.md first.
2. Disjoint file ownership (section 4). A package touches another owner's file only through a written request to that owner.
3. Application references Domain only; Winora.Elevation.Protocol references Domain only; Winora.Elevation.Windows references Protocol and Domain; Infrastructure.* reference Application/Domain (and, for the client-side elevation adapter, Protocol and Elevation.Windows); Desktop, CLI and the Worker are composition roots. The Worker references only Elevation.Protocol and Elevation.Windows and uses no DI container or logging framework (ADR-015; AGENTS.md exception PENDING owner application). Worker command DTOs live in Domain and carry no arbitrary targets or commands.
4. Original text and identifiers only; GPL-3.0-only SPDX header on every new file; user-facing text in .resx (English and Persian satellite) with `Code_*` keys for every new code that a host can show. Worker-silent codes (the worker shows no user text) follow the excluded-key rule of errors.md once approved (WP-A).
5. Pinned central package versions; any new package is an owner-visible change. M4 needs NEW packages, correcting the earlier "needs none": the NativeAOT ILCompiler and runtime packs (if S0 passes) with a lock-file target and notice/SBOM rows, and `System.Security.Cryptography.Cose` for definitions (D-05, WP-C2).
6. Unimplemented behavior fails explicitly (`worker.unimplemented`, `elevation.unavailable`). No stubs that report success.
7. Record status and evidence in technical-docs/verification.md and technical-docs/backlog.md (SEC-001, SEC-002); no claim without evidence.
8. Dev-host guards (WP-0, D-02): no script, test, CI step or documented procedure may execute `Winora.Worker.exe` or `Winora.Worker.dll`; the only exception is the VM kit (`tests/vm/**`), which refuses to run without `WINORA_VM_TESTS=1` and the VM marker file and is allow-listed in the guard test. No test project may reference the production worker project or the real launcher type.
9. No test-only input (`--sandbox-root`, `--database`, `--no-log`, `WINORA_DATA_DIR`, `WINORA_NO_LOG`) reaches a machine-scope or elevated path (security.md "Test inputs and the machine path").

## 2. Order and dependencies

```
WP-0 dev-host guards (FIRST; one change set; no dependency)
  |
  +--> S0 NativeAOT / COSE / crypto-API spike (tools/spikes, throwaway)
  +--> WP-A Domain DTOs, codes, stub retirement (needs only the AOT constraint "no reflection serializers" from S0)
          |
          +--> WP-B protocol library (Elevation.Protocol)      needs S0's crypto-API result
          +--> WP-D0 shared Windows primitives (Elevation.Windows: SDDL builder, identity reader, image guard)
                  |
                  +--> WP-E worker  (needs A, B, D0, S0)
                  +--> WP-D client side (availability, attestation reader, pipe client, launcher, client adapter)  (needs A, B, D0)
WP-H2 VM kit, M4a subset ......... after E and D          ==> M4a exit (section 4.2)
-------------------------------- M4a ends; the rest starts only after the M4a exit review --------------------------------
WP-C1 definition parser/invariants/floor logic (A)  -> WP-C2 COSE signature layer and signing skeleton (C1, S0, key custody)
WP-F orchestration + journal v4 (A, B, C1, D) -> WP-J consent window, CLI machine verb, resx (A, F contracts)
WP-G per-machine installer and pipeline (WP-0, S0, A; D-07 outcome; needs the final worker binary shape)
WP-H1 unelevated test consolidation (integration only)   WP-H2 full VM runs (E, D, G, F)
WP-I docs (continuous; closes last)   WP-K independent review and fix (after the implementation diff is stable)
```

Critical path (M4a): WP-0, S0, A, B, D0, E and D in parallel, H2 subset. Critical path (full): M4a exit, F, J, G, H2 full, K. C1/C2 and G can run in parallel with F once A lands and the M4a exit review is done. G is gated on S0 and WP-0 because it depends on the worker's final binary shape (runtime, subsystem) and on the manifest decision.

## 3. Contracts (documentation only; these are not files)

Namespaces and project placement are proposals. The byte-level shapes (snapshots with a `targetKind` byte, `MachineValue`, batch digest, plan tokens) are normative in protocol.md Appendix A; the blocks below show ownership and placement, not encodings.

```csharp
// ---------- Domain (WP-A): src/Winora.Domain/Elevation/*.cs ----------
public enum MessageType : byte { ClientHello=0x01, ServerHello=0x02, Finished=0x10, BatchBegin=0x20, BatchAccepted=0x21,
                                 Prepare=0x30, Prepared=0x31, Commit=0x32, Outcome=0x33, Abort=0x40, Bye=0x41 }   // authoritative list: protocol.md
public enum ElevationOutcomeStatus : byte { Applied=1, AlreadyConfigured=2, Failed=3, Conflict=4, Refused=5, Partial=6 }
public enum PrepareMode : byte { ApplyChoice=0, Restore=1 }
public sealed record BatchItem(Guid OperationId, string DefinitionId, uint DefinitionVersion, PrepareMode Mode, string? ChoiceId,
                               MachineValue? RestoreValue, ReadOnlyMemory<byte> PlanTokenDigest);     // committed by the batch digest (R-08)
public sealed record BatchBegin(IReadOnlyList<BatchItem> Items);                                      // 1..8
public sealed record PrepareRequest(Guid OperationId, ReadOnlyMemory<byte> PlanTokenDigest);
public sealed record MachineSnapshot(byte TargetKind, bool Exists, /* registry or service layout per Appendix A */ ReadOnlyMemory<byte> Canonical);
public sealed record PreparedReply(Guid OperationId, ElevationOutcomeStatus Status, string Code, MachineSnapshot Observed,
                                   ulong KeyRevision, bool WillChange, ReadOnlyMemory<byte> PreparedDigest);   // receipt field only if protocol.md keeps receipts (R-29)
public sealed record CommitRequest(Guid OperationId, ReadOnlyMemory<byte> PreparedDigest);
public sealed record OutcomeReply(Guid OperationId, ElevationOutcomeStatus Status, string Code, MachineSnapshot After, ulong KeyRevision);
public sealed record MachineValue(MachineValueKind Kind, long? Number, string? Text);
public sealed record MachineSettingDefinition(/* trust.md section 7.2 */);
public static class ElevationLimits { public const int MaxFrame = 65536; public const int MaxBatch = 8; public const int MaxFramesPerDirection = 64; /* timers: protocol.md 13 */ }
// Errors.cs additions: protocol.md section 17. WorkerRequest / WorkerProtocol (foundation validator stub) are removed with tests migrated (D-16).

// ---------- Winora.Elevation.Protocol (WP-B, WP-C): src/Winora.Elevation.Protocol/** ; references Domain only; no Windows API ----------
public interface IClock        { long MonotonicMilliseconds { get; } }                                  // Stopwatch-backed; fake in tests
public interface IRandom       { void Fill(Span<byte> buffer); }
public interface IFrameChannel { ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);
                                 ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);   // THE transport seam (pipe, in-memory, later a service)
                                 void Close(); }
public sealed record PeerIdentity(uint ProcessId, ulong StartFileTime, string UserSid, string LogonSid, uint SessionId);
public sealed class FrameCodec { /* seal/open, strict direction-separated counters, size caps */ }
public sealed class HandshakeEngine { /* ECDH P-256, transcript incl. flavor byte, HKDF; role = Client | Worker */ }
public static class WireCodec { /* Encode/Decode per Appendix A; strict grammar, bounds, no trailing bytes */ }
public sealed class WorkerSession { /* worker-side state machine over IFrameChannel; handler registry empty in production => worker.unimplemented */ }
public sealed class ClientSession { /* client-side state machine over IFrameChannel */ }
public interface IWorkerTarget { PreparedReply Prepare(MachineSettingDefinition d, PrepareRequest r);  OutcomeReply Commit(MachineSettingDefinition d, PreparedReply p); }
public interface IDefinitionSetVerifier { DefinitionSetResult Verify(ReadOnlySpan<byte> setBytes, ReadOnlySpan<byte> coseSign1); }   // WP-C2: COSE_Sign1 ES256 detached, verify before parse
public interface IAntiRollbackStore { FloorRecord Read();  bool TryAdvance(FloorRecord expected, FloorRecord next); }              // worker only
public static class RollbackPolicy { public static string? Check(FloorRecord floor, long serial, ReadOnlySpan<byte> hash); }
// ReceiptMac: kept only if protocol.md keeps receipts; the review recommends removal (R-29).

// ---------- Winora.Elevation.Windows (WP-D0, WP-E, WP-D): src/Winora.Elevation.Windows/** ----------
public interface IProcessIdentity { PeerIdentity? ReadOwn(); PeerIdentity? ReadPipeClient(SafePipeHandle pipe); string? ReadImagePath(uint pid); bool IsElevated(uint pid); }
public interface IImageVerifier   { ImageCheck VerifyOpenOnce(string expectedCanonicalPath, out ImageHandle held); }   // protocol.md 4.3, ancestors to the volume root
// pipe server/client with the SDDL builder, hardening bootstrap, mitigation table, HKLM RegistryValueTarget (VmTrust self-test only in M4a/M4),
// anti-rollback store (HKLM Trust key, one Floor value), Event Log writer (ReportEvent) -- all internal sealed unless listed here.
public interface IWorkerLauncher  { Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken ct); }   // ShellExecuteEx runas, fixed image only; OPEN: stays here (D-03) or moves to the client adapter (ADR-015)
public sealed record LaunchRequest(Guid Rendezvous, PeerIdentity Client, byte[] BatchDigest, int Ops);                // no path, no free text

// ---------- Application (WP-F): src/Winora.Application/Elevation/*.cs ; references Domain only ----------
public interface IElevationAvailability { ElevationAvailability Evaluate(); }          // gates G1..G9; never raises UI or UAC
public interface IMachineChannel        { /* port: connect, Prepare, Commit over typed Domain messages; implemented in the Infrastructure.Windows client adapter */ }
public interface IMachineSettingsExecutor   // the only entry point for Desktop/CLI machine changes; takes no path, root, database, logger-disable or shell-refresh parameter
{
    Task<MachinePlan> PlanAsync(string definitionId, string choiceId, CancellationToken ct);                            // unelevated read
    Task<IReadOnlyList<MachineOutcome>> ApplyBatchAsync(IReadOnlyList<MachinePlan> reviewed, ElevationConfirmation confirmation, CancellationToken ct);
    Task<MachineOutcome> RevertAsync(Guid operationId, ElevationConfirmation confirmation, CancellationToken ct);
    Task<IReadOnlyList<MachineOutcome>> RecoverAsync(CancellationToken ct);                                             // read-only, never launches the worker
}
public sealed record ElevationConfirmation(string BatchDigestShort, bool Interactive);   // executor verifies BatchDigestShort equals the digest of the plans about to be applied (R-35)

// ---------- Infrastructure.Windows client adapter (WP-D): src/Winora.Infrastructure.Windows/Elevation/*.cs ----------
// implements IElevationAvailability and IMachineChannel using Elevation.Protocol + Elevation.Windows; the real launcher type is internal sealed, no InternalsVisibleTo to any test assembly
```

## 4. Work packages

Effort is engineer-days for one engineer including unit tests, excluding review and owner waiting time, taken from the review's section 9.1 (the earlier plan figures were 53 days in total and are replaced). The review's per-package column sums to 82; WP-0 (1.5) and WP-J (5) are not priced there and are allocated here, giving a point estimate of about 88, the upper part of the 75 to 90 range. For one maintainer working with agents the review estimates 14 to 18 weeks elapsed.

| WP | Scope | Files owned (exclusive) | Depends | Effort | Acceptance criteria |
|---|---|---|---|---|---|
| WP-0 | Dev-host guards, FIRST and in ONE change set (D-02). Rewrite the three places that execute the worker so they read the PE instead: `tools/verify_dist.ps1` (lines about 155-157: stop running `Winora.Worker.exe`; read the PE manifest level and subsystem with `System.Reflection.PortableExecutable`; keep the "unsigned" assertion for now), a development tool that is not published (lines 42 and 49: `run(worker, exitcode=3)` and the `.dll` path; replace by a file-existence and PE check), `technical-docs/manual-test-plan.md` (step 5 "Run `worker\Winora.Worker.exe`" and the U-29 row that says "process test only on the dev host"; replace by the PE-inspection step). Add a guard test that scans `tools/`, `tests/`, `.github/` and the manual plan for `Worker.exe`/`Worker.dll` in an executing context (allow-list only for `tests/vm/**`), a reference-graph test (no test project references the production worker project), and a note that the manifest stays `asInvoker` | `tools/verify_dist.ps1`, a development tool that is not published, `technical-docs/manual-test-plan.md` (step 5 and the U-29 row only), `tests/Winora.Guards.Tests/**` (new) | none | 1.5 | Guard test green; the three scripts run clean without starting the worker; nothing in the repo executes the worker image; no UAC-capable path exists yet. Lands before any manifest, launcher or pipe code |
| S0 | NativeAOT spike for the worker with the Windows subsystem: AesGcm, ECDiffieHellman P-256, HKDF, SHA-256, overlapped named pipe via P/Invoke, WinVerifyTrust P/Invoke, `System.Security.Cryptography.Cose` detached verify, `ReportEvent` P/Invoke; record package, MSVC linker prerequisite, lock-file and CI image impact, size, startup, determinism (X18), env-hook behavior to prepare V-09 | `tools/spikes/worker-aot/**` (throwaway, not shipped; never executed elevated) | WP-0 | 3 | Report in verification.md: builds on the CI image, unsupported APIs, COSE package version and stability, decision recorded in ADR-014 (NativeAOT or self-contained fallback). Runs unelevated only |
| A | Domain DTOs, `MachineSettingDefinition` schema, error codes and limits; retire `WorkerRequest`/`WorkerProtocol` (D-16) in one change that also moves the tests, the Worker `Program.cs` use of `WorkerProtocol.MaximumMessageBytes` and the catalog disposition (migration test: persisted `protocol.*` codes stay readable; keep a code only if something still uses it); errors.md rows with Severity; resx keys; Appendix A inputs | `src/Winora.Domain/Elevation/**`, `src/Winora.Domain/Errors.cs` (additions), `technical-docs/errors.md`, `*/Resources/Strings*.resx` (Code_ keys only), `tests/Winora.Domain.Tests/Elevation/**` | WP-0 | 3 (M4a subset 1.5) | Records immutable and bounded; catalog test enforces a resx key for every new host-visible code and handles worker-silent codes by the excluded-key rule; every out-of-grammar id rejected; old stub tests migrated; build and tests green |
| B | `Winora.Elevation.Protocol`: `FrameCodec`, `HandshakeEngine`, `WireCodec`, replay guard, both-side session state machines over `IFrameChannel`, byte-exact test vectors with an independent reference implementation in another language kept in `tools/`, fuzz harness | `src/Winora.Elevation.Protocol/**` (except `Definitions/`), `tests/Winora.Elevation.Protocol.Tests/**`, `tests/fuzz/Winora.ProtocolFuzz/**` | A, S0 crypto result | 8 (M4a subset 5) | U-F01..F09, U-H01..H08, U-Z01..Z03, U-W03..W04 pass; fuzz corpus of every decoder runs without crash or unbounded allocation; at least 10 vectors per structure committed; no Windows API reference (project references enforce it) |
| C1 | Definition parser (strict JSON profile), invariants (allow/deny lists, aliasing, hostile-path corpus), `RollbackPolicy`, floor logic with a fake store, schema fields `restoreCompatible`/`managedBy`/`allowRestore` | `src/Winora.Elevation.Protocol/Definitions/**`, `definitions/machine/**`, `tests/Winora.Elevation.Protocol.Tests/Definitions/**` | A, M4a exit | 2 | U-D01..D04, D05, D07..D12 pass; hostile corpus rejected |
| C2 | Definition-signature layer: COSE_Sign1 ES256 detached verify before parse, pinned keys, `kid`, test-key signer (test project only), MSBuild embedding of the set and signature, a development tool that is not published skeleton that calls an external signer and holds no key; key custody and approver wiring per D-17 | `src/Winora.Elevation.Protocol/Definitions/Cose/**`, a development tool that is not published, embedding items in `src/Winora.Domain/Winora.Domain.csproj` and the Worker project (coordinated with A) | C1, S0, key custody | 3 | U-D01..D03 pass; build fails if the embedded set does not verify with the compiled public key; release configuration contains no test key (assembly scan); signature layer exists before the M5 gate (not in M4a) |
| D0 | Shared Windows primitives in `Winora.Elevation.Windows`: pipe SDDL builder, process/token identity reader, image guard (open-once, share-deny, reparse walk, effective-ACL evaluation from the volume root down, `WinVerifyTrust` on the handle with the fixed flag set), mitigation-table loader | `src/Winora.Elevation.Windows/**` (primitives part), `tests/Winora.Elevation.Windows.Tests/**` | A | 4 (inside the review's D 11) | U-S01..S04, U-I01..I09 pass with fake ACL providers; client open mask test U-S04 against the real SDDL (GUID pipe names, same user) |
| D | Client side: gates G1..G9, attestation reader (share modes of protocol.md section 6), pipe client with the exact desired-access value, `WorkerLauncher` (`ShellExecuteEx runas`, scrubbed environment, fixed path, hidden window), client adapter implementing the Application ports | `src/Winora.Infrastructure.Windows/Elevation/**`, `src/Winora.Elevation.Windows/Launcher/**` (OPEN, see ADR-015), `tests/Winora.Infrastructure.Windows.Tests/Elevation/**`; the existing source-scan test is updated to add the launcher as the single further process-start exception | A, B, D0 | 7 (D0 + D = 11) | U-C01..C09, U-G01..G08, U-P01..P05, U-A03 (one file calls `ShellExecuteEx`) pass; the launcher takes no path or command from callers (signature enforced and scanned); no test starts a process or contacts UAC |
| E | Worker executable and worker-side Windows code: argument parser (exact set, no response files, no environment), hardening bootstrap (token self-check first, mitigation table, DLL directory, job object with process/memory/handle limits), pipe server (flags, SDDL, deadlines, reject budget), attestation writer, early-failure record, `run\` guard, `worker.lock`, no-op handler for VmTrust, empty handler set in release (`worker.unimplemented`), `asInvoker` manifest, Windows subsystem, exit codes. The thin exe contains only `Main`; the session loop is in Protocol | `src/Winora.Worker/**` (including `app.manifest`, `Winora.Worker.csproj`), `src/Winora.Elevation.Windows/Server/**`, test worker project `tests/Winora.TestWorker/**` (non-shipped, refuses to run under Program Files) | A, B, D0, S0 | 10 (M4a subset 7) | U-W01..W17, U-A01, U-A05 pass; assembly scan: allowed namespaces only, no HKCU/HKU/profile/env expansion, no process APIs; PE manifest test asserts `asInvoker` and the Windows subsystem; in-process VmTrust session with a fake target |
| F | Orchestration and journal: `MachineSettingsExecutor` (plan incl. policy check, journal, batch driving, read-back, recovery with the worker-gone gate), journal v4 migration with `simulation.v3.bak` backup, additive transitions for machine rows (`Prepared -> AlreadyConfigured`, `compensation_committed`), drift check, executor refusal of test hooks | `src/Winora.Application/Elevation/Orchestration/**`, `src/Winora.Application/SettingsService.cs` (machine branch, coordinated), `src/Winora.Infrastructure.Persistence/SqliteSettingOperations.cs` and schema files (v4), `technical-docs/operations.md` (machine-row rules, owner request), `tests/**/Elevation/Orchestration/**`, `tests/**/Persistence/V4*` | A, B, C1, D | 10 | U-R01..R16, U-J01..J06, U-X01..X02, U-A04 pass; v3 to v4 migration fixture with rollback on failure; fault injection at every boundary B1..B10 and C1..C5 of trust.md 11.4; Applied only after read-back; recovery never launches the worker and defers while the lock is held |
| J | Consent and presentation: Desktop consent window, CLI `machine` verb (interactive consoles only; the user types the first 8 hex of the batch digest; no `--yes`), resx strings (English, Persian), accessibility and RTL check, dialog content of protocol.md consent section (R-35) | `src/Winora.Desktop/Views/Elevation/**`, `src/Winora.Cli/MachineCommand.cs`, `*/Resources/Strings*.resx` (Elevation_ keys) | A, F contracts | 5 | Content spec satisfied per item; default focus Cancel, no auto-accept timer; keyboard and screen-reader operable; screenshot review on the secure desktop (V-15); CLI refuses non-interactive use. Not part of M4a |
| G | Per-machine installer and pipeline: WiX package, new UpgradeCode, fixed Program Files folder (no `INSTALLDIR`), `run\` folder, `Install` values and Event Log source rows, no custom action, no `Trust` key; `tools/publish.ps1` signing order of trust.md 4.5; `tools/verify_dist.ps1` release checks (signature, timestamp, VmTrust literal, MSI tables, empty release set); a development tool that is not published (VM); CI workflow signing job; supply-chain items D-20 | `installer/Winora.Machine.Installer/**`, `tools/publish.ps1`, `tools/verify_dist.ps1` (after WP-0), a development tool that is not published, `.github/workflows/**` (signing job and D-20 settings files only) | WP-0, S0, A; D-07 outcome | 8 | MSI builds and is inspected read-only; contains no custom action; ICE run recorded from a VM; verify_dist passes on a signed fixture and fails on unsigned release, VmTrust literal or an executed-worker regression; existing per-user installer untouched |
| H1 | Unelevated test tiers consolidated (integration only, packages own their own folders): property/fuzz in CI, deterministic seeds, SDDL and vector fixtures, source-scan tests | `tests/Winora.Integration.Tests/**` | B..F | 3 | Every U-* test in protocol.md 16 exists and passes in CI; test evidence recorded in technical-docs/verification.md |
| H2 | VM kit and runs: scripts and runbook for V-01..V-27, X1-X20, XI-1..XI-8, XE-1..XE-3; VM marker guard; build matrix (22621, 22631, newest, plus an Administrator protection configuration where offered); evidence template | `tests/vm/**`, `technical-docs/manual-test-plan.md` (VM section only) | E, D (M4a subset); G, F (full) | 10 (M4a subset 5) | Scripts refuse to run without `WINORA_VM_TESTS=1` and the marker; every item has steps, expected result and evidence field; M4a subset: V-01, V-02, V-03, V-04, V-06, V-09, V-12..V-15 and X1-X6, X9, X12-X18; full run on at least two builds before any release claim |
| I | Docs: verification.md and backlog.md (SEC-001/SEC-002 status, evidence, limits), release.md/signing.md/release-limits.md updates (including how long a withdrawn definition stays usable), sources.md rows, notices, threat-model refresh | `technical-docs/verification.md`, `technical-docs/backlog.md` (status text), `technical-docs/release.md`, `technical-docs/signing.md`, `technical-docs/release-limits.md`, `technical-docs/sources.md`, `CHANGELOG.md` | continuous | 3 (M4a subset 1.5) | Status words match evidence; no document claims availability of elevation; contradictions with AGENTS.md reported, not resolved silently |
| K | Independent review and fix: external cryptographic review of the channel (D-04), diff review of the implementation, abuse-case table pass/accepted-residual, fixes | none (fix commits in the owning package) | stable diff | 8 (M4a subset 2) | Review report recorded; every AB row passes or has an accepted-residual note; fallback (SSPI Negotiate) decision recorded if the handshake is rejected |

Totals: review column 82 days, plus WP-0 1.5 and WP-J 5 = about 88 (range 75 to 90). The review notes the plan's seven-week shape implied two or three engineers in parallel; one maintainer with agents should expect 14 to 18 weeks.

Ownership notes
- WP-D0 owns the shared identity primitives that WP-E and WP-D consume; D0 lands first, which removes the earlier E-depends-on-D cycle.
- `Winora.Domain.csproj` is touched by A (code) and C2 (embedded resources); they coordinate through one owner (A) merging C2's item group.
- `SettingsService.cs`, the persistence schema and `technical-docs/operations.md` are shared with their owners; WP-F makes only additive changes and migrates existing tests, never reverting others' edits.
- `technical-docs/manual-test-plan.md` is edited by WP-0 (step 5 and the U-29 row) and by H2 (VM section); neither edits the other's part.
- H1 owns integration tests only, not other packages' folders.
- Stub retirement (A) is ONE change set (types, tests, `Program.cs` reference, catalog disposition).

### 4.1 Milestone M4a scope and exit (D-14)

Goal: show on a VM that a client signed with the test chain can launch the VmTrust worker through UAC, verify the image, receive and check the attestation, connect to the DACL'd pipe, complete the transcript-bound handshake, exchange authenticated frames, run the Prepare exchange for one typed no-op operation (`BatchBegin(1)` -> `BatchAccepted` -> `Prepare` -> `Prepared(willChange=0, AlreadyConfigured)` -> `Bye`; it reads and writes nothing) and exit cleanly; and settle every `[U]` that gates the channel design.

Packages: WP-0, S0, A (subset), B (no receipts, no restore), D0, D (gates G1..G9, image guard, attestation, launcher, pipe client, CLI verb only), E (hardening, `run\` guard, pipe server, attestation, early-failure record, no-op handler), H2 subset, I subset, K subset. The Event Log events 1001-1004, 2001-2003, 2006, 2008 are exercised by the no-op. Install on the VM by an administrator script into `%ProgramFiles%\WinoraVm` with default ACLs (no MSI).

Not in M4a: journal v4, definitions or the signature layer, the floor, the installer, the consent window, real settings, the Event Log source MSI rows (registered by the VM script).

Experiments run in M4a: X1-X6, X9, X12-X18 and the channel V-items. Experiments that need artifacts M4a does not build run later with their package: X7 (registry links; needs the HKLM target, WP-E full), X8 (flush; needs a real write), X10/XI-1..XI-7 (need the installer, WP-G), X11, X19 (real signing certificate), X20 (full flow), XE-1..XE-3 (Event Log registration through the MSI).

Effort: about 30 engineer-days (review components: S0 3, A 1.5, B 5, D0 and D 7, E 7, VM subset 5, docs 1.5, review 2 = 32; WP-0 1.5 is additional if priced separately).

Exit criteria: every included U-test green on the development host with no elevation, no UAC, no HKLM writes and no process started by tests; every V result and X observation recorded in verification.md; design documents corrected for every `[U]` whose result differed; an independent read of the protocol sections against the observations. Only then start journal v4 (F), definitions (C1, C2), the consent window (J) and the installer (G).

## 5. Global acceptance for M4 (phase 2 exit)

1. Every U-* test passes in a locked-mode Release build on the dev host without elevation, UAC, HKLM writes or processes started by tests, and no script, test or workflow starts the production worker image (WP-0 guard green).
2. Every V-* test passes on a disposable VM on at least two builds, with evidence in verification.md; any `[U]` item whose result differed has its document section corrected.
3. Release artifacts: signed per policy (D-07), `verify_dist.ps1` passing, no VmTrust literal, worker manifest `asInvoker` and Windows subsystem (read from the PE, never by running it), embedded release definition set empty and verifying.
4. Independent security review of protocol.md and trust.md and of the implementation diff recorded (D-04: cryptographic review of the handshake); abuse-case table has a passing test or an accepted-residual note for each row.
5. technical-docs/backlog.md SEC-001 and SEC-002 completion evidence filled; release-limits.md states "administrator-level changes are not available in this release" until M5 ships a reviewed definition.
6. The M5 gate list of trust.md 13.4 is the exit for shipping the first machine-scope definition; none of it is claimed by M4a.

## 6. Verification evidence to record (technical-docs/verification.md, phase 2)

Test counts per tier, fuzz iterations and seeds, the SDDL string, VM build numbers and result per V item and per X/XI/XE item, ACL dumps, the signing certificate subject and timestamp time (when real signing exists), SignPath answers Q-S1..Q-S4, release set hash and serial, the chosen worker runtime model and measured size/startup, COSE package version, Event Log registration result, and each `[U]` item with its observed result.

## 7. Schedule shape (not a commitment)

Week 1: WP-0, then S0 and WP-A. Weeks 2 to 4: B, D0, then E and D in parallel. Weeks 4 to 6: H2 subset and the M4a exit review. After the M4a exit, weeks 7 to 14: C1, F, J, C2, G in parallel where files are disjoint, H1 continuous, H2 full runs, I continuous. Weeks 15 to 18: K (external cryptographic review lead time is outside engineering control), fixes, final VM runs. Owner answers to section 8 gate the start of D (pins) and G.

## 8. Owner decisions

Status vocabulary: DECIDED (D-nn) means m4-decisions.md decides it; "execution pending" means the decision stands but an action is still open. OPEN means no owner decision exists; a recommendation is given.

| ID | Decision | Status | Resolution or recommendation | Blocks |
|---|---|---|---|---|
| O-1 | Signing certificate, publisher name, key custody | DECIDED (D-07); execution pending | Apply to SignPath Foundation now; fallback OV on a cloud HSM or Azure Artifact Signing; pin by subject + chain + key-hash set; install root AND subject pin; accept pause/revoke risk. Pending: the application and answers Q-S1..Q-S4; no pin constant is written before them. MSI Manufacturer stays the project name (OPEN: confirm) | Release, WP-G pins |
| O-2 | Per-machine install policy | DECIDED (D-08) | Elevation only in the signed per-machine package; portable, per-user and unsigned never elevate; per-user MSI stays; MSIX deferred. OPEN, recommended adopt: the "uninstall per-user first" wording and the `simulation.v3.bak` backup rule (trust.md 3.2, 3.5) | WP-G, release notes |
| O-3 | Anti-rollback storage | DECIDED (D-06) | One `Floor` value in an administrator-ACL'd HKLM key created by the worker; MSI never owns it; protects against non-administrator replay only | WP-C1, WP-E |
| O-4 | Developer override | DECIDED (D-09) | None at runtime; compile-time VmTrust only, on disposable VMs, with the identity details of trust.md 5 | WP-C2, WP-G |
| O-5 | Update channel | DECIDED (D-10) | No in-app updater; GitHub Releases + winget with hashes, SBOM, attestations; later opt-in notify-only check under ADMX policy, never in the worker. Revisit before the first machine-scope definition (revocation lag) | M5 gate |
| O-6 | Worker runtime | DECIDED (D-11), result pending spike S0 | NativeAOT with the Windows subsystem if S0 passes; else self-contained with startup-hook support disabled plus a typed-code exit when hook variables are present (detective only, ADR-014); V-09 proves the choice | WP-E, ADR-014 |
| O-7 | Elevate-without-consent | DECIDED (D-12) | Allow with an in-app warning and a second confirmation; non-interactive CLI refuses | WP-D, WP-J text |
| O-8 | Worker dependency boundary | DECIDED (D-03) | New `Winora.Elevation.Protocol` and `Winora.Elevation.Windows`; Worker references only those; the earlier default (reference Infrastructure.Windows) is withdrawn. OPEN: where the launcher lives (recommendation: client adapter, ADR-015) | WP-D0, D, E |
| O-9 | Approve ADR-015 and the Application wording | DECIDED in substance (D-03); owner confirmation of ADR-015 OPEN | ADR-015 keeps "Application references Domain only" unchanged and amends ADR-001 by adding the two projects (recommendation: approve) | WP-D, F |
| O-10 | AGENTS.md exception for the worker composition root | DECIDED (D-03); PENDING owner application | Text in ADR-015; applied when phase 2 starts (not before) | Phase 2 start |
| O-11 | Manifest level | DECIDED (D-02) | `asInvoker` plus `runas`; WP-0 guards; no script, test or CI step may execute the worker image | WP-0, E |
| O-12 | Signer identity under a shared-subject service | DECIDED (D-07) | Admin-only install root AND subject pin; residual stated; signed release manifest only if the install root proves insufficient | WP-D |
| O-13 | Definition-signature layer and approver model | DECIDED (D-05, D-14, D-17) | Layer (COSE_Sign1 ES256) not in M4a, built before the M5 gate; two-reviewer rule; with one maintainer the machine tier stays closed. OPEN: naming the second reviewer; or accepting a hardware-backed delayed signing flow as substitute | M5 gate |
| O-14 | Machine audit | DECIDED (D-15) | Event Log source `Winora`, redacted fields, stable ids (trust.md 12, ADR-018); no HKLM audit record | WP-D, E, F |
| O-15 | Fate of `protocol.operation/command/feature`; stub removal | DECIDED (D-16) | Remove `WorkerRequest`/`WorkerProtocol`; keep a code only if used; migration test for persisted codes | WP-A |
| O-16 | Platform stance | DECIDED (D-13) | Windows 11 24H2 primary; 22621/22631 tested; Administrator protection and Smart App Control as typed codes until VM results; Windows 10 and ARM64 unsupported | WP-D, H2 |
| O-17 | Restore across definition versions | DECIDED (D-18) | `restoreCompatible`; revoked without `allowRestore` refuses restore with a typed code | WP-C1, F |
| O-18 | First milestone | DECIDED (D-14) | M4a (about 30 engineer-days) before the full plan; gate before the first machine-scope definition (trust.md 13.4) | Plan shape |
| O-19 | HKCU flush/drift verification | DECIDED (D-19) | Separate task before M5 (not in this plan's packages) | Separate task |
| O-20 | Cryptographic review of the channel | DECIDED (D-04); reviewer OPEN | External review is a gate before the first machine-scope definition; recommendation: identify and book the reviewer during M4a so lead time does not block M5; fallback SSPI Negotiate if rejected | WP-K |
| O-21 | COSE package unusable (GA version or NativeAOT) | OPEN | Recommendation: if S0 or the version check fails, verify a raw detached ECDSA P-256 signature over the domain-separated bytes with the same pinned keys; needs owner approval because D-05 names COSE_Sign1 | WP-C2 |
| O-22 | Public export of m4-review, review-findings, research | DECIDED (D-22) | Keep out of the public export until the corrections land | Release |
| O-23 | Supply-chain controls and enterprise order | DECIDED (D-20, D-21) | Adopt D-20 now; D-21 order is the enterprise backlog | WP-G, I |

## 9. Open risks

1. Several protocol assumptions are unverified OS behavior (`[U]`); the M4a VM runs may force design changes (environment inheritance, process/token query across integrity, creator access on the pipe, mitigation policy availability, attestation sharing).
2. NativeAOT feasibility (S0) and the COSE package under NativeAOT are unproven; the fallback is weaker against environment injection (detective only).
3. The estimate (75 to 90 days) is several times the earlier backlog figure; schedule pressure could tempt skipping VM tiers, which the release gates forbid.
4. SignPath acceptance, pinning behavior and renewal are unknown; signing and installer work depend on them.
5. The per-machine move changes the 0.x installation story; support burden is not quantified.
6. Shared files (`Winora.Domain.csproj`, `SettingsService.cs`, persistence schema, `operations.md`) are conflict-prone across packages despite the ownership table.
7. One maintainer cannot satisfy the two-reviewer rule: M5 cannot ship real machine definitions until a second reviewer exists (trust.md 7.5).
8. External cryptographic review lead time is outside engineering control.

## 10. Self-consistency check (2026-10-09)

Verified against the files in this set: every work package id used here is defined in section 4 (WP-0, S0, A, B, C1, C2, D0, D, E, F, J, G, H1, H2, I, K); every ADR cited (ADR-001, 005, 008, 010 to 016, 018) exists in adrs.md (ADR-017 is the renumbered M0 record); every trust.md section cited (3.2, 3.5, 4.5, 5, 7.5, 11.4, 12, 13.4) exists; D-01..D-22 are each applied or referenced (D-01 ADR-016; D-02 WP-0/ADR-014; D-03 ADR-015; D-04 ADR-010/WP-K; D-05 trust 6/ADR-012; D-06 trust 6.4; D-07 trust 4; D-08 trust 2/3; D-09 trust 5; D-10 trust 4.7; D-11 ADR-014/S0; D-12 trust 2; D-13 trust 13.1; D-14 section 4.1/trust 13.4; D-15 ADR-018/trust 12; D-16 WP-A; D-17 trust 7.5; D-18 trust 6.5/6.6; D-19 O-19; D-20 trust 4.6; D-21 trust 13.2; D-22 trust 13.3).

Unresolved items (not decided by the owner or contradictory; reported to the owner):
1. Launcher location: D-03 puts the launcher in Elevation.Windows, which the worker references; the recommendation is the client adapter (ADR-015, O-8).
2. The self-contained fallback of D-11 cannot prevent startup hooks or profilers that load before `Main`; the environment check is detective only (ADR-014).
3. Experiments X7, X8, X10, X11, X19, X20 cannot run in M4a because the artifacts they test do not exist yet (section 4.1), although D-14 says "X1-X20 in M4a".
4. D-05 requires a COSE package in the elevated image; plan rule 5 earlier claimed no new package (corrected here); GA version and NativeAOT compatibility unverified (O-21).
5. Event Log registration without a custom action and standard-user write access are unverified (XE-1, XE-2); entries are forgeable by local writers.
6. D-17 with one maintainer keeps the machine tier closed, so the M5 gate cannot be met until a second reviewer is named (O-13).
7. The SignPath certificate subject differs from the MSI Manufacturer text; the earlier rule that they match is withdrawn.
8. Estimate: the M4a component sum is 32 days (WP-0 extra), D-14 says about 30; the full sum is about 88 inside the 75 to 90 range.
9. Cross-document dependencies on files this plan does not own: protocol.md (Appendix A, error-code table, flavor byte, no-op set, timers), errors.md (Severity column, `setting.managed_by_policy`, worker-silent codes, `protocol.*` disposition), operations.md (machine-row rules), compatibility.md (build stance of D-13), release.md and signing.md (signing order, SignPath), AGENTS.md (exception text, pending).
