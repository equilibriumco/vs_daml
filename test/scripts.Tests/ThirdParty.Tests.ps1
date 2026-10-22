Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\ThirdParty.psm1') -Force

function Get-Sha256([string] $Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
}

# A vendored folder: its files, written as UTF-8 without BOM, and a PIN listing the given ones.
function New-Vendored([string] $Root, [string] $Name, [hashtable] $Files, [string[]] $Pinned = $null, [hashtable] $PinnedAs = @{}) {
    $folder = Join-Path $Root $Name
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    foreach ($path in $Files.Keys) {
        $file = Join-Path $folder $path
        New-Item -ItemType Directory -Force -Path (Split-Path $file -Parent) | Out-Null
        [IO.File]::WriteAllText($file, $Files[$path])
    }
    if ($null -eq $Pinned) { $Pinned = @($Files.Keys) }
    $lines = @("repository:  https://github.com/example/$Name", 'commit:      0123456789abcdef0123456789abcdef01234567', '', 'sha256  path')
    foreach ($path in $Pinned) {
        $text = if ($PinnedAs.ContainsKey($path)) { $PinnedAs[$path] } else { $Files[$path] }
        $lines += "$(Get-Sha256 $text)  $path"
    }
    [IO.File]::WriteAllText((Join-Path $folder 'PIN'), ($lines -join "`n") + "`n")
}

Describe 'Pinned third-party files' {
    BeforeEach {
        $script:root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:root | Out-Null
    }

    It 'accepts files that match their PIN' {
        New-Vendored $script:root 'lib' @{ 'LICENSE' = 'license'; 'src/a.js' = 'a' }
        @(Test-PinnedFile $script:root).Count | Should Be 0
    }

    It 'reports a file that no longer matches its hash' {
        New-Vendored $script:root 'lib' @{ 'LICENSE' = 'license'; 'a.js' = 'changed' } -PinnedAs @{ 'a.js' = 'a' }
        $problems = @(Test-PinnedFile $script:root)
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'lib/a\.js'
        $problems[0] | Should Match (Get-Sha256 'changed')
        $problems[0] | Should Match (Get-Sha256 'a')
    }

    It 'reports a pinned file that is missing' {
        New-Vendored $script:root 'lib' @{ 'LICENSE' = 'license'; 'a.js' = 'a' }
        Remove-Item (Join-Path $script:root 'lib\a.js')
        $problems = @(Test-PinnedFile $script:root)
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'lib/a\.js.*missing'
    }

    It 'reports a file the PIN does not list' {
        New-Vendored $script:root 'lib' @{ 'LICENSE' = 'license'; 'a.js' = 'a'; 'sub/b.js' = 'b' } -Pinned @('LICENSE', 'a.js')
        $problems = @(Test-PinnedFile $script:root)
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'lib/sub/b\.js.*not in.*PIN'
    }

    # A local build can leave obj/ output inside a vendored project; git ignores it, so does the check.
    It 'skips files git ignores but still reports other unlisted ones' {
        New-Vendored $script:root 'lib' @{ 'LICENSE' = 'license'; 'a.js' = 'a'; 'obj/Debug/x.cs' = 'built'; 'stray.js' = 's' } -Pinned @('LICENSE', 'a.js')
        [IO.File]::WriteAllText((Join-Path $script:root '.gitignore'), "obj/`n")
        git -C $script:root init --quiet
        $problems = @(Test-PinnedFile $script:root)
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'lib/stray\.js.*not in.*PIN'
    }

    It 'reports a folder without a PIN or without a license' {
        New-Item -ItemType Directory -Force -Path (Join-Path $script:root 'bare') | Out-Null
        [IO.File]::WriteAllText((Join-Path $script:root 'bare\a.js'), 'a')
        [IO.File]::WriteAllText((Join-Path $script:root 'bare\LICENSE'), 'license')
        New-Vendored $script:root 'unlicensed' @{ 'a.js' = 'a' }
        $problems = @(Test-PinnedFile $script:root)
        $problems.Count | Should Be 2
        ($problems -join ';') | Should Match 'bare.*no PIN'
        ($problems -join ';') | Should Match 'unlicensed.*no LICENSE'
    }

    It 'reads repository, commit and upstream path of every pinned file' {
        New-Vendored $script:root 'lib' @{ 'LICENSE' = 'license'; 'a.js' = 'a' }
        $pin = Join-Path $script:root 'lib\PIN'
        [IO.File]::WriteAllText($pin, ([IO.File]::ReadAllText($pin) -replace "(?m)^(\w{64})  a\.js$", '$1  a.js  upstream/src/a.js'))
        $files = @(Get-PinnedFile $script:root)
        $files.Count | Should Be 2
        $source = @($files | Where-Object Path -eq 'a.js')[0]
        $source.Folder | Should Be 'lib'
        $source.Upstream | Should Be 'upstream/src/a.js'
        $source.Sha256 | Should Be (Get-Sha256 'a')
        $source.Repository | Should Be 'https://github.com/example/lib'
        $source.Commit | Should Be '0123456789abcdef0123456789abcdef01234567'
        @($files | Where-Object Path -eq 'LICENSE')[0].Upstream | Should Be 'LICENSE'
        @(Test-PinnedFile $script:root).Count | Should Be 0
    }

    It 'holds for every file vendored in this repository' {
        $problems = @(Test-PinnedFile (Join-Path $PSScriptRoot '..\..\third_party'))
        $problems -join "`n" | Should BeNullOrEmpty
    }
}
