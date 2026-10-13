#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the BenchmarkDotNet benchmarks (Release, x64) and leaves the reports in artifacts/bench.

.DESCRIPTION
    The budgets are measured on net48, the runtime Visual Studio loads the extension into;
    net10.0 is there for comparison. Extra arguments go to BenchmarkDotNet unchanged, e.g.
    -- --filter '*Relex*' or --job short for a quick look.

    -Check compares the medians of the run with the budgets in scripts/lib/BenchBudgets.psm1
    (the table in docs/plan/01-test-strategy.md §6) and fails when one is over; with -NoRun it
    checks the reports already in artifacts/bench without running anything.

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

    [Parameter(ValueFromRemainingArguments)]
    [string[]] $BenchmarkArguments = @()
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\BenchBudgets.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'test\Daml.VisualStudio.Benchmarks\Daml.VisualStudio.Benchmarks.csproj'
$artifacts = Join-Path $root "artifacts\bench\$Framework"

if (-not $NoRun) {
    if (-not ($BenchmarkArguments -contains '--filter')) { $BenchmarkArguments += @('--filter', '*') }
    & dotnet run -c Release -f $Framework --project $project -- --artifacts $artifacts @BenchmarkArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $Check) { exit 0 }

$checked = Test-BenchBudgets -ResultsPath (Join-Path $artifacts 'results')
$checked | Format-Table Benchmark, @{ n = 'Median (ms)'; e = { if ($null -eq $_.MedianMs) { 'missing' } else { '{0:0.000}' -f $_.MedianMs } } }, @{ n = 'Budget (ms)'; e = 'BudgetMs' }, Passed -AutoSize | Out-String | Write-Host
$failed = @($checked | Where-Object { -not $_.Passed })
if ($Framework -ne 'net48') { Write-Warning "The budgets are set for net48; this is $Framework." }
if ($failed.Count -gt 0) {
    Write-Error ("Over budget or missing: " + (($failed | ForEach-Object Benchmark) -join ', '))
    exit 1
}
Write-Host "All $($checked.Count) budgets met."
