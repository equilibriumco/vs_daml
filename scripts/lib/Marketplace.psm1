# Checks the Visual Studio Marketplace listing (publishManifest.json, its overview page and the
# extension's description) against the trademark rules in TRADEMARK.md before anything is published.
Set-StrictMode -Version Latest

$script:longNotice = 'EQ Daml Tools for Visual Studio is an independent project by Equilibrium and is not affiliated with, sponsored by, or endorsed by Digital Asset. Daml and Canton are trademarks of Digital Asset (Switzerland) GmbH. Visual Studio is a trademark of Microsoft Corporation.'
$script:shortNotice = 'Independent Daml tooling by Equilibrium — not affiliated with or endorsed by Digital Asset. Daml is a trademark of Digital Asset (Switzerland) GmbH.'
$script:repository = 'https://github.com/equilibriumco/vs_daml'
# The categories VsixPublisher takes for a Visual Studio extension.
$script:categories = @('build', 'coding', 'data', 'debugging', 'deployment', 'documentation', 'other', 'performance',
    'programming languages', 'reporting', 'scaffolding', 'security', 'source control', 'team development', 'testing',
    'visual studio extensions')
# Words and names the listing may only use inside the notice that denies them.
$script:forbidden = @('official', 'certified', 'endorsed', 'sponsored', 'partner', 'powered by Daml', 'Daml Studio',
    'Daml for Visual Studio', 'Visual Studio Daml')

function Get-Flat([string] $Text) { ($Text -replace '\s+', ' ').Trim() }

function Get-JsonValue([object] $Object, [string] $Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { $property.Value } else { $null }
}

<#
.SYNOPSIS
    Problems with the Marketplace listing, one line each; nothing when it can be published.
#>
function Test-MarketplaceListing {
    param(
        [Parameter(Mandatory)] [string] $PublishManifest,
        [Parameter(Mandatory)] [string] $VsixManifest
    )
    $problems = [System.Collections.Generic.List[string]]::new()
    $publish = Get-Content -LiteralPath $PublishManifest -Raw -Encoding UTF8 | ConvertFrom-Json

    $categories = @(Get-JsonValue $publish 'categories' | Where-Object { $_ })
    if ($categories.Count -lt 1 -or $categories.Count -gt 3) { $problems.Add("publishManifest.json lists $($categories.Count) categories; it takes one to three") }
    foreach ($category in $categories | Where-Object { $script:categories -notcontains $_ }) { $problems.Add("publishManifest.json: '$category' is not a Marketplace category") }
    if ((Get-JsonValue $publish 'priceCategory') -ne 'free') { $problems.Add('publishManifest.json: priceCategory has to be free') }
    if ((Get-JsonValue $publish 'private') -ne $false) { $problems.Add('publishManifest.json: private has to be false for a public listing') }
    if ((Get-JsonValue $publish 'repo') -ne $script:repository) { $problems.Add("publishManifest.json: repo has to be $script:repository") }
    if (-not (Get-JsonValue $publish 'publisher')) { $problems.Add('publishManifest.json names no publisher') }
    $identity = Get-JsonValue $publish 'identity'
    if (-not $identity -or -not (Get-JsonValue $identity 'internalName')) { $problems.Add('publishManifest.json names no identity.internalName') }

    $overviewName = Get-JsonValue $publish 'overview'
    $overviewPath = if ($overviewName) { Join-Path (Split-Path -Parent $PublishManifest) $overviewName } else { $null }
    if (-not $overviewPath -or -not (Test-Path -LiteralPath $overviewPath)) {
        $problems.Add("publishManifest.json names overview page '$overviewName', which doesn't exist")
    }
    else {
        # Paragraph by paragraph, so a wrapped sentence still reads as one but a heading never runs
        # into the paragraph below it.
        $paragraphs = @((Get-Content -LiteralPath $overviewPath -Raw -Encoding UTF8) -split '(\r?\n){2,}' | ForEach-Object { Get-Flat $_ } | Where-Object { $_ })
        if (-not @($paragraphs | Where-Object { $_.Contains($script:longNotice) })) { $problems.Add("the overview page lacks the long trademark notice (TRADEMARK.md)") }
        $rest = @($paragraphs | ForEach-Object { $_.Replace($script:longNotice, '') })
        foreach ($word in $script:forbidden | Where-Object { $pattern = '(?i)\b' + [regex]::Escape($_) + '\b'; @($rest | Where-Object { $_ -match $pattern }) }) {
            $problems.Add("the overview page says '$word' outside the trademark notice")
        }
    }

    $vsix = [xml](Get-Content -LiteralPath $VsixManifest -Raw -Encoding UTF8)
    $description = Get-Flat ($vsix.SelectSingleNode("//*[local-name()='Metadata']/*[local-name()='Description']").InnerText)
    if (-not $description.EndsWith($script:shortNotice)) { $problems.Add('the extension description has to end with the short notice (TRADEMARK.md)') }

    $problems
}

Export-ModuleMember -Function Test-MarketplaceListing
