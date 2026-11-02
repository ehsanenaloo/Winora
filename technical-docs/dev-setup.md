# Development environment

## Prerequisites

- Windows 10/11 x64 (development host reported Windows 11, build 10.0.26300).
- .NET SDK **10.0.401** exactly (pinned in global.json). It was NOT installed on the host when the 2026-10-08 review ran, so build and tests could not be run there. Install from https://dotnet.microsoft.com/download/dotnet/10.0, or relax global.json via an owner-approved change.
- Visual Studio is optional; PowerShell 7 recommended. Python 3.10+ only for a development tool that is not published.
- Initial NuGet restore needs network; later work is offline.

## Commands

```powershell
dotnet restore Winora.slnx --locked-mode
dotnet build Winora.slnx -c Release --no-restore
dotnet test Winora.slnx -c Release --no-build
python a development tool that is not published
```

## Rules for contributors and subagents

- Read AGENTS.md and docs in its precedence order before edits; stop on contradictions.
- Never mutate real Windows settings, services, power, files or registry on the development host. Mutation tests target temporary SQLite files, an isolated HKCU test subkey, or a disposable VM.
- Behavior evidence for other tools stays outside this repository. Do not copy source, XAML, assets, identifiers, service tables or power files; write original requirement IDs and text.
- User-facing text in .resx (English neutral, Persian satellite); stable codes for persisted outcomes.
- Keep the layering: Domain platform neutral; Application references Domain only; adapters reference Application/Domain; Desktop, CLI and Worker are composition roots.
- Record status, coverage, evidence and limits in technical-docs/verification.md and technical-docs/backlog.md. Do not claim UI, Windows integration or release readiness without evidence.
- Git: no repository exists yet. Initialize only when the owner asks.

## Test infrastructure to build (M0/M2)

- Two-process SQLite harness (spawn child test host).
- Fault-injecting store wrapper for every durable boundary.
- v1 schema fixture databases for migration tests.
- HKCU test-subkey fixture (create, snapshot, cleanup) for registry mutator tests.
