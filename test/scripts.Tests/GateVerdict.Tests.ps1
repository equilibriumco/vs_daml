# Pester tests of the gate's verdicts in scripts/lib/TestResults.psm1: what makes a category fail,
# what a required category has to show, and how coverage is merged and judged.
# Run with scripts/test-scripts.ps1.

Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\TestResults.psm1') -Force

function New-Run([string] $Project = 'App.Tests', [string] $Tfm = 'net10.0', [int] $ExitCode = 0, [int] $Executed = 3, [int] $Skipped = 0) {
    [pscustomobject]@{ Project = $Project; Tfm = $Tfm; ExitCode = $ExitCode; Counters = [pscustomobject]@{ Executed = $Executed; Skipped = $Skipped } }
}

function New-Totals([int] $Executed = 3, [int] $Failed = 0) {
    [pscustomobject]@{ Executed = $Executed; Failed = $Failed }
}

# A Cobertura report with one class whose lines are hit as given: @{ 1 = 1; 2 = 0 }.
function New-Report([string] $Path, [string] $Assembly, [hashtable] $Hits, [string] $File = 'A.cs') {
    $lines = ($Hits.Keys | Sort-Object | ForEach-Object { "<line number=`"$_`" hits=`"$($Hits[$_])`" />" }) -join ''
    Set-Content -Path $Path -Value "<coverage><packages><package name=`"$Assembly`"><classes><class filename=`"$File`"><lines>$lines</lines></class></classes></package></packages></coverage>"
}

Describe 'Run verdict' {
    It 'passes a category whose runs all executed tests and none failed' {
        $verdict = Get-RunVerdict -Category 'Unit' -Runs @(New-Run) -Totals (New-Totals) -Required $true
        $verdict.Status | Should Be 'PASSED'
        @($verdict.Problems).Count | Should Be 0
    }

    It 'fails on a failed test, a run that exited badly, or lost results' {
        (Get-RunVerdict -Category 'Unit' -Runs @(New-Run) -Totals (New-Totals -Failed 1)).Status | Should Be 'FAILED'
        (Get-RunVerdict -Category 'Unit' -Runs @(New-Run -ExitCode 1) -Totals (New-Totals)).Status | Should Be 'FAILED'
        $lost = Get-RunVerdict -Category 'Integration' -Runs @(New-Run) -Totals (New-Totals) -Lost 2
        $lost.Status | Should Be 'FAILED'
        $lost.Problems -join "`n" | Should Match 'lost the results of 2'
    }

    It 'calls a category that ran nothing N/A, not passed' {
        (Get-RunVerdict -Category 'Contract' -Runs @(New-Run -Executed 0) -Totals (New-Totals -Executed 0)).Status | Should Be 'N/A'
    }

    It 'lets an optional category run nothing or skip without a problem' {
        $verdict = Get-RunVerdict -Category 'Contract' -Runs @(New-Run -Executed 0 -Skipped 2) -Totals (New-Totals -Executed 0) -Required $false
        @($verdict.Problems).Count | Should Be 0
    }

    # Per project and framework, so tests vanishing from one can't hide behind the totals of the others.
    It 'requires every project to execute tests on every framework, without skips' {
        $runs = @((New-Run -Tfm 'net48'), (New-Run -Tfm 'net10.0' -Executed 0), (New-Run -Project 'Core.Tests' -Skipped 1))
        $problems = (Get-RunVerdict -Category 'Unit' -Runs $runs -Totals (New-Totals -Executed 6) -Required $true).Problems -join "`n"
        $problems | Should Match 'App.Tests executed 0 tests on net10.0'
        $problems | Should Match 'Core.Tests skipped 1 test'
        $problems | Should Not Match 'net48'
    }

    It 'flags a pending category that has gained tests' {
        $verdict = Get-RunVerdict -Category 'Perf' -Runs @(New-Run) -Totals (New-Totals) -Pending 'no runner yet'
        $verdict.Problems -join "`n" | Should Match 'pending .* now has tests'
        (Get-RunVerdict -Category 'Perf' -Runs @(New-Run -Executed 0) -Totals (New-Totals -Executed 0) -Pending 'no runner yet').Problems.Count | Should Be 0
    }
}

Describe 'Coverage' {
    BeforeEach { $script:dir = Join-Path ([IO.Path]::GetTempPath()) ('eqdaml-coverage-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $script:dir | Out-Null }
    AfterEach { Remove-Item -LiteralPath $script:dir -Recurse -Force }

    It 'counts a line covered when any run hit it' {
        New-Report (Join-Path $script:dir 'a.xml') 'Core' @{ 1 = 1; 2 = 0; 3 = 0; 4 = 0 }
        New-Report (Join-Path $script:dir 'b.xml') 'Core' @{ 1 = 0; 2 = 1; 3 = 0; 4 = 0 }
        $measured = Measure-AssemblyCoverage @((Join-Path $script:dir 'a.xml'), (Join-Path $script:dir 'b.xml'))
        $measured['Core'].Valid | Should Be 4
        $measured['Core'].Covered | Should Be 2
        $measured['Core'].Rate | Should Be 50
    }

    It 'keeps lines of different files apart' {
        New-Report (Join-Path $script:dir 'a.xml') 'Core' @{ 1 = 1 } -File 'A.cs'
        New-Report (Join-Path $script:dir 'b.xml') 'Core' @{ 1 = 0 } -File 'B.cs'
        (Measure-AssemblyCoverage @((Join-Path $script:dir 'a.xml'), (Join-Path $script:dir 'b.xml')))['Core'].Valid | Should Be 2
    }

    It 'judges each assembly against its threshold, and tells missing from no code' {
        $measured = @{
            'High' = [pscustomobject]@{ Valid = 10; Covered = 9; Rate = 90.0 }
            'Low'  = [pscustomobject]@{ Valid = 10; Covered = 5; Rate = 50.0 }
            'Empty' = [pscustomobject]@{ Valid = 0; Covered = 0; Rate = $null }
        }
        $rows = @(Get-CoverageVerdict -Measured $measured -Framework 'net48' -Thresholds @{ High = 80; Low = 80; Empty = 80; Gone = 80 })
        ($rows | Where-Object Assembly -eq 'High').Status | Should Be 'OK'
        ($rows | Where-Object Assembly -eq 'Low').Status | Should Be 'LOW'
        ($rows | Where-Object Assembly -eq 'Empty').Status | Should Be 'NO CODE'
        ($rows | Where-Object Assembly -eq 'Gone').Status | Should Be 'MISSING'
        $problems = @($rows | Where-Object Problem | ForEach-Object Problem) -join "`n"
        $problems | Should Match 'Low on net48 is 50.0%, below 80%'
        $problems | Should Match 'Gone is absent from the net48 reports'
        $problems | Should Not Match 'High|Empty'
    }

    It 'passes an assembly exactly at its threshold' {
        $measured = @{ 'Core' = [pscustomobject]@{ Valid = 5; Covered = 4; Rate = 80.0 } }
        (Get-CoverageVerdict -Measured $measured -Framework 'net10.0' -Thresholds @{ Core = 80 }).Status | Should Be 'OK'
    }
}
