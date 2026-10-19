#Requires -Version 5.1
<#
    The design-time build overhead of Daml.Build.CSharp and its budget. A driver project runs the
    design-time build of a C# project several times in one MSBuild process and prints one line per
    run: DAML-DTB-RUN <run> <ticks>. The first run pays for loading and JIT-compiling everything once
    (the cold run); the rest are what Visual Studio sees for every design-time build after that
    (the warm runs). The budget applies to the warm overhead; the cold one is reported.
#>

function Get-DesignTimeBudgetMs {
    [CmdletBinding()]
    [OutputType([double])]
    param()

    return 200
}

function Read-DesignTimeRuns {
    <#
    .SYNOPSIS
        The milliseconds of every run in the driver's output, ordered by run number.
    #>
    [CmdletBinding()]
    param([AllowEmptyCollection()][string[]] $Lines)

    $runs = @{}
    foreach ($line in $Lines) {
        if ($line -match 'DAML-DTB-RUN (\d+) (\d+)') {
            $run = [int] $Matches[1]
            if ($runs.ContainsKey($run)) { throw "The design-time driver reported run $run twice." }
            $runs[$run] = [double] $Matches[2] / 10000
        }
    }
    if ($runs.Count -eq 0) { throw 'The output has no design-time runs (DAML-DTB-RUN lines).' }
    return , @($runs.Keys | Sort-Object | ForEach-Object { $runs[$_] })
}

function Get-Median {
    [CmdletBinding()]
    [OutputType([double])]
    param([double[]] $Values)

    $sorted = @($Values | Sort-Object)
    $middle = [int] [Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

function Measure-DesignTimeOverhead {
    <#
    .SYNOPSIS
        The cold and warm design-time builds of the project without Daml (the baseline) and with it.
    .PARAMETER Baseline
        The runs of the project without Daml.Build.CSharp, in milliseconds, the first being the cold one.
    .PARAMETER WithDaml
        The runs of the same project with Daml.Build.CSharp and a Daml reference.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][double[]] $Baseline,
        [Parameter(Mandatory)][double[]] $WithDaml
    )

    if ($Baseline.Count -ne $WithDaml.Count) { throw 'Both projects need the same number of runs.' }
    if ($Baseline.Count -lt 2) { throw 'The measurement needs at least one warm run after the cold one.' }
    $warmBaseline = Get-Median ($Baseline | Select-Object -Skip 1)
    $warmWithDaml = Get-Median ($WithDaml | Select-Object -Skip 1)
    [pscustomobject] @{
        ColdBaselineMs = $Baseline[0]
        ColdWithDamlMs = $WithDaml[0]
        ColdOverheadMs = $WithDaml[0] - $Baseline[0]
        WarmBaselineMs = $warmBaseline
        WarmWithDamlMs = $warmWithDaml
        WarmOverheadMs = $warmWithDaml - $warmBaseline
        WarmRuns       = $Baseline.Count - 1
    }
}

function Test-DesignTimeBudget {
    <#
    .SYNOPSIS
        Whether the warm overhead of a measurement is within the budget.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Result,
        [double] $BudgetMs = (Get-DesignTimeBudgetMs)
    )

    if ($null -eq $Result.PSObject.Properties['WarmOverheadMs'] -or $null -eq $Result.WarmOverheadMs) {
        throw 'The measurement has no WarmOverheadMs.'
    }
    $overhead = [double] $Result.WarmOverheadMs
    $passed = $overhead -le $BudgetMs
    $verdict = if ($passed) { 'within' } else { 'over' }
    [pscustomobject] @{
        Passed   = $passed
        BudgetMs = $BudgetMs
        Message  = ('Warm design-time overhead of Daml.Build.CSharp: {0:0} ms, {1} the budget of {2:0} ms.' -f $overhead, $verdict, $BudgetMs)
    }
}

Export-ModuleMember -Function Get-DesignTimeBudgetMs, Read-DesignTimeRuns, Get-Median, Measure-DesignTimeOverhead, Test-DesignTimeBudget
