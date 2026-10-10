#Requires -Version 7.2
<#
.SYNOPSIS
    Release build plus packaging: the VSIX and every packable project's NuGet package
    end up in artifacts/packages.

.DESCRIPTION
    Projects are not packable unless they opt in with IsPackable=true, so this only
    produces NuGet packages for the projects meant to ship as packages.
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
$vsix | Copy-Item -Destination $out

Get-ChildItem $out | ForEach-Object { Write-Host "  $($_.Name)" }
