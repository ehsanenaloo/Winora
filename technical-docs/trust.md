<!-- SPDX-License-Identifier: GPL-3.0-only -->
# SEC-002: Trust root, installation, signing and signed definitions (design)

Status: DESIGN ONLY (M4 phase 1; revised 2026-10-09 to apply the owner-delegated decisions D-01..D-22 of an unpublished design note and the review an unpublished design note). Nothing here is implemented: no per-machine package exists, nothing is signed, no definition set, verifier or floor store exists. The current installer is the per-user MSI of ADR-008 and has never been installed by the project. Where this file and m4-decisions.md differ, m4-decisions.md wins (it overrides earlier documents); the difference is a defect to report.

Notation. `[U]` marks OS or tool behavior not verified while writing; each has a VM experiment named in the text (X-numbers are the review's, section 3 of m4-review.md; XI-, XE- and Q-numbers are defined here in 3.2, 12.3 and 4.8). `DECIDED (D-nn)` points to m4-decisions.md. `OPEN` is an owner decision still outstanding.

Read with [protocol.md](protocol.md) (channel; this file refers to it by section and to its Appendix A for byte encodings), [adrs.md](adrs.md) ADR-010..ADR-018, [release.md](release.md), [m4-implementation-plan.md](m4-implementation-plan.md).

## 1. Trust model in one page

Three things must be trustworthy for an elevated change to be legitimate, and each has exactly one root:

| Question | Root of trust | Who can alter it |
|---|---|---|
| Is this the genuine Winora client/worker code? | An administrator-only install root (Program Files path, ACL-checked) AND an Authenticode signature with a pinned signer (4.3). With a shared-subject signing service the signature identifies the signing service, not Winora (4.3, D-07); the install root carries the rest | The holder of the signing identity; an administrator of the machine |
| What may the worker touch? | The definition set embedded in the signed binaries, additionally signed (COSE_Sign1, ES256) by a dedicated definition key once the definition-signature layer exists (not in M4a, D-05, D-14) | Release process (two different reviewers, D-17) and the definition key holder |
| Does the person at the keyboard want this change? | UAC consent (and, for standard users, an administrator's credentials) | The interactive user; malware cannot answer the secure desktop |

The journal, profiles, CLI arguments, command lines, environment variables and anything under `%LOCALAPPDATA%` are untrusted input everywhere in this design. Honest limit (D-04): the channel cannot defend against code injected into the genuine client or against a same-user process that wins a race before the attestation check; UAC consent is the trust anchor, and the closed definition set bounds what consent can be abused for.

## 2. Installation modes and the elevation decision

DECIDED (D-08, ADR-011): elevation exists only in the signed per-machine package. Everywhere else it is unavailable with a typed code, never degraded to a weaker mechanism. A per-machine MSI ships only together with the elevated worker.

| Mode | Location | Elevation | Code when asked | Reason |
|---|---|---|---|---|
| Per-machine MSI (new, signed) | `%ProgramFiles%\Winora` | Available (if gates G1..G9 of protocol.md section 3 pass) | none | Directory is writable only by administrators/TrustedInstaller, so path + signature are a trust root. A machine administrator opts in by installing it |
| Per-user MSI (current 0.x, frozen) | `%LOCALAPPDATA%\Programs\Winora` | Never | `elevation.install_untrusted` | Any process running as the user can replace the files; a UAC prompt for a user-writable image is a prompt for attacker code |
| Portable zip | any folder | Never | `elevation.install_untrusted` | Same, and path unknown at build time |
| Per-machine, unsigned or signer not pinned (local build, CI artifact before signing) | Program Files | Never | `elevation.unsigned_build` | No signer identity to verify. Under Smart App Control an unsigned build is also expected to be blocked (13.1) |
| VmTrust development build (section 5) | `%ProgramFiles%\WinoraVm` of a test VM | Only with the VM test chain | n/a | Separate identity, never interoperates with release |
| UAC disabled | any | Never | `elevation.uac_disabled` | No split token, no integrity boundary |
| Desktop already elevated | any | Never | `elevation.client_elevated` | Client assumed Medium integrity |
| Elevate-without-consent configured (prompt behavior "no prompt") | per-machine | Allowed with warning | none | DECIDED (D-12): in-app warning plus a second confirmation; non-interactive CLI refuses |

Why not allow elevation from the portable or per-user package by pointing at an installed worker: the worker authenticates its client by path and signature (protocol.md section 7.2). A client in a user-writable folder can be replaced or have DLLs planted beside it by same-user malware, which turns "signed client" into "any code". The portable package therefore remains the 0.x capability (read-only discovery plus HKCU settings). This is DECIDED (D-08); the alternative (portable client driving an installed worker) is rejected.

Consequence for the installers (D-08): the per-user MSI stays for the non-elevated tier; the per-machine MSI does not own the Trust key (3.1, 6.4); an unsigned or per-user build shows the machine-setting area as unavailable with the typed code, and never raises a prompt.

## 3. Per-machine package specification (future `installer/Winora.Machine.Installer`)

Nothing below exists. Current facts: `installer/Winora.Installer/Package.wxs` is `Scope="perUser"`, installs to `%LOCALAPPDATA%\Programs\Winora`, uses UpgradeCode `{912F5E18-EB3C-4C58-A6C7-4FE958DA71BE}`, has no custom actions, writes HKCU bookkeeping for the shortcut.

### 3.1 Contents and layout

```
%ProgramFiles%\Winora\
  desktop\  Winora.Desktop.exe + deps          (signed)
  cli\      Winora.Cli.exe + deps              (signed)
  worker\   Winora.Worker.exe (NativeAOT if spike S0 passes, else self-contained; Windows subsystem; signed)
  run\      (empty directory; the worker creates the attestation record, the early-failure record and worker.lock here)
  FILES.sha256, LICENSE, THIRD-PARTY-NOTICES.md, third-party-licenses\, sbom.cdx.json
```

- `Scope="perMachine"`, `ALLUSERS=1`, all-users Start Menu shortcut, Apps and features entry in HKLM. One UAC prompt at install. The install folder is fixed to the Program Files known folder: `INSTALLDIR` is not a public, overridable property (a silent install with a relocated root would break the image guard rules of protocol.md 4.3), and the client additionally requires the path to equal `HKLM\SOFTWARE\Winora\Install\Root` and to lie on a fixed local NTFS volume (R-20).
- No custom actions (preserves ADR-008). Registry values are written by MSI table rows only, and only these: `HKLM\SOFTWARE\Winora\Install\Root` (REG_SZ canonical install root) and `HKLM\SOFTWARE\Winora\Install\Version`. These components are NOT permanent: uninstall removes them, so a stale install record cannot validate a leftover copy (R-19).
- **The MSI does not author the `Trust` key and does not rely on any `Permanent` attribute** (DECIDED D-06, D-08). The worker creates `HKLM\SOFTWARE\Winora\Trust` on first use with an explicit protected DACL (6.4). The MSI authors the key path `SOFTWARE\Winora\Install` only, never the parent `SOFTWARE\Winora`, so no installer removal logic names the parent that holds `Trust`. Whether Windows Installer removes an empty or non-empty parent on uninstall is `[U]`: experiment XI-2.
- Event Log source registration for the audit trail (section 12) is authored as registry table rows under `HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\Winora` (EventMessageFile, TypesSupported). The WiX Util extension's event-source element is NOT used because it relies on a built-in custom action, which would break the no-custom-action rule; `[U]` that registry-only registration works with a standard user writer: experiments XE-1, XE-2.
- Directory ACLs: nothing is overridden. Program Files defaults (Users read/execute; Administrators, SYSTEM, TrustedInstaller full) are the required state. The package must not use `LockPermissions` to add any ACE. The image guard evaluates the effective result at run time over every directory from the volume root through `InstallRoot` and everything below it (`desktop`, `cli`, `worker`, `run`) and every file in `desktop`, `cli`, `worker`: no principal outside {SYSTEM, Administrators, TrustedInstaller} holds write, delete, delete-child, `WRITE_DAC`, `WRITE_OWNER`, append or create rights (protocol.md 4.3.4; R-20). Failure is `elevation.install_untrusted` with a logged reason token, never a retry. A VM script a development tool that is not published (phase 2) dumps and asserts every DACL in the tree, including `run\`, on `C:` and on a second volume (X12).
- Reparse points: the package creates none; the client refuses an install tree containing any reparse point on the path or inside `desktop|cli|worker|run` (protocol.md 4.3.2).
- Desktop and CLI also live in Program Files: the worker authenticates its client by path and signature, so the client must be in the trusted root as well.
- Winora data stays per user in `%LOCALAPPDATA%\Winora` (journal, preferences, logs). The installer never touches it. The worker never uses it. The folder inherits the user's profile ACL; the design does not rely on it (R-30).
- Launch condition: x64 Windows 11 build 22000+ as today; machine-scope definitions carry `minBuild` of at least 22621 (13.1). ARM64 stays unsupported.
- Runtime: the worker must not depend on a machine-wide, user-influenced runtime resolution (protocol.md 14, ADR-014). Desktop and CLI remain framework-dependent per ADR-008.

### 3.2 Moving between the per-user and the per-machine line: behavior and the experiments that settle each `[U]`

Facts that constrain the design: Windows Installer does not change install context in a major upgrade, so a per-user product and a per-machine product are different products (research row 20: VERIFIED from search-result summaries of Microsoft pages; the page bodies were not read, so XI-1 confirms it on a VM); an elevated installer cannot reliably find the per-user product because HKCU belongs to the installing account (the alternate-credential problem of protocol.md 11.4).

DECIDED (D-08, ADR-011): the per-machine line gets a NEW permanent UpgradeCode. The per-user line is frozen at its last 0.x release. Migration is documented, not automated. Both lines read `%LOCALAPPDATA%\Winora` (journal, preferences, logs) and may run at the same time.

| Transition | Decided behavior | Unverified claims (`[U]`) | Experiment (VM, snapshot before each) |
|---|---|---|---|
| Per-user to per-machine | User installs the per-machine MSI. It neither removes nor adopts the per-user product (different UpgradeCode). Two Apps and features entries, two sets of shortcuts. Release notes: "uninstall the per-user package first" (ADR-008 wording kept). User data is picked up unchanged; the first machine operation migrates the journal (3.5) | Side-by-side is harmless; per-user shortcuts and ARP entries do not collide; a per-user product with the same UpgradeCode would not be upgraded across contexts | XI-1 (= X11): install the existing per-user MSI, then the per-machine MSI; record entries, files, shortcuts; uninstall each; confirm `%LOCALAPPDATA%\Winora` untouched. XI-1b: attempt an MSI with the per-user UpgradeCode and `ALLUSERS=1` over the per-user install; record refusal or second product |
| Per-machine to per-user | User uninstalls the per-machine package and installs the frozen per-user MSI. Machine settings that Winora changed persist (3.4). The journal may be v4, which a frozen 0.x build refuses (`journal.schema_unsupported`) and leaves untouched; restoring `simulation.v3.bak` brings back the old history but loses machine rows (3.5) | The frozen build refuses cleanly (no write, no repair attempt); the message can name the per-machine line only if a last per-user release carrying that text is cut before the per-machine line ships; already-released builds show the generic message | XI-4: open a v4 file with the last 0.x build; expect refusal, byte-identical file, no new files. XI-5: restore the backup and run the 0.x build |
| Per-machine upgrade (same UpgradeCode, newer) | MSI major upgrade replaces the tree atomically. Fails with a restart-required message when `worker.lock` is held. The floor in `Trust` is untouched. Journal schema migrations follow the same backup rule as 3.5 | Major upgrade removes files cleanly while an idle VmTrust worker exists; stale files in `run\` do not block | XI-3 (= V-27): upgrade while an idle VmTrust worker holds the lock and while it does not |
| Per-machine uninstall | Removes files, shortcuts, the `Install` key and the Event Log source rows. Leaves: the `Trust` key (never MSI-owned), `%LOCALAPPDATA%\Winora`, all machine settings Winora changed, and existing Event Log entries. ARPCOMMENTS and release-limits say that machine changes persist | `Trust` survives uninstall and reinstall; `Install` is removed; the `run\` folder is removed despite leftover files, or its failure does not block removal | XI-2 (= X10): install, run the VmTrust worker so `Trust` exists, uninstall, reinstall, upgrade; record key presence. XI-3b: abnormal worker termination leaving files in `run\`, then uninstall |
| Per-machine install in a non-default folder | Not supported: `INSTALLDIR` is not overridable. Administrative install (`msiexec /a`) and relocated copies fail the install-root rule | Image guard rejects a copy extracted elsewhere; `Install\Root` mismatch is detected | XI-6: admin image extraction and a hand copy of the tree to another folder and volume; expect `elevation.install_untrusted` |
| Repair / modify | Reinstalls files; ACLs stay the Program Files defaults. The image guard still decides at run time | Repair does not widen any ACL | XI-7: tamper an ACL (VM only), run repair, re-run the guard |
| Per-user uninstall (frozen line) | As ADR-008: user data kept | none new | none |

Downgrade: `MajorUpgrade` blocks MSI downgrade. Uninstall plus install of an older genuine MSI is possible; the anti-rollback floor (6.4) makes the older worker refuse to operate once a newer worker has run on the machine.

### 3.3 TOCTOU-safe verification and launch (summary; steps in protocol.md 4.3)

Open once with a share mode that denies write/delete, verify Authenticode and DACLs and hash on that handle, keep it while launching, confirm the started process image has the same file id, and have the child re-verify its own image and the client's. Admin-only ACLs are the primary control; the handle is defense in depth against a misconfigured ACL and against races during the window (X17 measures the handle's effect).

### 3.4 Installer effects on the worker and trust state (R-38)

Install creates `run\` empty (CreateFolder row, no custom action), the `Install` key values and the Event Log source rows. Upgrade replaces the tree atomically and fails with a restart-required message if `worker.lock` is held. Uninstall behavior is in the table above. A per-machine uninstall never reverts machine settings; a tool that does so is not planned in M4/M5.

### 3.5 Journal v4 migration and backup (R-28)

Before the per-machine build migrates the journal from v3 to v4 it copies the database to `simulation.v3.bak` in the same folder (temp name then atomic move, never overwritten; one backup per source schema version) and logs that fact. A failed migration leaves v3 intact. The per-machine release notes document that the two lines share data and that downgrade requires restoring the backup (with loss of machine rows). Tests: U-J06 (backup before migration, failed migration leaves v3 intact), XI-4, XI-5.

## 4. Signing authority, key custody, pinning, revocation

### 4.1 Requirement

Release binaries (Desktop, CLI, Worker, their Winora*.dll) and both MSIs carry an Authenticode signature with a SHA-256 digest and an RFC 3161 timestamp. Third-party DLLs keep their own signatures or none and are covered by the signed MSI and `FILES.sha256`; the worker and client never `LoadLibrary` them from untrusted places. Verification code in the product accepts only: chain to a Windows-trusted root, code-signing EKU, signer matching the pin set of 4.3. Verification flags are fixed in protocol.md Appendix A (no revocation retrieval, cache-only URL retrieval, no UI, MD2/MD4 disabled; R-18). If chain building would need the network the result is `elevation.install_untrusted`, never a retry.

### 4.2 Decision (D-07)

DECIDED (D-07): apply to the SignPath Foundation now. Fallbacks, in order: an OV certificate on a cloud HSM; Azure Artifact Signing if the owner qualifies by location. Self-signed or private-CA certificates exist only for the VmTrust chain on test VMs and never for a release. The signing step runs only in a protected CI environment ("release") that requires a reviewer other than the actor who triggered it; no developer machine, no agent session and no pull-request job can sign. Pull-request builds produce unsigned artifacts, which the product treats as unavailable for elevation.

| Option | Status | Notes |
|---|---|---|
| SignPath Foundation (free for qualifying open-source projects) | Primary, application pending | Key held in an HSM by the service; publisher shown to users is "SignPath Foundation" (VERIFIED for real projects). The subject is shared with other projects (4.3). Terms and eligibility VERIFIED in the research note; pinning and renewal behavior UNVERIFIED (Q-S1..Q-S4) |
| OV certificate on a cloud HSM | Fallback 1 | Yearly cost, identity validation. Certificates issued from 2026-03-01 are capped at 460 days with keys in certified hardware (PLAUSIBLE). EV no longer gives instant SmartScreen reputation (VERIFIED) |
| Azure Artifact Signing | Fallback 2 | Eligibility depends on location (not yet checked for the owner) |
| Self-signed / private CA | VmTrust only | Never for releases |

Publisher name: under SignPath the certificate subject is the service's. The MSI Manufacturer stays the project name; the earlier rule "MSI Manufacturer equals the certificate subject" is withdrawn (D-07 supersedes), and the pre-launch dialog shows the publisher that the UAC prompt will show (protocol.md consent content; R-35) with one explanatory sentence that the signing service signs for several projects.

### 4.3 Pinning and signer identity (D-07, R-17)

Pin by certificate SUBJECT plus trusted-root chain plus a small set (maximum 3) of SHA-256 hashes of the issuing intermediate's SubjectPublicKeyInfo, never a single thumbprint. A leaf rotation within the same issuer needs no code change; an issuer change needs a release shipping old and new pins for one cycle. Rejected: pinning leaf thumbprints (breaks at every renewal), subject name only (a different CA can issue the same subject text), trusting any Microsoft-trusted publisher.

Signer equality (client and worker must "have the same signer") means: same pinned subject DN AND issuer SPKI hash in the pinned set, never a leaf thumbprint. Equality survives leaf rotation (tested: U-I08 rotated leaf accepted, wrong issuer rejected).

Because the SignPath subject and issuer are shared by every project the Foundation signs, the pin identifies the signing service, not Winora. The control that makes a binary "genuine Winora" is therefore the AND of (a) the administrator-only install root (Program Files path, effective-ACL walk, reparse and volume rules of protocol.md 4.3, equality with the install record) and (b) the Authenticode subject pin. Residual limit, stated rather than hidden: anyone who can place a file in the install root is already an administrator; a binary signed for another project by the same service and run from elsewhere cannot be mistaken for the client because the client must be in the install root. ADR-011 records this as: "with a shared-subject signing service the signature check is a tamper check, not a project identity". A signed release manifest (hashes of every shipped Winora file, signed with a Winora-held key) is added only if the install-root control proves insufficient; it is NOT part of M4a (DECIDED D-07; R-17 option kept in reserve).

The same limit applies to the client check itself: the worker identifies the client image through the peer process id, and process ids can be reused or handed off (protocol.md 4.2, X3). The image and signature check is therefore defense in depth beside the kernel token facts, not client authentication by itself (D-04).

### 4.4 Timestamping and revocation

- Timestamp: RFC 3161, SHA-256, so signatures remain valid after the leaf expires. Clock rollback by an attacker can make an expired certificate look valid unless a timestamp is present; with timestamps required for acceptance of an expired leaf, rollback only matters for certificates that have not yet expired (AB-25). Whether the SignPath timestamp authority is acceptable: Q-S3. Expired-leaf-with-timestamp behavior: X9.
- Online revocation (CRL/OCSP) is NOT consulted at run time: Winora is an offline product and the elevated worker must not use the network. A revoked-but-pinned certificate keeps being accepted until a release changes the pins. Key compromise response: (1) revoke at the CA/service, (2) release a new version with new pins, a raised definition floor and the next definition key only (6.4), (3) advise users to update. Old installs keep accepting old material until updated; this is a documented limit, not a fix (see 4.7).

### 4.5 Release signing order (R-37)

`tools/publish.ps1` already orders work so signing slots in after publish and before `FILES.sha256`/zip/MSI (release.md). Required order for a release workflow: (1) build unsigned and record `UNSIGNED-SHA256SUMS` (hashes of every shipped file before signing); (2) sign `Winora*.exe` and `Winora*.dll` with SHA-256 and an RFC 3161 timestamp; (3) verify each signature and timestamp; (4) compute `FILES.sha256`, build the zip, build the MSI; (5) sign the MSI last; (6) compute `SHA256SUMS` over signed artifacts; (7) publish both lists. Independent rebuilders compare the unsigned list and the Authenticode PE digest (the hash the signature covers), since signed bytes cannot be reproduced. NativeAOT reproducibility is unverified (X18): until shown, the release notes state that the worker is not claimed reproducible. The CycloneDX list and `THIRD-PARTY-NOTICES.md` generation add the ILCompiler and runtime packs and a "statically linked runtime" component for a NativeAOT worker, plus `System.Security.Cryptography.Cose` and any other package the elevated image gains (D-05). `verify_dist.ps1` after the change (WP-0 and WP-G): for release builds require `Get-AuthenticodeSignature` Valid, expected subject, timestamp present, absence of the VmTrust literal, worker subsystem and manifest level read from the PE (never by running it), per-machine MSI tables (`Property ALLUSERS=1`, no `CustomAction`, HKLM registry rows only for `Install` and the Event Log source), the `run` directory row present, and an empty release definition set until the M5 gate.

### 4.6 CI hardening on the public repository (D-20)

Adopted now: GitHub Actions pinned by commit SHA; build-provenance attestations (SLSA Build Level 2; Level 3 needs a reusable isolated workflow); protected branches and tags without administrator bypass; passkeys or 2FA on every account with write access; Dependabot and OSV scanning; CodeQL; OpenSSF Scorecard; the CycloneDX SBOM (kept); the reproducible zip (kept). These are repository settings; verification.md records screenshots or API output as evidence because they cannot be verified from the checkout.

### 4.7 Updates (D-10)

DECIDED (D-10): there is no in-app updater. Distribution is GitHub Releases and winget, with hashes, SBOM and build-provenance attestations. A later opt-in, notify-only update check may be added under an ADMX policy; it never downloads or executes anything and never lives in the worker. Consequence stated plainly: a revoked definition or pin reaches a machine only through a new release that the user or administrator installs; before the first machine-scope definition ships the release-limits text must say how long a withdrawn definition can stay usable, and this decision must be revisited (the gate list in 13.4 includes it).

### 4.8 SignPath risks accepted explicitly (D-07)

1. The Foundation can pause the subscription or revoke the certificate; releases stop or must be re-signed. 2. Shared subject (4.3). 3. Pinning/renewal behavior is unknown. Vendor questions to ask at application time: Q-S1 intended subject and its stability across renewals; Q-S2 renewal cadence and whether the issuing intermediate changes; Q-S3 timestamp authority and policy; Q-S4 whether per-project signing policies can restrict signers (artifact configuration, origin verification) and whether signed artifact metadata can carry a project-specific attribute. Answers are recorded in verification.md; until answered no pin constant is written.

## 5. Unsigned, development and VM behavior

- An unsigned build, a build signed by an unpinned certificate, or a build run from the wrong place never starts a UAC prompt: gates G1/G2 (protocol.md section 3) return `elevation.unsigned_build` or `elevation.install_untrusted` (localized text in Strings.resx, phase 2). The UI shows the feature as unavailable with that reason.
- Developer override: DECIDED (D-09, ADR-011): none at runtime. No environment variable, registry value, config file, command-line flag, profile field or "allow unsigned" setting exists. Every runtime switch is reachable by same-user malware or by a standard user and would turn the signature gate into a default-off checkbox. A flag guarded by "must be administrator to set it" adds a second privileged configuration surface for no benefit.
- Compile-time alternative, accepted (D-09): an MSBuild configuration `VmTrust` (not Debug, not Release) defines the symbol `WINORA_VM_TRUST`. Only that configuration, only on disposable VMs:
  1. Trust pins are replaced by a test CA/public-key hash compiled into the VmTrust binaries. In M4a the only operation is the built-in `winora.selftest.noop` (it reads and writes nothing); once the definition layer exists the VmTrust set is signed by a throwaway test definition key whose public half is compiled in. Neither exists in release binaries.
  2. Identity details (R-23): install folder `%ProgramFiles%\WinoraVm`, registry `HKLM\SOFTWARE\WinoraVm`, pipe prefix `Winora.VmElev.`, a different UpgradeCode and an obvious product name. Every assembly carries a `const string BuildFlavor` literal ("vmtrust" or "release"); the transcript includes a one-byte flavor (0 release, 1 vmtrust; protocol.md 8.2), so release and VmTrust binaries refuse each other with `Finished` failure. The "install root equals ProgramFiles\Winora" rule reads the flavor's constant folder.
  3. The VM kit refuses to import the test root certificate or to install a VmTrust package unless the VM marker file `C:\WinoraVm\VM-MARKER.txt` exists (first line `WINORA-DISPOSABLE-VM`; the scripts also refuse to run inside a git checkout); installing a test root on any other host is forbidden. Test private keys are generated per run under `tests/vm/keys` (not committed) and never exist on the release runner.
  4. The release pipeline is meant to fail if a shipped worker or Windows-library binary contains the literal "vmtrust" or the test key id. What is enforced today (unverified for any other file): a unit test over the built worker and Windows-library outputs (`WorkerFlavorTests`). The Domain and Protocol assemblies necessarily contain the enum member `VmTrust` and the self-test handler, and the VM kit's `Winora.VmElev.` pipe prefix is a compiled string, so the literal cannot be absent from every binary; `verify_dist.ps1` scans every file of the portable package for the VM test identity (`Winora VM Test`, the test key id, the `WinoraVm` folder name); the pipe prefix is not scanned because it is chosen at run time from the flavor and is present in release binaries by design; the MSI cab is not scanned yet; CI never builds `VmTrust` on the release branch.
  5. An attacker cannot enable this on a release install: it is a different set of binaries. Installing a VmTrust build requires an administrator. AGENTS.md forbids executing any elevation code on the development host; the dev-host guards (plan WP-0) enforce that no script, test or CI step executes the worker image, and only the VM kit (marker-guarded, allow-listed) may.
- Local developer loop without a VM: all unelevated tests (protocol.md section 16) cover the protocol library with fakes. No developer-side elevation is required or supported.

## 6. Signed definition set

### 6.1 Decisions (ADR-012, D-05)

| Question | Decision | Rejected and why |
|---|---|---|
| Format of the payload | UTF-8 JSON in a restricted profile: no comments, no duplicate keys, ASCII keys, depth <= 8, size <= 256 KiB, integers only. The signature covers the exact bytes of the file and is verified BEFORE parsing; no canonicalization exists | CBOR payload (extra package, harder diff review); RFC 8785 canonical JSON (a canonicalizer is more code than a byte-exact signature and gains nothing when the file is shipped, not re-serialized) |
| Signature | COSE_Sign1 (RFC 9052) with DETACHED payload, algorithm ES256 (ECDSA P-256 with SHA-256, 64-byte r\|\|s as COSE defines). Protected header: `alg` = -7 and `kid` = first 8 bytes of SHA-256(SPKI) only; any other header, any critical parameter, any other algorithm is rejected. The domain-separation label `"winora/defset/v1"` is the COSE external additional authenticated data. Verified with `System.Security.Cryptography.Cose` (`VerifyDetached`) | Ed25519 (no supported .NET/CNG path found); RSA (larger, no benefit); ML-DSA (needs a Windows 11 Insider build, not a baseline); JWS and a custom sidecar (no inbox verifier) |
| Keys | Two or three pinned definition public keys (P-256) compiled into the binaries (current, next, optional spare), with `kid`. Rotation = release carrying both, then a release dropping the old one. Private keys in a cloud KMS/HSM or hardware token, used by a protected CI job whose approvers satisfy D-17; public keys and `kid` printed in release notes | Single key (no rotation); threshold signatures (`CoseSign1Message` is single-signer; multi-signer needs custom CBOR) |
| Hash for binding | `H_d = SHA-256("winora/defset/v1/hash\0" \|\| bytes)`, placed in the attestation, both hellos and the transcript. For M4a (no set) protocol.md defines H_d as the hash of the empty set so the transcript shape is already final | |
| Where it lives | EMBEDDED as resources `MachineDefinitions.v1.json` and `MachineDefinitions.v1.cose`. The worker assembly embeds its own copy; Desktop and CLI load a copy from `Winora.Domain.dll`; three copies exist and skew is detected by `H_d` in the handshake (`definition.set_mismatch`). The worker parses nothing from disk at run time | A file under Program Files (extra TOCTOU and version skew in the elevated process) |
| Why also sign when Authenticode already covers the resource | (1) each process verifies the same set independently of how the carrier was loaded; (2) a reviewer can verify and diff a set offline against the published key without a Windows trust chain; (3) separates the approval path (a different job and approvers than the Authenticode key); (4) lets the set move out of the binary later. Honest limit: the binaries embed the definition public keys, so whoever can ship a validly Authenticode-signed binary with different embedded keys defeats this separation; it is a process control, not a cryptographic one | |
| When it exists | The definition-signature LAYER (verifier, `kid` pinning, signing job, hostile corpus) is NOT part of M4a (D-05, D-14). It is implemented before the M5 gate (13.4). Until then the release set is empty and the M4a no-op is a built-in VmTrust-only operation | |

Custody (2026-10-11, agent decision under the owner's delegation, reversible): the definition key is created on a hardware token held by the owner (PIN and touch per signature, non-exportable) and used by hand; a second token holds the next key; no CI job holds it; no key is created or pinned before M5 prepares the first definition.

Status (2026-10-11): the verifier is implemented (`DefinitionSetVerifier`, project Winora.Elevation.Protocol) with the package `System.Security.Cryptography.Cose` 10.0.12 (spike S0 result 10), pinned centrally with lock files and listed in the notices. The JSON profile is read by a hand-written strict reader, not by `System.Text.Json`, so the elevated worker has no general JSON library. No key is pinned in any shipped binary yet: key custody (D-17) is an owner decision, so the release set stays empty and no set is embedded. a development tool that is not published is a skeleton that calls an external signer.

Dependency note (D-05 consequence, contradicts plan rule 5 "no new package"): COSE verification uses the `System.Security.Cryptography.Cose` package. It must be pinned centrally with a lock file, appear in the notices and SBOM, be compatible with the chosen worker runtime (spike S0 must verify it under NativeAOT) and be a stable, non-prerelease version when pinned (the Microsoft page lists a preview version, UNVERIFIED for GA). If S0 or the version check fails, the fallback needs an owner decision (OPEN): verify a raw detached ECDSA P-256 signature over the domain-separated bytes with a minimal envelope, using the same keys.

### 6.2 File shape (illustrative, not a shipped file)

```json
{
  "format": "winora.machine-definitions/1",
  "setId": "winora-machine",
  "serial": 1,
  "minProtocol": 1,
  "minWorkerBuild": 22621,
  "revoked": [ { "id": "machine.example.withdrawn", "belowVersion": 3, "allowRestore": false } ],
  "definitions": [ { "id": "machine....", "version": 1, "scope": "machine", "target": { }, "choices": [ ], "restoreDomain": [ ],
                     "restoreCompatible": [ ], "managedBy": [ ],
                     "recovery": "Reversible", "risk": "Medium", "requiresRestart": false, "minBuild": 22621, "maxBuild": null,
                     "sourceRefs": [ "https://learn.microsoft.com/..." ], "reviewRef": "SEC-DEF-0001" } ]
}
```

`serial` is a monotonically increasing integer assigned at signing time and is the only freshness mechanism (no timestamps). Machine definition ids use the reserved prefix `machine.` and the user catalog refuses that prefix (R-31). The M4 release set contains zero definitions; the VmTrust build contains the built-in no-op (M4a) and later self-test definitions. `restoreCompatible` lists earlier versions of the same definition whose journal rows this version can restore (D-18); `allowRestore` on a `revoked` entry keeps a revoked definition restorable (6.6); `managedBy` lists the documented policy channels that can control the target (7.1 item 7).

### 6.3 Verification (both processes, same code)

1. Load the embedded bytes; reject size > 256 KiB.
2. Decode the COSE_Sign1; require the protected header exactly as in 6.1, `kid` among the pinned keys; verify the detached signature over the raw bytes with the external AAD. Any failure: `definition.signature_invalid`. No byte is parsed before this succeeds.
3. Strict-parse the JSON profile (duplicate keys rejected) and validate against the schema of 7.2 (every field required, every string grammar-checked, every collection bounded, unknown fields rejected). Failure: `definition.signature_invalid` (the set is unusable even if signed; the build also validates before signing).
4. Machine invariants (7.1 as code): hive HKLM only, no forbidden path fragments, deny-list, one target per definition, choices within `restoreDomain`.
5. Floor check (6.4). Compute `H_d`.
6. The Desktop and the worker compare `H_d` in the handshake; a difference fails closed with `definition.set_mismatch` before any request.
7. A worker whose embedded set has zero definitions answers `BatchBegin` with `Abort(worker.unimplemented)` before any lookup; `definition.unknown` is returned only when the set is non-empty and the id is absent (R-03).

### 6.4 Anti-rollback floor (D-06)

- Store: ONE `REG_BINARY` value `Floor` in `HKLM\SOFTWARE\Winora\Trust` (64-bit view) = `u8 version(1) || u64 serial (little-endian) || hash[32]`, written by one `RegSetValueEx` so a torn pair cannot exist (R-19). The worker creates the key on first use with an explicit protected DACL (SYSTEM and Administrators full control, Users read). The MSI never authors, owns or removes it.
- Initial state: key or value absent means floor `(0, none)`; the worker advances the floor only after the definition set verified and `BatchBegin` was accepted. An administrator-deleted key is indistinguishable from a first run; that is the honest limit below.
- Both processes verify, before trusting the value, the key's EFFECTIVE DACL (inherited entries and resolved CREATOR OWNER entries included): no write, `WRITE_DAC`, `WRITE_OWNER` or set-value to any principal other than SYSTEM, Administrators, TrustedInstaller, and the key is not a link (`trust.store_insecure` otherwise).
- Rule: set serial < floor serial -> `definition.rollback`. Equal serial with a different hash -> `definition.set_mismatch` (equivocation). Greater serial is accepted. A malformed value is `trust.store_insecure`.
- Writer: only the worker, only after it verified the set and accepted the batch; if it cannot persist the new floor it exits without mutating. The Desktop only reads (early refusal and a clear message).
- Honest limit (D-06): this defends against replay of an old signed set by non-administrator attackers only. Any administrator or SYSTEM can reset it, and restoring a machine from backup restores the old floor. It is not tamper-resistant against administrators. TPM NV counters were not shown practical for a .NET application (no Windows how-to found); revisit on evidence.
- Rejected: a file in `%ProgramData%` (pre-creatable by non-admins), a DPAPI blob in the user profile (user-writable), a counter in the journal (untrusted), wall-clock expiry (rollbackable), two separate values (non-atomic), MSI ownership with a `Permanent` component (relies on unverified behavior).
- Tests: U-D05 (extended: torn pair impossible), U-D12 (absent floor, equal-serial-different-hash, malformed value, effective-ACL evaluation with inherited and CREATOR OWNER entries), XI-2 (floor survives uninstall and reinstall of both lines), V-22.

### 6.5 Versioning and compatibility

- Per-definition `version` increases when the target, choices, restore domain or semantics change. A journal row carries `(id, version, setSerial, H_d)`.
- Revert of a row whose definition version still exists is allowed. If the version differs, revert is allowed only when the current definition lists the row's version in `restoreCompatible` (D-18); otherwise it is refused with `operation.not_reversible` and guidance, never guessed. A row whose definition id is unknown to the current set is quarantined (`journal.untrusted_definition`).
- Set `format` changes only with a new wire/profile review. A client and worker built from the same revision share the set; skew is detected by `H_d`.
- `minProtocol` in the set prevents a signed set from being used over an older, weaker protocol.

### 6.6 Revocation of a definition (D-10, D-18)

Add `{ id, belowVersion, allowRestore }` to `revoked` in a newer set (higher serial). Once any worker has run that set, the floor prevents the older set. Revocation reaches a machine only through a new release (4.7). Until then the old definition remains usable on that machine; the pre-launch dialog shows the set serial so support can tell. A revoked definition that exists in the journal remains revertable only if the revoked entry says `allowRestore: true` (restore-only mode: the value to write must still be in `restoreDomain`); otherwise revert is refused with `operation.not_reversible` and an explanation. Apply of a revoked definition is always refused (`definition.revoked`).

## 7. Machine-scope definition admission

### 7.1 Criteria (all required; a definition not meeting one is not added)

1. Documented source. A primary Microsoft document (Win32, policy CSP, Group Policy reference, support article) naming the exact registry value or service and its effect. "Observed only" is NOT enough for machine scope. Evidence class and URLs recorded in `sourceRefs` and technical-docs/sources.md.
2. Reversible AND restorable at run time. `recovery` must be `Reversible` or `Conditional`; `Irreversible` is not admissible. The pre-existing value (including "value absent") must lie in `restoreDomain`. This is also enforced at run time: Prepare returns `Refused` with `setting.state_unrecognized` when the observed value is not a member of `restoreDomain` by kind and exact bytes, and the client's plan performs the same test before showing Apply (R-11), so an OEM-set or wrongly typed value never leads to an unrevertable change.
3. No deletion. No key deletion, no recursive operation, no file/service/task deletion or creation, no changing of binary paths or service accounts. Allowed: set one registry value to a value from `restoreDomain`; delete that single value only when `Absent` is a member of `restoreDomain`; change one service's start type among Automatic, Manual, Disabled (never Boot/System; never stop/start as the effect). Automatic (delayed) is NOT admissible until a two-step operation with its own journal and recovery rows is designed, because delayed auto-start is a separate configuration from one `ChangeServiceConfig` call (R-11).
4. Bounded target. Exactly one target per definition (one value, one service, or one value name on the driver key of display adapters, see 7.3). Literal strings: no wildcards, no `%`, no `..`, no environment expansion, ASCII only, key path <= 255 chars, value name <= 128, value <= 1 KiB. Hive is HKLM only. 64- or 32-bit view explicit. Subtree must be in the allow-prefix list (7.3).
5. Observable without elevation. The state must be readable by a standard user (preview, verification and crash recovery never need the worker). Definitions on keys unreadable to Users are not admissible.
6. Not a protection downgrade. No definition may weaken: User Account Control, SmartScreen, Defender or any anti-malware setting, firewall, Windows Update signature/verification, credential protection (LSA, Credential Guard, LAPS), auditing/event logging, Secure Boot/boot configuration, driver signing, code integrity, or the Winora trust store itself. An exception requires its own ADR and owner sign-off.
7. Respect management (policy awareness; gate item in 13.4). Every definition declares `managedBy` (the documented policy channels: policy registry value locations and/or Policy CSP names). The preview reads those locations; when a managed value is present the preview reports "managed by policy" and Apply is refused (new code `setting.managed_by_policy`, to be added to errors.md) instead of overriding. A definition whose target itself lives under a Policies key declares that fact in `managedBy` with the rule "present value means managed", because comparing a policy value with itself is meaningless. Values that Windows protects against non-Microsoft writers (UCPD for default-association values is the known case; evidence is secondary, UNVERIFIED as Microsoft documentation) are unsupported, not worked around. Winora does not fight management.
8. Compatibility bounded. `minBuild` is required (at least 22621, 13.1); unknown builds fail closed (unsupported), as in M2.
9. Two reviewers recorded (D-17, 7.5): the author cannot be the only approver; the checklist of 7.4 completed in the pull request; `reviewRef` in the definition; a VM test (apply, read-back, revert, external-change conflict, interrupted apply at every durable boundary of section 11) on the oldest and newest supported builds recorded in verification.md.
10. Originality. Source and wording authored from Microsoft documentation; nothing copied from other tools (AGENTS.md).
11. No shell refresh. Machine scope performs no broadcast or Explorer restart in M4 or M5. A definition that needs one is not admissible until an ADR defines a journaled client-side refresh (R-14).

### 7.2 Typed schema (documentation only, not code)

```csharp
public enum MachineTargetKind { RegistryValue, ServiceStartType, DisplayDriverValue }   // DisplayDriverValue: see 7.3, the administrator Desktop app only
public enum MachineRecovery { Reversible, Conditional }                  // Irreversible is not representable
public enum ServiceStartKind { Automatic, Manual, Disabled }             // AutomaticDelayed excluded (7.1 item 3)
public enum MachineValueKind { DWord, QWord, String, Absent }            // no Binary, MultiString, ExpandString in M4/M5 v1

// HKLM only: there is no hive field, mirroring M2's HKCU-only RegistryTarget.
public sealed record MachineRegistryTarget(string SubKey, string ValueName, RegistryViewKind View);
public sealed record MachineServiceTarget(string ServiceName);           // [A-Za-z0-9_.-]{1,64}, no path chars

public sealed record MachineValue(MachineValueKind Kind, long? Number, string? Text);    // Absent => both null
public sealed record MachineChoice(string Id, string LabelKey, MachineValue Value /* registry */, ServiceStartKind? Start /* service */);

public sealed record MachineSettingDefinition(
    string Id, int Version, string TitleKey, string DescriptionKey, SettingCategory Category, RiskLevel Risk,
    MachineRecovery Recovery, bool RequiresRestart, int MinBuild, int? MaxBuild,
    MachineTargetKind TargetKind, MachineRegistryTarget? Registry, MachineServiceTarget? Service,
    IReadOnlyList<MachineChoice> Choices, string DefaultChoiceId, string? RecommendedChoiceId,
    IReadOnlyList<MachineValue> RestoreDomain, IReadOnlyList<int> RestoreCompatible, IReadOnlyList<ManagedByChannel> ManagedBy,
    IReadOnlyList<string> SourceRefs, string ReviewRef);
```

Invariants checked by the verifier: exactly one of `Registry`/`Service` set and matching `TargetKind`; every choice value is in `RestoreDomain`; `RestoreDomain` contains the documented default (or `Absent`); ids match `machine.[a-z0-9._]{1,56}`; labels are resource keys, never display text; sizes bounded. The wire and journal byte encodings of `MachineValue` and of the snapshot (a `targetKind` byte; registry layout and service layout) are normative in protocol.md Appendix A; this file does not restate them.

### 7.3 Path policy

- Allow-prefix list (examples to be fixed per batch in phase 2, each entry reviewed): `SOFTWARE\Policies\Microsoft\Windows\<documented subkey>`, `SOFTWARE\Microsoft\Windows\CurrentVersion\<documented subkey>`; services are addressed through the Service Control Manager by name, not by registry path.
- Deny-list compiled into the worker (belt and braces, checked at set load and again before opening): `SAM`, `SECURITY`, `SYSTEM\CurrentControlSet\Control\Lsa`, `...\SecurityProviders`, `...\Session Manager`, `...\Winlogon`, `...\Image File Execution Options`, `...\SafeBoot`, any `Run`/`RunOnce`/`AppInit_DLLs`/`Environment` location, `SYSTEM\CurrentControlSet\Services\*\ImagePath|ObjectName|Parameters`, `SOFTWARE\Microsoft\Windows Defender*`, `SOFTWARE\Policies\Microsoft\Windows Defender*`, `SOFTWARE\Policies\Microsoft\WindowsFirewall`, `SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`, `SOFTWARE\Winora\Trust`, `SOFTWARE\Winora\Install`.
- Aliasing and links (R-31): the path comparison canonicalizes `ControlSet00x` and `CurrentControlSet` to one form before the allow/deny test; after opening, the worker reads the final key name (`NtQueryKey` name information) and requires it to equal the expected `\REGISTRY\MACHINE\...` path, so a registry symbolic link is refused (`[U]`, X7) rather than relying on link-open semantics alone. The hostile-path corpus includes aliases and links.

- Exact entries and exceptions: next to the two allow-prefixes the worker accepts a reviewed list of exact values (one key and one value name each), and a reviewed list of exceptions that let one exact value through although its key matches a deny fragment (for example the Windows Update policy values, the verbose status switch, memory integrity, legacy transport security entries, the SMB 1 server switch and the SMB signing values, and the SmartScreen switches). An entry marked High only is valid only in a definition with risk High. An entry marked delete only may only restore the absent value. Nothing else on the same key passes. The owner approved the High only entries in writing.
- Service rules: a service start type may be changed only for a service on the reviewed service allow list, never when another enabled service depends on it (for a change to Disabled), and only when the service image is a Microsoft-signed file in the Windows folder. The start kinds are automatic, automatic with delayed start, manual and disabled.
- Display driver values: a definition of the kind `displayDriver` names only a value name and a PCI vendor id (`10DE` NVIDIA, `1002` AMD, `8086` Intel). It never names a key, and no key comes from user input. The pair must be on the reviewed list of `DefinitionInvariants` and the definition must declare at least the risk of that entry (High for the NVIDIA fixed performance state, Medium for AMD ULPS). The app finds the key of each present display adapter at run time, in this way: it lists the devices that are present in the display setup class `{4d36e968-e325-11ce-bfc1-08002be10318}` with the device installation functions (it does not walk the registry to find adapters); it keeps a device only when it is on the PCI bus, has no problem code, runs a driver other than the generic Microsoft display driver, names a reviewed vendor in its first PCI hardware id and reports a driver key of exactly the form `{class guid}\NNNN` (four digits); it refuses two devices that report one key. Virtual adapters (Microsoft Basic Display, Hyper-V, VMware and others) fail one of these tests. A laptop with an integrated and a discrete card gives two adapters, and each one is a separate target with its own name in the preview, the journal and the undo. Before any read or write the path must still be `SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\NNNN`, and the registry layer then compares the final key name that Windows reports (`ControlSetNNN` for `CurrentControlSet`) with it. A value is never written to a key that does not exist. The journal records the adapter, its device instance id and the resolved key; the undo resolves the adapter again and goes ahead only when the same device still has the same key and the value is still the one Winora wrote. The vendors do not document these values (class C). Only the administrator Desktop app changes them. The elevated helper does not execute this kind: it knows the definition, ends the batch with `target.kind_unsupported` before any Prepare, and the client executor refuses the plan with the same code before it starts a session.
- Source classes: every definition names its source class (A Microsoft documentation, B vendor or recognized expert, C community) and the links on an explicit host list; a community source may also be a numbered neutral label ("reference source 3") that carries no link and no name and is described in the sources document. The class is shown to the user.

### 7.4 Per-definition review checklist (in the pull request; copied into technical-docs/settings-catalog.md rows)

- [ ] Source URL(s) read; the value name, type, meaning of each value, and OS range match the document.
- [ ] Default state and "absent" behavior documented; restore domain complete, including vendor/OEM values seen on test images; observed-outside-domain behaves as `setting.state_unrecognized`.
- [ ] Single target; literal strings; allow-prefix match; deny-list miss; no alias or link path.
- [ ] Readable without elevation (checked as a standard user on a VM).
- [ ] No protection downgrade (7.1 item 6).
- [ ] `managedBy` filled; Group Policy and Policy CSP channels checked; preview shows "managed by policy" when present; UCPD or other OS-protected value excluded.
- [ ] Effect scope understood (all users, next boot, service restart) and `requiresRestart` set; no shell refresh needed; user-facing text explains it (scope sentence "every account on this PC").
- [ ] Reversibility: restore value shown in the consent dialog; `restoreCompatible` set for any version bump; revoked behavior (`allowRestore`) decided.
- [ ] Failure modes: value of unexpected type, access denied, key missing, value changed concurrently, hive flush behavior (X8).
- [ ] Administrator-protection check: the definition does not depend on HKCU or the caller's profile (the elevated account may differ, 13.1).
- [ ] VM evidence on oldest and newest supported builds: apply, read-back, revert, external change, interrupted apply at each boundary B1..B10 and C1..C5 (section 11), second user session.
- [ ] Localization keys added (English and Persian draft), no hard-coded text.
- [ ] Two different approvers recorded (7.5); `reviewRef` assigned; `serial` bumped; test-key and release-key signing both exercised in CI.

### 7.5 Approver rule and what it means for a one-maintainer project (D-17)

Rule: no machine-scope definition ships until two different people have reviewed it. Enforced by: protected branches without administrator bypass; CODEOWNERS for `definitions/machine/**` and `src/Winora.Elevation.*` requiring an approving review from someone other than the author; the protected `release` environment's "prevent self-review" option for the definition-signing job; two distinct accounts with passkeys or 2FA.

What this means today: the repository has one maintainer, so the machine-scope tier stays CLOSED. Concretely, the release definition set stays empty, `verify_dist.ps1` asserts it, M5 does not start shipping machine definitions, and "second reviewer named and enabled" is a precondition recorded as evidence in verification.md (settings screenshot or API output). The review of the channel itself (D-04, 13.4) is an additional external control, not a substitute. The only substitute the decision admits is a hardware-backed signing flow with an approval delay that the owner explicitly accepts; it is not enabled by default and would be recorded in an ADR. VmTrust self-test definitions never ship and need no second reviewer. No document may claim separation of duties that does not exist.

## 8. Test references

Unelevated (any machine): definition verifier tests U-D01..D06 and D10..D12, floor logic with a fake store U-D05, availability gates U-G01..G08, image guard with fakes U-I01..I03 and I07..I09, install ACL/reparse logic with a fake filesystem and permissive-ancestor, sibling-file and removable-volume cases U-I04..I06, deny-list/allow-list tests U-D07..D09 over a corpus of hostile paths (rooted, `..`, 8.3, unicode confusables, trailing dots, over-length, `%`, ControlSet aliases), schema validator property tests, state-machine tests U-R06..R16 (section 11), v4 migration backup U-J06, test-input isolation U-A04..A06 and U-L04. VM only: V-04, V-05, V-07, V-18, V-19, V-20 (protocol.md) plus V-21 per-machine MSI install/upgrade/uninstall with ACL dump, V-22 per-user to per-machine side-by-side and Trust key survival (XI-1..XI-7), V-23 signed release chain with timestamp after leaf expiry (VM clock, X9), V-24 dev-key binaries rejected by a release build and the reverse, V-26 two sessions, V-27 upgrade while a VmTrust worker is idle (XI-3), XE-1..XE-3 Event Log. M4a runs only the subset in the plan (X1-X6, X9, X12-X18 and the channel V-items); the installer, definition, journal and signing experiments (X7, X8, X10, X11, X19, X20, XI-*, XE-*) run with the packages that create the thing under test.

## 9. Microsoft and external documentation

| Topic | URL | Status |
|---|---|---|
| Authenticode verification on a handle | https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_file_info | Not re-fetched |
| WinVerifyTrust | https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust | Not re-fetched |
| Windows Installer per-user vs per-machine, ALLUSERS, major upgrades | https://learn.microsoft.com/en-us/windows/win32/msi/installation-context ; https://learn.microsoft.com/windows/win32/msi/allusers ; https://learn.microsoft.com/en-ie/Windows/Win32/msi/major-upgrades | Context change in a major upgrade: VERIFIED from search summaries only (research row 20); page bodies not read; XI-1 |
| Program Files default permissions | general Windows behavior | `[U]`; verified by X12/V-21 |
| Registry key security and access rights | https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-key-security-and-access-rights | Already cited in sources.md |
| Service start types (`ChangeServiceConfig`) | https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfigw | Not re-fetched |
| COSE_Sign1 in .NET | https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cose.cosesign1message ; RFC 9052 https://rfc-editor.org/rfc/rfc9052.html | VERIFIED existence of detached sign/verify; GA package version to be confirmed in S0 |
| SignPath Foundation terms | https://signpath.org/terms ; https://signpath.org/ | VERIFIED (terms); pinning/renewal UNVERIFIED (Q-S1..Q-S4) |
| Administrator protection, Smart App Control | Microsoft Learn pages cited in research/elevation-architecture.md | VERIFIED (existence and behavior summary); on-machine behavior needs X13 |
| Event Log sources | learn.microsoft.com Event Logging (RegisterEventSource, ReportEvent) | Not re-fetched; registry-only registration `[U]` (XE-1) |
| UAC elevation behavior | protocol.md section 18 | See there |

## 10. Open risks

1. Signing authority is chosen (SignPath, D-07) but not yet granted; until it is, and until the pins are known (Q-S1..Q-S4), nothing can be pinned and elevation stays disabled in every build.
2. Offline operation means no CA revocation and revocation of definitions only by release; users who never update stay exposed to a withdrawn definition (D-10).
3. The definition/Authenticode separation is a process control; an Authenticode-key compromise lets an attacker ship binaries with their own embedded definition keys. With a shared-subject service the signature is a tamper check, not a project identity (4.3).
4. Anti-rollback stored in HKLM can be reset by any administrator or restored from backup; Program Files ACL defaults and the uninstall behavior of the parent registry key are unverified (X12, XI-2).
5. Coexisting per-user and per-machine installs and the frozen line's refusal of a v4 journal are untested (XI-1, XI-4); users may reach a confusing state until a VM run defines it.
6. Moving the client into Program Files changes the 0.x per-user, no-admin install story; users without administrator rights lose nothing but never gain machine settings.
7. Admission criteria are policy, not code; reviewer discipline is the control for protection-downgrade and policy-conflict rules (only the deny-list and path rules are mechanically checked). With one maintainer the tier is closed (7.5).
8. The VmTrust configuration is a second build flavor that must never reach a release; the guard is a pipeline scan plus a unit test, which can themselves be skipped by a careless change.
9. The SignPath Foundation can pause or revoke (4.8).
10. A COSE package dependency enters the elevated image (6.1); NativeAOT compatibility is unproven until S0.

## 11. Machine-row state machine and crash recovery

This section replaces the sequence and recovery prose of protocol.md 12.2 and 12.7 (those sections point here; R-09..R-14, m4-review 4.2). Row states are the existing ones (`Planned, Prepared, Executing, Applied, AlreadyConfigured, Failed, Partial, PendingRestart, Reverted`) plus the existing compensation-prepared marker and one new durable flag `compensation_committed`. operations.md must gain the machine-row rules below (E-07, owner: operations.md).

### 11.1 Invariants

1. One active row. At most one setting row of any kind (user or machine scope) is active (`Prepared`, `Executing`, compensation prepared or committed) at any time. While a machine row is `Executing`, user-scope operations are also blocked by the existing global rule (`operation.recovery_required`).
2. Rows are created one at a time. The first operation's `Prepared` row is committed before the worker is launched (UAC may then be pending); each later operation's row is committed after the previous row is terminal and before that operation's `Prepare` message. Operations never started in a batch get NO row; the UI reports them from the batch result with `elevation.batch_aborted` (display-only, never persisted).
3. Before-state is written once, at `Prepared`, from the client's own unelevated read. The worker's observed before-state (step 5) is stored as `worker_before_digest` in a new column and must equal the Prepared before-state digest; inequality aborts the batch (`plan.stale`, row `Failed`) and the before-state is not rewritten.
4. `Executing` (or `compensation_committed = 1` for a revert) is committed with FULL sync strictly before the `Commit` message is sent.
5. `Applied` is recorded only after the client's own unelevated read-back, whatever the worker claimed.
6. The worker is told nothing about the journal; no decision uses wall-clock time except the user-initiated review action in 11.5.
7. Machine scope performs no shell refresh (7.1 item 11); no `PendingRestart` is produced by a refresh, only by a definition that declares `requiresRestart`.

### 11.2 Forward path

```
1  client: plan (unelevated read of HKLM state, definition, choice, policy check, token)       -- no mutation
2  client: row Planned -> Prepared (intent: definition id+version, choice id, set serial+hash, batch digest, scope=machine,
           planTokenDigest, before snapshot, owner pid+start)                                  -- B1, committed FULL
3  client: launch + attestation + handshake (once per batch)                                   -- B2, B3; failure => row Failed (elevation.*)
4  client -> worker Prepare; worker reads under its token; replies Prepared (observed snapshot, revision, willChange)   -- B4, B5
   willChange = 0 : row Prepared -> AlreadyConfigured, no Commit; next operation continues
   observed not in restoreDomain : Refused (setting.state_unrecognized), row Failed
5  client: compare worker_before_digest with the row; store it; row Prepared -> Executing      -- B6, committed FULL
6  client -> worker Commit(preparedDigest)                                                      -- B7
7  worker: re-read, compare-and-set (expected = prepared snapshot), RegFlushKey on the target key, re-read, reply Outcome   -- B8
8  client: independent unelevated re-read; classify; row -> terminal state                      -- B9 (Outcome received), B10 (committed)
```

Outcome status mapping (client-side, from its own read-back; a worker `code` is accepted only if it is in the allow-list for that message and status, otherwise the session aborts with `elevation.channel_failed`):

| Worker status | Row state and code |
|---|---|
| Applied (1) | `Applied`, or `PendingRestart`/`operation.restart_pending` if the definition requires a restart |
| AlreadyConfigured (2) | `AlreadyConfigured` |
| Failed (3) | `Failed` with the worker's allow-listed code |
| Conflict (4) (compare mismatch before the write; nothing written) | `Failed`/`operation.external_conflict` |
| Refused (5) | `Failed` with the refusal code |
| Partial (6) (written, verification read differs) | `Partial`/`operation.verification_failed` |

Cancel at the consent stage (the user dismisses the UAC prompt or the pre-launch dialog): row `Prepared -> Failed`/`elevation.declined`.

### 11.3 Revert path

Preconditions checked by the CLIENT before any launch: the row's definition id exists in the current set and its version is the current version or listed in `restoreCompatible` (else `operation.not_reversible`); live state equals the recorded after-state AND the key revision equals the recorded revision (else `operation.external_conflict`, or `operation.superseded` if a newer Winora operation changed the setting; no UAC raised). The restore value comes from the definition's restore domain, is shown exactly in the consent dialog with its origin ("recorded when this change was applied"), and the plan token for a restore is SHA-256("winora/plan/v1/restore" || definitionId || version || canonical recorded after-state || keyRevision) (protocol.md Appendix A defines the bytes).

```
C1  row (Applied | PendingRestart) -> compensation prepared (intent committed, restore value digest recorded)
C2  launch, handshake, Prepare(mode Restore); worker compares live to the recorded after-state; mismatch => Conflict => operation.external_conflict (not plan.stale)
C3  client sets compensation_committed = 1, committed FULL, then sends Commit
C4  worker writes (flushes), replies Outcome; client re-reads; row -> Reverted | Partial
```

A forward-only definition upgrade (version bump without `restoreCompatible`) leaves its applied rows permanently unrevertable; this is deliberate and visible to the user.

### 11.4 Recovery per durable boundary

Recovery is read-only: it reads live HKLM state through the unelevated reader (definitions are admitted only if readable without elevation, 7.1 item 5), never launches the worker, never signals a process and never replays a journaled target. Rows whose owner process (pid + start time) is alive are left alone (ADR-007). Verdict predicate (same value-plus-revision rule as ADR-007): "unchanged" = live equals the recorded before-state; "ours" = live equals the desired value AND the key revision differs from the recorded before-revision; live equal to desired with an unmoved revision cannot be attributed and is `Partial`/`operation.external_conflict`.

Worker-gone gate for `Executing` rows (forward B6..B9) and for rows with `compensation_committed = 1` (revert C3..C4): no verdict is committed while a worker that might write is alive. The worker is provably gone when `<InstallRoot>\run\worker.lock` can be opened with a read-sharing test open that does NOT fail with a sharing violation (or does not exist), or when the machine has booted since the row's `Executing` commit (boot-session identifier recorded in the row). A sharing violation means a worker is alive and recovery defers (no change to the row, code `operation.recovery_required` shown with the worker-busy reason). The 5 minute worker lifetime cap bounds the wait; the lock probe is `[U]` for a standard user reading `run\`. Experiment XI-8 (part of V-16): from a standard-user process open `run\worker.lock` with full read-sharing while a VmTrust worker holds it (expect a sharing violation), after a normal exit and after the worker is killed (expect not found or a clean open); also record the result under Administrator protection. If the probe cannot be performed (access denied), recovery treats the worker as possibly alive and defers.

| Boundary | Row state on disk | Meaning | Recovery verdict |
|---|---|---|---|
| B0 before step 2 | none | nothing | none |
| B1 step 2 committed | Prepared | UAC may be pending | unchanged: `Failed`/`operation.reconciled_unchanged`; live = desired: `Partial`/`operation.reconciled_partial` (the mutation never started); else `Partial`/`operation.external_conflict` |
| B2 launched, no attestation | Prepared | worker may start later with the client dead; it exits when its client is gone | as B1; no write is possible because no Commit exists |
| B3 handshake done | Prepared | read-only so far | as B1 |
| B4 Prepare sent | Prepared | worker read only | as B1 |
| B5 Prepared received, before step 5 | Prepared | no Commit possible | as B1 (an `AlreadyConfigured` result not yet journaled resolves as unchanged-equals-desired: `AlreadyConfigured`) |
| B6 step 5 committed | Executing | Commit may be sent | after the worker-gone gate: unchanged: `Failed`/`operation.reconciled_unchanged`; ours: `Applied`/`operation.reconciled_applied` (`PendingRestart` if `requiresRestart`); else `Partial`/`operation.reconciled_partial` |
| B7 Commit sent | Executing | worker may write | as B6, after the worker is gone |
| B8 worker wrote, no Outcome | Executing | | as B6 |
| B9 Outcome received, not journaled | Executing | | as B6 |
| B10 step 8 committed | Applied / Failed / Partial / PendingRestart / AlreadyConfigured | terminal | startup drift check only (11.6) |
| C1 compensation prepared | Applied or PendingRestart + compensation prepared | revert intent, no Commit possible | live = recorded after: back to `Applied`/`operation.rollback_abandoned`; else `Partial`/`operation.external_conflict` |
| C2 revert launched / Prepare sent | same | read-only | as C1 |
| C3 `compensation_committed` = 1 | same + flag | worker may write | after the worker-gone gate: live = recorded after: `Applied`/`operation.rollback_abandoned`; live = restore value and revision moved: `Reverted`/`operation.reverted`; else `Partial`/`operation.external_conflict` |
| C4 Commit sent / worker wrote / Outcome not journaled | same + flag | | as C3 |
| C5 outcome committed | Reverted / Partial | terminal | startup drift check only |

### 11.5 When the worker or the install is unavailable

Recovery never needs the worker. It needs only the unelevated reader and the journal, so these cases behave as follows:

1. Worker binary missing, untrusted, blocked by Smart App Control or Defender, UAC disabled, or the per-machine install removed: recovery of `Prepared` and `Executing` rows proceeds exactly as above (the worker-gone gate treats a missing `run\` or lock file as gone). Only Apply and Revert are unavailable, with the gate's typed code.
2. Definition set unavailable or different (older or newer build, per-user build): rows whose definition id/version is unknown or whose fingerprint differs are quarantined (`journal.untrusted_definition`), which lifts the global block without guessing; nothing is written.
3. Frozen per-user build opens a v4 journal: refused with `journal.schema_unsupported`; the machine rows can only be examined by a per-machine build.
4. Target unreadable by the standard user (an ACL change after the definition was approved): the row stays open with `operation.recovery_required`. Because the global rule would block every other setting indefinitely, the user may choose "Mark as reviewed" once the worker-gone gate holds or 10 minutes have passed since the row's last write (a user-initiated action, the only use of wall-clock time). It journals the acknowledgment, moves the row to `Partial` with `operation.recovery_required` acknowledged, performs no registry action and lifts the block.
5. Lock probe inconclusive: defer; the UI shows the row as waiting for a possibly running elevated session and offers the same review action after the 10 minutes.

### 11.6 Drift check, durability and multi-user behavior

- The worker calls `RegFlushKey` on the target key before sending `Outcome` (`[U]`: whether a hard power loss loses an unflushed write, X8). The client records its own post-write key revision from its step-8 read; `Outcome` carries a revision for diagnostics only.
- At startup the client runs a read-only drift check on machine rows in `Applied`/`PendingRestart`: live not equal to the recorded after-state marks the row `Partial`/`operation.external_conflict`; nothing is written. (Whether M2's HKCU path gets the same discipline is D-19, a separate task.)
- Machine state is global but journals are per user (R-27). An elevated host keeps that per-user journal in the admin-only folder, `%ProgramData%\Winora\journals\operations-<SID>.db`, so a standard user cannot forge a row that the elevated app later acts on; a client that is not elevated keeps `%LOCALAPPDATA%\Winora\simulation.db` (see [security.md](security.md)). After user A changes a machine setting, user B's Winora has no row: it shows "changed by another account" from a live read only, and a revert there is refused with `operation.revision_unknown`. `worker.lock` is machine-wide, so a second session sees `worker.busy` with the holder's age. The Event Log audit (section 12) is the machine-level record; there is no HKLM audit key (D-15). ARPCOMMENTS and release-limits state that machine changes persist after uninstall.

## 12. Audit trail (Event Log; D-15)

### 12.1 Decision and limits

DECIDED (D-15): audit to the Windows Event Log through a dedicated source named `Winora` in the Application log, not to an HKLM audit record (an HKLM record adds an unneeded machine-writable store). Both the elevated worker and the unelevated client write; events are best effort: a failed write never blocks, changes or hides an outcome, but it is counted and logged by the client. Events are never read back by Winora and never act as authority. Limits stated plainly: any local process that can write the Application log can forge entries with this source name, so the log is an audit trail for collection by enterprises (it is what they collect), not a tamper-evident store; ranges of event ids distinguish writer role but cannot authenticate it (`[U]` writer permissions: XE-2).

### 12.2 Event table

Worker events use ids 2000-2099, client events 1000-1099. Event type is Information unless stated. Fields are positional insertion strings in the order shown.

| Id | Writer | Meaning | Fields |
|---|---|---|---|
| 1001 | client | Elevation requested | batchSize, setSerial, batchDigest8 |
| 1002 | client | Elevation unavailable (Warning) | gateCode |
| 1003 | client | Prompt declined | code |
| 1004 | client | Elevation failed (Error) | code, detail |
| 1005 | client | Operation outcome (Information or Warning) | operationId, definitionId, definitionVersion, choiceId, mode, state, code |
| 1006 | client | Recovery verdict | operationId, state, code |
| 1007 | client | Recovery deferred (Warning) | operationId, reasonToken |
| 2001 | worker | Worker started | build, flavor, setSerial, setHash8 |
| 2002 | worker | Client accepted | callerSidHash, sessionId |
| 2003 | worker | Client rejected (Warning) | reasonToken |
| 2004 | worker | Operation prepared | operationId, definitionId, definitionVersion, mode |
| 2005 | worker | Operation committed | operationId, status, code |
| 2006 | worker | Worker refused to run (Error or Critical) | reasonToken, code |
| 2007 | worker | Floor advanced | oldSerial, newSerial |
| 2008 | worker | Worker exited | exitToken |

M4a writes only the worker and client events that the typed no-op exercises (1001-1004, 2001-2003, 2006, 2008), proving source registration, writer permissions and redaction before any real definition exists.

### 12.3 Redaction and registration

- Allowed values: operation ids (GUID), definition ids and choice ids (grammar `[a-z0-9._]`), integers, 8-hex digests, stable catalog codes and closed reason tokens. `callerSidHash` = first 16 hex characters of SHA-256("winora/audit/sid/v1" 0x00 || SID string); it is a pseudonym, linkable across events and guessable for local accounts, not a privacy guarantee. Never written: paths (including the install root), registry values, snapshot or restore bytes, anything the user typed, nonces, keys, the transcript hash `T`, receipts, SIDs in clear, machine or user names.
- The client's existing log redactor still applies to its own file log; the Event Log insertion strings pass the field grammar above first, so injection through ids is impossible.
- Registration: MSI registry rows only (3.1). If registration is absent (portable, per-user or unsigned runs) events fall back to the client file log and the worker, if running, writes nothing; the absence is not an error.
- Experiments: XE-1 registry-only source registration displays event text correctly with the chosen message file (Microsoft's EventLogMessages.dll or an own message-table resource; to be settled); XE-2 an unelevated user process can write the source's events, a Low-integrity process cannot (or can; record); XE-3 worker events appear with the expected event ids after a VmTrust no-op batch, and uninstall leaves existing entries.

## 13. Platform stance, enterprise readiness and exposure policy

### 13.1 Platform stance (D-13)

Windows 11 24H2 (build 26100) and later is the primary target; builds 22621 and 22631 are tested; machine-scope definitions require `minBuild` >= 22621. The MSI launch condition (22000+) is unchanged until compatibility.md is updated by its owner. Windows 10 and ARM64 stay unsupported. Administrator protection (off by default in 2026) makes the elevated token come from a hidden, profile-separated account, so the worker may not be the caller's account; this reinforces "no HKCU in the worker" and is "unverified, typed code" until X13 settles it on a VM. Smart App Control blocks unknown unsigned code in enforcement mode: unsigned builds are expected to be blocked (`elevation.launch_failed`, detail `blocked`); signed builds are supported.

### 13.2 Enterprise readiness order (D-21)

(1) per-machine MSI with silent switches and documented exit codes, (2) Event Log integration and a documented log location (section 12), (3) ADMX/ADML policy set (disable categories or the elevated worker; control update notification), (4) idempotent desired-state profiles, (5) winget manifest, (6) Intune deployment guide, (7) accessibility conformance report, (8) CRA/NIS2 review of obligations when distributed in the EU (scope for non-commercial open source is contested; PLAUSIBLE). Each step stays "not claimed" in release-limits.md until evidence exists. An administrator can refuse elevation by not installing the per-machine package, and (when item 3 exists) by policy.

### 13.3 Public repository exposure (D-22)

`an unpublished design note`, `an unpublished design note` and `technical-docs/research/` stay out of the public export until the design is corrected, because they describe unfixed weaknesses and unverified claims. This file, protocol.md and the ADRs reference them; the public export tool must either drop those references or ship the files after the corrections land.

### 13.4 Gate before the first real machine-scope definition ships (D-14)

All of the following, none of which M4a satisfies:
1. External security review of the channel (cryptographic review of the handshake per D-04; fallback to SSPI Negotiate sign/seal if rejected).
2. All VM experiments passed or their fallbacks adopted (X1-X20 and XI-/XE- items), on at least two Windows 11 builds including V-09, V-14, V-16 at every boundary, V-21/V-22, V-26.
3. Definition-signature layer implemented and operating (6.1), key custody as documented, and the second reviewer enabled and evidenced (7.5, D-17).
4. The per-definition checklist of 7.4 satisfied for the definition, including `managedBy`, `restoreCompatible` and runtime reversibility.
5. Policy awareness implemented: relevant policy keys read, "managed by policy" reported instead of overriding, OS-protected values unsupported.
6. Real signing route in use by protected CI, pins known (Q-S1..Q-S4), timestamps verified; reproducibility statement accurate.
7. Update and revocation story restated in release-limits (4.7).
8. Event Log audit working (section 12); multi-user behavior documented and tested (11.6).
9. Consent UX reviewed by screenshot on the secure desktop (V-15), accessibility and Persian review.
10. Journal recovery proven under fault injection at every boundary of 11.4; drift check shipped; flush behavior measured (X8).
11. Dev-host isolation green: no script, test or CI step executes the worker image; no test-only input reaches a machine or elevated path; `verify_dist.ps1` and CI scans clean for the VmTrust literal.

## 14. Self-consistency record

Codes this file uses (protocol.md section 17 and errors.md must contain each; new ones are marked): `elevation.unavailable`, `elevation.unsigned_build`, `elevation.install_untrusted`, `elevation.uac_disabled`, `elevation.client_elevated`, `elevation.declined`, `elevation.launch_failed` (detail `blocked`), `elevation.timeout`, `elevation.server_untrusted`, `elevation.client_untrusted`, `elevation.channel_failed`, `elevation.batch_aborted`, `worker.busy`, `worker.unimplemented`, `definition.unknown`, `definition.revoked`, `definition.signature_invalid`, `definition.rollback`, `definition.set_mismatch`, `trust.store_insecure`, `plan.stale`, `setting.state_unrecognized`, `setting.managed_by_policy` (NEW, errors.md), `journal.untrusted_definition`, `journal.schema_unsupported`, `operation.external_conflict`, `operation.superseded`, `operation.not_reversible`, `operation.revision_unknown`, `operation.verification_failed`, `operation.recovery_required`, `operation.restart_pending`, `operation.rollback_abandoned`, `operation.reverted`, `operation.reconciled_applied`, `operation.reconciled_unchanged`, `operation.reconciled_partial`, `access.denied`.

Terms and numbers this file depends on in protocol.md (the other author must satisfy or tell us): gates G1..G9 (section 3); image guard 4.3, 4.3.2, 4.3.4; client peer identity 4.2, 7.2; flavor byte in the transcript 8.2; Appendix A (encodings of snapshots, `MachineValue`, batch digest, restore plan token, Authenticode flags); message set including Abort(`worker.unimplemented`); worker-gone lock semantics of `worker.lock`; section 16 test ids U-F/H/S/C/I/P/W/G/A/L and V-xx; section 17 code table; M4a no-op command `winora.selftest.noop` and the H_d value for the empty set.
