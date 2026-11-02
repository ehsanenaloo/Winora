# Winora 0.x: what it does and does not do

This statement ships with every Winora 0.x build (as RELEASE-LIMITS.md) and is meant to be read before you run it. It describes what the project can honestly claim today. Verification status of every claim is in [verification.md](verification.md); unverified items are listed in [manual-test-plan.md](manual-test-plan.md).

## What it does

- Changes Windows 11 settings from fixed, authored catalogs: per-user values in the current user's registry (HKCU) and machine values in HKLM (the desktop app asks for administrator rights when it starts). It also changes services, scheduled tasks, startup entries, power plans, DNS settings and optional Windows features, each through a reviewed, closed list. The lists are in [settings-catalog.md](settings-catalog.md).
- Shows a review (before value, after value, exact location, risk, Explorer warning) before every change, records what it is about to do in a local journal before it does it, and can revert a change it made. Revert refuses if something else changed the value since (for example you changed it in the Windows Settings app); it never overwrites a newer outside change.
- After a confirmed change that needs it, it asks Windows to pick the change up: a settings-changed broadcast and, only for settings that need it, a graceful restart of File Explorer (open File Explorer windows close and the taskbar disappears for a few seconds). Some settings only take effect after you sign out; Winora tells you instead of claiming success.
- Lists startup entries, scheduled tasks, services and installed applications (Desktop pages and `winora discover`). The command line tool only lists.
- Removes preinstalled Store apps and installed programs after a review. A program is removed by starting the removal command that it registered for itself, only from a folder that only administrators can change; Winora never ends an uninstaller. Leftover folders are moved into a quarantine from which they can be restored, and are deleted only when you delete them there or the retention time passes.
- A clearly labeled simulation page and `winora simulate`, which operate on a fake setting in Winora's own database only.
- English and Persian (right-to-left) interface, light/dark theme, a command line (`Winora.Cli.exe`) with the same engine.
- A local diagnostics log and a user-initiated, redacted diagnostics export to a folder you choose (see verification.md for the current status of this feature). Nothing is sent anywhere.

## What it does not do

- No automatic or background changes: every change starts from a review you confirm. Some changes cannot be undone; the review says which. A machine policy (Group Policy, MDM) is respected: a setting that a policy manages is not offered for change.
- The command line tool does not elevate and changes only per-user settings.
- No hardware tuning beyond the listed graphics driver values, and no deletion of other software's data except through the quarantine described above.
- No telemetry, accounts, advertising, auto-update, or network access of its own. There is no update channel yet; you update by installing a newer build yourself.
- Not signed (see below). Not Microsoft Store or MSIX packaged.
- Windows 11 only (build 22000 or later; some settings need 22621 or later, one stops at 22631). Windows 10 and Windows Server are not supported. x64 only; ARM64 is not supported until it is tested.
- The Persian text is machine-drafted and has not had a native-speaker review. Expect awkward wording ([persian-review.md](persian-review.md)).
- Whether each setting visibly takes effect immediately, after Explorer restarts or only after sign-out is partly observed behavior that has not been verified on a range of real Windows 11 builds. Manual VM verification is pending ([manual-test-plan.md](manual-test-plan.md)).
- Accessibility (Narrator, high contrast, 150 and 200 percent scaling) has been checked by automated structure tests only, not with assistive technology.

## Unsigned builds, SmartScreen and what to expect

Release artifacts are built and checksummed but not code-signed. Expect Windows SmartScreen ("Windows protected your PC") when running Winora.Desktop.exe or the MSI downloaded from a browser, and expect Smart App Control, if enabled in enforcement mode, to block unsigned binaries. Do not bypass these warnings on a machine you do not control. Instead verify the download as below, and prefer trying Winora in a virtual machine first. Signing needs a certificate and a publisher identity that the project owner has not chosen yet.

## Prerequisite: .NET 10 Desktop Runtime

Builds are framework-dependent. Install the .NET 10 Desktop Runtime (x64) from Microsoft before starting Winora.Desktop.exe. If it is missing the app host reports it. Run `Check-Prerequisites.ps1` (included in the zip; read-only) to see what is found.

## Verify a download

Each release has a `SHA256SUMS` file next to the artifacts. In PowerShell:

```powershell
Get-FileHash .\Winora-0.1.0-win-x64-portable.zip -Algorithm SHA256
# compare with the line for that file in SHA256SUMS
```

The zip also contains `FILES.sha256` with the hash of every file inside. `sbom.cdx.json` lists the NuGet packages and licenses. Checksums prove the file matches what the maintainer published, not that the maintainer is who they claim; there is no signature.

## Where Winora keeps data

The Desktop app runs as administrator and keeps its journal and preferences in `%ProgramData%\Winora\journals\operations-<SID>.db`, one file per account, in a folder only administrators can change; the other records of the elevated app are in the same folder, and uninstallers and repair tools get a temporary folder under `%ProgramData%\Winora\temp` that is removed three days later. The log and the profiles stay under `%LOCALAPPDATA%\Winora`. The command line in a normal (not elevated) console keeps its journal in `%LOCALAPPDATA%\Winora\simulation.db` (the file name is historical). The first elevated start copies the checked rows of that old file once and renames it to `simulation.db.untrusted-<time>`. Nothing is written elsewhere except the registry values of the settings you change. Removing Winora (portable: delete the folder; installed: uninstall) keeps that data folder by default, so history survives a reinstall.

Uninstalling Winora never reverts your Windows settings. If you want the original values back, revert them from Winora's History page (or `winora settings revert <operation-id>`) before you remove it. Afterwards, to delete Winora's own data, close Winora and delete `%LOCALAPPDATA%\Winora` and, as administrator, `%ProgramData%\Winora`. The installer does not offer to do this for you in 0.x.

## Reporting problems

There is no telemetry, so nobody knows about a problem unless you say so. Use the project's issue tracker (location to be published by the owner). Attach the diagnostics export if you are comfortable with its contents; read it first, it is a plain archive you can open. Do not include anything you consider private. Security issues should not be posted publicly; the owner's contact for that is not yet published.
