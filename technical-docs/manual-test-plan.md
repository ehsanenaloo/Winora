# Manual test plan (disposable Windows VMs)

Status: **written, not yet executed.** Nothing in this plan has been run by an agent or a person. Its purpose is to turn the claims that today rest on fakes, offscreen renders and source reading into evidence, and to produce the observations needed to correct the settings catalog flags. Results belong in a copy of the results tables below, then in [verification.md](verification.md).

## 0. Ground rules

1. **Disposable VMs only.** Run every step in a virtual machine that can be reverted to a snapshot and that holds nothing you care about. Never run this plan on a developer machine or a daily-use PC: the product really changes Windows user settings, restarts Explorer and can sign the user out.
2. **Test the shipped artifacts**, not a developer build: the exact `Winora-<version>-win-x64-portable.zip` and `.msi` whose SHA-256 values are in `SHA256SUMS` (section 2). Record the hashes in the report.
3. **Unsigned builds.** SmartScreen and Smart App Control behavior is part of the test (section 3); do not disable protections to make a step pass without writing down that you did.
4. **One tester, one variable.** Change one thing at a time and record the observation before the next step. Use the evidence helper a development tool that is not published (read-only; copy it into the VM) before and after every apply, revert and external change. It prints the build, Explorer process ids and sessions, and the raw value, kind and key last-write time of all 14 catalog settings.
5. **Evidence**: for every failed or surprising step keep a screenshot, the probe output before and after, `winora settings history --json`, and a diagnostics export (section 13). Name files `<vm>-<section>-<step>`.
6. A step passes only if the stated observation is seen. If it cannot be performed as written, mark it BLOCKED with the reason; do not improvise a substitute and call it passed.
7. Safety: never type real credentials or personal data; use a fresh local test account.

## 1. Environment matrix and setup

| VM | Windows 11/10 build | Purpose | Expected |
| --- | --- | --- | --- |
| A | Windows 11 22H2, build 22621 | Minimum for open_to, compact_view, search_mode | supported |
| B | Windows 11 23H2, build 22631 | Last build for search.web_results | supported |
| C | Windows 11 24H2, build 26100 | Current; web_results must be refused | supported; search.web_results shows unsupported |
| D | Windows 10 22H2, build 19045 | Expected-unsupported check only (section 3.7) | installer refuses; nothing else is exercised |

Record for each: edition, `winver` output, patch level (UBR), language, VM host and hardware (vCPU, RAM, graphics), and whether the VM is joined to a domain or MDM managed (a managed machine may have policies that override the settings; note them, do not remove them).

Prepare each of A, B, C once, then snapshot at each stage so any section can restart from a known state:

1. Install Windows from clean media, create two **local** accounts: `wuser` (standard user, the main tester) and `wadmin` (administrator, used only for installing prerequisites and for external-change steps that need `reg.exe`). Take snapshot **S0-clean**.
2. As `wadmin`, install the .NET 10 Desktop Runtime (x64) from Microsoft (record the exact version) and nothing else. Take **S1-runtime**. (A separate snapshot **S1b-no-runtime** is taken before this step for the missing-runtime test.)
3. Copy the artifacts, `SHA256SUMS` and a development tool that is not published into the VM through a read-only share or ISO (no internet needed after the runtime is in).
4. Sign in as `wuser`. Create the test fixtures used by the setting tests in `C:\WinoraTest` (this folder is only for the test): `visible.txt`, `hidden.txt` with `attrib +h C:\WinoraTest\hidden.txt`, and a sub-folder `many` with 40 small files (to see row spacing in a list).
5. Display/accessibility variants (used in section 10; set them in Settings, then revert to the snapshot): 100 %, 150 % and 200 % scale; a second virtual monitor with a different scale if the hypervisor supports it; high contrast themes (Aquatic, Desert, Dusk, Night sky) and the contrast-themes shortcut `Alt+Left Shift+Print Screen`; Narrator (`Win+Ctrl+Enter`); Persian: add the Persian (Iran) language pack and keyboard as `wuser`'s preferred language. Windows may need to be signed out and in again after adding a language.
6. Snapshot **S2-ready** (signed in as `wuser`, runtime installed, artifacts and helper present, Winora **not** installed).

Run the sections below in order on A, then repeat the per-setting section (5), the Explorer section (6) and the sign-out section (7) on B and C. Sections 3, 4 and 8 to 14 need only one of the supported VMs unless a step says otherwise; use C for them by default and repeat any failing step on A.

## 2. Intake and artifact verification

1. In the VM as `wuser`, in PowerShell: `Get-FileHash <each artifact> -Algorithm SHA256` and compare with `SHA256SUMS`. Record hashes and the result. A mismatch stops the run.
2. `Get-AuthenticodeSignature` on the MSI and on `desktop\Winora.Desktop.exe` after extracting the zip: both must report `NotSigned` (this is the 0.x state). Record anything else as a finding.
3. Open `FILES.sha256` from the extracted zip and spot-check five files with `Get-FileHash`.
4. Read `RELEASE-LIMITS.md` from the zip: it must match what you are about to see (14 settings, no elevation, unsigned, Windows 11 x64 only). Note any sentence the product contradicts.

## 3. Install, upgrade, uninstall

Always start each case from **S2-ready** (or the snapshot named). Use `wuser`, a standard account, unless stated.

