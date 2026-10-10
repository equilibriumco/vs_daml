#Requires -Version 7.2
<#
.SYNOPSIS
    Restores and builds Daml.VisualStudio.sln with VS 2026 MSBuild.

.DESCRIPTION
    Picks the build instance (MSBuild 18.x compatible with the SDK in
    global.json), builds with warnings as errors and writes artifacts/logs/build.binlog.
    The exit code is MSBuild's.

.EXAMPLE
    pwsh scripts/build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [ValidateSet('quiet', 'minimal', 'normal', 'detailed', 'diagnostic')]
    [string] $Verbosity = 'minimal',

    # Use this installation instead of auto-selecting one (installationPath from vswhere).
    [string] $VsInstallationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force

$root = Get-RepoRoot
$vs = Select-BuildVsInstance -InstallationPath $VsInstallationPath
$preview = if ($vs.IsPrerelease) { ' [prerelease]' } else { '' }
Write-Host "Build instance: $($vs.DisplayName) $($vs.Version)$preview, MSBuild $($vs.MSBuildVersion), .NET SDK $($vs.SdkVersion)"

$logDir = Join-Path $root 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# NuGet audit findings stay warnings (see Directory.Build.props); everything else fails the build.
$msbuildArgs = @(
    (Join-Path $root 'Daml.VisualStudio.sln')
    '-restore'
    "-p:Configuration=$Configuration"
    "-verbosity:$Verbosity"
    '-nologo'
    '-maxCpuCount'
    '-nodeReuse:false'
    '-warnAsError'
    '-warnNotAsError:NU1901;NU1902;NU1903;NU1904'
    "-binaryLogger:$(Join-Path $logDir 'build.binlog')"
)

& $vs.MSBuildPath @msbuildArgs
exit $LASTEXITCODE
