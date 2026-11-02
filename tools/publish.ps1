# SPDX-License-Identifier: GPL-3.0-only
#Requires -Version 7.0
<#
.SYNOPSIS
  Builds the UNSIGNED Winora release artifacts under dist/ (git-ignored). It never signs, installs, uninstalls or publishes anything.
.DESCRIPTION
  Steps: locked restore, Release build, tests (skippable), framework-dependent win-x64 publish of Desktop, CLI and Worker, merge into one
  layout, deterministic portable zip (fixed order, normalized timestamps), optional unsigned per-user MSI (WiX, build only),
  CycloneDX-style package SBOM, release-notes stub from CHANGELOG.md, SHA256SUMS. -Machine also builds the per-machine layout with the NativeAOT worker
  (one native image, no runtime to inject into; needs the Visual Studio C++ build tools) and an UNSIGNED per-machine MSI. Signing is a separate, protected step (trust.md 4.5). -ReproCheck repeats the whole build from a copy of the
  source tree at a different path and compares hashes.
  Nothing here touches the Windows registry, services, shell or the installed-programs list. The MSI is built, never run.
.EXAMPLE
  pwsh -File tools/publish.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$Machine,
    [switch]$ReproCheck,
    [string]$OutputRoot,
    [string]$SourceRevision
)
Set-StrictMode -Version Latest
$env:WINORA_NO_LOG = "1"
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'dist' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$fixedTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

