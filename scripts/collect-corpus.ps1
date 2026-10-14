#Requires -Version 7.2
<#
.SYNOPSIS
    Collects the real-world Daml corpus the grammar is measured against.

.DESCRIPTION
    Takes the .daml files of every source in grammar/corpus-sources.psd1 at its pinned commit and
    copies them to grammar/test/real-world/<source>/, with each source's licence and a
    manifest.json saying where every file came from. Files whose content another source already
    brought are dropped. Neither the corpus nor the cache of checkouts (grammar/test/.cache) is
    committed; both can be deleted at any time.

    Only the folders listed for a source are fetched (a shallow, sparse checkout without the rest
    of the history), and a checkout already at the pinned commit is reused. Needs git and network
    access to GitHub; fails when the corpus has fewer files than MinimumFiles.

.PARAMETER Force
    Fetches every source again, even when its checkout is already at the pinned commit.

.EXAMPLE
    pwsh scripts/collect-corpus.ps1
#>
[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/Corpus.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$config = Import-PowerShellDataFile (Join-Path $root 'grammar/corpus-sources.psd1')
$problems = @(Test-CorpusSources $config)
if ($problems.Count -gt 0) {
    throw "grammar/corpus-sources.psd1 has problems:`n  " + ($problems -join "`n  ")
}

$corpus = Join-Path $root 'grammar/test/real-world'
$cache = Join-Path $root 'grammar/test/.cache'

function Invoke-Git([string] $Directory, [string[]] $Arguments) {
    & git -C $Directory @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed in $Directory (exit code $LASTEXITCODE)" }
}

# A checkout of just the source's folders and licence, at its commit.
function Sync-Source([hashtable] $Source) {
    $dir = Join-Path $cache $Source.Name
    $patterns = @($Source.Paths | ForEach-Object { '/' + $_.Trim('/').Replace('\', '/') + '/' }) + @('/' + $Source.LicenseFile)
    if (-not (Test-Path (Join-Path $dir '.git'))) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Invoke-Git $dir @('init', '--quiet')
        Invoke-Git $dir @('remote', 'add', 'origin', $Source.Repository)
        # The documentation's paths are deep enough to pass Windows' old 260-character limit.
        Invoke-Git $dir @('config', 'core.longpaths', 'true')
    }
    Invoke-Git $dir (@('sparse-checkout', 'set', '--no-cone') + $patterns)
    $head = (& git -C $dir rev-parse --verify --quiet HEAD) 2>$null
    if ($Force -or $head -ne $Source.Commit) {
        Write-Host "Fetching $($Source.Name) at $($Source.Commit.Substring(0, 12)) ($($Source.Ref))…"
        Invoke-Git $dir @('fetch', '--quiet', '--depth', '1', '--filter=blob:none', 'origin', $Source.Commit)
        Invoke-Git $dir @('checkout', '--quiet', '--detach', $Source.Commit)
    }
    $head = & git -C $dir rev-parse HEAD
    if ($head -ne $Source.Commit) { throw "$($Source.Name): the checkout is at $head, not the pinned $($Source.Commit)" }
    $dir
}

# Starts from an empty corpus folder, so files of an earlier collection can't linger.
if (Test-Path $corpus) {
    $full = [IO.Path]::GetFullPath($corpus)
    if (-not $full.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'grammar/test')), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear $full"
    }
    Remove-Item -LiteralPath $full -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $corpus | Out-Null

$seen = @{}
$results = foreach ($source in $config.Sources) {
    $sourceRoot = if ($source.Local) { $root } else { Sync-Source $source }
    Copy-CorpusSource -Source $source -Root $sourceRoot -Destination $corpus -Seen $seen
}

$manifest = New-CorpusManifest -Results @($results) -Sources @($config.Sources) -GeneratedAt (Get-Date)
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $corpus 'manifest.json') -Encoding utf8NoBOM

$manifest.sources | ForEach-Object { [pscustomobject]$_ } |
    Format-Table name, sdk, files, duplicatesDropped, @{ n = 'commit'; e = { if ($_.commit) { $_.commit.Substring(0, 12) } else { 'working tree' } } }, license -AutoSize |
    Out-String | Write-Host
Write-Host "$($manifest.files) files in $corpus"
if ($manifest.files -lt $config.MinimumFiles) {
    Write-Error "The corpus has $($manifest.files) files; at least $($config.MinimumFiles) are needed."
    exit 1
}
