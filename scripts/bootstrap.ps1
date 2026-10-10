#Requires -Version 7.2
<#
.SYNOPSIS
    Checks the development toolchain and reports what is missing.

.DESCRIPTION
    Read-only: no downloads, no installs, no network. Prints one row per requirement and
    exits non-zero when anything required is missing or too old. Pins come from
    global.json (.NET SDK) and scripts/tools.psd1 (Daml SDKs, JDK, VS test hosts).

    -WithParser adds the optional grammar tooling (Node, tree-sitter CLI).

.EXAMPLE
    pwsh scripts/bootstrap.ps1
#>
[CmdletBinding()]
param(
    [switch] $WithParser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force

$tools = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'tools.psd1')
$rows = [System.Collections.Generic.List[object]]::new()

function Add-Row([string] $Name, [string] $Status, [string] $Detail, [switch] $Optional) {
    $rows.Add([pscustomobject]@{
            Requirement = $Name
            Status      = $Status
            Required    = -not $Optional
            Detail      = $Detail
        })
}

# Runs a check and turns a thrown message into a MISSING row, so one bad tool doesn't hide the rest.
function Test-Requirement([string] $Name, [scriptblock] $Check, [switch] $Optional) {
    try {
        $detail = & $Check
        Add-Row $Name 'OK' $detail -Optional:$Optional
    }
    catch {
        Add-Row $Name 'MISSING' $_.Exception.Message -Optional:$Optional
    }
}

function Get-JavaMajor([string] $JavaExe) {
    # `java -version` prints to stderr, e.g. 'openjdk version "21.0.4"' or 'java version "1.8.0_402"'.
    $banner = (& $JavaExe -version 2>&1 | Select-Object -First 1).ToString()
    if ($banner -notmatch '"(\d+)(?:\.(\d+))?') { throw "Unrecognised java -version output: $banner" }
    $major = [int]$Matches[1]
    if ($major -eq 1 -and $Matches[2]) { $major = [int]$Matches[2] }
    [pscustomobject]@{ Major = $major; Banner = $banner }
}

Test-Requirement '.NET SDK (global.json)' {
    $sdk = Get-SdkMinimumMSBuildVersion
    "$($sdk.SdkVersion) (needs MSBuild >= $($sdk.MinimumMSBuildVersion))"
}

Test-Requirement '.NET Framework 4.8 targeting pack' {
    $dir = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
    if (-not (Test-Path (Join-Path $dir 'mscorlib.dll'))) { throw "not found in '$dir'" }
    $dir
}

Test-Requirement 'Build instance (MSBuild 18.x)' {
    $vs = Select-BuildVsInstance 3>$null
    $tag = if ($vs.IsPrerelease) { ' [prerelease]' } else { '' }
    "$($vs.DisplayName) $($vs.Version)$tag, MSBuild $($vs.MSBuildVersion)"
}

foreach ($testHost in $tools.VsTestHosts) {
    $major = ([version]"$testHost.0").Major.ToString()
    Test-Requirement "Test IDE VS $testHost" {
        $vs = Select-TestVsInstance -Major $major
        $tag = if ($vs.IsPrerelease) { ' [prerelease]' } else { '' }
        "$($vs.DisplayName) $($vs.Version)$tag"
    }
}

$dpm = Find-Tool dpm
Test-Requirement 'dpm' {
    if (-not $dpm) { throw "not on PATH or in $((Get-DpmHomeCandidates) -join ', ')" }
    $dpm
}

if ($dpm) {
    # Shape per Digital Asset docs: array of { version, installed, remote, active }.
    $installed = @()
    try {
        $json = & $dpm version --all -o json 2>$null
        if ($LASTEXITCODE -eq 0) { $installed = @($json | ConvertFrom-Json | Where-Object { $_.PSObject.Properties['installed'] -and $_.installed }) }
    }
    catch { }

    foreach ($sdk in $tools.Dpm.Sdks) {
        $match = $installed | Where-Object { $_.version -eq $sdk.Version } | Select-Object -First 1
        if ($match) {
            $active = if ($match.PSObject.Properties['active'] -and $match.active) { ' (active)' } else { '' }
            Add-Row "Daml SDK $($sdk.Version)" 'OK' "installed$active"
        }
        else {
            Add-Row "Daml SDK $($sdk.Version)" 'MISSING' 'not reported by `dpm version --all -o json`'
        }
    }
}
else {
    foreach ($sdk in $tools.Dpm.Sdks) { Add-Row "Daml SDK $($sdk.Version)" 'MISSING' 'dpm not found' }
}

Test-Requirement "JDK $($tools.Java.MinimumMajor)+" {
    $java = Find-Tool java
    if (-not $java) { throw 'java not on PATH and JAVA_HOME not set' }
    $v = Get-JavaMajor $java
    if ($v.Major -lt $tools.Java.MinimumMajor) { throw "found Java $($v.Major) at $java; need $($tools.Java.MinimumMajor)+" }
    $note = if ($env:JAVA_HOME) { '' } else { '; JAVA_HOME not set (dpm expects it)' }
    "Java $($v.Major) at $java$note"
}

if ($WithParser) {
    Test-Requirement 'Node.js' -Optional {
        $node = Find-Tool node
        if (-not $node) { throw 'not on PATH' }
        "$(& $node --version) at $node"
    }
    Test-Requirement 'tree-sitter CLI' -Optional {
        $ts = Find-Tool tree-sitter
        if (-not $ts) { throw 'not on PATH (the grammar pins its own CLI via npm; this is only for ad-hoc use)' }
        "$(& $ts --version) at $ts"
    }
}

$rows | Format-Table Requirement, Status, Required, Detail -AutoSize -Wrap | Out-Host

$missing = @($rows | Where-Object { $_.Required -and $_.Status -ne 'OK' })
if ($missing) {
    Write-Host "$($missing.Count) required item(s) missing." -ForegroundColor Red
    exit 1
}
Write-Host 'Toolchain OK.' -ForegroundColor Green
exit 0
