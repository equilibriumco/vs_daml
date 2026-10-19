#Requires -Version 7.2
<#
.SYNOPSIS
    Measures what Daml.Build.CSharp adds to the design-time build of a C# project.

.DESCRIPTION
    Creates a solution from the daml-dotnet-app template of artifacts/packages (run pack.ps1
    first) in a temporary folder, with a NuGet cache of its own so the packages are this build's,
    builds it once, and adds the same C# project without Daml.Build.CSharp and its Daml reference
    as the baseline. A driver project then runs the design-time build Visual Studio asks for
    (CompileDesignTime) of each project -Runs + 1 times in one 64-bit MSBuild.exe of the Visual
    Studio the extension is tested in, each with a fresh evaluation.

    The first run of each project is the cold one: it pays once for loading and JIT-compiling
    the tasks and for evaluating the Daml project the build asks for its DAR. The median of the
    other runs is what every design-time build after that costs, as in Visual Studio. The budget
    (200 ms) applies to the warm overhead; the cold one is reported. Results, both binlogs and the
    host go to artifacts/designtime.

    -Check fails when the warm overhead is over the budget; -NoRun checks the last result.

.EXAMPLE
    pwsh scripts/measure-designtime.ps1 -Check
.EXAMPLE
    pwsh scripts/measure-designtime.ps1 -NoRun -Check
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 50)]
    [int] $Runs = 5,

    [switch] $Check,

    [switch] $NoRun,

    # Lets the first build install the pinned C# generator on a machine that hasn't got it.
    [switch] $AllowToolInstall,

    # Leaves the temporary solution in place, to look at.
    [switch] $Keep
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\DesignTimeBudget.psm1') -Force

$root = Get-RepoRoot
$artifacts = Join-Path $root 'artifacts\designtime'
$resultPath = Join-Path $artifacts 'result.json'

function Invoke-Checked([string] $What, [string] $Exe, [string[]] $Arguments) {
    $output = & $Exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Select-Object -Last 40 | Out-Host
        throw "$What failed with exit code $LASTEXITCODE."
    }
    $output
}

