#Requires -Version 7.2
<#
.SYNOPSIS
    Release build plus packaging: the VSIX and every packable project's NuGet package
    end up in artifacts/packages.

.DESCRIPTION
    Projects are not packable unless they opt in with IsPackable=true, so this only
    produces NuGet packages for the projects meant to ship as packages.

    The VSIX is checked before it is accepted: it must target VS 2026 on x64 only, carry our
    own assemblies, must not carry assemblies Visual Studio provides itself (shipping our copy
    of those breaks binding in the IDE), and every other third-party assembly needs a codeBase
    entry in the pkgdef for exactly the version it ships.
#>
[CmdletBinding()]
param(
    [string] $VsInstallationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force

$root = Get-RepoRoot
$out = Join-Path $root 'artifacts\packages'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -VsInstallationPath $VsInstallationPath `
    -Targets 'Build', 'Pack' -Properties @{ PackageOutputPath = $out }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$vsix = @(Get-ChildItem (Join-Path $root 'src\Daml.VisualStudio\bin\Release') -Recurse -Filter '*.vsix' -ErrorAction SilentlyContinue)
if (-not $vsix) {
    Write-Host 'Release build produced no VSIX.' -ForegroundColor Red
    exit 1
}
if ($vsix.Count -ne 1) {
    Write-Host "Expected one VSIX, found $($vsix.Count): $($vsix.Name -join ', ')" -ForegroundColor Red
    exit 1
}

function Read-ZipText($Zip, [string] $Name) {
    $entry = $Zip.GetEntry($Name)
    if (-not $entry) { return $null }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}

# Assembly name -> version of every codeBase entry in a .pkgdef.
function Get-CodeBases([string] $Pkgdef) {
    $result = @{}
    foreach ($section in ($Pkgdef -split '(?m)^\[') | Where-Object { $_ -match '^\$RootKey\$\\RuntimeConfiguration\\dependentAssembly\\codeBase\\' }) {
        if ($section -match '"name"="([^"]+)"' ) {
            $name = $Matches[1]
            $result[$name] = if ($section -match '"version"="([^"]+)"') { $Matches[1] } else { '' }
        }
    }
    $result
}

function Test-Vsix([string] $Path) {
    $problems = [System.Collections.Generic.List[string]]::new()
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) "daml-vsix-check-$PID"
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
        $manifestText = Read-ZipText $zip 'extension.vsixmanifest'
        if (-not $manifestText) { return @('no extension.vsixmanifest') }
        $manifest = [xml]$manifestText
        $codeBases = Get-CodeBases ((Read-ZipText $zip 'Daml.VisualStudio.pkgdef') ?? '')

        # A third-party assembly found only by probing can lose to another extension's copy of a
        # different version; each one needs an exact codeBase entry matching the file we ship.
        New-Item -ItemType Directory -Force -Path $scratch | Out-Null
        foreach ($dll in $zip.Entries | Where-Object { $_.Name -match '\.dll$' -and $_.Name -notmatch '^Daml\.' }) {
            $file = Join-Path $scratch $dll.Name
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($dll, $file, $true)
            $identity = [System.Reflection.AssemblyName]::GetAssemblyName($file)
            if (-not $codeBases.ContainsKey($identity.Name)) { $problems.Add("$($dll.FullName) has no codeBase entry in the pkgdef") }
            elseif ($codeBases[$identity.Name] -ne $identity.Version.ToString()) { $problems.Add("$($dll.FullName) is $($identity.Version) but its codeBase entry says $($codeBases[$identity.Name])") }
        }
    }
    finally {
        $zip.Dispose()
        if (Test-Path $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    }

    $targets = @($manifest.PackageManifest.Installation.InstallationTarget)
    if (-not $targets) { $problems.Add('no InstallationTarget') }
    foreach ($target in $targets) {
        if ($target.Version -ne '[18.0,)') { $problems.Add("InstallationTarget $($target.Id) has range $($target.Version), expected [18.0,)") }
        $arch = @($target.ProductArchitecture)
        if ($arch.Count -ne 1 -or $arch[0] -ne 'amd64') { $problems.Add("InstallationTarget $($target.Id) architectures '$($arch -join ', ')', expected amd64 only") }
    }

    foreach ($own in 'Daml.VisualStudio.dll', 'Daml.VisualStudio.Core.dll', 'Daml.VisualStudio.LanguageServer.dll', 'Daml.Build.Core.dll', 'Daml.VisualStudio.pkgdef') {
        if ($own -notin $entries) { $problems.Add("missing $own") }
    }
    # Visual Studio loads its own copies of these; ours would shadow or conflict with them.
    $vsProvided = '^(Microsoft\.VisualStudio\.|Microsoft\.CodeAnalysis|Microsoft\.ServiceHub\.|StreamJsonRpc|Newtonsoft\.Json|System\.Text\.Json|Microsoft\.Web\.WebView2|WebView2Loader|MessagePack|Nerdbank\.)'
    foreach ($entry in $entries) {
        $name = Split-Path $entry -Leaf
        if ($name -match '\.dll$' -and $name -match $vsProvided) { $problems.Add("ships VS-provided assembly $entry") }
    }
    $problems
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$vsixProblems = @(Test-Vsix $vsix[0].FullName)
if ($vsixProblems) {
    $vsixProblems | ForEach-Object { Write-Host "VSIX check: $_" -ForegroundColor Red }
    exit 1
}
Write-Host "VSIX check passed: $($vsix[0].Name) (VS 2026 [18.0,), amd64, no VS-provided assemblies, codeBase for every third-party assembly)"
$vsix | Copy-Item -Destination $out

Get-ChildItem $out | ForEach-Object { Write-Host "  $($_.Name)" }
