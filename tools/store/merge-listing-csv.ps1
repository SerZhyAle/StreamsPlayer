<#
    Fills a Partner Center "Export listing" template with the StreamsPlayer EN/RU
    copy, keeping the template's own Field / ID / Type columns intact (Partner Center
    rejects any other ID values). Matches rows by the Field column and writes the
    en-us and ru-ru columns; adds those columns if the template lacks them.

    Usage:
      1. Partner Center → app overview → Store listings → Export listing.
         Save the downloaded .csv (e.g. to tmp/exported-listing.csv).
      2. Run:
         pwsh -NoProfile -File tools/store/merge-listing-csv.ps1 `
           -Template tmp/exported-listing.csv `
           -Out msix/store-listing-import.filled.csv
      3. Import the produced file via Import listings → Upload .csv.

    Content source: msix/store-listing-import.csv (Field -> en-us / ru-ru).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Template,
    [string] $Out = 'msix/store-listing-import.filled.csv',
    [string] $Content = "$PSScriptRoot\..\..\msix\store-listing-import.csv"
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Template)) { throw "Template not found: $Template (Export listing from Partner Center first)." }
if (-not (Test-Path $Content))  { throw "Content file not found: $Content" }

$src = Import-Csv $Content
$map = @{}
foreach ($r in $src) { $map[$r.Field] = @{ 'en-us' = $r.'en-us'; 'ru-ru' = $r.'ru-ru' } }

$tpl  = Import-Csv $Template
$cols = $tpl[0].psobject.Properties.Name

# Detect the template's actual language column names (Partner Center uses 'en-us' and 'ru',
# not 'ru-ru'). Add a column only if the template has none for that language.
$enCol = @('en-us', 'en-US', 'en') | Where-Object { $cols -contains $_ } | Select-Object -First 1
if (-not $enCol) { $enCol = 'en-us'; foreach ($row in $tpl) { $row | Add-Member -NotePropertyName $enCol -NotePropertyValue '' -Force } }
$ruCol = @('ru', 'ru-ru', 'ru-RU') | Where-Object { $cols -contains $_ } | Select-Object -First 1
if (-not $ruCol) { $ruCol = 'ru'; foreach ($row in $tpl) { $row | Add-Member -NotePropertyName $ruCol -NotePropertyValue '' -Force } }
Write-Host ("Target language columns: EN='{0}'  RU='{1}'" -f $enCol, $ruCol)

$matched = 0; $unmatched = @()
foreach ($row in $tpl) {
    if ($row.Field -and $map.ContainsKey($row.Field)) {
        $row.$enCol = $map[$row.Field].'en-us'
        $row.$ruCol = $map[$row.Field].'ru-ru'
        $matched++
    }
}
foreach ($field in $map.Keys) {
    if (-not ($tpl.Field -contains $field)) { $unmatched += $field }
}

# Export-Csv (UTF-8 BOM in PS7) preserves the template's Field/ID/Type columns verbatim.
$tpl | Export-Csv -Path $Out -NoTypeInformation -Encoding utf8BOM
Write-Host ("Wrote {0}  (rows: {1}, filled: {2})" -f $Out, $tpl.Count, $matched) -ForegroundColor Green
if ($unmatched.Count) {
    Write-Warning ("These content fields had no matching row in the template (reconcile the Field name): {0}" -f ($unmatched -join ', '))
    Write-Host "Open the exported template, check the exact Field names Partner Center used, and rename them in msix/store-listing-import.csv to match, then re-run." -ForegroundColor Yellow
}
