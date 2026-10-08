#requires -Version 7.0
<#
.SYNOPSIS
    SP-0198: fixture tests for the site-facts and positioning gate (SiteFacts.ps1).

.DESCRIPTION
    Builds a throwaway tree with a facts declaration, a positioning source, a few surfaces and three small copy
    decks, then runs the real gate against it:

      passes     a consistent tree; a README that names the pillars among other words; a translated key line
                 whose untranslated term is in place; a route named by its command marker; a Windows 11 minimum
      fails      a minimum Windows the MSIX manifest or the winget manifest states differently, or that is not a
                 Windows 10 build number; a pattern with no match; a channel whose packaging file or whose
                 mention in it is gone; a channel card the declaration does not list, or one it lists and the deck
                 lacks; a fact key that lost its placeholder or carries a typed number; a title that carries a
                 placeholder; a prose list that dropped a route; a surface that misses a pillar, names two out of
                 order, or does not exist; a key line with the wrong count, order or untranslated term; a missing
                 or malformed pillar table; a missing or unparseable declaration

    Exit 0 when every check passes, 1 otherwise. The last line is `site-facts: PASS|FAIL (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/SiteFacts.ps1"

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("site-facts-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null

$utf8 = New-Object System.Text.UTF8Encoding($false)
function Write-Fixture([string] $Path, [string] $Content) {
    $full = Join-Path $fixture $Path
    New-Item -ItemType Directory -Force -Path (Split-Path $full -Parent) | Out-Null
    [System.IO.File]::WriteAllText($full, $Content, $utf8)
}
function Set-Fixture([string] $Path, [string] $Old, [string] $New) {
    $full = Join-Path $fixture $Path
    $text = [System.IO.File]::ReadAllText($full, $utf8)
    if (-not $text.Contains($Old)) { throw "fixture $Path does not contain '$Old'" }
    [System.IO.File]::WriteAllText($full, $text.Replace($Old, $New), $utf8)
}
function Has([string[]] $Problems, [string] $Pattern) { return @($Problems | Where-Object { $_ -match $Pattern }).Count -gt 0 }

function New-Deck {
    return [ordered]@{
        'title-home'               = 'Player'
        'description-home'         = 'A player.'
        'hero-eyebrow'             = '[[windows]]'
        'fact-windows'             = '[[windows]]'
        'fact-langs'               = '[[languages]] interface languages'
        'cap-yours-4'              = 'Switch between [[languages]] languages.'
        'hero-kicker'              = 'Internet radio · Live video · RTSP'
        'what-p1'                  = 'An app for internet radio, live video, and RTSP cameras.'
        'channel-setup'            = 'Installer (.exe)'
        'channel-setup-note'       = 'Not signed.'
        'channel-github'           = 'Portable ZIP'
        'channel-store'            = 'Microsoft Store'
        'channel-winget'           = 'winget'
        'status-description'       = 'Run the installer, get it on the Microsoft Store, take the ZIP, or use winget.'
        'use-description'          = 'Install it from the Microsoft Store, with winget, or unpack the ZIP.'
        'distribution-description' = 'Run the installer, the Microsoft Store, the ZIP, or install with [[command]].'
    }
}
function New-Decks {
    $ru = New-Deck
    $ru['hero-kicker'] = 'Интернет-радио · Live-видео · RTSP'
    $ru['what-p1'] = 'Приложение для интернет-радио, live-видео и RTSP-камер.'
    $de = New-Deck
    $de['hero-kicker'] = 'Internetradio · Live-Video · RTSP'
    return [ordered]@{ en = (New-Deck); ru = $ru; de = $de }
}
function Get-Problems { param($Decks = (New-Decks)) return ,@(Get-SiteFactProblem -Root $fixture -Deck $Decks) }

function Write-Tree {
    Write-Fixture 'tools/site/site-facts.json' @'
{
  "positioning": {
    "source": "POSITIONING.md",
    "surfaces": [
      { "path": "README.md", "locale": "en" },
      { "path": "README.ru.md", "locale": "ru" }
    ],
    "deckLead": { "key": "what-p1", "locales": ["en", "ru"] },
    "deckKeyLine": { "key": "hero-kicker", "separator": "·" }
  },
  "minimumWindows": {
    "source": { "path": "app.csproj", "pattern": "<SupportedOSPlatformVersion>([^<]+)</SupportedOSPlatformVersion>" },
    "agree": [
      { "path": "AppxManifest.xml", "pattern": "MinVersion=\"([^\"]+)\"" },
      { "path": "installer.yaml", "pattern": "(?m)^MinimumOSVersion:\\s*(\\S+)" }
    ]
  },
  "channels": [
    { "id": "setup", "deckKey": "channel-setup", "evidence": [ { "path": "setup.iss" }, { "path": "release.yml", "contains": "-setup.exe" } ] },
    { "id": "portable-zip", "deckKey": "channel-github", "evidence": [ { "path": "release.yml", "contains": ".zip" } ] },
    { "id": "store", "deckKey": "channel-store", "name": "Microsoft Store", "evidence": [ { "path": "AppxManifest.xml" } ] },
    { "id": "winget", "deckKey": "channel-winget", "name": "winget", "orMarker": "[[command]]", "evidence": [ { "path": "installer.yaml" } ] }
  ],
  "channelProse": ["status-description", "use-description", "distribution-description"],
  "renderedKeys": {
    "languages": ["fact-langs", "cap-yours-4"],
    "windows": ["hero-eyebrow", "fact-windows"]
  }
}
'@
    Write-Fixture 'POSITIONING.md' @'
# Positioning

## 1. What it is

A player.

## 2. The pillars

| # | Pillar | What it covers | en | ru | uk |
| --- | --- | --- | --- | --- | --- |
| 1 | Internet radio | Radio | Internet radio | Интернет-радио | Інтернет-радіо |
| 2 | Live video | Video | Live video | Live-видео | Live-відео |
| 3 | RTSP | Cameras | RTSP | RTSP | RTSP |

## 3. Elsewhere

None.
'@
    Write-Fixture 'README.md' "<p>Internet radio, live video, and RTSP for Windows.</p>`nMore words, then RTSP again.`n"
    Write-Fixture 'README.ru.md' "<p>Интернет-радио, live-видео и RTSP для Windows.</p>`n"
    Write-Fixture 'app.csproj' '<Project><PropertyGroup><SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion></PropertyGroup></Project>'
    Write-Fixture 'AppxManifest.xml' '<Package><Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" /></Dependencies></Package>'
    Write-Fixture 'installer.yaml' "PackageIdentifier: X.Y`nMinimumOSVersion: 10.0.17763.0`n"
    Write-Fixture 'setup.iss' '[Setup]'
    Write-Fixture 'release.yml' "files: Player-setup.exe Player.zip`n"
}

