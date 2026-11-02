# SPDX-License-Identifier: GPL-3.0-only
#Requires -Version 7.0
<#.SYNOPSIS
 Verifies the artifacts produced by tools/publish.ps1 without installing anything..DESCRIPTION
 Unzips the portable zip into a fresh temp directory and checks: SHA256SUMS, FILES.sha256, expected files, notice and license files,
 SBOM shape, forbidden content (PDB, tests, keys, user paths, secrets), unsigned state, zip normalization; then runs the unzipped CLI
 read-only (--version, discover startup --json, settings list --json with a hidden sandbox registry root and a temp database) and
 reads the Worker image as a file (PE header, manifest level, version); the Worker is never started. If an MSI exists it is opened READ-ONLY through the Windows Installer database API and inspected (tables, files,
 properties); msiexec is never invoked and the MSI is never installed. Exit code 0 only if every check passes..EXAMPLE
 pwsh -File tools/verify_dist.ps1 -DistRoot dist
#>
[CmdletBinding()]
param([string]$DistRoot, [switch]$KeepTemp)
Set-StrictMode -Version Latest
$env:WINORA_NO_LOG = "1"   # verification must not write logs into the real user data folder
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $DistRoot) { $DistRoot = Join-Path $repo 'dist' }
$DistRoot = (Resolve-Path $DistRoot).Path
$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}
function Warn([string]$what) { Write-Host "  warn  $what" -ForegroundColor Yellow; $warnings.Add($what) }
function Sha([string]$p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant() }

$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)
$version = @($props.Project.PropertyGroup | Where-Object { $_.PSObject.Properties['Version'] } | ForEach-Object { $_.Version })[0]
$stamp = "Winora-$version-win-x64"
Write-Host "Verifying $DistRoot (version $version)" -ForegroundColor Cyan

# ---- SHA256SUMS ----
Write-Host 'SHA256SUMS'
$sumsPath = Join-Path $DistRoot 'SHA256SUMS'
Check (Test-Path $sumsPath) 'SHA256SUMS exists'
$listed = @{}
foreach ($line in Get-Content $sumsPath) {
    if ($line -notmatch '^([0-9a-f]{64}) \*(.+)$') { Check $false "SHA256SUMS line is well formed: '$line'"; continue }
    $listed[$Matches[2]] = $Matches[1]
    $file = Join-Path $DistRoot $Matches[2]
    Check ((Test-Path $file) -and (Sha $file) -eq $Matches[1]) "hash matches for $($Matches[2])"
}
$zipPath = Join-Path $DistRoot "$stamp-portable.zip"
Check ($listed.ContainsKey((Split-Path $zipPath -Leaf))) 'portable zip is listed in SHA256SUMS'

