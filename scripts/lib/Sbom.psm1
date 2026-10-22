# Turns the CycloneDX tool's view of a project (every package restore resolved) into an SBOM of what
# one artifact actually carries, and checks that SBOM and NOTICE against the artifact.
Set-StrictMode -Version Latest

# Our assemblies carry the Equilibrium prefix; the package files keep the Daml prefix until the
# package ids are renamed.
$script:ownFile = '^Equilibrium\.Daml\..+\.(dll|nupkg)$|^Daml\.(Sdk|Build\.CSharp|Templates)\..+\.nupkg$'
# License texts and notices travel with the code they cover; they aren't components themselves.
$script:licenseDocument = '(?i)(license|notice|copying)'
$script:noticeWording = @{
    'MIT'          = 'MIT License'
    'Apache-2.0'   = 'Apache License, Version 2.0'
    'BSD-3-Clause' = 'BSD 3-Clause'
}

function Get-JsonValue([object] $Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { $property.Value } else { $null }
}

# The items of an array property; the tool leaves empty ones out altogether.
function Get-JsonArray([object] $Object, [string] $Name) {
    @(Get-JsonValue $Object $Name) | Where-Object { $null -ne $_ }
}

function Get-Sha256([IO.Stream] $Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { -join ($sha.ComputeHash($Stream) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
}

function Get-LicenseId([object] $Component) {
    foreach ($entry in Get-JsonArray $Component 'licenses') {
        $license = Get-JsonValue $entry 'license'
        $id = Get-JsonValue $license 'id'
        if (-not $id) { $id = Get-JsonValue $license 'name' }
        if (-not $id) { $id = Get-JsonValue $entry 'expression' }
        if ($id) { $id }
    }
}

function New-Component([string] $Type, [string] $Ref, [string] $Name, [string] $Version, [string] $Sha256, [string] $Location) {
    [ordered]@{
        type       = $Type
        'bom-ref'  = $Ref
        name       = $Name
        version    = $Version
        hashes     = @([ordered]@{ alg = 'SHA-256'; content = $Sha256 })
        licenses   = @()
        evidence   = [ordered]@{ occurrences = @([ordered]@{ location = $Location }) }
    }
}

<#
.SYNOPSIS
    The files of a VSIX or NuGet package: path inside it (URL-decoded), file name and sha256.
#>
function Get-ArtifactFile([string] $Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries | Where-Object { $_.Name }) {
            $stream = $entry.Open()
            try { $sha256 = Get-Sha256 $stream } finally { $stream.Dispose() }
            $decoded = [Uri]::UnescapeDataString($entry.FullName)
            [pscustomobject]@{ Path = $decoded; Name = ($decoded -split '/')[-1]; Sha256 = $sha256 }
        }
    }
    finally { $zip.Dispose() }
}

<#
.SYNOPSIS
    bom-ref -> file names of the runtime assemblies (lib, runtimes) of each NuGet component, read
    from the extracted packages in the NuGet package folder.
#>
function Get-PackageAssembly([string] $PackagesFolder, [object] $Bom) {
    $map = @{}
    foreach ($component in Get-JsonArray $Bom 'components') {
        $purl = Get-JsonValue $component 'purl'
        if ($purl -notmatch '^pkg:nuget/([^@]+)@(.+)$') { continue }
        $folder = Join-Path $PackagesFolder ('{0}\{1}' -f $Matches[1].ToLowerInvariant(), $Matches[2].ToLowerInvariant())
        $names = foreach ($sub in 'lib', 'runtimes') {
            $path = Join-Path $folder $sub
            if (Test-Path -LiteralPath $path) { Get-ChildItem -LiteralPath $path -Recurse -File -Filter '*.dll' | ForEach-Object Name }
        }
        $map[$component.'bom-ref'] = @($names | Sort-Object -Unique)
    }
    $map
}

<#
.SYNOPSIS
    The SBOM of one artifact, from the tool's SBOM of the project that builds it.

