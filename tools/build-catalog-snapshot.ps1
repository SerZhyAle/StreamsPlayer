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

    The download address and the size ceiling are read from the built StreamsPlayer.Core assembly
    (StreamCatalogService.CatalogUrl, BundledCatalogSnapshot.MaximumSnapshotBytes) the way
    tools/InterfaceLanguages.ps1 reads the language registry. Neither value is restated here, so the
    script and the application cannot drift apart.

    Usage:
        ./tools/build-catalog-snapshot.ps1                    regenerate the artifact
        ./tools/build-catalog-snapshot.ps1 -Check             verify the tracked artifact, write nothing
        ./tools/build-catalog-snapshot.ps1 -Check -Offline    the same, minus the freshness comparison

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
    # Which build of StreamsPlayer.Core to read the contract from. Release first, then Debug.
    [ValidateSet('Release', 'Debug')] [string] $Configuration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactPath = Join-Path $root 'src/StreamsPlayer.Core/Resources/catalog-snapshot.zip'

function Get-CoreContract {
    param([string] $Configuration)

    $configurations = if ($Configuration) { @($Configuration) } else { @('Release', 'Debug') }
    $assemblyPath = $null
    foreach ($candidate in $configurations) {
        $path = Join-Path $root "src/StreamsPlayer.Core/bin/$candidate/net10.0/StreamsPlayer.Core.dll"
        if (Test-Path -LiteralPath $path) { $assemblyPath = $path; break }
    }

    if (-not $assemblyPath) {
        throw "StreamsPlayer.Core.dll not found under src/StreamsPlayer.Core/bin ($($configurations -join ', ')). " +
              'The catalog address and the size ceiling are read from the built assembly, so build first: ' +
              'dotnet build StreamsPlayer.sln -c Release'
    }

    # Load from bytes, not LoadFrom: LoadFrom locks the file and a later dotnet build in the same
    # session would fail with the assembly still held open.
    $assembly = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($assemblyPath))
    $service = $assembly.GetType('StreamsPlayer.Core.StreamCatalogService', $true)
    $snapshot = $assembly.GetType('StreamsPlayer.Core.BundledCatalogSnapshot', $true)

    return [pscustomobject]@{
        CatalogUrl   = [string] $service.GetField('CatalogUrl').GetRawConstantValue()
        MaximumBytes = [int] $snapshot.GetField('MaximumSnapshotBytes').GetRawConstantValue()
    }
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

# The bank's is_live column is untrusted maintainer metadata, so only an explicit false is dropped.
# Excluding rows whose value is absent or unparsable would silently shrink the snapshot on a column the
# publisher is free to leave empty; what has to go is the 881 traffic cameras, which say false outright.
function Test-RowIsLive {
    param([string] $Line, [int] $IsLiveColumn)

    if ($IsLiveColumn -lt 0) { return $true }
    $fields = Split-CsvLine -Line $Line
    if ($fields.Count -le $IsLiveColumn) { return $true }

    $value = $fields[$IsLiveColumn].Trim().ToLowerInvariant()
    return -not ($value -in @('false', '0', 'no'))
}

# RFC 4180 enough for this one job: the bank quotes fields containing commas and doubles inner quotes.
function Split-CsvLine {
    param([string] $Line)

    $fields = [System.Collections.Generic.List[string]]::new()
    $current = [System.Text.StringBuilder]::new()
    $inQuotes = $false
    for ($i = 0; $i -lt $Line.Length; $i++) {
        $char = $Line[$i]
        if ($inQuotes) {
            if ($char -eq '"') {
                if ($i + 1 -lt $Line.Length -and $Line[$i + 1] -eq '"') { [void] $current.Append('"'); $i++ }
                else { $inQuotes = $false }
            }
            else { [void] $current.Append($char) }
        }
        elseif ($char -eq '"') { $inQuotes = $true }
        elseif ($char -eq ',') { $fields.Add($current.ToString()); [void] $current.Clear() }
        else { [void] $current.Append($char) }
    }

    $fields.Add($current.ToString())
    return , $fields.ToArray()
}

function Write-SnapshotZip {
    param([string] $Path, [string] $Csv, [byte[]] $Atlas, [string] $Metadata)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew)
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
    finally { $stream.Dispose() }
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

            $rows = ([System.Text.Encoding]::UTF8.GetString((Read-ZipEntryBytes -Archive $archive -Name 'streams.csv')) -split "`n").Count - 1
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

Write-Host "Downloading the published bank from $($contract.CatalogUrl)"
$response = Invoke-WebRequest -Uri $contract.CatalogUrl -TimeoutSec 300
$sourceDate = [datetimeoffset]::UtcNow
if ($response.Headers.ContainsKey('Last-Modified')) {
    $parsedHeader = [datetimeoffset]::MinValue
    if ([datetimeoffset]::TryParse([string] $response.Headers['Last-Modified'], [ref] $parsedHeader)) {
        $sourceDate = $parsedHeader
    }
}

$bankStream = New-Object System.IO.MemoryStream(, $response.Content)
try {
    $bank = New-Object System.IO.Compression.ZipArchive($bankStream, [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        $csvBytes = Read-ZipEntryBytes -Archive $bank -Name 'streams.csv'
        if (-not $csvBytes) { throw 'The published bank contains no streams.csv.' }
        $atlas = Read-ZipEntryBytes -Archive $bank -Name 'favicon-atlas.png'
    }
    finally { $bank.Dispose() }
}
finally { $bankStream.Dispose() }

$lines = [System.Text.Encoding]::UTF8.GetString($csvBytes) -split "`r?`n"
$header = $lines[0]
$columns = Split-CsvLine -Line $header
$isLiveColumn = [array]::IndexOf(@($columns | ForEach-Object { $_.Trim().ToLowerInvariant() }), 'is_live')
if ($isLiveColumn -lt 0) {
    Write-Warning 'The published bank has no is_live column; nothing is excluded from the snapshot.'
}

$kept = [System.Collections.Generic.List[string]]::new()
$dropped = 0
foreach ($line in $lines[1..($lines.Count - 1)]) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if (Test-RowIsLive -Line $line -IsLiveColumn $isLiveColumn) { $kept.Add($line) }
    else { $dropped++ }
}

if ($kept.Count -eq 0) { throw 'Every row was excluded; the snapshot would carry no channels.' }

$csv = ($header, $kept) | ForEach-Object { $_ } | Join-String -Separator "`n"
$metadata = [ordered]@{
    sourceUrl  = $contract.CatalogUrl
    sourceDate = $sourceDate.ToString('o')
} | ConvertTo-Json -Compress

Write-SnapshotZip -Path $artifactPath -Csv $csv -Atlas $atlas -Metadata $metadata

Write-Host "Kept $($kept.Count) channels, dropped $dropped that are not live streams."
# $sourceDate is the Last-Modified this download already reported, so the freshness comparison costs
# no second request - and it still runs, which is what keeps the -Check message honest after a rebuild.
Test-Snapshot -Path $artifactPath -MaximumBytes $contract.MaximumBytes -PublishedDate $sourceDate
Write-Host "Wrote $artifactPath - commit it; it is generated output, never hand-edited."
