#Requires -Version 7.2
<#
.SYNOPSIS
    Runs test projects by category and writes TRX (and coverage) per target framework.

.DESCRIPTION
    Builds through build.ps1 (unless -NoBuild), then runs the test projects that
    test-gates.psd1 maps to each category, with a vstest filter on the Category trait, once per
    target framework. Results go to artifacts/test/<Category>/<tfm>/<project>/.

    Local runs are lenient: a category without tests is reported as N/A and skipped tests
    are allowed. -RequiredTools turns the run into a gate check for -Milestone (M0, M1, ... or
    PR for pull requests): every category of the gate runs, missing tools, a required project
    that executes no tests on one of its frameworks, any skip, any failure, a pending category
    that suddenly has tests, and coverage below the gate's thresholds all make the script exit
    non-zero. Test code sees EQDAML_REQUIRED_TOOLS=1 and must fail instead of skipping when a
    tool is missing.

.EXAMPLE
    pwsh scripts/test.ps1 -Category Unit
.EXAMPLE
    pwsh scripts/test.ps1 -Category All -Milestone PR -RequiredTools
.EXAMPLE
    pwsh scripts/test.ps1 -Category All -Milestone M0 -RequiredTools
#>
[CmdletBinding()]
param(
    [ValidateSet('Unit', 'Process', 'Editor', 'Contract', 'MSBuild', 'Integration', 'Grammar', 'Perf', 'All')]
    [string] $Category = 'Unit',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    # MSBuild tests come in two flavours: Fake (FakeDpm, runs anywhere) and Real (needs dpm + JDK).
    [ValidateSet('Fake', 'Real', 'All')]
    [string] $Backend = 'Fake',

    [ValidatePattern('^(M\d|PR)$')]
    [string] $Milestone,

    [switch] $RequiredTools,
    [switch] $NoBuild,
    [switch] $NoCoverage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\TestResults.psm1') -Force

$root = Get-RepoRoot
$resultsRoot = Join-Path $root 'artifacts\test'
$vstestCategories = 'Unit', 'Process', 'Editor', 'Contract', 'MSBuild', 'Integration'
$layout = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'test-gates.psd1')

$gate = $null
if ($Milestone) {
    if (-not $layout.Gates.ContainsKey($Milestone)) { throw "No gate definition for $Milestone in test-gates.psd1." }
    $gate = $layout.Gates[$Milestone]
}
if ($RequiredTools) {
    if (-not $gate) { throw '-RequiredTools needs -Milestone to know what is required.' }
    # A gate is all-or-nothing: checking one category would report green while the others
    # never ran.
    if ($Category -ne 'All') { throw "A gate run checks every category of $Milestone; use -Category All instead of -Category $Category." }
    if ($NoCoverage -and $gate.Coverage.Count -gt 0) { throw "$Milestone enforces coverage thresholds; -NoCoverage is not allowed with -RequiredTools." }
    # From M3 on, L4 has to run against the real dpm as well, not only the fake one.
    if ($gate.ContainsKey('MSBuildBackend')) {
        if ($PSBoundParameters.ContainsKey('Backend') -and $Backend -ne $gate.MSBuildBackend) {
            throw "$Milestone runs the MSBuild tests with -Backend $($gate.MSBuildBackend); -Backend $Backend would leave some out."
        }
        $Backend = $gate.MSBuildBackend
    }
}

$categories = if ($Category -ne 'All') { @($Category) }
              elseif ($gate) { @($gate.Required) + @($gate.Pending.Keys) }
              else { $vstestCategories }
$required = if ($RequiredTools) { @($gate.Required) } else { @() }
$pending = if ($RequiredTools) { $gate.Pending } else { @{} }

$problems = [System.Collections.Generic.List[string]]::new()

if ($RequiredTools) {
    $env:EQDAML_REQUIRED_TOOLS = '1'
    foreach ($tool in $gate.Tools) {
        $path = Find-Tool $tool
        if ($path) { Write-Host "Tool ${tool}: $path" }
        else { $problems.Add("required tool '$tool' not found") }
    }
}
else {
    Remove-Item Env:EQDAML_REQUIRED_TOOLS -ErrorAction SilentlyContinue
}

