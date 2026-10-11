#Requires -Version 7.2
<#
.SYNOPSIS
    Starts the experimental instance of a test IDE, optionally opening a folder or solution.

.EXAMPLE
    pwsh scripts/run-exp.ps1 -Path samples\single-package
#>
[CmdletBinding()]
param(
    # Folder or .sln to open; relative paths resolve against the repository root.
    [string] $Path,

    [string] $VsInstallationPath,

    # Block until the IDE exits (handy for scripted demos).
    [switch] $Wait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force

$vs = Select-TestVsInstance -InstallationPath $VsInstallationPath
$devenvArgs = @('/rootSuffix', 'Exp')

if ($Path) {
    $target = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path (Get-RepoRoot) $Path }
    if (-not (Test-Path $target)) { throw "'$target' does not exist." }
    $devenvArgs += "`"$((Resolve-Path $target).Path)`""
}

Write-Host "Starting $($vs.DisplayName) $($vs.Version) $($devenvArgs -join ' ')"
$process = Start-Process $vs.DevenvPath -ArgumentList $devenvArgs -PassThru -Wait:$Wait
if ($Wait) { exit $process.ExitCode }
