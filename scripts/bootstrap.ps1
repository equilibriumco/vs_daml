#Requires -Version 7.2
<#
.SYNOPSIS
    Checks the development toolchain and reports what is missing; -Install adds dpm and
    the pinned Daml SDKs.

.DESCRIPTION
    Without -Install the script is read-only: no downloads, no installs, no network. It
    prints one row per requirement and exits non-zero when anything required is missing
    or too old. Pins come from global.json (.NET SDK) and scripts/tools.psd1 (Daml SDKs,
    JDK, VS test hosts).

    -Install downloads each pinned dpm-sdk archive (cached under
    %LOCALAPPDATA%\DamlVisualStudio\downloads), refuses it unless the SHA-256 matches
    tools.psd1, and runs the archive's own "dpm bootstrap". Visual Studio and the JDK are
    never installed by this script. PATH and user environment variables are left alone;
    the script prints what to add.

    -WithParser adds the optional grammar tooling (Node, tree-sitter CLI).

.EXAMPLE
    pwsh scripts/bootstrap.ps1
.EXAMPLE
    pwsh scripts/bootstrap.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch] $Install,

    # Install dpm here instead of its default home. Applies to this run only; persist
    # DPM_HOME yourself if you use it.
    [string] $DpmHome,

    [switch] $WithParser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force

$tools = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'tools.psd1')
if ($DpmHome) { $env:DPM_HOME = $DpmHome }
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

function Get-DpmVersion([string] $Dpm) {
    # First line of `dpm --version` is e.g. 'version: 1.0.22'.
    $banner = (& $Dpm --version 2>&1 | Select-Object -First 1).ToString()
    if ($banner -notmatch '(\d+\.\d+\.\d+)') { throw "Unrecognised dpm --version output: $banner" }
    [version]$Matches[1]
}

# Local only: `dpm version --all` would also ask the remote registry, which a check must not do.
# Output is an array of { version, installed, active }.
function Get-InstalledDamlSdks([string] $Dpm) {
    if (-not $Dpm) { return @() }
    try {
        $json = & $Dpm versions -o json 2>$null
        if ($LASTEXITCODE -ne 0) { return @() }
        @($json | ConvertFrom-Json | Where-Object { $_.PSObject.Properties['installed'] -and $_.installed })
    }
    catch { @() }
}

function Install-DamlSdks {
    $stateDir = Join-Path $env:LOCALAPPDATA 'DamlVisualStudio'
    $cache = Join-Path $stateDir 'downloads'
    # dpm's docs warn against extracting into a temp directory the user may not own.
    $work = Join-Path $stateDir 'dpm-bootstrap'
    New-Item -ItemType Directory -Force -Path $cache, $work | Out-Null

    # Oldest first: every archive bootstraps with its own dpm.exe, so finishing with the
    # default (first-listed) SDK leaves the newest dpm in place and that SDK installed last.
    $sdks = @($tools.Dpm.Sdks)
    [array]::Reverse($sdks)

    foreach ($sdk in $sdks) {
        $installed = Get-InstalledDamlSdks (Find-Tool dpm)
        if ($installed | Where-Object { $_.version -eq $sdk.Version }) {
            Write-Host "Daml SDK $($sdk.Version) is already installed."
            continue
        }

        $name = "dpm-$($sdk.Version)-windows-amd64.zip"
        $zip = Join-Path $cache $name
        if (-not (Test-Path $zip)) {
            Write-Host "Downloading $name (~1.2 GB)..."
            $partial = "$zip.partial"
            # The progress bar slows Invoke-WebRequest down by an order of magnitude on big files.
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri "$($tools.Dpm.BaseUrl)/$name" -OutFile $partial
            Move-Item $partial $zip -Force
        }

        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
        if ($hash -ne $sdk.Sha256) {
            Remove-Item $zip -Force
            throw ("SHA-256 mismatch for $name (expected $($sdk.Sha256), got $($hash.ToLowerInvariant())). " +
                'The archive changed upstream; find out why before re-pinning it in tools.psd1.')
        }

        $dest = Join-Path $work $sdk.Version
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Write-Host "Extracting $name..."
        Expand-Archive -Path $zip -DestinationPath $dest
        $sdkRoot = Join-Path $dest 'windows-amd64'

        Write-Host "dpm bootstrap $($sdk.Version)..."
        & (Join-Path $sdkRoot 'bin\dpm.exe') bootstrap $sdkRoot
        $exitCode = $LASTEXITCODE
        Remove-Item $dest -Recurse -Force
        if ($exitCode -ne 0) { throw "dpm bootstrap for $($sdk.Version) failed with exit code $exitCode." }
    }

    $dpm = Find-Tool dpm
    if (-not $dpm) {
        throw "dpm bootstrap reported success but dpm.exe is not in $((Get-DpmHomeCandidates) -join ' or ')."
    }
    $onPath = Get-Command dpm -CommandType Application -ErrorAction SilentlyContinue
    if (-not $onPath) {
        Write-Host "dpm is installed at $dpm but not on PATH. Add '$(Split-Path $dpm)' to your user PATH." -ForegroundColor Yellow
    }
}

if ($Install) {
    Install-DamlSdks
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

Test-Requirement 'Test IDE (VS 2026)' {
    $vs = Select-TestVsInstance
    $tag = if ($vs.IsPrerelease) { ' [prerelease]' } else { '' }
    "$($vs.DisplayName) $($vs.Version)$tag"
}

$dpm = Find-Tool dpm
Test-Requirement 'dpm' {
    if (-not $dpm) { throw "not on PATH or in $((Get-DpmHomeCandidates) -join ', ')" }
    $version = Get-DpmVersion $dpm
    if ($version -lt [version]$tools.Dpm.MinimumVersion) { throw "found dpm $version at $dpm; need $($tools.Dpm.MinimumVersion)+" }
    "dpm $version at $dpm"
}

if ($dpm) {
    $installed = Get-InstalledDamlSdks $dpm
    foreach ($sdk in $tools.Dpm.Sdks) {
        $match = $installed | Where-Object { $_.version -eq $sdk.Version } | Select-Object -First 1
        if ($match) {
            $active = if ($match.PSObject.Properties['active'] -and $match.active) { ' (active)' } else { '' }
            Add-Row "Daml SDK $($sdk.Version)" 'OK' "installed$active"
        }
        else {
            Add-Row "Daml SDK $($sdk.Version)" 'MISSING' 'not reported by `dpm versions -o json`'
        }
    }
}
else {
    foreach ($sdk in $tools.Dpm.Sdks) { Add-Row "Daml SDK $($sdk.Version)" 'MISSING' 'dpm not found' }
}

Test-Requirement "JDK $($tools.Java.MinimumMajor)+" {
    $java = Find-Tool java
    if (-not $java) { throw 'JAVA_HOME not set and java not on PATH' }
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
    if (-not $Install -and ($missing | Where-Object { $_.Requirement -like 'Daml SDK*' -or $_.Requirement -eq 'dpm' })) {
        Write-Host 'dpm and the Daml SDKs can be installed with: pwsh scripts/bootstrap.ps1 -Install'
    }
    exit 1
}
Write-Host 'Toolchain OK.' -ForegroundColor Green
exit 0
