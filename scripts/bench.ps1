#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the BenchmarkDotNet benchmarks (Release, x64) and leaves the reports in artifacts/bench.

.DESCRIPTION
    The budgets are measured on net48, the runtime Visual Studio loads the extension into;
    net10.0 is there for comparison. Extra arguments go to BenchmarkDotNet unchanged, e.g.
    -- --filter '*Relex*' or --job short for a quick look.

.EXAMPLE
    pwsh scripts/bench.ps1
.EXAMPLE
    pwsh scripts/bench.ps1 -Framework net10.0 --filter '*Lex*'
#>
[CmdletBinding()]
param(
    [ValidateSet('net48', 'net10.0')]
    [string] $Framework = 'net48',

    [Parameter(ValueFromRemainingArguments)]
    [string[]] $BenchmarkArguments = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'test\Daml.VisualStudio.Benchmarks\Daml.VisualStudio.Benchmarks.csproj'
$artifacts = Join-Path $root "artifacts\bench\$Framework"

if (-not ($BenchmarkArguments -contains '--filter')) { $BenchmarkArguments += @('--filter', '*') }
& dotnet run -c Release -f $Framework --project $project -- --artifacts $artifacts @BenchmarkArguments
exit $LASTEXITCODE
