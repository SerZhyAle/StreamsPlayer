#requires -Version 7.0
<#
.SYNOPSIS
    SP-0039 (phase 6.4): encodes the silent master recording into the demo assets, and judges them offline.

.DESCRIPTION
    Encode (default). Reads -Source (the master recording; not tracked, temp/ is ignored) and writes into -OutDir:

      streamsplayer-demo.mp4          H.264 High, yuv420p, 1280x720, 30 fps, crf 28, +faststart, no audio (site <video>)
      streamsplayer-demo.gif          the right-to-left segment plus a tail of grid, 720 px wide, 10 fps (the READMEs)
      streamsplayer-demo-poster.jpg   the first frame, 1280x720 (the <video> poster)

    The files are encoded into a staging folder, judged by the same offline check -Check runs, and moved into -OutDir
    only when all three pass, so a failed encode never leaves a half-set. Needs ffmpeg and ffprobe on PATH - a
    maintainer tool, never a project or runtime dependency (winget install --id Gyan.FFmpeg -e).

    The GIF window is [GifStart, GifSegmentEnd + GifGridTail], clamped to the master's real duration (ffprobe) and to
    the GIF's cap: it ends at the master's last frame if the plan runs past it, falls back to the master's last seconds
    if the start lies beyond it, and moves its START later if it exceeds 12 s. The console says what moved.

    -Check judges the files WITHOUT ffmpeg or ffprobe, so it runs anywhere, CI included, and writes nothing:
      * MP4: the box tree is walked in PowerShell. It starts with ftyp, holds one moov whose mvhd duration over
        timescale is under the limit, every trak/mdia/hdlr handler type is vide and none is soun (the demo is silent),
        and moov precedes mdat (the +faststart the encoder promises, so a <video preload="none"> starts without the
        whole file). The walker reads 64-bit box sizes, takes size 0 as "to the end of the enclosing box" and refuses a
        box that runs past its container, so a truncated recording is a finding.
      * GIF: signature, the declared width, the trailer byte. Poster: JPEG signature and end-of-image marker. Every
        file within its byte cap, and all three present.
    Paths are relative to the repository root, not the current directory.

    Exit codes (CHECK-VERDICT, plus one of this script's own):
      0  passed (encode: the three files were written)
      1  a defect: a file breaks a rule above, or the encode failed
      2  could not verify: a file exists but could not be read (locked, access denied)
      3  not there yet: a file of the set does not exist (-Check only). scripts/check.ps1 does not call the check while
         the folder holds no demo file at all; a folder with only some of them is a defect and ends here.
    In -Check mode the last line is `demo-capture: PASS|FAIL|MISSING|CANNOT VERIFY ..`.

.EXAMPLE
    pwsh -NoProfile -File tools/site/make-demo.ps1 -Source temp/SP-0039/demo-master.mp4 -GifStart 40
    pwsh -NoProfile -File tools/site/make-demo.ps1 -Check
    pwsh -NoProfile -File tools/site/make-demo.ps1 -Check -VideoFile temp/SP-0039/demo-fixtures/with-audio.mp4
#>
[CmdletBinding()]
param(
    # The silent master recording (SP-0039 6.3).
    [string] $Source = 'temp/SP-0039/demo-master.mp4',

    # The folder holding the three files: written by the encode, read by -Check (which can read as -Directory, so a
    # fixture folder is pointed at without touching docs/).
    [Alias('Directory')]
    [string] $OutDir = 'docs/assets/demo',

    # Judge the existing files offline; write nothing.
    [switch] $Check,

    # -Check only: judge this one MP4 by the container rules and nothing else. For fixtures: a lone file is not a set.
    [string] $VideoFile,

    # The right-to-left segment of the master in seconds (storyboard rows 38-50 s) and the grid that follows it.
    [ValidateRange(0, 3600)] [double] $GifStart = 38,
    [ValidateRange(0, 3600)] [double] $GifSegmentEnd = 50,
    [ValidateRange(0, 60)] [double] $GifGridTail = 2,

    # Fewer colours shrink the GIF. The byte cap is a hard gate, so a clip that does not fit is lowered here by hand
    # rather than degraded silently.
    [ValidateRange(2, 256)] [int] $GifMaxColors = 256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

# ---- The declared shape of the three assets: what the encode produces is what the check judges. ----------------------

$Mp4Name = 'streamsplayer-demo.mp4'
$GifName = 'streamsplayer-demo.gif'
$PosterName = 'streamsplayer-demo-poster.jpg'

$VideoWidth = 1280
$VideoHeight = 720
$VideoFps = 30
$VideoCrf = 28
$VideoMaxSeconds = 60          # the MP4 runs strictly under this

$GifWidth = 720
$GifFps = 10
$GifMaxSeconds = 12

# Byte caps. A README GIF is paid for on every repository visit and every re-recording adds its bytes to the history
# for good, so the caps are gates and not targets. The poster's cap is this script's own (the plan names none): it
# keeps the image that shows before a click light, like the social cards (under 1 MiB).
$Mp4MaxBytes = 8MB
$GifMaxBytes = 5MB
$PosterMaxBytes = 1MB

# A plausible demo has a few dozen boxes; the cap keeps a corrupt file from making the walk long.
$MaxBoxes = 100000

$ExitPass = 0; $ExitDefect = 1
$ExitCannotVerify = 2; $ExitMissing = 3

function Resolve-RepoPath {
    param([Parameter(Mandatory)] [string] $Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $root $Path))
}

function Format-Mib([long] $Bytes) { return ('{0:N2} MiB' -f ($Bytes / 1MB)) }

# ---- Offline readers -------------------------------------------------------------------------------------------------

function Read-Bytes {
    # Exactly Count bytes at Offset, or $null when the file ends first: a truncated file must become a finding,
    # not an exception from a short read.
    param([System.IO.FileStream] $Stream, [long] $Offset, [int] $Count)
    if ($Offset -lt 0 -or ($Offset + $Count) -gt $Stream.Length) { return $null }
    [void] $Stream.Seek($Offset, [System.IO.SeekOrigin]::Begin)
    $buffer = New-Object byte[] $Count
    $done = 0
    while ($done -lt $Count) {
        $read = $Stream.Read($buffer, $done, $Count - $done)
        if ($read -le 0) { return $null }
        $done += $read
    }
    return , $buffer
}

function Get-UInt32BE([byte[]] $Bytes, [int] $Index) {
    return ([long] $Bytes[$Index] -shl 24) -bor ([long] $Bytes[$Index + 1] -shl 16) -bor ([long] $Bytes[$Index + 2] -shl 8) -bor [long] $Bytes[$Index + 3]
}

function Read-BoxHeader {
    # One box header at Offset inside a container ending at Limit. Size 1 means a 64-bit size follows the type;
    # size 0 means the box runs to the end of its container. Anything that cannot be a box comes back as an Error
    # and never as a position the caller could loop on: End is always past Start by at least the header.
    param([System.IO.FileStream] $Stream, [long] $Offset, [long] $Limit)
    $fail = { param($Message) [pscustomobject]@{ Error = $Message; Type = ''; Start = $Offset; HeaderSize = 0; Size = 0; End = $Offset } }
    $room = $Limit - $Offset
    if ($room -lt 8) { return & $fail "$room byte(s) at offset $Offset are too few for a box header" }
    $head = Read-Bytes $Stream $Offset 8
    if ($null -eq $head) { return & $fail "cannot read a box header at offset $Offset" }

    $size = Get-UInt32BE $head 0
    # Printable ASCII only, so a corrupt type cannot garble the message.
    $type = [System.Text.Encoding]::Latin1.GetString($head, 4, 4) -replace '[^\x20-\x7E]', '?'
    $headerSize = 8
    if ($size -eq 1) {
        if ($room -lt 16) { return & $fail "box '$type' at offset $Offset announces a 64-bit size but the header is cut off" }
        $extended = Read-Bytes $Stream ($Offset + 8) 8
        if ($null -eq $extended) { return & $fail "cannot read the 64-bit size of box '$type' at offset $Offset" }
        $high = Get-UInt32BE $extended 0
        $low = Get-UInt32BE $extended 4
        # 0x...L: a bare 32-bit hex literal with its top bit set is a negative Int32 in PowerShell.
        if ($high -ge 0x80000000L) { return & $fail "box '$type' at offset $Offset declares a size beyond 2^63" }
        $size = ($high -shl 32) -bor $low
        $headerSize = 16
    }
    elseif ($size -eq 0) {
        $size = $room
    }
    if ($size -lt $headerSize) { return & $fail "box '$type' at offset $Offset declares $size byte(s), less than its own header" }
    if ($size -gt $room) { return & $fail "box '$type' at offset $Offset declares $size byte(s) but only $room remain in its container (truncated file?)" }
    return [pscustomobject]@{ Error = $null; Type = $type; Start = $Offset; HeaderSize = $headerSize; Size = $size; End = ($Offset + $size) }
}

function Read-Mvhd {
    param([System.IO.FileStream] $Stream, [pscustomobject] $Box, $State)
    $payload = $Box.Size - $Box.HeaderSize
    $first = $Box.Start + $Box.HeaderSize
    $head = if ($payload -ge 4) { Read-Bytes $Stream $first 4 } else { $null }
    if ($null -eq $head) { $State.Findings.Add('moov/mvhd is too short to hold a version'); return }

    # Version 0 keeps 32-bit times, version 1 64-bit; the timescale is 32-bit in both, the duration follows it.
    if ($head[0] -eq 0) { $needed = 20; $scaleAt = 12; $durationAt = 16 }
    elseif ($head[0] -eq 1) { $needed = 32; $scaleAt = 20; $durationAt = 24 }
    else { $State.Findings.Add("moov/mvhd has unknown version $($head[0])"); return }

    $body = if ($payload -ge $needed) { Read-Bytes $Stream $first $needed } else { $null }
    if ($null -eq $body) { $State.Findings.Add('moov/mvhd is cut short'); return }

    # An all-ones duration is the "unknown" marker a muxer leaves when it never finished the file.
    $high = if ($head[0] -eq 1) { Get-UInt32BE $body $durationAt } else { 0 }
    $low = Get-UInt32BE $body ($durationAt + $(if ($head[0] -eq 1) { 4 } else { 0 }))
    $unknown = ($low -eq 0xFFFFFFFFL -and ($head[0] -eq 0 -or $high -eq 0xFFFFFFFFL))
    $duration = ([double] $high * 4294967296.0) + [double] $low
    if ($null -eq $State.Mvhd) { $State.Mvhd = [pscustomobject]@{ Timescale = (Get-UInt32BE $body $scaleAt); Duration = $duration; Unknown = $unknown } }
}

function Read-Hdlr {
    param([System.IO.FileStream] $Stream, [pscustomobject] $Box, $State)
    $payload = $Box.Size - $Box.HeaderSize
    # version/flags (4), pre_defined (4), handler_type (4)
    $body = if ($payload -ge 12) { Read-Bytes $Stream ($Box.Start + $Box.HeaderSize) 12 } else { $null }
    if ($null -eq $body) { $State.Findings.Add('moov/trak/mdia/hdlr is too short to hold a handler type'); return }
    $handler = [System.Text.Encoding]::Latin1.GetString($body, 8, 4) -replace '[^\x20-\x7E]', '?'
    if ($null -ne $State.Current -and $null -eq $State.Current.Handler) { $State.Current.Handler = $handler }
}

function Read-BoxTree {
    # Walks the sibling boxes in [Start, Limit) and descends only moov, trak and mdia - the path to the duration and
    # the handler types. Termination: every iteration advances Offset by a box's full size, which Read-BoxHeader
    # guarantees is at least its header, and the loop ends at Limit; descent depth is fixed by the three container
    # names; the box count is capped as well.
    param([System.IO.FileStream] $Stream, [long] $Start, [long] $Limit, [string] $Path, $State)
    $offset = $Start
    while ($offset -lt $Limit) {
        $State.BoxCount++
        if ($State.BoxCount -gt $MaxBoxes) { $State.Findings.Add("more than $MaxBoxes boxes - not a plausible demo file"); return }

        $box = Read-BoxHeader $Stream $offset $Limit
        if ($null -ne $box.Error) { $State.Findings.Add($(if ($Path) { "${Path}: $($box.Error)" } else { $box.Error })); return }

        $here = if ($Path) { "$Path/$($box.Type)" } else { $box.Type }
        $inner = $box.Start + $box.HeaderSize
        if (-not $Path) { $State.Top.Add([pscustomobject]@{ Type = $box.Type; Start = $box.Start }) }

        switch -CaseSensitive ($here) {
            'moov' {
                $State.MoovCount++
                Read-BoxTree $Stream $inner $box.End $here $State
            }
            'moov/trak' {
                $track = [pscustomobject]@{ Handler = $null }
                $State.Tracks.Add($track)
                $State.Current = $track
                Read-BoxTree $Stream $inner $box.End $here $State
                $State.Current = $null
            }
            'moov/trak/mdia' { Read-BoxTree $Stream $inner $box.End $here $State }
            'moov/mvhd' { Read-Mvhd $Stream $box $State }
            'moov/trak/mdia/hdlr' { Read-Hdlr $Stream $box $State }
        }
        $offset = $box.End
    }
}

function Get-Mp4Verdict {
    # Adds every rule the MP4 breaks to Problems; returns the one-line summary of what it is.
    param([string] $Path, [System.Collections.Generic.List[string]] $Problems)
    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -eq 0) { $Problems.Add('is empty'); return '' }
    if ($length -gt $Mp4MaxBytes) { $Problems.Add("is $(Format-Mib $length); the cap is $(Format-Mib $Mp4MaxBytes)") }

    $state = @{
        Findings = $Problems; BoxCount = 0; MoovCount = 0; Mvhd = $null; Current = $null
        Top = [System.Collections.Generic.List[object]]::new(); Tracks = [System.Collections.Generic.List[object]]::new()
    }
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try { Read-BoxTree $stream 0 $stream.Length '' $state } finally { $stream.Dispose() }

    if ($state.Top.Count -eq 0 -or $state.Top[0].Type -cne 'ftyp') { $Problems.Add('does not start with an ftyp box - not an MP4') }
    if ($state.MoovCount -eq 0) { $Problems.Add('has no moov box (an unfinished or truncated recording)') }
    elseif ($state.MoovCount -gt 1) { $Problems.Add("has $($state.MoovCount) moov boxes") }

    $seconds = $null
    if ($state.MoovCount -ge 1) {
        $mvhd = $state.Mvhd
        if ($null -eq $mvhd) { $Problems.Add('has no readable moov/mvhd') }
        elseif ($mvhd.Timescale -eq 0) { $Problems.Add('moov/mvhd declares a timescale of 0') }
        elseif ($mvhd.Unknown) { $Problems.Add('moov/mvhd leaves the duration unknown') }
        else {
            $seconds = $mvhd.Duration / $mvhd.Timescale
            if ($seconds -ge $VideoMaxSeconds) { $Problems.Add(('runs {0:N1} s; it must stay under {1} s' -f $seconds, $VideoMaxSeconds)) }
        }
    }

    $handlers = @()
    $index = 0
    foreach ($track in $state.Tracks) {
        $index++
        if ($null -eq $track.Handler) { $Problems.Add("track $index has no mdia/hdlr handler"); continue }
        $handlers += $track.Handler
        if ($track.Handler -ceq 'soun') { $Problems.Add("track $index is audio (handler soun); the demo must be silent") }
        elseif ($track.Handler -cne 'vide') { $Problems.Add("track $index has handler '$($track.Handler)'; only vide is allowed") }
    }
    if ($state.MoovCount -ge 1 -and $state.Tracks.Count -eq 0) { $Problems.Add('has no track') }
    elseif ($state.Tracks.Count -gt 0 -and -not ($handlers -ccontains 'vide')) { $Problems.Add('has no video track') }

    $moov = $state.Top | Where-Object { $_.Type -ceq 'moov' } | Select-Object -First 1
    $mdat = $state.Top | Where-Object { $_.Type -ceq 'mdat' } | Select-Object -First 1
    if ($null -eq $mdat) { $Problems.Add('has no mdat box') }
    elseif ($null -ne $moov -and $moov.Start -gt $mdat.Start) { $Problems.Add('has moov after mdat (not +faststart)') }

    $runtime = if ($null -ne $seconds) { '{0:N1} s' -f $seconds } else { 'duration unknown' }
    return "$(Format-Mib $length), $runtime, $($state.Tracks.Count) track(s) [$($handlers -join ' ')]"
}

