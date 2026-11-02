<!-- SPDX-License-Identifier: GPL-3.0-only -->
# SEC-001: Elevation protocol (design)

Status: DESIGN ONLY, revision 2 (written 2026-10-09). NOT IMPLEMENTED. No pipe, UAC launch, signature check, definition verifier, attestation record or machine-scope mutation exists in the repository; the Worker is still the inert executable described in [security.md](security.md). Nothing in this document may be cited as release evidence. This revision applies the owner-delegated decisions D-01 to D-22 of an unpublished design note, which override earlier text, and the protocol-related findings of the M4 design review. Every code, byte layout and number below is PROPOSED until the work package that implements it has passed its tests and the VM experiments named in section 18.

Companion documents: [trust.md](trust.md) (install location, signer pinning, signed definition set, anti-rollback floor, machine-scope definition admission), [adrs.md](adrs.md) (ADR-010 to ADR-014 and the ADR for the worker project graph), [errors.md](errors.md), [operations.md](operations.md) (state and durability invariants), [m4-implementation-plan.md](m4-implementation-plan.md) (work packages, milestone M4a), [security.md](security.md) (M4 design), an unpublished design note. The review that produced finding ids `R-nn` is an internal document (D-22) and is cited here only by id.

## 0. Conventions

Verification legend. Every statement about operating-system behavior carries a tag `{STATUS C-nn Xn}`: STATUS is VER (a Microsoft or other primary page was fetched and states it), PLA (plausible: follows from fetched pages or well-known behavior, not stated verbatim), or UNV (unverified: nothing found states it). Two more tags record VM results (verification.md): OBS (observed on a VM, one build unless stated) and REFUTED (a VM observation contradicts the claim). `C-nn` is a row of the ledger in section 18; `Xn` is the VM experiment (X1 to X20, defined in the M4 review and summarized in section 18) that settles it. A claim tagged PLA or UNV is an assumption of the design; when its experiment disagrees, this document must be corrected before the dependent code is released. No claim was tested on any machine: AGENTS.md forbids elevated execution on the development host and this phase writes documents only.

Numbering. Subsections are `N.M`; the numbered items inside sections 11 and 12.2 are cited as `11.4`, `12.2` item 3, and so on; appendix subsections are `A.n`/`B.n`.

Requirement words. MUST, MUST NOT and MAY are used in the RFC 2119 sense for requirements on implementations. "Abort" means: close the pipe, release resources, never write machine state, and report the stated code.

Term index (defined once here so other documents can use the same words).

| Term | Meaning |
|---|---|
| client | `Winora.Desktop.exe` or `Winora.Cli.exe` from the per-machine install, Medium integrity, never elevated |
| worker | `Winora.Worker.exe` from the per-machine install, elevated by `runas`, short-lived, one batch |
| batch | 1 to 8 operations carried by one worker launch (1 in M4a) |
| operation | one definition and one choice (or one restore) on one target |
| definition, definition set | a signed catalog entry naming the target and its admissible values; the set is the collection the worker holds (trust.md). `H_d` = SHA-256 of the set's raw bytes |
| flavor | build identity byte: 0 = release, 1 = VmTrust (D-09) |
| VmTrust | compile-time VM-only configuration with separate identities and keys (D-09) |
| rendezvous R | 128 random bits per launch that name the pipe and the records |
| attestation record | admin-written file `run\<R>.att` proving who owns the pipe (section 6) |
| early-failure record | admin-written file `run\<R>.fail` carrying a closed reason (section 6.4) |
| `run\` | the directory `<InstallRoot>\run` created empty by the installer |
| transcript T | SHA-256 over all handshake inputs (A.6) |
| Result | wire status block: code, severity, retryable, detail (A.3.3) |
| detail token | closed-enum subcode, grammar `[a-z0-9_]{1,24}` (section 17.2) |
| M4a | first milestone: the full channel carrying one harmless typed no-op (section 19) |

## 1. Goals and non-goals

Goals
1. A machine-scope change (HKLM value; service start type in M5) can be made without Winora Desktop ever running elevated.
2. The elevated process is short-lived, accepts only a closed set of typed requests that name a signed definition and a choice, and can be driven only by a verified Winora client on behalf of a user who answered a UAC prompt.
3. Every mutation has its before-state and intent durably journaled by the initiating user's unelevated process before the worker is told to write.
4. Failures, crashes, cancellation and attacks end in an explicit typed outcome or an explicit "uncertain, reconcile by observation" state. Nothing is reported successful without a read-back by the unelevated side.
5. The transport is one seam (`IFrameChannel`) so that a per-machine service can be added later without touching operations, journal or definitions (D-01).

Non-goals (stated so they cannot be assumed)
- Defending against code that already runs inside the verified Winora client process (injection, DLL planting into the client, debugger attach by the same user). See sections 15 and 22.
- Defending against an administrator or anyone who can write to the install directory. They already own the machine.
- Confidentiality of request contents. Requests carry only catalog identifiers; secrecy is not claimed.
- Remote or cross-machine use. Remote clients are rejected at the kernel flag level.
- Per-user (HKCU, profile, file) work. It never crosses this channel (section 11.4).
- Third-party-verifiable proof of what the worker did. The session is authenticated to the live client only (section 12.8).

## 2. Actors, vocabulary and identity

| Actor | Process | Integrity / identity | Role |
|---|---|---|---|
| Client | `Winora.Desktop.exe` or `Winora.Cli.exe` | Medium integrity, initiating user U, logon session L, terminal-services session S | Plans, journals, launches, verifies, drives the channel, reads back state |
| Worker | `Winora.Worker.exe` | High integrity, token user A (A may equal U or be another account; under Administrator protection A is a separate system-managed account {VER C-25 X13}), session S, different logon session | Verifies, applies one batch, exits |
| Journal | SQLite file under `%LOCALAPPDATA%\Winora` owned by U | U | Evidence store, written by the client only; inherits the profile ACL and the design does not rely on that ACL (R-30) |
| Definition set | Signed data embedded in the signed binaries | Authenticode plus the detached definition signature (trust.md) | The only source of targets the worker may touch |
| Trust store | `HKLM\SOFTWARE\Winora\Trust` | Writable by Administrators and SYSTEM only; created by the worker, never by the installer | Anti-rollback floor (trust.md) |

The worker has no database, no network code, no UI and no log file. It returns codes through the channel and, once the installer has registered the source, writes redacted audit events to the Windows Event Log (section 13.3, D-15). The client logs into U's log with the existing redaction rules.

Code layout (D-03). The worker executable references only `Winora.Elevation.Protocol` (pure; references Domain only: framing, handshake, codecs, session state machines, rollback policy, later the definition verifier) and `Winora.Elevation.Windows` (pipe, token and identity checks, image guard, launcher). The session loop is in `Winora.Elevation.Protocol` behind interfaces, so every state machine runs in unelevated tests with fakes and no test ever starts the production worker image.

Identity rules (D-04). Identity comes from the kernel token and the install location, never from a client-supplied value or a PID alone.
- Client identity, as seen by the worker: (a) the token of the pipe client (user SID, logon SID, session id, elevation state, integrity level), read by identification-level impersonation; (b) the image path and Authenticode signer of the process that created the client end of the pipe. These are the authorization inputs.
- PID and process start time are secondary evidence only: a mismatch rejects (tripwire), a match grants nothing. Why: the PID returned for a pipe peer can be made to name a different process by handle hand-off, and PIDs are reused {VER C-08}; the parent PID of a process can be set by its creator {PLA C-07}. The token is bound to the connection by the kernel and is not affected by either trick; the image check inherits the PID weakness, which section 7.3 states and bounds.
- Server identity, as seen by the client: the admin-written attestation record whose `SHA-256(Qs)` must match the key the peer proves possession of (sections 6 and 8). The server's PID and image path are secondary evidence only.

## 3. Availability gates

The client evaluates the gates before it shows any elevation UI and again immediately before launch. A failed gate yields a typed code and no UAC prompt is ever raised.

### 3.1 Gates

| # | Condition required | Failure code (detail) |
|---|---|---|
| G1 | Client image is Authenticode-valid with a pinned signer and runs from the per-machine install root (trust.md) | `elevation.unsigned_build` (unsigned or unpinned); `elevation.install_untrusted` (location, ACL, reparse) |
| G2 | The install is the per-machine package: the HKLM install record matches the client's own path. Portable zip, per-user MSI and unsigned builds never qualify (D-08) | `elevation.install_untrusted` (`location`) |
| G3 | UAC enabled: `EnableLUA` = 1 in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` {UNV C-27 X14}. The persisted value can differ from the running state until reboot; the gate reports what it can read | `elevation.uac_disabled` |
| G4 | Client is not elevated (token elevation false, integrity Medium) | `elevation.client_elevated` |
| G5 | Embedded definition set verifies (signature, serial not below the machine floor) and contains the requested definitions | `definition.signature_invalid`, `definition.rollback`, `definition.unknown`, `definition.revoked`, `definition.restore_incompatible`, `definition.restore_revoked` |
| G6 | Worker image guard succeeds (section 4.3) | `elevation.install_untrusted` |
| G7 | Interactive desktop session, not session 0, not a service | `elevation.unavailable` (`session`) |
| G8 | Client token has a logon SID (the pipe DACL needs it) | `elevation.unavailable` (`logon_sid`) |
| G9 | Client process environment contains none of the runtime-hook variables of section 14.2 (best-effort detection; hygiene gate, does not change the residual of section 15) | `elevation.unavailable` (`environment`) |
| G10 | Platform: 64-bit x64 Windows 11, build 22621 or later; build 26100 (24H2) and later is primary; 22621 and 22631 are tested. Windows 10, builds below 22621 and ARM64 are unsupported (D-13) | `elevation.platform_unsupported` |
| G11 | Administrator protection is not enabled. Until experiment X13 settles behavior, a machine with it enabled is "unverified, typed code" (D-13). The detection method is itself an X13 deliverable {UNV C-25 X13}; if it cannot be detected reliably the gate is skipped and the worker/launch outcome is classified as in 3.3 | `elevation.platform_unverified` |

### 3.2 Consent stance (D-12)

The UAC prompt is not the application's to remove. If the effective setting is "elevate without prompting" (`ConsentPromptBehaviorAdmin` = 0 for an administrator account {UNV C-27 X14}), elevation is ALLOWED, but (a) the UI states in plain words that Windows will not ask for consent on this machine and that the trust anchor is then only the integrity of the Winora client process, and (b) the user must give a second, explicit in-app confirmation before the worker is launched. Declining the second confirmation yields `elevation.declined`. The CLI never elevates non-interactively: unless stdin and stdout are interactive consoles the machine verb refuses with `elevation.confirmation_required`, and `--yes` is not accepted for machine scope. In the normal-prompt case the CLI additionally requires the user to type the first 8 hex characters of the batch digest (the consent content is specified in operations.md and the UX work package; this document only fixes that the digest is shown and checked, section 4.4).

### 3.3 Platform stance (D-13)

- Windows 11 24H2 (build 26100) and later is the primary target; 22621 and 22631 are tested.
- Administrator protection (optional, off by default in 2026) elevates to a hidden, profile-separated account {VER C-25 X13}. The protocol already tolerates A different from U (section 11.4), so the expectation is that it works, but the whole batch flow is "unverified" until X13 runs; the client returns `elevation.platform_unverified` rather than guessing.
- Smart App Control blocks unknown unsigned code and allows signed code under conditions {VER C-31 X20}. Unsigned builds are expected to be blocked and never qualify for elevation (G1). If a signed launch is blocked by policy (Smart App Control, App Control, SmartScreen), the client reports `elevation.blocked`; the exact failure code returned by the shell is `{UNV C-31 X20}`.
- A launch that fails for a reason not covered above is `elevation.launch_failed`.

## 4. Launch

### 4.1 Mechanism (D-02)

The worker's manifest requests `asInvoker` (not `requireAdministrator`). The client launches it with `ShellExecuteEx` and the `runas` verb {VER C-01 X5}, which shows the consent prompt (or the credential prompt for a standard user) and elevates an `asInvoker` image regardless of its manifest {PLA C-02 X5}. `hProcess` is not guaranteed to be returned {VER C-03 X5}, so nothing depends on it. `ERROR_CANCELLED` (1223) maps to `elevation.declined` {PLA C-03 X5}; other failures map to `elevation.launch_failed` or `elevation.blocked`.

Why `asInvoker`: a `requireAdministrator` manifest would raise a UAC prompt whenever a script, test or developer ran the worker image on a development machine. With `asInvoker` a plain launch is harmless: the worker's first action is the token self-check of 14.1, which exits with code 3 without creating any object. No script, test or CI step may execute the worker image (section 20); a unit test parses the built worker's manifest and asserts `asInvoker`; X5 proves that `runas` elevates the `asInvoker` image and that a plain launch does not.

Rejected alternatives: a persistent elevated service or scheduled task (a standing privileged endpoint without per-use consent; D-01); COM elevation moniker (HKLM class registration, still shows the prompt, no gain; D-01); `CreateProcess` with a duplicated or linked token (not available to an `asInvoker` client by design); inherited handles, anonymous pipes or a secret in the environment or command line (all fail across the UAC boundary, 4.2).

### 4.2 What crosses the UAC boundary

- The elevated process is started by the AppInfo service after consent, not as a child created by the client; handle inheritance is impossible {VER C-04 X13}. For a standard user it runs as a different account in a different logon session {PLA C-04 X13}. For an administrator in Admin Approval Mode the token is documented as the elevated token of the same user and is still a different logon session {UNV C-25 X13}; with Administrator protection it is a separate account {VER C-25 X13}.
- The environment the worker starts with may derive from the calling process, from the account profile, or both {UNV C-05 X1}. The design holds either way (section 14.2).
- A command line is assumed readable by same-user Medium observers between process creation and exit and may appear in the consent details, event logs and crash dumps {UNV C-06 X3}. It is therefore public; nothing secret is placed on it.
- The parent process id can be set by the creator and is never used for authentication {PLA C-07}.

Consequence: no confidentiality can be carried from the client to the worker before a cryptographic exchange, so the client has no secret to prove its identity. It is identified by what the kernel reports about the pipe peer (7.2), the worker is identified to the client by something only an administrator-level writer can produce (6), and the key exchange binds the session to both observed identities (8).

### 4.3 Pre-launch image guard (open once, verify on the handle, hold it)

Run by the client for the worker image and, as a self-check, for its own image. TOCTOU-safe.

1. Resolve the expected worker path `<InstallRoot>\worker\Winora.Worker.exe`. `InstallRoot` comes from `HKLM\SOFTWARE\Winora\Install\Root` (written by the installer, non-permanent) and MUST equal `SHGetKnownFolderPath(FOLDERID_ProgramFiles)\Winora` (VmTrust: `...\WinoraVm`) and the directory of the running client's own image minus its last component. Mismatch: `elevation.install_untrusted` (`location`). `InstallRoot` MUST lie on a fixed local NTFS volume (`volume`).
2. Walk the path from the volume root, opening each component with `FILE_FLAG_OPEN_REPARSE_POINT` and rejecting any `FILE_ATTRIBUTE_REPARSE_POINT` (`reparse`). `GetFinalPathNameByHandle` of the file MUST equal the expected canonical path (case-insensitive, no 8.3 names, no `\\?\GLOBALROOT`, drive-letter volume).
3. Open the worker file ONCE: `GENERIC_READ`, share `FILE_SHARE_READ` only (no write, no delete sharing), and keep the handle until the key exchange has completed {PLA C-18 X17}.
4. ACL rule: on every directory from the volume root through `InstallRoot` inclusive, every directory below it (`desktop`, `cli`, `worker`, `run`), every file in `desktop`, `cli`, `worker` and the files listed by the release manifest of trust.md: no principal other than SYSTEM, Administrators and TrustedInstaller holds write, append, delete, delete-child, create-file, create-subdirectory, `WRITE_DAC` or `WRITE_OWNER`; the owner is one of those. Effective ACLs are evaluated (inherited and CREATOR OWNER entries resolved). Failure: `elevation.install_untrusted` (`acl`). Trust boundary (clarification A.11 item 17): the directories strictly ABOVE `InstallRoot` (the volume root and `Program Files` itself) are judged only for rights that let a principal replace or re-permission the child (delete, delete-child, `WRITE_DAC`, `WRITE_OWNER`, generic all); the create-file and create-subdirectory rights are not counted there, because a default `C:\` lets Authenticated Users create folders, which cannot replace `Program Files\Winora`. `InstallRoot` and everything below it get the full rule above. Default Program Files ACLs and non-default volumes are assumed to behave as described {PLA C-32 X12}.
5. On the held handle: `WinVerifyTrust` with `WINTRUST_FILE_INFO.hFile` set so the verified bytes are the held bytes {PLA C-19 X9}, with the flags of A.10 (no revocation, cache-only URL retrieval, no UI). Require a chain to a Windows-trusted root, the code-signing EKU, and signer equality with the client as defined by trust.md (subject pin plus issuing-key pin, never a leaf thumbprint). If chain building would need the network the result is `elevation.install_untrusted` (`chain_offline`), never a retry {UNV C-20 X9}. The check is the embedded-signature check only: Windows inbox binaries are catalog-signed and report as unsigned through it (confirmed on notepad, cmd and powershell). That is acceptable by design: the image guard is applied to Winora's own files (client, worker), which are embedded-signed, and never to inbox binaries (A.11 item 18).
6. Record the file identity (volume serial and 128-bit file id from `GetFileInformationByHandleEx` with `FileIdInfo`) and the SHA-256 of the contents (computed from the same handle).
7. Verify the embedded definition set (trust.md) and compute `H_d`.

### 4.4 Command line (public by design)

```
Winora.Worker.exe --proto 1 --rendezvous <32 hex> --client-pid <dec> --client-start <FILETIME dec>
                  --initiator-sid <S-1-5-21-...> --initiator-logon-sid <S-1-5-5-x-y>
                  --batch-digest <64 hex> --ops <1..8>
