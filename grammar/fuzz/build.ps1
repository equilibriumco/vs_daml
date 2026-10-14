#Requires -Version 7.2
<#
.SYNOPSIS
    Builds the Daml grammar's fuzzer (fuzzer.c) on Windows with MSVC's AddressSanitizer and
    libFuzzer, to replay what the Linux run found (that one adds the undefined-behaviour sanitizer,
    which MSVC doesn't have).

.DESCRIPTION
    Compiles the tree-sitter runtime at the commit of the pinned CLI, the generated parser
    (run `npx tree-sitter generate` in grammar/tree-sitter-daml first) and the scanner, and puts
    the AddressSanitizer runtime next to the executable so it runs outside the build environment.

.EXAMPLE
    pwsh grammar/fuzz/build.ps1
    grammar/fuzz/out/daml-fuzzer.exe crash-1234…        replays an input the fuzzer saved
#>
[CmdletBinding()]
param([string] $Out = (Join-Path $PSScriptRoot 'out'))

$ErrorActionPreference = 'Stop'
$grammar = Join-Path $PSScriptRoot '..\tree-sitter-daml'
New-Item -ItemType Directory -Force $Out | Out-Null

# tree-sitter v0.27.1, the runtime of the pinned CLI.
$runtimeCommit = '10eb5ac119eec9cb4267a8e035066473983f5f3a'
$runtime = Join-Path $Out 'tree-sitter'
if ((git -C $runtime rev-parse HEAD 2>$null) -ne $runtimeCommit) {
    Remove-Item -Recurse -Force $runtime -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $runtime | Out-Null
    git -C $runtime init -q
    git -C $runtime remote add origin https://github.com/tree-sitter/tree-sitter
    git -C $runtime sparse-checkout set --no-cone /lib/src/ /lib/include/
    git -C $runtime fetch -q --depth 1 origin $runtimeCommit
    git -C $runtime checkout -q FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Fetching the tree-sitter runtime failed.' }
}
if (-not (Test-Path (Join-Path $grammar 'src\parser.c'))) {
    throw 'No generated parser: run npx tree-sitter generate in grammar/tree-sitter-daml.'
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio with the C++ tools found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

# The scanner, the runtime and the harness get coverage; the parser's tables only the sanitizer.
$flags = "/nologo /c /std:c11 /O1 /Zi /Fd`"$Out\compile.pdb`" /fsanitize=address"
$commands = @(
    "cl $flags /fsanitize=fuzzer /I `"$runtime\lib\include`" /I `"$runtime\lib\src`" `"$runtime\lib\src\lib.c`" /Fo`"$Out\runtime.obj`""
    "cl $flags /I `"$grammar\src`" `"$grammar\src\parser.c`" /Fo`"$Out\parser.obj`""
    "cl $flags /fsanitize=fuzzer /I `"$grammar\src`" `"$grammar\src\scanner.c`" /Fo`"$Out\scanner.obj`""
    "cl $flags /fsanitize=fuzzer /I `"$runtime\lib\include`" `"$PSScriptRoot\fuzzer.c`" /Fo`"$Out\fuzzer.obj`""
    "cl /nologo /Zi /fsanitize=address /fsanitize=fuzzer `"$Out\runtime.obj`" `"$Out\parser.obj`" `"$Out\scanner.obj`" `"$Out\fuzzer.obj`" /Fe`"$Out\daml-fuzzer.exe`" /link /INCREMENTAL:NO"
    "copy /y `"!VCToolsInstallDir!bin\Hostx64\x64\clang_rt.asan_dynamic-x86_64.dll`" `"$Out`" >nul"
)
# Delayed expansion (/v:on, !…!): the paths vcvars sets exist only once it has run.
cmd /v:on /c "call `"$vcvars`" >nul && $($commands -join ' && ')"
if ($LASTEXITCODE -ne 0) { throw 'Building the fuzzer failed.' }
"Built $Out\daml-fuzzer.exe"
