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
