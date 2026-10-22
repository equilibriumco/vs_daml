Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\Marketplace.psm1') -Force

$script:longNotice = 'EQ Daml Tools for Visual Studio is an independent project by Equilibrium and is not affiliated with, sponsored by, or endorsed by Digital Asset. Daml and Canton are trademarks of Digital Asset (Switzerland) GmbH. Visual Studio is a trademark of Microsoft Corporation.'
$script:shortNotice = 'Independent Daml tooling by Equilibrium — not affiliated with or endorsed by Digital Asset. Daml is a trademark of Digital Asset (Switzerland) GmbH.'

function New-Listing([string] $Folder, [hashtable] $Manifest = @{}, [string] $Overview = $null, [string] $Description = $null) {
    New-Item -ItemType Directory -Force $Folder | Out-Null
    $publish = [ordered]@{
        '$schema'     = 'http://json.schemastore.org/vsix-publish'
        categories    = @('programming languages', 'build')
        identity      = @{ internalName = 'EqDamlTools' }
        overview      = 'overview.md'
        priceCategory = 'free'
        publisher     = 'Equilibrium'
        private       = $false
        qna           = $false
        repo          = 'https://github.com/equilibriumco/vs_daml'
    }
    foreach ($key in $Manifest.Keys) { $publish[$key] = $Manifest[$key] }
    $publish | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Folder 'publishManifest.json') -Encoding UTF8
    if (-not $Overview) { $Overview ="# EQ Daml Tools for Visual Studio`n`nDaml in Visual Studio.`n`n## Trademarks`n`nEQ Daml Tools for Visual Studio is an independent project by Equilibrium and is not affiliated with,`nsponsored by, or endorsed by Digital Asset. Daml and Canton are trademarks of Digital Asset`n(Switzerland) GmbH. Visual Studio is a trademark of Microsoft Corporation.`n" }
    Set-Content (Join-Path $Folder 'overview.md') $Overview -Encoding UTF8
    if (-not $Description) { $Description ="Daml language support for Visual Studio. $script:shortNotice" }
    $vsix = '<PackageManifest xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"><Metadata><Identity Id="Equilibrium.DamlTools" Version="1.0" Language="en-US" Publisher="Equilibrium" /><DisplayName>EQ Daml Tools for Visual Studio</DisplayName><Description xml:space="preserve">' + [Security.SecurityElement]::Escape($Description) + '</Description></Metadata></PackageManifest>'
    Set-Content (Join-Path $Folder 'source.extension.vsixmanifest') $vsix -Encoding UTF8
}

function Test-Folder([string] $Folder) {
    @(Test-MarketplaceListing -PublishManifest (Join-Path $Folder 'publishManifest.json') -VsixManifest (Join-Path $Folder 'source.extension.vsixmanifest'))
}

Describe 'Marketplace listing' {
    BeforeEach { $script:folder = Join-Path ([IO.Path]::GetTempPath()) ('eqdaml-listing-' + [guid]::NewGuid().ToString('N')) }
    AfterEach { if (Test-Path -LiteralPath $script:folder) { Remove-Item -LiteralPath $script:folder -Recurse -Force } }

    It 'accepts a listing that follows the trademark rules' {
        New-Listing $script:folder
        (Test-Folder $script:folder).Count | Should Be 0
    }

    It 'accepts the listing in the repository' {
        $root = Join-Path $PSScriptRoot '..\..'
        $problems = @(Test-MarketplaceListing -PublishManifest (Join-Path $root 'src\Equilibrium.Daml.VisualStudio\Marketplace\publishManifest.json') -VsixManifest (Join-Path $root 'src\Equilibrium.Daml.VisualStudio\source.extension.vsixmanifest'))
        $problems -join "`n" | Should Be ''
    }

    It 'needs the overview page it names' {
        New-Listing $script:folder -Manifest @{ overview = 'missing.md' }
        (Test-Folder $script:folder) -join "`n" | Should Match 'missing\.md'
    }

    It 'needs the long trademark notice on the overview page, wrapped or not' {
        New-Listing $script:folder -Overview "# EQ Daml Tools for Visual Studio`n`nDaml in Visual Studio.`n"
        (Test-Folder $script:folder) -join "`n" | Should Match 'notice'
    }

    It 'rejects endorsement words outside the notice' {
        New-Listing $script:folder -Overview ("# EQ Daml Tools`n`nThe official Daml extension.`n`n" + $script:longNotice + "`n")
        (Test-Folder $script:folder) -join "`n" | Should Match 'official'
    }

    It 'rejects names the trademark rules forbid' {
        New-Listing $script:folder -Overview ("# Daml for Visual Studio`n`n" + $script:longNotice + "`n")
        (Test-Folder $script:folder) -join "`n" | Should Match 'Daml for Visual Studio'
    }

    It 'needs the short notice at the end of the extension description' {
        New-Listing $script:folder -Description 'Daml language support for Visual Studio.'
        (Test-Folder $script:folder) -join "`n" | Should Match 'short notice'
    }

    It 'takes one to three known categories' {
        New-Listing $script:folder -Manifest @{ categories = @('languages') }
        (Test-Folder $script:folder) -join "`n" | Should Match 'languages'
        New-Listing $script:folder -Manifest @{ categories = @('coding', 'build', 'testing', 'other') }
        (Test-Folder $script:folder) -join "`n" | Should Match 'three'
    }

    It 'lists the extension as free and public with the repository' {
        New-Listing $script:folder -Manifest @{ priceCategory = 'trial'; private = $true; repo = 'https://example.com/x' }
        $problems = (Test-Folder $script:folder) -join "`n"
        $problems | Should Match 'free'
        $problems | Should Match 'private'
        $problems | Should Match 'repo'
    }
}
