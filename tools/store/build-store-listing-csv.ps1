<#
    SP-0034: fills a Partner Center listing export with the thirteen-language copy deck.

    The export is the column contract. Partner Center accepts an import only when Field, ID and Type
    match the file it generated for this submission, the ID values are account-specific, and the
    language columns are exactly the ones already added to the submission. So this script never
    invents a column and never reorders a row: it reads the export, writes into empty cells, and
    writes the whole thing back.

    Recorded Partner Center behaviour this script is built around:
      - Re-export before every import. The export carries the current submission's asset URLs and
        defines which columns an import will accept.
      - A language must be added to the submission by hand first (Manage additional languages). A
        column that is not there means that language's copy is dropped silently, so a shipped
        language with no column fails the run instead of being skipped.
      - The import is all-or-nothing: one bad cell rejects the whole file.
      - The file must be UTF-8 *without* BOM. A BOM is rejected.
      - A listing needs both a description and at least one screenshot to leave Incomplete; a
        text-only language sits at Incomplete with no error shown anywhere.
      - A relative image path works only through Upload folder, never in a flat CSV upload, so the
        flat output carries no image path at all.
      - Never copy OverrideLogosForWin10 = True into a language that has no StoreLogo rows of its
        own: it holds the listing Incomplete with nothing shown on the page.

    Usage:
      # 1. Partner Center -> Store listings -> Export listing, save the CSV.
      pwsh -NoProfile -File tools/store/build-store-listing-csv.ps1 -Export tmp/exported-listing.csv

      # 2. With screenshots, as an Upload folder payload:
      pwsh -NoProfile -File tools/store/build-store-listing-csv.ps1 -Export tmp/exported-listing.csv `
        -ImportFolder msix/dist/store-listing-import

      # Round-trip proof against the committed fixture (criterion 11):
      pwsh -NoProfile -File tools/store/build-store-listing-csv.ps1 -FillNothing
#>
[CmdletBinding()]
param(
    # The Partner Center export. Defaults to the committed fixture so the round trip is checkable
    # without a live session; a real run must pass a freshly re-taken export.
    [string] $Export,
    [string] $Out,
    [string] $DeckDirectory,
    [string] $SearchTermsFile,
    # Write the export back with nothing filled in. The output must be byte-identical to the input.
    [switch] $FillNothing,
    # Replace copy that is already in the export instead of leaving it. Needed whenever a claim
    # changes: a listing that already says "English and Russian interface" will never be corrected by
    # filling empty cells only. Safe by construction - the decks name nothing but prose fields, so an
    # asset URL or a logo row can never be reached. Every replacement is printed.
    [switch] $ReplaceCopy,
    # Stage the CSV beside the screenshots for Partner Center's Upload folder.
    [string] $ImportFolder,
    [string] $ScreenshotDirectory,
    # Continue when a shipped language has no column in the export. Off by default: a missing column
    # is how a language's copy gets dropped without a word of warning.
    [switch] $AllowMissingLanguages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/../InterfaceLanguages.ps1"

$root = Get-RepositoryRoot
if (-not $Export) { $Export = Join-Path $root 'msix/store-listing-export.sample.csv' }
if (-not $Out) { $Out = Join-Path $root 'msix/dist/store-listing-import.csv' }
if (-not $DeckDirectory) { $DeckDirectory = Join-Path $root 'msix/listing' }
if (-not $SearchTermsFile) { $SearchTermsFile = Join-Path $DeckDirectory 'search-terms.txt' }
if (-not $ScreenshotDirectory) { $ScreenshotDirectory = Join-Path $root 'assets/store' }

$failures = [System.Collections.Generic.List[string]]::new()

# ------------------------------------------------------------------------------------ CSV, exactly

function Read-ListingCsv {
    param([Parameter(Mandatory)] [string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $preamble = [System.Text.Encoding]::UTF8.GetPreamble()
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq $preamble[0] -and $bytes[1] -eq $preamble[1] -and $bytes[2] -eq $preamble[2]
    $offset = if ($hasBom) { 3 } else { 0 }
    $text = [System.Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)

    # A deliberately small RFC-4180 reader. Import-Csv would do the parsing but not the writing:
    # Export-Csv re-decides quoting and appends a trailing newline, which is exactly why the
    # previous merge script could never produce a byte-identical round trip.
    $rows = [System.Collections.Generic.List[string[]]]::new()
    $fields = [System.Collections.Generic.List[string]]::new()
    $field = [System.Text.StringBuilder]::new()
    $quoted = $false
    $index = 0

    while ($index -lt $text.Length) {
        $character = $text[$index]
        if ($quoted) {
            if ($character -eq '"') {
                if ($index + 1 -lt $text.Length -and $text[$index + 1] -eq '"') {
                    [void] $field.Append('"')
                    $index += 2
                    continue
                }
                $quoted = $false
                $index += 1
                continue
            }
            [void] $field.Append($character)
            $index += 1
            continue
        }
        # if/elseif rather than switch: 'continue' inside a PowerShell switch does not reliably mean
        # "next iteration of the enclosing while", and a parser is the wrong place to find out.
        if ($character -eq '"') {
            $quoted = $true
            $index += 1
        } elseif ($character -eq ',') {
            $fields.Add($field.ToString()); [void] $field.Clear()
            $index += 1
        } elseif ($character -eq "`r" -or $character -eq "`n") {
            if ($character -eq "`r" -and $index + 1 -lt $text.Length -and $text[$index + 1] -eq "`n") { $index += 1 }
            $fields.Add($field.ToString()); [void] $field.Clear()
            $rows.Add($fields.ToArray()); $fields.Clear()
            $index += 1
        } else {
            [void] $field.Append($character)
            $index += 1
        }
    }
    if ($field.Length -or $fields.Count) {
        $fields.Add($field.ToString())
        $rows.Add($fields.ToArray())
    }

    if ($rows.Count -lt 2) { throw "$Path holds no data rows - is it really a Partner Center export?" }

    $header = $rows[0]
    $data = [System.Collections.Generic.List[string[]]]::new()
    for ($i = 1; $i -lt $rows.Count; $i += 1) {
        # Rows are never padded or trimmed: a differing width means the file is not what this script
        # thinks it is, and silently reshaping it would break the byte-identical round trip.
        if ($rows[$i].Length -ne $header.Length) {
            throw "$Path row $($i + 1) has $($rows[$i].Length) field(s), the header has $($header.Length). Re-export from Partner Center."
        }
        $data.Add($rows[$i])
    }

    return [pscustomobject]@{
        Path    = $Path
        HasBom  = $hasBom
        Header  = $header
        Rows    = $data
    }
}

function Write-ListingCsv {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string[]] $Header,
        [Parameter(Mandatory)] [System.Collections.IEnumerable] $Rows
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $quote = { param($value) '"' + ([string] $value).Replace('"', '""') + '"' }
    $lines.Add((($Header | ForEach-Object { & $quote $_ }) -join ','))
    foreach ($row in $Rows) {
        $lines.Add((($row | ForEach-Object { & $quote $_ }) -join ','))
    }

    # Every field quoted, CRLF between records, no trailing newline, no BOM. All four matter:
    # Partner Center rejects a BOM and mis-reads an unquoted field that contains a comma.
    $content = $lines -join "`r`n"
    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $content, (New-Object System.Text.UTF8Encoding($false)))
}

# ------------------------------------------------------------------------------------- the deck

function Read-Deck {
    param([Parameter(Mandatory)] [string] $Path)

    $text = [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $values = [ordered]@{}
    $currentKey = $null
    $buffer = [System.Collections.Generic.List[string]]::new()
    $flush = {
        if ($currentKey) { $values[$currentKey] = ($buffer -join "`n").Trim() }
    }
    foreach ($line in $text.Split("`n")) {
        if ($line.StartsWith('@@')) {
            & $flush
            $currentKey = $line.Substring(2).Trim()
            if ($values.Contains($currentKey)) { throw "${Path}: duplicate field '$currentKey'." }
            $buffer = [System.Collections.Generic.List[string]]::new()
            continue
        }
        if ($null -eq $currentKey) { continue }
        $buffer.Add($line)
    }
    & $flush
    return $values
}

