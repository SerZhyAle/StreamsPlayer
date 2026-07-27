<#
    Captures a real screenshot of the running StreamsPlayer window as a Store-ready
    1366x768 PNG (the Store minimum). Use this to produce genuine in-app shots that
    replace or supplement the composed cards from make-store-images.ps1.

    Usage:
      1. Launch the app and arrange the window you want (e.g. catalog List mode,
         Grid mode with thumbnails, or the video player). Refresh the catalog first
         so content is visible.
      2. Run:  pwsh -NoProfile -File tools/store/capture-app.ps1 -Name catalog-en
         A 5s countdown lets you click the target window to bring it to the front.
      3. Output: assets/store/shot-<Name>-1366x768.png

    The foreground window is captured, then scaled to fit a 1366x768 canvas on the
    app's dark background (letterboxed if the aspect ratio differs). Maximize the
    window to a 16:9 monitor for a pixel-perfect fill.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Name,
    [int] $CountdownSeconds = 5
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
. "$PSScriptRoot/StoreCanvas.ps1"

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32Cap {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
}
'@

$Root   = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$OutDir = Join-Path $Root 'assets\store'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$OutFile = Join-Path $OutDir "shot-$Name-1366x768.png"

for ($i = $CountdownSeconds; $i -gt 0; $i--) {
    Write-Host "Capturing the foreground window in $i s - click the StreamsPlayer window now..." -NoNewline
    Start-Sleep -Seconds 1
    Write-Host "`r" -NoNewline
}

$h = [Win32Cap]::GetForegroundWindow()
$r = New-Object Win32Cap+RECT
[void][Win32Cap]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { throw 'Could not read the foreground window bounds.' }

$grab = New-Object System.Drawing.Bitmap($w, $ht)
$gg = [System.Drawing.Graphics]::FromImage($grab)
$gg.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
$gg.Dispose()

# Compose onto the shared Store canvas (tools/store/StoreCanvas.ps1) so both capture scripts produce
# the same size and the same letterboxing.
$saved = Save-StoreCanvasImage -Image $grab -Path $OutFile
$grab.Dispose()
Write-Host ("Wrote {0} ({1} x {2})" -f $saved.Path, $saved.Width, $saved.Height) -ForegroundColor Green
