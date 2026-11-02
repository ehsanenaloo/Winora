<!-- SPDX-License-Identifier: GPL-3.0-only -->
# Spike S0: NativeAOT feasibility for the elevated worker

Date: 2026-10-09. Host: Windows 11 Pro 10.0.26300, SDK 10.0.401, runtime Microsoft.NETCore.App 10.0.12, unelevated session (verified `IsInRole(Administrator) = False`). Code: `tools/spikes/worker-aot/` (throwaway, outside `Winora.slnx`, own `Directory.Build.props` and `Directory.Packages.props` stopping root inheritance, no lock file, default NuGet source). Nothing was run elevated; only the spike's own binaries were executed; no pipe, event source, registry or shell object was created. `CreateNamedPipeW` and `ReportEventW` were compiled but never called.

## 1. Verdict

**NativeAOT feasible with conditions.** Every API protocol.md needs builds and runs correctly as a NativeAOT, Windows-subsystem executable with zero trim/AOT warnings. All reproducible Appendix B vectors pass. The conditions are build-environment and process conditions, listed in section 6. The environment-hook half of V-09 (unelevated) shows the decisive difference: AOT ignored startup hooks and profiler variables, while every CoreCLR variant loaded a profiler DLL even with startup hooks disabled and trimming on.

## 2. Evidence table

| # | Question | Result |
|---|---|---|
| 1 | Linker prerequisites present? | Yes, in an older form: Visual Studio Build Tools 2019 (`C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools`), MSVC 14.29.30133 `link.exe` (Hostx64\x64), Windows SDK 10.0.19041.0. No VS 2022 installed. Nothing was installed. Microsoft.VisualStudio.Component.VC.Tools.x86.x64 is present (vswhere lists it). |
| 2 | AOT publish works? | Yes, after one environment fix: ILCompiler invokes `vswhere.exe` by bare name, so `C:\Program Files (x86)\Microsoft Visual Studio\Installer` must be on `PATH` (first attempt failed with "'vswhere.exe' is not recognized", link exit 123). |
| 3 | ILCompiler pin | `Microsoft.DotNet.ILCompiler` 10.0.12 and `runtime.win-x64.Microsoft.DotNet.ILCompiler` 10.0.12 (matches runtime 10.0.12). Licence MIT (nuspec). Package ILCompiler 95 KB nupkg; runtime.win-x64 package 11.9 MB nupkg, 36 MB unpacked (contains `ilc.exe`, jit, AOT framework libs). Transitive: `Microsoft.NET.ILLink.Tasks` 10.0.12. Neither enters the shipped binary. Explicit `PackageReference` produces MSBuild warning "Delete explicit 'Microsoft.DotNet.ILCompiler' package reference ... to avoid versioning problems" (warning only; not promoted by `TreatWarningsAsErrors`, which is a compiler setting; a repo using `MSBuildTreatWarningsAsErrors` would fail). |
| 4 | Self-test result (AOT, run unelevated) | 14 of 14 rows PASS plus `json-roundtrip=PASS`, exit code 0. `DynamicCode=false`. |
| 5 | SHA-256 | H_d for vmtrust and release flavours and SHA-256(Qs) match Appendix B.3. |
| 6 | AesGcm, 12-byte nonce, 16-byte tag | GCM test cases 16 and 13 (B.2) reproduced; decrypt round trip; tampered ciphertext rejected with `CryptographicException`. |
| 7 | ECDH P-256 + HKDF | `HKDF.DeriveKey` matches RFC 5869 test case 1 (B.1). Importing the Appendix B scalars with their SEC1 points, `ECDiffieHellman.DeriveRawSecretAgreement` gives Z on both sides; exported Q equals documented Qc. |
| 8 | Appendix B reproducibility | The "GENERATED, NOT YET REPRODUCED" status can be upgraded for: B.1, B.2, Qc/Qs/Z, SHA-256(Qs), H_d (both), the 417-byte transcript hash T, PRK, k_c2s, k_s2c (via `HKDF.Extract` and `HKDF.DeriveKey` with `info` = label, no NUL), and the encrypted frame C>W type 0x10 counter 0 (decrypts to T and re-encrypts byte-identically). Not covered: the other frames, digests and plan tokens. `ExportParameters`/import of private scalar D plus Q works (Q required on import). |
| 9 | ECDsa P-256 | Sign/verify, 64-byte P1363 signature, tamper rejected. |
| 10 | COSE | `System.Security.Cryptography.Cose` is NOT in the .NET 10 shared framework (absent from `Microsoft.NETCore.App\10.0.12` and the reference pack). NuGet package 10.0.12, MIT, 86 KB net10.0 dll, depends on `System.Formats.Cbor` 10.0.12 (also MIT). `CoseSign1Message.SignDetached` / `DecodeSign1` / `VerifyDetached` work under NativeAOT with no IL2xxx/IL3xxx warnings (also none under the trimmed self-contained build). Tamper rejected. |
| 11 | System.Text.Json | Source-generated context only, `JsonSerializerIsReflectionEnabledByDefault=false`; serialize and deserialize round trip passes; zero warnings with trim, AOT and single-file analyzers enabled. |
| 12 | P/Invoke | `LibraryImport` (source generated, `AllowUnsafeBlocks`): `ConvertStringSecurityDescriptorToSecurityDescriptorW` (SDDL with NETWORK deny, user allow, SYSTEM allow converted to a 104-byte descriptor, length verified, freed with `LocalFree`); `CreateNamedPipeW` and `ReportEventW` stubs compiled into the AOT image via function-pointer address, not called; `WinVerifyTrust` structures declared and called ONLY on a temp copy of the spike's own exe: result `0x800B0100` (TRUST_E_NOSIGNATURE), as expected for an unsigned file. Signed-file behaviour is untested. |
| 13 | Subsystem and PE properties | `OutputType=WinExe` gives subsystem 2 (Windows GUI). DLL characteristics 0x8160: high-entropy VA, dynamic base, NX, no CFG bit (control-flow guard not enabled by default; add `/guard:cf` linker arg and test if wanted). Static imports are system DLLs only (ADVAPI32, bcrypt, CRYPT32, KERNEL32, ncrypt, ole32, api-ms-win-crt-*). `wintrust.dll` and the P/Invoke targets load lazily by name at first call (relevant to `SetDefaultDllDirectories`; `DirectPInvoke` items could link them statically). |
| 14 | Dynamic code | `RuntimeFeature.IsDynamicCodeSupported` is false under AOT, true under CoreCLR. Supports the "dynamic-code prohibition" row of the mitigation table for NativeAOT. |

