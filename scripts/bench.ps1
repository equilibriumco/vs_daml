#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the BenchmarkDotNet benchmarks (Release, x64) and leaves the reports in artifacts/bench.

.DESCRIPTION
    The budgets are measured on net48, the runtime Visual Studio loads the extension into;
    net10.0 is there for comparison. Extra arguments go to BenchmarkDotNet unchanged, e.g.
    -- --filter '*Relex*' or --job short for a quick look.

    -Check compares medians with absolute budgets and a same-host baseline (at most 10% slower).
    Supply -BaselineResultsPath, or keep baseline reports under artifacts/bench/baseline/<tfm>/results.
    -NoRun checks the last run; fresh runs clear their result folder so old reports cannot fill gaps.

.EXAMPLE
    pwsh scripts/bench.ps1
.EXAMPLE
    pwsh scripts/bench.ps1 -Check
.EXAMPLE
    pwsh scripts/bench.ps1 -Framework net10.0 --filter '*Lex*'
#>
[CmdletBinding()]
param(
    [ValidateSet('net48', 'net10.0')]
    [string] $Framework = 'net48',

    [switch] $Check,

    [switch] $NoRun,

    [string] $BaselineResultsPath,

    [Parameter(ValueFromRemainingArguments)]
    [string[]] $BenchmarkArguments = @()
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\BenchBudgets.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'test\Equilibrium.Daml.VisualStudio.Benchmarks\Equilibrium.Daml.VisualStudio.Benchmarks.csproj'
$artifacts = Join-Path $root "artifacts\bench\$Framework"

if (-not $BaselineResultsPath) { $BaselineResultsPath = Join-Path $root "artifacts\bench\baseline\$Framework\results" }
if ($Check -and -not (Test-Path -LiteralPath $BaselineResultsPath)) {
    throw "No benchmark baseline at '$BaselineResultsPath'. Preserve reports from the comparison revision on this host, or supply -BaselineResultsPath."
}

if (-not $NoRun) {
    $resultsPath = [IO.Path]::GetFullPath((Join-Path $artifacts 'results'))
    $allowedRoot = [IO.Path]::GetFullPath($artifacts) + [IO.Path]::DirectorySeparatorChar
    if (-not $resultsPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Benchmark results path escaped its artifact directory.' }
    if (Test-Path -LiteralPath $resultsPath) { Remove-Item -LiteralPath $resultsPath -Recurse -Force }
    if (-not ($BenchmarkArguments -contains '--filter')) { $BenchmarkArguments += @('--filter', '*') }
    & dotnet run -c Release -f $Framework --project $project -- --artifacts $artifacts @BenchmarkArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $Check) { exit 0 }

$checked = Test-BenchBudgets -ResultsPath (Join-Path $artifacts 'results') -BaselineResultsPath $BaselineResultsPath -ValidateHost -Framework $Framework
$checked | Format-Table Benchmark, @{ n = 'Median (ms)'; e = { if ($null -eq $_.MedianMs) { 'missing' } else { '{0:0.000}' -f $_.MedianMs } } }, @{ n = 'Budget (ms)'; e = 'BudgetMs' }, BaselineMs, RegressionPercent, Passed -AutoSize | Out-String | Write-Host
$failed = @($checked | Where-Object { -not $_.Passed })
if ($Framework -ne 'net48') { Write-Warning "The budgets are set for net48; this is $Framework." }
if ($failed.Count -gt 0) {
    Write-Error ("Over budget, regressed or missing: " + (($failed | ForEach-Object Benchmark) -join ', '))
    exit 1
}
Write-Host "All $($checked.Count) budgets met."