.DESCRIPTION
    Keeps the packages with an assembly in the artifact; packages that are only compiled against
    (the host provides them) drop out, together with their edges in the dependency graph. Adds our
    own assemblies and nested packages, and vendored files found in the artifact by their pinned
    hash. Every component records where in the artifact it is.

    Licenses maps a third_party folder to @{ Default = '<SPDX id>'; Files = @{ '<path>' = '<SPDX id>' } }.
#>
function Select-ShippedSbom {
    param(
        [Parameter(Mandatory)] [object] $Bom,
        [Parameter(Mandatory)] [object[]] $Files,
        [Parameter(Mandatory)] [hashtable] $PackageAssemblies,
        [object[]] $Pinned = @(),
        [hashtable] $Licenses = @{},
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $Supplier,
        [Parameter(Mandatory)] [string] $License
    )
    $result = $Bom | ConvertTo-Json -Depth 32 | ConvertFrom-Json
    $ownLicense = @([ordered]@{ license = [ordered]@{ id = $License } })
    $components = [System.Collections.Generic.List[object]]::new()

    foreach ($component in Get-JsonArray $result 'components') {
        $assemblies = [System.Collections.Generic.HashSet[string]]::new([string[]]@($PackageAssemblies[$component.'bom-ref']), [StringComparer]::OrdinalIgnoreCase)
        $locations = @($Files | Where-Object { $assemblies.Contains($_.Name) } | ForEach-Object Path)
        if (-not $locations) { continue }
        $component | Add-Member -Force -NotePropertyName evidence -NotePropertyValue ([ordered]@{ occurrences = @($locations | ForEach-Object { [ordered]@{ location = $_ } }) })
        $components.Add($component)
    }

    foreach ($file in $Files | Where-Object { $_.Name -match $script:ownFile }) {
        if ($file.Name -match '^(?<id>.+?)\.(?<version>\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?)\.nupkg$') {
            $component = New-Component 'library' "$($Matches.id)@$($Matches.version)" $Matches.id $Matches.version $file.Sha256 $file.Path
            $component.purl = "pkg:nuget/$($Matches.id)@$($Matches.version)"
        }
        else {
            # Keyed by file name: the main assembly shares name and version with the artifact.
            $component = New-Component 'library' "$($file.Name)@$Version" ([IO.Path]::GetFileNameWithoutExtension($file.Name)) $Version $file.Sha256 $file.Path
        }
        $component.supplier = [ordered]@{ name = $Supplier }
        $component.licenses = $ownLicense
        $components.Add([pscustomobject]$component)
    }

    $byHash = @{}
    foreach ($pin in @($Pinned | Where-Object { ($_.Path -split '/')[-1] -notmatch $script:licenseDocument })) { $byHash[$pin.Sha256] = $pin }
    foreach ($file in $Files | Where-Object { $_.Name -notmatch $script:licenseDocument -and $_.Name -notmatch $script:ownFile -and $byHash.ContainsKey($_.Sha256) }) {
        $pin = $byHash[$file.Sha256]
        $component = New-Component 'file' "third_party/$($pin.Folder)/$($pin.Path)" ($pin.Path -split '/')[-1] $pin.Commit $file.Sha256 $file.Path
        $folderLicense = $Licenses[$pin.Folder]
        $id = $null
        if ($folderLicense) {
            $id = if ($folderLicense.Files -and $folderLicense.Files.ContainsKey($pin.Path)) { $folderLicense.Files[$pin.Path] } else { $folderLicense.Default }
        }
        if ($id) { $component.licenses = @([ordered]@{ license = [ordered]@{ id = $id } }) }
        if ($pin.Repository -match '^https://github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$') {
            $component.purl = "pkg:github/$($Matches[1])/$($Matches[2])@$($pin.Commit)#$($pin.Upstream)"
        }
        if ($pin.Repository) { $component.externalReferences = @([ordered]@{ type = 'vcs'; url = $pin.Repository }) }
        $components.Add([pscustomobject]$component)
    }

    $root = $result.metadata.component
    $root | Add-Member -Force -NotePropertyName supplier -NotePropertyValue ([ordered]@{ name = $Supplier })
    $root | Add-Member -Force -NotePropertyName licenses -NotePropertyValue $ownLicense

    $refs = [System.Collections.Generic.HashSet[string]]::new([string[]]@($components | ForEach-Object { $_.'bom-ref' }))
    $dependencies = [System.Collections.Generic.List[object]]::new()
    $dependencies.Add([ordered]@{ ref = $root.'bom-ref'; dependsOn = @($components | ForEach-Object { $_.'bom-ref' }) })
    $listed = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($entry in Get-JsonArray $result 'dependencies' | Where-Object { $refs.Contains($_.ref) }) {
        $dependencies.Add([ordered]@{ ref = $entry.ref; dependsOn = @(Get-JsonArray $entry 'dependsOn' | Where-Object { $refs.Contains($_) }) })
        $listed.Add($entry.ref) | Out-Null
    }
    foreach ($ref in $refs | Where-Object { -not $listed.Contains($_) }) { $dependencies.Add([ordered]@{ ref = $ref; dependsOn = @() }) }

    $result | Add-Member -Force -NotePropertyName components -NotePropertyValue $components.ToArray()
    $result | Add-Member -Force -NotePropertyName dependencies -NotePropertyValue $dependencies.ToArray()
    $result | ConvertTo-Json -Depth 32 | ConvertFrom-Json
}