function Read-TermList {
    param([Parameter(Mandatory)] [string] $Path)

    $terms = [System.Collections.Generic.List[pscustomobject]]::new()
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        $hash = $trimmed.IndexOf('#')
        $term = if ($hash -ge 0) { $trimmed.Substring(0, $hash).Trim() } else { $trimmed }
        $reason = if ($hash -ge 0) { $trimmed.Substring($hash + 1).Trim() } else { '' }
        if ($term) { $terms.Add([pscustomobject]@{ Term = $term; Reason = $reason }) }
    }
    return $terms
}

# ------------------------------------------------------------------------------- read the export

# $Export is a [string] parameter, so the parsed result needs a name of its own - assigning an
# object back into it would silently stringify the whole thing.
$exportCsv = Read-ListingCsv -Path $Export
$header = $exportCsv.Header
$rows = $exportCsv.Rows

if ($header[0] -ne 'Field') {
    throw "$Export does not start with a Field column (found '$($header[0])'). Export the listing from Partner Center and pass that file."
}
$defaultIndex = [array]::IndexOf($header, 'default')
if ($defaultIndex -lt 0) {
    throw "$Export has no 'default' column, so the language columns cannot be located. Re-export from Partner Center."
}
$languageColumns = @($header | Select-Object -Skip ($defaultIndex + 1))
Write-Host ("Export: {0} rows, {1} fixed columns, {2} language column(s): {3}" -f
    $rows.Count, ($defaultIndex + 1), $languageColumns.Count, ($languageColumns -join ', '))
