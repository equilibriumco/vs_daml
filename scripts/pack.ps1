#Requires -Version 7.2
<#
.SYNOPSIS
    Release build plus packaging: the VSIX and every packable project's NuGet package
    end up in artifacts/packages.

.DESCRIPTION
    Projects are not packable unless they opt in with IsPackable=true, so this only
    produces NuGet packages for the projects meant to ship as packages.

    The VSIX is checked before it is accepted: it must target VS 2026 on x64 only, carry our
    own assemblies, must not carry assemblies Visual Studio provides itself (shipping our copy
    of those breaks binding in the IDE), and every other third-party assembly needs a codeBase
    entry in the pkgdef for exactly the version it ships.
#>
[CmdletBinding()]
param(
    [string] $VsInstallationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force

$root = Get-RepoRoot
$out = Join-Path $root 'artifacts\packages'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -VsInstallationPath $VsInstallationPath `
    -Targets 'Build', 'Pack' -Properties @{ PackageOutputPath = $out }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$vsix = @(Get-ChildItem (Join-Path $root 'src\Daml.VisualStudio\bin\Release') -Recurse -Filter '*.vsix' -ErrorAction SilentlyContinue)
if (-not $vsix) {
    Write-Host 'Release build produced no VSIX.' -ForegroundColor Red
    exit 1
}
if ($vsix.Count -ne 1) {
    Write-Host "Expected one VSIX, found $($vsix.Count): $($vsix.Name -join ', ')" -ForegroundColor Red
    exit 1
}

Import-Module (Join-Path $PSScriptRoot 'lib\VsixContents.psm1') -Force
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'src\Daml.VisualStudio\bin\Release\net48\Daml.VisualStudio.dll')).FileVersion
$vsixProblems = @(Test-Vsix $vsix[0].FullName -ExpectedVersion $version)
if ($vsixProblems) {
    $vsixProblems | ForEach-Object { Write-Host "VSIX check: $_" -ForegroundColor Red }
    exit 1
}
Write-Host "VSIX check passed: $($vsix[0].Name) $version (VS 2026 [18.0,), amd64, no VS-provided assemblies, codeBase for every third-party assembly)"
$vsix | Copy-Item -Destination $out

Get-ChildItem $out | ForEach-Object { Write-Host "  $($_.Name)" }