## 3. Size, start-up, determinism

Same source, release, win-x64, `InvariantGlobalization`, `UseSystemResourceKeys`.

| Mode | Files | Size | Median cold start of 15 (ms, includes ~240 ms `Start-Process` overhead) |
|---|---|---|---|
| NativeAOT, size-optimised | 1 | 2.82 MB (exe 2,957,824 B) | 302 (min 230) |
| Framework-dependent CoreCLR | 6 | 0.37 MB (needs installed runtime) | 745 |
| Self-contained CoreCLR | 193 | 77.0 MB | 816 |
| Self-contained trimmed | 44 | 19.7 MB | 2098 (noisy; likely first-run scanning of fresh DLLs; do not rely on it) |

The 14-check self-test (key generation, signatures) dominates the AOT time, so AOT start-up is near the process-launch floor. Measurements are indicative, single host, Defender active.

Determinism (X18). Source copied to two different folders (`detA\src`, `detB\deeper\path\src`) and published:
- Managed `SpikeS0.dll` (fdd): identical SHA-256 across folders.
- NativeAOT exe, default linker: three builds, three different hashes; `cmp` shows exactly 3 differing bytes, the PE `TimeDateStamp` (linker uses wall-clock).
- With `<LinkerArg Include="/Brepro" />`: three builds, two folders, **identical** SHA-256 `fc1a53e2...5585`. Deterministic, provided `/Brepro`, `Deterministic`, `ContinuousIntegrationBuild`, and the same toolchain (same MSVC and SDK versions) are used. A different MSVC or SDK version will change the bytes; the toolchain must therefore be pinned in CI and recorded in the SBOM.

## 4. V-09, unelevated half (spike's own binaries only)

Each binary was started with the variable set in its own environment block; markers were written by a managed startup hook (`hook/`) and by a stand-in profiler DLL whose `DllMain` creates a marker file (`prof/s0prof.c`, built with the installed MSVC; it is not a real profiler).

