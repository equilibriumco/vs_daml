#Requires -Version 7.2
<#
.SYNOPSIS
    Builds daml_ts.dll, the tree-sitter runtime and the Daml grammar in one library, for x64 and
    ARM64 with the CMake and MSVC of Visual Studio 2026, and runs its smoke test on the
    architecture of this machine.

.DESCRIPTION
    Generates the parser first when the grammar or its version changed since it was last generated
    (or it never was). CMake fetches the tree-sitter runtime pinned in
    grammar/tree-sitter-runtime.txt into each build directory. ARM64 is cross-compiled; its smoke
    test needs an ARM64 machine.

    -Package zips each architecture for a release: the DLL, its PDB and import library, the
    headers, and the licenses of the grammar and the runtime.

.EXAMPLE
    pwsh native/build.ps1                          both → build/native/<arch>/Release/daml_ts.dll
    pwsh native/build.ps1 -Architecture x64
    pwsh native/build.ps1 -Package                 also build/native/daml_ts-<version>-windows-<arch>.zip
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string[]] $Architecture = @('x64', 'ARM64'),
    [string] $Out = (Join-Path $PSScriptRoot '..\build\native'),
    [switch] $Package
)

$ErrorActionPreference = 'Stop'
$Out = [IO.Path]::GetFullPath($Out)
$grammar = Join-Path $PSScriptRoot '..\grammar\tree-sitter-daml'
$version = (Get-Content -Raw (Join-Path $grammar 'tree-sitter.json') | ConvertFrom-Json).metadata.version

$parser = Join-Path $grammar 'src\parser.c'
$sources = @(Get-ChildItem (Join-Path $grammar 'grammar.js'), (Join-Path $grammar 'grammar') -Recurse -File -Filter *.js) +
    (Get-Item (Join-Path $grammar 'tree-sitter.json'))
if (-not (Test-Path $parser) -or ($sources | Where-Object LastWriteTime -gt (Get-Item $parser).LastWriteTime)) {
    Push-Location $grammar
    try {
        npx tree-sitter generate
        if ($LASTEXITCODE -ne 0) { throw 'Generating the parser failed.' }
    }
    finally {
        Pop-Location
    }
}

# A stable Visual Studio 2026 with the C++ tools for every architecture asked for, else a preview.
$components = @('Microsoft.VisualStudio.Component.VC.Tools.x86.x64')
if ($Architecture -contains 'ARM64') { $components += 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$query = @('-latest', '-products', '*', '-version', '[18.0,19.0)', '-property', 'installationPath', '-requires') + $components
$vs = (& $vswhere @query) ?? (& $vswhere -prerelease @query)
if (-not $vs) { throw "No Visual Studio 2026 with the C++ tools for $($Architecture -join ' and ') found." }

$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path $cmake)) { $cmake = (Get-Command cmake -ErrorAction SilentlyContinue)?.Source }
if (-not $cmake) { throw 'No CMake: add "C++ CMake tools for Windows" to Visual Studio, or put cmake on PATH.' }
$ctest = Join-Path (Split-Path $cmake) 'ctest.exe'

$machine = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
foreach ($arch in $Architecture) {
    $dir = Join-Path $Out $arch
    & $cmake -S $PSScriptRoot -B $dir -G 'Visual Studio 18 2026' -A $arch "-DCMAKE_GENERATOR_INSTANCE=$vs"
    if ($LASTEXITCODE -ne 0) { throw "Configuring the $arch build failed." }
    & $cmake --build $dir --config Release --parallel
    if ($LASTEXITCODE -ne 0) { throw "The $arch build failed." }
    if ($arch -eq $machine) {
        & $ctest --test-dir $dir -C Release --output-on-failure --no-tests=error
        if ($LASTEXITCODE -ne 0) { throw "The $arch smoke test failed." }
    }
    else {
        Write-Warning "$arch built, not run: this machine is $machine."
    }
    "Built $(Join-Path $dir 'Release\daml_ts.dll')"

    if ($Package) {
        $stage = Join-Path $dir 'package'
        Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
        $headers = New-Item -ItemType Directory -Force (Join-Path $stage 'include\tree_sitter')
        Copy-Item (Join-Path $dir 'Release\daml_ts.*') $stage -Include *.dll, *.pdb, *.lib
        Copy-Item (Join-Path $dir 'tree-sitter\lib\include\tree_sitter\api.h'), (Join-Path $PSScriptRoot 'include\tree_sitter\tree-sitter-daml.h') $headers
        Copy-Item (Join-Path $grammar 'LICENSE') $stage
        Copy-Item (Join-Path $dir 'tree-sitter\LICENSE') (Join-Path $stage 'LICENSE.tree-sitter')
        $zip = Join-Path $Out "daml_ts-$version-windows-$($arch.ToLowerInvariant()).zip"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
        "Packaged $zip"
    }
}
