$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here '..\..\scripts\lib\ScaleBuild.psm1') -Force

Describe 'Measure-BuildLog' {
    It 'counts the dpm builds and the generator runs of a build log' {
        $lines = @(
            '  C:\Users\me\AppData\Roaming\dpm\bin\dpm.cmd build',
            '  Pkg01 -> C:\scale\Pkg01\.daml\dist\scale-pkg01-1.0.0.dar',
            '  "C:\Program Files\dpm\dpm.exe" build',
            '  Generated C# for ..\Pkg01\Pkg01.msbuildproj in 2.1 s.',
            '  Generated C# for ..\Pkg02\Pkg02.msbuildproj in 1.9 s.',
            '  C:\tools\dpm.cmd test',
            '  the build ran dpm build before'
        )

        $counts = Measure-BuildLog $lines

        $counts.DpmBuilds | Should Be 2
        $counts.GeneratorRuns | Should Be 2
    }
}

Describe 'Test-ScaleBuild' {
    $sizes = [pscustomobject] @{ Packages = 20; References = 20 }

    It 'passes a cold build of every package and reference and a warm build that runs nothing' {
        $result = [pscustomobject] @{
            Sizes = $sizes
            Cold  = [pscustomobject] @{ DpmBuilds = 20; GeneratorRuns = 20 }
            Warm  = [pscustomobject] @{ DpmBuilds = 0; GeneratorRuns = 0 }
        }

        @(Test-ScaleBuild $result).Count | Should Be 0
    }

    It 'names each thing that went wrong' {
        $result = [pscustomobject] @{
            Sizes = $sizes
            Cold  = [pscustomobject] @{ DpmBuilds = 21; GeneratorRuns = 19 }
            Warm  = [pscustomobject] @{ DpmBuilds = 1; GeneratorRuns = 2 }
        }

        $problems = @(Test-ScaleBuild $result)

        $problems.Count | Should Be 4
        ($problems -join "`n") | Should Match 'cold build ran dpm build 21 times for 20 packages'
        ($problems -join "`n") | Should Match 'cold build generated C# 19 times for 20 references'
        ($problems -join "`n") | Should Match 'warm build ran dpm build 1 time'
        ($problems -join "`n") | Should Match 'warm build generated C# 2 times'
    }
}

Describe 'Test-ScaleIde' {
    function Measured([double] $cold = 4, [double[]] $warm = @(2.1, 2.3), [double] $lensP95 = 0.8, [double] $sync = 10.7) {
        [pscustomobject] @{ ColdDiagnosticSeconds = $cold; WarmDiagnosticSeconds = $warm; WarmLensP95Seconds = $lensP95; SyncSeconds = $sync }
    }

    It 'passes times within the targets, on them included' {
        @(Test-ScaleIde (Measured)).Count | Should Be 0
        @(Test-ScaleIde (Measured -cold 30 -lensP95 2 -sync 20)).Count | Should Be 0
    }

    It 'names each time over its target' {
        $problems = @(Test-ScaleIde (Measured -warm @(2.1, 31.5) -lensP95 2.4 -sync 20.5))

        $problems.Count | Should Be 3
        ($problems -join "`n") | Should Match 'first diagnostic took 31.5 s; the target is 30 s'
        ($problems -join "`n") | Should Match 'C# references rows took 2.4 s at p95; the target is 2 s'
        ($problems -join "`n") | Should Match 'Background sync took 20.5 s; the target is 20 s'
    }

    It 'counts a time the report lacks as a problem, not a pass' {
        $problems = @(Test-ScaleIde ([pscustomobject] @{ ColdDiagnosticSeconds = 4; WarmDiagnosticSeconds = @(2) }))

        $problems.Count | Should Be 2
        ($problems -join "`n") | Should Match 'WarmLensP95Seconds'
        ($problems -join "`n") | Should Match 'SyncSeconds'
    }
}
