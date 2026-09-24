<#
    SP-0034: one real Store screenshot per shipped interface language.

    Replaces tools/store/auto-capture.ps1, which produced two files that were both wrong. Its regex
    matched the *old* language value - '(English|Russian)' - and [regex]::Replace is a silent no-op
    when it matches nothing, so with a saved state on Ukrainian both app-en-*.png and app-ru-*.png
    were captured from a Ukrainian window and written under English and Russian names. Nothing in the
    run said so. Everything below exists to make that class of failure impossible:

      - the language set comes from the Core registry, not from a list in this file;
      - the state write is verified by reading the file back, so a no-op throws;
      - the captured window is queried through UI Automation for a string taken from that language's
        dictionary, so a window in the wrong language throws before a PNG is written;
      - the owner's real profile is renamed aside for the whole run and its hash is checked
        afterwards, so a capture can never write into the real catalog, pins or history.

    What is captured is fixed here, not inherited from whatever the owner last looked at: the catalog
    in Grid mode, filtered to video channels, with the cached preview frames copied into the sandbox.
    That view is the product's lead screenshot on the Store (DesktopScreenshot1) and on the site. The
    Media filter lives in browsing-session.json since SP-0067, and the frames in grid-previews/; a run
    that copied only catalog-state.json captured an unfiltered grid of stretched favicons.

    This needs a real desktop, a stable screen and a populated catalog, so it cannot run in CI.

    Usage:
      pwsh -NoProfile -File tools/store/capture-store-screenshots.ps1
      # pwsh -File binds only the first of several values and hands the rest to the next positional
      # parameter, so pass a list through -Command:
      pwsh -NoProfile -Command "& ./tools/store/capture-store-screenshots.ps1 -Languages de,ar"
#>
[CmdletBinding()]
param(
    # Listing codes (en-us, pt-br, zh-hans, ..). Defaults to every shipped language.
    [string[]] $Languages,
    # Grid mode decodes a preview frame per visible tile, so it needs longer than List mode did.
    [int] $LoadWaitSeconds = 12,
    [string] $OutputDirectory,
    # StreamTileSize member name. The smallest size puts the most live frames in one shot.
    [ValidateSet('VerySmall', 'Small', 'Medium', 'Large')] [string] $TileSize = 'VerySmall',
    # By default the sandbox copy is unpinned, so the owner's personal pinned strip does not take half
    # of a public screenshot. The real profile is never touched either way.
    [switch] $KeepPins
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/../InterfaceLanguages.ps1"
. "$PSScriptRoot/StoreCanvas.ps1"

$root = Get-RepositoryRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'assets/store' }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CaptureWin32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@

$SW_RESTORE = 9
$GWL_EXSTYLE = -20
$WS_EX_LAYOUTRTL = 0x00400000
# PrintWindow copies the window's own content instead of whatever pixels happen to be on screen, so a
# tooltip, a notification or another window cannot land in the shot. PW_RENDERFULLCONTENT is required
# for a composited (WPF) window - without it the client area comes back blank.
$PW_RENDERFULLCONTENT = 2

# The automation name used to prove the window really is in the requested language. Its expected value
# is read from that language's dictionary, never written here.
# SearchName, not LanguagePickerName: the language picker left the main window for Settings (SP-0050),
# and the search box is the one named control that stays on the title line in every layout.
$VerificationKey = 'SearchName'

# --------------------------------------------------------------------------- resolve the languages

$registry = Get-InterfaceLanguages
if (-not $Languages) { $Languages = @($registry | ForEach-Object { $_.ListingCode }) }

$targets = [System.Collections.Generic.List[pscustomobject]]::new()
foreach ($requested in $Languages) {
    $match = $registry | Where-Object { $_.ListingCode -eq $requested -or $_.DictionaryCode -eq $requested -or $_.Language -eq $requested }
    if (-not $match) {
        throw "'$requested' is not a shipped language. Known listing codes: $(($registry | ForEach-Object { $_.ListingCode }) -join ', ')."
    }
    $targets.Add(@($match)[0])
}

