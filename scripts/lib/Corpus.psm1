#Requires -Version 7.2
<#
    The real-world Daml corpus of the grammar: checking the pins in grammar/corpus-sources.psd1,
    choosing the files of a source, copying them with duplicates dropped, and the manifest that
    says where every file came from. Fetching is scripts/collect-corpus.ps1's business.
#>

function Test-CorpusSources {
    <#
    .SYNOPSIS
        The problems of a corpus configuration, one string each; nothing when it is sound.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [hashtable] $Config)

    if (-not ($Config.MinimumFiles -is [int]) -or $Config.MinimumFiles -le 0) {
        'MinimumFiles must be a positive number'
    }
    $names = @{}
    foreach ($source in @($Config.Sources)) {
        $name = $source.Name
        if (-not ($name -match '^[a-z0-9][a-z0-9-]*$')) {
            "source name '$name' must be lower-case letters, digits and dashes"
        }
        elseif ($names.ContainsKey($name)) {
            "source name '$name' is used twice"
        }
        $names[$name] = $true

        if (-not $source.Local) {
            if (-not ($source.Repository -match '^https://')) { "${name}: Repository must be an https URL" }
            if (-not ($source.Commit -match '^[0-9a-f]{40}$')) { "${name}: Commit '$($source.Commit)' is not a full commit id" }
            if (-not $source.Ref) { "${name}: Ref is missing (the branch or tag the commit came from)" }
        }
        foreach ($field in 'License', 'LicenseFile', 'Sdk') {
            if (-not $source[$field]) { "${name}: $field is missing" }
        }
        $paths = @($source.Paths)
        if ($paths.Count -eq 0) {
            "${name}: no Paths"
        }
        foreach ($path in $paths) {
            if ([IO.Path]::IsPathRooted($path) -or $path -match '^[A-Za-z]:' -or ($path -split '[\\/]') -contains '..') {
                "${name}: path '$path' must stay inside the source"
            }
        }
    }
}

function Get-CorpusFiles {
    <#
    .SYNOPSIS
        The .daml files under -Paths of -Root, relative to -Root with forward slashes, sorted and
        each once. Build output (.daml folders) and other hidden folders are left out.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string[]] $Paths
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $found = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $Paths) {
        $start = Join-Path $rootFull $path
        if (-not (Test-Path -LiteralPath $start -PathType Container)) {
            continue
        }
        foreach ($file in Get-ChildItem -LiteralPath $start -Recurse -File -Filter '*.daml' -Force) {
            $relative = $file.FullName.Substring($rootFull.Length + 1).Replace('\', '/')
            $folders = $relative.Split('/')
            # Hidden folders, `.daml` build output among them; the file name itself may start
            # with anything.
            if (@($folders[0..($folders.Count - 2)] | Where-Object { $_.StartsWith('.') }).Count -gt 0) {
                continue
            }
            [void] $found.Add($relative)
        }
    }
    $found
}

function Copy-CorpusSource {
    <#
    .SYNOPSIS
        Copies the files of one source into -Destination/<name>/ with its licence. A file with the
        same content as one copied before (from any source; -Seen is shared) is dropped.
    .OUTPUTS
        Name, Files (Path relative to -Destination and Sha256 for each) and Duplicates.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [hashtable] $Source,
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [hashtable] $Seen
    )

    $name = $Source.Name
    $target = Join-Path $Destination $name
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $license = Join-Path $Root $Source.LicenseFile
    if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
        throw "${name}: licence file '$($Source.LicenseFile)' not found in $Root"
    }
    Copy-Item -LiteralPath $license -Destination (Join-Path $target 'LICENSE') -Force

    $files = [Collections.Generic.List[object]]::new()
    $duplicates = 0
    foreach ($relative in Get-CorpusFiles -Root $Root -Paths $Source.Paths) {
        $from = Join-Path $Root $relative
        $hash = (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Seen.ContainsKey($hash)) {
            $duplicates++
            continue
        }
        $path = "$name/$relative"
        $Seen[$hash] = $path
        $to = Join-Path $Destination $path
        New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null
        Copy-Item -LiteralPath $from -Destination $to -Force
        $files.Add([pscustomobject]@{ Path = $path; Sha256 = $hash })
    }
    [pscustomobject]@{ Name = $name; Files = $files.ToArray(); Duplicates = $duplicates }
}

function New-CorpusManifest {
    <#
    .SYNOPSIS
        The manifest of a collected corpus: every source with its pin, SDK, licence and counts,
        and every file with its source and hash.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object[]] $Results,
        [Parameter(Mandatory)] [hashtable[]] $Sources,
        [Parameter(Mandatory)] [datetime] $GeneratedAt
    )

    $byName = @{}
    foreach ($source in $Sources) { $byName[$source.Name] = $source }
    $entries = foreach ($result in $Results) {
        foreach ($file in $result.Files) {
            [ordered]@{ path = $file.Path; source = $result.Name; sha256 = $file.Sha256 }
        }
    }
    [ordered]@{
        generatedAt = $GeneratedAt.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        files       = @($entries).Count
        sources     = @(foreach ($result in $Results) {
            $source = $byName[$result.Name]
            [ordered]@{
                name              = $result.Name
                repository        = if ($source.Local) { 'this repository' } else { $source.Repository }
                ref               = $source.Ref
                commit            = $source.Commit
                sdk               = $source.Sdk
                license           = $source.License
                licenseFile       = "$($result.Name)/LICENSE"
                paths             = @($source.Paths)
                files             = @($result.Files).Count
                duplicatesDropped = $result.Duplicates
            }
        })
        entries     = @($entries)
    }
}

Export-ModuleMember -Function Test-CorpusSources, Get-CorpusFiles, Copy-CorpusSource, New-CorpusManifest
