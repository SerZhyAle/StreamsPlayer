<#
    Turns the Store screenshots into the site's lead screenshot, one per site language.

    Input : assets/store/app-<listing-code>.png, written by tools/store/capture-store-screenshots.ps1
            (the catalog as a grid of video channels, 1366x768).
    Output: docs/assets/screens/grid-<dictionary-code>.jpg, referenced by tools/site/templates/index.html.

    The site pages are named by dictionary code (en, pt, zh) and the Store by listing code (en-us,
    pt-br, zh-hans); the mapping comes from the Core registry, never from a list in this file. JPEG
    rather than the PNG itself: the frames are photographic, and a ~1 MB PNG per page is a lot to put
    above the fold.

    Usage:
      pwsh -NoProfile -File tools/site/export-site-screenshots.ps1
#>
[CmdletBinding()]
param(
    [ValidateRange(50, 100)] [int] $Quality = 85
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/../InterfaceLanguages.ps1"
Add-Type -AssemblyName System.Drawing

$root = Get-RepositoryRoot
$sourceDirectory = Join-Path $root 'assets/store'
$outputDirectory = Join-Path $root 'docs/assets/screens'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$encoder = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$parameters = New-Object System.Drawing.Imaging.EncoderParameters(1)
$parameters.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long] $Quality)

$missing = [System.Collections.Generic.List[string]]::new()
foreach ($language in Get-InterfaceLanguages) {
    $source = Join-Path $sourceDirectory "app-$($language.ListingCode).png"
    if (-not (Test-Path -LiteralPath $source)) { $missing.Add($source); continue }

    $target = Join-Path $outputDirectory "grid-$($language.DictionaryCode).jpg"
    $image = [System.Drawing.Image]::FromFile($source)
    try { $image.Save($target, $encoder, $parameters) }
    finally { $image.Dispose() }
    Write-Host ("{0,-8} -> {1} ({2:N0} KB)" -f $language.ListingCode, [System.IO.Path]::GetFileName($target), ((Get-Item -LiteralPath $target).Length / 1KB))
}

if ($missing.Count) {
    throw "No Store screenshot for: $($missing -join ', '). Run tools/store/capture-store-screenshots.ps1 first."
}