| Variable set | AOT | Self-contained (default) | Self-contained, trimmed (`PublishTrimmed`) | Self-contained, `StartupHookSupport=false` | Framework-dependent |
|---|---|---|---|---|---|
| `DOTNET_STARTUP_HOOKS` | ignored | hook RAN | ignored (feature off) | ignored | hook RAN |
| `CORECLR_ENABLE_PROFILING` + `CORECLR_PROFILER` + `CORECLR_PROFILER_PATH` | ignored | profiler DLL LOADED | LOADED | LOADED | LOADED |
| `DOTNET_ROOT` to a bogus dir | ignored | (not used by self-contained) | n/a | n/a | no effect observed (apphost fell back to the global install) |

Conclusions: (a) a CoreCLR worker cannot be protected from profiler injection by `StartupHookSupport=false` or trimming; only an in-`Main` detective check exists, and that runs after the profiler DLL is already loaded; (b) a NativeAOT worker contains no runtime that reads these variables, so they are inert. Honest limit: this was an unelevated run; an elevated process uses the same runtime code, but environment inheritance for elevated launches (ShellExecute `runas` rebuilds the environment from the parent in a way that differs by launcher) was NOT tested, and the CoreCLR profiler path for elevated processes may apply additional checks. X1/V-09 on the VM decides. Registry-based profiler enablement (`COR_*` through machine environment keys or system-wide variables) was not tested.

### Exact VM procedure for the elevated half (not run here)

On the disposable VM with `WINORA_VM_TESTS=1` and the marker file, using the spike's own AOT and self-contained binaries (not Winora.Worker), from an elevated prompt:
1. Copy `s0\aot`, `s0\sc`, `s0\probe\S0Hook.dll`, `s0\probe\s0prof.dll` to `C:\S0` (admin-only ACL).
2. For each of {aot, sc, sctrim}: in the elevated shell set `DOTNET_STARTUP_HOOKS=C:\S0\S0Hook.dll`, `S0_HOOK_MARKER=C:\S0\hook.mark`; run the exe with an output path; record `hook.mark`. Clear the variable.
3. Repeat with `CORECLR_ENABLE_PROFILING=1`, `CORECLR_PROFILER={11111111-2222-3333-4444-555555555555}`, `CORECLR_PROFILER_PATH=C:\S0\s0prof.dll`, `S0_PROF_MARKER=C:\S0\prof.mark`.
4. Repeat steps 2 and 3 launching from a medium-integrity parent through the intended production launcher path (ShellExecute `runas`), with the variables set in the medium parent, and record whether they reach the elevated child (dump with a tiny test exe that lists variable names).
5. Repeat with the variables set machine-wide (System environment) and after reboot, then remove them.
6. Record OS build, results and expected output in technical-docs/verification.md.

## 5. Reproduce

PowerShell, from the repository root:

```
$env:Path=[Environment]::GetEnvironmentVariable('Path','Machine')+';'+[Environment]::GetEnvironmentVariable('Path','User')+';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
cd tools\spikes\worker-aot
dotnet publish SpikeS0.csproj -p:Mode=aot    -c Release -o <out>\aot      # NativeAOT (needs vswhere on PATH, MSVC + Windows SDK)
dotnet publish SpikeS0.csproj -p:Mode=fdd    -c Release -o <out>\fdd
dotnet publish SpikeS0.csproj -p:Mode=sc     -c Release -o <out>\sc
dotnet publish SpikeS0.csproj -p:Mode=sctrim -c Release -o <out>\sctrim
dotnet publish SpikeS0.csproj -p:Mode=scnohook -c Release -o <out>\scnohook
dotnet build hook\Hook.csproj -c Release -o <out>\probe                    # startup hook
# stand-in profiler: vcvars64.bat, then: cl /nologo /LD /O1 prof\s0prof.c /link /Brepro /OUT:s0prof.dll
<out>\aot\SpikeS0.exe <out>\aot.json                                       # unelevated; writes JSON + exit code
```

Delete `bin` and `obj` between modes (one working tree, different restore graphs). The Windows-subsystem exe prints nothing; results go to the path in argument 1 (default `%TEMP%\s0-selftest.json`). Determinism: copy the sources to two folders, publish each, compare `sha256sum`.

