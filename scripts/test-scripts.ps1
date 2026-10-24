#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the Pester tests of the build and test scripts themselves (test/scripts.Tests).

.DESCRIPTION
    The gate logic in scripts/lib decides whether a test run counts as green; these tests feed
    it hand-made results. Written for Pester 3.4 (shipped with Windows) and 4.x (CI installs 4.10.1);
    Pester 5 changed the syntax, so a newer one is never picked.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Import-Module Pester -MaximumVersion 4.99 -ErrorAction Stop
$result = Invoke-Pester -Path (Join-Path $root 'test\scripts.Tests') -PassThru
if ($result.PassedCount -eq 0 -or $result.SkippedCount -gt 0 -or $result.PendingCount -gt 0 -or $result.InconclusiveCount -gt 0) {
    throw "Script tests need executed, unskipped evidence (passed=$($result.PassedCount), skipped=$($result.SkippedCount), pending=$($result.PendingCount), inconclusive=$($result.InconclusiveCount))."
}
if ($result.FailedCount -gt 0) {
    Write-Error "$($result.FailedCount) script test(s) failed."
    exit 1
}
Write-Host "Script tests: $($result.PassedCount) passed."
