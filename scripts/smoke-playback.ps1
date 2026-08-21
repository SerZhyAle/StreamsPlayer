<#
.SYNOPSIS
  Pre-release gate: prove a built StreamsPlayer actually plays - audio and video, from a real network.

.DESCRIPTION
  This exists because of SP-0093, and it is worth stating plainly why 858 unit tests could not have
  caught that defect. They cover this repository's own code - catalog parsing, merge, state,
  localization - and none of it was wrong. What broke was the WPF runtime underneath: 10.0.11 refuses
  every Internet-zone http(s) media URI before Media Foundation is reached, so the application
  started, listed 18,908 channels, rendered its grid, and played nothing at all. Build succeeded.
  Tests passed. A release shipped, and no user could hear a thing.

  No unit test can catch that class of defect, because the defect is not in the unit. The only check
  that can is the one nobody had run: start the thing that will actually be shipped, point it at a
  real station, and confirm media came out. That is all this script does.

  Two rounds, because the product has two independent media stacks and a green one says nothing
  about the other:
    audio -> WPF MediaElement (the stack SP-0093 broke)
    video -> LibVLC, loaded from native DLLs beside the executable (the stack a packaging mistake
             breaks - the natives are ~40% of the payload and have been mis-copied before)

  Deliberately NOT part of scripts/check.ps1: that gate must stay offline and deterministic so CI can
  run it. This one needs the network, a desktop session and a working audio device, so it runs on the
  owner's machine before a release - release.ps1 step 2b.

.PARAMETER AppPath
  StreamsPlayer.exe to test. Defaults to publishing the current tree into artifacts/smoke, because
  the thing worth testing is publish output: that is the shape that ships, and SP-0093 lived in the
  runtime a publish selects, not in any source file.

.PARAMETER AudioUrl
.PARAMETER VideoUrl
  Sources to try. A round passes when ANY of its URLs plays: one dead stream is a fact about that
  stream, while every stream failing is a fact about the build.

.PARAMETER TimeoutSeconds
  Per-source budget. Catalog load alone takes ~30 s against a populated state file and playback is
  only attempted afterwards, so do not lower this below about 60 without measuring first.

.PARAMETER SkipVideo
  Run the audio round only. For a machine with no usable video output; not for a release.

.EXAMPLE
  pwsh -NoProfile -File ./scripts/smoke-playback.ps1
  pwsh -NoProfile -File ./scripts/smoke-playback.ps1 -AppPath 'C:\...\StreamsPlayer.exe'
