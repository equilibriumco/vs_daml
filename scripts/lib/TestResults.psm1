# Reads what a test run left behind: TRX files, and the IDE test sources they should account for.

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

# The IDE harness can lose results: when the Visual Studio it drives dies, the runner stops
# reporting and the run still looks green. Count what the sources declare instead.
function Get-IdeTestShortfall {
    param([string] $ProjectPath, [string] $Directory)

    # One attribute per test; a commented-out one doesn't count.
    $declared = @(Get-ChildItem (Split-Path $ProjectPath) -Filter '*.cs' -Recurse |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Select-String -Pattern '\[IdeFact\b' |
        Where-Object { $_.Line -notmatch '^\s*//' }).Count
    $reported = 0
    foreach ($trx in Get-ChildItem $Directory -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue) {
        $run = ([xml](Get-Content $trx.FullName -Raw)).TestRun
        if ($run.PSObject.Properties['Results']) {
            # The harness adds one result for the Visual Studio instance itself.
            $reported += @($run.Results.ChildNodes | Where-Object { $_.testName -notlike 'Xunit.Instances.*' }).Count
        }
    }
    [Math]::Max(0, $declared - $reported)
}

# Only categories selected for this invocation can supply coverage evidence. Merged reports and
# projects that executed no tests are excluded; reports are kept separate by runtime.
function Get-RunCoverageReports {
    param([string] $ResultsRoot, [string[]] $Categories, [string] $Framework)
    foreach ($category in $Categories) {
        $tfmDir = Join-Path $ResultsRoot "$category\$Framework"
        Get-ChildItem -LiteralPath $tfmDir -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'coverage' -and (Get-TrxCounters $_.FullName).Executed -gt 0 } |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Recurse -Filter 'coverage.cobertura.xml' }
    }
}

function Get-TreeSitterTestCounters {
    <#
    .SYNOPSIS
        The totals of a `tree-sitter test` run, from the summary line it prints last:
        "Total parses: N; successful parses: P; failed parses: F; ...". Found is false when
        there is no such line (the parser didn't compile, the CLI failed), so a broken run can't
        pass for one with no failures.
        The highlight and tag test files, listed under "syntax highlighting:" and "tags:", are
        counted apart: QueryFiles, QueryFailed (a file without "(N assertions)") and Assertions.
    #>
    param([string[]] $Lines)

    $summary = $null
    $inQueries = $false
    $queryFiles = 0
    $queryFailed = 0
    $assertions = 0
    foreach ($line in $Lines) {
        $plain = [regex]::Replace($line, "`e\[[0-9;]*m", '')
        $match = [regex]::Match($plain, 'Total parses:\s*(\d+);\s*successful parses:\s*(\d+);\s*failed parses:\s*(\d+)')
        if ($match.Success) { $summary = $match }
        if ($plain -match '^(syntax highlighting|tags):\s*$') { $inQueries = $true; continue }
        if ($plain -match '^\S') { $inQueries = $false }
        if ($inQueries) {
            $file = [regex]::Match($plain, '^\s+\d+\.\s+\S+\s+\S.*?(\((\d+) assertions\))?\s*$')
            if ($file.Success) {
                $queryFiles++
                if ($file.Groups[2].Success) { $assertions += [int] $file.Groups[2].Value } else { $queryFailed++ }
            }
        }
    }
    if ($null -eq $summary) {
        return [pscustomobject]@{ Found = $false; Executed = 0; Passed = 0; Failed = 0; QueryFiles = $queryFiles; QueryFailed = $queryFailed; Assertions = $assertions }
    }
    [pscustomobject]@{
        Found       = $true
        Executed    = [int] $summary.Groups[1].Value
        Passed      = [int] $summary.Groups[2].Value
        Failed      = [int] $summary.Groups[3].Value
        QueryFiles  = $queryFiles
        QueryFailed = $queryFailed
        Assertions  = $assertions
    }
}

function Get-TreeSitterFuzzCounters {
    <#
    .SYNOPSIS
        What a `tree-sitter fuzz` run found, from its output: the CLI exits 0 even when tests fail
        fuzzing, so the output is the evidence. Executed counts the corpus tests it listed
        ("  12. daml - corpus - ..."); Failed comes from the line it prints last when any failed,
        "N daml corpus tests failed fuzzing", or from the lines naming the failures when that one
        is missing. Failures are those lines ("Incorrect parse for <test> - seed S", "Incorrect
        initial parse for <test>", "Unexpected scope change in seed S ...", which may follow
        "Error: " on the same line).
    #>
    param([string[]] $Lines)

    $executed = 0
    $failed = 0
    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $Lines) {
        $plain = [regex]::Replace($line, "`e\[[0-9;]*m", '').TrimEnd()
        if ($plain -match '^\s+\d+\.\s+\S') { $executed++ }
        elseif ($plain -match '^(\d+) .*corpus tests failed fuzzing$') { $failed = [int] $Matches[1] }
        elseif ($plain -match '^(Incorrect (initial )?parse for |(Error: )?Unexpected scope change in seed )') { $failures.Add($plain) }
    }
    [pscustomobject]@{
        Executed = $executed
        Failed   = [Math]::Max($failed, $failures.Count)
        Failures = $failures.ToArray()
    }
}

