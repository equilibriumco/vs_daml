Add-Type -AssemblyName System.IO.Compression
Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\VsixContents.psm1') -Force

# Files maps an entry name to a file whose bytes go in; Pkgdef is the pkgdef's text.
function New-TestVsix([string] $Path, [string[]] $ExtraEntries = @(), [string] $Version = '0.2.0.0', [string[]] $Without = @(), [string] $Id = 'Equilibrium.Daml.VisualStudio',
    [hashtable] $Files = @{}, [string] $Pkgdef = '', [string] $Range = '[17.14,)', [string[]] $Architectures = @('amd64'),
    [string] $EditorRange = '[18.0,)') {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $arch = ($Architectures | ForEach-Object { "<ProductArchitecture>$_</ProductArchitecture>" }) -join ''
        $manifest = $zip.CreateEntry('extension.vsixmanifest')
        $writer = [IO.StreamWriter]::new($manifest.Open())
        $prerequisites = if ($EditorRange) { '<Prerequisites><Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" Version="' + $EditorRange + '" /></Prerequisites>' } else { '' }
        try { $writer.Write('<PackageManifest><Metadata><Identity Id="' + $Id + '" Version="' + $Version + '" /></Metadata><Installation><InstallationTarget Id="Microsoft.VisualStudio.Community" Version="' + $Range + '">' + $arch + '</InstallationTarget></Installation>' + $prerequisites + '</PackageManifest>') }
        finally { $writer.Dispose() }
        $required = @('[Content_Types].xml', 'Equilibrium.Daml.VisualStudio.dll', 'Equilibrium.Daml.VisualStudio.Core.dll', 'Equilibrium.Daml.VisualStudio.LanguageServer.dll', 'Equilibrium.Daml.Build.Core.dll', 'LICENSE', 'NOTICE')
        foreach ($name in @($required | Where-Object { $_ -notin $Without }) + $ExtraEntries) {
            $zip.CreateEntry($name) | Out-Null
        }
        if ('Equilibrium.Daml.VisualStudio.pkgdef' -notin $Without) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry('Equilibrium.Daml.VisualStudio.pkgdef').Open())
            try { $writer.Write($Pkgdef) } finally { $writer.Dispose() }
        }
        foreach ($name in $Files.Keys) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $Files[$name], $name) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}

# A codeBase entry as the VSSDK writes it into the pkgdef.
function CodeBase([string] $Name, [string] $Version) {
    "[`$RootKey`$\RuntimeConfiguration\dependentAssembly\codeBase\{$([guid]::NewGuid())}]`n`"name`"=`"$Name`"`n`"version`"=`"$Version`"`n"
}

# Assemblies every PowerShell 7 ships, so the tests need nothing from the build: one stands for a
# third-party assembly of ours, the other is one Visual Studio provides itself.
$script:thirdParty = [System.Management.Automation.PSObject].Assembly
$script:vsProvided = [System.Text.Json.JsonSerializer].Assembly

