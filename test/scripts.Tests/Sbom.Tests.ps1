Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\Sbom.psm1') -Force

# What the CycloneDX tool writes for a project: every package restore resolved, whether it ships or
# is only compiled against, all of them direct dependencies of the project. Leaves have no dependsOn.
function New-ToolBom {
    @'
{
  "bomFormat": "CycloneDX",
  "specVersion": "1.7",
  "metadata": { "component": { "type": "application", "bom-ref": "Daml.VisualStudio@1.0.0.0", "name": "Daml.VisualStudio", "version": "1.0.0.0" } },
  "components": [
    { "type": "library", "bom-ref": "pkg:nuget/YamlDotNet@18.1.0", "name": "YamlDotNet", "version": "18.1.0", "licenses": [ { "license": { "id": "MIT" } } ], "purl": "pkg:nuget/YamlDotNet@18.1.0" },
    { "type": "library", "bom-ref": "pkg:nuget/Microsoft.VisualStudio.SDK@17.14.40265", "name": "Microsoft.VisualStudio.SDK", "version": "17.14.40265", "purl": "pkg:nuget/Microsoft.VisualStudio.SDK@17.14.40265" },
    { "type": "library", "bom-ref": "pkg:nuget/Newtonsoft.Json@13.0.3", "name": "Newtonsoft.Json", "version": "13.0.3", "licenses": [ { "license": { "id": "MIT" } } ], "purl": "pkg:nuget/Newtonsoft.Json@13.0.3" },
    { "type": "library", "bom-ref": "pkg:nuget/Toolkit@17.0.551", "name": "Toolkit", "version": "17.0.551", "licenses": [ { "license": { "id": "Apache-2.0" } } ], "purl": "pkg:nuget/Toolkit@17.0.551" }
  ],
  "dependencies": [
    { "ref": "Daml.VisualStudio@1.0.0.0", "dependsOn": [ "pkg:nuget/YamlDotNet@18.1.0", "pkg:nuget/Microsoft.VisualStudio.SDK@17.14.40265", "pkg:nuget/Newtonsoft.Json@13.0.3", "pkg:nuget/Toolkit@17.0.551" ] },
    { "ref": "pkg:nuget/YamlDotNet@18.1.0" },
    { "ref": "pkg:nuget/Microsoft.VisualStudio.SDK@17.14.40265", "dependsOn": [ "pkg:nuget/Newtonsoft.Json@13.0.3" ] },
    { "ref": "pkg:nuget/Newtonsoft.Json@13.0.3", "dependsOn": [] },
    { "ref": "pkg:nuget/Toolkit@17.0.551", "dependsOn": [ "pkg:nuget/Microsoft.VisualStudio.SDK@17.14.40265" ] }
  ]
}
'@ | ConvertFrom-Json
}

function New-File([string] $Path, [string] $Sha256 = ('0' * 64)) {
    [pscustomobject]@{ Path = $Path; Name = ($Path -split '/')[-1]; Sha256 = $Sha256 }
}

# The VSIX of the tests: our assemblies, two bundled libraries (the toolkit's assembly is named
# differently from its package), a vendored script and the license files.
function New-VsixFiles {
    @(
        New-File 'Daml.VisualStudio.dll' ('1' * 64)
        New-File 'Daml.Build.Core.dll'
        New-File 'YamlDotNet.dll'
        New-File 'Community.VisualStudio.Toolkit.dll'
        New-File 'Assets/webview/webview.js' ('a' * 64)
        New-File 'Assets/webview/LICENSE' ('c' * 64)
        New-File 'LICENSE'
        New-File 'NOTICE'
        New-File 'Templates/Daml.Templates.0.3.0-preview.7.nupkg'
        New-File 'extension.vsixmanifest'
    )
}

function New-Pinned {
    @(
        [pscustomobject]@{ Folder = 'daml-extension'; Path = 'webview.js'; Upstream = 'sdk/src/webview.js'; Sha256 = ('a' * 64); Repository = 'https://github.com/digital-asset/daml'; Commit = 'abc123' }
        [pscustomobject]@{ Folder = 'daml-extension'; Path = 'grammar.xml'; Upstream = 'sdk/grammar.xml'; Sha256 = ('b' * 64); Repository = 'https://github.com/digital-asset/daml'; Commit = 'abc123' }
        [pscustomobject]@{ Folder = 'daml-extension'; Path = 'LICENSE'; Upstream = 'LICENSE'; Sha256 = ('c' * 64); Repository = 'https://github.com/digital-asset/daml'; Commit = 'abc123' }
    )
}