```

- `rendezvous` (R) is 128 random bits from the client's CSPRNG per launch. It names the pipe and the records. It is an identifier, not a secret; nothing is derived from it.
- `client-pid` and `client-start` are secondary evidence (section 2, 7.2 step 3). `initiator-logon-sid` is needed to build the pipe DACL before any client connects. `initiator-sid` and `initiator-logon-sid` are compared with the pipe client's token (7.2); they are not authorization on their own.
- `batch-digest` commits the client to the whole batch (A.5.2) before the user sees the consent dialog. The dialog shows its first 8 hex characters; the same digest is in the visible command line.
- Strict parser in the worker: exact option set, each exactly once, bounded lengths, ASCII only, hex and decimal grammar, SID grammar. An unknown option, a duplicate, a response file (`@file`), or any option supplied through the environment: exit code 4 before any object is created. The worker reads no other input (section 20).
- `lpDirectory` is the worker's own directory; `lpFile` is the verified canonical path; the launch uses `SW_HIDE` (the worker has no window, 14.1).

### 4.5 What the client verifies, before and after the launch

Before `ShellExecuteEx`: gates G1 to G11 (twice); the image guard of 4.3 on the worker and on itself; the definition set and `H_d`; `run\` ACL rule (4.3.4); no stale `<R>` record (fresh R, `CREATE_NEW` semantics make a collision visible); the pre-launch dialog shows the batch (operations.md) and the first 8 hex of the batch digest and of `H_d`.

After `ShellExecuteEx` returns: wait up to the attestation deadline (13.1) polling `run\<R>.att` and `run\<R>.fail` (the early-failure record, 6.4); accept the attestation per 6.2; re-open the worker path (not the held handle), obtain its file id and compare with the launch record; run the secondary process checks of 7.1; release the image-guard handle only after the key exchange completed; verify `Finished` (8.2). Any failure: abort, never retry the same R, and classify the row as in section 12.9.

The worker, for its part, repeats the image guard on its own image and requires the same signer as the client (14.1).

## 5. Pipe creation (worker)

Name: `\\.\pipe\Winora.Elev.<R as 32 lowercase hex>` (VmTrust: `\\.\pipe\Winora.VmElev.<R>`). Created by the worker, once, before it writes the attestation.

| Parameter | Value | Reason |
|---|---|---|
| `dwOpenMode` | `PIPE_ACCESS_DUPLEX \| FILE_FLAG_FIRST_PIPE_INSTANCE \| FILE_FLAG_OVERLAPPED` | Creation fails with `ERROR_ACCESS_DENIED` if the name already exists {VER C-09 X2}: squatting is detected before the worker trusts anything. Overlapped I/O for deadlines. |
| `dwPipeMode` | `PIPE_TYPE_BYTE \| PIPE_READMODE_BYTE \| PIPE_WAIT \| PIPE_REJECT_REMOTE_CLIENTS` | Remote clients rejected by the kernel {VER C-09}. Byte mode with our own framing. |
| `nMaxInstances` | 1 | Single connection. |
| Buffers | 64 KiB each | Frame cap. |
| Security attributes | explicit descriptor below, non-inheritable | The default DACL grants Everyone read {VER C-09}. |

Security descriptor (`<LOGON>` is the client's logon SID from `--initiator-logon-sid`):

```
D:P(D;;FA;;;NU)(A;;0x00120083;;;<LOGON>)
```

Access rights are individual rights, not generic masks (R-05).

- `(D;;FA;;;NU)`: explicit deny for the NETWORK well-known SID, defense in depth next to `PIPE_REJECT_REMOTE_CLIENTS`.
- Granted mask `0x00120083` = `FILE_READ_DATA` (0x1) + `FILE_WRITE_DATA` (0x2) + `FILE_READ_ATTRIBUTES` (0x80) + `READ_CONTROL` (0x20000) + `SYNCHRONIZE` (0x100000). It deliberately omits bit `0x4`, which is both `FILE_APPEND_DATA` and `FILE_CREATE_PIPE_INSTANCE`; `FILE_GENERIC_WRITE` includes it and would let the client, or anything in its logon session, create further instances {VER C-09 X2}. It also omits `FILE_WRITE_ATTRIBUTES` (0x100), so a client cannot change pipe state with `SetNamedPipeHandleState`.
- The client therefore opens the pipe with exactly `FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE` = `0x00100083`, using the API overload that takes explicit pipe access rights (.NET: the `NamedPipeClientStream` constructor taking `PipeAccessRights`, with `PipeAccessRights.ReadData | WriteData | ReadAttributes | Synchronize`), together with `SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`. A generic read/write open (`GENERIC_READ | GENERIC_WRITE`, which includes `FILE_APPEND_DATA`) is refused by the DACL; this is intended and tested (U-S04). The client never changes pipe read mode or other pipe state after connecting.
- The ACE names the client's LOGON SID, not only the user SID, so access is confined to processes of the initiating logon session {PLA C-12 X2 X13}. The user SID is checked from the token (7.2).
- No ACE for SYSTEM or Administrators. The creator of the first instance needs no ACE for the creating call {UNV C-10 X2}; if X2 shows otherwise, add `(A;;0x00120083;;;BA)` and record it here.
- No mandatory label. A pipe without a label already denies a Low-integrity client, and the label changed no result for a Medium client, so it is omitted (observed on one VM, verification.md "M4a VM run 3"; claim C-11). The builder keeps an option to add `S:(ML;;NW;;;ME)`; creating a pipe with the label through the .NET security path failed for an unelevated creator, which another way of omitting it avoids.
- A unit test asserts the exact SDDL string for a given logon SID, the absent 0x4 and 0x100 bits, and the NETWORK deny (U-S01 to U-S03).

Connection policy. The worker accepts up to 3 connections that fail the identity checks of 7.2; after a rejected connection it calls `DisconnectNamedPipe` and a fresh `ConnectNamedPipe` on the single instance. "Rejected" counts only connections that completed the connect. The 4th rejection ends the session with `elevation.channel_failed` (`budget_exhausted`), distinct from timeout because a rejected connection consumed the user's consent (R-34). The first connection that passes proceeds, and the worker never calls `ConnectNamedPipe` again. The connect deadline is 13.1.

## 6. Attestation and early-failure records

### 6.1 Problem and design

The client must not send anything, and must not accept a key exchange, with a process that merely owns the pipe name. A same-user attacker can run the genuine signed `Winora.Worker.exe` unelevated, or a patched copy, and it would present the right image path and signature while being fully attacker-controlled. Only elevation separates the attacker from the real worker, and querying the pipe server across the integrity boundary may not be possible {PLA C-16 X3}. So the worker writes a record that only an administrator-level writer can create.

- Location `<InstallRoot>\run\<R>.att`. `<InstallRoot>` is under Program Files; Users have read and execute only, so a Medium process cannot create or modify files there {PLA C-32 X12}. `run\` is created empty by the installer and checked by the image guard (4.3.4). The worker MUST check the `run\` ACL rule itself before it creates anything there (14.1 step 7).
- Contents: exactly 106 bytes (A.8.1): magic, flavor, protocol min and max, R, worker PID, worker start FILETIME, session id, `SHA-256(Qs)`, `H_d`. `Qs` is the worker's ephemeral ECDH public key (section 8).

### 6.2 Client acceptance

Open `run\<R>.att` with `FILE_FLAG_OPEN_REPARSE_POINT`. Require: a regular file with link count 1 inside `run\`; the `run\` ACL rule holds; contents parse exactly (A.8.1); `R` matches; flavor equals the client's; `H_d` equals the client's; session id equals the client's session {UNV C-26 X15}. The file owner SID is logged, never relied on alone: owner is expected to be Administrators or SYSTEM but the default owner of objects created by an elevated token is OS behavior {UNV C-17 X4}; the directory ACL is the real control. Freshness is established by R (random, 128 bits, `CREATE_NEW`) and, where available, by the worker PID and start time matching the pipe peer. Freshness NEVER depends on wall-clock comparison (R-06).

Secondary checks (best effort; failure to perform a check is logged and accepts nothing extra; a mismatching value aborts): `GetNamedPipeServerProcessId` equals the attested PID {PLA C-16 X3}; `GetProcessTimes(pid)` equals the attested start; `QueryFullProcessImageName(pid)` equals the verified worker path. Whether a Medium process may open the elevated worker for `PROCESS_QUERY_LIMITED_INFORMATION` is `{REFUTED C-14 X3}`: it was denied for a Medium client of an administrator in Admin Approval Mode (verification.md "M4a VM run 4"); the image and start-time checks are then skipped.

Primary check: the `ServerHello` key `Qs` hashes to the attested value and the peer proves possession of its private half by decrypting and producing `Finished` (8.2). Squatters cannot forge the record and cannot produce a matching key. The record is public (Users can read it); that is fine because it contains only a hash of a public key.

### 6.3 File sharing, lifetime and cleanup (R-06)

Windows checks sharing in both directions: a later opener must permit the access and sharing the earlier handle holds {PLA C-17 X4}.
- Worker: `CreateFile` with `CREATE_NEW`, `GENERIC_WRITE`, share `FILE_SHARE_READ`, `FILE_FLAG_DELETE_ON_CLOSE`. The worker keeps the handle for its lifetime.
- Client: `GENERIC_READ`, share `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`, `FILE_FLAG_OPEN_REPARSE_POINT`. The client reads the whole record into memory and closes its handle immediately after parsing. A client share mode lacking `FILE_SHARE_WRITE` or `FILE_SHARE_DELETE` would fail against the worker's write and delete access; this is the exact bug the earlier draft had and the reason for the explicit modes (U-C08).
- Delete-on-close deletes the file when the LAST handle closes. The record therefore disappears when the worker has exited and the client has closed its handle; if the worker crashes while the client holds the handle, the file lingers only until the client closes it. No record outlives a launch by design (U-C09). Stale files after abnormal termination (for example power loss) are tolerated by uninstall and by the client, which never trusts a record it did not just see created for its own R.

### 6.4 Early-failure record (R-15)

The client cannot read the worker's exit code (`hProcess` is not guaranteed). Without another channel every early worker failure would look like "no attestation in time" (`elevation.timeout`, retryable), and permanent causes would invite retries, each raising a UAC prompt. So before exiting for ANY reason after argument parsing, the worker attempts to create `<InstallRoot>\run\<R>.fail`: exactly 16 bytes (A.8.2) = magic, version, a reason from the closed list below, and the first 8 bytes of R. It uses `CREATE_NEW`, `GENERIC_WRITE`, share `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`, `FILE_FLAG_DELETE_ON_CLOSE`, and HOLDS the handle for a grace period of 5 seconds before exiting, so a live client can read it; delete-on-close then removes it. The client polls both `.att` and `.fail` every 250 ms during the attestation wait and surfaces the mapped code at once. If the worker cannot create the file (for example the `run\` ACL is weak or the worker is not elevated), the client sees only the timeout; this is the documented residual.

| Reason byte | Token | Client surfaces |
|---|---|---|
| 1 | `busy` | `worker.busy` |
| 2 | `hardening` | `worker.hardening_failed` |
| 3 | `environment` | `elevation.unavailable` (`environment`) |
| 4 | `image` | `elevation.install_untrusted` (`hash` or `signer`) |
| 5 | `run_dir` | `elevation.install_untrusted` (`run_dir`) |
| 6 | `definition` | `definition.signature_invalid` |
| 7 | `rollback` | `definition.rollback` |
| 8 | `floor_store` | `trust.store_insecure` |
| 9 | `set_mismatch` | `definition.set_mismatch` |
| 10 | `squatted` | `elevation.channel_failed` (`squatted`) |
| 11 | `arguments` | `protocol.version` |
| 12 | `internal` | `error.unexpected` |

Retryable only for `busy` (and `timeout`); all other reasons are shown as permanent.

### 6.5 Rejected alternatives and residual

Rejected: a secret the worker proves it holds (the only secrets an administrator could install need installer-time key provisioning and still need a record like this one); a registry attestation under `HKLM\SOFTWARE\Winora\Run\<R>` (viable and the documented fallback if X4 shows the file semantics unworkable; not preferred because delete-on-close gives crash cleanup for free); `%ProgramData%` (non-administrators can pre-create `ProgramData\Winora` before install, so the parent ACL cannot be trusted).

Residual: the record attests that an administrator-level process holding key `Qs` has PID P. It does not attest that the process's code is the real worker; for that the user relies on the UAC prompt (the publisher and path the consent dialog showed {VER C-33 X19}) and the image guard (the file launched is the verified file).

## 7. Mutual verification

### 7.1 Client verifies the server (before sending any byte)

1. Attestation accepted (6.2).
2. Open the pipe with the explicit rights and impersonation level of section 5 (`SECURITY_IDENTIFICATION`; .NET `TokenImpersonationLevel.Identification`) so a malicious pipe server cannot impersonate the client; a server at Identification level can read the token's SIDs but cannot act as the client {VER C-13}.
3. Secondary process checks (6.2).
4. Receive `ServerHello`; require `SHA-256(Qs)` to equal the attested hash and the hello fields to equal the attestation. Failure: close, `elevation.server_untrusted`, never retry the same R.

The client sends `ClientHello` immediately after the connect (it is buffered in the pipe while the worker verifies the client, which may take as long as a cold Authenticode check). It sends nothing else before `ServerHello` has been verified. Its own `Finished` (counter 0) follows that verification and is the first encrypted frame; it sends no request until the worker's `Finished` has been verified too (8.2, A.11 item 6). Section 8.2 and Appendix B.4 are the normative order.

### 7.2 Worker verifies the client (after connect, before processing any request)

For each connection, in order; any failure closes the connection and counts toward the budget of section 5.

1. Token (primary). `ImpersonateNamedPipeClient` at identification level, open the thread token, read user SID, logon SID, session id, elevation type and integrity level, then `RevertToSelf` in a `finally`, doing nothing else while impersonating. Require: user SID equals `--initiator-sid`; logon SID equals `--initiator-logon-sid`; session id equals the worker's own session {UNV C-26 X15}; token not elevated; integrity Medium (Low or High refused: `elevation.client_untrusted`, `token` or `integrity`).
2. Image (primary). `GetNamedPipeClientProcessId` gives pid_obs. Open it (`PROCESS_QUERY_LIMITED_INFORMATION`); `QueryFullProcessImageName` MUST equal `<InstallRoot>\desktop\Winora.Desktop.exe` or `<InstallRoot>\cli\Winora.Cli.exe` (exact, canonical); run the image guard of 4.3.2 to 4.3.5 on it; the signer MUST equal the worker's signer. If the process token of pid_obs can be opened, its user SID and logon SID MUST equal those read in step 1 (this detects a process that is not the one holding the connection); if it cannot be opened {UNV C-15 X3; the tag names the opposite direction, ISS-039} the cross-check is skipped and logged, and the result still rests on steps 1 and 2.
3. Secondary tripwire. pid_obs MUST equal `--client-pid` and the process creation time MUST equal `--client-start`; a mismatch rejects (`elevation.client_untrusted`, `pid`). A match adds no authority: the checks in steps 1 and 2 already decided.
4. Only then read `ClientHello`.

The initiating user U may differ from the worker's token user A (standard user with administrator credentials, or Administrator protection). That is accepted; U and A are both recorded (12.3).

### 7.3 Why PID and start time are secondary (D-04)

`GetNamedPipeClientProcessId` reports the process that created the client end of the pipe; a process that holds a duplicated handle, or a recycled PID, can make that answer name a different process {VER C-08}. If the PID were the identity, an attacker who can duplicate a handle to the genuine client's pipe end could present the genuine client's image to the worker. The token read by impersonation, by contrast, belongs to the connection the kernel is serving. Therefore: authorization rests on the token (who) and, as far as the PID allows, on the image (what); PID and start time only catch accidents and cheap spoofs. The bound on the weakness: an attacker that can obtain `PROCESS_DUP_HANDLE` on the genuine client, or inject code into it, is inside the client's trust boundary, which section 15 lists as out of scope.

## 8. Key exchange and transcript binding

### 8.1 Primitives

Ephemeral ECDH P-256 (`ECDiffieHellman` on `nistP256`, raw secret agreement), SEC1 uncompressed public keys (65 bytes; the point at infinity and any point not on the curve are protocol errors at import), HKDF-SHA256 (RFC 5869), AES-256-GCM with a 12-byte nonce and 16-byte tag, SHA-256. All from the .NET BCL backed by Windows CNG {VER C-34} (the HKDF class is {PLA C-34}); no custom crypto. Primitive choices are fixed by D-04 (no X25519, Ed25519 or ChaCha20-Poly1305 in the baseline). Rejected: static shared secret (not confidential across UAC), TLS over the pipe (large parser surface in an elevated process), Noise libraries (new unaudited dependency in the elevated TCB), RSA key transport (no benefit). An independent cryptographic review of this handshake is a release gate (D-04); if it rejects the custom handshake the fallback is SSPI Negotiate sign/seal over the pipe, keeping the attestation record for server authentication.

### 8.2 Handshake

```
Worker start:     generate (ds, Qs); Ns = 32 random bytes; create pipe; write the attestation record (SHA-256(Qs), H_d, ...)
Client:           verify attestation; generate (dc, Qc); Nc = 32 random bytes; connect
Client -> Worker  ClientHello: protoMin, protoMax, flavor, R, Nc, Qc, H_d, batchDigest, ops
Worker -> Client  ServerHello: protoMin, protoMax, flavor, R, Ns, Qs, H_d, chosen protocol
```

Both sides select `proto = max(common)` and require `proto >= floor`, where `floor` is the definition set's `minProtocol` field (1 in M4a). With one defined version the rule degenerates to equality. Both flavors MUST be equal, else abort (`elevation.channel_failed`, `flavor`). Both compute, with the exact encodings of A.6:

```
Z      = ECDH(own private, peer public)
T      = SHA-256( label || proto || flavor || protoMin/Max of both || R || Nc || Ns || Qc || Qs
                  || clientId || serverId || H_d || batchDigest || ops || suite )
