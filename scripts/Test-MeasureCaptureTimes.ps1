#requires -Version 7.0
<#
.SYNOPSIS
    SP-0239 P4 item 7: fixture tests for Measure-CaptureTimes.ps1 - one case per verdict, plus the unreadable inputs.

.DESCRIPTION
    Builds the files in a throwaway folder and runs the script as the shipped caller does, as a child process, so the
    exit code and the last stdout line are the ones a caller reads:

      JPEG      hand-assembled bytes (SOI, optional APP0 and an XMP APP1 that is not Exif, the Exif APP1 with a TIFF
                block in either byte order, EOI) - the reader never decodes pixels, so none are written
      MP4       hand-assembled boxes (ftyp, moov/mvhd version 0 or 1, an mdat before or after moov)
      other     an MPEG-TS-shaped file and a missing path

    MP4 creation times are computed from the name's local wall-clock time with this machine's zone, so the cases hold
    on any machine. The one case that needs a zone offset other than zero (a muxer that stored local time as if it
    were UTC) is skipped, and named above the verdict line, on a machine whose offset is zero.

    Exit 0 when every case holds, 1 otherwise, 2 when the script under test cannot be run. The last line is
    `test-measure-capture-times: PASS|FAIL|COULD NOT VERIFY run=N skipped=M`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$measure = Join-Path $PSScriptRoot 'Measure-CaptureTimes.ps1'
