#Requires -Version 7.2
<#
.SYNOPSIS
    Installs the built VSIX into the experimental instance (/rootSuffix Exp) of a test IDE.

.DESCRIPTION
    Removes any previous install of the same extension Id from Exp first, so rebuilding
    without bumping the version still gets picked up. Close the Exp instance before running;
    the installer is not allowed to shut down running Visual Studio processes, because that
    would also kill your normal VS windows. Afterwards the Exp configuration is rebuilt
    (devenv /updateConfiguration) so the package registration is guaranteed to be picked up.
    The installer log lands in artifacts/logs.

.EXAMPLE
    pwsh scripts/install-exp.ps1
.EXAMPLE
    pwsh scripts/install-exp.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    # Defaults to the newest VSIX under src/Equilibrium.Daml.VisualStudio/bin/<Configuration>.
    [string] $VsixPath,

    [string] $VsInstallationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Get-RepoRoot

if (-not $VsixPath) {
    $binDir = Join-Path $root "src\Equilibrium.Daml.VisualStudio\bin\$Configuration"
    $VsixPath = Get-ChildItem $binDir -Recurse -Filter '*.vsix' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $VsixPath) { throw "No VSIX found under '$binDir'. Run scripts/build.ps1 -Configuration $Configuration first." }
}
$VsixPath = (Resolve-Path $VsixPath).Path

function Get-VsixId([string] $Path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry('extension.vsixmanifest')
        if (-not $entry) { throw "'$Path' has no extension.vsixmanifest." }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { ([xml]$reader.ReadToEnd()).PackageManifest.Metadata.Identity.Id }
        finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}

function Invoke-VsixInstaller([string[]] $Arguments) {
    # GUI executable: Start-Process is the only way to wait for it and read the exit code.
    (Start-Process $vs.VsixInstallerPath -ArgumentList $Arguments -Wait -PassThru -NoNewWindow).ExitCode
}

$vs = Select-TestVsInstance -InstallationPath $VsInstallationPath
$id = Get-VsixId $VsixPath
Write-Host "Test instance: $($vs.DisplayName) $($vs.Version) ($($vs.InstanceId)), Exp"
Write-Host "VSIX: $VsixPath ($id)"

$logName = "daml-vsix-install-$((Get-Date).ToString('yyyyMMdd-HHmmss')).log"
$common = '/quiet', '/rootSuffix:Exp', "/instanceIds:$($vs.InstanceId)", "/logFile:$logName"

# Non-zero here usually just means "not installed yet".
$null = Invoke-VsixInstaller ($common + "/uninstall:$id")
$exitCode = Invoke-VsixInstaller ($common + "`"$VsixPath`"")

$logDir = Join-Path $root 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $env:TEMP $logName
if (Test-Path $log) { Copy-Item $log $logDir -Force }

if ($exitCode -ne 0) {
    Write-Host "VSIXInstaller failed with exit code $exitCode; see artifacts\logs\$logName" -ForegroundColor Red
    exit $exitCode
}

# VSIXInstaller doesn't always get the new .pkgdef merged: on a fresh Exp hive the pkgdef cache
# considered itself current and the package was never registered (MEF parts loaded, the package
# didn't). Rebuilding the configuration explicitly makes the install deterministic.
Write-Host 'Updating Exp configuration...'
$update = Start-Process $vs.DevenvPath -ArgumentList '/rootSuffix', 'Exp', '/updateConfiguration' -Wait -PassThru -NoNewWindow
if ($update.ExitCode -ne 0) {
    Write-Host "devenv /updateConfiguration failed with exit code $($update.ExitCode)" -ForegroundColor Red
}
exit $update.ExitCode