PRK    = HKDF-Extract(salt = T, ikm = Z)
k_c2s  = HKDF-Expand(PRK, "winora/v1/c2s", 32)
k_s2c  = HKDF-Expand(PRK, "winora/v1/s2c", 32)
```

`clientId` is the client identity as the worker observed it (7.2) and as the client knows it about itself; `serverId` is as attested and verified (6, 7.1). Key confirmation: frame counter 0 in each direction is a `Finished` message whose plaintext is `T`. An AEAD failure or a `T` mismatch aborts (`elevation.channel_failed`, `handshake`). This detects any disagreement about version lists (downgrade), flavor, either hello, either identity block (including PID and start, which acts as the secondary tripwire in the transcript), `H_d` or the batch digest. `Z`, the private keys and `PRK` are zeroed after key derivation. There is no receipt key: integrity of every later message is the AEAD tag (12.8).

### 8.3 What this does and does not authenticate

- Server to client: by the attestation (administrator-only writable), carried into the transcript by `Qs`, and by key possession. Strong against same-user attackers.
- Client to server: by the kernel-reported token and the image check of 7.2. The client holds no secret, so the ECDH does NOT cryptographically authenticate the client; it binds the session to the identity the worker observed. A same-user attacker who can inject code into the genuine client is inside the trust boundary (section 15).
- Channel properties added: fresh keys per launch (no cross-session replay), separate keys per direction, strict ordering, no reflection, key confirmation, and a transcript `T` that can be stored (its first 8 bytes) for session correlation.

## 9. Framing

```
Frame  = Header(16) || Ciphertext(N) || Tag(16)      total <= 65 536 bytes, so N <= 65 504
Header = 0x57 0x45 | proto u8 | type u8 | N u32 LE | counter u64 LE
Nonce  = direction u32 LE (1 = client-to-worker, 2 = worker-to-client) || counter u64 LE
Key    = k_c2s for client frames, k_s2c for worker frames        AAD = the 16 header bytes
Counter starts at 0 per direction, strictly +1, hard cap 64 frames per direction
```

- `ClientHello` and `ServerHello` are the only unencrypted frames: type 0x01 and 0x02, counter 0, no tag, `N <= 512`. A plaintext `Abort` (type 0x40, `N <= 96`) is allowed only before key derivation (states AwaitHello and the client's wait for `ServerHello`); it is advisory: the receiver closes, may log the code marked "unauthenticated", and never persists it as an outcome. After key derivation `Abort` is an ordinary encrypted frame.
- Reader procedure: read exactly 16 bytes; validate magic, protocol version, type in the allowed set for the current state, `N <= 65 504` and counter equals the expected next value BEFORE allocating or reading more; then read exactly `N + 16` bytes; decrypt; fail closed on any shortfall, trailing byte, wrong counter or tag failure. No partial acceptance, no resynchronization: any error aborts the session. Details: `framing`, `size`, `counter`, `tag`, `state`.
- The 10 s per-frame deadline of 13.1 runs from the first byte of a frame to its last byte.
- A key is used for one direction of one session only, and 64 frames is far below GCM limits. Tag comparison uses the BCL constant-time verify.
- A side that detects an error sends `Abort` when it can; this is best effort and not needed for safety.

## 10. Messages and session state machine

### 10.1 Message set

All layouts are in A.4. All integers little-endian; identifiers are ASCII `[a-z0-9._]` with a u8 length prefix, at most 64 bytes; unknown types, flags or trailing bytes abort. Every decoder is a pure function of the protocol library (fuzzed, section 16).

| Type | Dir | Name | Purpose |
|---|---|---|---|
| 0x01 | C>W | ClientHello | versions, flavor, R, nonce, public key, `H_d`, `batchDigest`, ops |
| 0x02 | W>C | ServerHello | versions, flavor, R, nonce, public key, `H_d`, chosen version |
| 0x10 | both | Finished | `T`, counter 0 in each direction |
| 0x20 | C>W | BatchBegin | the whole batch: per operation id, definition, version, mode, choice, restore value, plan token |
| 0x21 | W>C | BatchAccepted | ops accepted, worker token user SID (diagnostics) |
| 0x30 | C>W | Prepare | operation id; the worker reads and reports |
| 0x31 | W>C | Prepared | status, Result, observed snapshot, key revision, willChange, preparedDigest |
| 0x32 | C>W | Commit | operation id, preparedDigest |
| 0x33 | W>C | Outcome | status, Result, after snapshot, key revision |
| 0x40 | both | Abort | Result only |
| 0x41 | both | Bye | empty |

The batch holds 1 to 8 operations (exactly 1 in M4a) and operations run strictly one at a time, in the announced order. The message `Prepare` carries only the operation id: everything else was committed in `BatchBegin` and in the batch digest, so a `Prepare` cannot disagree with it.

### 10.2 State machine (worker; the client mirrors it)

| State | Accepts | Deadline (monotonic) | On expiry |
|---|---|---|---|
| AwaitConnect | a pipe connection | 30 s from pipe creation | exit with `elevation.timeout` (`connect`) |
| AwaitHello | `ClientHello` | 10 s after the identity checks pass | abort with `elevation.timeout` (`frame`) |
| AwaitFinished | `Finished` | 10 s | abort with `elevation.timeout` (`frame`) |
| AwaitBatchBegin | `BatchBegin` | 30 s | abort with `elevation.timeout` (`frame`) |
| AwaitPrepare | `Prepare` for the next operation, or `Abort` | 60 s | abort with `elevation.timeout` (`idle`) |
| AwaitCommit | `Commit` for the prepared operation, or `Abort` (a `Prepare` is a state error) | 30 s from sending `Prepared` | abort with `elevation.timeout` (`commit`) |
| AwaitBye | `Bye` | 10 s | exit normally (batch finished) |

Rules. Any message not valid in the current state, a second `BatchBegin`, a `Commit` without a matching `Prepared`, a `Commit` whose `preparedDigest` differs, a `Prepare` for an operation id that is not next, or an operation id repeated: abort without mutation (`state`). If an operation ends with a Prepared status 3, 4 or 5 or an Outcome status 3, 4 or 6, the worker ends the batch: it sends that message, expects `Bye`, and never starts the remaining operations. The client reports them from the batch result as `elevation.batch_aborted` (display only, not persisted, 12.2). If `Prepared.willChange` is 0 (status 2), the client does NOT send `Commit`: the operation is finished and the worker proceeds to the next `Prepare` or to AwaitBye. A `Bye` is exchanged by both sides after the last operation.

### 10.3 Result block, severity and retryability on the wire (R-15)

Every `Prepared`, `Outcome` and `Abort` carries a Result (A.3.3): `code` (a catalog code), `severity` (0 Info, 1 Warning, 2 Error, 3 Critical) and `retryable` (0 or 1), plus a `detail` token (empty or from the closed list of 17.2). The values on the wire are advisory copies: the receiver looks the code up in its own catalog and the session aborts (`elevation.channel_failed`, `code`) if the code is not in the allow-list for that message type and status (table below), the severity or retryable differs from the catalog, or the detail is not allowed for the code. The client never persists a worker-supplied string that did not pass this check.

| Message and status | Allowed codes |
|---|---|
| Prepared 0 (Ready) | `operation.prepared` |
| Prepared 2 / Outcome 2 | `operation.already_configured` |
| Outcome 1 (Applied) | `operation.applied` |
| Prepared or Outcome 3 (Failed) | `access.denied`, `error.unexpected` |
| Prepared 4 / Outcome 4 (Conflict) | `plan.stale` (apply), `operation.external_conflict` |
| Prepared 5 (Refused) | `setting.state_unrecognized`, `operation.not_reversible`, `definition.unknown`, `definition.revoked`, `definition.restore_incompatible`, `definition.restore_revoked` |
| Outcome 6 (Partial) | `operation.verification_failed` |
| Abort | `worker.unimplemented`, `worker.busy`, `worker.hardening_failed`, `definition.*` (all of 17.1), `target.kind_unsupported` (a definition of a kind the worker does not execute), `trust.store_insecure`, `protocol.size`, `protocol.version`, `elevation.client_untrusted`, `elevation.timeout`, `elevation.channel_failed`, `error.unexpected` |

## 11. Authorization model

1. Closed command set. The wire has exactly the messages of 10.1. No message carries a path, registry target, service name, command line, script, executable, environment value or arbitrary bytes beyond the bounded restore value (A.3.2) and the bounded selftest echo (19). The v1 foundation validator stub (`WorkerCommand`, `WorkerRequest`, `WorkerProtocol` in Domain/Contracts.cs) is RETIRED in work package A (D-16); it was never used by any peer. Persisted codes remain readable (17.3).
2. Definition resolution. The worker resolves `(definitionId, definitionVersion)` only in its verified embedded set. Mode 0 (apply): the version MUST equal the set's version for that id. Mode 1 (restore): the version is the one recorded when the value was applied and MUST equal the set's version or appear in the definition's `restoreCompatible` list (11.7). Revoked entries are refused for apply (`definition.revoked`). The target comes from the set; the client never names a target.
   Empty set: a worker whose embedded set has zero definitions (the release flavor in M4a) answers `BatchBegin` with `Abort(worker.unimplemented)` BEFORE any definition lookup (AGENTS.md rule). `definition.unknown` is returned only when the set is non-empty and the id is absent.
3. Machine scope only. Each definition has `scope = machine`. The worker rejects at load time (not only at request time) any set containing a definition whose target hive is not `HKLM`, whose key path starts with `HKEY_` or `\Registry\User`, begins with `\\.`, or contains `..`, `%`, wildcards or non-ASCII, or whose kind is not one of the typed target kinds. User-scope definitions are never in the machine set. Machine definition ids carry the reserved prefix `machine.`; the user catalog refuses that prefix.
4. Why HKCU is refused in the worker. After `runas` the worker's token user A can be a different account than the initiating user U (standard user with administrator credentials, or Administrator protection {VER C-25 X13}). `HKEY_CURRENT_USER` in the worker is A's hive, `%APPDATA%` and `SHGetKnownFolderPath` return A's profile. A user-scope change made by the worker would silently land on the wrong account, report success and change nothing for U. Mapping U's hive in the worker would need new privileges and cross-account hive handling that is not reviewed. Decision: user-scope work always runs in the unelevated client; the worker has no code path that opens `HKEY_CURRENT_USER`, `HKEY_USERS`, profile folders or environment-expanded paths, enforced by a source-scan test (U-A01).
5. Initiating SID comes from the pipe client token (7.2), never from a request field. It is used for audit (diagnostics, Event Log hash) and to enforce that the channel belongs to the launching user's logon session. It is not an authorization input for what may be changed; the signed definition and the UAC consent are.
6. Plan token. `planTokenDigest` (A.5.1) commits to the definition, version, choice, the observed snapshot and the key revision. The worker recomputes it from its own elevated read; a mismatch ends the operation with `Prepared` status 4 and `plan.stale` (apply) or `operation.external_conflict` (restore, see 12.6). Both processes read through un-virtualized, manifested binaries so the same registry view is seen {UNV C-30}; the snapshot records the registry view (64-bit by default).
7. Restore (D-18). A revert is a new operation with mode 1. The restore value comes from the journal, which is untrusted, so the worker accepts it only if (kind, bytes) lies inside the current definition's `restoreDomain` (enumerated values, bounded numeric range, or Absent). Otherwise `Refused` with `operation.not_reversible`. A definition carries `restoreCompatible` (the earlier versions whose rows it can restore) and `allowRestore` (whether restore remains available after revocation). A recorded version that is neither the current version nor in `restoreCompatible` is refused with `definition.restore_incompatible`; a revoked definition without `allowRestore` is refused with `definition.restore_revoked`; neither case guesses (never write a value the current definition does not describe). The client performs the same test from its own copy of the set before it shows any prompt, so these refusals normally cost no UAC prompt. A tampered journal can at worst select another admissible value of the same definition, which the user could have chosen anyway.
8. No security downgrade, no deletion, no cross-definition effects: enforced by the admission rules in trust.md (machine-scope definition admission), not by the worker at runtime; the worker additionally refuses any definition whose target is in its compiled deny list (trust.md).
9. Policy awareness (gate for the first real definition, D-14). Before planning, the client reads the definition's documented policy locations (`managedBy`); if a policy value is present the plan is refused with a "managed by policy" outcome rather than overridden. This is a client-side plan rule and adds no message.

## 12. Two-phase operation, journal and recovery

### 12.1 Why two phases

AGENTS.md and operations.md require before-state and intent to be persisted before any mutation. The worker has no store (no SQLite in the elevated process, by design). The trusted journal owner is the initiating user's process, so the worker MUST NOT write until the client has durably recorded the worker-observed before-state. Hence Prepare, Prepared, Commit.

### 12.2 Row rules for machine rows (R-09)

1. At most one machine row is active at any time, and while a machine row is active no other setting row (user scope included) may be active (the global single-active rule of operations.md).
2. Rows are created one at a time: row 1 is created (state `Prepared`, committed with full sync) BEFORE the launch, so a crash or decline during the UAC wait is visible and recoverable; row k+1 is created immediately before `Prepare` k+1 is sent. Operations not started when a batch ends get no row; the UI reports them from the batch result with `elevation.batch_aborted` (display only).
3. The before-state is written once. The `Prepared` row holds the client's own read; at step 5 of 12.3 the client stores `worker_before_digest` in a separate column and requires it to equal the row's before-state digest. Inequality aborts the batch (row `Failed`, `plan.stale`); the before-state is never rewritten.
4. If `Prepared.willChange = 0` the client ends that operation without `Commit` and the row moves `Prepared -> AlreadyConfigured` (this edge is added for machine rows in operations.md).
5. A Restore operation additionally sets the durable flag `compensation_committed = 1` before its `Commit` is sent (12.6).
6. Journal schema (work package F, not M4a): a new schema version adds `scope` (user or machine), `defset_serial`, `defset_hash`, `batch_digest`, `session_transcript_short` (first 8 bytes of `T`), `worker_before_digest`, `worker_id` (pid, start, SID) and `compensation_committed`. Before migrating, the per-machine build copies the database to `simulation.v3.bak` (atomic temp and move, never overwritten); the migration is one-way. Rows with `scope = machine` whose definition is unknown, revoked, below the machine floor or whose fingerprint differs are quarantined as `journal.untrusted_definition` (existing mechanism). Details belong to operations.md and the journal work package.

### 12.3 Client sequence per operation

```
1  client: plan (unelevated read of HKLM state, definition, choice, plan token)                      -- no mutation
2  client: create journal row Prepared (intent: definition id+version, choice id, set serial+hash,
           batch digest, scope=machine, planTokenDigest, owner pid+start) -- committed, FULL sync
3  client: launch + handshake (once per batch; failure => row Failed with an elevation.* code, nothing changed)
4  client -> worker Prepare; worker reads under its token; replies Prepared
5  client: check Prepared (allow-list, plan-token consistency, observed in restoreDomain), store worker_before_digest
           and diagnostics (worker pid/start/SID); if willChange = 1 set row Executing, if 0 move it to
           AlreadyConfigured and stop (12.2 item 4) -- committed, FULL sync
