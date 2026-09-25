<#
.SYNOPSIS
  Pre-release gate: prove a built StreamsPlayer actually plays - audio and video, from a real network.

.DESCRIPTION
  This exists because of SP-0093, and it is worth stating plainly why 858 unit tests could not have
  caught that defect. They cover this repository's own code - catalog parsing, merge, state,
  localization - and none of it was wrong. What broke was the WPF runtime underneath: 10.0.11 refused
  every Internet-zone http(s) media URI before Media Foundation was reached, so the application
  started, listed 18,908 channels, rendered its grid, and played nothing at all. Build succeeded.
  Tests passed. A release shipped, and no user could hear a thing.

  No unit test can catch that class of defect, because the defect is not in the unit. The only check
  that can is the one nobody had run: start the thing that will actually be shipped, point it at a
  real station, and confirm media came out. That is all this script does.

  Two rounds, because the product has two independent media paths and a green one says nothing
  about the other:
    audio -> the audio-only LibVLC engine (SP-0104; it replaced WPF MediaElement, the stack SP-0093 broke)
    video -> LibVLC, loaded from native DLLs beside the executable (the stack a packaging mistake
             breaks - the natives are ~40% of the payload and have been mis-copied before)

  SP-0133 made the verdict mean what it says:
    - A round passes on real output, not on a state the engine reports first. Radio needs AUDIO HEARD -
      the audio output has played a buffer - not AUDIO LIVE, which the engine raises before a sample is
      decoded. Video needs PLAYBACK SHOWN - the engine's displayed-picture counter moved - not PLAYBACK
      LIVE, which is a full buffer. A package missing its output or codec plugins reaches both LIVE lines.
    - libvlc.dll and libvlccore.dll must be loaded from the published folder under test. A copy found
      anywhere else means the round proved some other package's natives.
    - Every probe runs against a fresh profile of its own (STREAMSPLAYER_DATA_DIRECTORY, under
      artifacts/smoke-profile). The owner's running copy is never closed - SP-0118's instance identity
      makes a relocated profile an instance of its own - and the owner's session logs are never rotated;
      the gate checks that last point itself and fails if they changed.

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
  Per-source budget for the round's proof line. A fresh profile has no catalog to load, so the budget
  is mostly the stream's own start plus the audio proof's 20 s deadline.

.PARAMETER SkipVideo
  Run the audio round only. For a machine with no usable video output; not for a release.

.PARAMETER RecordSeconds
  SP-0121: how long each round records once its stream is live. The app is told through
  STREAMSPLAYER_SMOKE_RECORD_SECONDS / _FOLDER, records through the same path the Record button uses, and
  logs one SMOKE RECORD line; the gate then checks the file exists, is not trivially small, and starts with
  the container signature its extension promises. A green playback round with a failed recording is red.

