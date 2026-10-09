<#
    SP-canon: renders the link-preview cards from one artwork:
      docs/assets/og-card.png         1200x630  the og:image / twitter:image tags the site templates emit
      docs/assets/social-preview.png  1280x640  the repository's social preview (GitHub, Settings)

    One drawing function takes the canvas size, so the two cards are the same artwork and not two
    drawings: every coordinate is written for a 630-pixel-high reference and scaled by
    Height / 630, then the whole artwork is centred horizontally on whatever width is left.

    One card serves every language. The card carries the product name and the wordless brand mark
    only - no translated sentence - so a single image stays honest on all thirteen locale pages and
    the generator never has to pick a language for a shared asset.

    Palette is taken from docs/style.css (--bg #0a0f0a, --accent #3fb950) so the card matches the
    site it previews.

    Usage:
      pwsh -NoProfile -File tools/site/make-og-image.ps1
      pwsh -NoProfile -File tools/site/make-og-image.ps1 -Check   # fail if a card is missing, mis-sized or over 1 MiB
#>
[CmdletBinding()]
param(
    # Verify both cards exist, have the exact pixel size (read from the PNG IHDR chunk) and stay
    # under the size limit; write nothing.
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

. "$PSScriptRoot/../InterfaceLanguages.ps1"

$root = Get-RepositoryRoot
$iconPath = Join-Path $root 'docs/assets/streamsplayer-icon-256.png'

# The reference canvas the coordinates below are written for. 1200x630 is the Open Graph size every
# major consumer crops to 1.91:1 around; the other card scales its height and centres the width.
$DesignWidth = 1200
$DesignHeight = 630

# GitHub's social preview limit is "under 1 MB"; Open Graph consumers have no tighter one.
$MaxBytes = 1048576

$cards = @(
    [pscustomobject]@{ Relative = 'docs/assets/og-card.png'; Width = 1200; Height = 630 }
    [pscustomobject]@{ Relative = 'docs/assets/social-preview.png'; Width = 1280; Height = 640 }
) | ForEach-Object {
    $_ | Add-Member -NotePropertyName Path -NotePropertyValue (Join-Path $root $_.Relative) -PassThru
}

function Get-PngDimensions {
    # Reads the size from the IHDR chunk, so the check holds for the bytes on disk and not for what a
    # decoder makes of them.
    param([Parameter(Mandatory)] [string] $Path)

    $header = New-Object byte[] 24
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $read = $stream.Read($header, 0, $header.Length)
    } finally {
        $stream.Dispose()
    }

    $signature = [byte[]] (0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    $isPng = ($read -eq $header.Length)
    for ($i = 0; $isPng -and $i -lt $signature.Length; $i++) {
        if ($header[$i] -ne $signature[$i]) { $isPng = $false }
    }
    if (-not $isPng -or [System.Text.Encoding]::ASCII.GetString($header, 12, 4) -ne 'IHDR') {
        throw "$Path does not start with a PNG signature and an IHDR chunk."
    }

    $width = ([int64] $header[16] -shl 24) -bor ([int64] $header[17] -shl 16) -bor ([int64] $header[18] -shl 8) -bor [int64] $header[19]
    $height = ([int64] $header[20] -shl 24) -bor ([int64] $header[21] -shl 16) -bor ([int64] $header[22] -shl 8) -bor [int64] $header[23]
    return [pscustomobject]@{ Width = $width; Height = $height }
}

function Assert-Card {
    param([Parameter(Mandatory)] $Card)

    if (-not (Test-Path -LiteralPath $Card.Path)) {
        throw "$($Card.Relative) is missing. Run tools/site/make-og-image.ps1."
    }
    $size = Get-PngDimensions -Path $Card.Path
    if ($size.Width -ne $Card.Width -or $size.Height -ne $Card.Height) {
        throw "$($Card.Relative) is $($size.Width)x$($size.Height); expected $($Card.Width)x$($Card.Height)."
    }
    $length = (Get-Item -LiteralPath $Card.Path).Length
    if ($length -ge $MaxBytes) {
        throw "$($Card.Relative) is $length bytes; it must stay under $MaxBytes (1 MiB)."
    }
    Write-Host "$($Card.Relative) is $($size.Width)x$($size.Height), $length bytes." -ForegroundColor Green
}

if ($Check) {
    foreach ($card in $cards) { Assert-Card -Card $card }
    return
}

if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "Source icon not found: docs/assets/streamsplayer-icon-256.png"
}

function Write-SocialCard {
    param(
        [Parameter(Mandatory)] [int] $Width,
        [Parameter(Mandatory)] [int] $Height,
        [Parameter(Mandatory)] [string] $Path
    )

    $bg = [System.Drawing.Color]::FromArgb(10, 15, 10)      # --bg
    $accent = [System.Drawing.Color]::FromArgb(63, 185, 80)  # --accent
    $ink = [System.Drawing.Color]::White
    $muted = [System.Drawing.Color]::FromArgb(154, 168, 156)

    # The layout derives from the canvas height; the width only decides the horizontal margin.
    $unit = $Height / $DesignHeight
    $offsetX = ($Width - ($DesignWidth * $unit)) / 2

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

        # From here the artwork is drawn in reference coordinates. Pixel-unit fonts scale with it.
        $graphics.TranslateTransform([single] $offsetX, 0)
        $graphics.ScaleTransform([single] $unit, [single] $unit)

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

        # Accent rule along the bottom, the one strong brand cue at thumbnail size. It spans the whole
        # canvas, so it is drawn in canvas coordinates, not reference ones.
        $graphics.ResetTransform()
        $rule = New-Object System.Drawing.SolidBrush($accent)
        $ruleHeight = [single] (12 * $unit)
        $graphics.FillRectangle($rule, 0, [single] ($Height - $ruleHeight), $Width, $ruleHeight)
        $rule.Dispose()
        $graphics.TranslateTransform([single] $offsetX, 0)
        $graphics.ScaleTransform([single] $unit, [single] $unit)

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

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

foreach ($card in $cards) {
    Write-SocialCard -Width $card.Width -Height $card.Height -Path $card.Path
    Assert-Card -Card $card
}
