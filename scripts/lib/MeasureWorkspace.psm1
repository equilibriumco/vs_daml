#Requires -Version 7.2
<#
    A folder for measuring builds of this repository's packages as a user's solution has them:
    short (generated code sits deep below it), with the repository's SDK pin, and a nuget.config
    with a package folder of its own, so the projects restore the packages scripts/pack.ps1 left
    in artifacts/packages and never an older copy of the same version from the machine's cache.
    Everything else comes from that cache, so nothing needs the network that a build here didn't.
#>

function Get-ThisBuildPackages {
    <#
    .SYNOPSIS
        The packages of this build: their folder, version and prefix, the templates package, and the
        Daml.Runtime version the templates' app compiles against. Throws when pack.ps1 hasn't run.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $RepositoryRoot)

    $folder = Join-Path $RepositoryRoot 'artifacts\packages'
    $sdk = Get-ChildItem $folder -Filter '*.Sdk.*.nupkg' -ErrorAction SilentlyContinue | Select-Object -First 1
    $templates = Get-ChildItem $folder -Filter '*.Templates.*.nupkg' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $sdk -or -not $templates -or -not (Get-ChildItem $folder -Filter '*.Build.CSharp.*.nupkg' -ErrorAction SilentlyContinue)) {
        throw "No packages in $folder; run pwsh scripts/pack.ps1 first."
    }
    if ($sdk.Name -notmatch '^(?<prefix>.+)\.Sdk\.(?<version>\d.+)\.nupkg$') { throw "Can't read the version of $($sdk.Name)." }
    $app = Join-Path $RepositoryRoot 'src\Daml.Templates\content\daml-dotnet-app\DamlApp\DamlApp.csproj'
    $runtime = ([xml](Get-Content $app -Raw)).Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq 'Daml.Runtime' } | Select-Object -First 1
    [pscustomobject] @{
        Folder         = $folder
        Prefix         = $Matches.prefix
        Version        = $Matches.version
        Templates      = $templates.FullName
        RuntimeVersion = $runtime.Version
    }
}

function New-MeasureWorkspace {
    <#
    .SYNOPSIS
        Creates the folder and returns its path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [Parameter(Mandatory)] [string] $Prefix
    )

    $packages = Join-Path $RepositoryRoot 'artifacts\packages'
    $workspace = Join-Path ([IO.Path]::GetTempPath()) ($Prefix + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $workspace | Out-Null
    Copy-Item (Join-Path $RepositoryRoot 'global.json') $workspace
    Set-Content (Join-Path $workspace 'nuget.config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="packages" />
  </config>
  <packageSources>
    <clear />
    <add key="this-build" value="$packages" />
    <add key="machine-cache" value="$(Join-Path $env:USERPROFILE '.nuget\packages')" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="this-build">
      <package pattern="*.Sdk" />
      <package pattern="*.Build.CSharp" />
    </packageSource>
    <packageSource key="machine-cache">
      <package pattern="*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
    $workspace
}

function Get-MeasureHost {
    <#
    .SYNOPSIS
        The machine, as a report names it.
    #>
    [CmdletBinding()]
    param()

    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
    "$cpu, $([Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)) GB"
}

Export-ModuleMember -Function Get-ThisBuildPackages, New-MeasureWorkspace, Get-MeasureHost