function Invoke-Dotnet {
    # Uses the automatic $args on purpose: dotnet options such as -c and -o must not bind to PowerShell parameters.
    Write-Host ("> dotnet " + ($args -join ' ')) -ForegroundColor DarkGray
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE" }
}
function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }
function Assert-Inside([string]$path, [string]$root) {
    $full = [IO.Path]::GetFullPath($path); $r = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    if (-not $full.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to touch '$full' outside '$root'" }
}
function Reset-Directory([string]$path) {
    Assert-Inside $path $OutputRoot
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path | Out-Null
}
function New-NameGuid([byte[]]$hash) {
    $h = ([BitConverter]::ToString($hash[0..15]) -replace '-', '').ToLowerInvariant()
    $variant = '89ab'[[Convert]::ToInt32($h[16].ToString(), 16) % 4]
    $h = $h.Substring(0, 12) + '5' + $h.Substring(13, 3) + $variant + $h.Substring(17, 15)
    '{0}-{1}-{2}-{3}-{4}' -f $h.Substring(0, 8), $h.Substring(8, 4), $h.Substring(12, 4), $h.Substring(16, 4), $h.Substring(20, 12)
}function New-GuidFromText([string]$text) { (New-NameGuid ([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))).ToUpperInvariant() }
function New-DeterministicZip([string]$sourceDir, [string]$zipPath, [string]$rootName) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $sourceDir = (Resolve-Path $sourceDir).Path.TrimEnd('\')
    $names = [string[]]@(Get-ChildItem -LiteralPath $sourceDir -Recurse -File | ForEach-Object { $_.FullName.Substring($sourceDir.Length + 1).Replace('\', '/') })
    [Array]::Sort($names, [StringComparer]::Ordinal)
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($n in $names) {
            $entry = $zip.CreateEntry("$rootName/$n", [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTime
            $in = [IO.File]::OpenRead((Join-Path $sourceDir $n.Replace('/', '\')))
            $out = $entry.Open()
            try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
        }
    } finally { $zip.Dispose() }
}

# ---- version stamping from Directory.Build.props (single source) ----
$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)
$version = @($props.Project.PropertyGroup | Where-Object { $_.PSObject.Properties['Version'] } | ForEach-Object { $_.Version })[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$version' in Directory.Build.props must be major.minor.patch" }
$stamp = "Winora-$version-win-x64"
Write-Host "Winora $version, output $OutputRoot" -ForegroundColor Cyan

Push-Location $repo
try {
    # ---- restore, build, test ----
    # The commit hash goes into the informational version for traceability. It is passed EXPLICITLY so a copy without .git (the
    # reproducibility check) stamps the identical value instead of silently differing.
    if (-not $SourceRevision) { $SourceRevision = (& git -C $repo rev-parse HEAD 2>$null); if ($LASTEXITCODE -ne 0) { $SourceRevision = '' }; $global:LASTEXITCODE = 0 }
    $ci = '-p:ContinuousIntegrationBuild=true'
    $rev = if ($SourceRevision) { "-p:SourceRevisionId=$SourceRevision" } else { '-p:SourceRevisionId=' }
    # Map the checkout path to /_/ so no build-machine path ends up in assemblies (PDB path, caller file paths); makes the build path independent.
    $pathMap = "-p:PathMap=$repo\=/_/"
    Invoke-Dotnet restore Winora.slnx --locked-mode
    Invoke-Dotnet build Winora.slnx -c $Configuration --no-restore $ci $rev $pathMap
    if ($SkipTests) { Write-Host 'Tests SKIPPED (-SkipTests).' -ForegroundColor Yellow }
    else { Invoke-Dotnet test Winora.slnx -c $Configuration --no-build }

    # ---- publish (framework-dependent; no PDBs; see ADR-008) ----
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    $work = Join-Path $OutputRoot 'stage-work'; Reset-Directory $work
    foreach ($p in 'Desktop', 'Cli', 'Launch', 'Worker') {
        # The Desktop app is precompiled for win-x64 (ReadyToRun, still framework-dependent): it starts several times faster (DEC-IW-361).
        $rid = if ($p -eq 'Desktop') { @('-r', 'win-x64', '--self-contained', 'false') } else { @() }
        # Forced rebuild: an up-to-date compile output would otherwise keep the PDB reference from the test build above.
        Invoke-Dotnet build "src/Winora.$p" -c $Configuration --no-restore --no-incremental @rid $ci $rev $pathMap '-p:DebugType=none' '-p:DebugSymbols=false'
        Invoke-Dotnet publish "src/Winora.$p" -c $Configuration --no-build @rid -o (Join-Path $work $p) $ci $rev $pathMap '-p:DebugType=none' '-p:DebugSymbols=false'
    }
    # One folder per application, exactly as `dotnet publish` produced it. The apps resolve different TFM variants of the same package
    # (SQLitePCLRaw.provider.e_sqlite3 differs between Desktop and CLI), so merging them into one folder would be unsafe.
    $stage = Join-Path $OutputRoot 'stage'; Reset-Directory $stage
    foreach ($p in 'Desktop', 'Cli', 'Launch', 'Worker') {
        $src = Join-Path $work $p
        $dstRoot = Join-Path $stage $p.ToLowerInvariant()
        foreach ($f in Get-ChildItem -LiteralPath $src -Recurse -File) {
            $rel = $f.FullName.Substring($src.Length + 1)
            if ($rel -like '*.pdb') { continue }
            # x64 only: the SQLite package ships natives for many platforms; keep win-x64 (ADR-008).
            if ($rel -like 'runtimes\*' -and $rel -notlike 'runtimes\win-x64\*') { continue }
            $dst = Join-Path $dstRoot $rel
            New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
            Copy-Item -LiteralPath $f.FullName -Destination $dst
        }
    }
    Remove-Item -LiteralPath $work -Recurse -Force

    # ---- documents shipped next to the binaries ----
    Copy-Item LICENSE (Join-Path $stage 'LICENSE')
    Copy-Item THIRD-PARTY-NOTICES.md (Join-Path $stage 'THIRD-PARTY-NOTICES.md')
    Copy-Item technical-docs/release-limits.md (Join-Path $stage 'RELEASE-LIMITS.md')
    Copy-Item tools/prereq-check.ps1 (Join-Path $stage 'Check-Prerequisites.ps1')
    $nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
    $licenseDir = Join-Path $stage 'third-party-licenses'; New-Item -ItemType Directory -Path $licenseDir | Out-Null
    $licenseFiles = @(
        @('wpf-ui/4.3.0/LICENSE.md', 'WPF-UI.LICENSE.md'), @('wpf-ui/4.3.0/ThirdPartyNotices.txt', 'WPF-UI.ThirdPartyNotices.txt'),
        @('communitytoolkit.mvvm/8.4.2/License.md', 'CommunityToolkit.Mvvm.License.md'), @('communitytoolkit.mvvm/8.4.2/ThirdPartyNotices.txt', 'CommunityToolkit.Mvvm.ThirdPartyNotices.txt'),
        @('microsoft.extensions.logging/10.0.12/THIRD-PARTY-NOTICES.TXT', 'Microsoft.Extensions.ThirdPartyNotices.txt'))
    foreach ($l in $licenseFiles) {
        $s = Join-Path $nuget $l[0]
        if (Test-Path $s) { Copy-Item $s (Join-Path $licenseDir $l[1]) } else { Write-Warning "License file not found in NuGet cache: $($l[0])" }
    }

    # ---- SBOM (authored, no tool download) ----
    $sbom = Join-Path $OutputRoot 'sbom.cdx.json'
    & (Join-Path $PSScriptRoot 'package_report.ps1') -Format Sbom -OutFile $sbom -ProductVersion $version
    Copy-Item $sbom (Join-Path $stage 'sbom.cdx.json')

    # ---- per-file hashes inside the package ----
    $fileLines = foreach ($f in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $rel = $f.FullName.Substring($stage.Length + 1).Replace('\', '/'); "$(Get-Sha256 $f.FullName) *$rel"
    }
    $sorted = [string[]]@($fileLines); [Array]::Sort($sorted, [StringComparer]::Ordinal)
    [IO.File]::WriteAllText((Join-Path $stage 'FILES.sha256'), (($sorted -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

    # Normalize file times in the staged tree: the MSI cabinet stores them, and they would otherwise carry the build time.
    foreach ($item in Get-ChildItem -LiteralPath $stage -Recurse -Force) { $item.LastWriteTimeUtc = $fixedTime.UtcDateTime; $item.CreationTimeUtc = $fixedTime.UtcDateTime }

    # ---- portable zip ----
    $zipPath = Join-Path $OutputRoot "$stamp-portable.zip"
    New-DeterministicZip $stage $zipPath $stamp
    $artifacts = [System.Collections.Generic.List[string]]::new(); $artifacts.Add($zipPath)

    # ---- unsigned per-user MSI (build only, never run) ----
    $msiPath = $null
    if ($SkipInstaller) { Write-Host 'Installer SKIPPED (-SkipInstaller).' -ForegroundColor Yellow }
    else {
        $wixproj = 'installer/Winora.Installer/Winora.Installer.wixproj'
        $productCode = New-GuidFromText "winora-product-code:$version"
        $msiDir = Join-Path $OutputRoot 'msi-out'; Reset-Directory $msiDir
        Invoke-Dotnet restore $wixproj
        Invoke-Dotnet build $wixproj -c $Configuration --no-restore --no-incremental "-p:StageDir=$stage" "-p:WinoraVersion=$version" "-p:WinoraProductCode={$productCode}" "-p:OutDir=$msiDir\"
        $built = Get-ChildItem -LiteralPath $msiDir -Filter *.msi -File | Select-Object -First 1
        if (-not $built) { throw 'WiX build produced no MSI' }
        $msiPath = Join-Path $OutputRoot "$stamp.msi"
        Move-Item -LiteralPath $built.FullName -Destination $msiPath -Force
        Remove-Item -LiteralPath $msiDir -Recurse -Force
        $artifacts.Add($msiPath)
    }

    # ---- per-machine layout and UNSIGNED per-machine MSI (build only, never run) ----
    $machineMsiPath = $null
    if ($Machine) {
        # The native compiler runs vswhere.exe by bare name, so the Visual Studio installer folder must be on PATH.
        $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
        if ((Test-Path -LiteralPath $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH = "$env:PATH;$vsInstaller" }
        $stageMachine = Join-Path $OutputRoot 'stage-machine'; Reset-Directory $stageMachine
        foreach ($item in Get-ChildItem -LiteralPath $stage -Force | Where-Object { $_.Name -notin 'worker', 'FILES.sha256', 'Check-Prerequisites.ps1' }) {
            Copy-Item -LiteralPath $item.FullName -Destination $stageMachine -Recurse
        }
        $aotWork = Join-Path $OutputRoot 'worker-aot'; Reset-Directory $aotWork
        Invoke-Dotnet restore src/Winora.Worker -r win-x64 '-p:WorkerAot=true' --locked-mode
        Invoke-Dotnet publish src/Winora.Worker -c $Configuration -r win-x64 --no-restore -o $aotWork '-p:WorkerAot=true' $ci $rev $pathMap '-p:DebugType=none' '-p:DebugSymbols=false'
        $workerExe = Join-Path $aotWork 'Winora.Worker.exe'
        if (-not (Test-Path -LiteralPath $workerExe)) { throw 'The NativeAOT worker was not produced.' }
        # Only the native image ships: no managed assemblies, no runtimeconfig, no deps.json, so nothing beside the signed exe can change what runs.
        $workerDir = Join-Path $stageMachine 'worker'; New-Item -ItemType Directory -Force -Path $workerDir | Out-Null
        Copy-Item -LiteralPath $workerExe -Destination $workerDir
        Remove-Item -LiteralPath $aotWork -Recurse -Force
        $machineLines = foreach ($f in Get-ChildItem -LiteralPath $stageMachine -Recurse -File) {
            $rel = $f.FullName.Substring($stageMachine.Length + 1).Replace('\', '/'); "$(Get-Sha256 $f.FullName) *$rel"
        }
        $machineSorted = [string[]]@($machineLines); [Array]::Sort($machineSorted, [StringComparer]::Ordinal)
        [IO.File]::WriteAllText((Join-Path $stageMachine 'FILES.sha256'), (($machineSorted -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
        foreach ($item in Get-ChildItem -LiteralPath $stageMachine -Recurse -Force) { $item.LastWriteTimeUtc = $fixedTime.UtcDateTime; $item.CreationTimeUtc = $fixedTime.UtcDateTime }
        if ($SkipInstaller) { Write-Host 'Per-machine installer SKIPPED (-SkipInstaller).' -ForegroundColor Yellow }
        else {
            $wixprojMachine = 'installer/Winora.Machine.Installer/Winora.Machine.Installer.wixproj'
            $machineProductCode = New-GuidFromText "winora-machine-product-code:$version"
            $machineMsiDir = Join-Path $OutputRoot 'msi-machine-out'; Reset-Directory $machineMsiDir
            Invoke-Dotnet restore $wixprojMachine
            Invoke-Dotnet build $wixprojMachine -c $Configuration --no-restore --no-incremental "-p:StageDir=$stageMachine" "-p:WinoraVersion=$version" "-p:WinoraProductCode={$machineProductCode}" "-p:OutDir=$machineMsiDir\"
            $builtMachine = Get-ChildItem -LiteralPath $machineMsiDir -Filter *.msi -File | Select-Object -First 1
            if (-not $builtMachine) { throw 'WiX build produced no per-machine MSI' }
            $machineMsiPath = Join-Path $OutputRoot "$stamp-machine.msi"
            Move-Item -LiteralPath $builtMachine.FullName -Destination $machineMsiPath -Force
            Remove-Item -LiteralPath $machineMsiDir -Recurse -Force
            $artifacts.Add($machineMsiPath)
        }
    }

    # ---- release notes stub from CHANGELOG ----
    $changelog = Get-Content CHANGELOG.md -Raw
    $m = [regex]::Match($changelog, "(?ms)^## \[?$([regex]::Escape($version))\]?.*?(?=^## |\z)")
    if (-not $m.Success) { $m = [regex]::Match($changelog, '(?ms)^## \[Unreleased\].*?(?=^## |\z)') }
    $section = if ($m.Success) { $m.Value.Trim() } else { '(No CHANGELOG section found for this version.)' }
    $notes = @"
# Winora $version release notes (stub)

**UNSIGNED BUILD.** These files are not code-signed. Windows SmartScreen and Smart App Control may warn or block. Verify the SHA-256 values in SHA256SUMS before running anything. See RELEASE-LIMITS.md for what this release does and does not do.

Files: $(($artifacts | ForEach-Object { Split-Path $_ -Leaf }) -join ', '), sbom.cdx.json, SHA256SUMS.

$section
"@
    $notesPath = Join-Path $OutputRoot 'RELEASE-NOTES.md'
    [IO.File]::WriteAllText($notesPath, ($notes.Replace("`r`n", "`n")), [Text.UTF8Encoding]::new($false))
    $artifacts.Add($sbom); $artifacts.Add($notesPath)

    # ---- SHA256SUMS ----
    $sumLines = [string[]]@($artifacts | ForEach-Object { "$(Get-Sha256 $_) *$(Split-Path $_ -Leaf)" })
    [Array]::Sort($sumLines, [StringComparer]::Ordinal)
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'SHA256SUMS'), (($sumLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

    Write-Host ''
    Write-Host 'Artifacts:' -ForegroundColor Cyan
    foreach ($a in $artifacts) { '{0,12:N0}  {1}' -f (Get-Item $a).Length, (Split-Path $a -Leaf) }
    Write-Host ''
    Write-Host '*** ALL ARTIFACTS ARE UNSIGNED. Nothing was signed, installed or published. ***' -ForegroundColor Yellow

    # ---- reproducibility check from a copy at a different path ----
    if ($ReproCheck) {
        $copy = Join-Path ([IO.Path]::GetTempPath()) ("winora-repro-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        $reproOut = Join-Path $OutputRoot 'repro'
        Write-Host "Reproducibility check: second build from $copy" -ForegroundColor Cyan
        & robocopy $repo $copy /E /XD bin obj .git .vs artifacts dist TestResults .work /XF *.trx /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
        $global:LASTEXITCODE = 0
        $args2 = @('-NoProfile', '-File', (Join-Path $copy 'tools/publish.ps1'), '-SkipTests', '-OutputRoot', $reproOut, '-SourceRevision', $SourceRevision)
        if ($SkipInstaller) { $args2 += '-SkipInstaller' }
        # The second build makes the same artifacts as the first, the per-machine MSI included.
        if ($Machine) { $args2 += '-Machine' }
        & pwsh @args2
        if ($LASTEXITCODE -ne 0) { throw 'second build failed' }
        $same = $true
        foreach ($a in $artifacts) {
            $leaf = Split-Path $a -Leaf
            if ($leaf -eq 'RELEASE-NOTES.md') { continue }
            $other = Join-Path $reproOut $leaf
            $h1 = Get-Sha256 $a; $h2 = Get-Sha256 $other
            if ($leaf -like '*.msi' -and $h1 -ne $h2) {
                # WiX 5 writes a fresh package code and build time into every MSI; there is no switch to pin them. Report how much differs.
                $x = [IO.File]::ReadAllBytes($a); $y = [IO.File]::ReadAllBytes($other)
                $diff = -1; if ($x.Length -eq $y.Length) { $diff = 0; for ($i = 0; $i -lt $x.Length; $i++) { if ($x[$i] -ne $y[$i]) { $diff++ } } }
                Write-Host "  KNOWN NON-DETERMINISM  $leaf  ($diff differing bytes of $($x.Length); expected: package code GUID and timestamps in the summary stream)`n     first  $h1`n     second $h2" -ForegroundColor Yellow
                continue
            }
            if ($h1 -eq $h2) { Write-Host "  identical  $leaf  $h1" } else { $same = $false; Write-Host "  DIFFERENT  $leaf`n     first  $h1`n     second $h2" -ForegroundColor Red }
        }
        # Per-file comparison inside the staged layout to point at the culprit if the zip differs.
        foreach ($f in Get-ChildItem -LiteralPath $stage -Recurse -File) {
            $rel = $f.FullName.Substring($stage.Length + 1)
            $o = Join-Path $reproOut "stage\$rel"
            if (-not (Test-Path -LiteralPath $o) -or (Get-Sha256 $o) -ne (Get-Sha256 $f.FullName)) { Write-Host "  staged file differs: $rel" -ForegroundColor Red }
        }
        Remove-Item -LiteralPath $copy -Recurse -Force -ErrorAction SilentlyContinue
        if ($same) { Write-Host 'REPRODUCIBLE: the zip, SBOM and every staged file from a second build at a different path matched byte for byte (the MSI is excluded, see above).' -ForegroundColor Green }
        else { Write-Host 'NOT REPRODUCIBLE for the files marked DIFFERENT (see above).' -ForegroundColor Red }
    }
} finally { Pop-Location }