if ($exportCsv.HasBom) {
    Write-Host "  The export carries a BOM. The import must not, so the output is written without one." -ForegroundColor Yellow
}

if ($FillNothing) {
    $target = if ($PSBoundParameters.ContainsKey('Out')) { $Out } else { Join-Path $root 'msix/dist/store-listing-roundtrip.csv' }
    Write-ListingCsv -Path $target -Header $header -Rows $rows
    $before = [System.IO.File]::ReadAllBytes($Export)
    $after = [System.IO.File]::ReadAllBytes($target)
    $identical = [System.Linq.Enumerable]::SequenceEqual([byte[]] $before, [byte[]] $after)
    if ($identical) {
        Write-Host ("Round trip is byte-identical: {0} bytes, {1} rows, nothing filled." -f $after.Length, $rows.Count) -ForegroundColor Green
        exit 0
    }
    Write-Host ("Round trip differs: input {0} bytes, output {1} bytes ({2})." -f $before.Length, $after.Length, $target) -ForegroundColor Red
    exit 1
}

# ----------------------------------------------------------- match the columns to shipped languages

$languages = Get-InterfaceLanguages
$byListingCode = @{}
foreach ($language in $languages) { $byListingCode[$language.ListingCode.ToLowerInvariant()] = $language }

$targets = [System.Collections.Generic.List[pscustomobject]]::new()
foreach ($column in $languageColumns) {
    $key = $column.ToLowerInvariant()
    if (-not $byListingCode.ContainsKey($key)) {
        Write-Host "  Column '$column' is not a shipped language - left exactly as exported." -ForegroundColor Yellow
        continue
    }
    $language = $byListingCode[$key]
    $deckPath = Join-Path $DeckDirectory "$($language.ListingCode).txt"
    if (-not (Test-Path -LiteralPath $deckPath)) {
        $failures.Add("${column}: no copy deck at msix/listing/$($language.ListingCode).txt, so this column would be left empty.")
        continue
    }
    $targets.Add([pscustomobject]@{
        Column   = $column
        Index    = [array]::IndexOf($header, $column)
        Language = $language
        Deck     = Read-Deck -Path $deckPath
    })
}

$missing = @($languages | Where-Object { $languageColumns -notcontains $_.ListingCode })
if ($missing.Count) {
    $message = "The export has no column for: $(($missing | ForEach-Object { $_.ListingCode }) -join ', '). " +
               'Add each language in Partner Center (Store listings -> Manage additional languages), re-export, and run again - ' +
               'an import cannot create a column, and a language with no column has its copy dropped without an error.'
    if ($AllowMissingLanguages) { Write-Host "  $message" -ForegroundColor Yellow } else { $failures.Add($message) }
}

# ------------------------------------------------------------------------------ shared rows and terms

$sharedPath = Join-Path $DeckDirectory 'shared.txt'
$shared = if (Test-Path -LiteralPath $sharedPath) { Read-Deck -Path $sharedPath } else { [ordered]@{} }

$searchTerms = @((Read-TermList -Path $SearchTermsFile).Term)
$forbidden = Read-TermList -Path (Join-Path $DeckDirectory 'forbidden-terms.txt')

if ($searchTerms.Count -gt 7) {
    $failures.Add("search-terms.txt holds $($searchTerms.Count) terms; Partner Center accepts at most 7. Extra: $((@($searchTerms) | Select-Object -Skip 7) -join ', ')")
}
$duplicates = @($searchTerms | Group-Object { $_.ToLowerInvariant() } | Where-Object Count -gt 1 | ForEach-Object { $_.Name })
if ($duplicates.Count) {
    $failures.Add("search-terms.txt repeats: $($duplicates -join ', ')")
}
foreach ($term in $searchTerms) {
    foreach ($entry in $forbidden) {
        if ($term.ToLowerInvariant().Contains($entry.Term.ToLowerInvariant())) {
            $failures.Add("search term '$term' contains the forbidden term '$($entry.Term)' - $($entry.Reason)")
        }
    }
}

