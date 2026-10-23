Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\Release.psm1') -Force

function New-Artifacts([string] $Folder, [string] $NuGet = '0.9.0-beta.3', [string] $Vsix = '0.9.0.3', [string] $TemplatesVersion = $null) {
    New-Item -ItemType Directory -Force $Folder | Out-Null
    if (-not $TemplatesVersion) { $TemplatesVersion = $NuGet }
    foreach ($package in @("Equilibrium.Daml.Sdk.$NuGet", "Equilibrium.Daml.Build.CSharp.$NuGet", "Equilibrium.Daml.Templates.$TemplatesVersion")) {
        Set-Content (Join-Path $Folder "$package.nupkg") 'not read'
    }
    $path = Join-Path $Folder 'Equilibrium.Daml.VisualStudio.vsix'
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('extension.vsixmanifest').Open())
        try { $writer.Write('<PackageManifest xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"><Metadata><Identity Id="Equilibrium.DamlTools" Version="' + $Vsix + '" Publisher="Equilibrium" /></Metadata></PackageManifest>') }
        finally { $writer.Dispose() }
    }
    finally { $zip.Dispose() }
}

Describe 'Release version' {
    BeforeEach { $script:folder = Join-Path ([IO.Path]::GetTempPath()) ('eqdaml-release-' + [guid]::NewGuid().ToString('N')) }
    AfterEach { if (Test-Path -LiteralPath $script:folder) { Remove-Item -LiteralPath $script:folder -Recurse -Force } }

    It 'reads one version from the packages and the VSIX' {
        New-Artifacts $script:folder
        $version = Get-ReleaseVersion $script:folder
        $version.NuGet | Should Be '0.9.0-beta.3'
        $version.Vsix | Should Be '0.9.0.3'
    }

    It 'takes a stable NuGet version with any build number in the VSIX' {
        New-Artifacts $script:folder -NuGet '1.0.0' -Vsix '1.0.0.17'
        (Get-ReleaseVersion $script:folder).NuGet | Should Be '1.0.0'
    }

    It 'refuses packages of different versions' {
        New-Artifacts $script:folder -TemplatesVersion '0.9.0-beta.2'
        { Get-ReleaseVersion $script:folder } | Should Throw 'Templates'
    }

    It 'refuses a VSIX of another release' {
        New-Artifacts $script:folder -Vsix '0.9.0.2'
        { Get-ReleaseVersion $script:folder } | Should Throw '0.9.0.2'
        Remove-Item -LiteralPath $script:folder -Recurse -Force
        New-Artifacts $script:folder -Vsix '0.8.0.3'
        { Get-ReleaseVersion $script:folder } | Should Throw '0.8.0.3'
    }
}

Describe 'Release tag' {
    It 'accepts a run from a branch' {
        @(Test-ReleaseTag -RefType 'branch' -RefName 'main' -Version '0.9.0-beta.3').Count | Should Be 0
    }

    It 'accepts the tag v<version>' {
        @(Test-ReleaseTag -RefType 'tag' -RefName 'v0.9.0-beta.3' -Version '0.9.0-beta.3').Count | Should Be 0
    }

    It 'refuses a tag that names another version' {
        (Test-ReleaseTag -RefType 'tag' -RefName 'v0.9.0-beta.2' -Version '0.9.0-beta.3') | Should Match 'v0\.9\.0-beta\.3'
    }
}