.PARAMETER SkipRecording
  Check playback only. Not for a release - recording is part of what ships.

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
    [switch] $SkipVideo,
    [ValidateRange(2, 60)] [int] $RecordSeconds = 6,
    [switch] $SkipRecording
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Good([string] $Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Bad([string] $Message) { Write-Host "    $Message" -ForegroundColor Red }

# The owner's session logs, as names and sizes. The gate must leave them exactly as it found them apart from the
# live Current.log, which the owner's own running copy may be writing to.
function Get-OwnerSessionLogs {
    $folder = Join-Path $env:LOCALAPPDATA 'StreamsPlayer'
    if (-not (Test-Path -LiteralPath $folder)) { return @() }
    @(Get-ChildItem -LiteralPath $folder -Filter 'Session-*.log' -File | ForEach-Object { "$($_.Name):$($_.Length)" } | Sort-Object)
}

# The newest line in $Log for one of $Events, stamped after $Since, as @{ Event; Line } - or $null.
function Find-LogEvent {
    param([string] $Log, [string[]] $Events, [DateTimeOffset] $Since)

    if (-not (Test-Path -LiteralPath $Log)) { return $null }
    $pattern = '^(\S+) \[Diag\] (' + (($Events | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?: \||$)'
    $found = $null
    foreach ($line in (Get-Content -LiteralPath $Log -ErrorAction SilentlyContinue)) {
        if ($line -notmatch $pattern) { continue }
        [DateTimeOffset] $stamp = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($Matches[1], [ref] $stamp) -or $stamp -lt $Since) { continue }
        $found = [pscustomobject]@{ Event = $Matches[2]; Line = $line }
    }
    return $found
}

# One source: launch against a fresh profile, watch that profile's log for the round's proof or failure line,
# return a verdict record.
function Invoke-PlaybackProbe {
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [Parameter(Mandatory)] [string] $Url,
        [Parameter(Mandatory)] $Round,
        [Parameter(Mandatory)] [string] $ProfileDir,
        [Parameter(Mandatory)] [int] $Budget,
        [int] $Record = 0,
        [string] $RecordFolder
    )

    # SP-0133: a profile of the probe's own. Nothing the owner has - a running copy, its catalog, its ten session
    # logs - is touched, and the probe's own log is the only Current.log in this folder.
    Remove-Item -LiteralPath $ProfileDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $ProfileDir | Out-Null
    $log = Join-Path $ProfileDir 'Current.log'

    $since = [DateTimeOffset]::UtcNow.AddSeconds(-5)
    # The child inherits these; an ordinary launch never has them, so the app never relocates or records on its own.
    $env:STREAMSPLAYER_DATA_DIRECTORY = $ProfileDir
    if ($Record -gt 0) {
        $env:STREAMSPLAYER_SMOKE_RECORD_SECONDS = "$Record"
        $env:STREAMSPLAYER_SMOKE_RECORD_FOLDER = $RecordFolder
    }
    try { $proc = Start-Process $Exe -ArgumentList '--url', $Url -PassThru }
    finally {
        Remove-Item Env:STREAMSPLAYER_DATA_DIRECTORY -ErrorAction SilentlyContinue
        Remove-Item Env:STREAMSPLAYER_SMOKE_RECORD_SECONDS -ErrorAction SilentlyContinue
        Remove-Item Env:STREAMSPLAYER_SMOKE_RECORD_FOLDER -ErrorAction SilentlyContinue
    }

    $verdict = ''
    $detail = ''
    $deadline = [DateTime]::UtcNow.AddSeconds($Budget)
    while ([DateTime]::UtcNow -lt $deadline -and -not $verdict) {
        Start-Sleep -Seconds 2
        if ($proc.HasExited) { $verdict = 'EXITED'; break }
        $hit = Find-LogEvent -Log $log -Events (@($Round.Pass) + $Round.Fail) -Since $since
        if ($hit) {
            $verdict = if ($hit.Event -eq $Round.Pass) { 'PASS' } else { 'FAIL' }
            $detail = $hit.Line
        }
    }

    # A binary that ignores the variable writes to the owner's profile instead - and may even have handed its
    # launch to the owner's running copy. Either way this folder has no log, and nothing it did was observed here.
    $relocated = Find-LogEvent -Log $log -Events 'DATA DIRECTORY' -Since $since
    if (-not $relocated -or $relocated.Line -notmatch 'relocated=true') {
        $verdict = 'UNISOLATED'
        $detail = "no 'DATA DIRECTORY | relocated=true' line in $log - this build predates SP-0133 or refused the profile"
    }
    $live = Find-LogEvent -Log $log -Events $Round.Live -Since $since

    # SP-0121: a proven stream then records for $Record seconds; wait for the app's own SMOKE RECORD line.
    $recording = $null
    if ($verdict -eq 'PASS' -and $Record -gt 0) {
        $recordDeadline = [DateTime]::UtcNow.AddSeconds($Record + 45)
        while ([DateTime]::UtcNow -lt $recordDeadline -and -not $recording) {
            Start-Sleep -Seconds 2
            if ($proc.HasExited) { break }
            foreach ($line in (Get-Content -LiteralPath $log -ErrorAction SilentlyContinue)) {
                if ($line -match '^(\S+) \[Diag\] SMOKE RECORD \| kind=(\w+) \| fate=([^|]+) \| bytes=(-?\d+) \| path=(.+)$') {
                    [DateTimeOffset] $stamp = [DateTimeOffset]::MinValue
                    if (-not [DateTimeOffset]::TryParse($Matches[1], [ref] $stamp) -or $stamp -lt $since) { continue }
                    $recording = [pscustomobject]@{
                        Kind = $Matches[2]; Fate = $Matches[3].Trim(); Bytes = [long] $Matches[4]; Path = $Matches[5].Trim()
                    }
                }
            }
        }
    }

    # Full paths, not names: the point is where the natives came from.
    $modules = @()
    if (-not $proc.HasExited) {
        $proc.Refresh()
        $modules = @($proc.Modules | Where-Object { $_.ModuleName -match '^(mf|libvlc)' } | ForEach-Object FileName)
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        $proc.WaitForExit(5000) | Out-Null
    }

    if (-not $verdict) { $verdict = 'SILENT' }
    [pscustomobject]@{
        Url = $Url; Verdict = $verdict; Detail = $detail; Live = [bool] $live; Modules = $modules; Recording = $recording
    }
}