$script:assemblies = @{
    'pkg:nuget/YamlDotNet@18.1.0'                     = @('YamlDotNet.dll')
    'pkg:nuget/Microsoft.VisualStudio.SDK@17.14.40265' = @()
    'pkg:nuget/Newtonsoft.Json@13.0.3'                = @('Newtonsoft.Json.dll')
    'pkg:nuget/Toolkit@17.0.551'                      = @('Community.VisualStudio.Toolkit.dll')
}
$script:licenses = @{ 'daml-extension' = @{ Default = 'Apache-2.0'; Files = @{ 'grammar.xml' = 'BSD-3-Clause' } } }

function Select-TestSbom($Files = (New-VsixFiles)) {
    Select-ShippedSbom -Bom (New-ToolBom) -Files $Files -PackageAssemblies $script:assemblies -Pinned (New-Pinned) `
        -Licenses $script:licenses -Version '0.3.0.7' -Supplier 'Equilibrium' -License 'Apache-2.0'
}

function Get-Component($Bom, [string] $Name) { @($Bom.components | Where-Object { $_.name -eq $Name }) }

Describe 'SBOM of what an artifact ships' {
    It 'keeps the packages whose assemblies the artifact carries and drops the ones only compiled against' {
        $bom = Select-TestSbom
        (Get-Component $bom 'YamlDotNet').Count | Should Be 1
        (Get-Component $bom 'Toolkit').Count | Should Be 1
        (Get-Component $bom 'Microsoft.VisualStudio.SDK').Count | Should Be 0
        (Get-Component $bom 'Newtonsoft.Json').Count | Should Be 0
        (Get-Component $bom 'Toolkit')[0].evidence.occurrences[0].location | Should Be 'Community.VisualStudio.Toolkit.dll'
    }

    It 'prunes the dependency graph to the components it keeps' {
        $bom = Select-TestSbom
        $refs = @($bom.components | ForEach-Object { $_.'bom-ref' }) + 'Daml.VisualStudio@1.0.0.0'
        foreach ($entry in $bom.dependencies) {
            $refs -contains $entry.ref | Should Be $true
            foreach ($target in @($entry.dependsOn)) { $refs -contains $target | Should Be $true }
        }
        $root = @($bom.dependencies | Where-Object { $_.ref -eq 'Daml.VisualStudio@1.0.0.0' })[0]
        @($root.dependsOn).Count | Should Be @($bom.components).Count
        @(@($bom.dependencies | Where-Object { $_.ref -eq 'pkg:nuget/Toolkit@17.0.551' })[0].dependsOn).Count | Should Be 0
    }

    It 'lists our own assemblies and the nested templates package under our name and license' {
        $bom = Select-TestSbom
        $own = (Get-Component $bom 'Daml.VisualStudio')[0]
        $own.version | Should Be '0.3.0.7'
        $own.supplier.name | Should Be 'Equilibrium'
        $own.licenses[0].license.id | Should Be 'Apache-2.0'
        $own.hashes[0].alg | Should Be 'SHA-256'
        $own.hashes[0].content | Should Be ('1' * 64)
        (Get-Component $bom 'Daml.Build.Core').Count | Should Be 1
        $templates = (Get-Component $bom 'Daml.Templates')[0]
        $templates.version | Should Be '0.3.0-preview.7'
        $templates.purl | Should Be 'pkg:nuget/Daml.Templates@0.3.0-preview.7'
        $templates.evidence.occurrences[0].location | Should Be 'Templates/Daml.Templates.0.3.0-preview.7.nupkg'
        $bom.metadata.component.supplier.name | Should Be 'Equilibrium'
        $bom.metadata.component.licenses[0].license.id | Should Be 'Apache-2.0'
    }

    It 'keeps references unique when an assembly has the name and version of the artifact' {
        $bom = Select-ShippedSbom -Bom (New-ToolBom) -Files (New-VsixFiles) -PackageAssemblies $script:assemblies -Version '1.0.0.0' -Supplier 'Equilibrium' -License 'Apache-2.0'
        $refs = @($bom.components | ForEach-Object { $_.'bom-ref' }) + $bom.metadata.component.'bom-ref'
        @($refs | Sort-Object -Unique).Count | Should Be $refs.Count
        @($bom.dependencies | ForEach-Object ref | Sort-Object -Unique).Count | Should Be @($bom.dependencies).Count
    }

    It 'adds the vendored files it carries with their upstream, commit and license, but not license texts' {
        $bom = Select-TestSbom
        $vendored = (Get-Component $bom 'webview.js')[0]
        $vendored.type | Should Be 'file'
        $vendored.version | Should Be 'abc123'
        $vendored.purl | Should Be 'pkg:github/digital-asset/daml@abc123#sdk/src/webview.js'
        $vendored.licenses[0].license.id | Should Be 'Apache-2.0'
        $vendored.evidence.occurrences[0].location | Should Be 'Assets/webview/webview.js'
        (Get-Component $bom 'LICENSE').Count | Should Be 0
        (Get-Component $bom 'grammar.xml').Count | Should Be 0
    }

    It 'takes a per-file license over the folder''s' {
        $files = @(New-VsixFiles) + (New-File 'Assets/grammar.xml' ('b' * 64))
        (Get-Component (Select-TestSbom $files) 'grammar.xml')[0].licenses[0].license.id | Should Be 'BSD-3-Clause'
    }

    It 'describes an artifact without packages, as the tool leaves it out when a project has none' {
        $tool = '{ "metadata": { "component": { "bom-ref": "pkg:nuget/Daml.Templates@0.3.0", "name": "Daml.Templates", "version": "0.3.0" } }, "dependencies": [ { "ref": "pkg:nuget/Daml.Templates@0.3.0" } ] }' | ConvertFrom-Json
        $files = @(New-File 'content/a/template.json')
        $assemblies = Get-PackageAssembly -PackagesFolder $TestDrive -Bom $tool
        $assemblies.Count | Should Be 0
        $bom = Select-ShippedSbom -Bom $tool -Files $files -PackageAssemblies $assemblies -Version '0.3.0' -Supplier 'Equilibrium' -License 'Apache-2.0'
        @($bom.components).Count | Should Be 0
        @($bom.dependencies).Count | Should Be 1
        @(Test-SbomCoverage -Bom $bom -Files $files).Count | Should Be 0
        @(Test-NoticeCoverage -Notice 'none' -Bom $bom -Files $files -Supplier 'Equilibrium').Count | Should Be 0
    }

    It 'leaves a vendored file of a folder without a recorded license unlicensed' {
        $pinned = @(New-Pinned) + [pscustomobject]@{ Folder = 'other'; Path = 'x.js'; Upstream = 'x.js'; Sha256 = ('d' * 64); Repository = 'https://example.org/other.git'; Commit = 'def' }
        $files = @(New-VsixFiles) + (New-File 'x.js' ('d' * 64))
        $bom = Select-ShippedSbom -Bom (New-ToolBom) -Files $files -PackageAssemblies $script:assemblies -Pinned $pinned `
            -Licenses $script:licenses -Version '0.3.0.7' -Supplier 'Equilibrium' -License 'Apache-2.0'
        $component = (Get-Component $bom 'x.js')[0]
        @($component.licenses).Count | Should Be 0
        @(Test-SbomCoverage -Bom $bom -Files $files) -join ';' | Should Match 'x\.js has no license'
    }
}

