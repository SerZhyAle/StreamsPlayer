<#
.SYNOPSIS
  Write the per-submission "What's new" into the ReleaseNotes row of a Partner Center listing export.

.DESCRIPTION
  build-store-listing-csv.ps1 deliberately never touches ReleaseNotes - the decks it fills are the
  durable listing copy, while release notes are dated prose written once per submission. This script is
  that one field and nothing else: it reads the export, maps the language columns by header name
  (Partner Center reorders them between exports), rewrites the single ReleaseNotes line with correct
  CSV quoting, and writes the result without a BOM, which the import rejects.

  The ten machine-translated languages take the English block, matching msix/store-listing.md: a
  release note is dated prose nobody proofreads twice.

  Output goes to msix/dist beside the packages and the other generated listing files, which is where
  this repo assembles everything a submission needs.

.EXAMPLE
  # Partner Center -> Store listings -> Export listing, then:
  pwsh -NoProfile -File tools/store/write-release-notes.ps1 `
    -Export ~/Downloads/listingData-9NBTD5SXB8TB-<id>.csv -Version 26.0806.2225

  Reads msix/listing/release-notes/<version>.<en-us|ru|uk>.txt and writes
  msix/dist/store-listing-import.csv.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Export,
    # Picks up msix/listing/release-notes/<Version>.<code>.txt for the three authored languages.
    [string] $Version,
    [string] $Out,
    [string] $NotesEn,
    [string] $NotesRu,
    [string] $NotesUk
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Out) { $Out = Join-Path $root 'msix/dist/store-listing-import.csv' }

if ($Version) {
    $deck = Join-Path $root 'msix/listing/release-notes'
    foreach ($pair in @(@('en-us', 'NotesEn'), @('ru', 'NotesRu'), @('uk', 'NotesUk'))) {
        $file = Join-Path $deck "$Version.$($pair[0]).txt"
        if (-not (Test-Path $file)) { throw "No release note for $($pair[0]) at $file." }
        Set-Variable -Name $pair[1] -Value ((Get-Content $file -Raw) -replace '\s+$', '')
    }
}

foreach ($required in 'NotesEn', 'NotesRu', 'NotesUk') {
    if (-not (Get-Variable $required -ValueOnly)) { throw "Pass -Version, or all three of -NotesEn, -NotesRu and -NotesUk." }
}

# Partner Center rejects the whole file over one long cell, and reports it as a generic failure.
foreach ($pair in @(@('en-us', $NotesEn), @('ru', $NotesRu), @('uk', $NotesUk))) {
    if ($pair[1].Length -gt 1500) { throw "The $($pair[0]) note is $($pair[1].Length) characters; the limit is 1500." }
}

$directory = Split-Path $Out -Parent
if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

$text = [IO.File]::ReadAllText($Export)
$header = ($text -split "`r?`n")[0]
$columns = $header -split ','

# Every newline written into this file must be CRLF, inside a quoted cell as much as between records.
# The export is CRLF throughout, and Partner Center's reader treats a bare LF as ordinary text rather
# than as the end of a record: a lone LF closing the ReleaseNotes row leaves the last column's quoted
# cell open, so it swallows the next record and the import fails with "<language> ReleaseNotes is too
# long" - naming only the last language, and never the real cause. Cost one rejected submission on
# 2026-08-07.
function ConvertTo-Crlf([string] $value) { ($value -replace "`r`n", "`n") -replace "`n", "`r`n" }

$notes = @{
    'en-us' = ConvertTo-Crlf $NotesEn
    'ru'    = ConvertTo-Crlf $NotesRu
    'uk'    = ConvertTo-Crlf $NotesUk
}

# Measured after the conversion, because that is the text Partner Center counts.
foreach ($code in 'en-us', 'ru', 'uk') {
    if ($notes[$code].Length -gt 1500) { throw "The $code note is $($notes[$code].Length) characters once CRLF-normalised; the limit is 1500." }
}

$row = [System.Collections.Generic.List[string]]::new()
foreach ($column in $columns) {
    $value = switch ($column) {
        'Field' { 'ReleaseNotes' }
        'ID' { '3' }
        'Type (Type)' { 'Text' }
        'default' { '' }
        default { if ($notes.ContainsKey($column)) { $notes[$column] } else { $notes['en-us'] } }
    }
    # Quote whenever the value could be misread; double every embedded quote. Empty stays bare, as the
    # export writes it.
    if ($value -eq '') { $row.Add('') }
    else { $row.Add('"' + $value.Replace('"', '""') + '"') }
}

# The pattern consumes the row's terminator too, so the replacement supplies its own CRLF rather than
# inheriting whatever the match left behind.
#
# The row is empty only on the very first export. From the second submission on it carries the previous
# version's notes, and those cells are quoted and contain CRLFs of their own, so the record spans many
# physical lines. The pattern is therefore a real CSV record matcher - a field is either a quoted cell
# (with doubled quotes inside) or a bare run with no comma, quote or newline - anchored at a line that
# starts with this row's own key. Nothing else in the export starts with `ReleaseNotes,3,Text,`, and the
# match count is asserted below, so the anchor cannot drift onto another record.
$replacement = ($row -join ',') + "`r`n"
$field = '(?:"(?:[^"]|"")*"|[^,\r\n]*)'
$pattern = "(?m)^ReleaseNotes,3,Text,$field(?:,$field)*\r?\n"
$matched = [regex]::Matches($text, $pattern)
if ($matched.Count -ne 1) {
    throw "Expected exactly one ReleaseNotes record in the export; found $($matched.Count). Refusing to guess."
}
$text = $text.Remove($matched[0].Index, $matched[0].Length).Insert($matched[0].Index, $replacement)

$strayLineFeeds = [regex]::Matches($text, "(?<!\r)\n").Count
if ($strayLineFeeds -gt 0) { throw "$strayLineFeeds bare line feed(s) in the output. Partner Center needs CRLF everywhere; refusing to write a file that will be rejected." }

[IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false)))

$lengths = foreach ($code in 'en-us', 'ru', 'uk') { "{0}: {1}" -f $code, $notes[$code].Length }
Write-Host "Wrote $Out"
Write-Host ("Languages: {0} column(s); ReleaseNotes filled in every one." -f ($columns.Count - 4))
Write-Host ("Note lengths, CRLF-normalised - {0} (Partner Center limit 1500)" -f ($lengths -join ', '))
Write-Host "Line endings: CRLF throughout, no bare line feed."