function Get-StillVerdict {
    # The GIF and the poster are judged alike: a signature at the front, a terminator at the back (a cut file lacks
    # it) and a byte cap. Only the file's first and last bytes are read, so a file far past its cap costs nothing.
    param([string] $Path, [System.Collections.Generic.List[string]] $Problems, [string] $Kind)
    $cap = if ($Kind -eq 'gif') { $GifMaxBytes } else { $PosterMaxBytes }
    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -eq 0) { $Problems.Add('is empty'); return '' }
    if ($length -gt $cap) { $Problems.Add("is $(Format-Mib $length); the cap is $(Format-Mib $cap)") }

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $head = Read-Bytes $stream 0 ([int] [Math]::Min(13, $length))
        $tail = Read-Bytes $stream ([Math]::Max(0, $length - 2)) ([int] [Math]::Min(2, $length))
    }
    finally { $stream.Dispose() }
    if ($null -eq $head -or $null -eq $tail) { $Problems.Add('could not be read'); return '' }

    if ($Kind -eq 'gif') {
        $signature = [System.Text.Encoding]::Latin1.GetString($head, 0, [Math]::Min(6, $head.Length))
        if ($head.Length -lt 13 -or ($signature -cne 'GIF89a' -and $signature -cne 'GIF87a')) { $Problems.Add('does not start with a GIF signature'); return '' }
        $width = [int] $head[6] + 256 * [int] $head[7]
        $height = [int] $head[8] + 256 * [int] $head[9]
        if ($width -ne $GifWidth) { $Problems.Add("is $width px wide; expected $GifWidth") }
        if ($tail[$tail.Length - 1] -ne 0x3B) { $Problems.Add('does not end with the GIF trailer (truncated)') }
        return "$(Format-Mib $length), ${width}x${height}"
    }
    if ($head.Length -lt 2 -or $head[0] -ne 0xFF -or $head[1] -ne 0xD8) { $Problems.Add('does not start with a JPEG signature'); return '' }
    if ($tail.Length -lt 2 -or $tail[0] -ne 0xFF -or $tail[1] -ne 0xD9) { $Problems.Add('does not end with the JPEG end-of-image marker (truncated)') }
    return (Format-Mib $length)
}

