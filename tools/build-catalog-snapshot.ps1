<#
    SP-0052: builds the catalog snapshot that ships inside the application binary.

    The snapshot is generated output that is committed, the same contract tools/site/build-site.ps1
    states for the site: re-run this script and commit whatever it changes. Never hand-edit the archive.

    What it produces - src/StreamsPlayer.Core/Resources/catalog-snapshot.zip:

        streams.csv        the published bank's rows, minus the ones that are not live streams
        favicon-atlas.png  the published atlas, byte-identical, so favicon_index stays valid
        snapshot.json      provenance: sourceUrl and sourceDate

    streams.csv must be the FIRST entry - StreamBankReader rejects the archive otherwise, and that
    failure would reach users as "the built-in list is unreadable" rather than the person who ran this.

    The download address, the download ceiling, the publish-window retry schedule and the snapshot ceiling
    are read from the built StreamsPlayer.Core assembly (StreamCatalogService.CatalogUrl and
    .MaximumArchiveBytes, PublishWindowRetry.Default, BundledCatalogSnapshot.MaximumSnapshotBytes) the way
    tools/InterfaceLanguages.ps1 reads the language registry. None is restated here, so the script and the
    application cannot drift apart. The download behaves as the product's refresh does (SP-0157): it stops
    at the ceiling, and a 404, a short read or an unreadable ZIP - the publish window, STREAM-BANK rule 11 -
    is retried on the same schedule; any other failure stops on the first attempt.

    The CSV is read by records, not lines (RFC 4180, the same rules as the product's Rfc4180Csv): the bank
    allows a newline inside a quoted field, so a record is kept or dropped whole, and every count is a count
    of channels.

    Usage:
        ./tools/build-catalog-snapshot.ps1                    regenerate the artifact
        ./tools/build-catalog-snapshot.ps1 -Check             verify the tracked artifact, write nothing
        ./tools/build-catalog-snapshot.ps1 -Check -Offline    the same, minus the freshness comparison
        ./tools/build-catalog-snapshot.ps1 -BankPath bank.zip -OutputPath out.zip
                                                              build from a local bank ZIP (a fixture) into
                                                              another file; nothing is downloaded

    -Check verifies that the artifact exists, is within the ceiling, has its entries in the order the
    reader requires, carries a usable source date, and - SP-0066 - is not older than the bank currently
    published at CatalogUrl. The publisher stamps no hash for this payload (artwork-manifest.json covers
    the tile packs, not the catalog), so freshness is the asset's Last-Modified, which is the same header
    a regeneration records as sourceDate.

    That comparison is fail-closed: an unreachable host or a missing header fails the check rather than
    passing it quietly. Pass -Offline to skip it deliberately; the structural checks always run.
#>

[CmdletBinding()]
param(
    # Verify the tracked artifact and exit non-zero if it is unusable. Writes nothing.
    [switch] $Check,
    # Skip the freshness comparison, the only part of -Check that needs a network.
    [switch] $Offline,
    # Which build of StreamsPlayer.Core to read the contract from. Unpinned, the most recently built one (SP-0133).
    [ValidateSet('Release', 'Debug')] [string] $Configuration,
    # Build from this local bank ZIP instead of downloading the published one. For fixtures and diagnosis.
    [string] $BankPath,
    # Write the snapshot here instead of the tracked artifact. Required with -BankPath, so a fixture run can
    # never overwrite the committed snapshot.
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Resolve-CoreAssemblyPath: the one rule for which built Core a tool reads (SP-0133).
. (Join-Path $PSScriptRoot 'InterfaceLanguages.ps1')
$artifactPath = Join-Path $root 'src/StreamsPlayer.Core/Resources/catalog-snapshot.zip'

function Get-CoreContract {
    param([string] $Configuration)

    $pinned = if ($Configuration) { @{ Configuration = $Configuration } } else { @{} }
    $assemblyPath = Resolve-CoreAssemblyPath @pinned `
        -Purpose 'The catalog address and the size ceiling are read from the built assembly'

    # Load from bytes, not LoadFrom: LoadFrom locks the file and a later dotnet build in the same
    # session would fail with the assembly still held open.
    $assembly = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($assemblyPath))
    $service = $assembly.GetType('StreamsPlayer.Core.StreamCatalogService', $true)
    $snapshot = $assembly.GetType('StreamsPlayer.Core.BundledCatalogSnapshot', $true)
    $retry = $assembly.GetType('StreamsPlayer.Core.PublishWindowRetry', $true)

    return [pscustomobject]@{
        CatalogUrl          = [string] $service.GetField('CatalogUrl').GetRawConstantValue()
        MaximumArchiveBytes = [long] $service.GetField('MaximumArchiveBytes').GetRawConstantValue()
        RetryDelays         = @($retry.GetProperty('Default').GetValue($null).Delays)
        MaximumBytes        = [int] $snapshot.GetField('MaximumSnapshotBytes').GetRawConstantValue()
    }
}

# SP-0157: one download attempt, bounded by the product's archive ceiling. Returns @{ Bytes; LastModified } on
# success, @{ Window = <cause> } when the attempt hit the publish window (a 404, a body shorter than declared,
# a ZIP that does not open), and throws on anything else - the same split PublishWindowRetry.Classify makes.
function Receive-BankOnce {
    # SP-0184 (S2-6): HttpClient.Timeout ends with the response headers under ResponseHeadersRead, so the body read
    # carries a deadline of its own - a stalled server would otherwise hold the release script forever.
    param([System.Net.Http.HttpClient] $Client, [string] $Url, [long] $MaximumBytes, [int] $BodyTimeoutSeconds = 300)

    $response = $Client.GetAsync($Url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        if ($response.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) { return [pscustomobject]@{ Window = 'NotFound' } }
        if (-not $response.IsSuccessStatusCode) { throw "Downloading $Url failed: HTTP $([int] $response.StatusCode) $($response.ReasonPhrase)." }

        $declared = $response.Content.Headers.ContentLength
        if ($null -ne $declared -and $declared -gt $MaximumBytes) {
            throw "$Url declares $declared bytes, over the $MaximumBytes-byte ceiling StreamCatalogService.MaximumArchiveBytes sets."
        }

        $buffer = [System.IO.MemoryStream]::new()
        $chunk = [byte[]]::new(81920)
        $source = $response.Content.ReadAsStream()
        $deadline = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($BodyTimeoutSeconds))
        try {
            while ($true) {
                try { $read = $source.ReadAsync($chunk, 0, $chunk.Length, $deadline.Token).GetAwaiter().GetResult() }
                catch {
                    if ($_.Exception -is [System.OperationCanceledException]) {
                        throw "$Url did not finish sending within $BodyTimeoutSeconds seconds; download stopped."
                    }
                    for ($e = $_.Exception; $e; $e = $e.InnerException) {
                        if ($e -is [System.IO.IOException]) { return [pscustomobject]@{ Window = 'ShortRead' } }
                    }
                    throw
                }
                if ($read -le 0) { break }
                if ($buffer.Length + $read -gt $MaximumBytes) {
                    throw "$Url sent more than the $MaximumBytes-byte ceiling StreamCatalogService.MaximumArchiveBytes sets; download stopped."
                }
                $buffer.Write($chunk, 0, $read)
            }
        }
        finally { $deadline.Dispose(); $source.Dispose() }
        if ($null -ne $declared -and $buffer.Length -ne $declared) { return [pscustomobject]@{ Window = 'ShortRead' } }

        $bytes = $buffer.ToArray()
        try {
            $probe = New-Object System.IO.Compression.ZipArchive([System.IO.MemoryStream]::new($bytes), [System.IO.Compression.ZipArchiveMode]::Read)
            $probe.Dispose()
        }
        catch {
            for ($e = $_.Exception; $e; $e = $e.InnerException) {
                if ($e -is [System.IO.InvalidDataException]) { return [pscustomobject]@{ Window = 'TruncatedArchive' } }
            }
            throw
        }
        return [pscustomobject]@{ Window = $null; Bytes = $bytes; LastModified = $response.Content.Headers.LastModified }
    }
    finally { $response.Dispose() }
}

# SP-0157: the product's refresh, as far as a maintainer tool needs it - PublishWindowRetry.Default's schedule
# around a bounded download. When the schedule is spent the last cause is the error.
function Get-PublishedBank {
    param([string] $Url, [long] $MaximumBytes, [TimeSpan[]] $Delays)

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(300)
    try {
        $attempts = $Delays.Count + 1
        for ($attempt = 1; ; $attempt++) {
            $result = Receive-BankOnce -Client $client -Url $Url -MaximumBytes $MaximumBytes
            if (-not $result.Window) { return $result }
            if ($attempt -ge $attempts) {
                throw "Downloading $Url failed after $attempts attempts: the last one ended in the publish window ($($result.Window)). If it persists, the asset is missing or damaged rather than being replaced."
            }
            $delay = $Delays[$attempt - 1]
            Write-Host "    publish window ($($result.Window)); retrying in $($delay.TotalSeconds) s - attempt $($attempt + 1) of $attempts"
            Start-Sleep -Milliseconds ([int] $delay.TotalMilliseconds)
        }
    }
    finally { $client.Dispose() }
}

function Read-ZipEntryBytes {
    param([System.IO.Compression.ZipArchive] $Archive, [string] $Name)

    $entry = $Archive.Entries | Where-Object { $_.FullName -like "*$Name" } | Select-Object -First 1
    if (-not $entry) { return $null }

    $stream = $entry.Open()
    try {
        $buffer = New-Object System.IO.MemoryStream
        $stream.CopyTo($buffer)
        # The leading comma keeps PowerShell from unrolling the byte array into the pipeline.
        return , $buffer.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

# The bank's is_live column is untrusted maintainer metadata, so only a row the app itself reads as not
# live is dropped - the same rule as StreamCatalogCsvParser (SP-0108, STREAM-BANK 03 section 2.3): "true"
# is the only true, any other non-empty value is false, and a blank cell says nothing. Excluding blank rows
# would silently shrink the snapshot on a column the publisher is free to leave empty; what has to go is
# the 881 traffic cameras, which say false outright.
function Test-RowIsLive {
    param([string[]] $Fields, [int] $IsLiveColumn)

    if ($IsLiveColumn -lt 0 -or $Fields.Count -le $IsLiveColumn) { return $true }
    $value = $Fields[$IsLiveColumn].Trim()
    # -eq on strings ignores case, as the product's OrdinalIgnoreCase comparison does.
    return ($value.Length -eq 0) -or ($value -eq 'true')
}

# SP-0157: the CSV split into RECORDS, each with its raw text (so a kept record is written back byte for byte,
# embedded newlines included) and its fields. The rules are the product's Rfc4180Csv, restated because that
# class returns fields only: a quote opens a quoted field only at the field's start, "" inside one is a quote,
# CR, LF and CRLF end a record outside quotes and are data inside them, and the text may not end inside quotes.
# Compiled rather than scripted: a character loop over the whole bank in PowerShell takes minutes.
if (-not ('SnapshotCsvRecords' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;

public sealed class SnapshotCsvRecord
{
    public SnapshotCsvRecord(string raw, string[] fields) { Raw = raw; Fields = fields; }
    public string Raw { get; }
    public string[] Fields { get; }
}

public static class SnapshotCsvRecords
{
    public static List<SnapshotCsvRecord> Split(string text)
    {
        var records = new List<SnapshotCsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; }
                    else { quoted = false; }
                }
                else { field.Append(character); }
                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                case '\n':
                    var end = index;
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') { index++; }
                    Complete(end);
                    start = index + 1;
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (quoted) { throw new FormatException("The CSV ends inside a quoted field."); }
        if (field.Length > 0 || fields.Count > 0) { Complete(text.Length); }
        return records;

        void Complete(int end)
        {
            fields.Add(field.ToString());
            field.Clear();
            records.Add(new SnapshotCsvRecord(text.Substring(start, end - start), fields.ToArray()));
            fields.Clear();
        }
    }
}
'@
}

# A record the product would list: the header's name and url cells both non-blank (StreamCatalogCsvParser).
# A blank line is a record of one empty field and is not a channel.
function Test-RecordIsChannel {
    param([string[]] $Fields, [int] $NameColumn, [int] $UrlColumn)

    if ($NameColumn -lt 0 -or $UrlColumn -lt 0) { return ($Fields -join '').Trim().Length -gt 0 }
    $name = if ($Fields.Count -gt $NameColumn) { $Fields[$NameColumn].Trim() } else { '' }
    $url = if ($Fields.Count -gt $UrlColumn) { $Fields[$UrlColumn].Trim() } else { '' }
    return $name.Length -gt 0 -and $url.Length -gt 0
}

# Column positions by trimmed, case-insensitive header name, BOM ignored, first occurrence wins - as the product.
function Get-ColumnIndex {
    param([string[]] $Header, [string] $Name)

    for ($i = 0; $i -lt $Header.Count; $i++) {
        if ($Header[$i].Trim().TrimStart([char] 0xFEFF) -eq $Name) { return $i }
    }
    return -1
}

# The number of channels a snapshot CSV carries, counted by records.
function Measure-CsvChannels {
    param([string] $Csv)

    $records = [SnapshotCsvRecords]::Split($Csv)
    if ($records.Count -eq 0) { return 0 }
    $header = $records[0].Fields
    $name = Get-ColumnIndex -Header $header -Name 'name'
    $url = Get-ColumnIndex -Header $header -Name 'url'
    $count = 0
    for ($i = 1; $i -lt $records.Count; $i++) {
        if (Test-RecordIsChannel -Fields $records[$i].Fields -NameColumn $name -UrlColumn $url) { $count++ }
    }
    return $count
}

function Write-SnapshotZip {
    param([string] $Path, [string] $Csv, [byte[]] $Atlas, [string] $Metadata)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }

    # SP-0184 (S2-5): the archive is written beside the target and moved over it only when complete, so a failure
    # part-way never leaves the tracked snapshot deleted or half-written.
    $temporary = "$Path.tmp"
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }

    $stream = [System.IO.File]::Open($temporary, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            # Order matters: streams.csv first, then the atlas, then the provenance.
            Add-ZipEntry -Archive $archive -Name 'streams.csv' -Bytes ([System.Text.Encoding]::UTF8.GetBytes($Csv))
            if ($Atlas) { Add-ZipEntry -Archive $archive -Name 'favicon-atlas.png' -Bytes $Atlas }
            Add-ZipEntry -Archive $archive -Name 'snapshot.json' -Bytes ([System.Text.Encoding]::UTF8.GetBytes($Metadata))
        }
        finally { $archive.Dispose() }
    }
    catch {
        $stream.Dispose()
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        throw
    }
    $stream.Dispose()
    [System.IO.File]::Move($temporary, $Path, $true)
}

function Add-ZipEntry {
    param([System.IO.Compression.ZipArchive] $Archive, [string] $Name, [byte[]] $Bytes)

    $entry = $Archive.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) }
    finally { $stream.Dispose() }
}

# SP-0066: the publisher stamps no hash for stream-catalog.zip, so the only statement it makes about
# the bank's age is the asset's Last-Modified - the same header a regeneration records as sourceDate.
# Every way of failing to read it throws: a release must not proceed on an unproven comparison.
function Get-PublishedBankDate {
    param([string] $Url)

    $offlineHint = 'Pass -Offline to run the structural checks without this comparison.'
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Head -TimeoutSec 60
    }
    catch {
        throw "Could not reach $Url to learn whether a newer bank has been published: " +
              "$($_.Exception.Message). $offlineHint"
    }

    if (-not $response.Headers.ContainsKey('Last-Modified')) {
        throw "$Url answered without a Last-Modified header, so the published bank's age is unknown. $offlineHint"
    }

    $published = [datetimeoffset]::MinValue
    $header = [string] $response.Headers['Last-Modified']
    if (-not [datetimeoffset]::TryParse($header, [ref] $published)) {
        throw "$Url returned an unparsable Last-Modified ('$header'), so the published bank's age is unknown. $offlineHint"
    }

    return $published
}

function Test-Snapshot {
    param(
        [string] $Path,
        [int] $MaximumBytes,
        # The published bank's Last-Modified. $null skips the comparison and $SkipReason then says why.
        [object] $PublishedDate,
        [string] $SkipReason
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "No catalog snapshot at $Path. Run this script without -Check to generate it."
    }

    $size = (Get-Item -LiteralPath $Path).Length
    if ($size -gt $MaximumBytes) {
        throw "The catalog snapshot is $size bytes, over the declared ceiling of $MaximumBytes. " +
              'Every build and every store update pays this; raise BundledCatalogSnapshot.MaximumSnapshotBytes ' +
              'deliberately or narrow what the snapshot carries.'
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Read)
        try {
            if ($archive.Entries.Count -eq 0 -or -not $archive.Entries[0].FullName.EndsWith('streams.csv')) {
                throw 'streams.csv must be the first entry of the catalog snapshot; StreamBankReader rejects it otherwise.'
            }

            $metadataBytes = Read-ZipEntryBytes -Archive $archive -Name 'snapshot.json'
            if (-not $metadataBytes) { throw 'The catalog snapshot carries no snapshot.json.' }

            $metadata = [System.Text.Encoding]::UTF8.GetString($metadataBytes) | ConvertFrom-Json
            $parsed = [datetimeoffset]::MinValue
            if (-not [datetimeoffset]::TryParse($metadata.sourceDate, [ref] $parsed)) {
                throw "snapshot.json carries no usable sourceDate (got '$($metadata.sourceDate)')."
            }

            if ($null -ne $PublishedDate) {
                $published = [datetimeoffset] $PublishedDate
                if ($published -gt $parsed) {
                    throw "The bundled snapshot was taken $($parsed.ToString('u')) but the bank published at " +
                          "CatalogUrl is newer ($($published.ToString('u'))). Releasing this would ship a channel " +
                          'list that was already out of date on release day: regenerate the snapshot and commit it.'
                }
            }

            $rows = Measure-CsvChannels -Csv ([System.Text.Encoding]::UTF8.GetString((Read-ZipEntryBytes -Archive $archive -Name 'streams.csv')))
            # SP-0184 (S2-4): a snapshot with no channels is structurally valid and useless - the first-run window
            # would offer nothing - so the check fails it rather than reporting "OK, 0 channels".
            if ($rows -le 0) { throw 'The catalog snapshot holds no channels: streams.csv has no record with both a name and a url.' }
            Write-Host "Catalog snapshot OK: $size bytes of $MaximumBytes, $rows channels, taken $($parsed.ToString('yyyy-MM-dd'))."
            if ($null -ne $PublishedDate) {
                Write-Host "Not older than the published bank (published $(([datetimeoffset] $PublishedDate).ToString('u')))."
            }
            else {
                Write-Host "Freshness against the published bank NOT compared - $SkipReason."
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

$contract = Get-CoreContract -Configuration $Configuration

if ($Check) {
    $publishedDate = $null
    $skipReason = $null
    if ($Offline) { $skipReason = '-Offline was passed' }
    else { $publishedDate = Get-PublishedBankDate -Url $contract.CatalogUrl }

    Test-Snapshot -Path $artifactPath -MaximumBytes $contract.MaximumBytes `
                  -PublishedDate $publishedDate -SkipReason $skipReason
    exit 0
}

if ($BankPath) {
    if (-not $OutputPath) { throw '-BankPath needs -OutputPath: a local bank must never overwrite the tracked snapshot.' }
    $BankPath = (Resolve-Path -LiteralPath $BankPath).Path
    $bankFile = Get-Item -LiteralPath $BankPath
    if ($bankFile.Length -gt $contract.MaximumArchiveBytes) {
        throw "$BankPath is $($bankFile.Length) bytes, over the $($contract.MaximumArchiveBytes)-byte ceiling StreamCatalogService.MaximumArchiveBytes sets."
    }
    Write-Host "Reading the bank from $BankPath"
    $bankBytes = [System.IO.File]::ReadAllBytes($BankPath)
    $sourceUrl = $bankFile.Name
    # Whole seconds, as a Last-Modified header carries: snapshot.json round-trips through ConvertFrom-Json,
    # which drops the fraction, and the freshness comparison would then call the file newer than itself.
    $stamp = $bankFile.LastWriteTimeUtc
    $sourceDate = [datetimeoffset]::new($stamp.AddTicks(-($stamp.Ticks % [TimeSpan]::TicksPerSecond)))
}
else {
    Write-Host "Downloading the published bank from $($contract.CatalogUrl)"
    $download = Get-PublishedBank -Url $contract.CatalogUrl -MaximumBytes $contract.MaximumArchiveBytes -Delays $contract.RetryDelays
    $bankBytes = $download.Bytes
    $sourceUrl = $contract.CatalogUrl
    $sourceDate = if ($null -ne $download.LastModified) { [datetimeoffset] $download.LastModified } else { [datetimeoffset]::UtcNow }
}
$target = if ($OutputPath) { [System.IO.Path]::GetFullPath($OutputPath) } else { $artifactPath }

$bankStream = New-Object System.IO.MemoryStream(, $bankBytes)
try {
    $bank = New-Object System.IO.Compression.ZipArchive($bankStream, [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        $csvBytes = Read-ZipEntryBytes -Archive $bank -Name 'streams.csv'
        if (-not $csvBytes) { throw 'The bank contains no streams.csv.' }
        $atlas = Read-ZipEntryBytes -Archive $bank -Name 'favicon-atlas.png'
    }
    finally { $bank.Dispose() }
}
finally { $bankStream.Dispose() }

$records = [SnapshotCsvRecords]::Split([System.Text.Encoding]::UTF8.GetString($csvBytes))
if ($records.Count -eq 0) { throw 'The bank''s streams.csv is empty.' }
$header = $records[0]
$isLiveColumn = Get-ColumnIndex -Header $header.Fields -Name 'is_live'
$nameColumn = Get-ColumnIndex -Header $header.Fields -Name 'name'
$urlColumn = Get-ColumnIndex -Header $header.Fields -Name 'url'
if ($isLiveColumn -lt 0) {
    Write-Warning 'The bank has no is_live column; nothing is excluded from the snapshot.'
}

# Whole records are kept or dropped, and only records are counted (SP-0157): a quoted field may span lines.
$kept = [System.Collections.Generic.List[string]]::new()
$keptChannels = 0
$dropped = 0
$blank = 0
for ($i = 1; $i -lt $records.Count; $i++) {
    $record = $records[$i]
    if ($record.Raw.Trim().Length -eq 0) { $blank++; continue }
    if (Test-RowIsLive -Fields $record.Fields -IsLiveColumn $isLiveColumn) {
        $kept.Add($record.Raw)
        if (Test-RecordIsChannel -Fields $record.Fields -NameColumn $nameColumn -UrlColumn $urlColumn) { $keptChannels++ }
    }
    else { $dropped++ }
}

if ($keptChannels -eq 0) { throw 'Every record was excluded; the snapshot would carry no channels.' }

$csv = (@($header.Raw) + $kept) -join "`n"
$metadata = [ordered]@{
    sourceUrl  = $sourceUrl
    sourceDate = $sourceDate.ToString('o')
} | ConvertTo-Json -Compress

Write-SnapshotZip -Path $target -Csv $csv -Atlas $atlas -Metadata $metadata

$multiLine = @($kept | Where-Object { $_.Contains("`n") -or $_.Contains("`r") }).Count
Write-Host ("Read {0} records; kept {1} ({2} channels, {3} spanning several lines), dropped {4} that are not live streams, skipped {5} blank." -f `
    ($records.Count - 1), $kept.Count, $keptChannels, $multiLine, $dropped, $blank)
# $sourceDate is the Last-Modified this download already reported, so the freshness comparison costs
# no second request - and it still runs, which is what keeps the -Check message honest after a rebuild.
Test-Snapshot -Path $target -MaximumBytes $contract.MaximumBytes -PublishedDate $sourceDate
if ($OutputPath) { Write-Host "Wrote $target (not the tracked snapshot)." }
else { Write-Host "Wrote $target - commit it; it is generated output, never hand-edited." }