if (-not $NoRun) {
    $packages = Join-Path $root 'artifacts\packages'
    $templates = Get-ChildItem $packages -Filter '*.Templates.*.nupkg' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $templates -or -not (Get-ChildItem $packages -Filter '*.Build.CSharp.*.nupkg' -ErrorAction SilentlyContinue)) {
        throw "No packages in $packages; run pwsh scripts/pack.ps1 first."
    }
    $vs = Select-TestVsInstance
    $msbuild = Join-Path $vs.Path 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (-not (Test-Path $msbuild)) { $msbuild = $vs.MSBuildPath }

    # Short: the generated C# of the Daml project sits deep below it.
    $workspace = Join-Path ([IO.Path]::GetTempPath()) ('ddt-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $saved = @{}
    foreach ($name in 'NUGET_PACKAGES', 'VSLANG', 'DOTNET_CLI_UI_LANGUAGE') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    New-Item -ItemType Directory -Force $workspace, $artifacts | Out-Null
    Push-Location $workspace
    try {
        # This build's packages, never an older copy of the same version from the machine's cache;
        # everything else from that cache, so nothing needs the network that a build here didn't need.
        $env:NUGET_PACKAGES = Join-Path $workspace 'packages'
        # The driver's lines are parsed; MSBuild's own messages stay in English for that.
        $env:VSLANG = '1033'
        $env:DOTNET_CLI_UI_LANGUAGE = 'en'
        Copy-Item (Join-Path $root 'global.json') $workspace
        Set-Content (Join-Path $workspace 'nuget.config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="this-build" value="$packages" />
    <add key="machine-cache" value="$(Join-Path $env:USERPROFILE '.nuget\packages')" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="this-build">
      <package pattern="*.Sdk" />
      <package pattern="*.Build.CSharp" />
    </packageSource>
    <packageSource key="machine-cache">
      <package pattern="*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
        $hive = Join-Path $workspace 'hive'
        Invoke-Checked 'Installing the templates' 'dotnet' @('new', 'install', $templates.FullName, '--debug:custom-hive', $hive) | Out-Null
        Invoke-Checked 'Creating the solution' 'dotnet' @('new', 'daml-dotnet-app', '-n', 'Measure', '-o', (Join-Path $workspace 'Measure'), '--debug:custom-hive', $hive) | Out-Null

        $withDaml = Join-Path $workspace 'Measure\Measure\Measure.csproj'
        $baseline = Join-Path $workspace 'Measure\Plain\Plain.csproj'
        New-Item -ItemType Directory -Force (Split-Path $baseline) | Out-Null
        Get-Content $withDaml | Where-Object { $_ -notmatch 'Build\.CSharp|DamlReference' } | Set-Content $baseline
        Set-Content (Join-Path (Split-Path $baseline) 'Program.cs') 'System.Console.WriteLine("baseline");'

        $build = @('-nologo', '-v:m', '-nr:false', '-restore')
        if ($AllowToolInstall) { $build += '-p:DamlAllowToolInstall=true' }
        Write-Host 'Building the solution once (dpm, the C# generator, the compiler)...'
        Invoke-Checked 'The first build' $msbuild (@((Join-Path $workspace 'Measure\Measure.slnx')) + $build) | Out-Null
        Invoke-Checked 'Restoring the baseline' $msbuild @($baseline, '-nologo', '-v:m', '-nr:false', '-t:Restore') | Out-Null

        $driver = Join-Path $workspace 'Driver.proj'
        Set-Content $driver @"
<Project>
  <ItemGroup>
    <MeasuredRun Include="$((0..$Runs) -join ';')" />
  </ItemGroup>
  <!-- One run per item, one after the other; MeasureRun makes each a new evaluation. -->
  <Target Name="Measure" Outputs="%(MeasuredRun.Identity)">
    <PropertyGroup>
      <_Started>`$([System.DateTime]::UtcNow.Ticks)</_Started>
    </PropertyGroup>
    <MSBuild Projects="`$(MeasuredProject)" Targets="CompileDesignTime"
             Properties="DesignTimeBuild=true;BuildingInsideVisualStudio=true;SkipCompilerExecution=true;ProvideCommandLineArgs=true;MeasureRun=%(MeasuredRun.Identity)" />
    <Message Importance="high" Text="DAML-DTB-RUN %(MeasuredRun.Identity) `$([MSBuild]::Subtract(`$([System.DateTime]::UtcNow.Ticks), `$(_Started)))" />
  </Target>
</Project>
"@
        $measured = @{}
        foreach ($project in @(@{ Name = 'Baseline'; Path = $baseline }, @{ Name = 'WithDaml'; Path = $withDaml })) {
            Write-Host "Measuring $($project.Name): $($Runs + 1) design-time builds..."
            $output = Invoke-Checked "Measuring $($project.Name)" $msbuild @(
                $driver, '-t:Measure', "-p:MeasuredProject=$($project.Path)", '-nologo', '-v:m', '-nr:false', '-m:1',
                "-bl:$(Join-Path $artifacts "$($project.Name).binlog")")
            $measured[$project.Name] = Read-DesignTimeRuns $output
        }

        $overhead = Measure-DesignTimeOverhead -Baseline $measured.Baseline -WithDaml $measured.WithDaml
        $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
        [ordered] @{
            Date          = (Get-Date).ToString('s')
            Machine       = "$cpu, $([Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)) GB"
            VisualStudio  = "$($vs.DisplayName) $($vs.Version)"
            MSBuild       = $msbuild
            Packages      = $templates.Name -replace '^.*\.Templates\.(.+)\.nupkg$', '$1'
            BaselineRuns  = $measured.Baseline
            WithDamlRuns  = $measured.WithDaml
            Overhead      = $overhead
        } | ConvertTo-Json -Depth 4 | Set-Content $resultPath
    }
    finally {
        Pop-Location
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        if ($Keep) { Write-Host "Kept $workspace" }
        else { Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if (-not (Test-Path $resultPath)) { throw "No measurement at $resultPath; run without -NoRun first." }
$result = Get-Content $resultPath -Raw | ConvertFrom-Json
$o = $result.Overhead
[pscustomobject] @{ Build = 'cold (first)'; Baseline = $o.ColdBaselineMs; WithDaml = $o.ColdWithDamlMs; Overhead = $o.ColdOverheadMs },
[pscustomobject] @{ Build = "warm (median of $($o.WarmRuns))"; Baseline = $o.WarmBaselineMs; WithDaml = $o.WarmWithDamlMs; Overhead = $o.WarmOverheadMs } |
    Format-Table Build, @{ n = 'Baseline ms'; e = { '{0:0.0}' -f $_.Baseline } }, @{ n = 'With Daml ms'; e = { '{0:0.0}' -f $_.WithDaml } }, @{ n = 'Overhead ms'; e = { '{0:0.0}' -f $_.Overhead } } -AutoSize |
    Out-Host
Write-Host "$($result.Date), $($result.VisualStudio), $($result.Machine); binlogs in $artifacts"

if ($Check) {
    $verdict = Test-DesignTimeBudget $o
    if ($verdict.Passed) { Write-Host $verdict.Message -ForegroundColor Green }
    else {
        Write-Host $verdict.Message -ForegroundColor Red
        exit 1
    }
}
exit 0