function Test-DemoFile {
    # Judges one file with its verdict function and files the outcome in the report. A missing file is a
    # category of its own (exit 3), a file that cannot be read another (exit 2), a broken rule a defect (exit 1).
    param($Report, [string] $Path, [ValidateSet('mp4', 'gif', 'poster')] [string] $Kind)
    $name = Split-Path -Leaf $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $Report.Missing.Add($name)
        Write-Host "MISSING $name" -ForegroundColor Yellow
        return
    }
    $problems = [System.Collections.Generic.List[string]]::new()
    try {
        $summary = switch ($Kind) {
            'mp4' { Get-Mp4Verdict $Path $problems }
            'gif' { Get-StillVerdict $Path $problems 'gif' }
            'poster' { Get-StillVerdict $Path $problems 'poster' }
        }
    }
    catch [System.IO.IOException], [System.UnauthorizedAccessException] {
        $Report.Unreadable.Add($name)
        Write-Host "CANNOT READ $name - $($_.Exception.Message)" -ForegroundColor Yellow
        return
    }
    if ($problems.Count -eq 0) { Write-Host "OK      $name  $summary" -ForegroundColor Green; return }
    foreach ($problem in $problems) {
        $Report.Findings.Add("$name $problem")
        Write-Host "FAIL    $name $problem" -ForegroundColor Red
    }
}