## 6. Prerequisites, conditions, risks

Missing prerequisites on this host: none for a working AOT build (VS Build Tools 2019 with the C++ x64/x86 build tools and Windows SDK 10.0.19041 suffice), but `vswhere.exe` is not on PATH by default. Recommended standard: VS 2022 (or later) Build Tools with component `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` and a Windows 10/11 SDK component (Microsoft.VisualStudio.Component.Windows11SDK.22621 or later). VS 2019 is out of mainstream support; do not rely on it for release builds. Workload "Desktop development with C++" is roughly 5 to 8 GB if it ever had to be installed; no install was attempted.

CI: the GitHub-hosted Windows Server 2025 image (current `windows-latest` mapping per GitHub's docs; confirm in the workflow) lists Visual Studio Enterprise 2022 17.14 with Workload.NativeDesktop, VC.Tools.x86.x64 17.14.36510.44, Windows SDK 10.0.26100, and .NET SDK 10.0.401 among its SDKs. So NativeAOT needs no extra install on that image, only a PATH entry for vswhere (the VS installer directory) in case the step does not already provide it. Sources: https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md and https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ (prerequisites section; consult at implementation time, not re-fetched here). Image toolchain versions drift without notice, which affects hash reproducibility (section 3).

Packages and lock files: AOT adds to the worker project `Microsoft.DotNet.ILCompiler`, `runtime.win-x64.Microsoft.DotNet.ILCompiler` (exact version; matching the SDK's runtime patch) plus transitive `Microsoft.NET.ILLink.Tasks`, and for COSE `System.Security.Cryptography.Cose` and `System.Formats.Cbor`. Central version pins and a `win-x64` lock target are needed, and the explicit-ILCompiler warning must be handled (alternatively drop the explicit reference and let the SDK select its bundled version, then pin by SDK via `global.json`; not tested here). THIRD-PARTY-NOTICES and SBOM rows are needed for the two COSE-related packages and the runtime components. Locked-mode restore in the repo was not exercised (the spike is deliberately standalone).

Risks:
- AOT output depends on the MSVC and Windows SDK versions: pin and record, build only on the CI image.
- ILCompiler and the runtime pack must track servicing releases of the runtime; every .NET servicing update is a worker rebuild and re-signing.
- Library behaviour under AOT beyond what was exercised (for example `Process.GetCurrentProcess().Modules`, used only by the spike) is not claimed.
- Debuggability and crash diagnostics are weaker (no managed stack from dumps without symbols); the early-failure record must be self-contained.
- `WinVerifyTrust` was only tested on an unsigned file; real signed-file and catalog behaviour needs the VM.
- The CFG bit is not set by default and the effect of the other mitigation-table entries on an AOT process is untested.
- Single run host; timings indicative only.

## 7. Recommendation

1. Protocol section 14.2 and D-11/A-1: record S0 as passed with the conditions above. Ship the worker as NativeAOT with the Windows subsystem and `/Brepro`. The evidence supports A-1's rule "NativeAOT for any release carrying a real machine-scope definition"; the CoreCLR self-contained fallback must remain M4a-VM-only, because profiler injection is not preventable there even with startup hooks off.
2. Keep the fail-closed environment rule in `Main` as defense in depth and diagnostics; for AOT it adds a second layer, for CoreCLR it is detective only. Move V-09/X1's elevated half to the VM using the procedure above, with the production launcher path.
3. A-5 / O-21: COSE under NativeAOT works with `System.Security.Cryptography.Cose` 10.0.12 (an out-of-band package, not shared framework). The raw ES256 envelope fallback is not needed for AOT reasons; the owner still decides whether a package (with `System.Formats.Cbor`) in the elevated image is acceptable versus the smaller raw envelope.
4. Protocol Appendix B: upgrade the status of the listed vectors to "reproduced by .NET 10 BCL (S0 spike)"; implement them as U-H07 tests in the real library.
5. Before WP-E starts: decide the build toolchain standard (VS 2022 Build Tools component pair, pinned image), add `vswhere` PATH handling to the build script, add the lock target and notices, and add a CI determinism step (build twice from different folders and compare hashes).
6. Owner decision needed only if the owner wants to avoid the out-of-band COSE dependency or cannot guarantee the C++ toolchain in CI; otherwise no escalation.