6  client -> worker Commit(preparedDigest)                       -- only if willChange = 1
7  worker: re-read, compare-and-set(expected = prepared snapshot, desired = choice value), RegFlushKey, re-read; reply Outcome
8  client: independently re-read live state unelevated; classify; journal the outcome -- committed
```

Step 5 commits `Executing` strictly before step 6 is sent: `Executing` means "the worker may have written". Step 8 never trusts the Outcome status alone: `Applied` only if the live value equals the definition's desired value; any disagreement is `Partial` with `operation.verification_failed`, whatever the worker claimed. The client records its OWN post-write key revision from its step 8 read; the `keyRevision` in `Outcome` is diagnostics only (R-13). A forged `Outcome` from a successfully impersonated server can therefore at worst make the client record `Partial` or `Failed`, never a false `Applied`.

### 12.4 Worker behavior

Cancellation is honored only before the write of step 7. The client may send `Abort` or close the pipe before `Commit`: no mutation. After `Commit` is accepted the worker finishes the single atomic write, calls `RegFlushKey` on the target key (R-13) {UNV C-30 X8}, performs the verification read and tries to reply; a broken pipe does not undo the write. Before reading or writing, the worker opens the target key with link following refused and checks that the final canonical key name (from `NtQueryKey`) equals the expected `\REGISTRY\MACHINE\...` path, so a registry symbolic link or a `CurrentControlSet` alias cannot redirect the write {UNV C-29 X7}; a mismatch ends the operation with status 3 (Failed) and `access.denied`. The write is one registry value set (or one `ChangeServiceConfig` call) and the worker never performs multi-step writes inside one operation in M4/M5. The compare is best effort (the registry has no atomic compare-and-set; same limit as ADR-007). A compare mismatch before the write is `Conflict` (nothing written). A differing verification read after the write is `Partial`.

### 12.5 Status values and mapping to journal states (R-14)

Status byte: 0 Ready (Prepared only), 1 Applied, 2 AlreadyConfigured, 3 Failed, 4 Conflict, 5 Refused, 6 Partial.

| Status | Journal state and persisted code |
|---|---|
| Outcome 1 Applied (client read-back agrees) | `Applied`, or `PendingRestart` with `operation.restart_pending` when the definition requires a restart (derived by the client from the definition, never from the worker) |
| 2 AlreadyConfigured | `AlreadyConfigured`, `operation.already_configured` |
| 3 Failed (nothing changed) | `Failed`, the Result code |
| 4 Conflict (state differed from the prepared snapshot, nothing written) | `Failed`, `operation.external_conflict` (restore) or `plan.stale` (apply) |
| 5 Refused | `Failed`, the refusal code |
| 6 Partial (written, verification read differs) | `Partial`, `operation.verification_failed` |

Machine scope performs no shell refresh in M4 or M5; a definition that needs one is not admissible until an ADR defines a journaled client-side refresh.

### 12.6 Restore sequence and plan token (R-10, D-18)

A restore uses the same messages with mode 1 in `BatchBegin`. The client first checks, WITHOUT any launch, that live state equals the journal's recorded after-state AND the key revision equals the recorded revision; if not it refuses with `operation.external_conflict` or `operation.superseded`. The restore `planTokenDigest` (A.5.1) commits to the recorded after-state and revision; the worker recomputes from its live read, so equality means "live equals the recorded after-state and the revision did not move". A difference ends the operation with status 4 and `operation.external_conflict` (not `plan.stale`: it is not retryable). The pre-launch dialog shows the exact value that will be written and its origin ("recorded when this change was applied"). The durable flag `compensation_committed` plays the role of `Executing` for recovery: it is set and committed before the `Commit` of a restore. The restore value is checked by the worker against `restoreDomain` (11.7).

### 12.7 Runtime reversibility (R-11)

Winora applies a machine change only if the prior state is representable and restorable. The worker answers `Prepare` with `Prepared` status 5 (Refused) and `setting.state_unrecognized` when the observed value is not a member of the definition's `restoreDomain` (by kind and exact bytes), and the client's plan performs the same test before showing Apply (a machine holding an OEM value of another type is never changed). The snapshot (A.3.1) carries a target kind byte so that registry values and service start types share one shape family. Delayed automatic start is not one `ChangeServiceConfig` call and is therefore NOT an admissible start kind until a decision defines it as an explicit two-step with its own journal and recovery rows (M5 gate, section 22).

### 12.8 Journal as evidence, and why there are no receipts (R-29)

- The journal belongs to U; only the client writes it. The worker neither reads nor writes it.
- The worker takes no target, no value (outside the bounded, domain-checked restore value) and no authorization from journal data. A tampered journal can hide history, make recovery refuse or quarantine rows, or select another admissible value of a definition the user could already pick. It cannot name a new target or bypass the definition set, the plan token read, the UAC prompt or the restore domain.
- The earlier draft's per-message HMAC "receipts" are removed. Every frame is authenticated by AES-GCM under keys only the two live ends hold, so a receipt authenticated nothing the tag does not and could not convince a third party; the defense against a forged success is the client read-back (12.3 step 8). The journal keeps the first 8 bytes of `T` for correlation. The catalog code `receipt.invalid` is dropped. A future design with third-party-verifiable evidence would need signatures and is out of scope.

### 12.9 Recovery (R-12)

Recovery is unelevated and read-only, never replays a journaled target, never launches the worker. Retry is a new plan, a new launch, a new UAC prompt.

Deferral. A machine row in `Executing` (or a restore row with `compensation_committed`) is not resolved while a worker that might write could be alive. Alive test: `<InstallRoot>\run\worker.lock` opened with read access and full sharing fails with a sharing violation (the worker holds it with no sharing) {UNV C-17 X4}; no worker is launched or signalled. Time test: 5 minutes have elapsed since the row's `Executing` commit, measured against a clock that survives a client restart (system boot time plus the interrupt-time counter stored in the row), or the boot id differs from the recorded one (a reboot ended the worker). Verdicts use value plus key revision, as ADR-007 does for M2.

| Boundary | Row on disk | Recovery verdict (read live HKLM state; worker gone) |
|---|---|---|
| B0 before step 2 | none | nothing |
| B1 step 2 committed | Prepared | live == before: `Failed`, `operation.reconciled_unchanged`; live == desired: `Partial`, `operation.reconciled_partial`; else `Partial`, `operation.external_conflict` |
| B2 launched, no attestation | Prepared | worker exits on a dead client (start-time check); as B1 |
| B3 handshake done | Prepared | read-only so far; as B1 |
| B4 Prepare sent | Prepared | worker only read; as B1 |
| B5 Prepared received, before step 5 | Prepared | no Commit possible; as B1 |
| B6 step 5 committed | Executing | live == before: `Failed`; live == desired and the key revision moved: `Applied`, `operation.reconciled_applied`; else `Partial`, `operation.reconciled_partial` |
| B7 Commit sent | Executing | as B6, after the worker is gone |
| B8 worker wrote, no Outcome | Executing | as B6 |
| B9 Outcome received, not journaled | Executing | as B6 |
| B10 step 8 committed | Applied, Failed, Partial, PendingRestart | startup drift check only: live != recorded after-state marks the row `Partial`, `operation.external_conflict`; nothing is written |

Unreadable target. If the live value cannot be read the row stays open with `operation.recovery_required`. Because the global rule would then block every setting operation, the user has a documented exit: "Mark as reviewed" moves the row to `Partial` (journaled, no registry action) and lifts the block. Machine state is global and journals are per user: another user's Winora shows "changed by another account" from a live read only and refuses revert with `operation.revision_unknown` (operations.md owns the audit-record decision, owner question O-14).

## 13. Failure, abort and timing rules

### 13.1 Timers and limits

| Rule | Value |
|---|---|
| Max frame | 65 536 bytes including header and tag |
| Max operations per batch / batches per worker | 8 (1 in M4a) / one batch, then exit |
| Client waits for the attestation after `ShellExecuteEx` returns | 30 s default (to be replaced by the value measured in X9; cold offline Authenticode may be slow) -> `elevation.timeout` (`attest`) |
| Client waits for `ServerHello` after connecting | 30 s (the worker checks the client image first) |
| Worker waits for an authentic client | 30 s from pipe creation; identity-failed connects allowed: 3 |
| Per-frame read deadline | 10 s, from the first byte to the last byte of one frame |
| Between frames | the state timers of 10.2 (AwaitPrepare 60 s tolerates SQLite full sync and antivirus pauses) |
| Idle (no frame in either direction) | 60 s -> worker exits (`idle`) |
| Prepared -> Commit window | 30 s |
| Total worker lifetime | 5 min hard cap (`lifetime`) |
| Early-failure record grace | 5 s |
| Frames per direction | 64 |
| Concurrent workers | 1 per machine: `<InstallRoot>\run\worker.lock` (`CREATE_ALWAYS`, share none, delete-on-close); a second worker writes the `busy` reason and exits `worker.busy` |

The consent wait inside `ShellExecuteEx` is unbounded by us (it is bounded by the user); the `Prepared` row exists before launch, and a UI Cancel at that stage resolves it `Failed` with `elevation.declined`.

Timers use `Stopwatch` (monotonic). No decision uses wall-clock time, except that Authenticode validation uses the system clock inside `WinVerifyTrust` (AB-25).

### 13.2 Exit codes

For humans and logs only; the client cannot rely on reading them.

| Code | Meaning |
|---|---|
| 0 | batch finished (any outcomes were sent) |
| 3 | not elevated, or unavailable |
| 4 | bad arguments |
| 5 | trust failure (mitigation, image, run directory) |
| 6 | timeout |
| 7 | channel failure |
| 8 | busy |
| 9 | definition or floor refusal |

Every abort path: close the pipe, release the lock and records, zero keys, write the early-failure record when possible, exit with a code above, never write machine state; client side: classify `Failed` if no `Commit` was sent, else leave `Executing` and run recovery (12.9).

### 13.3 Logging and audit (D-15)

- Worker: no file log and no console output (it is a Windows-subsystem executable with no window, 14.1). Diagnostics are the early-failure record and, optionally, an ETW provider writing fixed tokens only. Audit goes to the Windows Event Log under the dedicated source "Winora" once the installer has registered it {UNV C-35} (installer work package; M4a has none). PROPOSED events: 1001 BatchAccepted, 1002 OperationOutcome, 1003 BatchRefused. Fields: operation id, definition id and version, choice id, outcome code, caller SID hash (the first 8 bytes of SHA-256 of the binary user SID, hex). Never values the user typed, snapshot bytes, paths, keys, nonces or `T`. The worker MUST NOT fail if the event source is missing.
- Client: existing redactor; logs operation ids, definition id and version, choice ids, set serial, short hashes, the UAC outcome, worker pid and start, elapsed times and final codes. Never snapshot values, keys, nonces, `T`, SIDs (the redactor tokenizes them) or restore bytes.
- No log line contains externally supplied strings: identifiers pass the `[a-z0-9._]` grammar first, so log injection through ids is impossible.

## 14. Worker runtime

### 14.1 Startup order and hardening

The worker is a Windows-subsystem executable: no console, no window, no stdout or stderr use {OBS C-28 X16}. Order (R-34), each step fatal on failure unless stated:

1. Verify the own token: elevated, High integrity, not impersonating; else exit 3 without creating any object. (A plain, unelevated launch ends here.)
2. Environment rule (14.2).
3. `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR)` {PLA C-24}; current directory = the worker directory; never `LoadLibrary` a relative name. Prefer the link-time equivalent for static imports.
4. Process mitigation policies per the committed table `mitigations.v1` (14.3).
5. Job object: `JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 1`, `KILL_ON_JOB_CLOSE`, process-memory limit (default 64 MiB); a job object has no handle-count or thread-count limit (MS-14), so none is specified; at most one outstanding overlapped I/O; there is no `Process.Start`, `CreateProcess` or `ShellExecute` anywhere in the worker (source-scan test) {PLA C-22 X6}. The definition set (256 KiB cap) is decoded once. Key buffers are cleared with the platform's secure-clear primitive.
6. Strict argument parse (4.4); on failure write the `arguments` reason (6.4) if R itself could be parsed, otherwise write nothing.
7. Take `worker.lock`; verify the definition set and read the floor (trust.md); run the image guard on the own image and the `run\` ACL rule before creating anything in `run\`.
8. Create the pipe (section 5); write the attestation; accept connections.
9. Open the client process with `SYNCHRONIZE`; a watcher exits the worker if the client dies before the first connection. After a connection, the end of the client is found through the pipe (a read that ends or fails) and through the timers of 13.1 (per-frame 10 s, idle 60 s, lifetime 5 min), which bound the session even if the break is not signaled (A.11 item 27).

### 14.2 Environment and runtime (D-11)

Decision. Run spike S0 (NativeAOT with the Windows subsystem). If NativeAOT works, ship it; if not, ship self-contained. In both cases the worker FAILS CLOSED, exiting with the trust-failure code and the `environment` reason, when its environment block contains any runtime-hook variable: a name matching `STARTUP_HOOKS`, `ADDITIONAL_DEPS`, `SHARED_STORE`, `DOTNET_ROOT*`, `*_ENABLE_PROFILING`, `*_PROFILER*`, or ANY name starting `COMPlus_`, `CORECLR_` or `COR_`, and any `DOTNET_*` runtime-configuration knob (benign host variables such as `DOTNET_CLI_*`, `DOTNET_NOLOGO` and `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` are on a closed exception list; the exact list is settled by X1, which dumps the environment that really arrives). The worker enumerates variable NAMES for this rule only; it reads no value for behavior (section 20). The client applies the same list at gate G9.

Do not rely on NativeAOT ignoring these variables until V-09 (X1) proves it: that NativeAOT ignores startup hooks, profiler variables and `DOTNET_ROOT` is only {PLA C-23 X1}; `StartupHookSupport=false` is documented for trimmed apps {VER C-23}. Honest limit: a CoreCLR (self-contained) worker would run a startup hook or profiler BEFORE `Main`, so the fail-closed check in `Main` cannot pre-empt it; for that fallback the effective controls are `StartupHookSupport=false` in the runtime configuration and the runtime knobs that disable diagnostics, with X1 as the proof, and the check in `Main` is defense in depth and a diagnostic. The worker is not releasable until X1 passes with the chosen model. ADR-008's "no trimming" rule applies to Desktop and is not changed by this: the worker contains no WPF, no DI container, no SQLite and no reflection-based serialization.

### 14.3 Mitigation table (R-22)

A committed table `mitigations.v1` lists, per policy: name, required (yes or no), minimum build, expected result, and the VM builds on which it was verified. Required entries MUST succeed or the worker exits with the trust-failure code and the `hardening` reason; other entries are attempted and their results written into the diagnostic tokens. Candidates (each is {UNV X6} until verified in the worker; a PowerShell-child run on build 26200 is in verification.md "M4a VM run 5": all settable except win32k calls, which are refused with error 19, and dynamic code, which was not determined; settable-after-start subset is {VER C-21} only partly): extension points disabled, image load (no remote images, no low-integrity images, prefer System32), strict handle checks, Win32k system calls disallowed, control-flow guard, redirection trust enforce. The child-process policy is not settable at runtime {PLA C-22 X6}; the job object of 14.1 step 5 is the control. Dynamic-code prohibition applies only if the runtime model permits it (NativeAOT yes; CoreCLR JIT no). Entries change only with VM evidence.

### 14.4 Dependency minimization (TCB)

The worker references `Winora.Elevation.Protocol` and `Winora.Elevation.Windows` only (D-03); no SQLite, no `Microsoft.Extensions.*` container or logging framework, no WPF, no Infrastructure assemblies (discovery, shell refresh). A project-reference graph test and a published-assembly namespace scan enforce the allow-list (U-W17). The AGENTS.md exception for the worker composition root is drafted in m4-decisions.md and applied when phase 2 starts.

## 15. Same-user malware: what it can and cannot do

Attacker model: arbitrary code running as the same user U at Medium integrity (typical malware), not inside the Winora client, not administrator.

Can
- Read the process list, probably the worker's command line, enumerate `\\.\pipe\` and learn R.
- Pre-create a pipe named with R before the worker does; connect to the worker's pipe; connect repeatedly (consuming the 3-rejection budget, a denial of service); launch the genuine signed worker itself (it shows a UAC prompt, then exits after 30 s with no authentic client); kill the Winora client; write HKCU environment; drop files next to a portable copy.
- Run an unelevated copy of the real worker image or a patched copy and have it serve the pipe.
- Show a lookalike dialog or a prompt at a misleading time.

Cannot (and why)
- Make the worker mutate anything: the worker needs an authentic client (7.2), a signed definition, a matching plan token and a Commit; none can be produced without driving the real client.
- Impersonate the worker to the client: the client requires the admin-written attestation and the matching `Qs` plus key possession (section 6, AB-02, AB-03).
- Squat the pipe before the worker: `FILE_FLAG_FIRST_PIPE_INSTANCE` makes the worker's creation fail; it writes the `squatted` reason and exits with code 7; the client reports `elevation.channel_failed` (`squatted`) (AB-01).
- Impersonate the client to the worker: token SIDs, session, Medium integrity, image path and signature must all match (AB-21).
- Replay, reorder, truncate or inject frames: AEAD with per-direction keys and strict counters (AB-04 to AB-08).
- Impersonate the client's token via the server: the client connects at Identification level.
- Read or alter the worker or definitions on disk: Program Files ACLs.
- Obtain a false "Applied": the client re-reads live state before journaling success.

Residual (explicit; repeated in section 22)
- UAC consent is the trust anchor. If the user approves a prompt that the attacker triggered, nothing happens unless an authentic client is also driven; the cost is annoyance and a social-engineering window.
- The channel cannot defend against code injected into, or DLL planting for, the genuine client process (same user, same integrity): that gives the attacker everything the client can do, namely request catalog-approved, reversible, bounded machine changes, for which the user still sees a UAC prompt. It is bounded by the closed definition set and the admission rules, not by the channel (D-04).
- The channel also cannot defend against a same-user process that wins a race before the attestation check (for example one that connects to the pipe before the genuine client, or races the launch window); the client sends nothing before the attestation and `ServerHello` checks and the worker rejects non-matching clients, so the effect is a denial of service, but the property is stated as a limit (D-04).
- With "elevate without prompting" or an already-elevated session that auto-approves there is no prompt at all; only client-process integrity remains (3.2).
- A pre-existing administrator-level compromise defeats everything.

## 16. Abuse cases and test plan

Tests labelled U run unelevated with `dotnet test` on any machine, including the development host, and create no persistent state (GUID-named pipes, temp directories, in-memory fakes). Tests labelled V require a disposable VM with elevation, the test signing chain and (for alternate credentials) a second account; none may run on the development host: the VM scripts refuse to start unless `WINORA_VM_TESTS=1` and a VM marker file created by the VM image exist, and they are not part of `dotnet test`. No test, script or workflow starts the production worker image (section 20).

| ID | Attack | Expected defense | Proving test | Residual |
|---|---|---|---|---|
| AB-01 | Pipe squatting: attacker creates `Winora.Elev.<R>` first | First-instance flag: worker creation fails; `squatted` reason; client times out or reads the record; no byte sent | U-P01 (fake pipe factory), V-01 (X2) | DoS: attacker can always make a launch fail |
| AB-02 | Server impersonation: attacker pipe server answers | Client requires an attestation in `run\` with matching `SHA-256(Qs)` and proof of key possession; none: abort before send | U-C01..C04, V-02 (X4) | Depends on Program Files ACLs |
| AB-03 | Unelevated copy of the genuine signed worker serves the pipe | Worker refuses (not elevated, exit 3); if patched to continue it cannot write the attestation | U-W01, V-03 (X3) | None beyond AB-02 |
| AB-04 | Replay of a captured session | New ephemeral keys and nonces per launch; AEAD fails | U-F05 | None |
| AB-05 | Replay of a frame inside a session | Strict counter; duplicate or old counter aborts | U-F03 | Inside client-compromise scope |
| AB-06 | Reorder or drop frames | Counter gap aborts | U-F04 | same |
| AB-07 | Truncated frame or early EOF | Exact-length reads; missing Outcome leaves row `Executing` and triggers read-back recovery | U-F06, U-R04 | None |
| AB-08 | Oversized frame or length bomb | Header validated before allocation, cap 64 KiB | U-F02, U-Z01 | None |
| AB-09 | Malformed fields, charset, trailing bytes, huge lengths | Strict decoders; abort | U-Z02 fuzz and property tests | None |
| AB-10 | Protocol downgrade | Version lists, flavor and chosen version in `T`; floor in the signed set | U-H05 | One version today |
| AB-11 | Definition downgrade or rollback | Serial floor in `HKLM\SOFTWARE\Winora\Trust`; client and worker refuse; `H_d` in `T` | U-D05 (fake store), V-07 | Administrator can reset the floor |
| AB-12 | Definition tamper | Detached definition signature plus Authenticode of the carrier; verify raw bytes before parsing | U-D01..D04 | Signing-key compromise |
| AB-13 | TOCTOU swap of the worker image | Share-deny handle held through launch; admin-only ACLs; file id compare; worker self-verify | U-I03, V-04 (X17) | Needs administrator if ACLs are right |
| AB-14 | Worker or client run from a user-writable location | Install-root rule; elevation unavailable | U-G01..G03, V-05 | None |
| AB-15 | DLL search-order hijack | Admin-only install dir; `SetDefaultDllDirectories`; fixed CWD; few imports; image-load mitigation | U-W05, V-08 | Admin-only dir assumption |
| AB-16 | Runtime hook injection (`DOTNET_STARTUP_HOOKS`, profilers, `DOTNET_ROOT`) | Gate G9; worker fail-closed rule; NativeAOT or hooks disabled; VM proof | U-L03, V-09 (X1) | Release blocked until X1 passes; CoreCLR hooks run before `Main` |
| AB-17 | Worker spawns children or runs commands | Job limit, no process APIs (source scan), mitigation where settable | U-W06, V-10 (X6) | None |
| AB-18 | Parent-PID spoof | PPID unused; PID and start are secondary tripwires only; identity from token and image | U-W02 | None |
| AB-19 | Stale worker from an earlier launch | One batch; idle 60 s; 5 min cap; lock file; delete-on-close records; fresh R; no wall-clock freshness | U-W07, V-11 | None |
| AB-20 | Double-launch race | `worker.lock` exclusive; second exits `worker.busy`; distinct R | U-W08, V-12 | DoS needing repeated consent |
| AB-21 | Client impersonation: attacker connects first | Token, image, signature checks; budget of 3; keeps listening for the real client | U-W03, V-13 | Client-process compromise out of scope |
| AB-22 | Pipe handle duplication to mislead the client PID | Not defended by the PID; token is authoritative; attacker needs a handle to the genuine client | documented, no test | Accepted (inside client boundary) |
| AB-23 | Alternate credentials: U differs from A | HKCU and profile never used in the worker; initiating SID from the pipe token; U and A recorded | U-A01, U-A02, V-14 (X13) | A and U distinct is allowed by design |
| AB-24 | Journal tamper | Worker never reads the journal; targets only from the set; restore domain and version rules; quarantine | U-J01..J04 | History loss; admissible-value selection |
| AB-25 | Clock rollback | Monotonic timers; Authenticode timestamps and pinned signers | U-T01 | A rolled-back clock can make an expired signing cert appear valid |
| AB-26 | Log injection | Grammar-checked ids, fixed tokens, redactor | U-L01, U-L02 | None |
| AB-27 | Resource exhaustion: slow-loris, many connects, huge batch | Caps (8 ops, 64 frames, 64 KiB), per-frame and state deadlines, budget of 3, 5 min cap, job memory limit | U-Z03, U-W09, U-W16 | DoS only via consent-requiring launches |
| AB-28 | Confused deputy via profiles or CLI | Profiles name definitions, never plan tokens or targets; every machine apply re-plans and is reviewed; CLI refuses non-interactive elevation | U-P05, U-X01 | User approves a bad profile |
| AB-29 | UI spoofing of the consent context | Pre-launch dialog lists titles, choices, exact values and the first 8 hex of the batch digest; digest in the visible command line; worker exits in 30 s with no authentic client | manual V-15 (X19), U-X02 | User can approve an unrelated prompt |
| AB-30 | Cancellation mid-write | Cancellation only before Commit; after Commit the write completes; row `Executing`; read-back recovery | U-R02, U-R03, V-16 | Uncertain state until recovery runs |
| AB-31 | Forged success Outcome | AEAD plus client read-back before `Applied` | U-R05 | Transient wrong message possible |
| AB-32 | Attestation file tricks (hardlink, junction, symlink, replace) | Only administrators write `run\`; reparse flag, regular-file, link-count and ACL checks | U-C05..C07, V-02 (X4) | Administrator-level |
| AB-33 | Registry symbolic link in a target path | Open with link semantics refused; canonical-name check after open | V-17 (X7) | Unverified API detail |
| AB-34 | Tag timing | Constant-time compare | U-F07 | None |
| AB-35 | Nonce reuse or counter wrap | Directional keys, direction in the nonce, cap 64 | U-F08 | None |
| AB-36 | Elevation while UAC disabled or client elevated | Gates G3, G4 | U-G04, U-G05 | None |
| AB-37 | Definition-signing key compromise | Revocation by new release plus floor bump; approver rule D-17 | none (process) | Offline revocation lag |
| AB-38 | Test hook reaches a machine path or the worker | Separate machine verb, fail-closed on hooks, worker reads nothing but its option grammar | U-A04..A06, U-L04 | None |
| AB-39 | Restore across definition versions writes a value the current definition does not describe | `restoreCompatible`, `allowRestore`, restore domain; typed refusals | U-D10, U-W18 | User must change the setting in Windows |
| AB-40 | Early-failure record spoof | `run\` is administrator-only; reason maps to a closed code; no free text | U-C10 | Timeout if the worker cannot write |

Test inventory by tier

Unelevated (run in `dotnet test`, allowed on the development host): U-F01..F09 framing, AEAD, counters, size caps, constant-time compare, nonce uniqueness, state deadlines by fake clock; U-H01..H08 ECDH transcript (both sides derive equal keys, any single-field change breaks `Finished`, flavor mismatch, reflection and direction-key swap fail, invalid curve point rejected) and U-H07 Appendix B vector reproduction; U-Z01..Z03 fuzz and property tests of every decoder and the frame reader (deterministic seeds in CI); U-D01..D12 definition verification with generated test keys (when the definition layer exists, not M4a); U-S01..S04 SDDL construction (exact string, 0x4 and 0x100 bits absent, deny NETWORK, label, logon SID substitution) and U-S04 (a real client open with rights 0x00100083 succeeds against the real server SDDL in-process with the same logon SID; a generic read/write open is denied); U-C01..C10 client-side server verification with fake filesystem, process and pipe (missing attestation, wrong key hash, wrong session, wrong flavor, weak ACL, hardlink, mismatched PID, the exact open modes of 6.3 against a temp file, record disappears when both handles close, `.fail` record mapping); U-W01..W18 worker session state machine with fake identity provider and handlers (out-of-order messages, empty set gives `worker.unimplemented`, unknown definition, user-scope definition, restore out of domain or version, Commit without Prepared, reject budget, state timers by fake clock, hostile code or severity from a fake worker aborts, mitigation table with a fake setter, limits constants, project-reference and namespace scans); U-I01..I09 image guard with fakes (reparse, weak ACL on an ancestor or sibling, hash mismatch, removable volume, handle-held sequence, flag constants of A.10); U-G01..G11 availability gates; U-R01..R16 journal and recovery matrix of 12.9 over the SQLite fixtures (work package F); U-J01..J06 journal tamper and v3 backup; U-L01..L05 redaction, environment list, option parser rejects `@file`, duplicates and environment sources; U-A01..A06 source and assembly scans (no HKCU, HKU or profile in the worker; no process APIs; allowed namespaces; machine composition refuses each test hook, table-driven; published assemblies free of types named Fake, Sandbox, Test outside an allow-list); U-P01..P05 real unelevated named-pipe tests with GUID names exercising the real pipe client and server code against fakes of the identity provider; U-X01..X02 CLI and Desktop confirmation text. None of these creates a persistent artifact, touches HKLM, or starts a process other than the test host.

Require a VM with elevation and the test signing chain (never on the development host): V-01 real squat race; V-02 real attestation write by an elevated worker, ACL and creator-handle check on the pipe DACL, client read as standard user, owner SID per scenario; V-03 whether a Medium client can open the elevated worker for limited query and read its token; V-04 image swap attempts while the handle is held; V-05 portable and per-user installs report unavailable; V-06 `ShellExecuteEx` behavior (consent text, publisher display, `ERROR_CANCELLED`, whether `hProcess` is returned, `asInvoker` plain launch not elevated); V-07 floor persistence and rollback; V-08 DLL planting in the worker directory and CWD and PATH; V-09 hook injection through the client environment and `HKCU\Environment`, plus the environment the worker receives; V-10 child process creation attempts fail; V-11 stale worker cleanup after a client kill; V-12 double launch and `busy`; V-13 impersonation attempts by a second process of the same user and of another user; V-14 credential switch (standard user with administrator credentials, and Administrator protection): full batch, no HKCU effect for either account, journal rows carry U and A; V-15 secure-desktop screenshot review; V-16 kill the client or worker at every boundary B0..B10 and run recovery; V-17 registry link refusal; V-18 real signature chain, tampered binary, dev-key build refused; V-19 `EnableLUA` = 0 and `ConsentPromptBehaviorAdmin` = 0 behavior; V-20 Windows build matrix (22621, 22631, 26100 and later; Administrator protection and Smart App Control configurations).

## 17. Error codes (PROPOSED)

All codes below are PROPOSED additions to `technical-docs/errors.md` and `src/Winora.Domain/Errors.cs` for phase 2. Conventions follow errors.md: dotted lowercase code; resource key `Code_` plus the code with dots as underscores; category from {Validation, Conflict, Unsupported, Denied, Transient, Corruption, Cancelled, Internal}; severity from {Info, Warning, Error, Critical}; "retryable" means the same request may succeed unchanged after a wait or a fresh preview and never means automatic retry of a mutation. "Raised by": C = client, W = worker (through a Result, `Abort` or the early-failure record).

### 17.1 Code table

| Code | Category | Severity | Retryable | Raised by | Resx key | User guidance (key text, English) |
|---|---|---|---|---|---|---|
| `elevation.unavailable` | Unsupported | Warning | no | C | `Code_elevation_unavailable` | Administrator changes are not available in this session or installation. |
| `elevation.unsigned_build` | Unsupported | Warning | no | C | `Code_elevation_unsigned_build` | This build is not signed by the expected publisher, so administrator changes are turned off. |
| `elevation.install_untrusted` | Denied | Error | no | C, W | `Code_elevation_install_untrusted` | Winora's installation location or files cannot be trusted for administrator changes. |
| `elevation.uac_disabled` | Unsupported | Warning | no | C | `Code_elevation_uac_disabled` | User Account Control is turned off, so administrator changes are not available. |
| `elevation.client_elevated` | Unsupported | Warning | no | C | `Code_elevation_client_elevated` | Run Winora without administrator rights to make machine changes. |
| `elevation.declined` | Cancelled | Info | no | C | `Code_elevation_declined` | The administrator prompt was declined. Nothing changed. |
| `elevation.launch_failed` | Transient | Error | yes | C | `Code_elevation_launch_failed` | The administrator helper could not be started. Nothing changed. |
| `elevation.blocked` | Denied | Error | no | C | `Code_elevation_blocked` | Windows or your organization blocked the administrator helper. Nothing changed. |
| `elevation.timeout` | Transient | Warning | yes | C | `Code_elevation_timeout` | The administrator helper did not respond in time. Nothing changed; trying again shows the prompt again. |
| `elevation.server_untrusted` | Denied | Critical | no | C | `Code_elevation_server_untrusted` | The administrator helper could not be verified, so nothing was sent to it. |
| `elevation.client_untrusted` | Denied | Error | no | W | `Code_elevation_client_untrusted` | The helper refused the requesting program because it could not be verified. |
| `elevation.channel_failed` | Internal | Error | no | C, W | `Code_elevation_channel_failed` | The secure channel to the administrator helper failed. Review the change before trying again. |
| `elevation.batch_aborted` | Conflict | Warning | no | C | `Code_elevation_batch_aborted` | An earlier change in the same batch did not complete, so this one was not started. |
| `elevation.confirmation_required` | Denied | Warning | no | C | `Code_elevation_confirmation_required` | Administrator changes need an interactive confirmation. Run this in an interactive window. |
| `elevation.platform_unsupported` | Unsupported | Warning | no | C | `Code_elevation_platform_unsupported` | Administrator changes are not supported on this version of Windows. |
| `elevation.platform_unverified` | Unsupported | Warning | no | C | `Code_elevation_platform_unverified` | Administrator changes have not been verified on this Windows configuration, so they are turned off. |
| `definition.unknown` | Validation | Error | no | C, W | `Code_definition_unknown` | The change is not in Winora's trusted list. |
| `definition.revoked` | Unsupported | Warning | no | C, W | `Code_definition_revoked` | The change was withdrawn and is no longer available. |
| `definition.signature_invalid` | Corruption | Critical | no | C, W | `Code_definition_signature_invalid` | Winora's trusted list failed verification. |
| `definition.rollback` | Denied | Error | no | C, W | `Code_definition_rollback` | An older trusted list was refused. |
| `definition.set_mismatch` | Corruption | Critical | no | C, W | `Code_definition_set_mismatch` | The app and the helper disagree about the trusted list. |
| `definition.restore_incompatible` | Unsupported | Warning | no | C, W | `Code_definition_restore_incompatible` | This change was made by an older definition that cannot be safely reverted by Winora. Change the setting in Windows. |
| `definition.restore_revoked` | Unsupported | Warning | no | C, W | `Code_definition_restore_revoked` | This change was withdrawn and cannot be reverted by Winora. Change the setting in Windows. |
| `trust.store_insecure` | Corruption | Critical | no | W | `Code_trust_store_insecure` | The machine trust record has unsafe permissions. |
| `setting.managed_by_policy` | Denied | Warning | no | C | `Code_setting_managed_by_policy` | This setting is managed by a policy. Winora does not change it. |
| `worker.busy` | Transient | Warning | yes | W | `Code_worker_busy` | Another administrator helper is running. Try again in a moment. |
| `worker.hardening_failed` | Internal | Error | no | W | `Code_worker_hardening_failed` | The administrator helper could not start safely on this system. Nothing changed. |

Removed from the earlier draft: `receipt.invalid` (12.8). Codes reused unchanged: `plan.stale`, `operation.external_conflict`, `operation.not_reversible`, `operation.superseded`, `operation.revision_unknown`, `operation.verification_failed`, `operation.reconciled_partial`, `operation.recovery_required`, `setting.state_unrecognized`, `worker.unimplemented`, `protocol.size`, `protocol.version`, `access.denied`, `journal.untrusted_definition`, `error.unexpected`. Persisted informational outcome codes (outside the error catalog) used here: `operation.prepared`, `operation.applied`, `operation.already_configured`, `operation.restart_pending`, `operation.reconciled_applied`, `operation.reconciled_unchanged`.

### 17.2 Detail tokens

Grammar `[a-z0-9_]{1,24}`, drawn from a closed enum list in Domain (a persisted, validated subcode; never free text). Allowed values per code:

| Code | Detail tokens |
|---|---|
| `elevation.unavailable` | `environment`, `logon_sid`, `session`, `testhook`, `uac_state` |
| `elevation.install_untrusted` | `acl`, `reparse`, `location`, `volume`, `run_dir`, `chain_offline`, `hash`, `signer` |
| `elevation.client_untrusted` | `token`, `integrity`, `session`, `image`, `signature`, `pid` |
| `elevation.channel_failed` | `handshake`, `squatted`, `framing`, `size`, `counter`, `tag`, `state`, `code`, `flavor`, `version`, `budget_exhausted` |
| `elevation.timeout` | `attest`, `connect`, `frame`, `idle`, `lifetime`, `commit` |
| all others | none |

Rows persist the code and detail; hosts localize the code and may show the detail in diagnostics only.

### 17.3 Disposition of existing codes (D-16)

`protocol.size` (oversize frame header) and `protocol.version` (negotiation failure, bad arguments) stay in use. `protocol.operation`, `protocol.command` and `protocol.feature` are no longer produced on the wire. They may be removed from the catalog only after a migration test proves that no persisted journal or log fixture carries them; until then they remain readable and localizable and are listed as legacy (owner question O-15). The stub's types `WorkerCommand`, `WorkerRequest` and `WorkerProtocol` are removed in work package A together with their tests and the reference in the Worker `Program.cs` to `WorkerProtocol.MaximumMessageBytes`. In documents, the old name "protocol v1" is replaced by "foundation validator stub"; "proto 1" in this document is the wire version.

### 17.4 Localization

Each code needs its `Code_*` key in the Desktop and CLI `Strings.resx` and `Strings.fa.resx`. The worker shows no user text, so the ErrorTests rule that currently demands every key in the Worker neutral resx needs an excluded-key rule for worker-silent codes (small test edit; owner decision pending), instead of translating them into the worker.

## 18. OS-behavior ledger

Every operating-system claim of this document appears here with its status and the experiment that settles it. Experiments X1 to X20 are defined in the M4 review; their purpose is summarized in the last column. Experiments X10 (installer `Permanent` behavior), X11 (side-by-side MSI installs) and X18 (NativeAOT build determinism) settle installer and release claims rather than protocol claims and have no row below. Per D-14 all twenty run in M4a; X7, X8, X10 and X11 need registry, installer or MSI features that M4a does not ship, so they run with throw-away harnesses and a draft package, not product code (section 19).

| Id | Claim | Status | Experiment | What the experiment does |
|---|---|---|---|---|
| C-01 | `runas` shows the consent prompt, or the credential prompt for a standard user | VER (UAC how-it-works); consent prompt observed 2026-10-11 for an administrator in Admin Approval Mode, one VM (verification.md "M4a VM run 2"); credential prompt for a standard user not run | X5 | launch the VmTrust worker, screenshot the prompt |
| C-02 | `runas` elevates an `asInvoker` image regardless of manifest | PLA (secondary source) | X5 | same launch; also a plain launch is not elevated |
| C-03 | `hProcess` may be absent; decline gives `ERROR_CANCELLED` (1223) | VER for `hProcess` (MS-1), PLA for 1223; a declined prompt ended as `elevation.declined` (exit 130) on one VM, 2026-10-11; the raw error number was not recorded | X5 | cancel the prompt, record the result |
| C-04 | The elevated process is not a child of the caller; no handle inheritance; a standard user gets another account and logon session | VER (not a child), PLA (account) | X13 | record token user and logon SID in each scenario |
| C-05 | The worker's environment derives from the caller, the profile or both | UNV | X1 | set hook variables in the client, dump the worker environment |
| C-06 | A Medium same-user process can read the worker's command line | UNV | X3 (extended to also query the command line) | query from the Medium client |
| C-07 | The parent PID can be set by the creator | PLA | none (the design does not depend on it) | |
| C-08 | `GetNamedPipeClientProcessId` can name a different process via handle hand-off or PID reuse | VER (as attack primitive, secondary source) | none (the design assumes it) | |
| C-09 | Pipe DACL semantics: default DACL grants Everyone read; `FILE_GENERIC_WRITE` includes create-instance; first-instance flag gives `ERROR_ACCESS_DENIED`; remote clients rejected | VER (MS-2, MS-3) | X2 | exact SDDL: open with 0x00100083 succeeds, generic open denied, second create denied |
| C-10 | The creator of the first instance needs no ACE | observed 2026-10-11 on one VM (verification.md "M4a VM run 3") | X2 | create with the exact SDDL |
| C-11 | Label `ME`/`NW` is needed or harmless | observed 2026-10-11 on one VM: harmless and not needed for the Low-integrity denial (verification.md "M4a VM run 3") | X2 | repeat without the label; Low-integrity client denied |
| C-12 | The logon SID ACE confines pipe access to the initiating logon session (also under Administrator protection) | PLA | X2, X13 | second user denied; Administrator protection scenario |
| C-13 | `SECURITY_IDENTIFICATION` lets the server read identity but not act | VER | none (V-13 impersonation attempts) | |
| C-14 | A Medium client can open the elevated worker for limited query and read image name and times | REFUTED for a Medium client of an administrator in Admin Approval Mode: `OpenProcess` returned access denied (verification.md "M4a VM run 4") | X3 | record success per account scenario |
| C-15 | A Medium client can open the worker's token | REFUTED in the same scenario: the process cannot be opened (verification.md "M4a VM run 4") | X3 | `OpenProcessToken` from the Medium client |
| C-16 | `GetNamedPipeServerProcessId` is usable across the boundary | observed 2026-10-11 on one VM (run 4) | X3 | call from the Medium client |
| C-17 | Attestation file semantics: share modes are mutually compatible, delete-on-close lifetime, readable by Users under the inherited ACL, owner SID | observed 2026-10-11 on one VM (verification.md "M4a VM run 3") | X4 | document's modes (sharing violation) versus read+write+delete sharing (success); owner per scenario; kill worker |
| C-18 | A share-deny handle blocks rename and delete of the file and its ancestor directories | PLA; observed 2026-10-11 on one VM (verification.md "M4a VM run 1") | X17 | attempt as administrator while the handle is held |
| C-19 | `WinVerifyTrust` with a file handle verifies the held bytes | PLA (MS-6) | X9 | verify with the specified flags |
| C-20 | The flag set of A.10 avoids network access; an expired leaf with a valid timestamp passes; cold offline time is acceptable | OBS 2026-10-11 on one VM (verification.md "M4a VM run 8"): no network needed for the test chain, 4 ms warm and 79 ms cold online; an expired leaf without a timestamp fails (0x800B0101); the timestamp case and a true cold boot were not run | X9 | offline cold boot timing; clock beyond leaf expiry |
| C-21 | Which mitigation policies are settable after process start | VER partly (MS-4) | X6 | try each policy at start and after a window exists |
| C-22 | The child-process policy is not settable at runtime; a job limit blocks children | PLA | X6 (V-10) | child creation attempts |
| C-23 | Startup hooks are off by `StartupHookSupport=false` and in trimmed apps; NativeAOT ignores hooks, profiler variables and `DOTNET_ROOT` | VER (trimmed, knob), PLA (NativeAOT); PARTLY OBSERVED 2026-10-11 on one VM for the CoreCLR worker (no positive control for the runas case, HKCU environment and profiler variables untested, see ISS-034): the hook runs before `Main`, and `System.StartupHookProvider.IsSupported=false` stops it (verification.md "M4a VM run 7"); through `runas` the hook did NOT reach the elevated worker (run 7 part 2, one VM, administrator in Admin Approval Mode); profiler variables not determined | X1 | benign hook DLL and profiler stub; expect no marker |
| C-24 | `SetDefaultDllDirectories` restricts the DLL search | PLA (MS-5) | none (V-08 DLL planting) | |
| C-25 | Administrator protection elevates to a separate profile account; in Admin Approval Mode the token is the same user's linked token | VER (Administrator protection page), UNV (linked token) | X13 | full batch with the feature enabled |
| C-26 | Session id and logon SID of client and worker are comparable; fast user switching | UNV | X15 | two interactive sessions, record ids and `busy` |
| C-27 | `EnableLUA` persisted versus running state; behavior with `ConsentPromptBehaviorAdmin` = 0 | UNV | X14 | toggle each, relaunch, record gates |
| C-28 | A console-subsystem worker can stall on console selection; a Windows-subsystem worker has no window | observed 2026-10-11 on one VM, classic console host (verification.md "M4a VM run 6") | X16 | click in the console; repeat with the Windows-subsystem build |
| C-29 | Registry symbolic links can be detected and refused (canonical name check) | UNV | X7 | link key at an allowed path; expect `Refused` |
| C-30 | A registry write survives power loss only with `RegFlushKey`; both processes see the same registry view | UNV | X8 | hypervisor power-off with and without flush |
| C-31 | Smart App Control blocks unknown unsigned code and allows signed code; the error returned by the launch | VER (blocking), UNV (error); OBS 2026-10-11 on one VM: state read only (Smart App Control in evaluation mode), the signed worker was not run under it (verification.md "M4a VM run 9") | X20 | signed and unsigned worker under each protection |
| C-32 | Program Files default ACLs are administrator-only; a non-default volume can be permissive | PLA; default ACLs observed 2026-10-11 on one VM (verification.md "M4a VM run 1"); second volume not run | X12 | ACL dump and image-guard walk on two volumes |
| C-33 | The consent dialog shows the verified publisher; its details show the command line | VER (publisher), UNV (details); both observed 2026-10-11 on one VM with the test chain | X19 | screenshot with the test chain |
| C-34 | The .NET BCL gives ECDH P-256, AES-GCM (96-bit nonce) and HKDF on Windows 11 | VER (ECDH, AES-GCM), PLA (HKDF class) | none (U-H07 vectors) | |
| C-35 | An event source registered by an administrator allows the worker to write Event Log entries | UNV | none yet (installer work package; proposed X21) | |

## 19. M4a: the no-op command set

M4a (D-14) is the first milestone: the full channel carrying one harmless typed no-op command, plus the VM experiments, before journal changes, definitions or the installer. This section fixes what M4a includes and excludes; effort and work packages are in m4-implementation-plan.md.

### 19.1 What M4a contains

- One definition, `winora.selftest.noop`, version 1, existing only in the VmTrust flavor (flavor byte 1). Its definition-set bytes are the 28-byte ASCII string `winora-m4a-set:selftest-noop`; `H_d` is the SHA-256 of those bytes (A.9). The release flavor carries the empty set `winora-m4a-set:empty` and therefore answers every `BatchBegin` with `Abort(worker.unimplemented)` (11.2). There is no definition-signature layer in M4a (D-05), no floor, and no `minProtocol` field; `floor` is 1.
- Two choices, both with target kind 2 (`selftest`, A.3.1), no registry, service or file effect:
  - `ping`: expected snapshot is empty (`0200`). `Prepared` returns status 0 (Ready), willChange 1. After `Commit` the worker keeps a one-byte flag in its own memory and returns `Outcome` status 1 (Applied) with after-snapshot "pong". This exercises the Commit window, the Outcome and the kill-at-boundary experiment V-16 without any machine effect.
  - `echo.<p>` with `<p>` in `[a-z0-9]{1,32}`: the choice id (at most 37 bytes) is the bounded payload. `Prepared` returns status 2 (AlreadyConfigured), willChange 0, with the choice id echoed as the snapshot payload; no `Commit` is sent. This proves the worker parsed what the client sent and gives a bounded-payload fuzz target.
- Batch cap exactly 1 operation; mode 0 only. Mode 1 (restore) in M4a: the worker answers `Prepared` status 5 `operation.not_reversible`.
- All messages of 10.1 are implemented, including `Abort`, `Bye`, and the full state machine and timers, but `Commit`/`Outcome` only for `ping`.
- Client: a VmTrust-only CLI verb (a distinct `machine`-style verb, not `settings`; name chosen by the work package) that runs the launch, image guard, attestation, handshake and one operation, and writes its observations to a harness evidence file under a temp directory. No Desktop UI, no journal row.
- Worker startup, hardening, `run\` guard, lock, attestation, early-failure record, Windows-subsystem build and the mitigation table are implemented as specified.
- Install on the VM by an administrator script into `%ProgramFiles%\WinoraVm` with default ACLs (no MSI).
- Tests: every U-test for the included features; the Appendix B vectors reproduced; V-01 to V-04, V-06, V-09, V-12 to V-15 and the experiments X1 to X20 recorded in verification.md.

### 19.2 What is explicitly NOT in M4a

- Any real machine-scope definition, registry read or write on HKLM, service call, or any change to the host.
- Journal schema changes, machine rows, recovery of machine rows, the drift check, journal migration or backup (work package F).
- The definition-signature layer (COSE_Sign1), the release manifest signature, signer pinning beyond what the VM test chain needs, and the anti-rollback floor.
- The per-machine MSI, the Event Log source and audit events, ADMX policy, and any change to `Package.wxs`.
- Restore mode, `restoreCompatible`, `allowRestore`, batches of more than one operation, policy awareness and `managedBy`.
- The Desktop consent window, the production CLI machine verb, localized resources beyond what the harness needs.
- A release worker that does anything: the release flavor rejects everything with `worker.unimplemented`.
- Any claim that elevation works for users: M4a is evidence for the channel only.

### 19.3 Exit criteria for this document

Appendix B reproduced by the reference implementation and reviewed; every `{... Xn}` tag in sections 3 to 14 either confirmed or corrected in the ledger of section 18; the experiments' observations recorded in verification.md; an independent read of this document against the observations. Only then do the journal, definition and installer packages start.

## 20. Test inputs and the machine path

The shipped CLI accepts test conveniences (`--sandbox-root`, `--database`, `--no-log`, `WINORA_DATA_DIR`, `WINORA_NO_LOG`) that the foundation verification scripts use. They MUST NOT influence a machine path or the worker:

1. Machine-scope commands live in a distinct CLI verb and a distinct application service interface whose construction takes no path, root, database, logger-disable or shell-refresh parameter. Its composition obtains paths only from the known local-application-data folder and the install record, and never reads `WINORA_*` variables.
2. Fail closed on presence: when any of `WINORA_DATA_DIR`, `WINORA_NO_LOG` is set, or `--sandbox-root`, `--database` or `--no-log` appears on a machine command, the command exits 2 before reading state with `elevation.unavailable` (`testhook`). The Desktop host treats the same variables as inert with a startup log line.
3. No shared fakes in shipped assemblies; the machine executor takes no shell-refresh dependency at all; a published-assembly scan compares every type named `Fake`, `Sandbox` or `Test` with an allow-list. The machine registry target class has a compile-time constant hive of HKLM, no sandbox-root parameter and no HKCU member.
4. The worker reads no environment variable for behavior and no `WINORA_*` anything. Its only inputs are the strictly parsed option set of 4.4. The single environment access is the NAME enumeration of 14.2, in one allow-listed function. Source scans of the worker projects forbid `GetEnvironmentVariable`, `ExpandEnvironmentVariables`, `GetCommandLineArgs` outside the parser, `AppContext.GetData`, `Registry.CurrentUser`, special-folder access, and `File.*` outside the fixed `run\` operations.
5. VM-only affordances (self-test definitions, the VM marker check, the test-root importer) are compiled only under the VmTrust configuration and removed from release builds by an artifact scan (the literal "vmtrust" and the test key id must be absent from every shipped binary and MSI). No runtime switch, flag or registry value enables them.
6. The real launcher implementation is `internal sealed`, has no `InternalsVisibleTo` to any test assembly, and exactly one source file may call `ShellExecuteEx`. No test assembly references the real launcher or the production worker image; no script, test or workflow starts the worker image (the VM kit is the only allow-listed exception, and it requires the VM marker).

## 21. Microsoft documentation relied on

| Ref | URL | Used for | Status |
|---|---|---|---|
| MS-1 | https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shellexecuteinfow | `runas` verb, `hProcess` not guaranteed | Fetched 2026-10-09 |
| MS-2 | https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights | Default pipe DACL, `FILE_GENERIC_WRITE` includes create-instance, logon SID | Fetched 2026-10-09 |
| MS-3 | https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea | `FILE_FLAG_FIRST_PIPE_INSTANCE`, `PIPE_REJECT_REMOTE_CLIENTS`, instances | Fetched 2026-10-09 |
| MS-4 | https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessmitigationpolicy | Runtime mitigation policies | Fetched (research); which are settable per build is UNV |
| MS-5 | https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-setdefaultdlldirectories | DLL search restriction | Not re-fetched |
| MS-6 | https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_file_info | `hFile` in file verification | Not re-fetched |
| MS-7 | https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid | Client PID | Fetched; spoofing caveat from the Project Zero write-up (https://projectzero.google/2019/09/windows-exploitation-tricks-spoofing.html) |
| MS-8 | https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works | UAC architecture, prompts, not-a-child | Fetched (research) |
| MS-9 | https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography | BCL crypto surface (ECDH, AES-GCM, HKDF) | Fetched (research) |
| MS-10 | https://learn.microsoft.com/en-us/windows/security/application-security/application-control/administrator-protection/ | Administrator protection | Fetched (research) |
| MS-11 | https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview | Smart App Control | Fetched (research) |
| MS-12 | https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options | `StartupHookSupport` | Fetched (research) |
| MS-13 | https://learn.microsoft.com/en-us/windows/win32/secauthz/impersonation-levels | Identification level | Fetched (research) |
| MS-14 | https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information and https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information | Complete list of job limits: no handle or thread limit | Fetched 2026-10-10 |
| MS-15 | https://chromium.googlesource.com/chromium/src/+/main/chrome/elevation_service | Chromium's elevation service: path validation only for trusted locations such as Program Files; also the STIG default ACLs of the volume root and Program Files (https://system32.eventsentry.com/stig/viewer/V-277998, https://www.tenable.com/audits/items/DISA_STIG_Windows_Server_2016_v2r3.audit:268e58bd7aa750bc45bef13acbc34dfc) | Search results read 2026-10-10; the Chromium page itself and the STIG pages were not opened in full |
| MS-16 | https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-readfile and https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-operations | Pipe end behavior | Fetched 2026-10-10; named-pipe client close is not described there |

## Appendix A: normative byte-level encodings

Everything on the wire, in a digest input or in a record uses exactly the encodings below. No padding, alignment, optional field, default or trailing byte exists anywhere. A decoder rejects any deviation (`elevation.channel_failed`, `framing`). Appendix B gives vectors that exercise these rules.

### A.1 Primitives

| Notation | Encoding |
|---|---|
| `u8`, `u16`, `u32`, `u64` | unsigned, fixed width, little-endian. There are no signed integers on the wire |
| `X[n]` | exactly n raw bytes |
| `S(x)` | identifier string: `u8` length (0 to 64) then that many ASCII bytes from `[a-z0-9._]`. Length 0 only where stated. No terminator |
| `B(x)` | blob: `u32` length then the bytes. Used for SIDs (length at most 68), the suite string and nothing else on the wire |
| FILETIME | `u64`, 100 ns ticks since 1601-01-01 UTC |
| SID | the standard binary SID: revision `u8` (1), sub-authority count `u8`, identifier authority 6 bytes BIG-endian, then each sub-authority `u32` little-endian |
| UTF-16 text | UTF-16LE, no BOM, no terminator, even byte length, well-formed (no lone surrogates) |

### A.2 Domain-separation labels and strings

Labels are the exact ASCII bytes shown followed by one NUL byte (0x00), except HKDF info strings, which have no NUL.

| Use | Bytes |
|---|---|
| transcript | `winora/transcript/v1` + 0x00 |
| batch digest | `winora/batch/v1` + 0x00 |
| plan token (apply) | `winora/plan/v1` + 0x00 |
| plan token (restore) | `winora/plan/v1/restore` + 0x00 |
| prepared digest | `winora/prepared/v1` + 0x00 |
| HKDF info, client-to-worker key | `winora/v1/c2s` |
| HKDF info, worker-to-client key | `winora/v1/s2c` |
| suite string in the transcript | `P256-AESGCM-HKDF-SHA256`, encoded with `B()` |

### A.3 Structures

A.3.1 Snapshot (self-delimiting; the first byte is the target kind).

| Kind | Layout |
|---|---|
| 0 registryValue | `u8 0` `u8 keyExists` `u8 valueExists` `u8 view` (0 = 64-bit, 1 = 32-bit) `u8 kind` (0 none, 1 DWORD, 2 QWORD, 3 String) `u16 len` `X[len]`. DWORD: len 4, value little-endian. QWORD: len 8. String: UTF-16 text, len even, at most 1024. kind 0: len 0 |
| 1 serviceStartType (M5, reserved) | `u8 1` `u8 exists` `u8 startKind` (0 boot, 1 system, 2 automatic, 3 demand, 4 disabled) `u8 delayedFlag` (0 only until a decision defines delayed start) |
| 2 selftest (M4a) | `u8 2` `u8 len` (0 to 37) `X[len]` ASCII payload |

Any other kind byte, or a length violating the rules above, is a decoding error.

A.3.2 MachineValue (restore value): `u8 kind` `u16 len` `X[len]`; kind 0 = Absent (len 0), 1 = DWORD (len 4), 2 = QWORD (len 8), 3 = String (UTF-16 text, len at most 1024). Kinds 4 to 255 are reserved and rejected. In mode 0 the field is the Absent encoding `000000`. A total mapping to the existing `RegistryDataKind` is defined by the Domain contract in work package A.

A.3.3 Result: `S(code)` `u8 severity` (0 Info, 1 Warning, 2 Error, 3 Critical) `u8 retryable` (0 or 1) `S(detail)` (length 0 allowed; closed list of 17.2).

A.3.4 Status byte: 0 Ready, 1 Applied, 2 AlreadyConfigured, 3 Failed, 4 Conflict, 5 Refused, 6 Partial. Mode byte: 0 ApplyChoice, 1 Restore. Flavor byte: 0 release, 1 vmtrust.

A.3.5 Operation item (used identically in `BatchBegin` and in the batch digest):
`operationId X[16]` `S(definitionId)` (1 to 64) `u32 definitionVersion` `u8 mode` `S(choiceId)` (1 to 64 in mode 0; length 0 in mode 1) `MachineValue` (Absent in mode 0; the value to write in mode 1) `planTokenDigest X[32]`.

### A.4 Message payloads

Header and framing are in A.7. Payload sizes are fixed except where stated.

| Type | Name | Payload |
|---|---|---|
| 0x01 | ClientHello (181 bytes) | `u8 protoMin` `u8 protoMax` `u8 flavor` `R X[16]` `Nc X[32]` `Qc X[65]` `H_d X[32]` `batchDigest X[32]` `u8 ops` |
| 0x02 | ServerHello (149 bytes) | `u8 protoMin` `u8 protoMax` `u8 flavor` `R X[16]` `Ns X[32]` `Qs X[65]` `H_d X[32]` `u8 chosen` |
| 0x10 | Finished (32 bytes) | `T X[32]` |
| 0x20 | BatchBegin | `u8 ops` (1 to 8) then `ops` operation items (A.3.5) |
| 0x21 | BatchAccepted | `u8 ops` `B(workerTokenUserSid)` |
| 0x30 | Prepare (16 bytes) | `operationId X[16]` |
| 0x31 | Prepared | `operationId X[16]` `u8 status` (0, 2, 3, 4, 5) Result, observed Snapshot, `u64 keyRevision` (registry key last-write FILETIME, or 0), `u8 willChange` (1 only with status 0), `preparedDigest X[32]` |
| 0x32 | Commit (48 bytes) | `operationId X[16]` `preparedDigest X[32]` |
| 0x33 | Outcome | `operationId X[16]` `u8 status` (1, 2, 3, 4, 6) Result, after Snapshot, `u64 keyRevision` |
| 0x40 | Abort | Result |
| 0x41 | Bye | empty |

### A.5 Digests

A.5.1 Plan token.
`planTokenDigest (apply) = SHA-256( "winora/plan/v1\0" || S(definitionId) || u32 version || S(choiceId) || Snapshot || u64 keyRevision )`
`planTokenDigest (restore) = SHA-256( "winora/plan/v1/restore\0" || S(definitionId) || u32 version || recorded after-Snapshot || u64 recorded keyRevision )`
The Snapshot is the canonical encoding of A.3.1 of the state the client (apply) or the journal (restore) expects; the worker recomputes from its own read.

A.5.2 Batch digest.
`batchDigest = SHA-256( "winora/batch/v1\0" || u8 ops || item_1 || ... || item_ops )` where each item is the operation item of A.3.5 in batch order. It therefore binds, for every operation, the id, definition, version, mode, choice, restore value and plan token: everything the user sees before consent. The dialog shows its first 4 bytes as 8 lowercase hex characters (the "short form").

A.5.3 Prepared digest.
`preparedDigest = SHA-256( "winora/prepared/v1\0" || T || operationId || planTokenDigest || Snapshot || u64 keyRevision || u8 willChange )` with the Snapshot as sent in `Prepared`. `Commit` repeats it; the worker compares.

A.5.4 `H_d` = SHA-256 of the definition set's raw bytes as embedded (for the signed set: of the payload bytes as signed, per trust.md; for M4a see A.9).

### A.6 Transcript and key schedule

```
T = SHA-256(
      "winora/transcript/v1\0"
   || u8 proto || u8 flavor
   || u8 client.protoMin || u8 client.protoMax || u8 server.protoMin || u8 server.protoMax
   || R[16] || Nc[32] || Ns[32] || Qc[65] || Qs[65]
   || clientId || serverId
   || H_d[32] || batchDigest[32] || u8 ops
   || B("P256-AESGCM-HKDF-SHA256") )

