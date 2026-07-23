<#
    Captures REAL StreamsPlayer screenshots in English and Russian by driving the
    app through its state file (no fragile UI clicking): set language -> launch ->
    maximize -> screen-grab the window -> close. The user's original state file is
    backed up and restored at the end.

    Requires a published/built StreamsPlayer.exe and a populated catalog-state.json
    (an existing catalog). Run with the desktop visible; the window is briefly
    brought to the foreground for the capture.

    Output: assets/store/app-en-<w>x<h>.png, assets/store/app-ru-<w>x<h>.png
#>
[CmdletBinding()]
param([int] $LoadWaitSeconds = 6)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Root   = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$OutDir = Join-Path $Root 'assets\store'
$Tmp    = Join-Path $Root 'tmp'
New-Item -ItemType Directory -Path $OutDir, $Tmp -Force | Out-Null

$exe = Get-ChildItem (Join-Path $Root 'src\StreamsPlayer.App\bin') -Recurse -Filter 'StreamsPlayer.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*\win-x64\publish\*' } | Select-Object -First 1
if (-not $exe) { $exe = Get-ChildItem (Join-Path $Root 'src\StreamsPlayer.App\bin') -Recurse -Filter 'StreamsPlayer.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $exe) { throw "StreamsPlayer.exe not found. Build/publish first (./msix/build-msix.ps1 or dotnet build)." }
$exe = $exe.FullName

$statePath = Join-Path $env:LOCALAPPDATA 'StreamsPlayer\catalog-state.json'
if (-not (Test-Path $statePath)) { throw "No catalog-state.json - open the app and Update catalog once so there is content to show." }
$backup = Join-Path $Tmp 'catalog-state.backup.json'
Copy-Item $statePath $backup -Force
Write-Host "Backed up state -> $backup"

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
'@
$SW_MAXIMIZE = 3; $SW_RESTORE = 9

function Get-Title([IntPtr] $h) { $sb = New-Object System.Text.StringBuilder 512; [void][W]::GetWindowText($h, $sb, 512); $sb.ToString() }

function Prep-State([string] $lang) {
    $raw = Get-Content $statePath -Raw
    # language is a JSON string enum ("English"/"Russian"); swap it in place.
    $new = [regex]::Replace($raw, '("language"\s*:\s*")(English|Russian)(")', ('${1}' + $lang + '${3}'))
    # Null lastSelectedChannelId so the app does NOT auto-open/play the video player
    # window on startup (MainWindow.Launch.cs restores + plays it otherwise).
    $new = [regex]::Replace($new, '("lastSelectedChannelId"\s*:\s*)("[0-9a-fA-F\-]+"|null)', '${1}null')
    Set-Content -Path $statePath -Value $new -Encoding utf8
}

function Capture-App([string] $tag) {
    $p = Start-Process -FilePath $exe -PassThru
    try {
        $h = [IntPtr]::Zero
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 500
            $p.Refresh()
            if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $h = $p.MainWindowHandle; break }
        }
        if ($h -eq [IntPtr]::Zero) { throw "Main window never appeared for $tag." }
        $title = Get-Title $h
        Write-Host ("  captured window title: '{0}'" -f $title)
        if ($title -match 'video|видео') { throw "Got the player window ('$title'), not the catalog. lastSelectedChannelId suppression failed." }
        [void][W]::ShowWindow($h, $SW_RESTORE)
        [void][W]::ShowWindow($h, $SW_MAXIMIZE)
        [void][W]::SetForegroundWindow($h)
        Start-Sleep -Seconds $LoadWaitSeconds   # let the catalog render / layout settle
        $r = New-Object W+RECT
        [void][W]::GetWindowRect($h, [ref]$r)
        $w = $r.R - $r.L; $ht = $r.B - $r.T
        if ($w -le 0 -or $ht -le 0) { throw "Bad window rect for $tag." }
        $bmp = New-Object System.Drawing.Bitmap($w, $ht)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
        $g.Dispose()
        $out = Join-Path $OutDir ("app-{0}-{1}x{2}.png" -f $tag, $w, $ht)
        $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        Write-Host "Wrote $out ($w x $ht)" -ForegroundColor Green
    }
    finally {
        if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $p.HasExited) { $p.Kill() } }
        Start-Sleep -Seconds 1
    }
}

try {
    Prep-State 'English'; Capture-App 'en'
    Prep-State 'Russian'; Capture-App 'ru'
}
finally {
    Copy-Item $backup $statePath -Force
    Write-Host "Restored original state from backup." -ForegroundColor Yellow
}
