# Pester tests of the gate logic in scripts/lib/TestResults.psm1, over hand-made TRX files.
# Run with scripts/test-scripts.ps1.

Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\TestResults.psm1') -Force

function New-Trx([string] $Path, [string[]] $Outcomes, [string] $RunOutcome = 'Completed', [string[]] $Names = $null) {
    $results = for ($i = 0; $i -lt $Outcomes.Count; $i++) {
        $name = if ($Names) { $Names[$i] } else { "Tests.Case$i" }
        "<UnitTestResult testName=`"$name`" outcome=`"$($Outcomes[$i])`" />"
    }
    $resultsElement = if ($Outcomes.Count -gt 0) { "<Results>$($results -join '')</Results>" } else { '' }
    Set-Content -Path $Path -Value @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun>
  $resultsElement
  <ResultSummary outcome="$RunOutcome" />
</TestRun>
"@
}

Describe 'Get-TrxCounters' {
    BeforeEach {
        $script:dir = Join-Path ([IO.Path]::GetTempPath()) ("trx-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:dir | Out-Null
    }
    AfterEach { Remove-Item -Recurse -Force $script:dir }

    It 'counts each outcome per result, skips included' {
        New-Trx (Join-Path $script:dir 'a.trx') @('Passed', 'Passed', 'Failed', 'NotExecuted')

        $counters = Get-TrxCounters $script:dir

        $counters.Total | Should Be 4
        $counters.Executed | Should Be 3
        $counters.Passed | Should Be 2
        $counters.Failed | Should Be 1
        $counters.Skipped | Should Be 1
        $counters.Files | Should Be 1
    }

    It 'adds up several files' {
        New-Trx (Join-Path $script:dir 'a.trx') @('Passed')
        New-Trx (Join-Path $script:dir 'b.trx') @('Passed', 'Failed')

        $counters = Get-TrxCounters $script:dir

        $counters.Files | Should Be 2
        $counters.Passed | Should Be 2
        $counters.Failed | Should Be 1
    }

    It 'counts a crashed host with no results as one failure' {
        New-Trx (Join-Path $script:dir 'crash.trx') @() -RunOutcome 'Aborted'

        (Get-TrxCounters $script:dir).Failed | Should Be 1
    }

    It 'reports nothing for a folder without TRX files' {
        $counters = Get-TrxCounters $script:dir

        $counters.Files | Should Be 0
        $counters.Total | Should Be 0
    }
}

Describe 'Get-IdeTestShortfall' {
    BeforeEach {
        $script:dir = Join-Path ([IO.Path]::GetTempPath()) ("ide-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $script:dir 'src') | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:dir 'results') | Out-Null
        $script:project = Join-Path $script:dir 'src\Ide.csproj'
        Set-Content $script:project '<Project />'
        Set-Content (Join-Path $script:dir 'src\A.cs') @'
public class A
{
    [IdeFact]
    public void One() { }

    // [IdeFact] not a test any more
    public void Commented() { }

    [IdeFact(Timeout = 60)]
    public void Two() { }
}
'@
    }
    AfterEach { Remove-Item -Recurse -Force $script:dir }

    It 'is zero when every declared test reported' {
        New-Trx (Join-Path $script:dir 'results\run.trx') @('Passed', 'Passed', 'Passed') -Names @('Tests.One', 'Tests.Two', 'Xunit.Instances.VisualStudio')

        Get-IdeTestShortfall $script:project (Join-Path $script:dir 'results') | Should Be 0
    }

    It 'is the number of declared tests the run never reported' {
        New-Trx (Join-Path $script:dir 'results\run.trx') @('Passed') -Names @('Tests.One')

        Get-IdeTestShortfall $script:project (Join-Path $script:dir 'results') | Should Be 1
    }

    It 'ignores sources under bin and obj' {
        New-Item -ItemType Directory -Path (Join-Path $script:dir 'src\obj') | Out-Null
        Set-Content (Join-Path $script:dir 'src\obj\Generated.cs') '[IdeFact] class X {}'
        New-Trx (Join-Path $script:dir 'results\run.trx') @('Passed', 'Passed') -Names @('Tests.One', 'Tests.Two')

        Get-IdeTestShortfall $script:project (Join-Path $script:dir 'results') | Should Be 0
    }
}

Describe 'Get-RunCoverageReports' {
    BeforeEach {
        $script:dir = Join-Path ([IO.Path]::GetTempPath()) ("coverage-" + [guid]::NewGuid().ToString('N'))
        foreach ($category in @('Unit', 'Contract')) {
            $folder = Join-Path $script:dir "$category\net10.0\Tests"
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
            New-Trx (Join-Path $folder 'run.trx') @('Passed')
            Set-Content (Join-Path $folder 'coverage.cobertura.xml') '<coverage />'
        }
    }
    AfterEach { Remove-Item -LiteralPath $script:dir -Recurse -Force }

    It 'excludes earlier results of categories not selected for this run' {
        $reports = @(Get-RunCoverageReports -ResultsRoot $script:dir -Categories @('Unit') -Framework 'net10.0')
        $reports.Count | Should Be 1
        $reports[0].FullName | Should Match 'Unit'
    }

    It 'does not mix runtimes or count reports from zero-test runs' {
        New-Trx (Join-Path $script:dir 'Unit\net10.0\Tests\run.trx') @()
        @(Get-RunCoverageReports -ResultsRoot $script:dir -Categories @('Unit') -Framework 'net10.0').Count | Should Be 0
        @(Get-RunCoverageReports -ResultsRoot $script:dir -Categories @('Contract') -Framework 'net48').Count | Should Be 0
    }
}