function Get-DictionaryValue {
    param(
        [Parameter(Mandatory)] [string] $DictionaryCode,
        [Parameter(Mandatory)] [string] $Key
    )

    $path = Join-Path $root "src/StreamsPlayer.App/Localization.$DictionaryCode.xaml"
    if (-not (Test-Path -LiteralPath $path)) { throw "No dictionary at $path." }
    $xml = [xml] (Get-Content -LiteralPath $path -Raw -Encoding utf8)
    foreach ($node in $xml.DocumentElement.ChildNodes) {
        if ($node.NodeType -ne 'Element') { continue }
        if ($node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml') -eq $Key) { return $node.InnerText }
    }
    throw "Localization.$DictionaryCode.xaml has no '$Key' key, so the capture cannot be verified."
}

# A verification string shared by two languages would let a wrong-language window pass, so the key is
# checked for uniqueness across everything being captured before anything is launched.
$expected = @{}
foreach ($target in $targets) { $expected[$target.ListingCode] = Get-DictionaryValue -DictionaryCode $target.DictionaryCode -Key $VerificationKey }
$collisions = $expected.GetEnumerator() | Group-Object -Property Value | Where-Object Count -gt 1
if ($collisions) {
    $detail = ($collisions | ForEach-Object { "'$($_.Name)' is used by $(($_.Group | ForEach-Object { $_.Key }) -join ', ')" }) -join '; '
    throw "The verification key '$VerificationKey' is not unique across the requested languages ($detail). Pick a key whose value differs in every language."
}

# --------------------------------------------------------------------------------------- the app

# The newest build wins, and it must be newer than the sources. The first run of this script picked a
# 'publish' folder that happened to be three days stale and captured a window built before the
# thirteen languages existed - which the language verification below caught. Preferring a publish
# folder was the bug; a stale build is worse than no build.
$assembly = Get-ChildItem (Join-Path $root 'src/StreamsPlayer.App/bin') -Recurse -Filter 'StreamsPlayer.dll' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $assembly) { throw 'StreamsPlayer.dll not found. Build first: dotnet build StreamsPlayer.sln -c Release' }
$exe = Join-Path $assembly.DirectoryName 'StreamsPlayer.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "No StreamsPlayer.exe beside $($assembly.FullName)." }

$newestSource = Get-ChildItem (Join-Path $root 'src') -Recurse -Include '*.cs', '*.xaml' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newestSource.LastWriteTime -gt $assembly.LastWriteTime) {
    throw ("The newest build ({0:yyyy-MM-dd HH:mm}) is older than {1} ({2:yyyy-MM-dd HH:mm}). " -f
        $assembly.LastWriteTime, $newestSource.Name, $newestSource.LastWriteTime) +
        'Build first: dotnet build StreamsPlayer.sln -c Release'
}
Write-Host ("Build: {0} ({1:yyyy-MM-dd HH:mm})" -f $exe, $assembly.LastWriteTime)

if (@(Get-Process -Name 'StreamsPlayer' -ErrorAction SilentlyContinue).Count) {
    throw 'StreamsPlayer is running. Close it first - this script renames its profile folder aside for the duration of the run.'
}

# ------------------------------------------------------------------------------------- the sandbox

# %LOCALAPPDATA% cannot be redirected: the app resolves its folder through the known-folder API, so
# the environment variable has no effect (memory/MEMORY.md). The only way to sandbox it is to move
# the real folder out of the way and put a disposable copy in its place.
$profileRoot = Join-Path $env:LOCALAPPDATA 'StreamsPlayer'
$asideRoot = "$profileRoot.sp0034-aside"
$statePath = Join-Path $profileRoot 'catalog-state.json'
$sessionPath = Join-Path $profileRoot 'browsing-session.json'

if (-not (Test-Path -LiteralPath $statePath)) {
    throw "No catalog-state.json under $profileRoot. Open the app and refresh the catalog once, so a capture has content to show."
}
if (Test-Path -LiteralPath $asideRoot) {
    throw "$asideRoot already exists - a previous run did not finish. Check its contents, move it back to $profileRoot by hand, and run again."
}

