# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Reports the resolved NuGet packages of Winora projects from `dotnet list package --include-transitive --format json`,
  joined with the license metadata read from each package's nuspec in the local NuGet cache.
.DESCRIPTION
  Read-only. Needs a prior restore (it passes --no-restore). Downloads nothing. Nothing is invented: a package whose nuspec
  has neither a license expression, a license file nor a license URL, or is missing from the cache, is reported as
  "UNKNOWN - manual review". Formats:
    Sbom      CycloneDX 1.5-style JSON (authored here, no CycloneDX tool). Deterministic: no timestamp, serial number derived from content.
    Markdown  table for THIRD-PARTY-NOTICES.md (shipped projects plus a separate section for test-only packages).
    Json      the raw joined records.
#>
[CmdletBinding()]
param(
    [ValidateSet('Sbom', 'Markdown', 'Json')][string]$Format = 'Json',
    [string[]]$Projects = @('src/Winora.Desktop/Winora.Desktop.csproj', 'src/Winora.Cli/Winora.Cli.csproj', 'src/Winora.Launch/Winora.Launch.csproj', 'src/Winora.Worker/Winora.Worker.csproj'),
    [string[]]$TestProjects = @(),
    [string]$OutFile,
    [string]$ProductVersion = '0.1.0'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }

function New-NameGuid([byte[]]$hash) {
    $h = ([BitConverter]::ToString($hash[0..15]) -replace '-', '').ToLowerInvariant()
    $variant = '89ab'[[Convert]::ToInt32($h[16].ToString(), 16) % 4]
    $h = $h.Substring(0, 12) + '5' + $h.Substring(13, 3) + $variant + $h.Substring(17, 15)
    '{0}-{1}-{2}-{3}-{4}' -f $h.Substring(0, 8), $h.Substring(8, 4), $h.Substring(12, 4), $h.Substring(16, 4), $h.Substring(20, 12)
}
function Get-ProjectPackages([string]$project) {
    $text = & dotnet list (Join-Path $repo $project) package --include-transitive --no-restore --format json
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed for $project" }
    $doc = ($text -join "`n") | ConvertFrom-Json
    $found = @{}
    foreach ($p in $doc.projects) {
        foreach ($fw in @($p.frameworks)) {
            foreach ($list in 'topLevelPackages', 'transitivePackages') {
                if (-not $fw.PSObject.Properties[$list]) { continue }
                foreach ($pkg in @($fw.$list)) {
                    $found["$($pkg.id)|$($pkg.resolvedVersion)"] = [pscustomobject]@{ Id = $pkg.id; Version = $pkg.resolvedVersion; Direct = ($list -eq 'topLevelPackages') }
                }
            }
        }
    }
    $found.Values
}

function Get-LicenseInfo([string]$id, [string]$version) {
    $dir = Join-Path $nugetRoot ("$id".ToLowerInvariant() + '/' + $version.ToLowerInvariant())
    $nuspec = Join-Path $dir ("$id".ToLowerInvariant() + '.nuspec')
    $info = [ordered]@{ Expression = $null; File = $null; Url = $null; ProjectUrl = $null; Authors = $null; Status = 'ok'; Sha512 = $null }
    if (-not (Test-Path $nuspec)) { $info.Status = 'UNKNOWN - package not in local cache'; return $info }
    $xml = [xml](Get-Content $nuspec -Raw)
    $md = $xml.package.metadata
    $info.Authors = if ($md.PSObject.Properties['authors']) { [string]$md.authors } else { $null }
    $info.ProjectUrl = if ($md.PSObject.Properties['projectUrl']) { [string]$md.projectUrl } else { $null }
    $lic = if ($md.PSObject.Properties['license']) { $md.license } else { $null }
    if ($lic -and $lic.type -eq 'expression') { $info.Expression = [string]$lic.'#text' }
    elseif ($lic -and $lic.type -eq 'file') { $info.File = [string]$lic.'#text' }
    if ($md.PSObject.Properties['licenseUrl']) { $info.Url = [string]$md.licenseUrl }
    if (-not $info.Expression) {
        $info.Status = if ($info.File) { 'MANUAL REVIEW - license is a file inside the package' } elseif ($info.Url) { 'MANUAL REVIEW - only a license URL (no SPDX expression)' } else { 'UNKNOWN - manual review' }
    }
    $hashFile = Get-ChildItem $dir -Filter '*.nupkg.sha512' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hashFile) {
        $bytes = [Convert]::FromBase64String((Get-Content $hashFile.FullName -Raw).Trim())
        $info.Sha512 = ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
    }
    $info
}

$shipped = @{}
foreach ($proj in $Projects) {
    foreach ($pkg in Get-ProjectPackages $proj) {
        $key = "$($pkg.Id)|$($pkg.Version)"
        if (-not $shipped.ContainsKey($key)) { $shipped[$key] = [pscustomobject]@{ Id = $pkg.Id; Version = $pkg.Version; Direct = $pkg.Direct; UsedBy = [System.Collections.Generic.List[string]]::new() } }
        $shipped[$key].UsedBy.Add((Split-Path $proj -Leaf).Replace('.csproj', ''))
        if ($pkg.Direct) { $shipped[$key].Direct = $true }
    }
}
$testOnly = @{}
foreach ($proj in $TestProjects) {
    foreach ($pkg in Get-ProjectPackages $proj) {
        $key = "$($pkg.Id)|$($pkg.Version)"
        if (-not $shipped.ContainsKey($key) -and -not $testOnly.ContainsKey($key)) { $testOnly[$key] = [pscustomobject]@{ Id = $pkg.Id; Version = $pkg.Version; Direct = $pkg.Direct; UsedBy = [System.Collections.Generic.List[string]]::new() } }
        if ($testOnly.ContainsKey($key)) { $testOnly[$key].UsedBy.Add((Split-Path $proj -Leaf).Replace('.csproj', '')) }
    }
}
function Join-Records($table) {
    $table.Values | Sort-Object { $_.Id.ToLowerInvariant() }, Version | ForEach-Object {
        $lic = Get-LicenseInfo $_.Id $_.Version
        [pscustomobject]@{ Id = $_.Id; Version = $_.Version; Direct = $_.Direct; UsedBy = (@($_.UsedBy | Sort-Object -Unique) -join ', '); License = $lic }
    }
}
$shippedRecords = @(Join-Records $shipped)
$testRecords = @(Join-Records $testOnly)

