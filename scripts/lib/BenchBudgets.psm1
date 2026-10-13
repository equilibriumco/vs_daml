#Requires -Version 7.2
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
    }
}

function Test-BenchBudgets {
    <#
    .SYNOPSIS
        Compares the medians in a results folder's *-report-brief.json files with the budgets.
    .OUTPUTS
        One object per budget: Benchmark, MedianMs (null when the run has no such benchmark),
        BudgetMs, Passed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ResultsPath,

        [hashtable] $Budgets = (Get-BenchBudgets)
    )

    $medians = @{}
    if (Test-Path $ResultsPath) {
        foreach ($report in Get-ChildItem -Path $ResultsPath -Filter '*-report-brief.json' -File) {
            $json = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
            foreach ($benchmark in $json.Benchmarks) {
                $medians["$($benchmark.Type).$($benchmark.Method)"] = [double] $benchmark.Statistics.Median / 1e6
            }
        }
    }

    foreach ($name in ($Budgets.Keys | Sort-Object)) {
        $median = if ($medians.ContainsKey($name)) { $medians[$name] } else { $null }
        [pscustomobject] @{
            Benchmark = $name
            MedianMs  = $median
            BudgetMs  = [double] $Budgets[$name]
            Passed    = ($null -ne $median) -and ($median -le $Budgets[$name])
        }
    }
}

Export-ModuleMember -Function Get-BenchBudgets, Test-BenchBudgets