$testProjects = @{}
$testDir = Join-Path $root 'test'
if (Test-Path $testDir) {
    Get-ChildItem $testDir -Recurse -Include '*.Tests.csproj', '*.IntegrationTests.csproj' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object { $testProjects[$_.BaseName] = $_ }
}
$mapped = @($layout.Projects.Values | ForEach-Object { $_ }) | Sort-Object -Unique
$unmapped = @($testProjects.Keys | Where-Object { $_ -notin $mapped })
if ($unmapped) { throw "Test project(s) not mapped to any category in test-gates.psd1: $($unmapped -join ', ')." }
$missing = @($mapped | Where-Object { -not $testProjects.ContainsKey($_) })
if ($missing) { throw "test-gates.psd1 maps project(s) that don't exist under test/: $($missing -join ', ')." }

if (-not $NoBuild -and $testProjects.Count -gt 0) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Get-TargetFrameworks([string] $ProjectPath) {
    $doc = [xml](Get-Content $ProjectPath -Raw)
    $value = @($doc.SelectNodes('/Project/PropertyGroup/TargetFrameworks | /Project/PropertyGroup/TargetFramework') |
        ForEach-Object { $_.InnerText } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $value) { throw "No TargetFramework(s) in '$ProjectPath'." }
    $value.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() }
}

# Merges the reports of each framework into <category>/<tfm>/coverage (Cobertura + text summary
# for people) and returns a short note for the summary table.
function Merge-Coverage([string] $CategoryDir) {
    $notes = foreach ($tfmDir in Get-ChildItem $CategoryDir -Directory -ErrorAction SilentlyContinue) {
        $reports = @(Get-RunCoverageReports -ResultsRoot (Split-Path $CategoryDir -Parent) -Categories @(Split-Path $CategoryDir -Leaf) -Framework $tfmDir.Name)
        if (-not $reports) { continue }
        $target = Join-Path $tfmDir.FullName 'coverage'
        & dotnet tool run reportgenerator "-reports:$($reports.FullName -join ';')" "-targetdir:$target" '-reporttypes:Cobertura;TextSummary' '-verbosity:Warning' | Out-Host
        if ($LASTEXITCODE -ne 0) {
            $problems.Add("ReportGenerator failed for $($tfmDir.Name) in $(Split-Path $CategoryDir -Leaf) (exit code $LASTEXITCODE)")
            "$($tfmDir.Name) ?"
            continue
        }
        $summaryFile = Join-Path $target 'Summary.txt'
        $line = if (Test-Path $summaryFile) { Select-String -Path $summaryFile -Pattern '^\s*Line coverage:\s*(.+)$' | Select-Object -First 1 } else { $null }
        $rate = if ($line) { $line.Matches[0].Groups[1].Value.Trim() } else { '?' }
        "$($tfmDir.Name) $rate"
    }
    if ($notes) { ", coverage " + ($notes -join ' / ') } else { '' }
}

$runSettings = Join-Path $root 'test\coverlet.runsettings'
if (-not $NoCoverage) {
    & dotnet tool restore --tool-manifest (Join-Path $root 'dotnet-tools.json') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed (ReportGenerator).' }
}