$realStateHash = (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash
Write-Host ("Real state: {0} (SHA256 {1})" -f $statePath, $realStateHash.Substring(0, 16))

function Write-SandboxFile {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Text
    )

    # Atomic, matching StreamCatalogStore: write a temp file, then move it over the target.
    $temp = "$Path.capture-tmp"
    [System.IO.File]::WriteAllText($temp, $Text, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::Move($temp, $Path, $true)
}

function Set-SandboxState {
    param([Parameter(Mandatory)] [string] $Language)

    # Edited as a JSON tree, not by regex: every channel row carries a "language" property of its own,
    # and a pattern over the text rewrote all of them along with the interface language. Only the
    # root's properties are touched here; everything else round-trips as the node it was read as.
    $root = [System.Text.Json.Nodes.JsonNode]::Parse([System.IO.File]::ReadAllText($statePath))
    $root['language'] = [System.Text.Json.Nodes.JsonValue]::Create($Language)
    $root['viewMode'] = [System.Text.Json.Nodes.JsonValue]::Create('Grid')
    $root['tileSize'] = [System.Text.Json.Nodes.JsonValue]::Create($TileSize)
    if (-not $KeepPins) {
        foreach ($channel in $root['channels'].AsArray()) {
            if ($channel['pinned'] -and $channel['pinned'].GetValue[bool]()) { $channel['pinned'] = [System.Text.Json.Nodes.JsonValue]::Create($false) }
        }
    }
    # A state file from before SP-0067 still carries the last selection; the session file below is
    # what the current build reads, and both are cleared so nothing is highlighted or scrolled to.
    if ($root.AsObject().ContainsKey('lastSelectedChannelId')) { $root['lastSelectedChannelId'] = $null }
    Write-SandboxFile -Path $statePath -Text $root.ToJsonString()

    # The Media filter moved out of catalog-state.json into browsing-session.json (SP-0067). Writing
    # the whole session, rather than copying the owner's, is what makes the view the same on every run.
    $session = [ordered] @{
        schemaVersion = 1; searchQuery = ''; mediaFilter = 'Video'; categoryFilter = 'All'; topicFilter = 'All'
        languageFilter = 'All'; countryFilter = 'All'; minBitrateFilter = 'All'; collectionFilter = 'All'
        sortMode = 'Name'; scrollOffset = 0; lastSelectedChannelId = $null
    }
    Write-SandboxFile -Path $sessionPath -Text ($session | ConvertTo-Json -Compress)

    # Read both back. The predecessor of this script assumed its edit had landed; it had not, and every
    # image it produced was wrong. An unverified write is the whole defect.
    $stored = [System.Text.Json.Nodes.JsonNode]::Parse([System.IO.File]::ReadAllText($statePath))
    if ([string] $stored['language'] -ne $Language -or [string] $stored['viewMode'] -ne 'Grid') {
        throw ("The sandbox state reports language '{0}', view '{1}' after asking for '{2}', 'Grid'. Refusing to capture." -f
            [string] $stored['language'], [string] $stored['viewMode'], $Language)
    }
    if ((Get-Content -LiteralPath $sessionPath -Raw -Encoding utf8 | ConvertFrom-Json).mediaFilter -ne 'Video') {
        throw 'The sandbox session does not filter to Video. Refusing to capture.'
    }
}

function Test-WindowLanguage {
    param(
        [Parameter(Mandatory)] [IntPtr] $Handle,
        [Parameter(Mandatory)] [string] $Expected
    )

    $element = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    if (-not $element) { throw 'UI Automation could not reach the window.' }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Expected)
    $found = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    return $null -ne $found
}

function Get-WindowImage {
    param([Parameter(Mandatory)] [IntPtr] $Handle)

    $rect = New-Object CaptureWin32+RECT
    [void] [CaptureWin32]::GetWindowRect($Handle, [ref] $rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    # The window is found by process handle and validated by size; its title is localized and is not
    # a usable discriminator - the script it replaced hardcoded two locales into a title match.
    if ($width -lt 800 -or $height -lt 600) {
        throw "The window is $width x $height, too small to be the sized main window."
    }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [CaptureWin32]::PrintWindow($Handle, $hdc, $PW_RENDERFULLCONTENT)) {
            throw 'PrintWindow failed.'
        }
    }
    finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }

    # A window carrying WS_EX_LAYOUTRTL is returned horizontally flipped by PrintWindow. WPF mirrors
    # in managed layout and normally leaves the extended style clear, so this usually does nothing -
    # which is exactly why it is conditional. Flipping unconditionally would mirror every image.
    if ([CaptureWin32]::GetWindowLong($Handle, $GWL_EXSTYLE) -band $WS_EX_LAYOUTRTL) {
        Write-Host '    WS_EX_LAYOUTRTL is set - flipping the capture back.' -ForegroundColor Yellow
        $bitmap.RotateFlip([System.Drawing.RotateFlipType]::RotateNoneFlipX)
    }

    return $bitmap
}

function Test-BlankImage {
    param([Parameter(Mandatory)] [System.Drawing.Bitmap] $Image)

    # Samples a coarse grid below the title bar. A populated video grid measured 9-14% for its most
    # common colour; a blank capture ~98%, and a window still opening its catalog 86%.
    $counts = @{}
    $total = 0
    for ($x = 10; $x -lt $Image.Width - 10; $x += 25) {
        for ($y = 60; $y -lt $Image.Height - 10; $y += 25) {
            $key = $Image.GetPixel($x, $y).ToArgb()
            $counts[$key] = 1 + [int] $counts[$key]
            $total += 1
        }
    }
    $largest = ($counts.Values | Measure-Object -Maximum).Maximum
    return ($largest / $total) -gt 0.5
}

$results = [System.Collections.Generic.List[pscustomobject]]::new()

