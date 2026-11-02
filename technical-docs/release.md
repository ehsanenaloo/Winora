# Release process (non-elevated tier, 0.x)

Status: tooling exists and was run locally; no release has been made. Everything here produces **unsigned** artifacts. Nothing in this repository signs, installs, uninstalls or publishes. Decisions: [ADR-008](adrs.md) (packaging) and ADR-009 (reproducibility). What the product does: [release-limits.md](release-limits.md). What still needs a human on a VM: [manual-test-plan.md](manual-test-plan.md).

## Artifacts

All under `dist/` (git-ignored). `tools/publish.ps1` is the only producer.

| File | What it is |
| --- | --- |
| `Winora-<version>-win-x64-portable.zip` | Folder `Winora-<version>-win-x64/` with `desktop/`, `cli/`, `worker/` (each exactly as `dotnet publish` emits it, framework-dependent, no PDBs, only the win-x64 SQLite native; `desktop/` is published for win-x64 with ReadyToRun so the window opens several times faster), `LICENSE`, `THIRD-PARTY-NOTICES.md`, `third-party-licenses/`, `RELEASE-LIMITS.md`, `Check-Prerequisites.ps1`, `sbom.cdx.json`, `FILES.sha256`. Entries are in fixed ordinal order with a normalized time. |
| `Winora-<version>-win-x64.msi` | UNSIGNED per-user MSI (WiX 5). Built and inspected only. |
| `Winora-<version>-win-x64-machine.msi` | UNSIGNED all-users MSI (only with `-Machine`). Installs to Program Files and holds the NativeAOT worker as one native image. Built and inspected only; an unsigned install never gets administrator changes. |
| `SHA256SUMS` | SHA-256 of the zip, MSI, SBOM and release notes (`<hash> *<name>`, sorted). |
| `sbom.cdx.json` | CycloneDX 1.5-style package list authored by `tools/package_report.ps1` from `dotnet list package --include-transitive` and the packages' nuspec files. NuGet packages only; the .NET runtime is a prerequisite and is not listed. |
| `RELEASE-NOTES.md` | Stub: unsigned banner plus the matching CHANGELOG section. A human edits it before publishing anything. |
| `stage/` | Intermediate layout that feeds the zip and MSI. Not a deliverable. |

## Build

Requirements: Windows, PowerShell 7, SDK 10.0.401 (global.json), NuGet access to nuget.org for restore (the WiX SDK is restored like any package; nothing is installed globally).

```powershell
pwsh -File tools/publish.ps1                  # restore --locked-mode, Release build, tests, publish, package, MSI, SBOM, sums
pwsh -File tools/publish.ps1 -SkipTests       # when tests ran already (CI does this)
pwsh -File tools/publish.ps1 -SkipInstaller   # zip only
pwsh -File tools/publish.ps1 -Machine         # also the all-users MSI with the NativeAOT worker (needs the Visual Studio C++ build tools)
pwsh -File tools/publish.ps1 -ReproCheck      # also rebuild from a copy of the tree at another path and compare hashes
pwsh -File tools/verify_dist.ps1              # check the result
```

The version comes from `Directory.Build.props` (`<Version>`). `Winora.Cli` prints a hard-coded `Winora 0.1.0` in `src/Winora.Cli/Program.cs`; `verify_dist.ps1` fails if it differs from the props version, so bump both together (the CLI owner should read it from the assembly instead).

What the script does not do: sign, upload, tag, install, run the MSI, touch the registry or shell.

## What verify_dist.ps1 checks

SHA256SUMS and `FILES.sha256` match; zip names are safe, ordered and carry one normalized timestamp; required files exist; only `runtimes/win-x64` natives ship; LICENSE is GPLv3 and the notice file carries the GPL-3.0-only SPDX line and names every SBOM package; no PDB, test assembly, key, log, database or secret-named file; no private-key, token or password pattern; no build-machine user, temp or checkout path embedded in any shipped file (this check found real PDB paths before PathMap and a forced rebuild were added); executables are `NotSigned`; the unzipped CLI answers `--version`, `discover startup --json` (read-only) and `settings list --json --sandbox-root <guid>` with a temp database, a write command without `--yes` refuses with exit 2, and the Worker exits 3. If an MSI exists it is opened through the Windows Installer database API in read-only mode and checked: version, UpgradeCode, per-user, file names and sizes equal to the zip, Upgrade and Shortcut tables, only HKCU registry rows, and no custom actions, services, environment, file-association or COM tables. The MSI is never installed and `msiexec` is never started.

## Installer notes (installer/Winora.Installer)

