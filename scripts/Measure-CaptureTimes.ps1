#requires -Version 7.0
<#
.SYNOPSIS
    SP-0239 P4 item 7: measures CAPTURE-OUTPUT rule 16 on produced files - the capture time inside a file equals the
    one in its name.

.DESCRIPTION
    For every file given, reads the time the file carries and compares it with the time in its name:

      JPEG       EXIF DateTimeOriginal (APP1 -> IFD0 -> Exif IFD tag 0x9003, ASCII "yyyy:MM:dd HH:mm:ss"). It names
                 no zone, so it is compared with the name's local wall-clock second directly.
      MP4        the mvhd creation_time (version 0: 32-bit, version 1: 64-bit, seconds since 1904-01-01 UTC). It is
                 UTC, so it is converted to this machine's local time before it is compared with the name's local
                 wall-clock second (rule 3: the name is local). The row prints the UTC value, the converted value,
                 the name's value, the delta and the local offset, so a UTC-versus-local question can be read off it.
      other      a container with no creation-time carrier this script reads (.ts, .mp3, .aac, ..) is NO-CARRIER.

    The name is the CAPTURE-OUTPUT rule 3 form `<prefix>_<yyMMdd>_<HHmmss>[_<label>][ (<n>)].<ext>`, which is what
    CaptureFileName produces (`yyMMdd_HHmmss`, local wall-clock, invariant digits).

    One row per file, first word: EQUAL, DIFF, NO-CARRIER (the file holds no time to compare - an empty or zero
    creation_time and a JPEG without DateTimeOriginal count), or UNREADABLE (the file is missing, cut short, not the
    format its extension claims, or its name holds no rule 3 time - nothing could be compared). Every file is judged;
    the run does not stop at the first failure.

    Verdict, the last line on stdout (CHECK-VERDICT 2.1 and 2.5), with the exit code that travels with it:
      capture-times: PASS                any file inspected, all EQUAL                                  exit 0
      capture-times: FAIL                any DIFF                                                       exit 1
      capture-times: COULD NOT VERIFY    no file given, or any UNREADABLE and no DIFF                   exit 2
      capture-times: PASS WITH ADVISORIES  no DIFF, no UNREADABLE, at least one NO-CARRIER              exit 3
    The word follows the fixed precedence FAIL, COULD NOT VERIFY, PASS WITH ADVISORIES, PASS (rule 14). The line
    before the verdict carries the counts.

.PARAMETER Path
    The files to measure. A path with wildcard characters that names no existing file is expanded; one that exists is
    taken literally (a channel label may hold `[` and `]`).

