# SPDX-License-Identifier: GPL-3.0-only
# Shipped in the portable zip as Check-Prerequisites.ps1. Read-only: it runs `dotnet --list-runtimes`, reads the Windows build number,
# and prints what it found. It installs nothing and changes nothing. Works in Windows PowerShell 5.1 and PowerShell 7.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$problems = 0

$build = [Environment]::OSVersion.Version.Build
if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -and $env:PROCESSOR_ARCHITEW6432 -ne 'AMD64') {
    Write-Host "FAIL  This Winora build is x64 only. Processor architecture: $($env:PROCESSOR_ARCHITECTURE). ARM64 is not supported." -ForegroundColor Red; $problems++
} else { Write-Host 'OK    x64 processor.' }
if ($build -lt 22000) {
    Write-Host "FAIL  Windows build $build is older than Windows 11 (22000). Winora supports Windows 11 only; Windows 10 is not supported." -ForegroundColor Red; $problems++
} elseif ($build -lt 22621) {
    Write-Host "WARN  Windows build $build (Windows 11 21H2). Some settings need build 22621 or later and will show as unsupported." -ForegroundColor Yellow
} else { Write-Host "OK    Windows build $build." }

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host 'FAIL  dotnet was not found on PATH. Install the .NET 10 Desktop Runtime (x64) from https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Red; $problems++
} else {
    $runtimes = & dotnet --list-runtimes
    $core = @($runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.' })
    $desktop = @($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' })
    if ($core.Count -gt 0) { Write-Host "OK    $($core[-1])" } else { Write-Host 'FAIL  Microsoft.NETCore.App 10.x is missing (needed by Winora.Cli).' -ForegroundColor Red; $problems++ }
    if ($desktop.Count -gt 0) { Write-Host "OK    $($desktop[-1])" } else { Write-Host 'FAIL  Microsoft.WindowsDesktop.App 10.x is missing (needed by Winora.Desktop). Install the .NET 10 Desktop Runtime (x64).' -ForegroundColor Red; $problems++ }
}
if ($problems -eq 0) { Write-Host 'All prerequisites found.' } else { Write-Host "$problems problem(s) found."; exit 1 }
