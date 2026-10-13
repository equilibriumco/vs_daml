#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the Pester tests of the build and test scripts themselves (test/scripts.Tests).

.DESCRIPTION
    The gate logic in scripts/lib decides whether a test run counts as green; these tests feed
    it hand-made results. Works with the Pester that ships with Windows (3.x) and with newer ones.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Import-Module Pester -ErrorAction Stop
$result = Invoke-Pester -Path (Join-Path $root 'test\scripts.Tests') -PassThru
if ($result.FailedCount -gt 0) {
    Write-Error "$($result.FailedCount) script test(s) failed."
    exit 1
}
Write-Host "Script tests: $($result.PassedCount) passed."