.PARAMETER MorePath
    The files after the first, so `-Path a b c` and `a b c` both work under `pwsh -File`. Under `-File` a list written
    `-Path a,b` arrives as one string, not two, so the files are separated by spaces.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string[]] $Path,

    [Parameter(ValueFromRemainingArguments)]
    [string[]] $MorePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$epoch1904 = [datetime]::new(1904, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
$exifFormat = 'yyyy:MM:dd HH:mm:ss'
$displayFormat = 'yyyy-MM-dd HH:mm:ss'
# <prefix>_<yyMMdd>_<HHmmss> followed by the label, the ordinal or the extension - rule 3, as CaptureFileName writes it.
$namePattern = '^[a-z]+(?:_[a-z]+)*_(?<date>\d{6})_(?<time>\d{6})(?=[_. ])'

function New-Carrier([string] $State, [string] $Reason = '', $Wall = $null, $Utc = $null) {
    # State: time (Wall or Utc is set), none (a format that can hold a time holds none), error (cannot be read).
    [pscustomobject]@{ State = $State; Reason = $Reason; Wall = $Wall; Utc = $Utc }
}

function Read-Bytes([System.IO.FileStream] $Stream, [long] $Offset, [int] $Count) {
    if ($Offset -lt 0 -or ($Offset + $Count) -gt $Stream.Length) { return $null }
    $null = $Stream.Seek($Offset, [System.IO.SeekOrigin]::Begin)
    $buffer = [byte[]]::new($Count)
    $done = 0
    while ($done -lt $Count) {
        $read = $Stream.Read($buffer, $done, $Count - $done)
        if ($read -le 0) { return $null }
        $done += $read
    }
    return , $buffer
}

function Get-UInt([byte[]] $Bytes, [int] $At, [int] $Size, [bool] $Big) {
    [long] $value = 0
    for ($index = 0; $index -lt $Size; $index++) {
        $byte = if ($Big) { $Bytes[$At + $index] } else { $Bytes[$At + $Size - 1 - $index] }
        $value = ($value -shl 8) -bor $byte
    }
    return $value
}

# --- JPEG: EXIF DateTimeOriginal ---------------------------------------------------------------------------------------

function Find-IfdEntry([byte[]] $Bytes, [int] $Tiff, [long] $IfdOffset, [int] $Tag, [bool] $Big) {
    # The entry for $Tag in the IFD at $IfdOffset (from the TIFF header), $null when the IFD has none, or an error string.
    $start = $Tiff + $IfdOffset
    if ($IfdOffset -lt 8 -or ($start + 2) -gt $Bytes.Length) { return "an IFD offset ($IfdOffset) points outside the Exif block" }
    $count = Get-UInt $Bytes $start 2 $Big
    if (($start + 2 + 12 * $count) -gt $Bytes.Length) { return "an IFD of $count entries runs past the Exif block" }
    for ($index = 0; $index -lt $count; $index++) {
        $entry = $start + 2 + 12 * $index
        if ((Get-UInt $Bytes $entry 2 $Big) -eq $Tag) {
            return [pscustomobject]@{
                Type = (Get-UInt $Bytes ($entry + 2) 2 $Big)
                Count = (Get-UInt $Bytes ($entry + 4) 4 $Big)
                ValueAt = ($entry + 8)
            }
        }
    }
    return $null
}

function Read-ExifDateTimeOriginal([byte[]] $Bytes, [int] $Tiff) {
    if (($Tiff + 8) -gt $Bytes.Length) { return New-Carrier 'error' 'the Exif block is shorter than a TIFF header' }
    $order = [System.Text.Encoding]::ASCII.GetString($Bytes, $Tiff, 2)
    if ($order -ceq 'MM') { $big = $true } elseif ($order -ceq 'II') { $big = $false } else { return New-Carrier 'error' "the Exif byte order '$order' is neither II nor MM" }
    if ((Get-UInt $Bytes ($Tiff + 2) 2 $big) -ne 42) { return New-Carrier 'error' 'the TIFF header has no 42 marker' }

    $pointer = Find-IfdEntry $Bytes $Tiff (Get-UInt $Bytes ($Tiff + 4) 4 $big) 0x8769 $big
    if ($pointer -is [string]) { return New-Carrier 'error' $pointer }
    if ($null -eq $pointer) { return New-Carrier 'none' 'the Exif block has no Exif IFD' }
    $entry = Find-IfdEntry $Bytes $Tiff (Get-UInt $Bytes $pointer.ValueAt 4 $big) 0x9003 $big
    if ($entry -is [string]) { return New-Carrier 'error' $entry }
    if ($null -eq $entry) { return New-Carrier 'none' 'the Exif IFD has no DateTimeOriginal (0x9003)' }
    if ($entry.Type -ne 2 -or $entry.Count -lt 20) { return New-Carrier 'error' "DateTimeOriginal has type $($entry.Type) and count $($entry.Count), not an ASCII string of 20" }

    $at = $Tiff + (Get-UInt $Bytes $entry.ValueAt 4 $big)
    if (($at + $entry.Count) -gt $Bytes.Length) { return New-Carrier 'error' 'DateTimeOriginal points outside the Exif block' }
    $text = [System.Text.Encoding]::ASCII.GetString($Bytes, $at, [int] $entry.Count).TrimEnd([char] 0)
    if ($text.Trim(' :') -eq '') { return New-Carrier 'none' 'DateTimeOriginal is blank' }
    $wall = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($text, $exifFormat, $invariant, [System.Globalization.DateTimeStyles]::None, [ref] $wall)) {
        return New-Carrier 'error' "DateTimeOriginal '$text' is not '$exifFormat'"
    }
    return New-Carrier 'time' '' $wall
}