<#
.SYNOPSIS
    Problems with an artifact's SBOM: an assembly in the artifact that no component accounts for,
    a component without a license.
#>
function Test-SbomCoverage([object] $Bom, [object[]] $Files) {
    $located = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($component in Get-JsonArray $Bom 'components') {
        foreach ($occurrence in Get-JsonArray (Get-JsonValue $component 'evidence') 'occurrences') {
            $located.Add($occurrence.location) | Out-Null
        }
        if (-not @(Get-LicenseId $component)) { "$($component.name) has no license in the SBOM" }
    }
    foreach ($file in $Files | Where-Object { $_.Name -match '\.dll$' -and -not $located.Contains($_.Path) }) {
        "$($file.Path) is in the artifact, but no SBOM component accounts for it"
    }
}

<#
.SYNOPSIS
    Problems with NOTICE for an artifact: a third-party component of its SBOM that NOTICE doesn't
    name (packages by name and version, vendored files by name) or names without its license, and
    a third-party assembly in the artifact that NOTICE doesn't name.
#>
function Test-NoticeCoverage([string] $Notice, [object] $Bom, [object[]] $Files, [string] $Supplier) {
    $sections = $Notice -split '(?m)^[-=]{20,}\s*$'
    foreach ($component in Get-JsonArray $Bom 'components') {
        if ((Get-JsonValue (Get-JsonValue $component 'supplier') 'name') -eq $Supplier) { continue }
        $key = if ($component.type -eq 'file') { $component.name } else { "$($component.name) $($component.version)" }
        $entries = @($sections | Where-Object { $_.Contains($key) })
        if (-not $entries) {
            "NOTICE doesn't name $key"
            continue
        }
        foreach ($id in @(Get-LicenseId $component)) {
            $wording = $script:noticeWording[$id]
            if (-not $wording) { "NOTICE has no wording for license $id of $key" }
            elseif (-not @($entries | Where-Object { $_.Contains($wording) })) { "NOTICE names $key but not its license ($wording)" }
        }
    }
    foreach ($file in $Files | Where-Object { $_.Name -match '\.dll$' -and $_.Name -notmatch $script:ownFile -and -not $Notice.Contains($_.Name) }) {
        "NOTICE doesn't name $($file.Path), which the artifact bundles"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Export-ModuleMember -Function Get-ArtifactFile, Get-PackageAssembly, Select-ShippedSbom, Test-SbomCoverage, Test-NoticeCoverage
