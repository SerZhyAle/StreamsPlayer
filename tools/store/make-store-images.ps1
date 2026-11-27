<#
    Generates Microsoft Store / marketing images for StreamsPlayer by composing the
    brand icon and feature copy on the dark app palette. Outputs (assets/store/):
      poster-720x1080.png          9:16 Poster art  (Store logo, rendered 1440x2160)
      boxart-1080x1080.png         1:1 Box art      (Store logo, rendered 2160x2160)
      apptile-1080x1080.png        1:1 App tile icon (Store logo)
      banner-1280x360.png          promo banner (true 1280x360)

    Composed promotional cards (the house pattern), not raw app captures. For one real in-app
    screenshot per shipped interface language use tools/store/capture-store-screenshots.ps1; for a
    single arbitrary window use tools/store/capture-app.ps1.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Root   = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent   # repo root
$OutDir = Join-Path $Root 'assets\store'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# Prefer the high-resolution generated mark cut; fall back to the 256 icon.
$IconPath = Join-Path $Root 'assets\identity\out\mark-1254.png'
if (-not (Test-Path $IconPath)) { $IconPath = Join-Path $Root 'docs\assets\streamsplayer-icon-256.png' }
if (-not (Test-Path $IconPath)) { throw "Brand icon not found." }

$Background = [System.Drawing.Color]::FromArgb(9, 39, 73)
$White    = [System.Drawing.Color]::White

# Canvas at (baseW*Scale x baseH*Scale); a ScaleTransform lets all drawing use base
# coordinates while rasterizing crisply at the higher resolution.
function New-Canvas([int]$baseW, [int]$baseH, [double]$Scale = 1) {
    $bmp = New-Object System.Drawing.Bitmap([int]($baseW * $Scale), [int]($baseH * $Scale))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.ScaleTransform($Scale, $Scale)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $baseW, $baseH)
    $brush = New-Object System.Drawing.SolidBrush($Background)
    $g.FillRectangle($brush, $rect)
    $brush.Dispose()
    return @{ Bmp = $bmp; G = $g; W = $baseW; H = $baseH }
}

function Draw-CenteredImage($c, $path, [int]$targetW, [int]$y) {
    $img = [System.Drawing.Image]::FromFile($path)
    try {
        $h = [int]($targetW * $img.Height / $img.Width)
        $c.G.DrawImage($img, [int](($c.W - $targetW) / 2), $y, $targetW, $h)
        return $y + $h
    } finally { $img.Dispose() }
}

function Draw-CenteredText($c, $text, $font, $color, [int]$y) {
    $size = $c.G.MeasureString($text, $font)
    $sb = New-Object System.Drawing.SolidBrush($color)
    $c.G.DrawString($text, $font, $sb, ($c.W - $size.Width) / 2, $y)
    $sb.Dispose()
    return [int]($y + $size.Height)
}

function Save-Canvas($c, $file) {
    $c.Bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $w = $c.Bmp.Width; $h = $c.Bmp.Height
    $c.G.Dispose(); $c.Bmp.Dispose()
    Write-Host "Wrote $file ($w x $h)"
}

# ---- Store logo: 9:16 Poster art (base 720x1080, rendered 2x -> 1440x2160) ---
$c = New-Canvas 720 1080 2
$y = Draw-CenteredImage $c $IconPath 300 210
$pWord = New-Object System.Drawing.Font('Segoe UI', 46, [System.Drawing.FontStyle]::Bold)
$y = Draw-CenteredText $c 'STREAMS' $pWord $White ($y + 60)
$y = Draw-CenteredText $c 'Player' $pWord $White ($y + 2)
$pWord.Dispose()
Save-Canvas $c (Join-Path $OutDir 'poster-720x1080.png')

# ---- Store logo: 1:1 Box art (base 1080x1080, rendered 2x -> 2160x2160) ------
$c = New-Canvas 1080 1080 2
$y = Draw-CenteredImage $c $IconPath 420 250
$bWord = New-Object System.Drawing.Font('Segoe UI', 60, [System.Drawing.FontStyle]::Bold)
$y = Draw-CenteredText $c 'STREAMS Player' $bWord $White ($y + 60)
$bWord.Dispose()
Save-Canvas $c (Join-Path $OutDir 'boxart-1080x1080.png')

# ---- Store logo: 1:1 App tile icon (1080x1080) ------------------------------
$c = New-Canvas 1080 1080 1
[void](Draw-CenteredImage $c $IconPath 720 180)
Save-Canvas $c (Join-Path $OutDir 'apptile-1080x1080.png')

# ---- Banner (1280x360 at its true size, scale 1) ----------------------------
$c = New-Canvas 1280 360 1
$img = [System.Drawing.Image]::FromFile($IconPath)
try { $c.G.DrawImage($img, 120, 96, 168, 168) } finally { $img.Dispose() }
$nWord = New-Object System.Drawing.Font('Segoe UI', 52, [System.Drawing.FontStyle]::Bold)
$sbW = New-Object System.Drawing.SolidBrush($White)
$c.G.DrawString('STREAMS Player', $nWord, $sbW, 330, 118)
$sbW.Dispose(); $nWord.Dispose()
Save-Canvas $c (Join-Path $OutDir 'banner-1280x360.png')

# ---- Store display images: square tile logos (300, 150, 71) -----------------
# The Store "Store display images" slots: 1:1 App tile icon 300x300, and the
# 150x150 / 71x71 override tiles. Rendered from the master icon (already a rounded
# dark tile), so a high-quality downscale is exactly what the Store wants.
foreach ($sz in 300, 150, 71) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $img = [System.Drawing.Image]::FromFile($IconPath)
    try { $g.DrawImage($img, 0, 0, $sz, $sz) } finally { $img.Dispose() }
    $g.Dispose()
    $f = Join-Path $OutDir ("tile-{0}x{0}.png" -f $sz)
    $bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Host "Wrote $f ($sz x $sz)"
}

# Remove the earlier 1x screenshot files if present (superseded by 2x).
foreach ($old in 'screenshot-en-1366x768.png', 'screenshot-ru-1366x768.png') {
    $p = Join-Path $OutDir $old
    if (Test-Path $p) { Remove-Item $p -Force }
}

Write-Host 'Done. Store images in assets/store/.' -ForegroundColor Green