function Read-JpegCaptureTime([System.IO.FileStream] $Stream) {
    $length = $Stream.Length
    $soi = Read-Bytes $Stream 0 2
    if ($null -eq $soi -or $soi[0] -ne 0xFF -or $soi[1] -ne 0xD8) { return New-Carrier 'error' 'not a JPEG (no SOI marker)' }
    $offset = 2
    # Every pass moves $offset forward by at least one byte, and the walk ends at the start of the scan or at the end.
    while (($offset + 2) -le $length) {
        $head = Read-Bytes $Stream $offset 2
        if ($head[0] -ne 0xFF) { return New-Carrier 'error' "no marker at offset $offset" }
        $marker = $head[1]
        if ($marker -eq 0xFF) { $offset++; continue }
        if ($marker -eq 0xDA -or $marker -eq 0xD9) { break }
        if ($marker -eq 0x01 -or ($marker -ge 0xD0 -and $marker -le 0xD7)) { $offset += 2; continue }
        $sizeBytes = Read-Bytes $Stream ($offset + 2) 2
        if ($null -eq $sizeBytes) { return New-Carrier 'error' "the segment at offset $offset is cut off" }
        $size = Get-UInt $sizeBytes 0 2 $true
        if ($size -lt 2 -or ($offset + 2 + $size) -gt $length) { return New-Carrier 'error' "the segment at offset $offset declares $size byte(s) and does not fit the file" }
        if ($marker -eq 0xE1 -and $size -ge 8) {
            $payload = Read-Bytes $Stream ($offset + 4) ([int] ($size - 2))
            if ([System.Text.Encoding]::Latin1.GetString($payload, 0, 6) -ceq "Exif`0`0") { return Read-ExifDateTimeOriginal $payload 6 }
        }
        $offset += 2 + $size
    }
    return New-Carrier 'none' 'the JPEG has no Exif APP1 segment'
}

# --- MP4: mvhd creation_time -------------------------------------------------------------------------------------------

function Find-Box([System.IO.FileStream] $Stream, [long] $Start, [long] $Limit, [string] $Type) {
    # The first box of $Type among the siblings in [Start, Limit): Found with Start/HeaderSize/End, Absent, or Error.
    $offset = $Start
    while ($offset -lt $Limit) {
        $room = $Limit - $offset
        $head = if ($room -ge 8) { Read-Bytes $Stream $offset 8 } else { $null }
        if ($null -eq $head) { return [pscustomobject]@{ State = 'Error'; Reason = "$room byte(s) at offset $offset are too few for a box header" } }
        $size = Get-UInt $head 0 4 $true
        $name = [System.Text.Encoding]::Latin1.GetString($head, 4, 4) -replace '[^\x20-\x7E]', '?'
        $headerSize = 8
        if ($size -eq 1) {
            $extended = if ($room -ge 16) { Read-Bytes $Stream ($offset + 8) 8 } else { $null }
            if ($null -eq $extended) { return [pscustomobject]@{ State = 'Error'; Reason = "box '$name' at offset $offset announces a 64-bit size but the header is cut off" } }
            $size = Get-UInt $extended 0 8 $true
            $headerSize = 16
        }
        elseif ($size -eq 0) {
            $size = $room
        }
        if ($size -lt $headerSize -or $size -gt $room) {
            return [pscustomobject]@{ State = 'Error'; Reason = "box '$name' at offset $offset declares $size byte(s) with $room left (truncated file?)" }
        }
        if ($name -ceq $Type) { return [pscustomobject]@{ State = 'Found'; Start = $offset; HeaderSize = $headerSize; End = $offset + $size } }
        $offset += $size
    }
    return [pscustomobject]@{ State = 'Absent' }
}

