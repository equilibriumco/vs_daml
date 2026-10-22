# Validates package contents and reads assembly identities in a private temporary folder.
Set-StrictMode -Version Latest

# The Identity Id every release so far has carried. Never change it once a version is out.
$ShippedId = 'Equilibrium.DamlTools'

function Read-ZipText($Zip, [string] $Name) {
    $entry = $Zip.GetEntry($Name)
    if (-not $entry) { return $null }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}

# Assembly name -> version of every codeBase entry in a .pkgdef.
function Get-CodeBases([string] $Pkgdef) {
    $result = @{}
    foreach ($section in ($Pkgdef -split '(?m)^\[') | Where-Object { $_ -match '^\$RootKey\$\\RuntimeConfiguration\\dependentAssembly\\codeBase\\' }) {
        if ($section -match '"name"="([^"]+)"' ) {
            $name = $Matches[1]
            $result[$name] = if ($section -match '"version"="([^"]+)"') { $Matches[1] } else { '' }
        }
    }
    $result
}

function Test-Vsix([string] $Path, [string] $ExpectedVersion) {
    $problems = [System.Collections.Generic.List[string]]::new()
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("daml-vsix-check-" + [guid]::NewGuid().ToString('N'))
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
        $manifestText = Read-ZipText $zip 'extension.vsixmanifest'
        if (-not $manifestText) { return @('no extension.vsixmanifest') }
        $manifest = [xml]$manifestText
        $pkgdef = Read-ZipText $zip 'Daml.VisualStudio.pkgdef'
        if ($null -eq $pkgdef) { $pkgdef = '' }
        $codeBases = Get-CodeBases $pkgdef

        # A third-party assembly found only by probing can lose to another extension's copy of a
        # different version; each one needs an exact codeBase entry matching the file we ship.
        # Only the ones we decided to ship may be there at all: YamlDotNet and the TimeProvider
        # polyfill with our own entries, the Community Toolkit with the entry its targets write.
        $allowed = @('YamlDotNet', 'Microsoft.Bcl.TimeProvider', 'Community.VisualStudio.Toolkit')
        New-Item -ItemType Directory -Force -Path $scratch | Out-Null
        foreach ($dll in $zip.Entries | Where-Object { $_.Name -match '\.dll$' -and $_.Name -notmatch '^Daml\.' }) {
            $file = Join-Path $scratch $dll.Name
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($dll, $file, $true)
            $identity = [System.Reflection.AssemblyName]::GetAssemblyName($file)
            if ($identity.Name -notin $allowed) { $problems.Add("$($dll.FullName) isn't one of the assemblies this extension ships ($($allowed -join ', '))") }
            if (-not $codeBases.ContainsKey($identity.Name)) { $problems.Add("$($dll.FullName) has no codeBase entry in the pkgdef") }
            elseif ($codeBases[$identity.Name] -ne $identity.Version.ToString()) { $problems.Add("$($dll.FullName) is $($identity.Version) but its codeBase entry says $($codeBases[$identity.Name])") }
        }
        # A Release VSIX carries neither symbols nor the compiler's documentation files.
        foreach ($entry in $entries | Where-Object { $_ -match '\.(xml|pdb)$' -and $_ -ne '[Content_Types].xml' }) {
            $problems.Add("$entry doesn't belong in the VSIX")
        }
    }
    finally {
        $zip.Dispose()
        if (Test-Path $scratch) {
            Get-ChildItem -LiteralPath $scratch -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
            Remove-Item -LiteralPath $scratch
        }
    }

    $identity = $manifest.SelectSingleNode("//*[local-name()='Identity']")
    # Visual Studio updates an installed extension only from a VSIX with the same Id; a new Id
    # would install beside the old one, which keeps the users' settings.
    $id = if ($identity) { $identity.GetAttribute('Id') } else { '(none)' }
    if ($id -ne $ShippedId) { $problems.Add("the manifest has Id $id; releases so far shipped as $ShippedId") }
    # The manifest is written from the project's version, and a build that kept an older one
    # would ship it under the old number.
    if ($ExpectedVersion) {
        $version = if ($identity) { $identity.GetAttribute('Version') } else { '(none)' }
        if ($version -ne $ExpectedVersion) { $problems.Add("the manifest says version $version, the build $ExpectedVersion") }
    }

    $targets = @($manifest.PackageManifest.Installation.InstallationTarget)
    if (-not $targets) { $problems.Add('no InstallationTarget') }
    foreach ($target in $targets) {
        if ($target.Version -ne '[18.0,)') { $problems.Add("InstallationTarget $($target.Id) has range $($target.Version), expected [18.0,)") }
        $arch = @($target.ProductArchitecture)
        if ($arch.Count -ne 1 -or $arch[0] -ne 'amd64') { $problems.Add("InstallationTarget $($target.Id) architectures '$($arch -join ', ')', expected amd64 only") }
    }

    # The MIT notices of the libraries we bundle have to travel with them.
    foreach ($own in 'Daml.VisualStudio.dll', 'Daml.VisualStudio.Core.dll', 'Daml.VisualStudio.LanguageServer.dll', 'Daml.Build.Core.dll', 'Daml.VisualStudio.pkgdef', 'LICENSE', 'NOTICE') {
        if ($own -notin $entries) { $problems.Add("missing $own") }
    }
    # Visual Studio loads its own copies of these; ours would shadow or conflict with them.
    $vsProvided = '^(Microsoft\.VisualStudio\.|Microsoft\.CodeAnalysis|Microsoft\.ServiceHub\.|StreamJsonRpc|Newtonsoft\.Json|System\.Text\.Json|Microsoft\.Web\.WebView2|WebView2Loader|MessagePack|Nerdbank\.)'
    foreach ($entry in $entries) {
        $name = Split-Path $entry -Leaf
        if ($name -match '\.dll$' -and $name -match $vsProvided) { $problems.Add("ships VS-provided assembly $entry") }
    }
    $problems
}


Add-Type -AssemblyName System.IO.Compression.FileSystem
Export-ModuleMember -Function Test-Vsix