Move-Item -LiteralPath $profileRoot -Destination $asideRoot
try {
    New-Item -ItemType Directory -Path $profileRoot | Out-Null
    # A capture needs a populated catalog, so the sandbox starts as a copy of the real profile rather
    # than empty. Everything written from here on lands on the copy.
    Copy-Item -LiteralPath (Join-Path $asideRoot 'catalog-state.json') -Destination $statePath
    Get-ChildItem -LiteralPath $asideRoot -Filter '*.png' -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $profileRoot $_.Name) }
    # The preview frames the grid shows. Without them every video tile falls back to its favicon.
    $previews = Join-Path $asideRoot 'grid-previews'
    if (-not (Test-Path -LiteralPath $previews)) {
        throw "No grid-previews folder under $asideRoot. Open the app in Grid mode on video channels once, so a capture has frames to show."
    }
    Copy-Item -LiteralPath $previews -Destination (Join-Path $profileRoot 'grid-previews') -Recurse

    foreach ($target in $targets) {
        Write-Host ("{0} ({1})" -f $target.ListingCode, $target.Language) -ForegroundColor Cyan
        Set-SandboxState -Language $target.Language

        $process = Start-Process -FilePath $exe -PassThru
        try {
            $handle = [IntPtr]::Zero
            for ($attempt = 0; $attempt -lt 40; $attempt += 1) {
                Start-Sleep -Milliseconds 500
                $process.Refresh()
                if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $process.MainWindowHandle; break }
            }
            if ($handle -eq [IntPtr]::Zero) { throw "The main window never appeared for $($target.ListingCode)." }

            # Sized to the Store canvas's 16:9, not maximized: a maximized window takes the monitor's
            # shape, and on anything but a 16:9 work area the canvas letterboxes it with black bars.
            [void] [CaptureWin32]::ShowWindow($handle, $SW_RESTORE)
            $work = [System.Windows.Forms.Screen]::FromHandle($handle).WorkingArea
            $width = [Math]::Min($work.Width, [int]($work.Height * 16 / 9))
            $height = [int]($width * 9 / 16)
            $SWP_NOZORDER = 0x0004
            [void] [CaptureWin32]::SetWindowPos($handle, [IntPtr]::Zero, $work.Left, $work.Top, $width, $height, $SWP_NOZORDER)
            [void] [CaptureWin32]::SetForegroundWindow($handle)
            Start-Sleep -Seconds $LoadWaitSeconds

            if (-not (Test-WindowLanguage -Handle $handle -Expected $expected[$target.ListingCode])) {
                throw ("The window is not in {0}: no control is named '{1}'. Nothing was written." -f
                    $target.Language, $expected[$target.ListingCode])
            }
            Write-Host ("    verified: a control is named '{0}'" -f $expected[$target.ListingCode])

            # PrintWindow returns an all-white client area when the display is off or the window has not
            # composed yet, and reports success. A 2026-09-23 run wrote nine such images before this check.
            $image = $null
            for ($try = 1; $try -le 3; $try += 1) {
                [void] [CaptureWin32]::SetForegroundWindow($handle)
                $image = Get-WindowImage -Handle $handle
                if (-not (Test-BlankImage -Image $image)) { break }
                $image.Dispose(); $image = $null
                Write-Host "    the capture came back blank - waiting and trying again" -ForegroundColor Yellow
                Start-Sleep -Seconds 5
            }
            if (-not $image) { throw "Every capture of $($target.ListingCode) came back blank. Is the display on and unlocked?" }
            try {
                $saved = Save-StoreCanvasImage -Image $image -Path (Join-Path $OutputDirectory "app-$($target.ListingCode).png")
            }
            finally { $image.Dispose() }

            $results.Add([pscustomobject]@{
                Listing = $target.ListingCode
                File    = [System.IO.Path]::GetFileName($saved.Path)
                Size    = ('{0}x{1}' -f $saved.Width, $saved.Height)
            })
            Write-Host ("    wrote {0} ({1}x{2})" -f $saved.Path, $saved.Width, $saved.Height) -ForegroundColor Green
        }
        finally {
            if (-not $process.HasExited) {
                [void] $process.CloseMainWindow()
                Start-Sleep -Seconds 2
                if (-not $process.HasExited) { $process.Kill() }
            }
            Start-Sleep -Seconds 1
        }
    }
}
finally {
    if (Test-Path -LiteralPath $profileRoot) { Remove-Item -LiteralPath $profileRoot -Recurse -Force }
    Move-Item -LiteralPath $asideRoot -Destination $profileRoot
    $restoredHash = (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash
    if ($restoredHash -eq $realStateHash) {
        Write-Host "Real profile restored, catalog-state.json unchanged." -ForegroundColor Green
    } else {
        Write-Host "The restored catalog-state.json hash does not match what was recorded before the run." -ForegroundColor Red
        Write-Host ("  before {0}`n  after  {1}" -f $realStateHash, $restoredHash) -ForegroundColor Red
        throw 'The real state file changed during the run. Investigate before trusting these images.'
    }
}

Write-Host ""
$results | Format-Table -AutoSize
$sizes = @($results | ForEach-Object { $_.Size } | Sort-Object -Unique)
Write-Host ("{0} image(s), {1} distinct size(s): {2}" -f $results.Count, $sizes.Count, ($sizes -join ', ')) -ForegroundColor Green