function Read-Mp4CreationTime([System.IO.FileStream] $Stream) {
    $moov = Find-Box $Stream 0 $Stream.Length 'moov'
    if ($moov.State -eq 'Error') { return New-Carrier 'error' $moov.Reason }
    if ($moov.State -eq 'Absent') { return New-Carrier 'error' 'no moov box' }
    $mvhd = Find-Box $Stream ($moov.Start + $moov.HeaderSize) $moov.End 'mvhd'
    if ($mvhd.State -eq 'Error') { return New-Carrier 'error' "moov: $($mvhd.Reason)" }
    if ($mvhd.State -eq 'Absent') { return New-Carrier 'error' 'moov holds no mvhd box' }

    $first = $mvhd.Start + $mvhd.HeaderSize
    $payload = $mvhd.End - $first
    $version = if ($payload -ge 1) { Read-Bytes $Stream $first 1 } else { $null }
    if ($null -eq $version) { return New-Carrier 'error' 'mvhd is empty' }
    # Version 0 keeps 32-bit times, version 1 64-bit; both follow the 4 bytes of version and flags.
    if ($version[0] -eq 0) { $width = 4 } elseif ($version[0] -eq 1) { $width = 8 } else { return New-Carrier 'error' "mvhd has unknown version $($version[0])" }
    $field = if ($payload -ge (4 + $width)) { Read-Bytes $Stream ($first + 4) $width } else { $null }
    if ($null -eq $field) { return New-Carrier 'error' 'mvhd is too short to hold creation_time' }

    $seconds = Get-UInt $field 0 $width $true
    if ($seconds -lt 0) { return New-Carrier 'error' 'mvhd creation_time is beyond 2^63 seconds' }
    # Zero is what a muxer writes when it has no clock: the file carries no time, which is not a time of 1904.
    if ($seconds -eq 0) { return New-Carrier 'none' 'mvhd creation_time is 0 (unset)' }
    try {
        return New-Carrier 'time' '' $null $epoch1904.AddSeconds($seconds)
    }
    catch [System.ArgumentOutOfRangeException] {
        return New-Carrier 'error' "mvhd creation_time $seconds s since 1904 is outside the calendar"
    }
}

# --- one file ----------------------------------------------------------------------------------------------------------

function Format-Offset([timespan] $Offset) {
    return ('{0}{1:hh\:mm}' -f $(if ($Offset -lt [timespan]::Zero) { '-' } else { '+' }), $Offset)
}

