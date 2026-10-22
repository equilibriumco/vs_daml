#Requires -Version 7.2
<#
.SYNOPSIS
    CycloneDX SBOMs of the VSIX and the NuGet packages that pack.ps1 left in artifacts/packages,
    one per artifact, written to artifacts/sbom as <artifact file name>.cdx.json.

.DESCRIPTION
    The CycloneDX tool pinned in dotnet-tools.json reads the packages the build restored for the
    project behind each artifact, from its assets file and the local NuGet package folder. Restore
    and GitHub license lookups stay off, so beyond restoring the tool it needs no network.

    The tool lists everything a project compiles against, most of which Visual Studio or MSBuild
    provide at run time, so each SBOM is cut down to the packages whose assemblies the artifact
    carries; our own assemblies and vendored files found in the artifact by their pinned hash are
    added. Run after a Release build and pack. Fails when vendored files don't match their PIN,
    an assembly in an artifact isn't accounted for, or NOTICE misses a third-party component or
    its license.
#>
[CmdletBinding()]
param(
    [string] $Packages,
    [string] $Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\Toolchain.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\ThirdParty.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'lib\Sbom.psm1') -Force

$root = Get-RepoRoot
if (-not $Packages) { $Packages = Join-Path $root 'artifacts\packages' }
if (-not $Output) { $Output = Join-Path $root 'artifacts\sbom' }
$supplier = 'Equilibrium'
$ownLicense = 'Apache-2.0'
# SPDX ids of the vendored folders; PIN files record origin and hashes, not licenses.
$vendoredLicenses = @{
    'daml-extension'              = @{ Default = 'Apache-2.0'; Files = @{ 'daml12.tmLanguage.xml' = 'BSD-3-Clause' } }
    'canton-dotnet-sdk-mini-demo' = @{ Default = 'Apache-2.0' }
}

$thirdParty = Join-Path $root 'third_party'
$pinProblems = @(Test-PinnedFile $thirdParty)
if ($pinProblems) {
    $pinProblems | ForEach-Object { Write-Host "third_party: $_" -ForegroundColor Red }
    exit 1
}
$pinned = @(Get-PinnedFile $thirdParty)
$notice = [IO.File]::ReadAllText((Join-Path $root 'NOTICE'))
$packageFolder = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }

function Read-ZipEntry([string] $Path, [string] $Name) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry($Name)
        if (-not $entry) { throw "$Path has no $Name" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}

# Each artifact with the project whose restore describes its third-party content. Both MSBuild
# packages carry exactly the output of the tasks project.
$artifacts = [System.Collections.Generic.List[object]]::new()
$vsix = @(Get-ChildItem $Packages -Filter '*.vsix')
if ($vsix.Count -ne 1) { throw "Expected one VSIX in $Packages, found $($vsix.Count)." }
$identity = ([xml](Read-ZipEntry $vsix[0].FullName 'extension.vsixmanifest')).SelectSingleNode("//*[local-name()='Identity']")
$artifacts.Add(@{ File = $vsix[0]; Name = 'Equilibrium.Daml.VisualStudio'; Version = $identity.GetAttribute('Version'); Type = 'Application'; Project = 'src\Equilibrium.Daml.VisualStudio\Equilibrium.Daml.VisualStudio.csproj' })
foreach ($package in Get-ChildItem $Packages -Filter '*.nupkg') {
    $zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try { $nuspecName = @($zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.Name -like '*.nuspec' })[0].Name } finally { $zip.Dispose() }
    $metadata = ([xml](Read-ZipEntry $package.FullName $nuspecName)).package.metadata
    $project = switch -Wildcard ($metadata.id) {
        '*.Sdk' { 'src\Equilibrium.Daml.Build.Tasks\Equilibrium.Daml.Build.Tasks.csproj' }
        '*.Build.CSharp' { 'src\Equilibrium.Daml.Build.Tasks\Equilibrium.Daml.Build.Tasks.csproj' }
        '*.Templates' { 'src\Equilibrium.Daml.Templates\Equilibrium.Daml.Templates.csproj' }
        default { throw "No SBOM source project is known for package $($metadata.id)." }
    }
    $artifacts.Add(@{ File = $package; Name = $metadata.id; Version = $metadata.version; Type = 'Library'; Project = $project; NuGet = $true })
}

& dotnet tool restore --tool-manifest (Join-Path $root 'dotnet-tools.json') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed (CycloneDX).' }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('daml-sbom-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$problems = [System.Collections.Generic.List[string]]::new()
Push-Location $root
try {
    foreach ($artifact in $artifacts) {
        # The assets file already holds the packages of referenced projects. --exclude-dev drops the
        # build-only ones (analyzers, PolySharp); compile-only references go in Select-ShippedSbom.
        $arguments = @(
            (Join-Path $root $artifact.Project), '--output', $scratch, '--filename', 'tool.json', '--output-format', 'Json',
            '--spec-version', '1.6', '--exclude-dev', '--disable-package-restore',
            '--set-name', $artifact.Name, '--set-version', $artifact.Version, '--set-type', $artifact.Type
        )
        if ($artifact.ContainsKey('NuGet')) { $arguments += '--set-nuget-purl' }
        $log = & dotnet tool run dotnet-CycloneDX @arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            $log | Out-Host
            throw "CycloneDX failed for $($artifact.Project)."
        }
        $toolBom = Get-Content -LiteralPath (Join-Path $scratch 'tool.json') -Raw | ConvertFrom-Json
        $files = @(Get-ArtifactFile $artifact.File.FullName)
        $bom = Select-ShippedSbom -Bom $toolBom -Files $files -PackageAssemblies (Get-PackageAssembly $packageFolder $toolBom) `
            -Pinned $pinned -Licenses $vendoredLicenses -Version $artifact.Version -Supplier $supplier -License $ownLicense
        foreach ($problem in @(Test-SbomCoverage -Bom $bom -Files $files) + @(Test-NoticeCoverage -Notice $notice -Bom $bom -Files $files -Supplier $supplier)) {
            $problems.Add("$($artifact.File.Name): $problem")
        }
        $target = Join-Path $Output "$($artifact.File.Name).cdx.json"
        [IO.File]::WriteAllText($target, ($bom | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
        $thirdPartyCount = @($bom.components | Where-Object { -not ($_.PSObject.Properties['supplier'] -and $_.supplier.name -eq $supplier) }).Count
        Write-Host "SBOM: $(Split-Path $target -Leaf) ($(@($bom.components).Count) components, $thirdPartyCount third-party)"
    }
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($problems.Count) {
    $problems | ForEach-Object { Write-Host "SBOM check: $_" -ForegroundColor Red }
    exit 1
}