switch ($Format) {
    'Json' { $out = [pscustomobject]@{ shipped = $shippedRecords; testOnly = $testRecords } | ConvertTo-Json -Depth 6 }
    'Markdown' {
        $lines = [System.Collections.Generic.List[string]]::new()
        function Add-Table($records) {
            $lines.Add('| Package | Version | Direct | License (nuspec) | License URL / project | Status |')
            $lines.Add('| --- | --- | --- | --- | --- | --- |')
            foreach ($r in $records) {
                $l = $r.License
                $licText = if ($l.Expression) { $l.Expression } elseif ($l.File) { "file: $($l.File)" } else { '(none declared)' }
                $url = if ($l.Url) { $l.Url } elseif ($l.ProjectUrl) { $l.ProjectUrl } else { '' }
                $lines.Add("| $($r.Id) | $($r.Version) | $(if ($r.Direct) { 'yes' } else { 'no' }) | $licText | $url | $($l.Status) |")
            }
        }
        $lines.Add('### Packages resolved for the shipped applications (Desktop, CLI, Launch, Worker)'); $lines.Add('')
        Add-Table $shippedRecords
        if ($testRecords.Count -gt 0) { $lines.Add(''); $lines.Add('### Packages used only by test projects (not shipped)'); $lines.Add(''); Add-Table $testRecords }
        $out = ($lines -join "`n") + "`n"
    }
    'Sbom' {
        $components = foreach ($r in $shippedRecords) {
            $c = [ordered]@{
                type = 'library'; 'bom-ref' = "pkg:nuget/$($r.Id)@$($r.Version)"; name = $r.Id; version = $r.Version
                purl = "pkg:nuget/$($r.Id)@$($r.Version)"
            }
            if ($r.License.Authors) { $c.author = $r.License.Authors }
            if ($r.License.Sha512) { $c.hashes = @([ordered]@{ alg = 'SHA-512'; content = $r.License.Sha512 }) }
            if ($r.License.Expression) { $c.licenses = @([ordered]@{ expression = $r.License.Expression }) }
            else { $c.licenses = @(); $c.properties = @([ordered]@{ name = 'winora:license-status'; value = $r.License.Status }) }
            if ($r.License.Url) { $c.externalReferences = @([ordered]@{ type = 'license'; url = $r.License.Url }) }
            $c
        }
        $apps = foreach ($a in 'Winora.Desktop', 'Winora.Cli', 'Winora.Launch', 'Winora.Worker') {
            [ordered]@{ type = 'application'; 'bom-ref' = "app:$a"; name = $a; version = $ProductVersion; licenses = @([ordered]@{ license = [ordered]@{ id = 'GPL-3.0-only' } }) }
        }
        function Get-DepsOf([string]$app) { @($shippedRecords | Where-Object { ($_.UsedBy -split ', ') -contains $app } | ForEach-Object { "pkg:nuget/$($_.Id)@$($_.Version)" }) }
        $bom = [ordered]@{
            bomFormat = 'CycloneDX'; specVersion = '1.5'; version = 1
            metadata = [ordered]@{
                component = [ordered]@{ type = 'application'; 'bom-ref' = 'winora'; name = 'Winora'; version = $ProductVersion; licenses = @([ordered]@{ license = [ordered]@{ id = 'GPL-3.0-only' } }) }
                properties = @(
                    [ordered]@{ name = 'winora:note'; value = 'Authored by tools/package_report.ps1 from dotnet list package output and local nuspec files. Lists NuGet packages only. The .NET runtime is a prerequisite and is not part of this package. Native SQLite comes from the SQLitePCLRaw bundle packages listed here.' },
                    [ordered]@{ name = 'winora:signed'; value = 'false' }
                )
            }
            components = @($apps) + @($components)
            dependencies = @([ordered]@{ ref = 'winora'; dependsOn = @($apps | ForEach-Object { $_['bom-ref'] }) }) + @($apps | ForEach-Object { [ordered]@{ ref = $_['bom-ref']; dependsOn = (Get-DepsOf $_['name']) } })
        }
        $json = $bom | ConvertTo-Json -Depth 10
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($json))
        $guid = New-NameGuid $hash
        $ordered = [ordered]@{ bomFormat = 'CycloneDX'; specVersion = '1.5'; serialNumber = "urn:uuid:$guid"; version = 1; metadata = $bom.metadata; components = $bom.components; dependencies = $bom.dependencies }
        $out = $ordered | ConvertTo-Json -Depth 10
        $out += "`n"
    }
}
if ($OutFile) { [IO.File]::WriteAllText($OutFile, $out.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false)) } else { $out }