- Per-user, installs to `%LOCALAPPDATA%\Programs\Winora`; Start menu entry "Winora" runs `desktop\Winora.Desktop.exe`; uninstall through Apps & features (per-user entry) or `msiexec /x` by a human.
- UpgradeCode `{912F5E18-EB3C-4C58-A6C7-4FE958DA71BE}` is permanent. ProductCode is derived from the version. A higher version replaces a lower one; a lower one is refused ("newer version already installed"). The MSI compares only the first three version fields.
- Launch condition: 64-bit Windows 11, build 22000 or later (not applied when removing).
- It does not check for the .NET 10 Desktop Runtime (the version value names cannot be wildcard-searched). The app host reports a missing runtime; document it in release notes.
- **Data**: the package contains nothing under `%LOCALAPPDATA%\Winora` and never removes it. The default is keep. "Remove my data too" is not implemented; the manual step is in release-limits.md. Reason: an untested deletion path in an installer is the wrong risk for 0.x; a future in-app "delete Winora data" action can be reviewed under technical-docs/uninstall.md rules.
- **Windows settings**: uninstall never reverts settings Winora changed. Users revert from History first (or `winora settings revert`).
- Running instances: the MSI uses default Restart Manager behavior and may ask to close Winora; this is untested (manual plan section on install/upgrade).
- ICE validation is suppressed in the build (`SuppressValidation`) because it opens Windows Installer on the build host. Run `wix msi validate` (or Orca/ICE) on a VM before shipping.
- The WiX SDK is an MSBuild SDK, so NuGet's lock file for the installer project is empty (it locks nothing); the pin is the exact version in the .wixproj Sdk attribute. For review, the SHA-512 of WixToolset.Sdk 5.0.2 as restored here is `57a4734d62848b7e57757c92632810e984ae6a2f5e685ddd3cdf800cb37af9d391caa39d276b86380d11fef7a544bb5b8911dd3a9f9c6301ed2addb3334dfdeb` (compare with the nupkg on nuget.org before trusting a new machine).
- The MSI file is not byte-reproducible (WiX writes a new package code and times each build). The files inside are.

## Reproducibility (what is and is not claimed)

Recorded in verification.md ("M3 release"): a second build from a copy of the tree at a different path produced a byte-identical zip, SBOM and staged files. Deflate output depends on the .NET runtime's zlib; a different SDK or runtime version may legitimately produce a different zip from identical inputs. The MSI differs by a few dozen bytes per build. Nobody independent has rebuilt it yet.

## Where signing would plug in (nothing is signed today)

1. Obtain a code-signing certificate and decide the publisher name. Prefer a cloud signing service or a hardware token reached from a protected CI environment; never commit or store a `.pfx` in the repository or as a plain secret.
2. In `publish.ps1`, after the per-app publish and before FILES.sha256: sign `desktop`, `cli` and `worker` `Winora*.exe` and `Winora*.dll` with an RFC 3161 timestamp. (Third-party DLLs keep their own signatures or none.)
3. The zip, FILES.sha256, MSI and SHA256SUMS are then produced from the signed files (already the order in the script). Sign the MSI last.
4. Change `verify_dist.ps1` from "must be NotSigned" to "must be Valid and signed by the expected subject".
5. SmartScreen reputation still builds slowly even with a valid certificate; EV certificates no longer give instant reputation. Tell users honestly.
6. The CI file marks the step location. No secret exists today.

## Release checklist (human)

1. All work merged, `technical-docs/verification.md` and the manual test plan results updated, CHANGELOG section for the version complete, Persian review status stated.
2. Clean checkout, `pwsh -File tools/publish.ps1 -ReproCheck`, then `tools/verify_dist.ps1`; both pass.
3. Read the SBOM and THIRD-PARTY-NOTICES.md "manual review" list; resolve or consciously accept each item (Apache-2.0 text for SQLitePCLRaw, Segoe Fluent Icons question).
4. Run the manual test plan on the VM matrix with the exact artifacts whose hashes will be published. Record builds, results and the corrected `RequiresExplorerRestart` / `RequiresSignOut` flags.
5. Owner decisions made: publisher name, signing, issue tracker and security contact, update channel (none in 0.x), whether to ship self-contained.
6. Edit RELEASE-NOTES.md (keep the unsigned banner unless signed), publish files and `SHA256SUMS` together, tag the commit.

## Not done / owner decisions

Code-signing certificate and publisher identity; MSI Manufacturer text (currently the placeholder "Winora project"); update channel; issue tracker and security contact; self-contained versus framework-dependent; a per-machine package for M4 (ADR-008); commit-SHA pinning of the GitHub Actions in CI.
