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
$notes = @{ 'en-us' = $NotesEn; 'ru' = $NotesRu; 'uk' = $NotesUk }

$row = [System.Collections.Generic.List[string]]::new()
foreach ($column in $columns) {
    $value = switch ($column) {
        'Field' { 'ReleaseNotes' }
        'ID' { '3' }
        'Type (Type)' { 'Text' }
        'default' { '' }
        default { if ($notes.ContainsKey($column)) { $notes[$column] } else { $NotesEn } }
    }
    # Quote whenever the value could be misread; double every embedded quote. Empty stays bare, as the
    # export writes it.
    if ($value -eq '') { $row.Add('') }
    else { $row.Add('"' + $value.Replace('"', '""') + '"') }
}

$replacement = $row -join ','
$pattern = '(?m)^ReleaseNotes,3,Text,[^"\r\n]*\r?$'
if ($text -notmatch $pattern) { throw 'The ReleaseNotes row was not found in its expected empty form. Refusing to guess.' }
$text = [regex]::Replace($text, $pattern, { $replacement })

[IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false)))

$lengths = foreach ($key in 'en-us', 'ru', 'uk') { "{0}: {1} chars" -f $key, $notes[$key].Length }
Write-Host "Wrote $Out"
Write-Host ("Languages: {0} column(s); ReleaseNotes filled in every one." -f ($columns.Count - 4))
Write-Host ("Note lengths - {0} (Partner Center limit 1500)" -f ($lengths -join ', '))