function Invoke-DemoCheck {
    param([string] $Folder, [string] $LoneVideo)
    $report = [pscustomobject]@{
        Findings = [System.Collections.Generic.List[string]]::new()
        Missing = [System.Collections.Generic.List[string]]::new()
        Unreadable = [System.Collections.Generic.List[string]]::new()
    }
    if ($LoneVideo) { Test-DemoFile $report $LoneVideo 'mp4'; return $report }
    Test-DemoFile $report (Join-Path $Folder $Mp4Name) 'mp4'
    Test-DemoFile $report (Join-Path $Folder $GifName) 'gif'
    Test-DemoFile $report (Join-Path $Folder $PosterName) 'poster'
    return $report
}

function Write-DemoVerdict {
    # Prints the last line and returns the exit code. A defect outranks a missing file, which outranks a file that
    # could not be read: a definite failure is not softened by something unknown.
    param($Report)
    if ($Report.Findings.Count -gt 0) { Write-Host "demo-capture: FAIL ($($Report.Findings.Count) finding(s))"; return $ExitDefect }
    if ($Report.Missing.Count -gt 0) {
        Write-Host "demo-capture: MISSING ($($Report.Missing -join ', ')) - record with SP-0039 6.3, then run tools/site/make-demo.ps1"
        return $ExitMissing
    }
    if ($Report.Unreadable.Count -gt 0) { Write-Host "demo-capture: CANNOT VERIFY ($($Report.Unreadable -join ', '))"; return $ExitCannotVerify }
    Write-Host 'demo-capture: PASS'
    return $ExitPass
}