function Measure-CaptureFile([string] $File) {
    $row = { param($Status, $Detail) [pscustomobject]@{ Status = $Status; File = $File; Detail = $Detail } }
    $extension = [System.IO.Path]::GetExtension($File).ToLowerInvariant()
    $kind = switch ($extension) { { $_ -in '.jpg', '.jpeg' } { 'jpeg' } { $_ -in '.mp4', '.m4v', '.m4a', '.mov' } { 'mp4' } default { '' } }
    if ($kind -eq '') { return & $row 'NO-CARRIER' "no creation time that this script reads in a '$extension' file" }

    try {
        $stream = [System.IO.File]::Open($File, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    }
    catch [System.IO.IOException], [System.UnauthorizedAccessException] {
        return & $row 'UNREADABLE' "cannot open the file: $($_.Exception.Message)"
    }
    try {
        $carrier = if ($kind -eq 'jpeg') { Read-JpegCaptureTime $stream } else { Read-Mp4CreationTime $stream }
    }
    finally {
        $stream.Dispose()
    }
    if ($carrier.State -eq 'error') { return & $row 'UNREADABLE' $carrier.Reason }
    if ($carrier.State -eq 'none') { return & $row 'NO-CARRIER' $carrier.Reason }

    $leaf = [System.IO.Path]::GetFileName($File)
    $match = [regex]::Match($leaf, $namePattern)
    $named = [datetime]::MinValue
    if (-not $match.Success -or -not [datetime]::TryParseExact("20$($match.Groups['date'].Value)_$($match.Groups['time'].Value)", 'yyyyMMdd_HHmmss', $invariant, [System.Globalization.DateTimeStyles]::None, [ref] $named)) {
        return & $row 'UNREADABLE' "the name holds no rule 3 time (<prefix>_<yyMMdd>_<HHmmss>..): $leaf"
    }

    if ($kind -eq 'jpeg') {
        $inside = $carrier.Wall
        $detail = "exif=$($inside.ToString($displayFormat, $invariant))"
        $offsetText = ''
    }
    else {
        $inside = [System.TimeZoneInfo]::ConvertTimeFromUtc($carrier.Utc, [System.TimeZoneInfo]::Local)
        $detail = "mvhd-utc=$($carrier.Utc.ToString('yyyy-MM-ddTHH:mm:ss', $invariant))Z mvhd-local=$($inside.ToString($displayFormat, $invariant))"
        $offsetText = " local-offset=$(Format-Offset ([System.TimeZoneInfo]::Local.GetUtcOffset($carrier.Utc)))"
    }
    $delta = [long] [math]::Round(($inside - $named).TotalSeconds)
    $detail += " name=$($named.ToString($displayFormat, $invariant)) delta=$('{0:+0;-0;+0}' -f $delta)s$offsetText"
    return & $row $(if ($delta -eq 0) { 'EQUAL' } else { 'DIFF' }) $detail
}

# --- run ---------------------------------------------------------------------------------------------------------------

function Resolve-CaptureInputs([string[]] $Entries) {
    $files = [System.Collections.Generic.List[string]]::new()
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $Entries) {
        $unreadable = { param($Reason) $rows.Add([pscustomobject]@{ Status = 'UNREADABLE'; File = $entry; Detail = $Reason }) }
        if (Test-Path -LiteralPath $entry -PathType Leaf) { $files.Add((Resolve-Path -LiteralPath $entry).ProviderPath) }
        elseif (Test-Path -LiteralPath $entry -PathType Container) { & $unreadable 'is a folder; name the files to measure' }
        elseif ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($entry)) {
            $found = @(Get-ChildItem -Path $entry -File -ErrorAction SilentlyContinue)
            if ($found.Count -eq 0) { & $unreadable 'matches no file' } else { foreach ($item in $found) { $files.Add($item.FullName) } }
        }
        else { & $unreadable 'file not found' }
    }
    return [pscustomobject]@{ Files = $files; Rows = $rows }
}

try {
    $entries = @((@($Path) + @($MorePath)) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($entries.Count -eq 0) {
        Write-Host 'capture-times: no file given; pass the captured files with -Path.'
        Write-Host 'capture-times: COULD NOT VERIFY'
        exit 2
    }

    $inputs = Resolve-CaptureInputs $entries
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $inputs.Rows) { $rows.Add($row) }
    foreach ($file in $inputs.Files) { $rows.Add((Measure-CaptureFile $file)) }

    foreach ($row in $rows) { Write-Host ('{0,-11} {1}  {2}' -f $row.Status, $row.File, $row.Detail) }
    $count = { param($Status) @($rows | Where-Object { $_.Status -eq $Status }).Count }
    $diff = & $count 'DIFF'
    $unreadable = & $count 'UNREADABLE'
    $noCarrier = & $count 'NO-CARRIER'
    Write-Host ("files={0} equal={1} diff={2} no-carrier={3} unreadable={4}" -f $rows.Count, (& $count 'EQUAL'), $diff, $noCarrier, $unreadable)

    if ($diff -gt 0) { $word = 'FAIL'; $code = 1 }
    elseif ($unreadable -gt 0) { $word = 'COULD NOT VERIFY'; $code = 2 }
    elseif ($noCarrier -gt 0) { $word = 'PASS WITH ADVISORIES'; $code = 3 }
    else { $word = 'PASS'; $code = 0 }
}
catch {
    # The line is owed on every path (CHECK-VERDICT 2.11); an unexpected failure is "could not verify", never a pass.
    Write-Host "capture-times: unexpected failure: $($_.Exception.Message)"
    $word = 'COULD NOT VERIFY'
    $code = 2
}

Write-Host "capture-times: $word"
exit $code
