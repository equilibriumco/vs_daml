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

Describe 'Get-TreeSitterTestCounters' {
    It 'reads the totals from the summary line, colours and all' {
        $lines = @(
            "      724. `e[32m✓ varsym: operators with bar`e[0m",
            '',
            'Total parses: 725; successful parses: 724; failed parses: 1; success percentage: 99.86%; average speed: 2563 bytes/ms'
        )

        $counters = Get-TreeSitterTestCounters $lines

        $counters.Found | Should Be $true
        $counters.Executed | Should Be 725
        $counters.Passed | Should Be 724
        $counters.Failed | Should Be 1
    }

    It 'reports a run without a summary as not found, so it cannot pass as zero failures' {
        $counters = Get-TreeSitterTestCounters @('Error: could not compile the parser', 'exit')

        $counters.Found | Should Be $false
        $counters.Executed | Should Be 0
    }

    It 'takes the last summary when the output has more than one' {
        $lines = @(
            'Total parses: 3; successful parses: 3; failed parses: 0; success percentage: 100.00%',
            'Total parses: 10; successful parses: 8; failed parses: 2; success percentage: 80.00%'
        )

        (Get-TreeSitterTestCounters $lines).Failed | Should Be 2
    }

    It 'counts the highlight and tag test files apart from the parses, with their assertions' {
        $lines = @(
            "      13. `e[32m✓ record: data declaration`e[0m",
            'syntax highlighting:',
            "      1. `e[32m✓ exception.daml`e[0m (8 assertions)",
            "      2. `e[31m✗ haskell.daml`e[0m",
            '    Failure - row: 25, column: 8, expected highlight ''type'', actual highlights: ''punctuation.bracket''',
            "      3. `e[32m✓ template.daml`e[0m (25 assertions)",
            'tags:',
            "      1. `e[32m✓ template.daml`e[0m (4 assertions)",
            'Total parses: 796; successful parses: 796; failed parses: 0; success percentage: 100.00%'
        )

        $counters = Get-TreeSitterTestCounters $lines

        $counters.Executed | Should Be 796
        $counters.QueryFiles | Should Be 4
        $counters.QueryFailed | Should Be 1
        $counters.Assertions | Should Be 37
    }

    It 'reports no query files when the run has none' {
        $counters = Get-TreeSitterTestCounters @('Total parses: 3; successful parses: 3; failed parses: 0; success percentage: 100.00%')

        $counters.QueryFiles | Should Be 0
        $counters.QueryFailed | Should Be 0
    }
}

Describe 'Get-TreeSitterFuzzCounters' {
    It 'counts the tests a clean run listed and finds no failures' {
        $lines = @(
            'fuzz seed: 7091589735194505817',
            '',
            '  0. daml - corpus - daml - choice - choice: parameters, controller and do',
            '  1. daml - corpus - haskell - varsym - varsym: operators with dollar'
        )

        $counters = Get-TreeSitterFuzzCounters $lines

        $counters.Executed | Should Be 2
        $counters.Failed | Should Be 0
        @($counters.Failures).Count | Should Be 0
    }

    It 'reads the failures from the summary and names them, though the CLI exits 0' {
        $lines = @(
            '  705. daml - corpus - haskell - th - Close nested layouts at closing bracket in decl quote',
            'Incorrect parse for daml - corpus - haskell - th - Close nested layouts at closing bracket in decl quote - seed 15330765409034137429',
            '',
            "correct / `e[32mexpected`e[0m / `e[31munexpected`e[0m",
            "`e[31m(daml (declarations (bind (variable) (match (quote (quoted_decls (instance (name))) (ERROR (variable))))))`e[0m",
            '',
            '  706. daml - corpus - haskell - th - th: typed expression quotation',
            '1 daml corpus tests failed fuzzing'
        )

        $counters = Get-TreeSitterFuzzCounters $lines

        $counters.Executed | Should Be 2
        $counters.Failed | Should Be 1
        @($counters.Failures).Count | Should Be 1
        $counters.Failures[0] | Should Match 'seed 15330765409034137429$'
    }

    It 'counts a failure named in the output even without the summary line' {
        $lines = @(
            '  0. daml - corpus - daml - template - template: signatory',
            'Incorrect initial parse for daml - corpus - daml - template - template: signatory',
            '  1. daml - corpus - daml - template - template: observer',
            '',
            'Unexpected scope change in seed 42 with start seed 41'
        )

        $counters = Get-TreeSitterFuzzCounters $lines

        $counters.Failed | Should Be 2
        @($counters.Failures).Count | Should Be 2
    }

    It 'names a scope change printed on the line of its error prefix too' {
        $lines = @(
            '  565. daml - corpus - haskell - pragma - pragma: instance method with cpp',
            'Error: Unexpected scope change in seed 1000930 with start seed 1000000',
            'Position: (7, 0)',
            '2 daml corpus tests failed fuzzing'
        )

        $counters = Get-TreeSitterFuzzCounters $lines

        $counters.Failed | Should Be 2
        @($counters.Failures).Count | Should Be 1
        $counters.Failures[0] | Should Be 'Error: Unexpected scope change in seed 1000930 with start seed 1000000'
    }

    It 'reports a run that listed no tests as executing none' {
        $counters = Get-TreeSitterFuzzCounters @('Error: could not compile the parser')

        $counters.Executed | Should Be 0
        $counters.Failed | Should Be 0
    }
}

Describe 'Get-NodeTestCounters' {
    It 'reads the totals node --test prints with the spec reporter' {
        $lines = @('✔ a test (1.2ms)', 'ℹ tests 8', 'ℹ suites 0', 'ℹ pass 7', 'ℹ fail 1', 'ℹ cancelled 0', 'ℹ skipped 0')

        $counters = Get-NodeTestCounters $lines

        $counters.Found | Should Be $true
        $counters.Executed | Should Be 8
        $counters.Passed | Should Be 7
        $counters.Failed | Should Be 1
    }

    It 'reads them from TAP output too' {
        $counters = Get-NodeTestCounters @('ok 1 - a test', '# tests 3', '# pass 3', '# fail 0')

        $counters.Executed | Should Be 3
        $counters.Failed | Should Be 0
    }

    It 'reports a run without totals as not found' {
        (Get-NodeTestCounters @('SyntaxError: Unexpected token')).Found | Should Be $false
    }
}
