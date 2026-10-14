# Pester tests of scripts/lib/Corpus.psm1: the pins, which files are taken, copying with
# duplicates dropped, and the manifest. No network: sources are folders made here.

Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\Corpus.psm1') -Force

function New-Tree([string] $Root, [hashtable] $Files) {
    foreach ($relative in $Files.Keys) {
        $path = Join-Path $Root $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
        Set-Content -LiteralPath $path -Value $Files[$relative] -NoNewline
    }
}

function New-Source([string] $Name, [string[]] $Paths) {
    @{ Name = $Name; Repository = "https://example.com/$Name"; Ref = 'main'; Commit = ('a' * 40); Sdk = '3.6.0'; License = 'Apache-2.0'; LicenseFile = 'LICENSE'; Paths = $Paths }
}

Describe 'Test-CorpusSources' {
    It 'accepts the pins this repository uses' {
        $config = Import-PowerShellDataFile (Join-Path $PSScriptRoot '..\..\grammar\corpus-sources.psd1')

        @(Test-CorpusSources $config).Count | Should Be 0
    }

    It 'names every problem of a bad configuration' {
        $bad = New-Source 'x' @('../outside', 'C:\rooted')
        $bad.Commit = 'main'
        $noLicense = New-Source 'y' @('src')
        $noLicense.Remove('License')
        $twice = New-Source 'x' @('src')
        $config = @{ MinimumFiles = 0; Sources = @($bad, $noLicense, $twice) }

        $problems = @(Test-CorpusSources $config)

        ($problems -join "`n") | Should Match 'MinimumFiles'
        ($problems -join "`n") | Should Match "x: Commit 'main' is not a full commit id"
        ($problems -join "`n") | Should Match "x: path '../outside'"
        ($problems -join "`n") | Should Match "x: path 'C:\\rooted'"
        ($problems -join "`n") | Should Match 'y: License is missing'
        ($problems -join "`n") | Should Match "source name 'x' is used twice"
    }

    It 'wants paths for every source' {
        $empty = New-Source 'z' @()

        (@(Test-CorpusSources @{ MinimumFiles = 1; Sources = @($empty) }) -join "`n") | Should Match 'z: no Paths'
    }
}

Describe 'Get-CorpusFiles' {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('corpus-' + [guid]::NewGuid().ToString('N'))
    New-Tree $root @{
        'lib/A.daml'                  = 'module A where'
        'lib/Deep/B.daml'             = 'module Deep.B where'
        'lib/README.md'               = 'not daml'
        'lib/.daml/dist/Gen.daml'     = 'build output'
        'docs/with space/C.daml'      = 'module C where'
        'docs/.hidden/D.daml'         = 'hidden'
        'other/E.daml'                = 'not asked for'
    }

    It 'takes the .daml files under the given paths, sorted, with forward slashes' {
        $files = @(Get-CorpusFiles -Root $root -Paths @('lib', 'docs'))

        $files | Should Be @('docs/with space/C.daml', 'lib/A.daml', 'lib/Deep/B.daml')
    }

    It 'leaves out build output and hidden folders' {
        (@(Get-CorpusFiles -Root $root -Paths @('lib')) -join ',') | Should Not Match '\.daml/|\.hidden'
    }

    It 'lists a file once when two paths cover it' {
        @(Get-CorpusFiles -Root $root -Paths @('lib', 'lib/Deep')).Count | Should Be 2
    }

    It 'finds nothing under a path that does not exist' {
        @(Get-CorpusFiles -Root $root -Paths @('missing')).Count | Should Be 0
    }

    Remove-Item -Recurse -Force $root
}

Describe 'Copy-CorpusSource and New-CorpusManifest' {
    $work = Join-Path ([IO.Path]::GetTempPath()) ('corpus-' + [guid]::NewGuid().ToString('N'))
    $first = Join-Path $work 'first'
    $second = Join-Path $work 'second'
    $out = Join-Path $work 'out'
    New-Tree $first @{ 'LICENSE' = 'license one'; 'src/A.daml' = 'module A where'; 'src/B.daml' = 'module B where' }
    New-Tree $second @{ 'LICENSE' = 'license two'; 'daml/Copy.daml' = 'module A where'; 'daml/C.daml' = 'module C where' }
    $seen = @{}

    $one = Copy-CorpusSource -Source (New-Source 'first' @('src')) -Root $first -Destination $out -Seen $seen
    $two = Copy-CorpusSource -Source (New-Source 'second' @('daml')) -Root $second -Destination $out -Seen $seen

    It 'copies each source into its own folder with its licence' {
        Test-Path (Join-Path $out 'first/src/A.daml') | Should Be $true
        Test-Path (Join-Path $out 'second/daml/C.daml') | Should Be $true
        Get-Content (Join-Path $out 'second/LICENSE') -Raw | Should Be 'license two'
    }

    It 'drops a file whose content an earlier source already brought' {
        $one.Files.Count | Should Be 2
        $two.Files.Count | Should Be 1
        $two.Duplicates | Should Be 1
        Test-Path (Join-Path $out 'second/daml/Copy.daml') | Should Be $false
    }

    It 'records each file with its hash' {
        $two.Files[0].Path | Should Be 'second/daml/C.daml'
        $two.Files[0].Sha256 | Should Match '^[0-9a-f]{64}$'
    }

    It 'writes a manifest with the pins, licences, counts and files' {
        $manifest = New-CorpusManifest -Results @($one, $two) -Sources @((New-Source 'first' @('src')), (New-Source 'second' @('daml'))) -GeneratedAt ([datetime]'2026-10-14T12:00:00Z')

        $manifest.files | Should Be 3
        $manifest.sources.Count | Should Be 2
        $manifest.sources[1].name | Should Be 'second'
        $manifest.sources[1].commit | Should Be ('a' * 40)
        $manifest.sources[1].license | Should Be 'Apache-2.0'
        $manifest.sources[1].licenseFile | Should Be 'second/LICENSE'
        $manifest.sources[1].files | Should Be 1
        $manifest.sources[1].duplicatesDropped | Should Be 1
        @($manifest.entries).Count | Should Be 3
    }

    Remove-Item -Recurse -Force $work
}