Describe 'SBOM coverage of an artifact' {
    It 'accepts an SBOM in which every bundled assembly is a component' {
        @(Test-SbomCoverage -Bom (Select-TestSbom) -Files (New-VsixFiles)).Count | Should Be 0
    }

    It 'reports an assembly that no package or project accounts for' {
        $files = @(New-VsixFiles) + (New-File 'Stray.dll')
        $problems = @(Test-SbomCoverage -Bom (Select-TestSbom $files) -Files $files)
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'Stray\.dll'
    }
}

Describe 'NOTICE against the SBOM' {
    BeforeEach {
        $script:notice = @'
EQ Daml Tools for Visual Studio

==============================================================================
Redistributed
==============================================================================

------------------------------------------------------------------------------
YamlDotNet 18.1.0 (YamlDotNet.dll)
  Licensed under the MIT License (text below).

------------------------------------------------------------------------------
Toolkit 17.0.551 (Community.VisualStudio.Toolkit.dll)
  Licensed under the Apache License, Version 2.0 (see LICENSE).

------------------------------------------------------------------------------
third_party/daml-extension: webview.js
  Licensed under the Apache License, Version 2.0.
'@
    }

    It 'accepts a NOTICE that names every third-party component with its license' {
        @(Test-NoticeCoverage -Notice $script:notice -Bom (Select-TestSbom) -Files (New-VsixFiles) -Supplier 'Equilibrium').Count | Should Be 0
    }

    It 'reports a component NOTICE leaves out, or names at another version' {
        $notice = $script:notice -replace 'YamlDotNet 18\.1\.0', 'YamlDotNet 16.0.0'
        $problems = @(Test-NoticeCoverage -Notice $notice -Bom (Select-TestSbom) -Files (New-VsixFiles) -Supplier 'Equilibrium')
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'YamlDotNet 18\.1\.0'
    }

    It 'reports a component whose NOTICE entry gives another license' {
        $notice = $script:notice -replace 'Version 2\.0 \(see LICENSE\)', 'the MIT License'
        $problems = @(Test-NoticeCoverage -Notice $notice -Bom (Select-TestSbom) -Files (New-VsixFiles) -Supplier 'Equilibrium')
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'Toolkit 17\.0\.551.*Apache'
    }

    It 'reports a bundled assembly NOTICE doesn''t name' {
        $notice = $script:notice -replace ' \(Community\.VisualStudio\.Toolkit\.dll\)', ''
        $problems = @(Test-NoticeCoverage -Notice $notice -Bom (Select-TestSbom) -Files (New-VsixFiles) -Supplier 'Equilibrium')
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'Community\.VisualStudio\.Toolkit\.dll'
    }

    It 'reports a license it has no NOTICE wording for' {
        $bom = Select-TestSbom
        (Get-Component $bom 'YamlDotNet')[0].licenses[0].license.id = 'WTFPL'
        $problems = @(Test-NoticeCoverage -Notice $script:notice -Bom $bom -Files (New-VsixFiles) -Supplier 'Equilibrium')
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'WTFPL'
    }
}

