<#
    SP-0034: the one place that turns a captured window into a Store-valid image.

    Both capture scripts dot-source this so there is a single canvas size and a single composition
    rule. Before this file there were two, and the one auto-capture.ps1 used wrote the raw window
    rectangle - not a size the Store accepts.

    1366x768 is the Store's minimum desktop screenshot size; a window of any other shape is scaled to
    fit and centred on the application's own dark background rather than stretched.
#>

Add-Type -AssemblyName System.Drawing

$script:StoreCanvasWidth = 1366
$script:StoreCanvasHeight = 768
$script:StoreCanvasBackground = [System.Drawing.Color]::FromArgb(13, 16, 23)

function Save-StoreCanvasImage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.Drawing.Image] $Image,
        [Parameter(Mandatory)] [string] $Path
    )

    $canvas = New-Object System.Drawing.Bitmap($script:StoreCanvasWidth, $script:StoreCanvasHeight)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($canvas)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.Clear($script:StoreCanvasBackground)
            $scale = [Math]::Min($script:StoreCanvasWidth / $Image.Width, $script:StoreCanvasHeight / $Image.Height)
            $width = [int]($Image.Width * $scale)
            $height = [int]($Image.Height * $scale)
            $graphics.DrawImage($Image, [int](($script:StoreCanvasWidth - $width) / 2), [int](($script:StoreCanvasHeight - $height) / 2), $width, $height)
        }
        finally { $graphics.Dispose() }

        $directory = [System.IO.Path]::GetDirectoryName($Path)
        if ($directory -and -not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        $canvas.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $canvas.Dispose() }

    return [pscustomobject]@{
        Path   = $Path
        Width  = $script:StoreCanvasWidth
        Height = $script:StoreCanvasHeight
    }
}