try {
    Write-Tree

    $problems = Get-Problems
    Check ($problems.Count -eq 0) "consistent tree passes (got: $($problems -join ' | '))"

    # --- rendering
    $value = Get-SiteFactValue -Root $fixture -LanguageCount 13
    Check ($value.Languages -eq 13) 'the language count is the registry count'
    Check ($value.Windows -eq 'Windows 10 / 11') 'a Windows 10 build minimum renders as Windows 10 / 11'
    Set-Fixture 'app.csproj' '10.0.17763.0' '10.0.22000.0'
    Set-Fixture 'AppxManifest.xml' '10.0.17763.0' '10.0.22000.0'
    Set-Fixture 'installer.yaml' '10.0.17763.0' '10.0.22000.0'
    Check ((Get-SiteFactValue -Root $fixture -LanguageCount 13).Windows -eq 'Windows 11') 'a Windows 11 build minimum renders as Windows 11'
    Check ((Get-Problems).Count -eq 0) 'a consistent Windows 11 minimum passes'
    Set-Fixture 'app.csproj' '10.0.22000.0' '6.3.9600.0'
    $threw = $false
    try { [void] (Get-SiteFactValue -Root $fixture -LanguageCount 13) } catch { $threw = $true }
    Check $threw 'a minimum that is not a Windows 10 build number cannot be rendered'
    Check (Has (Get-Problems) 'not a Windows 10 or later build number') 'a minimum that is not a Windows 10 build number fails the gate'
    Write-Tree

    # --- source
    Set-Fixture 'AppxManifest.xml' '10.0.17763.0' '10.0.19041.0'
    Check (Has (Get-Problems) 'AppxManifest\.xml says 10\.0\.19041\.0, app\.csproj says 10\.0\.17763\.0') 'an MSIX minimum that differs fails'
    Write-Tree
    Set-Fixture 'installer.yaml' 'MinimumOSVersion: 10.0.17763.0' 'MinimumOSVersion: 10.0.18362.0'
    Check (Has (Get-Problems) 'installer\.yaml says 10\.0\.18362\.0') 'a winget minimum that differs fails'
    Write-Tree
    Set-Fixture 'app.csproj' 'SupportedOSPlatformVersion' 'SupportedOS'
    Check (Has (Get-Problems) 'app\.csproj has no match') 'a source pattern with no match fails'
    Write-Tree

    # --- channels
    Remove-Item (Join-Path $fixture 'setup.iss')
    Check (Has (Get-Problems) "channel 'setup': setup\.iss does not exist") 'a channel without its packaging file fails'
    Write-Tree
    Set-Fixture 'release.yml' '.zip' '.tar'
    Check (Has (Get-Problems) "channel 'portable-zip': release\.yml no longer mentions") 'a channel whose build step is gone fails'
    Write-Tree
    $decks = New-Decks; $decks['en']['channel-chocolatey'] = 'Chocolatey'
    Check (Has (Get-Problems $decks) 'channel-chocolatey that site-facts\.json does not declare') 'a channel card the declaration does not list fails'
    $decks = New-Decks; $decks['en'].Remove('channel-store')
    Check (Has (Get-Problems $decks) "channel 'store': the English deck has no channel-store") 'a declared channel the deck lacks fails'
    $decks = New-Decks; $decks['ru']['use-description'] = 'Install it with winget or unpack the ZIP.'
    Check (Has (Get-Problems $decks) '\[ru\] use-description does not name the store channel') 'a prose list that dropped a route fails'
    $decks = New-Decks; $decks['de']['distribution-description'] = 'The ZIP, or the Microsoft Store.'
    Check (Has (Get-Problems $decks) '\[de\] distribution-description does not name the winget channel') 'a prose list without the winget name or its command fails'
    $decks = New-Decks; $decks['de']['distribution-description'] = 'The ZIP, the Microsoft Store, or winget.'
    Check ((Get-Problems $decks).Count -eq 0) 'a route named by its word instead of its command passes'

    # --- rendered
    $decks = New-Decks; $decks['de']['fact-langs'] = '13 Sprachen'
    Check (Has (Get-Problems $decks) '\[de\] fact-langs does not carry \[\[languages\]\]') 'a typed language count fails'
    $decks = New-Decks; $decks['en']['fact-windows'] = 'Windows 10 / 11'
    Check (Has (Get-Problems $decks) '\[en\] fact-windows does not carry \[\[windows\]\]') 'a typed Windows version fails'
    $decks = New-Decks; $decks['ru'].Remove('cap-yours-4')
    Check (Has (Get-Problems $decks) '\[ru\] cap-yours-4 is missing') 'a missing fact key fails'
    $decks = New-Decks; $decks['de']['title-home'] = 'Player for [[windows]]'
    Check (Has (Get-Problems $decks) '\[de\] title-home carries a fact placeholder') 'a title that carries a fact placeholder fails'

    # --- pillars
    Write-Fixture 'README.md' "<p>Internet radio and live video for Windows.</p>`n"
    Check (Has (Get-Problems) "README\.md: pillar 3 'RTSP' is missing") 'a surface that misses a pillar fails'
    Write-Fixture 'README.md' "<p>RTSP, live video, internet radio.</p>`n"
    Check (Has (Get-Problems) "README\.md: pillar 2 'Live video' is out of order") 'a surface that names the pillars out of order fails'
    Write-Fixture 'README.md' "<p>Live video, internet radio, and RTSP.</p>`nLater, correctly: internet radio, live video, RTSP.`n"
    Check (Has (Get-Problems) "README\.md: pillar 2 'Live video' is out of order") 'a swapped lead line is not hidden by a later correct mention'
    Write-Fixture 'README.md' "<p>Internet radio, live video, and RTSP.</p>`n"
    Remove-Item (Join-Path $fixture 'README.ru.md')
    Check (Has (Get-Problems) 'README\.ru\.md does not exist') 'a positioning surface that does not exist fails'
    Write-Fixture 'README.ru.md' "<p>Интернет-радио, live-видео и RTSP.</p>`n"
    $decks = New-Decks; $decks['ru']['what-p1'] = 'Приложение для интернет-радио и RTSP.'
    Check (Has (Get-Problems $decks) '\[ru\] what-p1: pillar 2') 'a lead sentence that misses a pillar fails'

    $decks = New-Decks; $decks['de']['hero-kicker'] = 'Internetradio · RTSP'
    Check (Has (Get-Problems $decks) '\[de\] hero-kicker lists 2 items, POSITIONING\.md has 3 pillars') 'a key line with the wrong count fails'
    $decks = New-Decks; $decks['en']['hero-kicker'] = 'Live video · Internet radio · RTSP'
    Check (Has (Get-Problems $decks) "\[en\] hero-kicker item 1 is 'Live video'") 'a key line in the wrong order fails'
    $decks = New-Decks; $decks['de']['hero-kicker'] = 'Internetradio · Live-Video · RTSP-Kameras'
    Check (Has (Get-Problems $decks) "\[de\] hero-kicker item 3 is 'RTSP-Kameras', POSITIONING\.md says 'RTSP'") 'a translated key line that changes an untranslated term fails'
    $decks = New-Decks; $decks['de']['hero-kicker'] = 'Rundfunk · Bewegtbild · RTSP'
    Check ((Get-Problems $decks).Count -eq 0) 'a translated key line with its own words and the untranslated term passes'

    # --- the pillar table
    Set-Fixture 'POSITIONING.md' '| 2 | Live video |' '| 3 | Live video |'
    Check (Has (Get-Problems) 'pillar 2 is numbered ''3''') 'a pillar table that skips a number fails'
    Write-Tree
    Write-Fixture 'POSITIONING.md' "# Positioning`n`n## 2. The pillars`n`nNone yet.`n"
    Check (Has (Get-Problems) 'holds no pillar table') 'a pillar section without a table fails'
    Write-Tree
    Set-Fixture 'POSITIONING.md' '## 2. The pillars' '## Pillars'
    Check (Has (Get-Problems) "has no '## 2\.' pillar section") 'a missing pillar section fails'
    Write-Tree
    Set-Fixture 'POSITIONING.md' '| # | Pillar | What it covers | en | ru | uk |' '| # | Pillar | What it covers | en | ru |'
    Check (Has (Get-Problems) "has no 'uk' column") 'a pillar table without a column fails'
    Write-Tree
    Remove-Item (Join-Path $fixture 'POSITIONING.md')
    Check (Has (Get-Problems) 'POSITIONING\.md does not exist') 'a missing positioning source fails'
    Write-Tree

    # --- the declaration
    Write-Fixture 'tools/site/site-facts.json' '{ not json'
    Check (Has (Get-Problems) 'not valid JSON') 'an unparseable declaration fails'
    Remove-Item (Join-Path $fixture 'tools/site/site-facts.json')
    Check (Has (Get-Problems) 'does not exist') 'a missing declaration fails'
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) {
    Write-Output "site-facts: FAIL ($($failures.Count) of $($passed + $failures.Count))"
    exit 1
}
Write-Output "site-facts: PASS ($passed checks)"
exit 0
