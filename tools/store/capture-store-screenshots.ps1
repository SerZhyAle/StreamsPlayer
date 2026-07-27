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

    This needs a real desktop, a stable screen and a populated catalog, so it cannot run in CI.

    Usage:
      pwsh -NoProfile -File tools/store/capture-store-screenshots.ps1
      # pwsh -File takes array values space-separated, not comma-separated:
      pwsh -NoProfile -File tools/store/capture-store-screenshots.ps1 -Languages de ar
#>
[CmdletBinding()]
param(
    # Listing codes (en-us, pt-br, zh-hans, ..). Defaults to every shipped language.
    [string[]] $Languages,
    [int] $LoadWaitSeconds = 6,
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/../InterfaceLanguages.ps1"
. "$PSScriptRoot/StoreCanvas.ps1"

$root = Get-RepositoryRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'assets/store' }

Add-Type -AssemblyName System.Drawing
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
}
'@

$SW_RESTORE = 9
$SW_MAXIMIZE = 3
$GWL_EXSTYLE = -20
$WS_EX_LAYOUTRTL = 0x00400000
# PrintWindow copies the window's own content instead of whatever pixels happen to be on screen, so a
# tooltip, a notification or another window cannot land in the shot. PW_RENDERFULLCONTENT is required
# for a composited (WPF) window - without it the client area comes back blank.
$PW_RENDERFULLCONTENT = 2

# The automation name used to prove the window really is in the requested language. Its expected value
# is read from that language's dictionary, never written here.
$VerificationKey = 'LanguagePickerName'

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

if (-not (Test-Path -LiteralPath $statePath)) {
    throw "No catalog-state.json under $profileRoot. Open the app and refresh the catalog once, so a capture has content to show."
}
if (Test-Path -LiteralPath $asideRoot) {
    throw "$asideRoot already exists - a previous run did not finish. Check its contents, move it back to $profileRoot by hand, and run again."
}

$realStateHash = (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash
Write-Host ("Real state: {0} (SHA256 {1})" -f $statePath, $realStateHash.Substring(0, 16))

function Set-SandboxLanguage {
    param([Parameter(Mandatory)] [string] $Language)

    $text = [System.IO.File]::ReadAllText($statePath)

    if ($text -match '"language"\s*:') {
        $text = [regex]::Replace($text, '("language"\s*:\s*)(?:"[^"]*"|null)', ('${1}"' + $Language + '"'))
    } else {
        # A state file written by a build that never recorded a language has no property to replace.
        $text = [regex]::Replace($text, '^\s*\{', ('{"language":"' + $Language + '",'), 1)
    }
    # Stop the app restoring and auto-playing the last channel, which would put the player window in
    # front of the catalog.
    $text = [regex]::Replace($text, '("lastSelectedChannelId"\s*:\s*)(?:"[^"]*"|null)', '${1}null')

    # Atomic, matching StreamCatalogStore: write a temp file, then move it over the target.
    $temp = "$statePath.capture-tmp"
    [System.IO.File]::WriteAllText($temp, $text, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::Move($temp, $statePath, $true)

    # Read it back. The predecessor of this script assumed its edit had landed; it had not, and every
    # image it produced was wrong. An unverified write is the whole defect.
    $stored = (Get-Content -LiteralPath $statePath -Raw -Encoding utf8 | ConvertFrom-Json).language
    if ($stored -ne $Language) {
        throw "The sandbox state still reports language '$stored' after asking for '$Language'. Refusing to capture."
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
        throw "The window is $width x $height, too small to be the maximized main window."
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

$results = [System.Collections.Generic.List[pscustomobject]]::new()

Move-Item -LiteralPath $profileRoot -Destination $asideRoot
try {
    New-Item -ItemType Directory -Path $profileRoot | Out-Null
    # A capture needs a populated catalog, so the sandbox starts as a copy of the real profile rather
    # than empty. Everything written from here on lands on the copy.
    Copy-Item -LiteralPath (Join-Path $asideRoot 'catalog-state.json') -Destination $statePath
    Get-ChildItem -LiteralPath $asideRoot -Filter '*.png' -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $profileRoot $_.Name) }

    foreach ($target in $targets) {
        Write-Host ("{0} ({1})" -f $target.ListingCode, $target.Language) -ForegroundColor Cyan
        Set-SandboxLanguage -Language $target.Language

        $process = Start-Process -FilePath $exe -PassThru
        try {
            $handle = [IntPtr]::Zero
            for ($attempt = 0; $attempt -lt 40; $attempt += 1) {
                Start-Sleep -Milliseconds 500
                $process.Refresh()
                if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $process.MainWindowHandle; break }
            }
            if ($handle -eq [IntPtr]::Zero) { throw "The main window never appeared for $($target.ListingCode)." }

            [void] [CaptureWin32]::ShowWindow($handle, $SW_RESTORE)
            [void] [CaptureWin32]::ShowWindow($handle, $SW_MAXIMIZE)
            [void] [CaptureWin32]::SetForegroundWindow($handle)
            Start-Sleep -Seconds $LoadWaitSeconds

            if (-not (Test-WindowLanguage -Handle $handle -Expected $expected[$target.ListingCode])) {
                throw ("The window is not in {0}: no control is named '{1}'. Nothing was written." -f
                    $target.Language, $expected[$target.ListingCode])
            }
            Write-Host ("    verified: a control is named '{0}'" -f $expected[$target.ListingCode])

            $image = Get-WindowImage -Handle $handle
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
