Set-StrictMode -Version Latest

function Get-RepoRoot {
    (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

function Get-VsWherePath {
    $path = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $path)) {
        throw "vswhere.exe not found at '$path'. Install Visual Studio 2026 or Build Tools 2026."
    }
    $path
}

# Every VS / Build Tools instance that has MSBuild, newest first.
function Get-VsInstances {
    $json = & (Get-VsWherePath) -all -prerelease -products '*' -requires Microsoft.Component.MSBuild -format json -utf8
    if ($LASTEXITCODE -ne 0) { throw "vswhere failed with exit code $LASTEXITCODE." }

    $(foreach ($i in ($json | ConvertFrom-Json)) {
        $msbuild = Join-Path $i.installationPath 'MSBuild\Current\Bin\MSBuild.exe'
        if (-not (Test-Path $msbuild)) { continue }

        # FileVersion is "18.11.0.42713" style; the product version carries a commit suffix.
        $fileVersion = (Get-Item $msbuild).VersionInfo.FileVersion
        # Without this resolver MSBuild cannot load Microsoft.NET.Sdk projects. Build Tools only
        # gets it with the ".NET desktop build tools" workload.
        $resolver = Join-Path $i.installationPath 'MSBuild\Current\Bin\SdkResolvers\Microsoft.DotNet.MSBuildSdkResolver'
        [pscustomobject]@{
            InstanceId     = $i.instanceId
            DisplayName    = $i.displayName
            Channel        = $i.channelId
            Version        = [version]$i.installationVersion
            IsPrerelease   = [bool]$i.isPrerelease
            Path           = $i.installationPath
            MSBuildPath    = $msbuild
            MSBuildVersion = [version]($fileVersion -replace '^(\d+\.\d+\.\d+).*', '$1')
            HasDotNetSdkResolver = Test-Path $resolver
        }
    }) | Sort-Object Version -Descending
}

# The SDK pinned in global.json records the oldest MSBuild it works with.
function Get-SdkMinimumMSBuildVersion {
    $root = Get-RepoRoot
    Push-Location $root
    try {
        $sdkVersion = (& dotnet --version).Trim()
        if ($LASTEXITCODE -ne 0) { throw "The SDK pinned in global.json is not installed (dotnet --version failed)." }
        $sdkLine = & dotnet --list-sdks | Where-Object { $_ -like "$sdkVersion *" } | Select-Object -First 1
    }
    finally {
        Pop-Location
    }

    if (-not $sdkLine) { throw "Could not locate .NET SDK $sdkVersion." }
    $sdkDir = Join-Path ($sdkLine -replace '^\S+ \[(.+)\]$', '$1') $sdkVersion
    $file = Join-Path $sdkDir 'minimumMSBuildVersion'
    if (-not (Test-Path $file)) { throw "Missing '$file'; cannot validate MSBuild compatibility." }

    [pscustomobject]@{
        SdkVersion            = $sdkVersion
        MinimumMSBuildVersion = [version](Get-Content $file -Raw).Trim()
    }
}

function Get-BuildInstanceProblem {
    param($Instance, [version] $MinimumMSBuild)

    if ($Instance.MSBuildVersion.Major -ne 18) { return "MSBuild $($Instance.MSBuildVersion) is not 18.x" }
    if ($Instance.MSBuildVersion -lt $MinimumMSBuild) { return "MSBuild $($Instance.MSBuildVersion) < $MinimumMSBuild" }
    if (-not $Instance.HasDotNetSdkResolver) { return 'no .NET SDK resolver (add the ".NET desktop build tools" workload)' }
    $null
}