# ---- Check mode ------------------------------------------------------------------------------------------------------

if ($VideoFile -and -not $Check) { Write-Host 'make-demo: -VideoFile is a -Check option: it judges a lone MP4 and encodes nothing.' -ForegroundColor Red; exit $ExitDefect }

if ($Check) {
    try {
        $loneVideo = if ($VideoFile) { Resolve-RepoPath $VideoFile } else { $null }
        $report = Invoke-DemoCheck -Folder (Resolve-RepoPath $OutDir) -LoneVideo $loneVideo
        exit (Write-DemoVerdict $report)
    }
    catch {
        Write-Host "demo-capture: CANNOT VERIFY ($($_.Exception.Message))"
        exit $ExitCannotVerify
    }
}

# ---- Encode mode -----------------------------------------------------------------------------------------------------

function Invoke-Native {
    # The exit code is the verdict. The script runs with ErrorActionPreference Stop, and ffmpeg's stderr must not turn
    # into a terminating error on its own, so it is relaxed around the call only.
    param([string] $Tool, [string[]] $Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = (& $Tool @Arguments 2>&1 | Out-String) } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$Tool exited $LASTEXITCODE`n$output" }
    return $output
}

function Get-SourceFacts {
    param([string] $Path)
    $probe = Invoke-Native 'ffprobe' @('-v', 'error', '-show_entries', 'format=duration:stream=index,codec_type,width,height', '-of', 'json', $Path) | ConvertFrom-Json
    $video = @($probe.streams | Where-Object { $_.codec_type -eq 'video' })
    if ($video.Count -eq 0) { throw 'the source has no video stream' }
    # Nothing is ever muxed into the master (6.3); an audio stream means the recording picked up a device that must
    # not reach a published demo, so stop here rather than quietly drop it.
    if (@($probe.streams | Where-Object { $_.codec_type -eq 'audio' }).Count -gt 0) { throw 'the source carries an audio stream; the master recording must be silent (SP-0039 6.3)' }
    return [pscustomobject]@{
        Seconds = [double]::Parse([string] $probe.format.duration, [System.Globalization.CultureInfo]::InvariantCulture)
        Width = [int] $video[0].width
        Height = [int] $video[0].height
    }
}

# exit is taken after the finally block, so the staging folder is always cleaned up first.
$stage = $null
$exitCode = $ExitPass
try {
    foreach ($tool in 'ffmpeg', 'ffprobe') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "$tool is not on PATH. It is a maintainer tool, never a project dependency: winget install --id Gyan.FFmpeg -e"
        }
    }
    $sourcePath = Resolve-RepoPath $Source
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "the master recording is missing: $Source (record it with SP-0039 6.3)" }
    $target = Resolve-RepoPath $OutDir

    $facts = Get-SourceFacts $sourcePath
    # A scale to 1280x720 of another aspect would stretch the product's own window; refuse instead.
    if (($facts.Width * $VideoHeight) -ne ($facts.Height * $VideoWidth)) {
        throw "the source is $($facts.Width)x$($facts.Height); the demo is 16:9 (${VideoWidth}x${VideoHeight})"
    }
    if ($GifSegmentEnd -le $GifStart) { throw '-GifSegmentEnd must be later than -GifStart' }

    # The window is a plan; the master is what the run produced and is rarely the length the storyboard names. So it is
    # clamped to the master's real duration (floored to a millisecond, never past the last frame), and the cap moves
    # the START later: the clip ends on the master's closing seconds whatever the master's length.
    $plannedEnd = $GifSegmentEnd + $GifGridTail
    $gifEnd = [Math]::Min($plannedEnd, [Math]::Floor($facts.Seconds * 1000) / 1000)
    $gifFrom = [Math]::Max($(if ($GifStart -lt $gifEnd) { $GifStart } else { 0 }), $gifEnd - $GifMaxSeconds)
    $gifSeconds = $gifEnd - $gifFrom
    if ($gifSeconds -lt 1) { throw ('the GIF window is {0:N1} s (master {1:N1} s); it needs at least 1 s' -f $gifSeconds, $facts.Seconds) }
    if ($gifFrom -ne $GifStart -or $gifEnd -ne $plannedEnd) {
        Write-Host ('GIF window {0}-{1} s becomes {2}-{3} s: the master is {4:N1} s long and the clip is at most {5} s' -f $GifStart, $plannedEnd, $gifFrom, $gifEnd, $facts.Seconds, $GifMaxSeconds)
    }
    $gifFrames = [int] [Math]::Floor($gifSeconds * $GifFps + 1e-6)

    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ('streamsplayer-demo-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    $quiet = @('-hide_banner', '-loglevel', 'error', '-nostdin', '-y')

    Write-Host "encoding $Mp4Name .."
    Invoke-Native 'ffmpeg' ($quiet + @(
            '-i', $sourcePath, '-map', '0:v:0', '-an', '-sn', '-dn', '-map_metadata', '-1',
            # bitexact: no encoder version or wall-clock time in the file, so the same master gives the same bytes
            # and a re-run that changes nothing adds nothing to the history.
            '-fflags', '+bitexact', '-flags:v', '+bitexact',
            '-vf', "fps=${VideoFps},scale=${VideoWidth}:${VideoHeight}:flags=lanczos,setsar=1,format=yuv420p",
            '-c:v', 'libx264', '-profile:v', 'high', '-preset', 'slow', '-crf', "$VideoCrf",
            '-movflags', '+faststart', (Join-Path $stage $Mp4Name))) | Out-Null

    Write-Host "encoding $GifName .."
    $gifGraph = "fps=${GifFps},scale=${GifWidth}:-1:flags=lanczos,split[a][b];" +
    "[a]palettegen=max_colors=${GifMaxColors}:stats_mode=diff[p];" +
    '[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle'
    Invoke-Native 'ffmpeg' ($quiet + @(
            '-ss', "$gifFrom", '-t', "$gifSeconds", '-i', $sourcePath, '-an', '-map_metadata', '-1',
            '-filter_complex', $gifGraph, '-frames:v', "$gifFrames", '-loop', '0', (Join-Path $stage $GifName))) | Out-Null

    Write-Host "encoding $PosterName .."
    Invoke-Native 'ffmpeg' ($quiet + @(
            '-i', $sourcePath, '-an', '-frames:v', '1', '-map_metadata', '-1',
            '-vf', "scale=${VideoWidth}:${VideoHeight}:flags=lanczos,setsar=1", '-q:v', '3', '-update', '1', (Join-Path $stage $PosterName))) | Out-Null

    # The same offline check the gate runs, on the staged files: nothing reaches OutDir unless all three pass it.
    $report = Invoke-DemoCheck -Folder $stage -LoneVideo $null
    $code = Write-DemoVerdict $report
    if ($code -ne $ExitPass) { throw 'the staged files do not pass the check; OutDir was not touched' }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($name in $Mp4Name, $GifName, $PosterName) {
        Move-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $target $name) -Force
    }
    Write-Host "wrote $Mp4Name, $GifName and $PosterName to $target" -ForegroundColor Green
}
catch {
    Write-Host "make-demo: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = $ExitDefect
}
finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