Describe 'Release VSIX contents' {
    BeforeEach { $script:vsix = Join-Path ([IO.Path]::GetTempPath()) (([guid]::NewGuid().ToString('N')) + '.vsix') }
    AfterEach { if (Test-Path -LiteralPath $script:vsix) { Remove-Item -LiteralPath $script:vsix } }

    It 'accepts the required OPC content types XML' {
        New-TestVsix $script:vsix
        @(Test-Vsix $script:vsix).Count | Should Be 0
    }

    It 'rejects a manifest that kept another version than the build has' {
        New-TestVsix $script:vsix -Version '0.1.0.0'
        $problems = @(Test-Vsix $script:vsix -ExpectedVersion '0.2.0.0')
        $problems.Count | Should Be 1
        $problems[0] | Should Match '0.1.0.0.*0.2.0.0'
        @(Test-Vsix $script:vsix -ExpectedVersion '0.1.0.0').Count | Should Be 0
    }

    # Visual Studio only updates an installed extension from a VSIX with the same Id; another Id
    # installs a second extension next to the first and leaves its settings behind.
    It 'rejects a VSIX whose identity is not the one already shipped' {
        New-TestVsix $script:vsix -Id 'Daml.VisualStudio.e536ad39-5d35-4aba-a4ae-7bcc5b3fae1e'
        $problems = @(Test-Vsix $script:vsix)
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'Id Daml\.VisualStudio.*Equilibrium.Daml.VisualStudio'
    }

    It 'rejects a VSIX without the license and the third-party notices' {
        New-TestVsix $script:vsix -Without 'LICENSE', 'NOTICE'
        $problems = @(Test-Vsix $script:vsix)
        $problems.Count | Should Be 2
        ($problems -join ';') | Should Match 'missing LICENSE'
        ($problems -join ';') | Should Match 'missing NOTICE'
    }

    It 'rejects referenced-project symbols and assembly documentation' {
        New-TestVsix $script:vsix @('Equilibrium.Daml.VisualStudio.Core.pdb', 'Equilibrium.Daml.VisualStudio.Core.xml')
        $problems = @(Test-Vsix $script:vsix)
        $problems.Count | Should Be 2
        ($problems -join ';') | Should Match 'Core.pdb'
        ($problems -join ';') | Should Match 'Core.xml'
    }

    It 'rejects a third-party assembly the extension has not decided to ship' {
        New-TestVsix $script:vsix -Files @{ 'System.Management.Automation.dll' = $script:thirdParty.Location } -Pkgdef (CodeBase 'System.Management.Automation' $script:thirdParty.GetName().Version)
        $problems = @(Test-Vsix $script:vsix)
        $problems.Count | Should Be 1
        $problems[0] | Should Match "isn't one of the assemblies this extension ships"
    }

    # Found only by probing, it can lose to another extension's copy of another version.
    It 'needs a codeBase entry with the exact version of each shipped assembly' {
        $allowed = @('System.Management.Automation')
        $file = @{ 'System.Management.Automation.dll' = $script:thirdParty.Location }
        $version = $script:thirdParty.GetName().Version.ToString()

        New-TestVsix $script:vsix -Files $file
        (@(Test-Vsix $script:vsix -Allowed $allowed) -join ';') | Should Match 'no codeBase entry'
        Remove-Item -LiteralPath $script:vsix

        New-TestVsix $script:vsix -Files $file -Pkgdef (CodeBase 'System.Management.Automation' '1.0.0.0')
        (@(Test-Vsix $script:vsix -Allowed $allowed) -join ';') | Should Match "is $([regex]::Escape($version)) but its codeBase entry says 1\.0\.0\.0"
        Remove-Item -LiteralPath $script:vsix

        New-TestVsix $script:vsix -Files $file -Pkgdef (CodeBase 'System.Management.Automation' $version)
        @(Test-Vsix $script:vsix -Allowed $allowed).Count | Should Be 0
    }

    It 'rejects an assembly Visual Studio provides itself' {
        New-TestVsix $script:vsix -Files @{ 'System.Text.Json.dll' = $script:vsProvided.Location } -Pkgdef (CodeBase 'System.Text.Json' $script:vsProvided.GetName().Version)
        $problems = @(Test-Vsix $script:vsix -Allowed @('System.Text.Json'))
        $problems.Count | Should Be 1
        $problems[0] | Should Match 'ships VS-provided assembly System.Text.Json.dll'
    }

    It 'rejects the experimental API 18.0 range refused by Marketplace' {
        New-TestVsix $script:vsix -Range '[18.0,)'
        $problems = @(Test-Vsix $script:vsix) -join ';'
        $problems | Should Match 'range \[18\.0,\), expected stable API \[17\.14,\)'
    }

    It 'requires the stable API baseline used by the extension' {
        New-TestVsix $script:vsix -Range '[17.0,)'
        $problems = @(Test-Vsix $script:vsix) -join ';'
        $problems | Should Match 'range \[17\.0,\), expected stable API \[17\.14,\)'
    }

    It 'installs only on x64' {
        New-TestVsix $script:vsix -Architectures 'amd64', 'arm64'
        $problems = @(Test-Vsix $script:vsix) -join ';'
        $problems | Should Match "architectures 'amd64, arm64', expected amd64 only"
    }

    It 'keeps the Visual Studio 2026 product requirement in CoreEditor prerequisites' {
        New-TestVsix $script:vsix -EditorRange '[17.14,)'
        $problems = @(Test-Vsix $script:vsix) -join ';'
        $problems | Should Match 'CoreEditor.*expected \[18\.0,\)'
    }

    It 'rejects a missing CoreEditor prerequisite' {
        New-TestVsix $script:vsix -EditorRange ''
        $problems = @(Test-Vsix $script:vsix) -join ';'
        $problems | Should Match 'CoreEditor.*expected \[18\.0,\)'
    }
}