# Build instance: VS 2026 / Build Tools 2026 whose MSBuild 18.x satisfies the
# SDK in global.json. A stable release wins; a preview is used only when nothing stable qualifies.
function Select-BuildVsInstance {
    param([string] $InstallationPath)

    $sdk = Get-SdkMinimumMSBuildVersion
    $all = @(Get-VsInstances)
    if ($InstallationPath) {
        $all = @($all | Where-Object { $_.Path -eq $InstallationPath.TrimEnd('\') })
        if (-not $all) { throw "No VS instance with MSBuild at '$InstallationPath'." }
    }

    $usable = @($all | Where-Object { -not (Get-BuildInstanceProblem $_ $sdk.MinimumMSBuildVersion) })
    if (-not $usable) {
        $report = ($all | ForEach-Object {
                "  $($_.DisplayName) $($_.Version): $(Get-BuildInstanceProblem $_ $sdk.MinimumMSBuildVersion)"
            }) -join "`n"
        if (-not $report) { $report = '  (no instance with MSBuild found)' }
        throw "No usable build instance for .NET SDK $($sdk.SdkVersion) (needs MSBuild >= $($sdk.MinimumMSBuildVersion)):`n$report"
    }

    $chosen = @($usable | Where-Object { -not $_.IsPrerelease }) + @($usable | Where-Object { $_.IsPrerelease }) |
        Select-Object -First 1
    if ($chosen.IsPrerelease) {
        Write-Warning "No stable VS 2026 / Build Tools 2026 qualifies; building with prerelease $($chosen.DisplayName) $($chosen.Version)."
    }
    $chosen | Add-Member -NotePropertyName SdkVersion -NotePropertyValue $sdk.SdkVersion -PassThru
}

# The IDE the extension gets installed into and launched from: VS 2026 (18.x) or newer, the
# only host we support. Build Tools has no devenv, so it never qualifies. Default: newest
# stable IDE, else newest preview.
function Select-TestVsInstance {
    param(
        [string] $InstallationPath
    )

    $ides = @(Get-VsInstances | Where-Object {
            $_.Version.Major -ge 18 -and (Test-Path (Join-Path $_.Path 'Common7\IDE\devenv.exe'))
        })
    if ($InstallationPath) {
        $ides = @($ides | Where-Object { $_.Path -eq $InstallationPath.TrimEnd('\') })
    }

    if (-not $ides) {
        $want = if ($InstallationPath) { "Visual Studio 2026 IDE at '$InstallationPath'" } else { 'Visual Studio 2026 IDE' }
        throw "No $want is installed."
    }

    $chosen = @($ides | Where-Object { -not $_.IsPrerelease }) + @($ides | Where-Object { $_.IsPrerelease }) |
        Select-Object -First 1
    $chosen | Add-Member -NotePropertyName DevenvPath -NotePropertyValue (Join-Path $chosen.Path 'Common7\IDE\devenv.exe') -PassThru |
        Add-Member -NotePropertyName VsixInstallerPath -NotePropertyValue (Join-Path $chosen.Path 'Common7\IDE\VSIXInstaller.exe') -PassThru
}

# Where dpm keeps its home. DPM_HOME wins; the archive bootstrap installs into %APPDATA%\dpm,
# while Digital Asset's docs also mention %APPDATA%\.dpm, so both are checked.
function Get-DpmHomeCandidates {
    if ($env:DPM_HOME) { return @($env:DPM_HOME) }
    if (-not $env:APPDATA) { return @() }
    @((Join-Path $env:APPDATA '.dpm'), (Join-Path $env:APPDATA 'dpm'))
}

# External tools the tests may need. A fresh dpm install doesn't touch PATH in the current
# session, so its home directories are checked too.
function Find-Tool {
    param([Parameter(Mandatory)][ValidateSet('dpm', 'java', 'node', 'tree-sitter')] [string] $Name)

    $onPath = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    if ($Name -eq 'dpm') {
        foreach ($dpmHome in Get-DpmHomeCandidates) {
            foreach ($exe in 'dpm.exe', 'dpm.cmd') {
                $candidate = Join-Path $dpmHome "bin\$exe"
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }
    if ($Name -eq 'java' -and $env:JAVA_HOME) {
        $candidate = Join-Path $env:JAVA_HOME 'bin\java.exe'
        if (Test-Path $candidate) { return $candidate }
    }
    $null
}

Export-ModuleMember -Function Get-RepoRoot, Get-VsInstances, Get-SdkMinimumMSBuildVersion, Select-BuildVsInstance, Select-TestVsInstance, Find-Tool, Get-DpmHomeCandidates