if ($failures.Count) {
    Write-Host ""
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ""
    throw "The listing cannot be built ($($failures.Count) problem(s))."
}

# ------------------------------------------------------------------------------------------- fill

$fieldIndex = @{}
for ($i = 0; $i -lt $rows.Count; $i += 1) {
    $field = $rows[$i][0]
    if ($field) { $fieldIndex[$field] = $i }
}

foreach ($required in 'Description', 'ShortDescription') {
    if (-not $fieldIndex.ContainsKey($required)) {
        throw "$Export has no '$required' row. That is not a Partner Center listing export - re-export and pass that file."
    }
}

$filled = 0
$skipped = [System.Collections.Generic.List[string]]::new()
$replaced = [System.Collections.Generic.List[string]]::new()

function Set-ListingCell {
    param(
        [Parameter(Mandatory)] [int] $Row,
        [Parameter(Mandatory)] [int] $Column,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Value,
        # This cell holds prose the deck owns, so -ReplaceCopy may overwrite it.
        [switch] $Copy,
        [switch] $Force
    )

    $current = $script:rows[$Row][$Column]
    if ($current -eq $Value) { return $false }
    if ($current -and $Copy -and $script:ReplaceCopy) {
        $script:replaced.Add(("{0} / {1}: replaced {2} character(s) of existing copy" -f
            $script:rows[$Row][0], $script:header[$Column], $current.Length))
        $script:rows[$Row][$Column] = $Value
        return $true
    }
    if ($current -and -not $Force) {
        # Never overwrite: a non-empty cell may hold an asset URL tied to this submission or text
        # someone typed in Partner Center on purpose.
        # The -f expression needs its own parentheses: inside a method call, PowerShell would read the
        # comma as an argument separator and pass the format string one operand short.
        $script:skipped.Add(("{0} / {1}: already has content, left alone" -f $script:rows[$Row][0], $script:header[$Column]))
        return $false
    }
    $script:rows[$Row][$Column] = $Value
    return $true
}

foreach ($target in $targets) {
    $values = [ordered]@{}
    foreach ($key in $target.Deck.Keys) { $values[$key] = $target.Deck[$key] }
    foreach ($key in $shared.Keys) { $values[$key] = $shared[$key] }
    for ($n = 1; $n -le $searchTerms.Count; $n += 1) { $values["SearchTerm$n"] = $searchTerms[$n - 1] }

    foreach ($key in $values.Keys) {
        if (-not $fieldIndex.ContainsKey($key)) {
            $skipped.Add("${key}: the export has no such row, so it cannot be imported")
            continue
        }
        # Partner Center keeps CRLF inside a quoted field; the decks are LF files.
        $value = ([string] $values[$key]).Replace("`r`n", "`n").Replace("`n", "`r`n")
        if (Set-ListingCell -Row $fieldIndex[$key] -Column $target.Index -Value $value -Copy) { $filled += 1 }
    }

    # OverrideLogosForWin10 belongs to a language only if that language uploaded its own logos.
    $ownLogos = @($fieldIndex.Keys | Where-Object { $_ -like 'StoreLogo*' } |
        Where-Object { $rows[$fieldIndex[$_]][$target.Index] })
    if (-not $ownLogos.Count -and $fieldIndex.ContainsKey('OverrideLogosForWin10')) {
        $row = $fieldIndex['OverrideLogosForWin10']
        if ($rows[$row][$target.Index] -ne 'False') {
            $was = $rows[$row][$target.Index]
            [void] (Set-ListingCell -Row $row -Column $target.Index -Value 'False' -Force)
            Write-Host ("  {0}: OverrideLogosForWin10 forced False (was '{1}') - it has no StoreLogo rows of its own." -f $target.Column, $was) -ForegroundColor Yellow
        }
    }
}

# ------------------------------------------------------------------------------------------ output