### 3.1 Portable zip
1. Extract the zip to `C:\Users\wuser\Downloads\Winora-portable`. Observe whether Windows marks it from the web (Properties > Unblock); record.
2. Run `Check-Prerequisites.ps1` (right-click > Run with PowerShell). Expect OK lines for x64, build, .NET runtimes. Record execution-policy behavior.
3. Start `desktop\Winora.Desktop.exe`. Record the SmartScreen dialog text, and that no UAC prompt appears. The first-run welcome screen should appear (section 4). Close the app.
4. Start `cli\Winora.Cli.exe --version` in a console: expect `Winora <version>`. Run `cli\Winora.Cli.exe --help`.
5. Inspect `worker\Winora.Worker.exe` as a file; do NOT start it (no double-click and no launch from any console, by any host program): this plan never executes the worker. Check in Explorer Properties > Details that the file version equals the release version, and that Properties > Digital Signatures is absent (unsigned). `tools/verify_dist.ps1` reads the PE header (x64, console subsystem) and the embedded manifest (`requestedExecutionLevel` is `asInvoker`) without running it. Elevation and handler behavior are tested only in the VM kit (`tests/vm`), which refuses to run without `WINORA_VM_TESTS=1` and the VM marker file.
6. Delete the folder. Confirm `%LOCALAPPDATA%\Winora` still exists (data is kept) and nothing else was left behind except that folder.

### 3.2 Missing runtime (snapshot S1b-no-runtime)
Copy the zip in, run `desktop\Winora.Desktop.exe` and `cli\Winora.Cli.exe --version`. Expect Microsoft's message about the missing .NET runtime with a download offer, and no crash dialog from Winora. Record exact text. Run `Check-Prerequisites.ps1`: must report FAIL for the missing runtime.

