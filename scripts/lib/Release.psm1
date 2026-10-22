# What the release workflow checks before it drafts a release or publishes: one version across the
# VSIX and the packages, a tag that names it, and the Marketplace upload tool of the pinned VSSDK.
Set-StrictMode -Version Latest

$script:packages = @('Equilibrium.Daml.Sdk', 'Equilibrium.Daml.Build.CSharp', 'Equilibrium.Daml.Templates')

<#
.SYNOPSIS
    The release's NuGet and VSIX versions, read from the packages folder pack.ps1 fills; throws when
    the artifacts don't belong to one release.

.DESCRIPTION
    The VSIX manifest only takes numeric versions, so a prerelease 0.9.0-beta.3 is 0.9.0.3 there:
    the same major.minor.patch, and the build number of the prerelease label as the fourth part.
#>
function Get-ReleaseVersion([string] $Packages) {
    $versions = [ordered]@{}
    foreach ($id in $script:packages) {
        $files = @(Get-ChildItem -LiteralPath $Packages -Filter "$id.*.nupkg" | Where-Object { $_.Name -match ('^' + [regex]::Escape($id) + '\.(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\.nupkg$') })
        if ($files.Count -ne 1) { throw "Expected one $id package in $Packages, found $($files.Count)." }
        $null = $files[0].Name -match ('^' + [regex]::Escape($id) + '\.(.+)\.nupkg$')
        $versions[$id] = $Matches[1]
    }
    $nuget = $versions[$script:packages[0]]
    foreach ($id in $script:packages | Where-Object { $versions[$_] -ne $nuget }) {
        throw "$id is $($versions[$id]), but $($script:packages[0]) is $nuget."
    }

    $vsixFiles = @(Get-ChildItem -LiteralPath $Packages -Filter '*.vsix')
    if ($vsixFiles.Count -ne 1) { throw "Expected one VSIX in $Packages, found $($vsixFiles.Count)." }
    $zip = [IO.Compression.ZipFile]::OpenRead($vsixFiles[0].FullName)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('extension.vsixmanifest').Open())
        try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
    $vsix = $manifest.SelectSingleNode("//*[local-name()='Identity']").GetAttribute('Version')

    $core = ($nuget -split '-', 2)[0]
    $expected = if ($nuget -match '-.*\.(\d+)$') { "$core.$($Matches[1])" } else { $null }
    if (-not $vsix.StartsWith("$core.") -or ($expected -and $vsix -ne $expected)) {
        throw "The VSIX is $vsix, which isn't the release of packages $nuget$(if ($expected) { " (expected $expected)" })."
    }
    [pscustomobject]@{ NuGet = $nuget; Vsix = $vsix }
}

<#
.SYNOPSIS
    A problem when the workflow runs from a tag that doesn't name the release; nothing otherwise.
#>
function Test-ReleaseTag([string] $RefType, [string] $RefName, [string] $Version) {
    if ($RefType -eq 'tag' -and $RefName -ne "v$Version") {
        "The tag $RefName doesn't name this release; tag it v$Version."
    }
}

<#
.SYNOPSIS
    The version central package management pins for a package.
#>
function Get-PinnedPackageVersion([string] $PackagesProps, [string] $Id) {
    $props = [xml](Get-Content -LiteralPath $PackagesProps -Raw)
    $entry = @($props.SelectNodes('//PackageVersion') | Where-Object { $_.GetAttribute('Include') -eq $Id })
    if ($entry.Count -ne 1) { throw "$PackagesProps pins $Id $($entry.Count) times." }
    $entry[0].GetAttribute('Version')
}

<#
.SYNOPSIS
    VsixPublisher.exe from the given version of Microsoft.VSSDK.BuildTools in a NuGet package
    folder, or nothing when that version isn't there.
#>
function Find-VsixPublisher([string] $PackagesRoot, [string] $Version) {
    $bin = Join-Path $PackagesRoot "microsoft.vssdk.buildtools\$Version\tools\vssdk\bin"
    if (-not (Test-Path -LiteralPath $bin)) { return }
    @(Get-ChildItem -LiteralPath $bin -Recurse -Filter 'VsixPublisher.exe' | Sort-Object { $_.FullName.Length }) |
        Select-Object -First 1 | ForEach-Object FullName
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Export-ModuleMember -Function Get-ReleaseVersion, Test-ReleaseTag, Get-PinnedPackageVersion, Find-VsixPublisher