# SP-0133: the round's natives were loaded from the folder under test, and from nowhere else.
function Test-NativeOrigin {
    param([Parameter(Mandatory)] [string[]] $Modules, [Parameter(Mandatory)] [string] $AppDir)

    $prefix = $AppDir.TrimEnd('\') + '\'
    foreach ($name in 'libvlc.dll', 'libvlccore.dll') {
        $loaded = @($Modules | Where-Object { [IO.Path]::GetFileName($_) -ieq $name })
        if ($loaded.Count -eq 0) { return "$name was not loaded" }
        $foreign = @($loaded | Where-Object { -not $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
        if ($foreign) { return "$name loaded from $($foreign -join ', '), outside $AppDir" }
    }
    return $null
}

# SP-0121: the file a SMOKE RECORD line names must exist, hold more than a header's worth of media, and begin
# with the signature of the container its extension claims - the checks that catch a playlist saved as .mp3,
# an empty file left by a slow close, and a file that never left staging.
function Test-RecordedFile {
    param([Parameter(Mandatory)] $Recording, [long] $MinimumBytes = 16384)

    if ($Recording.Fate -ne 'saved' -and $Recording.Fate -ne 'stopped') { return "fate=$($Recording.Fate)" }
    if (-not (Test-Path -LiteralPath $Recording.Path)) { return "no file at $($Recording.Path)" }
    $size = (Get-Item -LiteralPath $Recording.Path).Length
    if ($size -lt $MinimumBytes) { return "only $size bytes" }

    $stream = [IO.File]::OpenRead($Recording.Path)
    try { $head = New-Object byte[] 400; [void] $stream.Read($head, 0, $head.Length) } finally { $stream.Dispose() }
    $ascii = [Text.Encoding]::ASCII
    $ok = switch ([IO.Path]::GetExtension($Recording.Path).ToLowerInvariant()) {
        '.mp3'  { $ascii.GetString($head, 0, 3) -eq 'ID3' -or ($head[0] -eq 0xFF -and ($head[1] -band 0xE0) -eq 0xE0) }
        '.aac'  { $head[0] -eq 0xFF -and ($head[1] -band 0xF6) -eq 0xF0 }
        '.ogg'  { $ascii.GetString($head, 0, 4) -eq 'OggS' }
        '.opus' { $ascii.GetString($head, 0, 4) -eq 'OggS' }
        '.flac' { $ascii.GetString($head, 0, 4) -eq 'fLaC' }
        '.ts'   { $head[0] -eq 0x47 -and $head[188] -eq 0x47 }
        '.mp4'  { $ascii.GetString($head, 4, 4) -in 'ftyp', 'moov', 'mdat', 'free', 'wide' }
        '.mkv'  { $head[0] -eq 0x1A -and $head[1] -eq 0x45 -and $head[2] -eq 0xDF -and $head[3] -eq 0xA3 }
        '.avi'  { $ascii.GetString($head, 0, 4) -eq 'RIFF' }
        '.ps'   { $head[0] -eq 0 -and $head[1] -eq 0 -and $head[2] -eq 1 -and $head[3] -eq 0xBA }
        default { $false }
    }
    if (-not $ok) { return "content does not match its extension ($([IO.Path]::GetExtension($Recording.Path)))" }
    return $null
}

Push-Location $root
try {
    if (-not $AppPath) {
        $out = Join-Path $root 'artifacts/smoke'
        Write-Step "Publishing to $out"
        # Emptied first: a file an earlier publish left behind is not part of what this tree ships.
        Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
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
    $vlc = Get-Item (Join-Path $appDir 'libvlc/win-x64/libvlc.dll') -ErrorAction SilentlyContinue
    Write-Step "Testing $AppPath"
    Write-Host "    app    = $((Get-Item $AppPath).VersionInfo.FileVersion)"
    Write-Host "    WPF    = $(if ($wpf) { $wpf.VersionInfo.FileVersion } else { 'framework-dependent' })"
    Write-Host "    libvlc = $(if ($vlc) { $vlc.VersionInfo.FileVersion } else { 'MISSING beside the executable' })"

    $profiles = Join-Path $root 'artifacts/smoke-profile'
    $ownerLogsBefore = Get-OwnerSessionLogs
    $running = @(Get-Process StreamsPlayer -ErrorAction SilentlyContinue)
    if ($running) { Write-Host "    $($running.Count) StreamsPlayer copy(ies) already running - left alone; the probes use their own profile" }

    $rounds = @(
        [pscustomobject]@{
            Name = 'audio'; Stack = 'LibVLC audio-only'; Urls = $AudioUrl
            Live = 'AUDIO LIVE'; Pass = 'AUDIO HEARD'; Fail = @('AUDIO FAIL', 'AUDIO SILENT')
        }
    )
    if (-not $SkipVideo) {
        $rounds += [pscustomobject]@{
            Name = 'video'; Stack = 'LibVLC'; Urls = $VideoUrl
            Live = 'PLAYBACK LIVE'; Pass = 'PLAYBACK SHOWN'; Fail = @('PLAYBACK FAIL')
        }
    }
    else { Write-Host '    video round skipped by request - not a valid state for a release' -ForegroundColor Yellow }

    $failed = @()
    foreach ($round in $rounds) {
        Write-Step "Round: $($round.Name) - $($round.Stack)"
        $passed = $false
        $attempts = @()
        $index = 0
        foreach ($url in $round.Urls) {
            $index++
            Write-Host "    trying $url"
            $recordFolder = Join-Path $root "artifacts/smoke-recordings/$($round.Name)"
            if (-not $SkipRecording) {
                Remove-Item -LiteralPath $recordFolder -Recurse -Force -ErrorAction SilentlyContinue
                New-Item -ItemType Directory -Force -Path $recordFolder | Out-Null
            }
            $probe = Invoke-PlaybackProbe -Exe $AppPath -Url $url -Round $round `
                -ProfileDir (Join-Path $profiles "$($round.Name)-$index") -Budget $TimeoutSeconds `
                -Record $(if ($SkipRecording) { 0 } else { $RecordSeconds }) -RecordFolder $recordFolder
            $attempts += $probe

            if ($probe.Verdict -eq 'PASS') {
                $origin = Test-NativeOrigin -Modules $probe.Modules -AppDir $appDir
                if ($origin) {
                    Write-Bad "expected: libvlc natives loaded from $appDir | actual: $origin"
                    $probe.Verdict = 'MODULES'
                    $probe.Detail = $origin
                    continue
                }
                Write-Good "expected: $($round.Pass) | actual: $($round.Pass)"
                Write-Good "native modules (all from the folder under test): $(@($probe.Modules | ForEach-Object { [IO.Path]::GetFileName($_) }) -join ', ')"
                $passed = $true
                if (-not $SkipRecording) {
                    $problem = if ($probe.Recording) { Test-RecordedFile -Recording $probe.Recording } else { 'no SMOKE RECORD line in the log' }
                    if ($problem) {
                        Write-Bad "expected: a ${RecordSeconds}s $($round.Name) recording | actual: $problem"
                        $passed = $false
                        $probe.Verdict = 'RECORD'
                        $probe.Detail = $problem
                        continue
                    }
                    Write-Good "expected: a ${RecordSeconds}s $($round.Name) recording | actual: $($probe.Recording.Bytes) bytes in $([IO.Path]::GetFileName($probe.Recording.Path))"
                }
                break
            }
            $reachedLive = if ($probe.Live) { "; $($round.Live) was logged, so the engine started but no output was proven" } else { '' }
            switch ($probe.Verdict) {
                'FAIL'       { Write-Bad "expected: $($round.Pass) | actual: $($probe.Detail)$reachedLive" }
                'EXITED'     { Write-Bad 'the application exited before proving any output' }
                'UNISOLATED' { Write-Bad "expected: an isolated profile | actual: $($probe.Detail)" }
                default      { Write-Bad "expected: $($round.Pass) within ${TimeoutSeconds}s | actual: nothing$reachedLive" }
            }
        }
        if (-not $passed) { $failed += [pscustomobject]@{ Round = $round; Attempts = $attempts } }
    }

    # SP-0133 acceptance, checked rather than assumed: the owner's session logs are exactly as they were.
    $ownerLogsAfter = Get-OwnerSessionLogs
    $ownerTouched = @(Compare-Object -ReferenceObject @($ownerLogsBefore) -DifferenceObject @($ownerLogsAfter)).Count -gt 0
    if ($ownerTouched) {
        Write-Bad "expected: the owner's Session-*.log files unchanged | actual: they changed (a copy rotated them during the run)"
    }
    else { Write-Good "expected: the owner's Session-*.log files unchanged | actual: unchanged ($(@($ownerLogsAfter).Count) file(s))" }

    Write-Host ''
    if (-not $failed -and -not $ownerTouched) {
        $recorded = if ($SkipRecording) { '' } else { ' and recorded' }
        $what = if ($SkipVideo) { 'audio was heard' } else { 'audio was heard and video was shown' }
        Write-Host "Playback smoke check PASSED - $what$recorded, against an isolated profile." -ForegroundColor Green
        exit 0
    }

    foreach ($f in $failed) {
        Write-Bad "expected: $($f.Round.Pass) and a checked recording from at least one source | actual: none ($($f.Round.Stack))"
        $f.Attempts | ForEach-Object { Write-Bad "  $($_.Url) -> $($_.Verdict) $($_.Detail)" }
    }
    Write-Host ''
    Write-Host @"
Cheapest checks first. Each probe's own log is in $profiles\<round>-<n>\Current.log.

  1. Play one of the failing URLs in a browser. If that fails too, the machine or the network is the
     problem and this result says nothing about the build.
  2. LIVE logged but no HEARD / SHOWN: the engine opened the stream and produced nothing. Suspect the
     native plugins - libvlc\win-x64\plugins\audio_output, \codec, \video_output - and read the AUDIO SILENT
     line: decoded_blocks=0 is a codec that did not load, decoded_blocks>0 with played_buffers=0 is an audio
     output that did not.
  3. MODULES verdict: libvlc was loaded from somewhere other than the folder under test, so the round proved
     another package's natives. Confirm libvlc\win-x64 exists beside the executable and is populated -
     packaging has dropped it before.
  4. UNISOLATED verdict: the binary did not honour STREAMSPLAYER_DATA_DIRECTORY - it predates SP-0133, or
     the path was refused (see the DATA DIRECTORY line in the owner's Current.log).
  5. RECORD verdict: playback worked and recording did not. "fate=unavailable:RecordUnavailableEngine"
     means the LibVLC probe refused the native package (SP-0121 decision 2) - read the RECORD PROBE line.
     A file whose content does not match its extension is C-09 back; "no SMOKE RECORD line" is a
     recording that never finished - look for RECORD FINISH / AUDIO RECORD END in the log.
  6. Nothing at all in the log: confirm the argument form is ``--url <value>``, two arguments - a bare URL
     parses as Invalid and is silently ignored.

Do not release on a red result here. This gate exists because a release already shipped that built
clean, tested clean, and played nothing.
"@ -ForegroundColor Yellow
    exit 1
}
finally { Pop-Location }