#>
[CmdletBinding()]
param(
    [string] $AppPath,
    [string[]] $AudioUrl = @(
        'https://0n-60s.radionetz.de/0n-60s.mp3',
        'http://ice1.somafm.com/groovesalad-128-mp3'
    ),
    [string[]] $VideoUrl = @(
        'https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8'
    ),
    [int] $TimeoutSeconds = 90,
    [switch] $SkipVideo
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Good([string] $Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Bad([string] $Message) { Write-Host "    $Message" -ForegroundColor Red }

# One source: launch, watch the log for this round's LIVE or FAIL event, return a verdict record.
function Invoke-PlaybackProbe {
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [Parameter(Mandatory)] [string] $Url,
        [Parameter(Mandatory)] [string] $Marker,
        [Parameter(Mandatory)] [string] $Log,
        [Parameter(Mandatory)] [int] $Budget
    )

    # A running instance swallows the launch: StreamsPlayer is single-instance, so a second process
    # hands off and contributes nothing to the log. The symptom is an ordinary-looking session with no
    # event at all - indistinguishable from the bug this gate hunts, which is why it is closed here
    # rather than assumed absent.
    $stale = @(Get-Process StreamsPlayer -ErrorAction SilentlyContinue)
    if ($stale) {
        Write-Host "    closing $($stale.Count) running instance(s) first"
        $stale | Stop-Process -Force
        Start-Sleep -Seconds 3
    }

    # Only trust lines newer than the launch. The app retires Current.log on startup, but a timestamp
    # floor makes that an assumption this script does not have to rely on.
    $since = [DateTimeOffset]::UtcNow.AddSeconds(-5)
    $proc = Start-Process $Exe -ArgumentList '--url', $Url -PassThru

    $verdict = ''
    $detail = ''
    $deadline = [DateTime]::UtcNow.AddSeconds($Budget)
    while ([DateTime]::UtcNow -lt $deadline -and -not $verdict) {
        Start-Sleep -Seconds 3
        if ($proc.HasExited) { $verdict = 'EXITED'; break }
        if (-not (Test-Path $Log)) { continue }
        foreach ($line in (Get-Content $Log -ErrorAction SilentlyContinue)) {
            if ($line -match "^(\S+) \[Diag\] $Marker (LIVE|FAIL)") {
                [DateTimeOffset] $stamp = [DateTimeOffset]::MinValue
                if (-not [DateTimeOffset]::TryParse($Matches[1], [ref] $stamp)) { continue }
                if ($stamp -lt $since) { continue }
                $verdict = $Matches[2]
                $detail = $line
            }
        }
    }

    $modules = @()
    if (-not $proc.HasExited) {
        $proc.Refresh()
        $modules = @($proc.Modules | Where-Object { $_.ModuleName -match '^(mf|libvlc)' } |
            ForEach-Object ModuleName | Sort-Object)
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }

    if (-not $verdict) { $verdict = 'SILENT' }
    [pscustomobject]@{ Url = $Url; Verdict = $verdict; Detail = $detail; Modules = $modules }
}

Push-Location $root
try {
    if (-not $AppPath) {
        $out = Join-Path $root 'artifacts/smoke'
        Write-Step "Publishing to $out"
        dotnet publish src/StreamsPlayer.App/StreamsPlayer.App.csproj -c Release -r win-x64 `
            --self-contained true -o $out --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
        $AppPath = Join-Path $out 'StreamsPlayer.exe'
    }
    if (-not (Test-Path $AppPath)) { throw "Not found: $AppPath" }
    $AppPath = (Resolve-Path $AppPath).Path
    $appDir = Split-Path $AppPath -Parent

    # Reported alongside the verdict, never used to decide it - SP-0093's whole lesson is that a
    # version number is not evidence. But when this gate goes red it is the first thing anyone asks.
    $wpf = Get-Item (Join-Path $appDir 'PresentationCore.dll') -ErrorAction SilentlyContinue
    Write-Step "Testing $AppPath"
    Write-Host "    app = $((Get-Item $AppPath).VersionInfo.FileVersion)"
    Write-Host "    WPF = $(if ($wpf) { $wpf.VersionInfo.FileVersion } else { 'framework-dependent' })"

    $log = Join-Path $env:LOCALAPPDATA 'StreamsPlayer/Current.log'
    $rounds = @(
        [pscustomobject]@{ Name = 'audio'; Marker = 'AUDIO'; Stack = 'WPF MediaElement'; Urls = $AudioUrl }
    )
    if (-not $SkipVideo) {
        $rounds += [pscustomobject]@{ Name = 'video'; Marker = 'PLAYBACK'; Stack = 'LibVLC'; Urls = $VideoUrl }
    }
    else { Write-Host '    video round skipped by request - not a valid state for a release' -ForegroundColor Yellow }

    $failed = @()
    foreach ($round in $rounds) {
        Write-Step "Round: $($round.Name) - $($round.Stack)"
        $passed = $false
        $attempts = @()
        foreach ($url in $round.Urls) {
            Write-Host "    trying $url"
            $probe = Invoke-PlaybackProbe -Exe $AppPath -Url $url -Marker $round.Marker -Log $log -Budget $TimeoutSeconds
            $attempts += $probe
            if ($probe.Verdict -eq 'LIVE') {
                Write-Good "expected: $($round.Marker) LIVE | actual: $($round.Marker) LIVE"
                Write-Good "native modules: $($probe.Modules -join ', ')"
                $passed = $true
                break
            }
            switch ($probe.Verdict) {
                'FAIL'   { Write-Bad "$($round.Marker) FAIL: $($probe.Detail)" }
                'EXITED' { Write-Bad 'the application exited before playing anything' }
                default  { Write-Bad "no $($round.Marker) event within ${TimeoutSeconds}s (modules: $($probe.Modules -join ', '))" }
            }
        }
        if (-not $passed) { $failed += [pscustomobject]@{ Round = $round; Attempts = $attempts } }
    }

    Write-Host ''
    if (-not $failed) {
        Write-Host 'Playback smoke check PASSED - audio and video both played.' -ForegroundColor Green
        exit 0
    }

    foreach ($f in $failed) {
        Write-Bad "expected: $($f.Round.Marker) LIVE from at least one source | actual: none played ($($f.Round.Stack))"
        $f.Attempts | ForEach-Object { Write-Bad "  $($_.Url) -> $($_.Verdict) $($_.Detail)" }
    }
    Write-Host ''
    Write-Host @'
Every source in a round failed, so this is the build and not one bad stream. Cheapest checks first:

  1. Play one of the failing URLs in a browser. If that fails too, the machine or the network is the
     problem and this result says nothing about the build.
  2. Audio round red: look for "Only site-of-origin pack URIs are supported for media" in the log.
     That is WPF refusing Internet-zone media (SP-0093, dotnet/wpf#11856), not a broken stream. The
     fix is the AppContext switch or the runtime pin, both written up in SP-0093.
  3. Video round red while audio is green: suspect the LibVLC natives. Confirm libvlc\win-x64 exists
     beside the executable and is populated - packaging has dropped it before.
  4. No event at all in either round: the launch was probably swallowed. Confirm nothing else was
     running and that the argument form is `--url <value>`, two arguments - a bare URL parses as
     Invalid and is silently ignored.

Do not release on a red result here. This gate exists because a release already shipped that built
clean, tested clean, and played nothing.
'@ -ForegroundColor Yellow
    exit 1
}
finally { Pop-Location }
