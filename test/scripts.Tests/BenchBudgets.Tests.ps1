$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here '..\..\scripts\lib\BenchBudgets.psm1') -Force

# The message of what $Script throws, or null. Pester 3.4's `Should Throw` misses every exception
# under PowerShell 7 (it works in Windows PowerShell 5.1), so the tests catch it themselves.
function Get-ThrownMessage([scriptblock] $Script) {
    try {
        & $Script | Out-Null
        return $null
    }
    catch {
        return $_.Exception.Message
    }
}

# A brief BenchmarkDotNet report with the given medians, in milliseconds, for one class.
function New-BriefReport([string] $Folder, [string] $Type, [hashtable] $MediansMs) {
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    $benchmarks = @()
    foreach ($method in $MediansMs.Keys) {
        $benchmarks += @{ Type = $Type; Method = $method; Statistics = @{ Median = $MediansMs[$method] * 1e6 } }
    }
    $report = @{ Title = $Type; Benchmarks = $benchmarks } | ConvertTo-Json -Depth 5
    Set-Content -LiteralPath (Join-Path $Folder "Daml.VisualStudio.Benchmarks.$Type-report-brief.json") -Value $report
}

Describe 'Test-BenchBudgets' {
    $results = Join-Path ([IO.Path]::GetTempPath()) ("bench-budgets-" + [Guid]::NewGuid().ToString('N'))
    $budgets = @{ 'Lexer.Whole' = 30; 'Lexer.Relex' = 1; 'Layout.Analyze' = 40 }

    It 'passes every benchmark whose median is within its budget' {
        New-BriefReport (Join-Path $results 'a') 'Lexer' @{ Whole = 12.5; Relex = 0.4 }
        New-BriefReport (Join-Path $results 'a') 'Layout' @{ Analyze = 40 }

        $checked = Test-BenchBudgets -ResultsPath (Join-Path $results 'a') -Budgets $budgets

        @($checked).Count | Should Be 3
        @($checked | Where-Object { -not $_.Passed }).Count | Should Be 0
        ($checked | Where-Object Benchmark -eq 'Lexer.Whole').MedianMs | Should Be 12.5
    }

    It 'fails the benchmark that is over budget and names it' {
        New-BriefReport (Join-Path $results 'b') 'Lexer' @{ Whole = 31; Relex = 0.4 }
        New-BriefReport (Join-Path $results 'b') 'Layout' @{ Analyze = 10 }

        $failed = Test-BenchBudgets -ResultsPath (Join-Path $results 'b') -Budgets $budgets | Where-Object { -not $_.Passed }

        @($failed).Count | Should Be 1
        $failed.Benchmark | Should Be 'Lexer.Whole'
        $failed.BudgetMs | Should Be 30
    }

    It 'fails a budgeted benchmark the run did not report' {
        New-BriefReport (Join-Path $results 'c') 'Lexer' @{ Whole = 5; Relex = 0.4 }

        $missing = Test-BenchBudgets -ResultsPath (Join-Path $results 'c') -Budgets $budgets | Where-Object Benchmark -eq 'Layout.Analyze'

        $missing.Passed | Should Be $false
        $missing.MedianMs | Should Be $null
    }

    It 'fails everything when there are no results' {
        $checked = Test-BenchBudgets -ResultsPath (Join-Path $results 'none') -Budgets $budgets

        @($checked | Where-Object Passed).Count | Should Be 0
    }

    It 'has a budget for every benchmark the strategy names' {
        $names = @((Get-BenchBudgets).Keys)
        ($names -contains 'LexerBenchmarks.LexWholeFile') | Should Be $true
        ($names -contains 'LexerBenchmarks.RelexAfterOneLineEdit') | Should Be $true
        ($names -contains 'LayoutBenchmarks.AnalyzeLexedFile') | Should Be $true
    }

    It 'rejects a regression over ten percent even when the absolute budget is met' {
        New-BriefReport (Join-Path $results 'baseline') 'Lexer' @{ Whole = 10 }
        New-BriefReport (Join-Path $results 'regressed') 'Lexer' @{ Whole = 11.01 }
        $checked = Test-BenchBudgets -ResultsPath (Join-Path $results 'regressed') -BaselineResultsPath (Join-Path $results 'baseline') -Budgets @{ 'Lexer.Whole' = 30 }
        $checked.Passed | Should Be $false
    }

    It 'accepts exactly ten percent regression and fails a missing baseline measurement' {
        New-BriefReport (Join-Path $results 'edge') 'Lexer' @{ Whole = 11 }
        $checked = Test-BenchBudgets -ResultsPath (Join-Path $results 'edge') -BaselineResultsPath (Join-Path $results 'baseline') -Budgets @{ 'Lexer.Whole' = 30 }
        $checked.Passed | Should Be $true
        $missing = Test-BenchBudgets -ResultsPath (Join-Path $results 'edge') -BaselineResultsPath (Join-Path $results 'missing') -Budgets @{ 'Lexer.Whole' = 30 }
        $missing.Passed | Should Be $false
    }

    It 'rejects duplicate measurements instead of silently selecting a report' {
        New-BriefReport (Join-Path $results 'duplicate') 'Lexer' @{ Whole = 10 }
        Copy-Item (Join-Path $results 'duplicate\Daml.VisualStudio.Benchmarks.Lexer-report-brief.json') (Join-Path $results 'duplicate\another-report-brief.json')
        Get-ThrownMessage { Test-BenchBudgets -ResultsPath (Join-Path $results 'duplicate') -Budgets @{ 'Lexer.Whole' = 30 } } | Should Match 'Duplicate benchmark result: Lexer\.Whole'
    }

    It 'requires net48 x64 Release evidence for the VS budgets' {
        New-BriefReport (Join-Path $results 'wrong-host') 'Lexer' @{ Whole = 10 }
        Get-ThrownMessage { Test-BenchBudgets -ResultsPath (Join-Path $results 'wrong-host') -Budgets @{ 'Lexer.Whole' = 30 } -ValidateHost } | Should Match 'Unsupported benchmark host'
    }

    Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue
}
