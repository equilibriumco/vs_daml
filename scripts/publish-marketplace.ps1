#Requires -Version 7
<#
.SYNOPSIS
    Uploads a VSIX to the Visual Studio Marketplace with the listing in
    src/Equilibrium.Daml.VisualStudio/Marketplace.

.DESCRIPTION
    Run by the release workflow after a person approves the release environment. Checks the listing
    again, then runs VsixPublisher from the Microsoft.VSSDK.BuildTools version that
    Directory.Packages.props pins, restoring the VSIX project when the package isn't in the NuGet
    folder yet. The personal access token comes from VS_MARKETPLACE_PAT, never from the command line.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Vsix
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'lib\Marketplace.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\Release.psm1') -Force

if (-not $env:VS_MARKETPLACE_PAT) { throw 'VS_MARKETPLACE_PAT is not set.' }
$vsixPath = (Resolve-Path -LiteralPath $Vsix).Path

$marketplace = Join-Path $root 'src\Equilibrium.Daml.VisualStudio\Marketplace'
$publishManifest = Join-Path $marketplace 'publishManifest.json'
$problems = @(Test-MarketplaceListing -PublishManifest $publishManifest -VsixManifest (Join-Path $root 'src\Equilibrium.Daml.VisualStudio\source.extension.vsixmanifest'))
if ($problems) { throw "The Marketplace listing isn't ready:`n$($problems -join "`n")" }

$version = Get-PinnedPackageVersion -PackagesProps (Join-Path $root 'Directory.Packages.props') -Id 'Microsoft.VSSDK.BuildTools'
$packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
$publisher = Find-VsixPublisher -PackagesRoot $packagesRoot -Version $version
if (-not $publisher) {
    dotnet restore (Join-Path $root 'src\Equilibrium.Daml.VisualStudio\Equilibrium.Daml.VisualStudio.csproj') --nologo
    if ($LASTEXITCODE -ne 0) { throw "Restoring the VSIX project failed ($LASTEXITCODE)." }
    $publisher = Find-VsixPublisher -PackagesRoot $packagesRoot -Version $version
    if (-not $publisher) { throw "No VsixPublisher.exe in Microsoft.VSSDK.BuildTools $version under $packagesRoot." }
}

Write-Host "Publishing $(Split-Path -Leaf $vsixPath) with $publisher"
& $publisher publish -payload $vsixPath -publishManifest $publishManifest -personalAccessToken $env:VS_MARKETPLACE_PAT
if ($LASTEXITCODE -ne 0) { throw "VsixPublisher failed ($LASTEXITCODE)." }
