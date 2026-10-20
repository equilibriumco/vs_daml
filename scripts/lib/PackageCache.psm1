Set-StrictMode -Version Latest

<#
.SYNOPSIS
    The copies a NuGet package folder (the machine's %USERPROFILE%\.nuget\packages, say) holds of
    the given packages' versions that weren't made from these very files.

.DESCRIPTION
    NuGet extracts a version into the package folder once and then takes that copy for every
    restore, whatever a source now offers under the same version. Local packs always carry build
    number 0, so a copy from an earlier pack hides the new package from every project that uses
    that folder. A copy counts as the package's own when NuGet's record of its SHA512 matches the
    file; without a record nothing shows it is, so it counts as stale too.
#>
function Get-StalePackageCopy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]] $Package,
        [Parameter(Mandatory)] [string] $PackagesFolder
    )
    if (-not (Test-Path -LiteralPath $PackagesFolder)) { return }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($path in $Package) {
        $id, $version = Get-PackageIdentity $path
        $name = '{0}\{1}' -f $id.ToLowerInvariant(), $version.ToLowerInvariant()
        $copy = Join-Path $PackagesFolder $name
        if (-not (Test-Path -LiteralPath $copy)) { continue }
        $record = Join-Path $copy ('{0}.{1}.nupkg.sha512' -f $id.ToLowerInvariant(), $version.ToLowerInvariant())
        $recorded = if (Test-Path -LiteralPath $record) { (Get-Content -LiteralPath $record -Raw).Trim() } else { $null }
        if ($recorded -ne (Get-PackageHash $path)) {
            [pscustomobject]@{ Id = $id; Version = $version; Folder = $copy }
        }
    }
}

# The id and version as the package's own nuspec says, not as its file name suggests: ids hold
# dots and versions dashes, so the name can't be split reliably.
function Get-PackageIdentity([string] $Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.Name -like '*.nuspec' } | Select-Object -First 1
        if (-not $entry) { throw "$Path has no nuspec." }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
    $metadata = $nuspec.package.metadata
    return $metadata.id, $metadata.version
}

function Get-PackageHash([string] $Path) {
    $sha = [Security.Cryptography.SHA512]::Create()
    try {
        $stream = [IO.File]::OpenRead($Path)
        try { return [Convert]::ToBase64String($sha.ComputeHash($stream)) } finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

Export-ModuleMember -Function Get-StalePackageCopy