### 3.3 MSI install (per-user)
1. Double-click the MSI as `wuser`. Record the SmartScreen/Smart App Control behavior and whether a UAC prompt appears (it must not). Install.
2. Expected afterwards: files under `%LOCALAPPDATA%\Programs\Winora` (`desktop`, `cli`, `worker`, documents); Start menu entry "Winora" (starts the Desktop); an entry in Settings > Apps > Installed apps for `wuser`; no new `Program Files` content; no new services (`Get-Service` before/after); no new scheduled tasks; `Winora.Cli.exe` is **not** on PATH.
3. Use the helper to confirm none of the 14 settings changed by installing.
4. As `wadmin` (another session, or after switching user): the app is **not** installed for that account (per-user). Record.
5. Registry: `reg query HKCU\Software\Winora` as `wuser` shows only the installer bookkeeping value (`Installer\StartMenuShortcut`); nothing under HKLM\Software\Winora (`reg query HKLM\Software\Winora` fails).
6. Install with logging once, `msiexec /i <msi> /l*v C:\WinoraTest\install.log` (this is the human tester's action in the VM), and read the log for warnings and for the Windows Installer property `ALLUSERS`/`MSIINSTALLPERUSER`.

### 3.4 Upgrade
Build a second package with a higher version on the build machine (copy the source tree, set `<Version>` in `Directory.Build.props` to the next patch (the CLI reads its version from the assembly), run `tools/publish.ps1`). In the VM:
1. Install version N, run the app, apply one setting (section 5 protocol) so a journal and a changed setting exist.
2. Install version N+1 by double-clicking its MSI. Expect a major upgrade: N is replaced, one Apps entry, shortcut still works, `%LOCALAPPDATA%\Winora` untouched, History still shows the earlier change and it can still be reverted.
3. Install version N while N+1 is installed: expect the "newer version is already installed" refusal and no change.
4. Install N+1 again while it is installed (same version): record behavior (maintenance/repair dialog or nothing).
5. Upgrade while Winora is **running**: record whether Windows Installer offers to close it, closes it, or asks for a reboot; the result must not leave a half-installed state. Run the app afterwards.
6. Repair (`msiexec /fa` or Apps > Modify/Repair if offered): files restored, nothing else changes.

### 3.5 Uninstall
1. Uninstall from Settings > Apps. Expect the folder `%LOCALAPPDATA%\Programs\Winora`, the Start menu entry and the Apps entry gone; `%LOCALAPPDATA%\Winora` **kept**; `HKCU\Software\Winora` gone.
2. Using the helper, confirm every Windows setting Winora had changed **still has the changed value** (uninstall never reverts settings). Confirm the documentation says exactly that.
3. Reinstall: History from before is visible; the earlier change can be reverted (if the value is still unchanged).
4. Uninstall while the app is running: record the behavior.
5. Manual data removal following release-limits.md (close Winora, delete `%LOCALAPPDATA%\Winora`): next start behaves as a first run.
6. Revert-first order: apply a setting, revert it from History, uninstall: the setting is back to the original value and the data folder is still there.

### 3.6 Other
- Group Policy or Smart App Control on (if the VM has it): record what happens and that Winora does not try to bypass it.
- Install on a path with a long user name, a space, and a non-ASCII character (create such a local account on VM C) and run section 5 for one setting.
- Disk full and permission-denied on `%LOCALAPPDATA%\Winora` (deny write to the folder for `wuser`): the app must show a clear error and change nothing.

### 3.7 Windows 10 22H2 (VM D): expected-unsupported check
1. Install attempt of the MSI: expect a refusal with "Winora supports 64-bit Windows 11 (build 22000 or later) only." Record the exact dialog.
2. Run `Check-Prerequisites.ps1` from the zip: must FAIL on the build line.
3. Optional, only to document behavior: with the runtime installed, start the portable Desktop and CLI; run `cli\Winora.Cli.exe settings list`. Expect all settings reported as unsupported on this build or a clean error, and no write possible. Any crash is a finding. This does not make Windows 10 supported.

## 4. First run, language, theme, preferences

1. First start (empty `%LOCALAPPDATA%\Winora`): welcome screen shows the points that nothing is sent anywhere, changes are reviewed and revertible, and where data is kept. "Get started" closes it; Settings has "Show the welcome screen again".
2. Switch language English <-> Persian from Settings: applies immediately, layout mirrors in Persian; restart the app: the choice persists.
3. Theme: System, Light, Dark; with Windows in dark and light mode (after changing it through Winora's own appearance settings and through Windows Settings); the app follows System.
4. Kill the app during first run, restart: no corrupted preferences, welcome appears again or not as designed; no error dialog.
5. Run the app as `wadmin` (elevated and non-elevated): record that it never asks for elevation and that settings it changes belong to the signed-in user's profile only. If "Run as administrator" is chosen on the exe, note that settings then apply to the elevated token's user profile: record whose HKCU changed (use the helper in both accounts). This is the alternate-credentials case from technical-docs/security.md.

## 5. The 14 settings

### 5.1 Protocol (repeat for each setting, each VM)

Preconditions for every setting:
- Start from a snapshot (S2 plus Winora installed via the MSI is the usual start; call it **S3-installed**). Sign in as `wuser`.
- Run the helper; record the **initial** state of the setting: present or absent, raw value, kind, key last-write time. "Absent" is normal for several settings; Winora lists no choice for Absent, so a profile may show "no matching choice" until something is applied (technical-docs/settings-catalog.md). Record what the Customize card shows for the initial state.
- Open the windows/apps named in "Where to look" before applying, so you can tell whether already-open windows change.

Apply (Desktop):
1. Customize > the setting's card > choose the other choice > review dialog: check the before/after values, the registry location, the risk, and the Explorer-restart option/warning text. Do **not** tick "Restart Explorer" in the first pass (pass A). Confirm.
2. Record immediately: what Winora's status text says (Applied, Written but not in effect, Pending restart, Sign-out needed), the badge (Explorer restart needed / Sign-out needed / No restart), and a screenshot.
3. Observe **where to look** (table below) and write down for each of these moments whether the effect is visible: (a) right after Apply in already-open windows; (b) after pressing F5 / reopening the window / opening a new window; (c) after **Winora's own** Explorer restart (pass B, use "Apply now (restart Explorer)" from History or the Explorer option in the review, after reverting to the initial value); (d) after manually ending and restarting `explorer.exe` in the VM; (e) after sign-out and sign-in.
4. Run the helper again: the value, kind and key last-write time must match the review's "after" value. The kind must be unchanged from the initial kind (DWORD stays DWORD; `WindowArrangementActive` stays a string).

Revert:
5. History > the operation > Revert > review: confirm it restores the initial value (or, if the value was absent, returns it to absent - helper shows "(absent)"). Record status text and whether the effect disappears at the same moments as in step 3.

External-change conflict (must refuse):
6. Apply again. Now change the same setting **outside Winora** using the method in the table ("External change"). Run the helper to prove the value changed. In Winora open History > Revert: it must **refuse** to overwrite, with the "Changed by something else" message; the Windows value must remain as the outside change set it. In the CLI the same revert must exit 3 with `operation.external_conflict`.
7. Same-value outside rewrite (do once per VM on `explorer.show_file_extensions`): after an apply, toggle the Explorer "File name extensions" option off and on again (the value ends the same as Winora wrote it, but the key was written). Revert must still refuse or must be explained; record exactly which. (The engine treats a changed key revision as an outside change.)

CLI parity (once per setting): `Winora.Cli.exe settings preview <id> <choice>` matches the Desktop review; `settings apply <id> <choice> --yes --no-restart-explorer --json` produces the same registry result; `settings revert <operation-id> --yes`; compare `outcome.state`, `needsExplorerRestart`, `needsSignOut`, `tookEffect` with what you saw in the Desktop.

Pass B (Explorer restart through Winora): for settings that did not appear without a restart in pass A, repeat the apply with Winora's Explorer restart. Record whether the setting then appears, how long the taskbar was gone, whether open Explorer windows closed, and whether the taskbar returned (section 6).

### 5.2 Per-setting expectations (to verify; every "expect" is a hypothesis, not a fact)

Choices use the catalog ids. "Builds" is the supported range in the catalog.

| # | Setting id (builds) | Target | Where to look | External change method | Hypothesis to confirm or refute |
| --- | --- | --- | --- | --- | --- |
| 1 | explorer.show_file_extensions (22000+) | HideFileExt: show=0, hide=1 | `C:\WinoraTest` open in File Explorer: file names show `.txt`; also the desktop and the Open dialog of Notepad | File Explorer > View > Show > File name extensions | Visible after F5 in open windows; maybe immediately after Winora's broadcast; no Explorer restart needed |
| 2 | explorer.show_hidden_files (22000+) | Hidden: shown=1, hidden=2 | `C:\WinoraTest` list: `hidden.txt` appears greyed or disappears | File Explorer > View > Show > Hidden items | Visible after F5; not in already-open Open/Save dialogs until reopened |
| 3 | explorer.open_to (22621+) | LaunchTo: home=2, this_pc=1 | Press `Win+E` for a new window; also the taskbar File Explorer button | File Explorer > ... > Options > "Open File Explorer to" | New windows only; open windows unchanged; no restart needed |
| 4 | explorer.compact_view (22621+) | UseCompactMode: comfortable=0, compact=1 | `C:\WinoraTest\many` in details view: row height | File Explorer > View > Compact view | Visible after F5 or new window; verify not immediately in open windows |
| 5 | taskbar.alignment (22000+) | TaskbarAl: center=1, left=0 | Taskbar icons move left/center | Settings > Personalization > Taskbar > Taskbar behaviors > Taskbar alignment | Probably immediate; record if Explorer restart or sign-out is needed |
| 6 | taskbar.clock_seconds (22000+) | ShowSecondsInSystemClock: hidden=0, shown=1 | Clock at the right end of the taskbar shows seconds | Settings > Personalization > Taskbar > Taskbar behaviors > Show seconds in system tray clock (if present) | Possibly needs Explorer restart on older builds; record per build; if the Settings toggle does not exist on a build, use `reg add` as in note 1 |
| 7 | taskbar.search_mode (22621+) | SearchboxTaskbarMode: hidden=0, icon=1, box=2 | Taskbar search control | Settings > Personalization > Taskbar > Search | Likely immediate; record edition/policy effects |
| 8 | taskbar.task_view_button (22000+) | ShowTaskViewButton: shown=1, hidden=0 | Task View icon next to Start | Settings > Personalization > Taskbar > Taskbar items > Task view | Likely immediate; record |
| 9 | privacy.advertising_id (22000+) | AdvertisingInfo\Enabled: enabled=1, disabled=0 | Settings > Privacy & security > General > "Let apps show me personalized ads by using my advertising ID" | The same Settings toggle | Settings app shows the new state when reopened; no restart. A device policy may override: record |
| 10 | privacy.tailored_experiences (22000+) | Privacy\TailoredExperiencesWithDiagnosticDataEnabled: enabled=1, disabled=0 | Settings > Privacy & security > Diagnostics & feedback > Tailored experiences | The same Settings toggle | As above; the catalog marks its default as unverified across editions: record the initial state and edition |
| 11 | search.web_results (22000-22631) | Search\BingSearchEnabled: enabled=1, disabled=0 | Open Start and type "weather": web suggestions/results visible or not | `reg add` (note 1) because Windows has no Settings toggle on every build | Catalog says sign-out needed; verify: immediate? after Explorer/Search restart? only after sign-out? On build 26100 Winora must show it as unsupported on this build and refuse to apply |
| 12 | appearance.apps_theme (22000+) | Themes\Personalize\AppsUseLightTheme: light=1, dark=0 | Open Settings, Notepad, File Explorer before applying: which switch to dark live | Settings > Personalization > Colors > Choose your mode | Many apps switch live after the settings-changed broadcast; list which do not |
| 13 | appearance.system_theme (22000+) | SystemUsesLightTheme: light=1, dark=0 | Taskbar and Start colors | Settings > Personalization > Colors > Choose your mode (Custom) | Probably immediate via broadcast; list exceptions |
| 14 | appearance.window_snapping (22000+) | Control Panel\Desktop\WindowArrangementActive (string "1"/"0") | Drag a window to the screen edge: snaps or not; hover over the Maximize button for snap layouts | Settings > System > Multitasking > Snap windows | Catalog says sign-out needed. Test right after Apply (still snaps?) and after sign-out/in |

Note 1: `reg add` is allowed only as the "external change" step, performed by the human tester in the VM: `reg add "HKCU\<key>" /v <Value> /t <REG_DWORD|REG_SZ> /d <data> /f` as `wuser` (it writes the user's own hive). Use the exact key, value and kind from the table and the helper's output, and keep the kind equal to the initial kind.

Special checks:
- **Absent initial value** (rows 3, 4, 5, 6, 8, 11 are often absent): after apply, the value is created with the catalog kind; after revert the value is deleted again (helper shows "(absent)") and the key itself is not removed.
- **Same value as default**: applying a choice that equals the current default while absent writes the value anyway and says so ("This value is not set now ... Winora will still write ..."). Record the review wording.
- **Unsupported build**: on VM C, `search.web_results` must be disabled or refused with a clear message, not written. On a build below 22621, `open_to`, `compact_view`, `search_mode` must behave the same way (no such VM in the matrix; mark NOT TESTED).
- **Policy override** (optional, domain or MDM VM): if a policy forces a value, Winora's apply may write successfully while the effect does not appear. Record what Winora says (it must not claim the effect).
- **Bulk apply**: select three settings (include one theme and one taskbar item) and apply together with one review: confirm they apply one after another, Explorer restarts at most once at the end, and that a forced mid-way failure (deny write permission on the key with Registry Editor on the test VM, then apply) stops at that setting, names it, leaves earlier ones applied and revertible from History.

### 5.3 Results table (copy one per VM; fill every cell, write n/a if not applicable)

Moments: **imm** = immediately after Apply in open windows; **F5** = after refresh/reopen/new window; **WE** = after Winora's own Explorer restart; **XE** = after manually restarting explorer.exe; **SO** = only after sign-out/in; **never** = not seen at all.

| Setting id | Initial state (present/absent, value) | Winora status text and badge after apply | First moment the effect was visible (imm/F5/WE/XE/SO/never) | Open windows/apps that did not update | Revert result and moment | External-change revert refused? (Y/N, exit code) | Catalog says now (restart / sign-out) | Observed RequiresExplorerRestart (Y/N) | Observed RequiresSignOut (Y/N) | Proposed catalog change | Evidence file names |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| explorer.show_file_extensions | | | | | | | no / no | | | | |
| explorer.show_hidden_files | | | | | | | no / no | | | | |
| explorer.open_to | | | | | | | no / no | | | | |
| explorer.compact_view | | | | | | | no / no | | | | |
| taskbar.alignment | | | | | | | no / no | | | | |
| taskbar.clock_seconds | | | | | | | no / no | | | | |
| taskbar.search_mode | | | | | | | no / no | | | | |
| taskbar.task_view_button | | | | | | | no / no | | | | |
| privacy.advertising_id | | | | | | | no / no | | | | |
| privacy.tailored_experiences | | | | | | | no / no | | | | |
| search.web_results | | | | | | | no / yes | | | | |
| appearance.apps_theme | | | | | | | no / no | | | | |
| appearance.system_theme | | | | | | | no / no | | | | |
| appearance.window_snapping | | | | | | | no / yes | | | | |

Rule for filling the observed columns: **RequiresExplorerRestart = Y** if the effect was visible on this build only at WE or XE (or the same setting is missing after F5 and appears after an Explorer restart); **RequiresSignOut = Y** if the effect was visible only at SO. If the effect is visible at imm or F5, both are N for that build. If builds disagree, keep one row per build and send the owner the disagreement; do not average. The catalog flags are then corrected from this table by the Windows adapter owner, never by the tester.

## 6. Explorer restart behavior (after a confirmed apply with the Explorer option, and `settings complete`)

Run on A, B and C. Use `taskbar.alignment` or `appearance.system_theme` as the changing setting (their effect is easy to see).

1. **Normal restart**: open three File Explorer windows and one Notepad with unsaved text. Apply with "Restart Explorer so this takes effect now". Observe and time: the warning text shown before; open File Explorer windows close (they should, per the warning); Notepad is untouched; the taskbar disappears and returns; tray icons come back; time until the taskbar is usable again. Winora must report success only after the taskbar returned. Record the Explorer process id before and after (helper): a new id in the same session.
2. **Taskbar did not return** (simulate carefully in the VM): before applying, rename nothing; instead end `explorer.exe` from Task Manager and **do not** start it, then Apply with restart. Expect `shell.restart_failed` ("The setting was changed, but Windows Explorer could not be restarted..."): the registry value is still applied, the History entry shows it as not in effect, "Apply now (restart Explorer)" is offered. Start Explorer manually (Task Manager > Run new task > explorer.exe) and confirm the History entry can be completed or reverted.
3. **Explorer already restarting / crash loop safeguard**: with a script in the VM that kills explorer.exe every 2 s (a loop the tester writes; stop it after one minute), start an apply with restart. Expect Winora to give up with a clear failure after its bounded retries (about 10 s to exit, 15 s to see the taskbar, one retry) and not to loop, spawn extra `explorer.exe` processes, or kill other programs. Count `explorer.exe` processes before and after.
4. **Multiple sessions**: with fast user switching, sign in `wadmin` in a second session (leave it signed in, switch back to `wuser`). Apply with restart as `wuser`. Expect: only `wuser`'s Explorer (same session, same user) is restarted; `wadmin`'s Explorer keeps its process id (helper in each session, or Task Manager Details with Session and User name columns). Repeat with an RDP session if the hypervisor allows.
5. **Graceful exit timeout** (last-resort kill path): hard to provoke; try with an Explorer that is hung (a shell extension that blocks, or suspend the process with a tool you control in the VM). Record whether Winora waited and whether it ended only that Explorer.
6. **Bulk**: apply three settings that each want an Explorer restart; Explorer restarts once, at the end; earlier ones show the "pending" state until then (record if History shows them as Applied afterwards).
7. **No restart choice**: apply the same setting without the Explorer option; History says written but not in effect; later "Apply now (restart Explorer)" (`settings complete <operation-id>` in the CLI) completes it and the status becomes Applied only after the taskbar returned.
8. After every case: helper output shows the registry state matches the journal; no leftover `explorer.exe` from another session was affected; Winora is responsive.

## 7. Sign-out button

1. Apply `appearance.window_snapping` (needs sign-out per catalog). Winora shows "Sign-out needed"; **it must not sign you out by itself**.
2. Click "Sign out now": a confirmation dialog appears ("Save your work first..."). Cancel: nothing happens. Click again, confirm: Windows signs out (programs may ask to save). Record whether unsaved Notepad text was protected by Windows' normal prompt.
3. Sign in again; open Winora: History shows the entry; record whether the pending state cleared and the snap behavior (drag a window) now matches the new value. Record on every VM.
4. Failure text: if the sign-out cannot start (hard to induce), the message "Windows did not start the sign-out. Nothing was changed." is expected; mark NOT TESTED if it cannot be induced.
5. Do the same with `search.web_results` on VM A or B (Start search before and after sign-in).

## 8. Crash, kill and power loss during apply

All in a VM, from snapshot S3-installed. After each experiment run the helper, `Winora.Cli.exe settings history --json` and `settings recover --json`, and start the Desktop and read History.

1. **Kill loop with the CLI** (journal and registry durability): in PowerShell as `wuser` run 40 iterations of: start `Winora.Cli.exe settings apply explorer.show_file_extensions hide --yes --no-refresh --json` (or the other choice, alternating) as a process, wait a random 5 to 250 ms, `Stop-Process -Force`. Then run `settings recover` and `settings history`. Expected: no operation is stuck in an active state after recovery; the registry value equals what the last recorded outcome says or the pre-test value; `apply` works again; a later revert either succeeds or refuses with a clear conflict, never writes a value the journal did not record.
2. **Kill the Desktop during apply and during revert**: with the "Apply" confirmation open, then with Explorer restart in progress, end `Winora.Desktop.exe` in Task Manager. Restart: History shows a consistent state and the next start runs recovery without a dialog storm.
3. **Hard power-off** (the hypervisor's "Power off", not shut down) right after clicking Apply (script the click with a short delay) and during the loop in step 1. After boot: the journal opens (no corruption message); recover and history as above. Repeat 5 times. This is the only test of the filesystem and SQLite durability claim (verification.md says power-loss durability is not claimed); a failure here is a finding, not a bug in the plan.
4. **Corrupt or locked journal**: while Winora is closed, truncate `simulation.db` to half its size in a copy and replace; start the Desktop: expected a clear error that names the folder and does not delete data. Open `simulation.db` with another process holding a lock (for example `sqlite3` shell is not required; any process with an open exclusive handle created by the tester) and apply: expected busy handling ("storage is busy"), no write to the registry.
5. **Another process writes the same value while Winora is between plan and apply** (race): open the review dialog, change the value outside Winora (section 5.1 step 6 method), then confirm the review. Expected: Winora refuses with a stale-plan/conflict message and writes nothing.

## 9. Journal migration from an older database

Goal: schema upgrade preserves history and does not lose or overwrite anything.
1. On the build machine, build the FND-001 foundation commit (v1 schema) and the M0 build (v2 schema) in separate scratch checkouts (build only). In a VM, run each older CLI once with `simulate apply` so `%LOCALAPPDATA%\Winora\simulation.db` is created by the old schema. Keep a copy of each database as `v1.db`, `v2.db`.
2. For each, install the current build, put the old database in place while Winora is closed, then start the Desktop and run `Winora.Cli.exe settings list` and `simulate history`.
3. Expected: the database is upgraded to the current schema without a prompt; the old simulation operations are listed (migrated v1 rows lose rollback by design: confirm the UI says so rather than offering a broken Revert); settings apply and revert work; a copy or backup is not required but note whether the old file is preserved; no crash. Opening the migrated database with the old CLI should fail closed with a clear "newer schema" error (do not repair it by hand).
4. Interrupt the migration: kill the process during the first start after placing the old file (repeat 10 times with random delays). Expected: next start completes or restarts the migration; no data loss.
5. Downgrade attempt: install an older build over the new one: refused by the MSI (section 3.4). With the portable older build run against the new database: clear refusal.

## 10. Language, right-to-left, theme, high contrast, DPI, Narrator

Run each checklist on VM C and repeat the high-contrast and 200 % items on A. Record screenshots for every failure. Do all of it for every page: Overview, Customize, Startup, Scheduled Tasks, Applications, Services, History, Settings, the review dialogs, the welcome screen, error bars and dialogs.

### 10.1 Persian (fa) and right-to-left
1. Settings > Language > Persian: the whole window mirrors immediately (navigation on the right, back/forward icons mirrored, scroll bars, tooltips, dialogs).
2. Every string is Persian except the technical tokens (setting ids, registry paths, codes, file names, `winora` commands). Note any English left on a page, and any key shown instead of text (a raw `Setting_..._Title`).
3. Mixed-direction: look at rows that contain a Latin name or path inside Persian (Startup and Applications rows, the details dialog, review dialog location line, `Code_*` messages). Digits, parentheses, colons and full stops must not jump to the wrong end. Record each garbled string with its key (from `technical-docs/persian-review.csv`, column `mixed_direction_risk`).
4. Dates, times and numbers are formatted for the Persian culture and are consistent between lists and details.
5. Truncation: no clipped buttons or labels at 100 %, 150 %, 200 %, and at the narrow window width (below 900 DIPs); Persian text is often longer.
6. Keyboard in Persian layout: Tab order follows the mirrored visual order; `Ctrl+F`, `Enter`, `Esc` work; access keys do not conflict.
7. CLI: `Winora.Cli.exe --help`, `settings list`, an error case and `discover startup` with a Persian console UI culture: text is Persian, tables are readable in a Persian-capable console font, JSON output is unchanged, ids and codes are not translated.
8. The Persian text is a machine draft: write down awkward phrases for the reviewer using technical-docs/persian-review.md, but do not fail the build for wording alone; fail it for meaning that is wrong or unsafe.

### 10.2 Theme
Light, dark and system follow; switch at runtime with dialogs open; text contrast is readable; icons visible; Mica/backdrop falls back sensibly with transparency effects turned off (Settings > Accessibility > Visual effects) and with battery saver.

### 10.3 High contrast
For each of the four high-contrast themes: every control, text and focus indicator is visible; no information is conveyed only by color (state badges still readable); disabled controls are distinguishable; the app uses system colors ("High contrast always uses system colors" is stated in the Settings help: verify); switching high contrast on and off while the app runs recovers without restart; review dialog and error dialogs included.

### 10.4 DPI and multi-monitor
At 100 %, 125 %, 150 %, 175 % and 200 % scale (sign out/in as required): no blurry text, no clipped controls, hit targets still at least 40 px at 100 %; list rows readable; dialogs centered on the correct monitor. Drag the window between monitors with different scales: it re-renders crisply (per-monitor DPI v2), no layout jump that hides controls. Window shorter than about 600 DIPs with the partial-results bar open (a known weak spot in verification.md): record usable list height.

### 10.5 Narrator (and NVDA if available)
Start Narrator before the app. For each page: the page title and landmark are announced; the navigation items announce name, position and selected state; every button, toggle, combobox and list item has a meaningful accessible name and help text (the code sets AutomationProperties; verify what is actually spoken); the order of reading matches the visual order, also in Persian; state changes are announced (Applied, pending restart, error InfoBar, progress ring); the review dialog announces its title and the before/after values; focus returns to a sensible control after the dialog closes; the error dialog's "Copy diagnostics" is reachable; nothing requires a mouse. Record anything not announced or announced as "unlabeled".

### 10.6 Keyboard-only walkthrough
Without the mouse: reach every page, search boxes (`Ctrl+F`), sort/filter controls, the details dialog (Enter on a row), each setting card, the review dialog buttons, History Revert, Settings controls. Focus is always visible and never trapped; `Esc` closes dialogs; `Alt` key menus are not required.

## 11. Discovery pages versus Windows (M1 claims)

Compare against Windows' own tools (read-only): Startup page vs Task Manager > Startup apps and `Win+R shell:startup`; Scheduled Tasks vs Task Scheduler (including hidden tasks); Services vs `services.msc` and `Get-Service`; Applications vs Settings > Installed apps and `Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*`. Record counts and the first differences (the product states MSIX apps and per-user service instances are not covered). Run once as a standard user and once on an account with administrator rights, non-elevated. A count that differs without an explanation printed by the app is a finding. Also: `Ctrl+C` during `discover all` in a real console exits with 130 and leaves no files behind (not exercised by tests). Press Refresh repeatedly and leave the page while scanning: no hang, no leftover scan.

## 12. CLI parity and exit codes

Run on VM C with `Winora.Cli.exe`: `--version`, `--help`, `discover <kind> [--json]`, `settings list|show|preview|apply|revert|complete|history|recover` (with `--json`), `simulate preview|apply|recover|history`, `diagnostics` (see section 13), and invalid arguments. Check: exit codes match technical-docs/settings-cli.md (0, 1, 2, 3, 4, 5, 6, 7, 130); a write without `--yes` and without a terminal refuses with 2 and does nothing; with a terminal it asks `[y/N]`; `--json` keeps stdout pure JSON with prompts on stderr; the schema key order is stable; the state after a CLI apply is visible in the Desktop History and vice versa; running Desktop and CLI at once on the same database does not corrupt it (apply from both at the same time: one wins, the other reports busy or conflict). Run the CLI as administrator from an elevated prompt: record whose HKCU is written (the elevated token's user), and that the product does not claim otherwise.

## 13. Diagnostics export content review

1. Create realistic activity first: a few applies, a revert, a refused revert, an error, discovery scans, and a name with spaces and non-ASCII characters in the user profile (VM C account).
2. Export (Settings > Export diagnostics, and the CLI command). Choose a folder outside `%LOCALAPPDATA%\Winora`; also try choosing the data folder itself and a network path: both must be refused with a clear message and nothing created.
3. Unzip into a fresh folder and read **every** file. The archive must contain the app/OS/.NET versions, schema version, error-code counts, recent redacted log lines and operation history codes and states. It must **not** contain: the user name, the machine name, user paths other than the application data folder name, registry values the user set, process names of the user's programs, domain names, IP addresses, e-mail addresses, or raw exception text. Search the extracted text for the test account name, the computer name (`$env:COMPUTERNAME`) and `C:\Users`. Record any hit with file and line.
4. Nothing leaves the machine: while the app runs and while exporting, list its connections (`Get-NetTCPConnection -OwningProcess <pid>`, `netstat -ano`) and watch the VM's network adapter or firewall log: no outbound connection from any Winora process at any time, including first run and discovery.
5. Size and time are reasonable; a second export does not overwrite the first silently; cancelling leaves nothing behind; a full disk fails cleanly.
6. Log rotation: run 300 CLI commands in a loop and verify `logs\` stays within its documented bound.

## 14. Performance and idle resource budget

These budgets are proposals made before any measurement; the test turns them into recorded numbers. A miss is a finding to discuss, not an automatic failure. Use a VM with 2 vCPU and 4 GB RAM, Windows idle for 5 minutes beforehand, three runs each, record medians.

| Measure | How | Proposed budget |
| --- | --- | --- |
| Cold start to interactive window | stopwatch from double-click (after a reboot, first launch) to Customize page rendered | under 5 s cold, under 2 s warm |
| Idle CPU | app open on Overview, untouched 5 min; `Get-Counter '\Process(Winora.Desktop)\% Processor Time'` | under 1 % average, no periodic spikes |
| Idle memory | working set and private bytes after 5 min idle | under 200 MB working set |
| Memory after use | visit every page 10 times, run each discovery scan 5 times, apply/revert 10 times | growth under 50 MB over idle; no continuous growth when repeated |
| Handles and threads | `Get-Process Winora.Desktop` Handles and Threads before and after the use pattern | no steady growth |
| Minimized/hidden | minimized for 5 min | CPU 0 %, no timers visible in Resource Monitor |
| Discovery scans | time of each scan page (Startup, Tasks, Services, Applications) and `discover all --json` | under 10 s each on the VM; the UI stays responsive during the scan |
| Apply latency | click Confirm to status shown, without Explorer restart | under 2 s |
| CLI | `Winora.Cli.exe --version`; `settings list` | under 1 s; under 2 s |
| Disk | size of `%LOCALAPPDATA%\Winora` after the use pattern; install size | data under 10 MB; install about 15 MB |
| Background | `Get-Process Winora*` after closing the app; no `Winora` service, scheduled task or Run entry created at any time | none |

## 15. Register of currently unverified claims this plan covers

Each row is a statement in the current documents that rests on fakes, source reading or nothing, and the section that checks it.

| ID | Claim or gap (source) | Section |
| --- | --- | --- |
| U-01 | Real effect of each of the 14 settings, and when it appears, is observed behavior only (verification.md "M2 adapter" limits; settings-catalog.md flags) | 5 |
| U-02 | The `RequiresExplorerRestart`/`RequiresSignOut` flags in the catalog are untested; the Explorer exit message is observed, not documented (compatibility.md, verification.md) | 5.3, 6 |
| U-03 | The Explorer restart and the taskbar-returns verification never ran against a real shell; open windows close and the taskbar returns (compatibility.md) | 6 |
| U-04 | Bulk applies restart Explorer once; earlier items stay pending (verification.md "M2 UI/CLI") | 5.2, 6.6 |
| U-05 | Sign-out via ExitWindowsEx(EWX_LOGOFF) behind a confirmation was never invoked (verification.md "M2 UI/CLI") | 7 |
| U-06 | The default Desktop composition (real HKCU, real shell, real sign-out) was never run by an agent (verification.md "M2 UI/CLI") | 5, 6, 7 |
| U-07 | Revert refuses after an outside change; same-value outside rewrite behavior; key last-write time as revision hint (engine docs, ADR-007) | 5.1 steps 6 and 7 |
| U-08 | Registry compare-and-set and ABA detection with a live writer (verification.md "M2 engine") | 5.1, 8.5 |
| U-09 | Power-loss and WAL durability of the journal are not claimed or tested (verification.md M2 engine, ADR-002/006) | 8.3 |
| U-10 | Crash/kill recovery was tested with child processes and fakes, not a real registry and Explorer (M0, M2 engine) | 8 |
| U-11 | Multi-user, fast user switching, alternate-credentials behavior (verification.md M2 engine; security.md) | 4.5, 6.4, 12 |
| U-12 | Unsupported builds fail closed; search.web_results ends at 22631 and is "verified only up to 23H2" (settings-catalog.md) | 5.2 rows 11, special checks |
| U-13 | Absent value equals shipped default, "no matching choice" display; tailored_experiences default unverified across editions; search_mode default varies (settings-catalog.md) | 5.1 preconditions |
| U-14 | A machine policy can override a user value; Winora does not read policy keys (compatibility.md) | 5.2 special checks |
| U-15 | Windows 10, ARM64, Server, domain-joined, locked-down and elevated hosts untested (compatibility.md, verification.md M1 adapters) | 3.7, 4.5, 12 |
| U-16 | Discovery counts were never compared with Windows tools (DISC-008); real COM/SCM readers only covered by one host smoke test (verification.md M1) | 11 |
| U-17 | Real Ctrl+C delivery to the CLI untested (verification.md M1 CLI) | 11 |
| U-18 | No Narrator/NVDA, keyboard-walkthrough, real high-contrast, 100/150/200 % DPI or multi-monitor checks (verification.md UI redesign, M1 UI, M2 UI/CLI) | 10 |
| U-19 | Mixed Latin/Persian text may reorder digits in right-to-left rows; Persian is a machine draft (verification.md, localization.md) | 10.1, persian-review.md |
| U-20 | Window shorter than about 600 DIPs leaves little list height; stale scroll offset when switching pages; fixed-width overview cards leave empty space (verification.md UI redesign, M1 UI) | 10.4 |
| U-21 | Desktop tests use fakes; real adapters were not driven through the UI (verification.md M1 UI) | 11 |
| U-22 | Diagnostics export and logging were not exercised against a real user profile or by Desktop; redaction is pattern-based and cannot catch arbitrary secrets; reparse checks are not race-free (verification.md "M3 diagnostics") | 13 |
| U-23 | No telemetry and no network access of its own (product statement, never measured) | 13.4 |
| U-24 | The MSI has never been installed, upgraded, repaired or uninstalled; ICE validation not run; per-user behavior, SmartScreen and Smart App Control, Restart Manager behavior unknown (verification.md "M3 release") | 3 |
| U-25 | Uninstall keeps data and never reverts settings (release-limits.md) | 3.5 |
| U-26 | Framework-dependent runtime check and missing-runtime experience (ADR-008) | 3.2 |
| U-27 | Schema migration v1/v2 to v3 with real older databases and interrupted migrations on a real filesystem (verification.md M0, M2 engine) | 9 |
| U-28 | Idle CPU and memory, startup time and responsiveness were never measured (no budget exists) | 14 |
| U-29 | The Worker image is an unsigned x64 console executable that requests `asInvoker`; its runtime behavior (inert, never elevates, rejects every request) is NOT observed by this plan or by any dev-host script, only by the future VM kit under `tests/vm` | 3.1.5 (PE inspection only) |
| U-30 | WPF-UI's Segoe Fluent Icons glyphs resolve from the font installed with Windows 11 (THIRD-PARTY-NOTICES.md manual review item) | 10 (look for missing-glyph boxes on every VM) |

## 16. Sign-off

A run is complete when: every section has a result per VM or an explicit NOT TESTED/BLOCKED reason; the results tables are filled; the evidence files are stored with the report; each failure is written up with the steps to reproduce; the unverified-claims register is updated (verified, refuted or still open) in verification.md. Only then may release notes say anything about Windows behavior, accessibility, Persian quality or installer behavior beyond "not verified".
