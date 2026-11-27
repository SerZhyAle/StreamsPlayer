#Requires -Version 7.0
<#
    Capture help pictures and live WPF bounds from a Windows release publish.
    Run without -Check to replace the generated shot set and render the offline help.
    -Check compares a fresh capture with the committed pictures and bounds without writing them.
    Exit codes: 0 pass, 1 stale or missing outputs, 2 could not capture or render.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HelpShots.ps1')

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$page = 'external-control'
$contentRoot = Join-Path $root 'help/content'
$locales = @(Get-ChildItem -LiteralPath $contentRoot -Directory | Sort-Object Name | ForEach-Object Name)
$images = Join-Path $root 'docs/help'
$metadata = Join-Path $root "help/shots/$page.json"
$scratch = $null

try {
    if (!$IsWindows) { throw 'Run this command on Windows with a published StreamsPlayer.exe.' }
    $exePath = (Resolve-Path -LiteralPath $Exe).ProviderPath
    if ([IO.Path]::GetFileName($exePath) -ine 'StreamsPlayer.exe') { throw 'Pass the published StreamsPlayer.exe to -Exe.' }

    $requests = @(foreach ($locale in $locales) {
        $copy = Get-Content -LiteralPath (Join-Path $contentRoot "$locale/$page.json") -Raw | ConvertFrom-Json
        $markers = @(foreach ($entry in $copy.elements) { @{ id = $entry.id; control = $entry.control } })
        if (!$markers.Count) { throw "$locale/$page has no controls to capture." }
        @{ locale = $locale; markers = $markers }
    })

    $scratch = Join-Path $root ('temp/help-capture/' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    ConvertTo-Json -InputObject $requests -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'request.json') -Encoding utf8
    $previous = $env:STREAMSPLAYER_HELP_CAPTURE_DIR
    try {
        $env:STREAMSPLAYER_HELP_CAPTURE_DIR = $scratch
        $process = Start-Process -FilePath $exePath -ArgumentList 'render-help-capture' -WindowStyle Hidden -Wait -PassThru
    } finally {
        $env:STREAMSPLAYER_HELP_CAPTURE_DIR = $previous
    }

    $resultFile = Join-Path $scratch 'result.json'
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultFile -PathType Leaf)) {
        $errorFile = Join-Path $scratch 'error.txt'
        $detail = if (Test-Path -LiteralPath $errorFile -PathType Leaf) { (Get-Content -LiteralPath $errorFile -Raw).Trim() }
            else { 'Use a current release publish with the help capture entry point.' }
        throw "StreamsPlayer.exe exited $($process.ExitCode). $detail"
    }

    $generated = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    $generated | Add-Member -NotePropertyName date -NotePropertyValue ([datetime]::Today.ToString('yyyy-MM-dd', [cultureinfo]::InvariantCulture))
    foreach ($shot in $generated.shots) {
        $name = Get-HelpShotName $page $shot.locale $shot.theme
        $hash = (Get-FileHash -LiteralPath (Join-Path $scratch $name) -Algorithm SHA256).Hash
        $shot | Add-Member -NotePropertyName sha256 -NotePropertyValue $hash
        $request = $requests | Where-Object { $_.locale -ceq $shot.locale }
        if (($shot.markers.id -join ',') -cne ($request.markers.id -join ',')) {
            throw "Capture $name does not match the requested controls."
        }
    }
    Assert-HelpShotSet $generated $page $locales $scratch

    if ($Check) {
        try {
            $committed = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
            Assert-HelpShotSet $committed $page $locales $images
            $actual = ConvertTo-Json -InputObject $generated.shots -Depth 8 -Compress
            $expected = ConvertTo-Json -InputObject $committed.shots -Depth 8 -Compress
            if ($actual -cne $expected) { throw 'The generated pixels or marker bounds changed.' }
        } catch {
            Write-Host "help-capture: FAIL ($($_.Exception.Message) Regenerate with the release publish.)"
            exit 1
        }
    } else {
        New-Item -ItemType Directory -Path (Split-Path $metadata -Parent), $images -Force | Out-Null
        foreach ($shot in $generated.shots) {
            $name = Get-HelpShotName $page $shot.locale $shot.theme
            Copy-Item -LiteralPath (Join-Path $scratch $name) -Destination (Join-Path $images $name) -Force
        }
        ConvertTo-Json -InputObject $generated -Depth 8 | Set-Content -LiteralPath $metadata -Encoding utf8
        pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-help.ps1')
        if ($LASTEXITCODE -ne 0) { throw "The help renderer exited $LASTEXITCODE." }
    }
    Write-Host "help-capture: PASS ($($generated.shots.Count) pictures and their markers)"
    exit 0
} catch {
    Write-Host "help-capture: COULD NOT VERIFY ($($_.Exception.Message -replace '\s+', ' '))"
    exit 2
} finally {
    if ($scratch -and (Test-Path -LiteralPath $scratch -PathType Container)) {
        Get-ChildItem -LiteralPath $scratch -File | Remove-Item -Force
        [IO.Directory]::Delete($scratch)
    }
}
