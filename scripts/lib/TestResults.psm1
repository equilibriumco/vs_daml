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

Export-ModuleMember -Function Get-TrxCounters, Get-IdeTestShortfall
