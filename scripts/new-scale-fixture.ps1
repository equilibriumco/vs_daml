#Requires -Version 7.2
<#
.SYNOPSIS
    Writes the scale fixture: a solution of Daml packages and C# projects on their bindings.

.DESCRIPTION
    The defaults are the sizes the test strategy measures at: 20 Daml packages of 50 modules of
    about 100 lines (1000 files, 100k lines), 25 templates a package (500 templates, each with a
    choice, for the binding index), and 10 C# projects, each on the bindings of two packages and
    using every template of them. The packages are independent, so they build side by side. The
    output is the same for the same parameters; the folder must not exist yet.

.EXAMPLE
    pwsh scripts/new-scale-fixture.ps1 -Path $env:TEMP\scale -SdkPackageVersion 0.2.0-preview.0 -RuntimeVersion 0.6.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string] $SdkPackageVersion,
    [Parameter(Mandatory)] [string] $RuntimeVersion,
    [string] $PackagePrefix = 'Daml',
    [string] $DamlSdk = '3.6.0',
    [ValidateRange(1, 100)] [int] $Packages = 20,
    [ValidateRange(1, 500)] [int] $ModulesPerPackage = 50,
    [ValidateRange(20, 1000)] [int] $LinesPerModule = 100,
    [ValidateRange(1, 500)] [int] $TemplatesPerPackage = 25,
    [ValidateRange(1, 50)] [int] $CSharpProjects = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($TemplatesPerPackage -gt $ModulesPerPackage) { throw '-TemplatesPerPackage can be at most -ModulesPerPackage: one template a module.' }
if (Test-Path $Path) { throw "$Path exists already." }

$utf8 = [Text.UTF8Encoding]::new($false)
function Write-File([string] $Relative, [string] $Text) {
    $file = Join-Path $Path $Relative
    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
    [IO.File]::WriteAllText($file, $Text.Replace("`r`n", "`n"), $utf8)
}

function Package([int] $p) { 'Pkg{0:00}' -f $p }
function Module([int] $m) { 'M{0:00}' -f $m }

# One module: a record, a template on it when the module has one, and functions up to the length.
function Module-Text([int] $p, [int] $m, [bool] $template) {
    $name = "$(Package $p).$(Module $m)"
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("module $name where")
    $lines.Add('')
    $lines.Add("-- | What asset $m of package $p holds.")
    $lines.Add("data Info$m = Info$m with")
    $lines.Add('    code : Text')
    $lines.Add('    amount : Int')
    $lines.Add('    note : Optional Text')
    $lines.Add('  deriving (Eq, Show)')
    $lines.Add('')
    if ($template) {
        $lines.Add("template Asset$m")
        $lines.Add('  with')
        $lines.Add('    issuer : Party')
        $lines.Add('    owner : Party')
        $lines.Add("    info : Info$m")
        $lines.Add('  where')
        $lines.Add('    signatory issuer')
        $lines.Add('    observer owner')
        $lines.Add('')
        $lines.Add("    choice Give$m : ContractId Asset$m")
        $lines.Add('      with')
        $lines.Add('        newOwner : Party')
        $lines.Add('      controller owner')
        $lines.Add('      do create this with owner = newOwner')
        $lines.Add('')
    }
    $lines.Add("total$m : [Info$m] -> Int")
    $lines.Add("total$m infos = sum (map (\i -> i.amount) infos)")
    $lines.Add('')
    $k = 0
    while ($lines.Count + 3 -le $LinesPerModule) {
        $k++
        $lines.Add("step${m}x$k : Int -> Int")
        $lines.Add("step${m}x$k x = x * $k + $m")
        $lines.Add('')
    }
    ($lines -join "`n") + "`n"
}

$daml = @()
for ($p = 1; $p -le $Packages; $p++) {
    $package = Package $p
    Write-File "$package\daml.yaml" @"
sdk-version: $DamlSdk
name: scale-$($package.ToLowerInvariant())
version: 1.0.0
source: daml
dependencies:
  - daml-prim
  - daml-stdlib

"@
    Write-File "$package\$package.msbuildproj" "<Project Sdk=`"$PackagePrefix.Sdk/$SdkPackageVersion`" />`n"
    for ($m = 1; $m -le $ModulesPerPackage; $m++) {
        Write-File "$package\daml\$package\$(Module $m).daml" (Module-Text $p $m ($m -le $TemplatesPerPackage))
    }
    $daml += $package
}

$csharp = @()
for ($c = 1; $c -le $CSharpProjects; $c++) {
    $app = 'App{0:00}' -f $c
    # Every package gets a project; with more projects than packages they wrap around.
    $references = @((((2 * $c - 2) % $Packages) + 1), (((2 * $c - 1) % $Packages) + 1)) | Select-Object -Unique
    $items = ($references | ForEach-Object { "    <DamlReference Include=`"..\$(Package $_)\$(Package $_).msbuildproj`" Namespace=`"Scale.$(Package $_)`" />" }) -join "`n"
    Write-File "$app\$app.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="$PackagePrefix.Build.CSharp" Version="$SdkPackageVersion" PrivateAssets="all" />
    <PackageReference Include="Daml.Runtime" Version="$RuntimeVersion" />
    <PackageReference Include="Daml.Ledger.Abstractions" Version="$RuntimeVersion" />
$items
  </ItemGroup>

</Project>

"@
    $uses = foreach ($r in $references) {
        for ($m = 1; $m -le $TemplatesPerPackage; $m++) {
            "Console.WriteLine(typeof(Scale.$(Package $r).$(Package $r).$(Module $m).Asset$m).Name);"
        }
    }
    Write-File "$app\Program.cs" ("// Every template of the packages this project generates C# from.`n" + ($uses -join "`n") + "`n")
    $csharp += $app
}

$projects = @($daml | ForEach-Object { "  <Project Path=`"$_/$_.msbuildproj`" Type=`"13b669be-bb05-4ddf-9536-439f39a36129`" />" }) +
    @($csharp | ForEach-Object { "  <Project Path=`"$_/$_.csproj`" />" })
Write-File 'Scale.slnx' ("<Solution>`n" + ($projects -join "`n") + "`n</Solution>`n")
Write-File 'multi-package.yaml' ("packages:`n" + (($daml | ForEach-Object { "  - ./$_" }) -join "`n") + "`n")

$files = $Packages * $ModulesPerPackage
$lines = (Get-ChildItem $Path -Recurse -Filter *.daml | ForEach-Object { @(Get-Content $_.FullName).Count } | Measure-Object -Sum).Sum
Write-Host "Scale fixture in ${Path}: $Packages Daml packages, $files files, $lines lines, $($Packages * $TemplatesPerPackage) templates, $CSharpProjects C# projects."
