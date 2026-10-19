#Requires -Version 5.1
<#
    What a build of the scale fixture did, read from its log at normal verbosity, and whether that
    is right: the first build builds every package with dpm and generates the C# of every Daml
    reference, the next one runs neither tool.
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

Export-ModuleMember -Function Measure-BuildLog, Test-ScaleBuild
