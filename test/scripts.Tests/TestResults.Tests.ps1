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