$run = 0
$skipped = [System.Collections.Generic.List[string]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

function Finish([string] $Word, [int] $Code) {
    foreach ($name in $script:skipped) { Write-Host "skipped: $name" }
    Write-Host "test-measure-capture-times: $Word run=$script:run skipped=$($script:skipped.Count)"
    exit $Code
}

if (-not (Test-Path -LiteralPath $measure -PathType Leaf)) {
    Write-Host "cannot find $measure"
    Finish 'COULD NOT VERIFY' 2
}

# --- bytes -------------------------------------------------------------------------------------------------------------

function Join-Bytes([object[]] $Parts) {
    $list = [System.Collections.Generic.List[byte]]::new()
    foreach ($part in $Parts) { $list.AddRange([byte[]] $part) }
    return , $list.ToArray()
}

function Get-Number([uint64] $Value, [int] $Size, [bool] $Big = $true) {
    $bytes = [byte[]]::new($Size)
    for ($index = 0; $index -lt $Size; $index++) {
        $bytes[$(if ($Big) { $Size - 1 - $index } else { $index })] = [byte](($Value -shr (8 * $index)) -band 0xFF)
    }
    return , $bytes
}

function Get-Ascii([string] $Text) { return , [System.Text.Encoding]::ASCII.GetBytes($Text) }

# --- JPEG --------------------------------------------------------------------------------------------------------------

function New-ExifBlock([string] $Stamp, [bool] $Big) {
    # "Exif\0\0" then a TIFF block: header, IFD0 with the Exif IFD pointer, the Exif IFD with DateTimeOriginal.
    $text = Join-Bytes @((Get-Ascii $Stamp), [byte[]] @(0))
    $exifIfdAt = 8 + 2 + 12 + 4
    $dataAt = $exifIfdAt + 2 + 12 + 4
    $tiff = Join-Bytes @(
        (Get-Ascii $(if ($Big) { 'MM' } else { 'II' })), (Get-Number 42 2 $Big), (Get-Number 8 4 $Big),
        (Get-Number 1 2 $Big), (Get-Number 0x8769 2 $Big), (Get-Number 4 2 $Big), (Get-Number 1 4 $Big), (Get-Number $exifIfdAt 4 $Big), (Get-Number 0 4 $Big),
        (Get-Number 1 2 $Big), (Get-Number 0x9003 2 $Big), (Get-Number 2 2 $Big), (Get-Number $text.Length 4 $Big), (Get-Number $dataAt 4 $Big), (Get-Number 0 4 $Big),
        $text)
    return Join-Bytes @((Get-Ascii "Exif`0`0"), $tiff)
}

function New-Segment([int] $Marker, [byte[]] $Payload) {
    return Join-Bytes @([byte[]] @(0xFF, $Marker), (Get-Number ($Payload.Length + 2) 2), $Payload)
}

function New-Jpeg([string] $Path, [string] $Stamp, [bool] $Big = $false, [bool] $Decoys = $false, [bool] $NoExif = $false) {
    $parts = [System.Collections.Generic.List[byte[]]]::new()
    $parts.Add([byte[]] @(0xFF, 0xD8))
    if ($Decoys -or $NoExif) { $parts.Add((New-Segment 0xE0 (Join-Bytes @((Get-Ascii "JFIF`0"), [byte[]] @(1, 1, 0, 0, 1, 0, 1, 0, 0))))) }
    if ($Decoys) { $parts.Add((New-Segment 0xE1 (Get-Ascii "http://ns.adobe.com/xap/1.0/`0<x:xmpmeta/>"))) }
    if (-not $NoExif) { $parts.Add((New-Segment 0xE1 (New-ExifBlock $Stamp $Big))) }
    $parts.Add([byte[]] @(0xFF, 0xD9))
    [System.IO.File]::WriteAllBytes($Path, (Join-Bytes $parts.ToArray()))
}

# --- MP4 ---------------------------------------------------------------------------------------------------------------

$epoch1904 = [datetime]::new(1904, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)

function New-Box([string] $Type, [byte[]] $Payload) {
    return Join-Bytes @((Get-Number ($Payload.Length + 8) 4), (Get-Ascii $Type), $Payload)
}

function New-Mp4([string] $Path, [uint64] $Creation, [int] $Version = 0, [bool] $MdatFirst = $false, [bool] $Truncate = $false) {
    $width = if ($Version -eq 1) { 8 } else { 4 }
    # version/flags, creation, modification, timescale, duration, rate, volume, reserved, matrix, pre_defined, next track.
    $mvhd = New-Box 'mvhd' (Join-Bytes @(
        [byte[]] @($Version, 0, 0, 0), (Get-Number $Creation $width), (Get-Number $Creation $width), (Get-Number 1000 4), (Get-Number 0 $width),
        (Get-Number 0x10000 4), (Get-Number 0x100 2), [byte[]]::new(10),
        (Get-Number 0x10000 4), [byte[]]::new(12), (Get-Number 0x10000 4), [byte[]]::new(12), (Get-Number 0x40000000 4),
        [byte[]]::new(24), (Get-Number 2 4)))
    $ftyp = New-Box 'ftyp' (Join-Bytes @((Get-Ascii 'isom'), (Get-Number 512 4), (Get-Ascii 'isomiso2mp41')))
    $moov = New-Box 'moov' $mvhd
    $mdat = New-Box 'mdat' ([byte[]]::new(64))
    $file = if ($MdatFirst) { Join-Bytes @($ftyp, $mdat, $moov) } else { Join-Bytes @($ftyp, $moov, $mdat) }
    if ($Truncate) { $file = $file[0..($ftyp.Length + 20)] }
    [System.IO.File]::WriteAllBytes($Path, [byte[]] $file)
}

function Get-Mp4Seconds([datetime] $LocalWall) {
    $utc = [System.TimeZoneInfo]::ConvertTimeToUtc($LocalWall, [System.TimeZoneInfo]::Local)
    return [uint64] ($utc - $epoch1904).TotalSeconds
}

# --- the script under test ---------------------------------------------------------------------------------------------

function Invoke-Measure([string[]] $Arguments) {
    $all = @(& pwsh -NoProfile -File $script:measure @Arguments 2>&1)
    $code = $LASTEXITCODE
    $stdout = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    return [pscustomobject]@{ Exit = $code; Lines = $stdout; Last = $(if ($stdout.Count) { $stdout[-1] } else { '' }) }
}

# Each expected row is a -clike pattern matched against the script's lines, so a row can name its file and its delta.
function Test-Case([string] $Name, [string[]] $Paths, [int] $Exit, [string] $Word, [string[]] $Rows = @()) {
    $script:run++
    $result = Invoke-Measure (@('-Path') + $Paths)
    $problems = @()
    if ($result.Exit -ne $Exit) { $problems += "exit $($result.Exit), expected $Exit" }
    if ($result.Last -cne "capture-times: $Word") { $problems += "last line '$($result.Last)', expected 'capture-times: $Word'" }
    foreach ($expected in $Rows) {
        if (-not ($result.Lines | Where-Object { $_ -clike $expected })) { $problems += "no row like '$expected'" }
    }
    if ($problems) {
        $script:failures.Add($Name)
        Write-Host "FAIL: $Name - $($problems -join '; ')"
        $result.Lines | ForEach-Object { Write-Host "    $_" }
    }
}

# --- cases -------------------------------------------------------------------------------------------------------------

$folder = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0239-measure-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    function Path-Of([string] $Leaf) { return Join-Path $script:folder $Leaf }

    # One local wall-clock second in mid-June: no zone changes its offset that day.
    $wall = [datetime]::new(2026, 6, 15, 14, 30, 15)
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $stamp = $wall.ToString('yyyy:MM:dd HH:mm:ss', $invariant)
    $seconds = Get-Mp4Seconds $wall
    $offset = [System.TimeZoneInfo]::Local.GetUtcOffset($wall)

    New-Jpeg (Path-Of 'video_frame_260615_143015_Test Channel.jpg') $stamp
    New-Jpeg (Path-Of 'video_frame_260615_143015_BBC [HD] (2).jpg') $stamp -Big $true -Decoys $true
    New-Mp4 (Path-Of 'stream_video_260615_143015_News.mp4') $seconds
    New-Mp4 (Path-Of 'stream_video_260615_143015.mp4') $seconds -Version 1 -MdatFirst $true
    New-Jpeg (Path-Of 'video_frame_260615_143015_off.jpg') ($wall.AddSeconds(1).ToString('yyyy:MM:dd HH:mm:ss', $invariant))
    New-Mp4 (Path-Of 'stream_video_260615_143015_off.mp4') ($seconds + 1)
    [System.IO.File]::WriteAllBytes((Path-Of 'stream_video_260615_143015.ts'), [byte[]] (@(0x47, 0x40, 0x11, 0x10) + [byte[]]::new(184)))
    New-Mp4 (Path-Of 'stream_video_260615_143015_zero.mp4') 0
    New-Jpeg (Path-Of 'video_frame_260615_143015_bare.jpg') $stamp -NoExif $true
    [System.IO.File]::WriteAllBytes((Path-Of 'video_frame_260615_143015_junk.jpg'), [byte[]]::new(40))
    New-Mp4 (Path-Of 'stream_video_260615_143015_cut.mp4') $seconds -Truncate $true
    New-Jpeg (Path-Of 'vacation.jpg') $stamp

    # PASS, exit 0: every file inspected and equal.
    Test-Case 'jpeg little-endian equal' @(Path-Of 'video_frame_260615_143015_Test Channel.jpg') 0 'PASS' @('EQUAL *exif=2026-06-15 14:30:15 name=2026-06-15 14:30:15 delta=+0s')
    Test-Case 'jpeg big-endian, JFIF and XMP segments first, label with brackets and an ordinal' @(Path-Of 'video_frame_260615_143015_BBC [HD] (2).jpg') 0 'PASS' @('EQUAL *')
    Test-Case 'mp4 version 0 equal' @(Path-Of 'stream_video_260615_143015_News.mp4') 0 'PASS' @('EQUAL *mvhd-utc=*mvhd-local=2026-06-15 14:30:15 name=2026-06-15 14:30:15 delta=+0s local-offset=*')
    Test-Case 'mp4 version 1 (64-bit), mdat before moov, equal' @(Path-Of 'stream_video_260615_143015.mp4') 0 'PASS' @('EQUAL *')
    Test-Case 'a wildcard that names existing files expands' @(Path-Of 'stream_video_2606*_1430*5.mp4') 0 'PASS' @('EQUAL *stream_video_260615_143015.mp4*')

    # FAIL, exit 1: any DIFF, and the other files are still judged.
    Test-Case 'jpeg one second apart' @(Path-Of 'video_frame_260615_143015_off.jpg') 1 'FAIL' @('DIFF *exif=2026-06-15 14:30:16 name=2026-06-15 14:30:15 delta=+1s')
    Test-Case 'mp4 one second apart' @(Path-Of 'stream_video_260615_143015_off.mp4') 1 'FAIL' @('DIFF *mvhd-local=2026-06-15 14:30:16 *delta=+1s *')
    if ($offset -ne [timespan]::Zero) {
        # A muxer that wrote the local wall-clock as if it were UTC: the delta is the local offset, so the question shows.
        $asUtc = [uint64] ([datetime]::SpecifyKind($wall, [System.DateTimeKind]::Utc) - $epoch1904).TotalSeconds
        New-Mp4 (Path-Of 'stream_video_260615_143015_utc.mp4') $asUtc
        $delta = '{0:+0;-0;+0}' -f [long] $offset.TotalSeconds
        Test-Case 'mp4 that stores local time as UTC shows the offset as its delta' @(Path-Of 'stream_video_260615_143015_utc.mp4') 1 'FAIL' @("DIFF *mvhd-utc=2026-06-15T14:30:15Z *delta=${delta}s *")
    }
    else {
        $skipped.Add('mp4 that stores local time as UTC (this machine runs at offset +00:00 on the case date)')
    }
    Test-Case 'a DIFF beside an EQUAL, a NO-CARRIER and a missing file still prints every row' `
        @((Path-Of 'video_frame_260615_143015_Test Channel.jpg'), (Path-Of 'video_frame_260615_143015_off.jpg'), (Path-Of 'stream_video_260615_143015.ts'), (Path-Of 'gone.jpg')) `
        1 'FAIL' @('EQUAL *', 'DIFF *', 'NO-CARRIER *', 'UNREADABLE *file not found')

    # PASS WITH ADVISORIES, exit 3: inspected, nothing wrong, but some file holds no time to compare.
    Test-Case 'an MPEG-TS file has no carrier' @(Path-Of 'stream_video_260615_143015.ts') 3 'PASS WITH ADVISORIES' @('NO-CARRIER *')
    Test-Case 'mp4 creation_time 0 is unset, not 1904' @(Path-Of 'stream_video_260615_143015_zero.mp4') 3 'PASS WITH ADVISORIES' @('NO-CARRIER *unset*')
    Test-Case 'jpeg without an Exif segment has no carrier' @(Path-Of 'video_frame_260615_143015_bare.jpg') 3 'PASS WITH ADVISORIES' @('NO-CARRIER *')
    Test-Case 'an EQUAL file and a NO-CARRIER file' @((Path-Of 'video_frame_260615_143015_Test Channel.jpg'), (Path-Of 'stream_video_260615_143015.ts')) 3 'PASS WITH ADVISORIES' @('EQUAL *', 'NO-CARRIER *')

    # COULD NOT VERIFY, exit 2: nothing was compared for some input, and no DIFF outranks it.
    Test-Case 'a missing file' @(Path-Of 'absent.jpg') 2 'COULD NOT VERIFY' @('UNREADABLE *file not found')
    Test-Case 'an EQUAL file and a missing file' @((Path-Of 'video_frame_260615_143015_Test Channel.jpg'), (Path-Of 'absent.jpg')) 2 'COULD NOT VERIFY' @('EQUAL *', 'UNREADABLE *')
    Test-Case 'a wildcard that names nothing' @(Path-Of 'nothing-*.jpg') 2 'COULD NOT VERIFY' @('UNREADABLE *matches no file')
    Test-Case 'a folder instead of files' @($folder) 2 'COULD NOT VERIFY' @('UNREADABLE *is a folder*')
    Test-Case 'a .jpg that is not a JPEG' @(Path-Of 'video_frame_260615_143015_junk.jpg') 2 'COULD NOT VERIFY' @('UNREADABLE *not a JPEG*')
    Test-Case 'a truncated mp4' @(Path-Of 'stream_video_260615_143015_cut.mp4') 2 'COULD NOT VERIFY' @('UNREADABLE *truncated*')
    Test-Case 'a carrier but a name with no rule 3 time' @(Path-Of 'vacation.jpg') 2 'COULD NOT VERIFY' @('UNREADABLE *no rule 3 time*')

    $run++
    $none = Invoke-Measure @()
    if ($none.Exit -ne 2 -or $none.Last -cne 'capture-times: COULD NOT VERIFY') {
        $failures.Add('no file given')
        Write-Host "FAIL: no file given - exit $($none.Exit), last line '$($none.Last)'"
    }
}
finally {
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { Finish 'FAIL' 1 }
Finish 'PASS' 0
