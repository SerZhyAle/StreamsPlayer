<#
    SP-canon: renders docs/assets/og-card.png, the 1200x630 link-preview card referenced by the
    og:image / twitter:image tags the site templates emit.

    One card serves every language. The card carries the product name and the wordless brand mark
    only - no translated sentence - so a single image stays honest on all thirteen locale pages and
    the generator never has to pick a language for a shared asset.

    Palette is taken from docs/style.css (--bg #0a0f0a, --accent #3fb950) so the card matches the
    site it previews.

    Usage:
      pwsh -NoProfile -File tools/site/make-og-image.ps1
      pwsh -NoProfile -File tools/site/make-og-image.ps1 -Check   # fail if the card is missing
#>
[CmdletBinding()]
param(
    # Verify the card exists and has the right dimensions; write nothing.
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

. "$PSScriptRoot/../InterfaceLanguages.ps1"

$root = Get-RepositoryRoot
$iconPath = Join-Path $root 'docs/assets/streamsplayer-icon-256.png'
$targetPath = Join-Path $root 'docs/assets/og-card.png'

# The Open Graph card size every major consumer crops to 1.91:1 around.
$Width = 1200
$Height = 630

if ($Check) {
    if (-not (Test-Path -LiteralPath $targetPath)) {
        throw "docs/assets/og-card.png is missing. Run tools/site/make-og-image.ps1."
    }
    $probe = [System.Drawing.Image]::FromFile($targetPath)
    try {
        if ($probe.Width -ne $Width -or $probe.Height -ne $Height) {
            throw "docs/assets/og-card.png is $($probe.Width)x$($probe.Height); expected ${Width}x${Height}."
        }
    } finally {
        $probe.Dispose()
    }
    Write-Host "og-card.png is present at ${Width}x${Height}." -ForegroundColor Green
    return
}

if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "Source icon not found: docs/assets/streamsplayer-icon-256.png"
}

$bg = [System.Drawing.Color]::FromArgb(10, 15, 10)      # --bg
$accent = [System.Drawing.Color]::FromArgb(63, 185, 80)  # --accent
$ink = [System.Drawing.Color]::White
$muted = [System.Drawing.Color]::FromArgb(154, 168, 156)

$bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $backdrop = New-Object System.Drawing.SolidBrush($bg)
    $graphics.FillRectangle($backdrop, 0, 0, $Width, $Height)
    $backdrop.Dispose()

    # A soft accent glow behind the mark, echoing the page's .background-glow.
    $glowRect = New-Object System.Drawing.Rectangle(60, 150, 620, 620)
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse($glowRect)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(70, $accent)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $accent))
    $graphics.FillEllipse($glow, $glowRect)
    $glow.Dispose()
    $glowPath.Dispose()

    # Accent rule along the bottom, the one strong brand cue at thumbnail size.
    $rule = New-Object System.Drawing.SolidBrush($accent)
    $graphics.FillRectangle($rule, 0, $Height - 12, $Width, 12)
    $rule.Dispose()

    $icon = [System.Drawing.Image]::FromFile($iconPath)
    try {
        $graphics.DrawImage($icon, 96, 175, 280, 280)
    } finally {
        $icon.Dispose()
    }

    $titleFont = New-Object System.Drawing.Font('Segoe UI', 74, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $kickerFont = New-Object System.Drawing.Font('Segoe UI', 34, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $footFont = New-Object System.Drawing.Font('Segoe UI', 27, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

    $inkBrush = New-Object System.Drawing.SolidBrush($ink)
    $accentBrush = New-Object System.Drawing.SolidBrush($accent)
    $mutedBrush = New-Object System.Drawing.SolidBrush($muted)
    try {
        $graphics.DrawString('STREAMS', $titleFont, $inkBrush, 440, 208)
        $graphics.DrawString('Player', $titleFont, $accentBrush, 440, 292)
        $graphics.DrawString('Internet radio, live video and RTSP', $kickerFont, $mutedBrush, 446, 400)
        $graphics.DrawString('Windows desktop  -  free  -  no telemetry', $footFont, $mutedBrush, 446, 452)
    } finally {
        $inkBrush.Dispose()
        $accentBrush.Dispose()
        $mutedBrush.Dispose()
        $titleFont.Dispose()
        $kickerFont.Dispose()
        $footFont.Dispose()
    }

    $bitmap.Save($targetPath, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Host "Wrote docs/assets/og-card.png (${Width}x${Height})." -ForegroundColor Green