# ---- unzip into a fresh temp directory ----
$temp = Join-Path ([IO.Path]::GetTempPath()) ("winora-verify-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
$sandboxGuid = [Guid]::NewGuid().ToString()
try {
    Write-Host 'Zip structure'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
        Check (@($names | Where-Object { $_.StartsWith('/') -or $_.Contains('..') -or $_.Contains('\') -or $_ -match '^[A-Za-z]:' }).Count -eq 0) 'no absolute, traversal or backslash entry names'
        Check (@($names | Where-Object { -not $_.StartsWith("$stamp/") }).Count -eq 0) "every entry is under $stamp/"
        $sortedNames = [string[]]$names.Clone(); [Array]::Sort($sortedNames, [StringComparer]::Ordinal)
        Check (($names -join "`n") -ceq ($sortedNames -join "`n")) 'entries are in fixed ordinal order'
        $times = @($zip.Entries | ForEach-Object { $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') } | Sort-Object -Unique)
        Check ($times.Count -eq 1 -and $times[0] -eq '2000-01-01 00:00:00') "all timestamps normalized to 2000-01-01 00:00:00 (found: $($times -join ', '))"
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $temp)
    $root = Join-Path $temp $stamp
    Check (Test-Path $root) 'extracted root folder exists'

    Write-Host 'Contents'
    $expected = @('desktop/Winora.Desktop.exe', 'desktop/Winora.Desktop.dll', 'desktop/Winora.Desktop.runtimeconfig.json', 'desktop/Winora.Desktop.deps.json',
        'cli/Winora.Cli.exe', 'cli/Winora.Cli.dll', 'cli/Winora.Cli.runtimeconfig.json', 'launch/Winora.Launch.exe', 'launch/Winora.Launch.dll', 'launch/Winora.Launch.runtimeconfig.json', 'launch/Winora.Instance.dll', 'worker/Winora.Worker.exe', 'worker/Winora.Worker.dll', 'worker/Winora.Worker.runtimeconfig.json',
        'desktop/Winora.Domain.dll', 'desktop/Winora.Application.dll', 'desktop/Winora.Presentation.dll', 'desktop/Winora.Infrastructure.Persistence.dll', 'desktop/Winora.Infrastructure.Windows.dll', 'desktop/Wpf.Ui.dll', 'cli/Winora.Domain.dll',
        'desktop/e_sqlite3.dll', 'cli/runtimes/win-x64/native/e_sqlite3.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'Check-Prerequisites.ps1', 'sbom.cdx.json', 'FILES.sha256',
        'desktop/fa/Winora.Presentation.resources.dll', 'cli/fa/Winora.Cli.resources.dll')
    foreach ($e in $expected) { Check (Test-Path (Join-Path $root $e)) "present: $e" }
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File)
    Check (@($files | Where-Object { $_.FullName.Substring($root.Length + 1) -like '*\runtimes\*' -and $_.FullName -notlike '*\runtimes\win-x64\*' }).Count -eq 0) 'only runtimes/win-x64 natives are shipped'

    Write-Host 'FILES.sha256'
    $inner = @{}
    foreach ($line in Get-Content (Join-Path $root 'FILES.sha256')) { if ($line -match '^([0-9a-f]{64}) \*(.+)$') { $inner[$Matches[2]] = $Matches[1] } }
    $mismatch = 0; $unlisted = 0
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($rel -eq 'FILES.sha256') { continue }
        if (-not $inner.ContainsKey($rel)) { $unlisted++; continue }
        if ($inner[$rel] -ne (Sha $f.FullName)) { $mismatch++; Write-Host "     mismatch: $rel" }
    }
    Check ($mismatch -eq 0) 'every file matches FILES.sha256'
    Check ($unlisted -eq 0) 'every file (except FILES.sha256) is listed in FILES.sha256'
    Check (@($inner.Keys | Where-Object { -not (Test-Path (Join-Path $root $_.Replace('/', '\'))) }).Count -eq 0) 'FILES.sha256 lists no missing file'

    Write-Host 'Notices and licenses'
    $license = Get-Content (Join-Path $root 'LICENSE') -Raw
    Check ($license -match 'GNU GENERAL PUBLIC LICENSE' -and $license -match 'Version 3') 'LICENSE is the GPL v3 text'
    $notice = Get-Content (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Raw
    Check ($notice -match 'SPDX-License-Identifier: GPL-3\.0-only') 'notice file carries the GPL-3.0-only SPDX identifier'
    $sbom = Get-Content (Join-Path $root 'sbom.cdx.json') -Raw | ConvertFrom-Json
    Check ($sbom.bomFormat -eq 'CycloneDX' -and $sbom.specVersion -eq '1.5') 'SBOM is CycloneDX 1.5 shaped JSON'
    $libs = @($sbom.components | Where-Object { $_.type -eq 'library' })
    Check ($libs.Count -gt 0) "SBOM lists $($libs.Count) package components"
    $missingFromNotice = @($libs | Where-Object { $notice.IndexOf($_.name, [StringComparison]::OrdinalIgnoreCase) -lt 0 } | ForEach-Object { "$($_.name) $($_.version)" })
    Check ($missingFromNotice.Count -eq 0) "every SBOM package is named in THIRD-PARTY-NOTICES.md$(if ($missingFromNotice) { ': ' + ($missingFromNotice -join ', ') })"
    foreach ($l in $libs | Where-Object { @($_.licenses).Count -eq 0 }) { Warn "SBOM package without license expression (manual review): $($l.name) $($l.version)" }
    Check (@(Get-ChildItem (Join-Path $root 'third-party-licenses') -File -ErrorAction SilentlyContinue).Count -gt 0) 'third-party-licenses folder has license texts'

    Write-Host 'Forbidden content'
    $bad = @($files | Where-Object { $_.Name -match '\.(pdb|trx|user|pfx|snk|key|pem|p12|cer|suo|binlog|log|db|sqlite)$|\.Tests\b|^xunit|^testhost|Microsoft\.TestPlatform|Microsoft\.CodeCoverage|^\.env|secret|credential' })
    Check ($bad.Count -eq 0) "no PDB, test, key, database, log or secret-named files$(if ($bad) { ': ' + (($bad | ForEach-Object Name) -join ', ') })"
    $userName = [regex]::Escape($env:USERNAME); $profileDir = $env:USERPROFILE
    $needles = @($profileDir, "\Users\$env:USERNAME", [IO.Path]::GetTempPath().TrimEnd('\'), $repo, 'source\repos\Winora') | Where-Object { $_ } | Select-Object -Unique
    $latin1 = [Text.Encoding]::Latin1
    $pathHits = [System.Collections.Generic.List[string]]::new(); $secretHits = [System.Collections.Generic.List[string]]::new(); $genericUsers = [System.Collections.Generic.List[string]]::new()
    foreach ($f in $files) {
        $text = $latin1.GetString([IO.File]::ReadAllBytes($f.FullName))
        $rel = $f.FullName.Substring($root.Length + 1)
        foreach ($n in $needles) {
            $ascii = $n; $wide = [string]::Join("`0", $n.ToCharArray())
            if ($text.IndexOf($ascii, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $text.IndexOf($wide, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $pathHits.Add("$rel contains '$n'") }
        }
        if ($text -match 'C:\\Users\\' -or $text -match "C:`0\\`0U`0s`0e`0r`0s`0\\") { $genericUsers.Add($rel) }
        if ($text -match '-----BEGIN [A-Z ]*PRIVATE KEY-----' -or $text -match 'ghp_[A-Za-z0-9]{30,}' -or $text -match 'AKIA[0-9A-Z]{16}' -or $text -match '(?i)password\s*[:=]\s*\S{6,}') { $secretHits.Add($rel) }
    }
    Check ($pathHits.Count -eq 0) "no build-machine user, temp or repo path embedded$(if ($pathHits.Count) { ': ' + (($pathHits | Select-Object -First 5) -join '; ') })"
    $vmOnly = [System.Collections.Generic.List[string]]::new()
    foreach ($f in $files) {
        $text = $latin1.GetString([IO.File]::ReadAllBytes($f.FullName))
        foreach ($n in 'Winora VM Test', 'winora-vmtrust-test-key', 'WinoraVm') {
            $wide = [string]::Join("`0", $n.ToCharArray())
            if ($text.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $text.IndexOf($wide, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $vmOnly.Add("$($f.FullName.Substring($root.Length + 1)) contains '$n'") }
        }
    }
    Check ($vmOnly.Count -eq 0) "no VM-only test identity or pin in any shipped file$(if ($vmOnly.Count) { ': ' + (($vmOnly | Select-Object -First 5) -join '; ') })"
    foreach ($g in $genericUsers) { Warn "contains the text 'C:\Users\' (inspect, probably library text): $g" }
    Check ($secretHits.Count -eq 0) "no private key, token or password pattern$(if ($secretHits.Count) { ': ' + ($secretHits -join ', ') })"

    Write-Host 'Signature state (must be unsigned in 0.x)'
    $signed = @(Get-ChildItem -LiteralPath $root -Recurse -Include 'Winora*.exe', 'Winora*.dll' | Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'NotSigned' })
    Check ($signed.Count -eq 0) 'Winora executables are not signed by this build'

    # ---- run the unzipped CLI: read-only commands only ----
    Write-Host 'Unzipped CLI (read-only commands, temp database, hidden sandbox registry root)'
    $cli = Join-Path $root 'cli\Winora.Cli.exe'
    $db = Join-Path $temp 'verify-journal.db'
    $ver = (& $cli --version) -join ''
    Check ($LASTEXITCODE -eq 0 -and $ver.Trim() -eq "Winora $version") "winora --version prints 'Winora $version' (got '$($ver.Trim())')"
    $out = (& $cli discover startup --json --limit 3) -join "`n"
    $discoverExit = $LASTEXITCODE
    try { $doc = $out | ConvertFrom-Json } catch { $doc = $null }
    Check ($discoverExit -eq 0 -and $null -ne $doc -and $doc.readOnly -eq $true -and $doc.schemaVersion -eq 1 -and $doc.kind -eq 'startup') 'discover startup --json: exit 0, readOnly true, schemaVersion 1, kind startup'
    $out = (& $cli settings list --json --database $db --sandbox-root $sandboxGuid) -join "`n"
    $listExit = $LASTEXITCODE
    try { $doc = $out | ConvertFrom-Json } catch { $doc = $null }
    Check ($listExit -eq 0 -and $null -ne $doc -and $doc.kind -eq 'settings.list' -and $doc.count -ge 14) "settings list --json (sandbox root, temp database): exit 0, at least the 14 base catalog settings (got $(if ($doc) { $doc.count } else { 'no JSON' }))"
    # A write command without --yes and without a terminal must refuse (exit 2) before doing anything. Sandbox root only.
    $psi = [Diagnostics.ProcessStartInfo]::new($cli)
    foreach ($a in 'settings', 'apply', 'explorer.show_file_extensions', 'show', '--database', $db, '--sandbox-root', $sandboxGuid) { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
    $proc = [Diagnostics.Process]::Start($psi); $proc.StandardInput.Close()
    $stdout = $proc.StandardOutput.ReadToEnd(); [void]$proc.StandardError.ReadToEnd(); $proc.WaitForExit()
    Check ($proc.ExitCode -eq 2 -and $stdout -eq '') 'settings apply without --yes and without a terminal refuses with exit 2 and no output'
    # ---- Worker: static PE inspection only. The worker image is NEVER started on this host. ----
    Write-Host 'Worker image (read as a file; never executed)'
    $worker = Join-Path $root 'worker\Winora.Worker.exe'
    $workerBytes = [IO.File]::ReadAllBytes($worker)
    $stream = [IO.MemoryStream]::new($workerBytes)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $hdr = $pe.PEHeaders
        Check ($hdr.CoffHeader.Machine -eq [System.Reflection.PortableExecutable.Machine]::Amd64) "worker machine type is x64 (got $($hdr.CoffHeader.Machine))"
        Check ($hdr.PEHeader.Subsystem -eq [System.Reflection.PortableExecutable.Subsystem]::WindowsGui) "worker subsystem is Windows GUI, no console (an unpublished design note 14.1; got $($hdr.PEHeader.Subsystem))"
    } finally { $pe.Dispose(); $stream.Dispose() }
    $workerVer = (Get-Item -LiteralPath $worker).VersionInfo.FileVersion
    Check ($workerVer -like "$version*") "worker file version starts with $version (got '$workerVer')"
    # The manifest is stored as UTF-8 text in the RT_MANIFEST resource; read the requested level from the raw bytes.
    $manifestText = [Text.Encoding]::Latin1.GetString($workerBytes)
    $levelMatch = [regex]::Match($manifestText, 'requestedExecutionLevel\s+level\s*=\s*["'']([A-Za-z]+)["'']')
    if ($levelMatch.Success) { Check ($levelMatch.Groups[1].Value -ceq 'asInvoker') "worker manifest requests asInvoker (got '$($levelMatch.Groups[1].Value)')" }
    else { Warn 'worker image has no embedded requestedExecutionLevel; Windows then defaults to asInvoker (src/Winora.Worker/app.manifest is the source of truth)' }
    Check ((Get-AuthenticodeSignature -LiteralPath $worker).Status -eq 'NotSigned') 'worker executable is not signed by this build'

    # ---- MSI, read-only ----
    $msi = Join-Path $DistRoot "$stamp.msi"
    if (Test-Path $msi) {
        Write-Host 'MSI (opened read-only through the Windows Installer database API; never installed)'
        $before = Sha $msi
        $installer = New-Object -ComObject WindowsInstaller.Installer
        function Invoke-Com($obj, [string]$name, $arguments) { [System.__ComObject].InvokeMember($name, [Reflection.BindingFlags]::InvokeMethod, $null, $obj, $arguments) }
        function Get-Prop($obj, [string]$name, $arguments = $null) { [System.__ComObject].InvokeMember($name, [Reflection.BindingFlags]::GetProperty, $null, $obj, $arguments) }
        function Get-MsiRows($db, [string]$sql) {
            $view = Invoke-Com $db 'OpenView' @($sql); Invoke-Com $view 'Execute' @() | Out-Null
            $rows = @(); while ($true) {
                $rec = Invoke-Com $view 'Fetch' @(); if ($null -eq $rec) { break }
                $count = Get-Prop $rec 'FieldCount'; $rows += , @(1..$count | ForEach-Object { Get-Prop $rec 'StringData' @([int]$_) })
            }
            Invoke-Com $view 'Close' $null | Out-Null; , $rows
        }
        $msiDb = Invoke-Com $installer 'OpenDatabase' @([string]$msi.ToString(), [int]0)   # 0 = msiOpenDatabaseModeReadOnly
        $tableRows = Get-MsiRows $msiDb 'SELECT `Name` FROM `_Tables`'
        $tables = @($tableRows | ForEach-Object { $_[0] })
        Write-Host "     tables: $($tables -join ', ')"
        $props = @{}; foreach ($r in (Get-MsiRows $msiDb 'SELECT `Property`,`Value` FROM `Property`')) { $props[$r[0]] = $r[1] }
        $launchRows = Get-MsiRows $msiDb 'SELECT `Condition` FROM `LaunchCondition`'
        $launch = @($launchRows | ForEach-Object { $_[0] })
        Check (@($launch | Where-Object { $_ -match 'WINORA_WINDOWS_BUILD' }).Count -eq 1 -and @($launch | Where-Object { $_ -match 'WindowsBuild|VersionNT\s*[<>]' }).Count -eq 0) "MSI has the registry-based Windows 11 launch condition and none that uses WindowsBuild or VersionNT comparisons (the installer reports Windows 8.1 values for them on Windows 11): $($launch -join ' ; ')"
        Check ($props['ProductVersion'] -eq $version) "MSI ProductVersion is $version (got $($props['ProductVersion']))"
        Check ($props['UpgradeCode'] -eq '{912F5E18-EB3C-4C58-A6C7-4FE958DA71BE}') 'MSI UpgradeCode is the permanent value'
        Check ($props['Manufacturer'] -and $props['ProductName'] -eq 'Winora') "MSI ProductName Winora, Manufacturer '$($props['Manufacturer'])' (owner decision pending)"
        Check (-not $props.ContainsKey('ALLUSERS') -or [string]::IsNullOrEmpty($props['ALLUSERS'])) 'MSI is per-user (ALLUSERS not set to 1)'
        $msiFiles = Get-MsiRows $msiDb 'SELECT `FileName`,`FileSize` FROM `File`'
        Check ($msiFiles.Count -eq ($files.Count)) "MSI file table has $($msiFiles.Count) files; zip layout has $($files.Count)"
        # Same names and sizes as the zip contents (guards against a stale MSI built from an older stage folder).
        $fromMsi = @($msiFiles | ForEach-Object { ($_[0] -split '\|')[-1] + ':' + $_[1] } | Sort-Object)
        $fromZip = @($files | ForEach-Object { $_.Name + ':' + $_.Length } | Sort-Object)
        Check (($fromMsi -join "`n") -ceq ($fromZip -join "`n")) 'MSI file names and sizes equal the zip contents'        if ($tables -contains 'CustomAction') { $ca = Get-MsiRows $msiDb 'SELECT `Action` FROM `CustomAction`'; Check ($ca.Count -eq 0) "MSI has no custom actions (found $($ca.Count))" } else { Check $true 'MSI has no CustomAction table (no custom code runs at install)' }
        Check ($tables -contains 'Upgrade') 'MSI has an Upgrade table (major upgrade)'
        Check ($tables -contains 'Shortcut') 'MSI has a Shortcut table (Start menu entry)'
        $reg = Get-MsiRows $msiDb 'SELECT `Root`,`Key`,`Name` FROM `Registry`'
        Check (@($reg | Where-Object { $_[0] -ne '1' }).Count -eq 0) "MSI registry rows are HKCU only (Root 1): $(($reg | ForEach-Object { $_[1] }) -join '; ')"
        foreach ($t in 'ServiceInstall', 'ServiceControl', 'Environment', 'Extension', 'Class', 'Verb') { Check (-not ($tables -contains $t)) "MSI has no $t table" }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($msiDb); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
        Check ((Sha $msi) -eq $before) 'inspection did not modify the MSI'
        Warn 'This check reads the MSI; it does not install it.'
    } else { Warn 'no MSI in dist (built with -SkipInstaller?)' }
    # ---- per-machine MSI (publish.ps1 -Machine), read-only ----
    $machineMsi = Join-Path $DistRoot "$stamp-machine.msi"
    if (Test-Path $machineMsi) {
        Write-Host 'Per-machine MSI (opened read-only through the Windows Installer database API; never installed)'
        $beforeMachine = Sha $machineMsi
        $installerM = New-Object -ComObject WindowsInstaller.Installer
        function Invoke-ComM($obj, [string]$name, $arguments) { [System.__ComObject].InvokeMember($name, [Reflection.BindingFlags]::InvokeMethod, $null, $obj, $arguments) }
        function Get-PropM($obj, [string]$name, $arguments = $null) { [System.__ComObject].InvokeMember($name, [Reflection.BindingFlags]::GetProperty, $null, $obj, $arguments) }
        function Get-MsiRowsM($db, [string]$sql) {
            $view = Invoke-ComM $db 'OpenView' @($sql); Invoke-ComM $view 'Execute' @() | Out-Null
            $rows = @(); while ($true) {
                $rec = Invoke-ComM $view 'Fetch' @(); if ($null -eq $rec) { break }
                $count = Get-PropM $rec 'FieldCount'; $rows += , @(1..$count | ForEach-Object { Get-PropM $rec 'StringData' @([int]$_) })
            }
            Invoke-ComM $view 'Close' $null | Out-Null; , $rows
        }
        $dbM = Invoke-ComM $installerM 'OpenDatabase' @([string]$machineMsi.ToString(), [int]0)
        $tableRowsM = Get-MsiRowsM $dbM 'SELECT `Name`,`Name` FROM `_Tables`'
        $tablesM = @($tableRowsM | ForEach-Object { $_[0] })
        $propsM = @{}; foreach ($r in (Get-MsiRowsM $dbM 'SELECT `Property`,`Value` FROM `Property`')) { $propsM[$r[0]] = $r[1] }
        Check ($propsM['ProductVersion'] -eq $version) "per-machine MSI ProductVersion is $version (got $($propsM['ProductVersion']))"
        Check ($propsM['UpgradeCode'] -eq '{E3A7B5C1-9D42-4F6A-8B31-5C2E7D9A1F40}' -and $propsM['UpgradeCode'] -ne '{912F5E18-EB3C-4C58-A6C7-4FE958DA71BE}') 'per-machine MSI has its own permanent UpgradeCode, different from the per-user package'
        Check ($propsM['ALLUSERS'] -eq '1') 'per-machine MSI sets ALLUSERS=1'
        Check ($propsM['ProductName'] -eq 'Winora (all users)') "per-machine MSI ProductName (got '$($propsM['ProductName'])')"
        Check (-not $propsM.ContainsKey('INSTALLDIR') -and -not $propsM.ContainsKey('INSTALLFOLDER') -and -not $propsM.ContainsKey('TARGETDIR_OVERRIDE')) 'per-machine MSI has no public install-folder property (fixed Program Files folder)'
        $dirsM = Get-MsiRowsM $dbM 'SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`'
        Check (@($dirsM | Where-Object { $_[0] -ceq 'WinoraInstallDir' -and $_[1] -ceq 'ProgramFiles64Folder' }).Count -eq 1) 'the install folder is a child of the 64-bit Program Files folder'
        Check (@($dirsM | Where-Object { $_[0] -ceq 'WinoraRunDir' -and $_[1] -ceq 'WinoraInstallDir' }).Count -eq 1) 'the empty run folder exists under the install folder'
        if ($tablesM -contains 'CustomAction') { $caM = Get-MsiRowsM $dbM 'SELECT `Action` FROM `CustomAction`'; Check ($caM.Count -eq 0) "per-machine MSI has no custom actions ($($caM.Count))" } else { Check $true 'per-machine MSI has no custom action table' }
        foreach ($t in 'ServiceInstall', 'ServiceControl', 'Environment', 'Extension', 'Class', 'Verb', 'LockPermissions', 'MsiLockPermissionsEx') { Check (-not ($tablesM -contains $t)) "per-machine MSI has no $t table (no ACE is added, nothing is registered)" }
        $regM = Get-MsiRowsM $dbM 'SELECT `Root`,`Key`,`Name` FROM `Registry`'
        Check (@($regM | Where-Object { $_[0] -ne '2' }).Count -eq 0) 'per-machine MSI registry rows are HKLM only (Root 2)'
        $allowedKeys = @('SOFTWARE\Winora\Install', 'SYSTEM\CurrentControlSet\Services\EventLog\Application\Winora')
        Check (@($regM | Where-Object { $allowedKeys -notcontains $_[1] }).Count -eq 0) "per-machine MSI authors only the install record and the Event Log source keys: $(($regM | ForEach-Object { $_[1] + '\' + $_[2] }) -join '; ')"
        Check (@($regM | Where-Object { $_[1] -match '(?i)\\Trust(\\|$)' -or $_[1] -ieq 'SOFTWARE\Winora' }).Count -eq 0) 'per-machine MSI never authors the Trust key or the parent HKLM\SOFTWARE\Winora'
        $compsM = Get-MsiRowsM $dbM 'SELECT `Component`,`Attributes` FROM `Component`'
        Check (@($compsM | Where-Object { ([int]$_[1] -band 16) -ne 0 }).Count -eq 0) 'no component is Permanent (uninstall removes the install record)'
        $fileRowsM = Get-MsiRowsM $dbM 'SELECT `FileName`,`FileSize` FROM `File`'
        $filesM = @($fileRowsM | ForEach-Object { ($_[0] -split '\|')[-1] })
        Check (@($filesM | Where-Object { $_ -ceq 'Winora.Worker.exe' }).Count -eq 1) 'the worker executable is in the package once'
        Check (@($filesM | Where-Object { $_ -like 'Winora.Worker*' -and $_ -cne 'Winora.Worker.exe' }).Count -eq 0) 'no managed worker assembly, runtime configuration or dependency list ships (NativeAOT image only)'
        Check (@($filesM | Where-Object { $_ -like 'MachineDefinitions*' }).Count -eq 0) 'no definition set file ships (the release set is empty)'
        Check ((Get-AuthenticodeSignature -LiteralPath $machineMsi).Status -eq 'NotSigned') 'per-machine MSI is not signed by this build'
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($dbM); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installerM)
        Check ((Sha $machineMsi) -eq $beforeMachine) 'inspection did not modify the per-machine MSI'
        Warn 'This check reads the per-machine MSI; it does not install it.'
    }
} finally {
    # The sandbox registry root is only read by `settings list`; delete it if a code path created it (exact guid-named key only).
    try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("Software\WinoraTests\$sandboxGuid", $false) } catch { }
    if (-not $KeepTemp -and (Test-Path $temp)) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host ''
if ($warnings.Count) { Write-Host "$($warnings.Count) warning(s)." -ForegroundColor Yellow }
if ($failures.Count) { Write-Host "$($failures.Count) check(s) FAILED." -ForegroundColor Red; exit 1 }
Write-Host 'verify_dist: all checks passed. Artifacts are UNSIGNED.' -ForegroundColor Green
