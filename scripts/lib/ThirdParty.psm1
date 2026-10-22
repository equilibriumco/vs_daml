# Vendored third-party files: each folder under third_party has a LICENSE and a PIN that records
# where its files came from and the sha256 of each, so a re-pin or a stray edit can't go unnoticed.
Set-StrictMode -Version Latest

function Get-FileSha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# PIN layout: "key: value" header lines (repository, commit, ...), then one line per file:
# "<sha256>  <local path>  [<upstream path>]".
function Read-Pin([string] $Path) {
    $header = @{}
    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^([0-9a-f]{64})\s+(\S+)(?:\s+(\S+))?\s*$') {
            $files.Add([pscustomobject]@{ Sha256 = $Matches[1]; Path = $Matches[2]; Upstream = if ($Matches[3]) { $Matches[3] } else { $Matches[2] } })
        }
        elseif ($line -match '^(\w[\w ]*):\s+(.+?)\s*$') {
            $header[$Matches[1]] = $Matches[2]
        }
    }
    [pscustomobject]@{ Header = $header; Files = $files }
}

function Get-VendoredFolder([string] $Root) {
    Get-ChildItem -LiteralPath $Root -Directory | Sort-Object Name
}

# Files under Folder that git ignores (build output of a vendored project, say), relative to it
# with forward slashes. Outside a git work tree nothing counts as ignored.
function Get-IgnoredFile([string] $Folder) {
    $ignored = @{}
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $ignored }
    $inside = & git -C $Folder rev-parse --is-inside-work-tree 2>$null
    if ($LASTEXITCODE -ne 0 -or $inside -ne 'true') { return $ignored }
    foreach ($path in & git -c core.quotepath=off -C $Folder ls-files --others --ignored --exclude-standard 2>$null) {
        $ignored[$path] = $true
    }
    $ignored
}

<#
.SYNOPSIS
    Every file a PIN lists, with the folder it belongs to and the repository and commit it came from.
#>
function Get-PinnedFile([string] $Root) {
    foreach ($folder in Get-VendoredFolder $Root) {
        $pinPath = Join-Path $folder.FullName 'PIN'
        if (-not (Test-Path -LiteralPath $pinPath)) { continue }
        $pin = Read-Pin $pinPath
        foreach ($file in $pin.Files) {
            [pscustomobject]@{
                Folder     = $folder.Name
                Path       = $file.Path
                Upstream   = $file.Upstream
                Sha256     = $file.Sha256
                Repository = $pin.Header['repository']
                Commit     = $pin.Header['commit']
            }
        }
    }
}

<#
.SYNOPSIS
    Problems with the vendored folders under Root: no PIN or LICENSE, a pinned file missing or with
    another hash, a file the PIN doesn't list. Nothing returned means all of it matches.
#>
function Test-PinnedFile([string] $Root) {
    foreach ($folder in Get-VendoredFolder $Root) {
        $name = $folder.Name
        if (-not (Test-Path -LiteralPath (Join-Path $folder.FullName 'LICENSE'))) { "third_party/$name has no LICENSE" }
        $pinPath = Join-Path $folder.FullName 'PIN'
        if (-not (Test-Path -LiteralPath $pinPath)) {
            "third_party/$name has no PIN"
            continue
        }
        $listed = @{}
        foreach ($file in (Read-Pin $pinPath).Files) {
            $listed[$file.Path] = $true
            $full = Join-Path $folder.FullName $file.Path
            if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
                "third_party/$name/$($file.Path) is listed in its PIN but missing"
                continue
            }
            $actual = Get-FileSha256 $full
            if ($actual -ne $file.Sha256) { "third_party/$name/$($file.Path) has sha256 $actual, its PIN says $($file.Sha256)" }
        }
        $ignored = Get-IgnoredFile $folder.FullName
        foreach ($file in Get-ChildItem -LiteralPath $folder.FullName -Recurse -File) {
            $relative = $file.FullName.Substring($folder.FullName.Length + 1).Replace('\', '/')
            if ($relative -ne 'PIN' -and -not $listed.ContainsKey($relative) -and -not $ignored.ContainsKey($relative)) {
                "third_party/$name/$relative is not in its PIN"
            }
        }
    }
}

Export-ModuleMember -Function Get-PinnedFile, Test-PinnedFile