if ($ImportFolder) {
    if (-not (Test-Path -LiteralPath $ImportFolder)) { New-Item -ItemType Directory -Path $ImportFolder -Force | Out-Null }
    Get-ChildItem -LiteralPath $ImportFolder -File | Remove-Item -Force

    foreach ($target in $targets) {
        $screenshot = Join-Path $ScreenshotDirectory "app-$($target.Language.ListingCode).png"
        if (-not (Test-Path -LiteralPath $screenshot)) {
            $failures.Add("$($target.Column): no screenshot at assets/store/app-$($target.Language.ListingCode).png - run tools/store/capture-store-screenshots.ps1 first.")
            continue
        }
        Copy-Item -LiteralPath $screenshot -Destination (Join-Path $ImportFolder ([System.IO.Path]::GetFileName($screenshot)))
        if ($fieldIndex.ContainsKey('DesktopScreenshot1')) {
            # A relative path is accepted only by Upload folder, which is why this is never written
            # into the flat CSV.
            [void] (Set-ListingCell -Row $fieldIndex['DesktopScreenshot1'] -Column $target.Index -Value ([System.IO.Path]::GetFileName($screenshot)))
        }
    }

    if ($failures.Count) {
        $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "The import folder is incomplete ($($failures.Count) problem(s))."
    }

    $csvPath = Join-Path $ImportFolder 'store-listing.csv'
    Write-ListingCsv -Path $csvPath -Header $header -Rows $rows
    $csvCount = @(Get-ChildItem -LiteralPath $ImportFolder -Filter '*.csv').Count
    if ($csvCount -ne 1) { throw "The import folder must hold exactly one .csv; it holds $csvCount." }
    $pngCount = @(Get-ChildItem -LiteralPath $ImportFolder -Filter '*.png').Count
    Write-Host ("Staged for Upload folder: {0} - 1 CSV and {1} screenshot(s)." -f $ImportFolder, $pngCount) -ForegroundColor Green
} else {
    Write-ListingCsv -Path $Out -Header $header -Rows $rows
    Write-Host ("Wrote {0} - {1} rows, no image path (a flat CSV upload rejects one per cell)." -f $Out, $rows.Count) -ForegroundColor Green
}

# ------------------------------------------------------------------------------ completeness report

Write-Host ""
Write-Host "Listing completeness:" -ForegroundColor Cyan
$screenshotRows = @($fieldIndex.Keys | Where-Object { $_ -like 'DesktopScreenshot*' -and $_ -notlike '*Caption*' })
foreach ($target in $targets) {
    $description = [bool] $rows[$fieldIndex['Description']][$target.Index]
    $short = [bool] $rows[$fieldIndex['ShortDescription']][$target.Index]
    $features = @(1..10 | Where-Object { $fieldIndex.ContainsKey("Feature$_") -and $rows[$fieldIndex["Feature$_"]][$target.Index] }).Count
    $terms = @(1..7 | Where-Object { $fieldIndex.ContainsKey("SearchTerm$_") -and $rows[$fieldIndex["SearchTerm$_"]][$target.Index] }).Count
    $shots = @($screenshotRows | Where-Object { $rows[$fieldIndex[$_]][$target.Index] }).Count
    $complete = $description -and $shots -gt 0
    $state = if ($complete) { 'complete' } else { 'INCOMPLETE' }
    $colour = if ($complete) { 'Green' } else { 'Yellow' }
    Write-Host ("  {0,-8} description {1,-3} short {2,-3} features {3,-3} terms {4,-3} screenshots {5,-3} -> {6}" -f
        $target.Column, $(if ($description) { 'yes' } else { 'NO' }), $(if ($short) { 'yes' } else { 'NO' }),
        $features, $terms, $shots, $state) -ForegroundColor $colour
}
Write-Host ""
Write-Host ("Filled {0} cell(s) across {1} language(s)." -f $filled, $targets.Count)
if (-not $ImportFolder) {
    Write-Host "No screenshot is in this file. A listing stays Incomplete until it has a description and at least one screenshot," -ForegroundColor Yellow
    Write-Host "and Partner Center reports nothing when it does not - upload the images in the UI, or re-run with -ImportFolder." -ForegroundColor Yellow
}
if ($replaced.Count) {
    Write-Host ""
    Write-Host "Replaced (-ReplaceCopy):" -ForegroundColor Cyan
    $replaced | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
}
if ($skipped.Count) {
    Write-Host ""
    Write-Host "Left untouched:" -ForegroundColor Cyan
    $skipped | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
    if (-not $ReplaceCopy -and @($skipped | Where-Object { $_ -like '*already has content*' }).Count) {
        Write-Host "  Copy that already exists is never replaced by default. If one of those cells holds a claim that" -ForegroundColor Yellow
        Write-Host "  has changed, re-run with -ReplaceCopy - the decks name only prose fields, never an asset row." -ForegroundColor Yellow
    }
}