function Get-NodeTestCounters {
    <#
    .SYNOPSIS
        The totals of a `node --test` run, as its spec ("ℹ tests 8") or TAP ("# tests 8") reporter
        prints them. Found is false when the totals are missing, e.g. a test file didn't load.
    #>
    param([string[]] $Lines)

    $values = @{}
    foreach ($line in $Lines) {
        $match = [regex]::Match($line, '^\s*(?:ℹ|#)\s+(tests|pass|fail)\s+(\d+)\s*$')
        if ($match.Success) { $values[$match.Groups[1].Value] = [int] $match.Groups[2].Value }
    }
    if (-not $values.ContainsKey('tests')) {
        return [pscustomobject]@{ Found = $false; Executed = 0; Passed = 0; Failed = 0 }
    }
    [pscustomobject]@{
        Found    = $true
        Executed = $values['tests']
        Passed   = [int] $values['pass']
        Failed   = [int] $values['fail']
    }
}

<#
.SYNOPSIS
    The verdict on one category's runs: FAILED, N/A when nothing ran, or PASSED, and the problems
    that make a gate run fail.

.DESCRIPTION
    Runs are one per test project and framework, each with Project, Tfm, ExitCode and Counters
    (Executed, Skipped); Totals are the category's counters (Executed, Failed). A required
    category has to execute tests in every run, without skips, so tests vanishing from one project
    or framework can't hide behind the totals of the others. A pending category (Pending holds its
    reason) that has tests now has to be moved to Required.
#>
function Get-RunVerdict {
    param(
        [Parameter(Mandatory)] [string] $Category,
        [object[]] $Runs = @(),
        [Parameter(Mandatory)] [object] $Totals,
        [int] $Lost = 0,
        [bool] $Required = $false,
        [string] $Pending = $null
    )
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($Lost -gt 0) { $problems.Add("$Category lost the results of $Lost test(s): the test host or Visual Studio stopped reporting") }
    $status = if ($Totals.Failed -gt 0 -or $Lost -gt 0 -or @($Runs | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0) { 'FAILED' }
              elseif ($Totals.Executed -eq 0) { 'N/A' }
              else { 'PASSED' }
    if ($status -eq 'FAILED') { $problems.Add("$Category has failing tests") }
    if ($Required) {
        foreach ($run in $Runs) {
            if ($run.Counters.Executed -eq 0) { $problems.Add("$Category is required but $($run.Project) executed 0 tests on $($run.Tfm)") }
            if ($run.Counters.Skipped -gt 0) { $problems.Add("$Category is required but $($run.Project) skipped $($run.Counters.Skipped) test(s) on $($run.Tfm)") }
        }
    }
    if ($Pending -and $Totals.Executed -gt 0) { $problems.Add("$Category is pending but now has tests; move it to Required in test-gates.psd1") }
    [pscustomobject]@{ Status = $status; Problems = $problems.ToArray() }
}

<#
.SYNOPSIS
    Line coverage per product assembly, merged across Cobertura reports: a line counts as covered
    when any run hit it.

.DESCRIPTION
    Computed from the raw reports because ReportGenerator drops assemblies that have no coverable
    lines, and "no code" must stay distinguishable from "not measured". Returns assembly name ->
    Valid, Covered and Rate (null when there are no lines).
#>
function Measure-AssemblyCoverage([object[]] $Reports) {
    $lines = @{}
    foreach ($report in $Reports) {
        $path = if ($report -is [System.IO.FileInfo]) { $report.FullName } else { [string]$report }
        $xml = [xml](Get-Content -LiteralPath $path -Raw)
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

<#
.SYNOPSIS
    One row per assembly a gate sets a threshold for on Framework: OK, LOW, NO CODE (measured, but
    no coverable lines) or MISSING (no evidence at all), with the problem a gate run fails on.
#>
function Get-CoverageVerdict {
    param(
        [Parameter(Mandatory)] [hashtable] $Measured,
        [Parameter(Mandatory)] [string] $Framework,
        [Parameter(Mandatory)] [hashtable] $Thresholds
    )
    foreach ($assembly in $Thresholds.Keys | Sort-Object) {
        $threshold = $Thresholds[$assembly]
        $m = $Measured[$assembly]
        $status = if (-not $m) { 'MISSING' } elseif ($m.Valid -eq 0) { 'NO CODE' } elseif ($m.Rate -ge $threshold) { 'OK' } else { 'LOW' }
        $problem = switch ($status) {
            'MISSING' { "coverage: $assembly is absent from the $Framework reports (no evidence)" }
            'LOW' { 'coverage: {0} on {1} is {2:0.0}%, below {3}%' -f $assembly, $Framework, $m.Rate, $threshold }
            default { $null }
        }
        [pscustomobject]@{
            Assembly = $assembly; Tfm = $Framework; Status = $status; Threshold = "$threshold%"
            Lines = if ($m) { "$($m.Covered)/$($m.Valid)" } else { '-' }
            Rate = if ($m -and $m.Valid) { '{0:0.0}%' -f $m.Rate } else { '-' }
            Problem = $problem
        }
    }
}

Export-ModuleMember -Function Get-TrxCounters, Get-IdeTestShortfall, Get-RunCoverageReports, Get-TreeSitterTestCounters, Get-TreeSitterFuzzCounters, Get-NodeTestCounters, Get-RunVerdict, Measure-AssemblyCoverage, Get-CoverageVerdict
