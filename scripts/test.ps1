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
    non-zero. Test code sees DAML_REQUIRED_TOOLS=1 and must fail instead of skipping when a
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
    [ValidateSet('Unit', 'Editor', 'Contract', 'MSBuild', 'Integration', 'Grammar', 'Perf', 'All')]
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

$root = Get-RepoRoot
$resultsRoot = Join-Path $root 'artifacts\test'
$vstestCategories = 'Unit', 'Editor', 'Contract', 'MSBuild', 'Integration'
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
}

$categories = if ($Category -ne 'All') { @($Category) }
              elseif ($gate) { @($gate.Required) + @($gate.Pending.Keys) }
              else { $vstestCategories }
$required = if ($RequiredTools) { @($gate.Required) } else { @() }
$pending = if ($RequiredTools) { $gate.Pending } else { @{} }

$problems = [System.Collections.Generic.List[string]]::new()

if ($RequiredTools) {
    $env:DAML_REQUIRED_TOOLS = '1'
    foreach ($tool in $gate.Tools) {
        $path = Find-Tool $tool
        if ($path) { Write-Host "Tool ${tool}: $path" }
        else { $problems.Add("required tool '$tool' not found") }
    }
}
else {
    Remove-Item Env:DAML_REQUIRED_TOOLS -ErrorAction SilentlyContinue
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

function Get-TrxCounters {
    param([string] $Directory)

    # Count per-test outcomes: the summary Counters element leaves notExecuted at 0 even
    # when xUnit skips a test, so skips would go unnoticed.
    $sum = [ordered]@{ Total = 0; Executed = 0; Passed = 0; Failed = 0; Skipped = 0; Files = 0 }
    foreach ($trx in Get-ChildItem $Directory -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue) {
        $sum.Files++
        $run = ([xml](Get-Content $trx.FullName -Raw)).TestRun
        $results = if ($run.PSObject.Properties['Results']) { @($run.Results.ChildNodes) } else { @() }
        foreach ($r in $results) {
            $sum.Total++
            switch ($r.outcome) {
                'Passed' { $sum.Executed++; $sum.Passed++ }
                'NotExecuted' { $sum.Skipped++ }
                default { $sum.Executed++; $sum.Failed++ }
            }
        }
        # A crashed test host can leave no results at all; the run outcome still says so.
        if ($run.ResultSummary.outcome -in 'Error', 'Aborted' -and $results.Count -eq 0) { $sum.Failed++ }
    }
    [pscustomobject]$sum
}

function Get-TargetFrameworks([string] $ProjectPath) {
    $doc = [xml](Get-Content $ProjectPath -Raw)
    $value = @($doc.SelectNodes('/Project/PropertyGroup/TargetFrameworks | /Project/PropertyGroup/TargetFramework') |
        ForEach-Object { $_.InnerText } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $value) { throw "No TargetFramework(s) in '$ProjectPath'." }
    $value.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() }
}

# Raw coverlet reports of the project runs (under one framework folder) that executed tests.
# A project with no tests in this category still emits a report in which its product
# assemblies show 0%; those don't count.
function Get-CoverageReports([string] $TfmDir) {
    @(Get-ChildItem $TfmDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'coverage' -and (Get-TrxCounters $_.FullName).Executed -gt 0 } |
        ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter 'coverage.cobertura.xml' })
}

# Line coverage per product assembly, merged across reports: a line counts as covered when any
# run hit it. Computed from the raw reports because ReportGenerator drops assemblies that have
# no coverable lines, and "no code" must stay distinguishable from "not measured".
function Measure-AssemblyCoverage([System.IO.FileInfo[]] $Reports) {
    $lines = @{}
    foreach ($report in $Reports) {
        $xml = [xml](Get-Content $report.FullName -Raw)
        foreach ($package in $xml.SelectNodes('//packages/package')) {
            $name = $package.GetAttribute('name')
            if (-not $lines.ContainsKey($name)) { $lines[$name] = @{} }
            foreach ($class in $package.SelectNodes('classes/class')) {
                $file = $class.GetAttribute('filename')
                foreach ($line in $class.SelectNodes('lines/line')) {
                    $key = "$file|$($line.GetAttribute('number'))"
                    $hit = [long]$line.GetAttribute('hits') -gt 0
                    $lines[$name][$key] = $hit -or ($lines[$name].ContainsKey($key) -and $lines[$name][$key])
                }
            }
        }
    }
    $result = @{}
    foreach ($name in $lines.Keys) {
        $valid = $lines[$name].Count
        $covered = @($lines[$name].Values | Where-Object { $_ }).Count
        $result[$name] = [pscustomobject]@{ Valid = $valid; Covered = $covered; Rate = if ($valid) { 100.0 * $covered / $valid } else { $null } }
    }
    $result
}

