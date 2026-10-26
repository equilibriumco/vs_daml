#Requires -Version 5.1
<#
    What a build of the scale fixture did, read from its log at normal verbosity, and whether that
    is right: the first build builds every package with dpm and generates the C# of every Daml
    reference, the next one runs neither tool. And whether Visual Studio answered on the fixture
    within the test strategy's targets.
#>

function Measure-BuildLog {
    <#
    .SYNOPSIS
        How many times the build ran dpm build (the command lines MSBuild logs) and the C# generator.
    #>
    [CmdletBinding()]
    param([AllowEmptyCollection()][string[]] $Lines)

    $dpm = @($Lines | Where-Object { $_ -match '\\dpm(\.cmd|\.exe)"?\s+build\s*$' }).Count
    $generator = @($Lines | Where-Object { $_ -match '\bGenerated C# for\b' }).Count
    [pscustomobject] @{ DpmBuilds = $dpm; GeneratorRuns = $generator }
}

function Test-ScaleBuild {
    <#
    .SYNOPSIS
        What is wrong with a measurement, one line each; nothing when it is right.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Result)

    function Times([int] $n) { if ($n -eq 1) { '1 time' } else { "$n times" } }

    if ($Result.Cold.DpmBuilds -ne $Result.Sizes.Packages) {
        "The cold build ran dpm build $(Times $Result.Cold.DpmBuilds) for $($Result.Sizes.Packages) packages."
    }
    if ($Result.Cold.GeneratorRuns -ne $Result.Sizes.References) {
        "The cold build generated C# $(Times $Result.Cold.GeneratorRuns) for $($Result.Sizes.References) references."
    }
    if ($Result.Warm.DpmBuilds -ne 0) {
        "The warm build ran dpm build $(Times $Result.Warm.DpmBuilds); it had nothing to build."
    }
    if ($Result.Warm.GeneratorRuns -ne 0) {
        "The warm build generated C# $(Times $Result.Warm.GeneratorRuns); it had nothing to generate."
    }
}

function Test-ScaleIde {
    <#
    .SYNOPSIS
        What in Visual Studio's measurement of the scale fixture (ide.json, from ScaleTests) is over
        the test strategy's targets, one line each; nothing when every time is within them.
    .DESCRIPTION
        The test itself only fails on a step that never finishes: the times depend on the machine
        and its load, so they are judged here, on a machine the measurement is meant for.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Result)

    function Seconds([double] $value) { $value.ToString('0.##', [Globalization.CultureInfo]::InvariantCulture) }

    $missing = @('ColdDiagnosticSeconds', 'WarmDiagnosticSeconds', 'WarmLensP95Seconds', 'SyncSeconds') |
        Where-Object { $null -eq $Result.PSObject.Properties[$_] -or $null -eq $Result.$_ }
    foreach ($name in $missing) {
        "The measurement has no $name."
    }

    if ($missing -notcontains 'ColdDiagnosticSeconds' -and $missing -notcontains 'WarmDiagnosticSeconds') {
        $slowest = (@($Result.ColdDiagnosticSeconds) + @($Result.WarmDiagnosticSeconds) | Measure-Object -Maximum).Maximum
        if ($slowest -gt 30) {
            "A first diagnostic took $(Seconds $slowest) s; the target is 30 s."
        }
    }
    if ($missing -notcontains 'WarmLensP95Seconds' -and $Result.WarmLensP95Seconds -gt 2) {
        "The C# references rows took $(Seconds $Result.WarmLensP95Seconds) s at p95; the target is 2 s."
    }
    if ($missing -notcontains 'SyncSeconds' -and $Result.SyncSeconds -gt 20) {
        "Background sync took $(Seconds $Result.SyncSeconds) s; the target is 20 s."
    }
}

Export-ModuleMember -Function Measure-BuildLog, Test-ScaleBuild, Test-ScaleIde
