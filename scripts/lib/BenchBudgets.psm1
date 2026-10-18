#Requires -Version 5.1
<#
    The performance budgets and the check of a BenchmarkDotNet run against them. The budgets are
    medians on net48 x64 (the runtime Visual Studio loads the extension into); the run's brief
    JSON reports carry the medians in nanoseconds.
#>

function Get-BenchBudgets {
    <#
    .SYNOPSIS
        The budgets, in milliseconds, keyed by '<benchmark class>.<method>'.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param()

    return @{
        'LexerBenchmarks.LexWholeFile'                = 30
        'LexerBenchmarks.LexWholeFileIntoTokens'      = 30
        'LexerBenchmarks.RelexAfterOneLineEdit'       = 1
        'LexerBenchmarks.RelexTokensAfterOneLineEdit' = 1
        'LayoutBenchmarks.AnalyzeLexedFile'           = 40
        # The binding index of 500 generated Daml types on a fresh, bound compilation.
        'BindingIndexBenchmarks.BuildWithColdCatalog' = 300
        'BindingIndexBenchmarks.BuildWithWarmCatalog' = 300
    }
}

function Read-BenchResults {
    param([string] $ResultsPath, [switch] $ValidateHost, [string] $Framework)
    $values = @{}
    $hostSignature = $null
    foreach ($report in Get-ChildItem -LiteralPath $ResultsPath -Filter '*-report-brief.json' -File -ErrorAction SilentlyContinue) {
        $json = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
        $hostInfo = $json.HostEnvironmentInfo
        $signature = "$($hostInfo.ProcessorName)|$($hostInfo.Architecture)|$($hostInfo.RuntimeVersion)|$($hostInfo.Configuration)"
        if ($ValidateHost) {
            $runtime = if ($Framework -eq 'net48') { '^\.NET Framework 4\.8' } else { '^\.NET 10\.' }
            if ($hostInfo.Architecture -ne 'X64' -or $hostInfo.Configuration -ne 'RELEASE' -or $hostInfo.RuntimeVersion -notmatch $runtime -or -not $hostInfo.ProcessorName) {
                throw "Unsupported benchmark host in $($report.Name): $signature"
            }
            if ($null -ne $hostSignature -and $signature -ne $hostSignature) { throw 'Reports mix benchmark hosts or runtimes.' }
        }
        $hostSignature = $signature
        foreach ($benchmark in $json.Benchmarks) {
            $key = "$($benchmark.Type).$($benchmark.Method)"
            if ($values.ContainsKey($key)) { throw "Duplicate benchmark result: $key" }
            $median = $benchmark.Statistics.Median
            if ($null -eq $median -or [double]::IsNaN([double] $median) -or [double]::IsInfinity([double] $median) -or [double] $median -le 0) {
                throw "Invalid benchmark median: $key"
            }
            $values[$key] = [double] $median / 1e6
        }
    }
    [pscustomobject] @{ Medians = $values; HostSignature = $hostSignature }
}

function Test-BenchBudgets {
    <#
    .SYNOPSIS
        Checks absolute budgets and, when supplied, a same-host baseline's regression limit.
    .OUTPUTS
        One object per required benchmark, with missing measurements explicitly failing.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ResultsPath,
        [hashtable] $Budgets = (Get-BenchBudgets),
        [string] $BaselineResultsPath,
        [double] $MaxRegressionPercent = 10,
        [switch] $ValidateHost,
        [ValidateSet('net48', 'net10.0')] [string] $Framework = 'net48'
    )
    if ($MaxRegressionPercent -lt 0) { throw 'The regression limit must be nonnegative.' }
    $results = Read-BenchResults $ResultsPath -ValidateHost:$ValidateHost -Framework $Framework
    $baseline = if ($BaselineResultsPath) { Read-BenchResults $BaselineResultsPath -ValidateHost:$ValidateHost -Framework $Framework } else { $null }
    if ($baseline -and $baseline.Medians.Count -gt 0 -and $results.Medians.Count -gt 0 -and $results.HostSignature -ne $baseline.HostSignature) { throw 'The baseline and current reports must use the same processor, architecture, runtime and configuration.' }
    foreach ($name in ($Budgets.Keys | Sort-Object)) {
        $median = if ($results.Medians.ContainsKey($name)) { $results.Medians[$name] } else { $null }
        $before = if ($baseline -and $baseline.Medians.ContainsKey($name)) { $baseline.Medians[$name] } else { $null }
        $regression = if ($null -ne $median -and $null -ne $before) { 100 * ($median / $before - 1) } else { $null }
        [pscustomobject] @{
            Benchmark = $name
            MedianMs = $median
            BudgetMs = [double] $Budgets[$name]
            BaselineMs = $before
            RegressionPercent = $regression
            Passed = ($null -ne $median) -and ($median -le $Budgets[$name]) -and
                (-not $BaselineResultsPath -or ($null -ne $before -and $regression -le $MaxRegressionPercent + 1e-9))
        }
    }
}

Export-ModuleMember -Function Get-BenchBudgets, Test-BenchBudgets
