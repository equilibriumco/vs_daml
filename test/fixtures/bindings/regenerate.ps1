#Requires -Version 7.2
<#
.SYNOPSIS
    Rewrites the checked-in generator output under test/fixtures/bindings with what the pinned
    generator writes now.

.DESCRIPTION
    Builds the solution, then runs BindingFixtureTests (real dpm, JDK and generator) with
    DAML_UPDATE_BINDING_FIXTURES=1, which writes the output instead of comparing it. Review the
    diff afterwards: it is what the binding index has to keep up with.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
& (Join-Path $root 'scripts\build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$env:DAML_UPDATE_BINDING_FIXTURES = '1'
try {
    dotnet test (Join-Path $root 'test\Daml.Build.Tests\Daml.Build.Tests.csproj') --no-build -f net10.0 `
        --filter 'FullyQualifiedName~BindingFixtureTests' --nologo
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:\DAML_UPDATE_BINDING_FIXTURES -ErrorAction SilentlyContinue
}
