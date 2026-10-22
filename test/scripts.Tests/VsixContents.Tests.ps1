Add-Type -AssemblyName System.IO.Compression
Import-Module (Join-Path $PSScriptRoot '..\..\scripts\lib\VsixContents.psm1') -Force

function New-TestVsix([string] $Path, [string[]] $ExtraEntries = @(), [string] $Version = '0.2.0.0', [string[]] $Without = @()) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $manifest = $zip.CreateEntry('extension.vsixmanifest')
        $writer = [IO.StreamWriter]::new($manifest.Open())
        try { $writer.Write('<PackageManifest><Metadata><Identity Id="Daml.VisualStudio" Version="' + $Version + '" /></Metadata><Installation><InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[18.0,)"><ProductArchitecture>amd64</ProductArchitecture></InstallationTarget></Installation></PackageManifest>') }
        finally { $writer.Dispose() }
        $required = @('[Content_Types].xml', 'Daml.VisualStudio.dll', 'Daml.VisualStudio.Core.dll', 'Daml.VisualStudio.LanguageServer.dll', 'Daml.Build.Core.dll', 'Daml.VisualStudio.pkgdef', 'LICENSE', 'NOTICE')
        foreach ($name in @($required | Where-Object { $_ -notin $Without }) + $ExtraEntries) {
            $zip.CreateEntry($name) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}

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

    It 'rejects a VSIX without the license and the third-party notices' {
        New-TestVsix $script:vsix -Without 'LICENSE', 'NOTICE'
        $problems = @(Test-Vsix $script:vsix)
        $problems.Count | Should Be 2
        ($problems -join ';') | Should Match 'missing LICENSE'
        ($problems -join ';') | Should Match 'missing NOTICE'
    }

    It 'rejects referenced-project symbols and assembly documentation' {
        New-TestVsix $script:vsix @('Daml.VisualStudio.Core.pdb', 'Daml.VisualStudio.Core.xml')
        $problems = @(Test-Vsix $script:vsix)
        $problems.Count | Should Be 2
        ($problems -join ';') | Should Match 'Core.pdb'
        ($problems -join ';') | Should Match 'Core.xml'
    }
}