Describe 'Artifact contents and package assemblies' {
    BeforeEach { $script:work = Join-Path $TestDrive ([guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force -Path $script:work | Out-Null }

    It 'lists the files of a package with their hashes and decoded paths' {
        $zipPath = Join-Path $script:work 'test.vsix'
        $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry('ItemTemplates/CPS%20Projects/a.daml').Open())
            try { $writer.Write('module A') } finally { $writer.Dispose() }
            $zip.CreateEntry('empty/') | Out-Null
        }
        finally { $zip.Dispose() }
        $files = @(Get-ArtifactFile $zipPath)
        $files.Count | Should Be 1
        $files[0].Path | Should Be 'ItemTemplates/CPS Projects/a.daml'
        $files[0].Name | Should Be 'a.daml'
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $expected = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('module A')) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
        $files[0].Sha256 | Should Be $expected
    }

    It 'finds the runtime assemblies of each package in the package folder' {
        $lib = Join-Path $script:work 'yamldotnet\18.1.0\lib\netstandard2.0'
        New-Item -ItemType Directory -Force -Path $lib | Out-Null
        Set-Content -LiteralPath (Join-Path $lib 'YamlDotNet.dll') -Value 'x'
        Set-Content -LiteralPath (Join-Path $lib 'YamlDotNet.xml') -Value 'x'
        $analyzers = Join-Path $script:work 'yamldotnet\18.1.0\analyzers'
        New-Item -ItemType Directory -Force -Path $analyzers | Out-Null
        Set-Content -LiteralPath (Join-Path $analyzers 'Generator.dll') -Value 'x'
        $map = Get-PackageAssembly -PackagesFolder $script:work -Bom (New-ToolBom)
        @($map['pkg:nuget/YamlDotNet@18.1.0']) -join ',' | Should Be 'YamlDotNet.dll'
        @($map['pkg:nuget/Newtonsoft.Json@13.0.3']).Count | Should Be 0
    }
}