# Merges the reports of each framework into <category>/<tfm>/coverage (Cobertura + text summary
# for people) and returns a short note for the summary table.
function Merge-Coverage([string] $CategoryDir) {
    $notes = foreach ($tfmDir in Get-ChildItem $CategoryDir -Directory -ErrorAction SilentlyContinue) {
        $reports = Get-CoverageReports $tfmDir.FullName
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

$coverageCategories = 'Unit', 'Editor', 'Contract', 'MSBuild'
$summary = foreach ($cat in $categories) {
    $isRequired = $required -contains $cat
    $outDir = Join-Path $resultsRoot $cat
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

    if ($cat -notin $vstestCategories) {
        # Grammar runs through tree-sitter and Perf through BenchmarkDotNet; neither exists yet.
        if ($isRequired) { $problems.Add("$cat is required but has no runner yet") }
        [pscustomobject]@{ Category = $cat; Status = 'N/A'; Note = 'no runner yet' }
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
            [pscustomobject]@{ Project = $proj.BaseName; Tfm = $tfm; ExitCode = $LASTEXITCODE; Counters = Get-TrxCounters $runDir }
        }
    }

    $coverageNote = if ($collectCoverage) { Merge-Coverage $outDir } else { '' }

    $c = Get-TrxCounters $outDir
    $status = if ($c.Failed -gt 0 -or ($runs | Where-Object { $_.ExitCode -ne 0 })) { 'FAILED' }
              elseif ($c.Executed -eq 0) { 'N/A' }
              else { 'PASSED' }
    if ($status -eq 'FAILED') { $problems.Add("$cat has failing tests") }
    if ($isRequired) {
        # Per project and framework, so tests vanishing from one of them can't hide behind the
        # totals of the others.
        foreach ($run in $runs) {
            if ($run.Counters.Executed -eq 0) { $problems.Add("$cat is required but $($run.Project) executed 0 tests on $($run.Tfm)") }
            if ($run.Counters.Skipped -gt 0) { $problems.Add("$cat is required but $($run.Project) skipped $($run.Counters.Skipped) test(s) on $($run.Tfm)") }
        }
    }
    $note = "executed $($c.Executed), passed $($c.Passed), failed $($c.Failed), skipped $($c.Skipped), trx $($c.Files)$coverageNote"
    if ($pending.ContainsKey($cat)) {
        if ($c.Executed -gt 0) { $problems.Add("$cat is pending in $Milestone but now has tests; move it to Required in test-gates.psd1") }
        $note += "; pending: $($pending[$cat])"
    }

    [pscustomobject]@{ Category = $cat; Status = $status; Note = $note }
}

$summary | Format-Table -AutoSize -Wrap | Out-Host

if ($RequiredTools -and $gate.Coverage.Count -gt 0) {
    $rows = foreach ($tfm in $gate.Coverage.Keys | Sort-Object) {
        $reports = @($coverageCategories | ForEach-Object { Get-CoverageReports (Join-Path $resultsRoot "$_\$tfm") })
        $measured = Measure-AssemblyCoverage $reports
        foreach ($assembly in $gate.Coverage[$tfm].Keys | Sort-Object) {
            $threshold = $gate.Coverage[$tfm][$assembly]
            $m = $measured[$assembly]
            $status = if (-not $m) { 'MISSING' } elseif ($m.Valid -eq 0) { 'NO CODE' } elseif ($m.Rate -ge $threshold) { 'OK' } else { 'LOW' }
            if ($status -eq 'MISSING') { $problems.Add("coverage: $assembly is absent from the $tfm reports (no evidence)") }
            if ($status -eq 'LOW') { $problems.Add(("coverage: {0} on {1} is {2:0.0}%, below {3}%" -f $assembly, $tfm, $m.Rate, $threshold)) }
            [pscustomobject]@{
                Assembly = $assembly; Tfm = $tfm; Status = $status; Threshold = "$threshold%"
                Lines = if ($m) { "$($m.Covered)/$($m.Valid)" } else { '-' }
                Rate = if ($m -and $m.Valid) { '{0:0.0}%' -f $m.Rate } else { '-' }
            }
        }
    }
    $rows | Format-Table Assembly, Tfm, Status, Lines, Rate, Threshold -AutoSize | Out-Host
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }
    exit 1
}
exit 0