clientId = u32 pid || u64 startFiletime || B(userSid) || B(logonSid) || u32 sessionId        (as the worker observed, 7.2)
serverId = u32 pid || u64 startFiletime || u32 sessionId                                       (as attested, 6)
Z        = ECDH shared secret, the 32-byte big-endian x coordinate
PRK      = HMAC-SHA256(key = T, message = Z)                          (HKDF-Extract with salt T)
k_c2s    = HMAC-SHA256(key = PRK, message = "winora/v1/c2s" || 0x01)  (HKDF-Expand, one block)
k_s2c    = HMAC-SHA256(key = PRK, message = "winora/v1/s2c" || 0x01)
```

`proto` is the negotiated version; with one version both min and max are 1. The transcript has 417 bytes for the vectors of Appendix B.

### A.7 Frames and nonces

`Header (16) = 0x57 0x45 || u8 proto || u8 type || u32 N || u64 counter`. Encrypted frame = Header || ciphertext(N) || tag(16), total at most 65 536. `nonce (12) = u32 direction || u64 counter`, direction 1 for client-to-worker frames and 2 for worker-to-client frames. AAD = the 16 header bytes. Hello frames and a pre-key `Abort` are `Header || payload` with no tag.

### A.8 Records

A.8.1 Attestation record, exactly 106 bytes: `"WEATT1"` (6 ASCII) `u8 flavor` `u8 protoMin` `u8 protoMax` `u8 0` `R X[16]` `u32 workerPid` `u64 workerStartFiletime` `u32 sessionId` `SHA-256(Qs) X[32]` `H_d X[32]`. The reserved byte MUST be 0.

A.8.2 Early-failure record, exactly 16 bytes: `"WEFAIL"` (6 ASCII) `u8 1` (version) `u8 reason` (6.4) `R[0..8) X[8]`.

A.8.3 `worker.lock`: contents irrelevant; semantics are the sharing mode (13.1, 12.9).

### A.9 Definition-set bytes in M4a

Before the signed set exists (D-05), `H_d` is the SHA-256 of a fixed ASCII string: `winora-m4a-set:selftest-noop` (VmTrust) or `winora-m4a-set:empty` (release). The signed-set layer replaces these with the digest of the signed payload bytes and keeps the same field in the same places.

### A.10 Authenticode verification flags

`WINTRUST_DATA`: `dwUIChoice = WTD_UI_NONE`; `fdwRevocationChecks = WTD_REVOKE_NONE`; `dwUnionChoice = WTD_CHOICE_FILE` with `WINTRUST_FILE_INFO.hFile` set to the held handle; `dwStateAction = WTD_STATEACTION_VERIFY` then `WTD_STATEACTION_CLOSE`; `dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_DISABLE_MD2_MD4`; `WTD_SAFER_FLAG` not set; policy `WINTRUST_ACTION_GENERIC_VERIFY_V2`. The intermediate certificate needed for the chain must be present in the signature or the trusted store; needing the network yields `elevation.install_untrusted` (`chain_offline`). A unit test asserts these constants in the structure builder.

### A.11 Clarifications (implementation, 2026-10-09)

These points were underspecified above and were fixed while implementing work package B (`src/Winora.Elevation.Protocol`). They are normative from this date; each is covered by the named test.

1. Operation id byte order (A.3.5). The 16 bytes on the wire are the Guid in its textual (big-endian) order: bytes `a0 a1 ... af` are the Guid `a0a1a2a3-a4a5-a6a7-a8a9-aaabacadaeaf`. An all-zero id is invalid everywhere (`WireCodecTests.Operation_id_uses_the_big_endian_guid_order_of_clarification_a`, `All_zero_operation_id_and_duplicate_ids_are_rejected`).
2. Registry snapshot header (A.3.1, kind 0). `keyExists = 0` forces `valueExists = 0`. `valueExists = 0` forces `dataKind = 0` and empty data. A decoder rejects any other combination as non-canonical, so every accepted snapshot re-encodes to the same bytes (`Registry_snapshot_canonical_form_rules_of_clarification_b`).
3. Ready without a change (10.2, A.4). `Prepared` with status 0 (Ready) and `willChange = 0` is legal and means "nothing to do": the client sends no `Commit` and the operation is finished. `willChange = 1` is legal only with status 0 (`Result_block_rules_and_ready_with_will_change_zero`).
4. Self-test payload charset (A.3.1 kind 2, 19.1). The snapshot payload uses `[a-z0-9._]` (lowercase letters, digits, dot, underscore) with length 0 to 37 (length 0 is the state before a ping, `0200`). The `<p>` of a choice `echo.<p>` stays `[a-z0-9]{1,32}`, so the choice id, and the echoed snapshot payload, is at most 37 bytes.
5. Numbers (A.3.2). A machine value number is an unsigned 64-bit integer on the wire; a DWORD is 0 to 4294967295 (`Machine_value_rules`).
6. Finished order (8.2, B.4). Both sides send `Finished` with counter 0, the client first: the client sends it after verifying `ServerHello`, the worker sends its own after it has verified the client's. The plaintext hello frames carry protocol byte 1, counter 0 and no tag.
7. Header validation order (9). A reader checks, in this order and before it reads or allocates any payload: magic, protocol byte and a defined type (`framing`); length against the cap (`size`); counter against the next expected value and the 64-frame cap (`counter`); type against the current state (`state`). This is why the replayed frame of B.7 item 2 reports `counter` and not `state`. A tag failure at key confirmation is reported as `handshake` (8.2); anywhere else it is `tag`.
8. Abort acceptance (10.2). The worker accepts `Abort` only in AwaitPrepare and AwaitCommit, exactly as the table says; the client accepts it in every wait after key derivation, because the worker may abort at any point. In the key confirmation step the worker accepts only `Finished`; the client accepts `Finished` or `Abort`. A pre-key plaintext Abort is surfaced as unauthenticated and advisory.
9. Client Abort code (12.4). When the user cancels between `Prepared` and `Commit` the client sends `Abort` with `operation.cancelled`; the Abort allow-list of 10.3 therefore also contains `operation.cancelled`.
10. Commit in M4a (19.1). The codec, the state machine and the `ping` handler implement `Commit` and `Outcome`, but the M4a worker session answers a `Commit` with `Abort(worker.unimplemented)` unless commit support is enabled in its options; only tests and the VM harness enable it. By default no `Commit` is accepted, so nothing is applied.
11. Order of BatchBegin checks in the worker (11.2). In this order: decode; an empty handler set gives `Abort(worker.unimplemented)` before any lookup; the operation count and the recomputed batch digest must equal the ones committed in `ClientHello` (`channel_failed`, `handshake`); an operation count above the milestone cap (1 in M4a) gives `channel_failed`, `size`; an unknown definition id, a version that is not the handler version or a choice the handler does not accept gives `Abort(definition.unknown)`. Mode 1 in M4a is accepted at BatchBegin and refused at `Prepare` with `Prepared` status 5 and `operation.not_reversible`.
12. Allow-list and catalog (10.3). A code that the receiver catalog does not contain is not allowed, whatever the table says: the M4a catalog lacks `definition.revoked`, `definition.restore_incompatible`, `definition.restore_revoked`, `definition.signature_invalid`, `definition.rollback` and `trust.store_insecure`, so a peer that sends them is aborted with `channel_failed`, `code` until the work package that adds them extends the catalog.
13. Client verification of `Prepared` (12.3 step 5). The client also recomputes `preparedDigest` from its own `T`, the operation id, the plan token it sent, and the received snapshot, key revision and `willChange`; a difference ends the session with `channel_failed`, `framing`. A wrong operation id is `state`; a code off the allow-list is `code`.
14. Waits and ends. The client waits 30 s for `BatchAccepted`, `Prepared` and `Outcome` (the longest worker window, Prepared to Commit) and 10 s for `Bye`; a missing `Bye` after complete results still ends the session as completed, without the Bye. A worker AwaitBye expiry is a normal end. Every end of a session closes the channel.
15. Early-failure reason `image` (6.4) is surfaced as `elevation.install_untrusted` with detail `hash`; the client never learns which of `hash` or `signer` failed.
16. Worker exit codes (13.2) are for logs only and are chosen as: completed 0; any timeout 6; `elevation.client_untrusted` 5; `worker.unimplemented` and `definition.*` 9; every other failure 7; startup reasons per `EarlyFailureMapping.ExitCodeFor`.

17. Trust boundary above the install root (4.3 step 4). `ImageGuardOptions.TrustBoundaryDirectory` is the install root; the worker, the client probe and the worker runner all pass it. Components strictly above it use the replace-only rights tier, the root and everything below use the full tier (`ImageGuardTests.TrustBoundaryRelaxesOnlyTheCreateRightsAboveIt`). STATUS: accepted on 2026-10-10 (owner-delegated decision). Basis: the default ACL of a Windows volume root gives Users the create-folders right on the root and read and execute elsewhere, and Program Files gives Users read and execute only, so neither lets a standard user replace `Program Files\Winora` (MS-15; sources not opened in full). An independent review is still open.
18. Catalog-signed files (4.3 step 5). The signature check accepts only an embedded Authenticode signature. Inbox Windows binaries (catalog-signed) are not accepted by it and are never checked by it; Winora's client, desktop, CLI and worker images are embedded-signed.
19. Signature failures (4.3 step 5, 17.2). A signature result of chain-offline or timeout is reported as `elevation.install_untrusted` with detail `chain_offline`; every other signature failure is `signer`. Which of the two a slow or offline machine produces is an implementation mapping, not a protocol guarantee.
20. Integrity mapping (7.2). Only a token at exactly Medium integrity maps to Medium. A token at the intermediate level above Medium (0x2100) maps to High, and unknown levels never map to Medium, so a client that is not plainly Medium fails the worker's integrity check.
21. Vanished client (13.2). When the client process is gone before the first connection, the worker ends the wait with `SessionEnd.Cancelled`, cleans up and exits with the timeout code 6 (`WorkerRunnerTests.A_client_that_is_already_gone_ends_the_wait_and_cleans_up`).
22. Early failure when the run-directory rule fails (6.4 against 14.1 step 7). Section 6.4 wins: the `.fail` record is written on a best-effort basis even then, and the client trusts it only after its own ACL check.
23. Commit and Outcome for `ping` (19.1). Resolved by item 10: the codec and handler implement them, the default worker aborts a Commit with `worker.unimplemented`.
24. Record file access (6.3). The worker opens every record file (`.att`, `.fail`, `worker.lock`) with `GENERIC_WRITE | DELETE`, because `FILE_FLAG_DELETE_ON_CLOSE` needs DELETE access on the handle. The share modes are those of 6.3.
25. Job limits (14.1 step 5). The worker's job object sets the active-process limit of 1, the process memory limit (64 MiB) and kill-on-close. No handle or thread ceiling exists in the specification or the code: the Windows job object API has no such limit (MS-14). The remaining process-level hardening is the mitigation table (14.3).
26. Where the install location is enforced (4.3 step 1, 20.4). The client compares the install root with the Program Files known folder and with the install record. The worker, which has no shell library by design, checks only that its install root is the parent of its own image folder, that the run directory rule holds and that the image guard passes on its own image with the full ACL rule at and below the install root. A copy of the signed worker in a user-writable place fails the worker's own ACL rule; a copy in an administrator-only place elsewhere needs an administrator to create it and is refused by the client. Industry practice agrees that a path check is only as strong as the ACLs of the trusted location (MS-15).
27. End of the client after a connection (14.1 step 9). The watcher covers the time before the first connection. After that, a client that dies closes its pipe end; the worker's read ends or fails and the session stops. This is not documented in the pages cited here for named pipes (MS-16 describes the broken-pipe error for anonymous pipes), so the worker does not rely on it: the timers of 13.1 end every session within 5 minutes at most, and `Commit` already accepted is never abandoned half way.

## Appendix B: test vectors

Status of these vectors: REPRODUCED (2026-10-09, work package B). They were computed while writing this document with a throw-away Python 3 script that used `hashlib` and `hmac` for SHA-256, HMAC-SHA256 and HKDF, plus a from-scratch P-256 scalar multiplication and a from-scratch AES-256-GCM, and checked as follows: HKDF against RFC 5869 test case 1 (B.1); AES-256-GCM against the GCM specification test cases 13 and 16 (B.2); the two P-256 public keys and the shared secret against OpenSSL 3.2.4. Every value below is now reproduced byte for byte by two implementations written independently of the document and of each other. (1) The production library `src/Winora.Elevation.Protocol` on the .NET 10 BCL (AES-GCM, ECDH and HKDF from Windows CNG), by the tests of `VectorTests` (B.1 and B.2: `B1_Hkdf_Rfc5869_case1`, `Hkdf_Rfc5869_case2_long_inputs`, `Hkdf_Rfc5869_case3_empty_salt_and_info`, `B2_AesGcm_known_answers`; B.3: `B3_Private_scalars_derive_from_the_documented_labels`, `B3_Public_keys_and_shared_secret_are_reproduced_by_both_sides`, `B3_Sids_and_identities_encode_as_documented`, `Sha256_known_answers_and_definition_set_hashes`; B.4 and B.5: `Plan_token_item_and_batch_digest`, `Hello_frames_transcript_and_key_schedule`, `Every_encrypted_frame_is_reproduced_byte_for_byte`, `Prepared_digests_of_both_sessions`; B.6: `B6_Attestation_record`, `B6_Early_failure_record_machine_value_and_registry_snapshot`, `B6_Nonces`; B.7: `B7_1_*` to `B7_6_*`) and by `EndToEndVectorTests.Engines_reproduce_the_wire_bytes_of_both_documented_sessions`, which runs the real client and worker engines with the fixed keys and nonces and compares every byte they write. (2) The standard-library Python reference a development tool that is not published (its own AES-256-GCM, P-256 and encoders; `python protocol_reference.py selftest` checks it against RFC 5869 and the GCM cases), compared with this appendix by `ReferenceCrossCheckTests.The_reference_reproduces_every_value_of_Appendix_B_independently_of_the_library` and with the library on randomized cases with fixed seeds by `ReferenceCrossCheckTests.The_library_agrees_with_the_reference_on_randomized_cases_with_fixed_seeds`; that test class is skipped with an explicit reason when python is not on PATH. The hex values of this appendix are copied verbatim into a test script of the development repository by a development tool that is not published; a change of any value here must be followed by regenerating that file. A difference between this document and either implementation is a defect and blocks the package. The independent cryptographic review of the handshake is still open (section 22 item 4). These vectors use FIXED keys and nonces and are test data only; they must never be used in a running session. The test-only private keys are published here on purpose.

### B.1 HKDF-SHA256 known answer (RFC 5869 test case 1)

IKM = 22 bytes of `0b`; salt = `000102030405060708090a0b0c`; info = `f0f1f2f3f4f5f6f7f8f9`; L = 42. Expected OKM:

```
3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865
```

### B.2 AES-256-GCM known answers (96-bit IV, 128-bit tag)

Case 1 (GCM test case 16): K = `feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308`; IV = `cafebabefacedbaddecaf888`; AAD = `feedfacedeadbeeffeedfacedeadbeefabaddad2`; plaintext = `d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39`. Expected ciphertext `522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662`, tag `76fc6ece0f4e1768cddf8853bb2d551b`.

Case 2 (GCM test case 13): K = 32 zero bytes, IV = 12 zero bytes, empty AAD, empty plaintext. Expected tag `530f8afbc74536b9a963b4f1c4cb738b`.

### B.3 Fixed inputs and key agreement (shared by both sessions)

| Name | Value |
|---|---|
| proto, protoMin, protoMax | 1, 1, 1 (both peers) |
| flavor | 1 (vmtrust) |
| R (rendezvous) | `000102030405060708090a0b0c0d0e0f` |
| Nc | `202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f` |
| Ns | `404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f` |
| dc (client private scalar) | `87db92565908a00e298cd155d746ff5821af39cc101e9a5eb032814dbed0267d` = (SHA-256 of ASCII `winora/test-vector/client-key/1`, as a big-endian integer) mod (n-1), plus 1 |
| ds (worker private scalar) | `51ef1f64737cae92e83e1c41c150bbf99b8d453a942a4b3fc2318719d63f8f95` = (SHA-256 of ASCII `winora/test-vector/server-key/1`, as a big-endian integer) mod (n-1), plus 1 |
| Client pid, start FILETIME, session | 4660, 133700000000000000, 2 |
| Worker pid, start FILETIME, session | 5678, 133700000010000000, 2 |
| Client user SID | S-1-5-21-1111111111-2222222222-3333333333-1001 |
| Client logon SID | S-1-5-5-0-123456 |
| Worker token user SID | S-1-5-21-1111111111-2222222222-3333333333-500 |
| Definition-set bytes (A.9) | ASCII `winora-m4a-set:selftest-noop` |

`Qc = SEC1 uncompressed public key of dc` (65 bytes)

```
04ceaedfebf5e4d4a19b53f5b5aff20bca82a80dd706b140df85ecb21ef4dae357d89c02e6160c73a98aa73d2b7d7614
cefbce2ca126b80d6cda64d0642f47b800
```

`Qs = SEC1 uncompressed public key of ds` (65 bytes)

```
0446ea38a477d0986c584d68f08fb9a013a76504f08a0ea32d9f6a36e9832a69145461885a14c1421107437ce46aefc7
56850faaeb9c48cb00170501ea9f7a30d4
```

`Z = ECDH shared secret (x coordinate, big-endian), identical from both sides` (32 bytes)

```
0e6e6111ea2a2e40874b549848322cd275b5afaacd4b803622b2168a1709f7b9
```

`SHA-256(Qs), the value placed in the attestation record` (32 bytes)

```
e2c046e75e3c459b9a6cf399278ca6cb55bdb9690bbca3dd203dd45328559765
```

`H_d = SHA-256 of ASCII "winora-m4a-set:selftest-noop" (VmTrust)` (32 bytes)

```
3c1cd51a7fb5ce0a88460bd5da33cbf6a2d6a6fcd0eaa1dd8ff865735dc25308
```

`H_d of the release flavor = SHA-256 of ASCII "winora-m4a-set:empty"` (32 bytes)

```
d2bbf0287969538896d1a252ff99d705dd688ca0610daf8906d421a1c280cee1
```

`client user SID, binary` (28 bytes)

```
010500000000000515000000c7353a428e6b748455a1aec6e9030000
```

`client logon SID, binary` (20 bytes)

```
0103000000000005050000000000000040e20100
```

`worker token user SID, binary` (28 bytes)

```
010500000000000515000000c7353a428e6b748455a1aec6f4010000
```

`clientId (A.6)` (72 bytes)

```
341200000040780e71ffda011c000000010500000000000515000000c7353a428e6b748455a1aec6e903000014000000
0103000000000005050000000000000040e2010002000000
```

`serverId (A.6)` (16 bytes)

```
2e16000080d6100f71ffda0102000000
```

### B.4 Session 1: batch of one operation, choice `echo.hello` (no Commit)

Operation id `a0a1a2a3a4a5a6a7a8a9aaabacadaeaf`, definition `winora.selftest.noop` version 1, mode 0, choice `echo.hello`, restore value Absent (`000000`), expected snapshot `020a6563686f2e68656c6c6f` (selftest layout of A.3.1), key revision 0. The worker answers `Prepared` with status 2 (AlreadyConfigured) and willChange 0, so no Commit is sent; the client then sends Bye. preparedDigest of this session = `bb1dc5477d9b387bd0bd229d1a330a636f6844daf211bb6584be0dc2a10d36f3`.

`planTokenDigest` (32 bytes)

```
bca076e760e8cd17395f96484724280f4df2fc2a02802928f3512e2d54ce0a7a
```

`operation item (A.3.5); the identical bytes are hashed into the batch digest and sent in BatchBegin` (88 bytes)

```
a0a1a2a3a4a5a6a7a8a9aaabacadaeaf1477696e6f72612e73656c66746573742e6e6f6f7001000000000a6563686f2e
68656c6c6f000000bca076e760e8cd17395f96484724280f4df2fc2a02802928f3512e2d54ce0a7a
```

batchDigest = `0c7862b55983de19a8ce527cb4f42e298b2f7bea450424aaacaa747f7945b0cf` (dialog short form `0c7862b5`)

`ClientHello frame (type 0x01, plaintext)` (197 bytes)

```
57450101b50000000000000000000000010101000102030405060708090a0b0c0d0e0f202122232425262728292a2b2c
2d2e2f303132333435363738393a3b3c3d3e3f04ceaedfebf5e4d4a19b53f5b5aff20bca82a80dd706b140df85ecb21e
f4dae357d89c02e6160c73a98aa73d2b7d7614cefbce2ca126b80d6cda64d0642f47b8003c1cd51a7fb5ce0a88460bd5
da33cbf6a2d6a6fcd0eaa1dd8ff865735dc253080c7862b55983de19a8ce527cb4f42e298b2f7bea450424aaacaa747f
7945b0cf01
```

`ServerHello frame (type 0x02, plaintext)` (165 bytes)

```
57450102950000000000000000000000010101000102030405060708090a0b0c0d0e0f404142434445464748494a4b4c
4d4e4f505152535455565758595a5b5c5d5e5f0446ea38a477d0986c584d68f08fb9a013a76504f08a0ea32d9f6a36e9
832a69145461885a14c1421107437ce46aefc756850faaeb9c48cb00170501ea9f7a30d43c1cd51a7fb5ce0a88460bd5
da33cbf6a2d6a6fcd0eaa1dd8ff865735dc2530801
```

`transcript input (A.6), hashed to T` (417 bytes)

```
77696e6f72612f7472616e7363726970742f763100010101010101000102030405060708090a0b0c0d0e0f2021222324
25262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f5051525354
55565758595a5b5c5d5e5f04ceaedfebf5e4d4a19b53f5b5aff20bca82a80dd706b140df85ecb21ef4dae357d89c02e6
160c73a98aa73d2b7d7614cefbce2ca126b80d6cda64d0642f47b8000446ea38a477d0986c584d68f08fb9a013a76504
f08a0ea32d9f6a36e9832a69145461885a14c1421107437ce46aefc756850faaeb9c48cb00170501ea9f7a30d4341200
000040780e71ffda011c000000010500000000000515000000c7353a428e6b748455a1aec6e903000014000000010300
0000000005050000000000000040e20100020000002e16000080d6100f71ffda01020000003c1cd51a7fb5ce0a88460b
d5da33cbf6a2d6a6fcd0eaa1dd8ff865735dc253080c7862b55983de19a8ce527cb4f42e298b2f7bea450424aaacaa74
7f7945b0cf0117000000503235362d41455347434d2d484b44462d534841323536
```

T = `1126de2fff972369394aef1d008419e783e79fa9e97ce2c0195f81692e0db8ad`

PRK = `a3e05be7c080bbf83b428d4543ccb432a2482edcda67939ee62b2b8186d76217`

k_c2s = `eb5179c2b5c0e34bc06c792a4a773c5e9630bbb62dc2cbe2537e688c4dc67fc5`

k_s2c = `cffa004c9c5333ea32d06010b4a8d926d8cc99e0d3a559234a9a47b44773d9de`

Encrypted frames in wire order (nonce = u32 direction then u64 counter; AAD = the first 16 bytes of the frame):

- C>W, type 0x10 ctr 0. Nonce `010000000000000000000000`. Plaintext (32 bytes): `1126de2fff972369394aef1d008419e783e79fa9e97ce2c0195f81692e0db8ad`.

```
5745011020000000000000000000000080413cc1a2d35f40f0c55acb019d20eb8dd3e80fb1088d6918ad1e80bc1ae200
6bfdd156d839e6e8365d694a66468001
```

- W>C, type 0x10 ctr 0. Nonce `020000000000000000000000`. Plaintext (32 bytes): `1126de2fff972369394aef1d008419e783e79fa9e97ce2c0195f81692e0db8ad`.

```
5745011020000000000000000000000000876fff68b7aa86b6fec1a57c29665b868f66374ddf1b4d9a5bf54dd5e9ad87
d3a9da1150acf44fb7f49ea55e51a19b
```

- C>W, type 0x20 ctr 1. Nonce `010000000100000000000000`. Plaintext (89 bytes): `01a0a1a2a3a4a5a6a7a8a9aaabacadaeaf1477696e6f72612e73656c66746573742e6e6f6f7001000000000a6563686f2e68656c6c6f000000bca076e760e8cd17395f96484724280f4df2fc2a02802928f3512e2d54ce0a7a`.

```
57450120590000000100000000000000674edd063e8666f573398c00c0b233c64bc644dce25b476645369ebf4a4da114
2df8834b7797bf736cc4151c61b36054ce0e2fab3830012af7286453c9cf0450f4640ab743cd594fc36d5db3847f898f
42acc7656a38e835f583b10b1a2cf2e7313e637ccfb79ac9b9
```

- W>C, type 0x21 ctr 1. Nonce `020000000100000000000000`. Plaintext (33 bytes): `011c000000010500000000000515000000c7353a428e6b748455a1aec6f4010000`.

```
574501212100000001000000000000002ac01ec7c360658446ea3e668a5298e021ad4f62792905ec66c8b9989226f081
7595c7c721879313ec809a1723743eaa4b
```

- C>W, type 0x30 ctr 2. Nonce `010000000200000000000000`. Plaintext (16 bytes): `a0a1a2a3a4a5a6a7a8a9aaabacadaeaf`.

```
57450130100000000200000000000000628daa35fa4b3c79fa6f09d3e260919f9e59846834a7ffb975f9022c29b57c49
```

- W>C, type 0x31 ctr 2. Nonce `020000000200000000000000`. Plaintext (102 bytes): `a0a1a2a3a4a5a6a7a8a9aaabacadaeaf021c6f7065726174696f6e2e616c72656164795f636f6e66696775726564000000020a6563686f2e68656c6c6f000000000000000000bb1dc5477d9b387bd0bd229d1a330a636f6844daf211bb6584be0dc2a10d36f3`.

```
57450131660000000200000000000000c362219a1fa7a753a164bc066008da54b7a0ff9316248510e9c44e90402ffd53
0ebc0b5ed2ca729dae2f560fd89e4c2a59cb1c43586bc04e295a6634099d8f5f49431765347e12fce2afd2fdbbfb60ef
91eae6e0a7f785bb6fe1fd1064f36d5bfafa9ab99544e4f80a9b0cbd2ddea6cf5ad83131b7bd
```

- C>W, type 0x41 ctr 3. Nonce `010000000300000000000000`. Plaintext (0 bytes): `(empty)`.

```
574501410000000003000000000000005cf074574326c23ec3f45073039eb32a
```

- W>C, type 0x41 ctr 3. Nonce `020000000300000000000000`. Plaintext (0 bytes): `(empty)`.

```
574501410000000003000000000000004ad1085ff058de432e729d6f46fa6216
```

### B.5 Session 2: batch of one operation, choice `ping` (with Commit)

Operation id `c0c1c2c3c4c5c6c7c8c9cacbcccdcecf`, choice `ping`, expected snapshot `0200` (empty selftest state), key revision 0. The worker answers `Prepared` with status 0 (Ready) and willChange 1; the client sends Commit carrying preparedDigest `b43651de66abb099f0c6191a6bb9f7c95bb0299422d88a50e365e7641362f755`; the worker answers Outcome status 1 (Applied) with after-snapshot `0204706f6e67` ("pong"). Nothing outside the worker process memory changes.

`planTokenDigest` (32 bytes)

```
bc8f8829e188d63bdff3a896ff50cfd05fd74c7e0c2f62255f0b6c1343db9511
```

`operation item (A.3.5); the identical bytes are hashed into the batch digest and sent in BatchBegin` (82 bytes)

```
c0c1c2c3c4c5c6c7c8c9cacbcccdcecf1477696e6f72612e73656c66746573742e6e6f6f7001000000000470696e6700
0000bc8f8829e188d63bdff3a896ff50cfd05fd74c7e0c2f62255f0b6c1343db9511
```

batchDigest = `26a5feccfbbefdc1564770c1e85f3edcaf238e5ba26a84cae427728ea4ab20e5` (dialog short form `26a5fecc`)

`ClientHello frame (type 0x01, plaintext)` (197 bytes)

```
57450101b50000000000000000000000010101000102030405060708090a0b0c0d0e0f202122232425262728292a2b2c
2d2e2f303132333435363738393a3b3c3d3e3f04ceaedfebf5e4d4a19b53f5b5aff20bca82a80dd706b140df85ecb21e
f4dae357d89c02e6160c73a98aa73d2b7d7614cefbce2ca126b80d6cda64d0642f47b8003c1cd51a7fb5ce0a88460bd5
da33cbf6a2d6a6fcd0eaa1dd8ff865735dc2530826a5feccfbbefdc1564770c1e85f3edcaf238e5ba26a84cae427728e
a4ab20e501
```

`ServerHello frame (type 0x02, plaintext)` (165 bytes)

```
57450102950000000000000000000000010101000102030405060708090a0b0c0d0e0f404142434445464748494a4b4c
4d4e4f505152535455565758595a5b5c5d5e5f0446ea38a477d0986c584d68f08fb9a013a76504f08a0ea32d9f6a36e9
832a69145461885a14c1421107437ce46aefc756850faaeb9c48cb00170501ea9f7a30d43c1cd51a7fb5ce0a88460bd5
da33cbf6a2d6a6fcd0eaa1dd8ff865735dc2530801
```

`transcript input (A.6), hashed to T` (417 bytes)

```
77696e6f72612f7472616e7363726970742f763100010101010101000102030405060708090a0b0c0d0e0f2021222324
25262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f5051525354
55565758595a5b5c5d5e5f04ceaedfebf5e4d4a19b53f5b5aff20bca82a80dd706b140df85ecb21ef4dae357d89c02e6
160c73a98aa73d2b7d7614cefbce2ca126b80d6cda64d0642f47b8000446ea38a477d0986c584d68f08fb9a013a76504
f08a0ea32d9f6a36e9832a69145461885a14c1421107437ce46aefc756850faaeb9c48cb00170501ea9f7a30d4341200
000040780e71ffda011c000000010500000000000515000000c7353a428e6b748455a1aec6e903000014000000010300
0000000005050000000000000040e20100020000002e16000080d6100f71ffda01020000003c1cd51a7fb5ce0a88460b
d5da33cbf6a2d6a6fcd0eaa1dd8ff865735dc2530826a5feccfbbefdc1564770c1e85f3edcaf238e5ba26a84cae42772
8ea4ab20e50117000000503235362d41455347434d2d484b44462d534841323536
```

T = `04e298801e2b4e031c9e4564ffd43e93d20d44f87c3026c341f5c0e055b23897`

PRK = `fafee6ebcc14c716569a7a26d4fb87e6349bebc9266701698e40379dd4aace38`

k_c2s = `1223d1abbcf575013bfc551bf489f00f34accd26a96ffeae575b2226c26fcd67`

k_s2c = `23fcdf5a5fbed4e9010c6d6bd98a78a246c0832514cff4f0c9b0b973281e75e4`

Encrypted frames in wire order (nonce = u32 direction then u64 counter; AAD = the first 16 bytes of the frame):

- C>W, type 0x10 ctr 0. Nonce `010000000000000000000000`. Plaintext (32 bytes): `04e298801e2b4e031c9e4564ffd43e93d20d44f87c3026c341f5c0e055b23897`.

```
57450110200000000000000000000000b99ee0e28896522bc791ac430858d3626205a87599a0a65bfe0400948c943c40
198ef50572c7055b7265a8cad0234e6d
```

- W>C, type 0x10 ctr 0. Nonce `020000000000000000000000`. Plaintext (32 bytes): `04e298801e2b4e031c9e4564ffd43e93d20d44f87c3026c341f5c0e055b23897`.

```
5745011020000000000000000000000051ca45c183fe37d032885762a2e63dc91808b4a4d20ff8ed0a62580a5406b192
9f7b385870684a9d37b2a7b723adcc7b
```

- C>W, type 0x20 ctr 1. Nonce `010000000100000000000000`. Plaintext (83 bytes): `01c0c1c2c3c4c5c6c7c8c9cacbcccdcecf1477696e6f72612e73656c66746573742e6e6f6f7001000000000470696e67000000bc8f8829e188d63bdff3a896ff50cfd05fd74c7e0c2f62255f0b6c1343db9511`.

```
5745012053000000010000000000000018198b5122c0323591ce87bde30b750abb503d99d2946f573d21110bcf4c9f25
18950d60481b48dd0360eefda114b0cfb03f8fbed86c17877cba8e00311100e0ad9c84e6067a59f23c80ed59d4aec894
fd1252f2c51c4ce91fe7ec4fe137bb50f20dda
```

- W>C, type 0x21 ctr 1. Nonce `020000000100000000000000`. Plaintext (33 bytes): `011c000000010500000000000515000000c7353a428e6b748455a1aec6f4010000`.

```
57450121210000000100000000000000530df64db6b42bffed3ab6574a090067b8808d3813c19269fb31fee9fd99efa7
91c34d7ebb00670968a98a6ce32fe4205f
```

- C>W, type 0x30 ctr 2. Nonce `010000000200000000000000`. Plaintext (16 bytes): `c0c1c2c3c4c5c6c7c8c9cacbcccdcecf`.

```
57450130100000000200000000000000e778f6048b213b02c42c7a2f474caea3add4cbf1eeecddce78a75e429729c897
```

- W>C, type 0x31 ctr 2. Nonce `020000000200000000000000`. Plaintext (82 bytes): `c0c1c2c3c4c5c6c7c8c9cacbcccdcecf00126f7065726174696f6e2e70726570617265640000000200000000000000000001b43651de66abb099f0c6191a6bb9f7c95bb0299422d88a50e365e7641362f755`.

```
57450131520000000200000000000000d07389d51024fda627c1123760424d5ea833fdad93a0e8b9c14d99bb01100b29
48797b0ab2c32cb27867d64d62b97c6c8368412796f336f3aadb4fe41f2716d1167c6a87d4b23a1489127f44dbaa4179
1b30eb2deeea217756ac2ea3091f2b01bc12
```

- C>W, type 0x32 ctr 3. Nonce `010000000300000000000000`. Plaintext (48 bytes): `c0c1c2c3c4c5c6c7c8c9cacbcccdcecfb43651de66abb099f0c6191a6bb9f7c95bb0299422d88a50e365e7641362f755`.

```
57450132300000000300000000000000b1635b792bbc0b927c43c5dd7ea2d81e5bea08bfe85b6544e8022c9b371bd981
3aad79b27f4b1af523b1cec7625305d060ccd10e0294e31b3108fdcaf9e25dd9
```

- W>C, type 0x33 ctr 3. Nonce `020000000300000000000000`. Plaintext (52 bytes): `c0c1c2c3c4c5c6c7c8c9cacbcccdcecf01116f7065726174696f6e2e6170706c6965640000000204706f6e670000000000000000`.

```
57450133340000000300000000000000a5db172fdff585dbb5dda5309357d9779139ae2f0bff7845102a5bbd681fdbc5
6f89aa10bd8901cc55b2e8168366d98fe7100be242a31f4ee2618d543a6da489eb8a24c0
```

- C>W, type 0x41 ctr 4. Nonce `010000000400000000000000`. Plaintext (0 bytes): `(empty)`.

```
574501410000000004000000000000009d468ef08dcd44330c4bdeeda428527b
```

- W>C, type 0x41 ctr 4. Nonce `020000000400000000000000`. Plaintext (0 bytes): `(empty)`.

```
57450141000000000400000000000000a1765d6ccb0d702c61ec2b23a3f0f8ac
```

### B.6 Records and small structures

`attestation record (A.8.1) for the fixed inputs (flavor 1, protoMin 1, protoMax 1)` (106 bytes)

```
57454154543101010100000102030405060708090a0b0c0d0e0f2e16000080d6100f71ffda0102000000e2c046e75e3c
459b9a6cf399278ca6cb55bdb9690bbca3dd203dd453285597653c1cd51a7fb5ce0a88460bd5da33cbf6a2d6a6fcd0ea
a1dd8ff865735dc25308
```

`early-failure record (A.8.2): reason 1 (busy), R prefix of the fixed rendezvous` (16 bytes)

```
57454641494c01010001020304050607
```

`MachineValue DWORD 1 (A.3.2)` (7 bytes)

```
01040001000000
```

`registry snapshot: key and value exist, 64-bit view, DWORD 0 (A.3.1)` (11 bytes)

```
0001010001040000000000
```

Restore-mode planTokenDigest example: definition `machine.example.setting` version 1, the registry snapshot above, key revision 133700000000000000: `e5a4aef1e16f55966ddbb8e01908edfb36aa02f95e4ca448836de1bd248254bc`.

Nonces: client-to-worker counter 0 = `010000000000000000000000`; worker-to-client counter 5 = `020000000500000000000000`.

### B.7 Negative vectors (expected behavior)

1. Session 1 inputs with flavor byte 0 in the transcript only: T = `1bde9fbb1bcec2e11a0f1d6b8f0e1ffcb29effc921b931a4fbd14021e69f9c04`. The peer's Finished decryption fails (`elevation.channel_failed`, detail `handshake`).
2. The session 1 frame "C>W type 0x30 ctr 2" presented again as the next client frame (counter 3 expected): rejected from the header, before any decryption (`elevation.channel_failed`, detail `counter`).
3. A session 1 worker frame ("W>C type 0x31") fed to the worker's own reader (reflection): decrypted with k_c2s, the tag fails (detail `tag`).
4. Session 1 BatchBegin with one flipped bit in the choice id: the worker's recomputed batch digest differs from the committed one (detail `handshake`).
5. A ClientHello whose Qc is not on the curve (for example the last byte of Y incremented): rejected at key import (detail `handshake`).
6. A header with N = 65505 (one over the cap), an altered magic (`5745` changed), version 2, or the unknown type `7f`: rejected after reading exactly 16 bytes and before allocating a payload buffer (detail `framing` or `size`).


## Appendix C: provenance of this revision

Internal ids: `D-nn` are the owner-delegated decisions in an unpublished design note; `R-nn` are findings of the M4 design review (an internal document, D-22), cited by id only.

| Id | Applied in |
|---|---|
| D-01 transport seam, no service in the baseline | 1 (goal 5), 4.1 |
| D-02 `asInvoker` plus `runas` | 4.1, 4.5, 14.1 |
| D-03 minimal projects | 2, 14.4 |
| D-04 channel cryptography, identity rules, residual limits | 2, 7, 8, 15, 22 |
| D-05 definition signature not in M4a | 19, A.9 |
| D-08 install policy | 3.1 (G1, G2) |
| D-09 VmTrust | 0, 4.3, 5, 19, 20 |
| D-11 worker runtime and environment rule | 14.2, 6.4 |
| D-12 consent stance | 3.2, 17.1 |
| D-13 platform stance | 3.1 (G10, G11), 3.3, 17.1 |
| D-14 M4a and gate | 19, 18 |
| D-15 Event Log audit | 13.3 |
| D-16 stub retirement | 11.1, 17.3 |
| D-18 restore across definition versions | 11.2, 11.7, 17.1 |
| R-05 pipe access rights | 5 |
| R-06, R-07 attestation sharing, lifetime, ownership | 6.2, 6.3 |
| R-08 encodings, digests, flavor byte | A.1 to A.8 |
| R-09 batch versus single-active row | 12.2 |
| R-10 restore sequence and marker | 12.6 |
| R-11 runtime reversibility | 12.7 |
| R-12 recovery with a live worker | 12.9 |
| R-13 flush, own revision, drift check | 12.3, 12.4, 12.9 |
| R-14 status mapping, code allow-list | 10.3, 12.5 |
| R-15 severity and retryability, early-failure record | 6.4, 10.3, 17 |
| R-16 timers, R-18 flags | 13.1, 10.2, A.10 |
| R-20 ACL walk, R-21 subsystem, R-22 mitigation table, R-32 limits, R-34 startup order | 4.3, 14 |
| R-24, R-25 test inputs and launcher isolation | 20 |
| R-26 gates G8 and G9 | 3.1 |
| R-29 receipts removed | 12.8 |
| R-30 journal ACL statement | 2 |
| R-31 `machine.` prefix, `managedBy` | 11.3, 11.9 |

## 22. Open risks

1. Several operating-system behaviors are assumptions until the VM experiments run (section 18, all tagged PLA or UNV): environment inheritance across `runas` (X1), pipe creator access and the mandatory label (X2), Medium access to the elevated worker (X3), attestation file sharing, ownership and lifetime (X4), `asInvoker` elevation and the consent text (X5), runtime-settable mitigations (X6), registry link refusal (X7), flush durability (X8), offline Authenticode (X9), session and logon SID equality (X15), Administrator protection (X13). Each can force a change to sections 5, 6, 7 or 14.
2. Residual limits stated by D-04. The channel cannot defend against code injected into the genuine client (same user, same integrity), which can request any catalog-approved, reversible, bounded change for which the user still sees a prompt; it cannot defend against a same-user process that wins a race before the attestation check beyond turning it into a denial of service; and UAC consent is the trust anchor. With "elevate without prompting" no prompt remains and only client-process integrity does (3.2).
3. The client is not cryptographically authenticated to the worker; client identity relies on the kernel token and an image check whose PID input is spoofable by a process that holds a handle to the genuine client (7.3). That case is inside the client trust boundary.
4. The handshake is custom. An independent cryptographic review is a release gate (D-04); the fallback is SSPI Negotiate sign/seal with the attestation record kept. The Appendix B vectors are reproduced by the library and by the independent reference (Appendix B status); the independent cryptographic review has not happened.
5. A CoreCLR (self-contained) worker runs startup hooks and profilers before `Main`, so the fail-closed environment rule cannot pre-empt them; only `StartupHookSupport=false` and proof by X1 can. NativeAOT feasibility for the worker (crypto APIs, linker prerequisites in CI) is unproven until spike S0. The worker is not releasable until X1 passes with the chosen model (D-11).
6. UAC consent text cannot show the batch. The mitigation is the pre-launch dialog and the batch digest in the visible command line, which a user can ignore.
7. Early-failure delivery needs a writable `run\`; if the worker cannot create its record (weak ACL, not elevated) the client sees only a timeout, which is marked retryable although the cause may be permanent. Each retry raises a UAC prompt.
8. Offline operation means no online revocation. Revocation of a definition or signer reaches machines only through a new release (D-10); the window is stated in release-limits before the first machine-scope definition.
9. With a shared-subject signing service (D-07) the Authenticode pin identifies the signing service, not Winora; the administrator-only install root and the ACL walk are the primary control, and a signed release manifest is added only if that proves insufficient.
10. Per-launch files in Program Files (`run\`) are unconventional and may interact with security software and controlled-folder protection (X20); the registry attestation alternative is documented in 6.5.
11. Machine state is global while journals are per user: another user's Winora cannot revert a change it has no record of; the machine audit record (owner question O-14) is undecided. The machine-wide `worker.lock` lets one session block another for up to 5 minutes.
12. Delayed automatic start for services is not defined (12.7); no service definition may ship before a decision.
13. Windows platform stance: Administrator protection and Smart App Control behavior is unverified (D-13); Windows 10, builds below 22621 and ARM64 are unsupported.
14. The per-message receipts of the earlier draft are removed; the design offers no third-party-verifiable evidence of what the worker did (12.8).
15. Gates before the first real machine-scope definition (D-14) are unmet: external channel review, all experiments passed or fallbacks adopted, definition-signature layer, per-definition checklist, policy awareness, and the two-reviewer approver rule (D-17).
16. Owner and process items still open: the AGENTS.md exception for the worker composition root (drafted in m4-decisions.md, applied when phase 2 starts); the ErrorTests rule for worker-silent codes (17.4); the fate of `protocol.operation`, `protocol.command` and `protocol.feature` (O-15, 17.3); the Event Log source and event ids (13.3); the exact exception list of the environment rule (14.2).
