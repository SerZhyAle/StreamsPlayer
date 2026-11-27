#Requires -Version 7.0
<#
.SYNOPSIS
    STRWIN-0301: writes every rendition of the product mark from its vector master and the identity manifest
    (PRODUCT-IDENTITY 0.1/0.4, sections 3.1, 3.4 and 5).

.DESCRIPTION
    The master is the vector SVG assets/identity/master/mark.svg (master.svg: pure vector geometry,
    1254 x 1254 viewBox). Every rendition below is a render target - never edit one by hand.

    Cut from the vector master by this script (rendered with WPF DrawingVisual / RenderTargetBitmap):
      assets/streamsplayer.ico                      app.exe-ico, app.installer-ico (16-256, PNG entries)
      assets/identity/out/mark-1254.png             other.store-input (1254x1254 cut for composers)
      assets/msix/Square44x44Logo.png ...           msix.square44, msix.square150, msix.storelogo, other.msix-square71
      docs/assets/favicon-32.png                    other.web-favicon-png (the site's PNG favicon)
      docs/assets/apple-touch-icon.png              web.apple-touch
      docs/assets/streamsplayer-icon-256.png        site.identity, readme.head, hub.card
      assets/identity/out/help-head-48.png, -96.png help.head (copied byte for byte into docs/help by build-help.ps1)

    Composed from the cut input by the two composers this script runs with -Compose (rule 6):
      tools/site/make-og-image.ps1       docs/assets/og-card.png (web.og-image), docs/assets/social-preview.png
                                         (github.social-preview)
      tools/store/make-store-images.ps1  assets/store/{boxart,poster,apptile,tile-*}.png (store.ms.logo),
                                         banner-1280x360.png
    Without -Compose their files are only hashed into the manifest, so a plain run never rewrites a store graphic.

    Then writes assets/identity/identity-manifest.json. A rerun on an unchanged master changes no byte (rung 3).

    -Check writes nothing: exit 0 when every cut rendition and the manifest equal what would be written, 1 when one
    differs or is missing (each named), 2 when the generator cannot run. The last line is the verdict
    (CHECK-VERDICT section 2).

.EXAMPLE
    pwsh -NoProfile -File ./tools/identity/build-identity.ps1
    pwsh -NoProfile -File ./tools/identity/build-identity.ps1 -Compose
    pwsh -NoProfile -File ./tools/identity/build-identity.ps1 -Check
#>
[CmdletBinding()]
param(
    [switch] $Compose,
    [switch] $Check
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$manifestRel = 'assets/identity/identity-manifest.json'
$masterRel = 'assets/identity/master/mark.svg'
# The plate colour of the mark, sampled from the master; the background of composed graphics (rule 6).
$background = '#092749'
$generator = 'pwsh -NoProfile -File tools/identity/build-identity.ps1 -Compose'

$cuts = @(
    @{ Surface = 'other.store-input';      Path = 'assets/identity/out/mark-1254.png';      Size = 1254 }
    @{ Surface = 'msix.square44';          Path = 'assets/msix/Square44x44Logo.png';        Size = 44 }
    @{ Surface = 'other.msix-square71';    Path = 'assets/msix/Square71x71Logo.png';        Size = 71 }
    @{ Surface = 'msix.square150';         Path = 'assets/msix/Square150x150Logo.png';      Size = 150 }
    @{ Surface = 'msix.storelogo';         Path = 'assets/msix/StoreLogo.png';              Size = 50 }
    @{ Surface = 'other.web-favicon-png';  Path = 'docs/assets/favicon-32.png';             Size = 32 }
    @{ Surface = 'web.apple-touch';        Path = 'docs/assets/apple-touch-icon.png';       Size = 180 }
    @{ Surface = 'site.identity';          Path = 'docs/assets/streamsplayer-icon-256.png'; Size = 256 }
    @{ Surface = 'help.head';              Path = 'assets/identity/out/help-head-48.png';   Size = 48 }
    @{ Surface = 'help.head';              Path = 'assets/identity/out/help-head-96.png';   Size = 96 }
)
$icoRel = 'assets/streamsplayer.ico'
$icoSizes = 16, 24, 32, 48, 64, 128, 256
# Files that are the same bytes as a cut above, listed again under the other surfaces they serve.
$aliases = @(
    @{ Surface = 'app.installer-ico'; Path = $icoRel }
    @{ Surface = 'readme.head';       Path = 'docs/assets/streamsplayer-icon-256.png' }
    @{ Surface = 'hub.card';          Path = 'docs/assets/streamsplayer-icon-256.png' }
)
$composed = @(
    @{ Surface = 'web.og-image';              Path = 'docs/assets/og-card.png' }
    @{ Surface = 'github.social-preview';     Path = 'docs/assets/social-preview.png' }
    @{ Surface = 'store.ms.logo';             Path = 'assets/store/boxart-1080x1080.png' }
    @{ Surface = 'store.ms.logo';             Path = 'assets/store/poster-720x1080.png' }
    @{ Surface = 'store.ms.logo';             Path = 'assets/store/apptile-1080x1080.png' }
    @{ Surface = 'store.ms.logo';             Path = 'assets/store/tile-71x71.png' }
    @{ Surface = 'store.ms.logo';             Path = 'assets/store/tile-150x150.png' }
    @{ Surface = 'store.ms.logo';             Path = 'assets/store/tile-300x300.png' }
    @{ Surface = 'other.store-banner';        Path = 'assets/store/banner-1280x360.png' }
)

function Get-Full([string] $Rel) { Join-Path $root ($Rel -replace '/', [System.IO.Path]::DirectorySeparatorChar) }
function Get-Sha([byte[]] $Bytes) { [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }

function Get-PngSize([byte[]] $Bytes) {
    if ($Bytes.Length -lt 24 -or $Bytes[0] -ne 0x89) { throw 'not a PNG' }
    $w = [System.Buffers.Binary.BinaryPrimitives]::ReadInt32BigEndian([byte[]] $Bytes[16..19])
    $h = [System.Buffers.Binary.BinaryPrimitives]::ReadInt32BigEndian([byte[]] $Bytes[20..23])
    return @($w, $h)
}

$masterFull = Get-Full $masterRel
if (-not (Test-Path -LiteralPath $masterFull -PathType Leaf)) {
    Write-Output "identity: COULD NOT VERIFY (master $masterRel is missing)"
    exit 2
}
$masterBytes = [System.IO.File]::ReadAllBytes($masterFull)
$masterSha = Get-Sha $masterBytes

$rawSvg = [System.Text.Encoding]::UTF8.GetString($masterBytes)
if ($rawSvg -match '<image' -or $rawSvg -match 'data:' -or $rawSvg -match 'href') {
    throw "Unsupported SVG feature: image elements or external references found in $masterRel"
}
[xml]$svgXml = $rawSvg
if ($svgXml.svg.viewBox -ne '0 0 1254 1254') {
    throw "Unsupported SVG viewBox: expected '0 0 1254 1254', got '$($svgXml.svg.viewBox)'"
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

function Get-Brush([string] $Fill) {
    if ($Fill -match '^#([0-9a-fA-F]{2})([0-9a-fA-F]{2})([0-9a-fA-F]{2})$') {
        $r = [Convert]::ToByte($Matches[1], 16)
        $g = [Convert]::ToByte($Matches[2], 16)
        $b = [Convert]::ToByte($Matches[3], 16)
        return [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromArgb(255, $r, $g, $b))
    }
    throw "Unsupported fill color: $Fill"
}

$svgDrawables = [System.Collections.Generic.List[object]]::new()
foreach ($node in $svgXml.svg.ChildNodes) {
    if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
    if ($node.LocalName -eq 'rect') {
        $x = [double]$node.x; $y = [double]$node.y
        $w = [double]$node.width; $h = [double]$node.height
        $rx = [double]$node.rx
        $brush = Get-Brush $node.fill
        $geom = [System.Windows.Media.RectangleGeometry]::new([System.Windows.Rect]::new($x, $y, $w, $h), $rx, $rx)
        $svgDrawables.Add(@{ Brush = $brush; Geometry = $geom })
    }
    elseif ($node.LocalName -eq 'path') {
        $brush = Get-Brush $node.fill
        $geom = [System.Windows.Media.Geometry]::Parse($node.d)
        $svgDrawables.Add(@{ Brush = $brush; Geometry = $geom })
    }
    else {
        throw "Unsupported SVG element: $($node.LocalName)"
    }
}

function New-Cut([int] $Size) {
    $scale = $Size / 1254.0
    $dv = [System.Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
    foreach ($item in $svgDrawables) {
        $dc.DrawGeometry($item.Brush, $null, $item.Geometry)
    }
    $dc.Pop()
    $dc.Close()

    $rtb = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)

    $enc = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $stream = [System.IO.MemoryStream]::new()
    $enc.Save($stream)
    return , $stream.ToArray()
}

function New-Ico([int[]] $Sizes) {
    $frames = foreach ($s in $Sizes) { , (New-Cut $s) }
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([uint16] 0); $writer.Write([uint16] 1); $writer.Write([uint16] $Sizes.Count)
    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $dim = if ($Sizes[$i] -ge 256) { 0 } else { $Sizes[$i] }
        $writer.Write([byte] $dim); $writer.Write([byte] $dim); $writer.Write([byte] 0); $writer.Write([byte] 0)
        $writer.Write([uint16] 1); $writer.Write([uint16] 32)
        $writer.Write([uint32] $frames[$i].Length); $writer.Write([uint32] $offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
    $writer.Flush()
    return , $stream.ToArray()
}

# Everything the generator directly cuts, in memory: path -> bytes.
$generatedCuts = [ordered]@{}
$rows = [System.Collections.Generic.List[object]]::new()

$ico = New-Ico $icoSizes
$generatedCuts[$icoRel] = $ico
$rows.Add([ordered]@{ surface = 'app.exe-ico'; path = $icoRel; format = 'ico'; width = 256; height = 256; sha256 = (Get-Sha $ico) })
foreach ($cut in $cuts) {
    $bytes = New-Cut $cut.Size
    $generatedCuts[$cut.Path] = $bytes
    $rows.Add([ordered]@{ surface = $cut.Surface; path = $cut.Path; format = 'png'; width = $cut.Size; height = $cut.Size; sha256 = (Get-Sha $bytes) })
}
foreach ($alias in $aliases) {
    $source = @($rows | Where-Object { $_.path -eq $alias.Path })[0]
    $row = [ordered]@{}
    foreach ($key in $source.Keys) { $row[$key] = $source[$key] }
    $row.surface = $alias.Surface
    $rows.Add($row)
}

# In write mode: write cut renditions to disk FIRST so composers consume fresh inputs
if (-not $Check) {
    foreach ($rel in $generatedCuts.Keys) {
        $full = Get-Full $rel
        New-Item -ItemType Directory -Path (Split-Path -Parent $full) -Force | Out-Null
        [System.IO.File]::WriteAllBytes($full, [byte[]] $generatedCuts[$rel])
        Write-Output "wrote $rel"
    }

    if ($Compose) {
        foreach ($composer in 'tools/site/make-og-image.ps1', 'tools/store/make-store-images.ps1') {
            & pwsh -NoProfile -File (Get-Full $composer)
            if ($LASTEXITCODE -ne 0) { throw "$composer exited $LASTEXITCODE" }
        }
    }
}

# Collect composed outputs
$missing = [System.Collections.Generic.List[string]]::new()
foreach ($item in $composed) {
    $full = Get-Full $item.Path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        $missing.Add("$($item.Path) - composed rendition is missing; run with -Compose")
        continue
    }
    $bytes = [System.IO.File]::ReadAllBytes($full)
    $size = Get-PngSize $bytes
    $rows.Add([ordered]@{ surface = $item.Surface; path = $item.Path; format = 'png'; width = $size[0]; height = $size[1]; sha256 = (Get-Sha $bytes) })
}

$manifest = [ordered]@{
    schemaVersion = 1
    product       = 'STREAMS Player'
    family        = $null
    master        = [ordered]@{ svg = $masterRel; background = $background; sha256 = $masterSha }
    generator     = $generator
    renditions    = $rows
}
$json = ($manifest | ConvertTo-Json -Depth 5) -replace "`r`n", "`n"
$manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json + "`n")

if (-not $Check) {
    $manifestFull = Get-Full $manifestRel
    [System.IO.File]::WriteAllBytes($manifestFull, $manifestBytes)
    Write-Output "wrote $manifestRel"
    foreach ($line in $missing) { Write-Output "  WARN $line" }
    Write-Output "identity: wrote the manifest ($($rows.Count) rendition(s))"
    exit 0
}

# In -Check mode: compare everything on disk against expected without writing
$differs = [System.Collections.Generic.List[string]]::new()
$differs.AddRange($missing)

foreach ($rel in $generatedCuts.Keys) {
    $full = Get-Full $rel
    $current = if (Test-Path -LiteralPath $full -PathType Leaf) { [System.IO.File]::ReadAllBytes($full) } else { $null }
    if ($null -eq $current) {
        $differs.Add("$rel - missing")
    } elseif ((Get-Sha $current) -ne (Get-Sha ([byte[]] $generatedCuts[$rel]))) {
        $differs.Add("$rel - differs from what the master generates")
    }
}

# Compare manifest on disk
$manifestFull = Get-Full $manifestRel
$currentManifest = if (Test-Path -LiteralPath $manifestFull -PathType Leaf) { [System.IO.File]::ReadAllBytes($manifestFull) } else { $null }
if ($null -eq $currentManifest) {
    $differs.Add("$manifestRel - missing")
} elseif ((Get-Sha $currentManifest) -ne (Get-Sha $manifestBytes)) {
    $differs.Add("$manifestRel - differs from what the master generates")
}

foreach ($line in $differs) { Write-Output "  FAIL $line" }
if ($differs.Count -gt 0) {
    Write-Output "identity: FAIL ($($differs.Count) file(s); run tools/identity/build-identity.ps1 -Compose)"
    exit 1
}

Write-Output "identity: PASS ($($generatedCuts.Count + 1) file(s) current, $($rows.Count) rendition(s))"
exit 0
