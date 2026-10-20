Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\PackageCache.psm1') -Force

function New-TestPackage([string] $Path, [string] $Id, [string] $Version, [string] $Content = 'targets') {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @{
            "$Id.nuspec" = "<?xml version=`"1.0`"?><package xmlns=`"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd`"><metadata><id>$Id</id><version>$Version</version></metadata></package>"
            'build/Package.targets' = $Content
        }
        foreach ($name in $entries.Keys) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try { $writer.Write($entries[$name]) } finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

# What NuGet leaves in a package folder after extracting a package: the folder named after the
# lower-case id and version, and the package's SHA512 next to it.
function Add-CachedCopy([string] $Folder, [string] $Package, [string] $Id, [string] $Version) {
    $copy = Join-Path $Folder ("{0}\{1}" -f $Id.ToLowerInvariant(), $Version.ToLowerInvariant())
    New-Item -ItemType Directory -Force -Path $copy | Out-Null
    $sha = [Security.Cryptography.SHA512]::Create()
    try { $hash = [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($Package))) } finally { $sha.Dispose() }
    Set-Content -LiteralPath (Join-Path $copy ("{0}.{1}.nupkg.sha512" -f $Id.ToLowerInvariant(), $Version.ToLowerInvariant())) -Value $hash -NoNewline
    $copy
}

Describe 'Package copies in a NuGet package folder' {
    BeforeEach {
        $script:work = Join-Path ([IO.Path]::GetTempPath()) ('pkgcache-' + [guid]::NewGuid().ToString('N'))
        $script:cache = Join-Path $script:work 'packages'
        New-Item -ItemType Directory -Force -Path $script:cache | Out-Null
        $script:package = Join-Path $script:work 'Daml.Sdk.0.3.0-preview.0.nupkg'
        New-TestPackage $script:package 'Daml.Sdk' '0.3.0-preview.0'
    }
    AfterEach { if (Test-Path -LiteralPath $script:work) { Remove-Item -LiteralPath $script:work -Recurse -Force } }

    It 'names a copy of the same version that came from another package' {
        $older = Join-Path $script:work 'older.nupkg'
        New-TestPackage $older 'Daml.Sdk' '0.3.0-preview.0' 'older targets'
        $copy = Add-CachedCopy $script:cache $older 'Daml.Sdk' '0.3.0-preview.0'

        $stale = @(Get-StalePackageCopy -Package $script:package -PackagesFolder $script:cache)

        $stale.Count | Should Be 1
        $stale[0].Id | Should Be 'Daml.Sdk'
        $stale[0].Version | Should Be '0.3.0-preview.0'
        $stale[0].Folder | Should Be $copy
    }

    It 'passes a copy made from this very package' {
        Add-CachedCopy $script:cache $script:package 'Daml.Sdk' '0.3.0-preview.0' | Out-Null

        @(Get-StalePackageCopy -Package $script:package -PackagesFolder $script:cache).Count | Should Be 0
    }

    It 'passes a version the folder never had' {
        $other = Join-Path $script:work 'other.nupkg'
        New-TestPackage $other 'Daml.Sdk' '0.2.0-preview.0' 'older targets'
        Add-CachedCopy $script:cache $other 'Daml.Sdk' '0.2.0-preview.0' | Out-Null

        @(Get-StalePackageCopy -Package $script:package -PackagesFolder $script:cache).Count | Should Be 0
    }

    It 'names a copy whose hash is missing, as nothing shows it is this package' {
        New-Item -ItemType Directory -Force -Path (Join-Path $script:cache 'daml.sdk\0.3.0-preview.0') | Out-Null

        @(Get-StalePackageCopy -Package $script:package -PackagesFolder $script:cache).Count | Should Be 1
    }

    It 'passes everything when there is no package folder at all' {
        @(Get-StalePackageCopy -Package $script:package -PackagesFolder (Join-Path $script:work 'none')).Count | Should Be 0
    }
}
