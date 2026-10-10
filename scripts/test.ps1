#Requires -Version 7.2
<#
.SYNOPSIS
    Runs test projects by category and writes TRX (and coverage) per target framework.

.DESCRIPTION
    Builds through build.ps1 (unless -NoBuild), then runs every test project under test/
    with a vstest filter on the Category trait. Results go to artifacts/test/<Category>/.

    Local runs are lenient: a category without tests is reported as N/A and skipped tests
    are allowed. -RequiredTools turns the run into a gate check for -Milestone: missing
    tools, a required category with zero executed tests, any skip or any failure all make
    the script exit non-zero. Test code sees DAML_REQUIRED_TOOLS=1 and must fail instead of
    skipping when a tool is missing.

.EXAMPLE
    pwsh scripts/test.ps1 -Category Unit
.EXAMPLE
    pwsh scripts/test.ps1 -Category All -Milestone M0 -RequiredTools
#>
[CmdletBinding()]
param(
    [ValidateSet('Unit', 'Editor', 'Contract', 'MSBuild', 'Integration', 'Grammar', 'Perf', 'All')]
    [string] $Category = 'Unit',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    # MSBuild tests come in two flavours: Fake (FakeDpm, runs anywhere) and Real (needs dpm + JDK).
    [ValidateSet('Fake', 'Real', 'All')]
    [string] $Backend = 'Fake',

    [ValidatePattern('^M\d$')]
    [string] $Milestone,

    [switch] $RequiredTools,
    [switch] $NoBuild,
    [switch] $NoCoverage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force

$root = Get-RepoRoot
$resultsRoot = Join-Path $root 'artifacts\test'
$vstestCategories = 'Unit', 'Editor', 'Contract', 'MSBuild', 'Integration'

$gate = $null
if ($Milestone) {
    $gates = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'test-gates.psd1')
    if (-not $gates.ContainsKey($Milestone)) { throw "No gate definition for $Milestone in test-gates.psd1." }
    $gate = $gates[$Milestone]
}
if ($RequiredTools -and -not $gate) { throw '-RequiredTools needs -Milestone to know what is required.' }

$categories = if ($Category -ne 'All') { @($Category) }
              elseif ($gate) { @($gate.Categories) }
              else { $vstestCategories }
$required = if ($RequiredTools) { @($gate.Categories) } else { @() }

$problems = [System.Collections.Generic.List[string]]::new()

if ($RequiredTools) {
    $env:DAML_REQUIRED_TOOLS = '1'
    foreach ($tool in $gate.Tools) {
        $path = Find-Tool $tool
        if ($path) { Write-Host "Tool ${tool}: $path" }
        else { $problems.Add("required tool '$tool' not found") }
    }
}
else {
    Remove-Item Env:DAML_REQUIRED_TOOLS -ErrorAction SilentlyContinue
}

$testProjects = @()
$testDir = Join-Path $root 'test'
if (Test-Path $testDir) {
    $testProjects = @(Get-ChildItem $testDir -Recurse -Include '*.Tests.csproj', '*.IntegrationTests.csproj' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
}

if (-not $NoBuild -and $testProjects) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Get-TrxCounters {
    param([string] $Directory)

    # Count per-test outcomes: the summary Counters element leaves notExecuted at 0 even
    # when xUnit skips a test, so skips would go unnoticed.
    $sum = [ordered]@{ Total = 0; Executed = 0; Passed = 0; Failed = 0; Skipped = 0; Files = 0 }
    foreach ($trx in Get-ChildItem $Directory -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue) {
        $sum.Files++
        $run = ([xml](Get-Content $trx.FullName -Raw)).TestRun
        $results = if ($run.PSObject.Properties['Results']) { @($run.Results.ChildNodes) } else { @() }
        foreach ($r in $results) {
            $sum.Total++
            switch ($r.outcome) {
                'Passed' { $sum.Executed++; $sum.Passed++ }
                'NotExecuted' { $sum.Skipped++ }
                default { $sum.Executed++; $sum.Failed++ }
            }
        }
        # A crashed test host can leave no results at all; the run outcome still says so.
        if ($run.ResultSummary.outcome -in 'Error', 'Aborted' -and $results.Count -eq 0) { $sum.Failed++ }
    }
    [pscustomobject]$sum
}

$summary = foreach ($cat in $categories) {
    $isRequired = $required -contains $cat
    $outDir = Join-Path $resultsRoot $cat
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

    if ($cat -notin $vstestCategories) {
        # Grammar runs through tree-sitter and Perf through BenchmarkDotNet; neither exists yet.
        if ($isRequired) { $problems.Add("$cat is required but has no runner yet") }
        [pscustomobject]@{ Category = $cat; Status = 'N/A'; Note = 'no runner yet' }
        continue
    }
    if (-not $testProjects) {
        if ($isRequired) { $problems.Add("$cat is required but there are no test projects") }
        [pscustomobject]@{ Category = $cat; Status = 'N/A'; Note = 'no test projects' }
        continue
    }

    $filter = "Category=$cat"
    if ($cat -eq 'MSBuild' -and $Backend -ne 'All') { $filter += "&Backend=$Backend" }
    $collectCoverage = -not $NoCoverage -and $cat -in 'Unit', 'Editor', 'Contract', 'MSBuild'

    $exitCodes = foreach ($proj in $testProjects) {
        $testArgs = @(
            'test', $proj.FullName
            '--no-build', '--nologo'
            '--configuration', $Configuration
            '--filter', $filter
            '--results-directory', $outDir
            '--logger', "trx;LogFilePrefix=$($proj.BaseName)"
        )
        if ($collectCoverage) { $testArgs += '--collect', 'XPlat Code Coverage' }
        & dotnet @testArgs | Out-Host
        $LASTEXITCODE
    }

    $c = Get-TrxCounters $outDir
    $status = if ($c.Failed -gt 0 -or ($exitCodes | Where-Object { $_ -ne 0 })) { 'FAILED' }
              elseif ($c.Executed -eq 0) { 'N/A' }
              else { 'PASSED' }
    if ($status -eq 'FAILED') { $problems.Add("$cat has failing tests") }
    if ($isRequired -and $c.Executed -eq 0) { $problems.Add("$cat is required but executed 0 tests") }
    if ($isRequired -and $c.Skipped -gt 0) { $problems.Add("$cat is required but skipped $($c.Skipped) test(s)") }

    [pscustomobject]@{
        Category = $cat; Status = $status
        Note     = "executed $($c.Executed), passed $($c.Passed), failed $($c.Failed), skipped $($c.Skipped), trx $($c.Files)"
    }
}

$summary | Format-Table -AutoSize | Out-Host

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }
    exit 1
}
exit 0