$coverageCategories = 'Unit', 'Process', 'Editor', 'Contract', 'MSBuild'
$summary = foreach ($cat in $categories) {
    $isRequired = $required -contains $cat
    $outDir = Join-Path $resultsRoot $cat
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

    if ($cat -eq 'Grammar') {
        # The tree-sitter grammar: its CLI is pinned in the grammar's own package.json, the parser
        # is generated from grammar.js every time (it isn't committed), and `tree-sitter test`
        # runs the corpus. Its summary line is the only evidence; without it the run failed.
        $grammarDir = Join-Path $root 'grammar\tree-sitter-daml'
        if (-not (Test-Path (Join-Path $grammarDir 'grammar.js'))) {
            if ($isRequired) { $problems.Add("$cat is required but there is no grammar") }
            [pscustomobject]@{ Category = $cat; Status = 'N/A'; Note = 'no grammar' }
            continue
        }
        if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
            if ($isRequired) { $problems.Add("$cat is required but Node.js (npx) isn't installed") }
            [pscustomobject]@{ Category = $cat; Status = 'N/A'; Note = 'Node.js not found (bootstrap.ps1 -WithParser)' }
            continue
        }
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        $log = Join-Path $outDir 'tree-sitter-test.log'
        Push-Location $grammarDir
        try {
            $exitCode = 0
            if (-not (Test-Path 'node_modules')) {
                & npm ci --no-audit --no-fund | Out-Host
                $exitCode = $LASTEXITCODE
            }
            if ($exitCode -eq 0) {
                & npx tree-sitter generate | Out-Host
                $exitCode = $LASTEXITCODE
            }
            if ($exitCode -eq 0) {
                & npx tree-sitter test 2>&1 | Tee-Object -FilePath $log | Out-Host
                $exitCode = $LASTEXITCODE
            }
            # Random edits to every corpus test, parsed incrementally and undone: the tree must
            # come back as the test expects it. The edits come from a fixed seed, as in CI:
            # about one random seed in four hits a layout bug inherited from tree-sitter-haskell.
            # A TREE_SITTER_SEED of your own wins, to replay a failure.
            $fuzz = 'not run'
            $fuzzExit = 0
            $fuzzLog = Join-Path $outDir 'tree-sitter-fuzz.log'
            if ($exitCode -eq 0) {
                $ownSeed = [bool]$env:TREE_SITTER_SEED
                if (-not $ownSeed) { $env:TREE_SITTER_SEED = '1' }
                try {
                    & npx tree-sitter fuzz 2>&1 | Tee-Object -FilePath $fuzzLog | Out-Null
                    $fuzzExit = $LASTEXITCODE
                }
                finally {
                    if (-not $ownSeed) { Remove-Item Env:TREE_SITTER_SEED }
                }
            }
        }
        finally {
            Pop-Location
        }
        # The CLI exits 0 even when tests fail fuzzing, so its log decides as well. Each failure
        # names a seed; TREE_SITTER_SEED=<seed> with --iterations 1 and --include <test> replays it.
        $fuzzFailed = $false
        if (Test-Path $fuzzLog) {
            $f = Get-TreeSitterFuzzCounters (Get-Content $fuzzLog)
            $fuzzFailed = $fuzzExit -ne 0 -or $f.Failed -gt 0 -or $f.Executed -eq 0
            $fuzz = if ($fuzzFailed) { "FAILED ($($f.Failed) of $($f.Executed) tests, exit code $fuzzExit)" } else { "passed ($($f.Executed) tests)" }
            foreach ($failure in $f.Failures) { $problems.Add("$cat fuzz: $failure") }
        }
        # The grammar's own scripts (the corpus report) have node tests next to them. TAP, because
        # its summary is plain ASCII: PowerShell decodes node's output with the console code page,
        # which turns the spec reporter's symbols into something else.
        $scriptsLog = Join-Path $outDir 'node-test.log'
        & node --test --test-reporter=tap (Join-Path $root 'grammar/scripts/*.test.mjs') 2>&1 | Tee-Object -FilePath $scriptsLog | Out-Host
        $scriptsExit = $LASTEXITCODE
        # The corpus gate needs the real-world corpus (scripts/collect-corpus.ps1), which isn't
        # committed: without it the gate is skipped, unless the category is required. Its report
        # goes next to the logs, not over the committed grammar/docs/corpus.md.
        $corpusGate = 'skipped (no corpus)'
        $gateExit = 0
        if (Test-Path (Join-Path $root 'grammar\test\real-world\manifest.json')) {
            & node (Join-Path $root 'grammar/scripts/corpus-gate.mjs') --out (Join-Path $outDir 'corpus.md') 2>&1 | Tee-Object -FilePath (Join-Path $outDir 'corpus-gate.log') | Out-Host
            $gateExit = $LASTEXITCODE
            $corpusGate = if ($gateExit -eq 0) { 'passed' } else { 'FAILED' }
        }
        elseif ($isRequired) { $problems.Add("$cat is required but the corpus isn't collected (scripts/collect-corpus.ps1)") }
        $c = Get-TreeSitterTestCounters $(if (Test-Path $log) { Get-Content $log } else { @() })
        $s = Get-NodeTestCounters (Get-Content $scriptsLog)
        $status = if ($exitCode -ne 0 -or $c.Failed -gt 0 -or $c.QueryFailed -gt 0 -or -not $c.Found -or $scriptsExit -ne 0 -or $s.Failed -gt 0 -or -not $s.Found -or $gateExit -ne 0 -or $fuzzFailed) { 'FAILED' }
                  elseif ($c.Executed -eq 0) { 'N/A' }
                  else { 'PASSED' }
        if ($status -eq 'FAILED') { $problems.Add("$cat failed (tree-sitter exit code $exitCode, $($c.Failed) failing parse(s), $($c.QueryFailed) failing highlight/tag file(s), fuzz $fuzz; node exit code $scriptsExit, $($s.Failed) failing script test(s); corpus gate $corpusGate; logs in $outDir)") }
        if ($isRequired -and ($c.Executed -eq 0 -or $s.Executed -eq 0)) { $problems.Add("$cat is required but ran 0 tree-sitter or 0 script tests") }
        [pscustomobject]@{ Category = $cat; Status = $status; Note = "executed $($c.Executed + $c.QueryFiles + $s.Executed), passed $($c.Passed + $c.QueryFiles - $c.QueryFailed + $s.Passed), failed $($c.Failed + $c.QueryFailed + $s.Failed) (tree-sitter test $($c.Passed)/$($c.Executed), highlight/tag files $($c.QueryFiles - $c.QueryFailed)/$($c.QueryFiles) with $($c.Assertions) assertions, fuzz $fuzz, scripts $($s.Passed)/$($s.Executed), corpus gate $corpusGate)" }
        continue
    }

    if ($cat -notin $vstestCategories) {
        # Not a vstest run: Perf is BenchmarkDotNet (scripts/bench.ps1 -Check).
        if ($isRequired) { $problems.Add("$cat is required, but test.ps1 doesn't run it; use scripts/bench.ps1 -Check") }
        [pscustomobject]@{ Category = $cat; Status = 'N/A'; Note = 'run by scripts/bench.ps1 -Check' }
        continue
    }
    $projects = @($layout.Projects[$cat] | ForEach-Object { $testProjects[$_] })
    if (-not $projects) {
        if ($isRequired) { $problems.Add("$cat is required but no test project is mapped to it") }
        [pscustomobject]@{ Category = $cat; Status = 'N/A'; Note = 'no test projects' }
        continue
    }

    $filter = "Category=$cat"
    if ($cat -eq 'MSBuild' -and $Backend -ne 'All') { $filter += "&Backend=$Backend" }
    $collectCoverage = -not $NoCoverage -and $cat -in $coverageCategories

    # One run per target framework into its own folder: coverage files don't say which
    # framework produced them, and net48 and net10.0 numbers must never be mixed.
    $runs = foreach ($proj in $projects) {
        foreach ($tfm in Get-TargetFrameworks $proj.FullName) {
            $runDir = Join-Path $outDir "$tfm\$($proj.BaseName)"
            $testArgs = @(
                'test', $proj.FullName
                '--no-build', '--nologo'
                '--configuration', $Configuration
                '--framework', $tfm
                '--filter', $filter
                '--results-directory', $runDir
                '--logger', "trx;LogFilePrefix=$($proj.BaseName)"
            )
            if ($collectCoverage) { $testArgs += '--collect', 'XPlat Code Coverage', '--settings', $runSettings }
            & dotnet @testArgs | Out-Host
            $exitCode = $LASTEXITCODE
            $missing = if ($cat -eq 'Integration') { Get-IdeTestShortfall $proj.FullName $runDir } else { 0 }
            [pscustomobject]@{ Project = $proj.BaseName; Tfm = $tfm; ExitCode = $exitCode; Counters = Get-TrxCounters $runDir; Missing = $missing }
        }
    }

    $coverageNote = if ($collectCoverage) { Merge-Coverage $outDir } else { '' }

    $c = Get-TrxCounters $outDir
    $lost = [int]($runs | Measure-Object -Property Missing -Sum).Sum
    $verdict = Get-RunVerdict -Category $cat -Runs @($runs) -Totals $c -Lost $lost -Required $isRequired -Pending $(if ($pending.ContainsKey($cat)) { $pending[$cat] } else { $null })
    $verdict.Problems | ForEach-Object { $problems.Add($_) }
    $status = $verdict.Status
    $note = "executed $($c.Executed), passed $($c.Passed), failed $($c.Failed), skipped $($c.Skipped), trx $($c.Files)$coverageNote"
    if ($lost -gt 0) { $note += ", missing $lost" }
    if ($pending.ContainsKey($cat)) { $note += "; pending: $($pending[$cat])" }

    [pscustomobject]@{ Category = $cat; Status = $status; Note = $note }
}

$summary | Format-Table -AutoSize -Wrap | Out-Host

if ($RequiredTools -and $gate.Coverage.Count -gt 0) {
    $rows = foreach ($tfm in $gate.Coverage.Keys | Sort-Object) {
        $reports = @(Get-RunCoverageReports -ResultsRoot $resultsRoot -Categories @($categories | Where-Object { $_ -in $coverageCategories }) -Framework $tfm)
        foreach ($row in Get-CoverageVerdict -Measured (Measure-AssemblyCoverage $reports) -Framework $tfm -Thresholds $gate.Coverage[$tfm]) {
            if ($row.Problem) { $problems.Add($row.Problem) }
            $row
        }
    }
    $rows | Format-Table Assembly, Tfm, Status, Lines, Rate, Threshold -AutoSize | Out-Host
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }
    exit 1
}
exit 0
