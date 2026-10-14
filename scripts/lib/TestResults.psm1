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
    #>
    param([string[]] $Lines)

    $summary = $null
    foreach ($line in $Lines) {
        $match = [regex]::Match($line, 'Total parses:\s*(\d+);\s*successful parses:\s*(\d+);\s*failed parses:\s*(\d+)')
        if ($match.Success) { $summary = $match }
    }
    if ($null -eq $summary) {
        return [pscustomobject]@{ Found = $false; Executed = 0; Passed = 0; Failed = 0 }
    }
    [pscustomobject]@{
        Found    = $true
        Executed = [int] $summary.Groups[1].Value
        Passed   = [int] $summary.Groups[2].Value
        Failed   = [int] $summary.Groups[3].Value
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

Export-ModuleMember -Function Get-TrxCounters, Get-IdeTestShortfall, Get-RunCoverageReports, Get-TreeSitterTestCounters, Get-NodeTestCounters
