#Requires -Version 7.2
<#
.SYNOPSIS
    Builds the scale fixture from the command line and reports what it took.

.DESCRIPTION
    Writes the scale fixture (new-scale-fixture.ps1) on this build's packages (run pack.ps1 first)
    in a temporary folder, builds its solution with the 64-bit MSBuild.exe of the Visual Studio the
    extension is tested in, the real dpm and the pinned C# generator, and builds it once more without
    a change. The report in artifacts/scale gives both times and how often each tool ran, with
    binlogs of both builds. -Check fails when the first build didn't build every package and
    generate every reference's C#, or the second one ran a tool.

.EXAMPLE
    pwsh scripts/measure-scale.ps1 -Check
.EXAMPLE
    pwsh scripts/measure-scale.ps1 -Packages 4 -CSharpProjects 2
#>
[CmdletBinding()]
param(
    [switch] $Check,
    [switch] $NoRun,
    # Lets the first build install the pinned C# generator on a machine that hasn't got it.
    [switch] $AllowToolInstall,
    # Leaves the fixture in place, to look at or to open in Visual Studio.
    [switch] $Keep,
    [ValidateRange(1, 100)] [int] $Packages = 20,
    [ValidateRange(1, 500)] [int] $ModulesPerPackage = 50,
    [ValidateRange(1, 500)] [int] $TemplatesPerPackage = 25,
    [ValidateRange(1, 50)] [int] $CSharpProjects = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\MeasureWorkspace.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\ScaleBuild.psm1') -Force

$root = Get-RepoRoot
$artifacts = Join-Path $root 'artifacts\scale'
$resultPath = Join-Path $artifacts 'result.json'

if (-not $NoRun) {
    $thisBuild = Get-ThisBuildPackages -RepositoryRoot $root
    $vs = Select-TestVsInstance
    $msbuild = Join-Path $vs.Path 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (-not (Test-Path $msbuild)) { $msbuild = $vs.MSBuildPath }
    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $workspace = New-MeasureWorkspace -RepositoryRoot $root -Prefix 'dsc-'
    $fixture = Join-Path $workspace 'Scale'
    $savedLanguage = $env:VSLANG
    try {
        # The logs are read; MSBuild's own lines stay in English for that.
        $env:VSLANG = '1033'
        & (Join-Path $PSScriptRoot 'new-scale-fixture.ps1') -Path $fixture -SdkPackageVersion $thisBuild.Version -RuntimeVersion $thisBuild.RuntimeVersion `
            -PackagePrefix $thisBuild.Prefix -Packages $Packages -ModulesPerPackage $ModulesPerPackage -TemplatesPerPackage $TemplatesPerPackage -CSharpProjects $CSharpProjects
        $references = @(Get-ChildItem $fixture -Recurse -Filter *.csproj | Select-String -Pattern '<DamlReference ').Count
        $files = @(Get-ChildItem $fixture -Recurse -Filter *.daml).Count
        $lines = (Get-ChildItem $fixture -Recurse -Filter *.daml | ForEach-Object { @(Get-Content $_.FullName).Count } | Measure-Object -Sum).Sum

        $builds = [ordered] @{}
        foreach ($name in 'Cold', 'Warm') {
            Write-Host "$name build of $Packages Daml packages and $CSharpProjects C# projects..."
            $log = Join-Path $artifacts "$name.log"
            $arguments = @((Join-Path $fixture 'Scale.slnx'), '-restore', '-m', '-nologo', '-v:m', '-nr:false',
                "-bl:$(Join-Path $artifacts "$name.binlog")", "-flp:verbosity=normal;logfile=$log")
            if ($AllowToolInstall) { $arguments += '-p:DamlAllowToolInstall=true' }
            $clock = [Diagnostics.Stopwatch]::StartNew()
            & $msbuild @arguments | Out-Null
            $exit = $LASTEXITCODE
            $clock.Stop()
            if ($exit -ne 0) {
                Get-Content $log | Select-String -Pattern ': error ' | Select-Object -First 20 | ForEach-Object { $_.Line } | Out-Host
                throw "The $($name.ToLowerInvariant()) build failed with exit code $exit; its log is $log."
            }
            $counts = Measure-BuildLog (Get-Content $log)
            $builds[$name] = [ordered] @{ Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1); DpmBuilds = $counts.DpmBuilds; GeneratorRuns = $counts.GeneratorRuns }
        }

        [ordered] @{
            Date         = (Get-Date).ToString('s')
            Machine      = Get-MeasureHost
            VisualStudio = "$($vs.DisplayName) $($vs.Version)"
            MSBuild      = $msbuild
            PackageVersion = $thisBuild.Version
            Sizes        = [ordered] @{ Packages = $Packages; Files = $files; Lines = $lines; Templates = $Packages * $TemplatesPerPackage; CSharpProjects = $CSharpProjects; References = $references }
            Cold         = $builds.Cold
            Warm         = $builds.Warm
        } | ConvertTo-Json -Depth 4 | Set-Content $resultPath
    }
    finally {
        $env:VSLANG = $savedLanguage
        if ($Keep) { Write-Host "Kept $fixture" }
        else { Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if (-not (Test-Path $resultPath)) { throw "No measurement at $resultPath; run without -NoRun first." }
$result = Get-Content $resultPath -Raw | ConvertFrom-Json
$s = $result.Sizes
Write-Host "$($s.Packages) Daml packages ($($s.Files) files, $($s.Lines) lines, $($s.Templates) templates), $($s.CSharpProjects) C# projects ($($s.References) Daml references)"
[pscustomobject] @{ Build = 'cold'; Seconds = $result.Cold.Seconds; Dpm = $result.Cold.DpmBuilds; Generator = $result.Cold.GeneratorRuns },
[pscustomobject] @{ Build = 'warm'; Seconds = $result.Warm.Seconds; Dpm = $result.Warm.DpmBuilds; Generator = $result.Warm.GeneratorRuns } |
    Format-Table -AutoSize | Out-Host
Write-Host "$($result.Date), $($result.VisualStudio), $($result.Machine); logs and binlogs in $artifacts"

if ($Check) {
    $problems = @(Test-ScaleBuild $result)
    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        exit 1
    }
    Write-Host 'Both builds did what they had to.' -ForegroundColor Green
}
exit 0
