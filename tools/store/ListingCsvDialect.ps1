# SP-0282: WINDOWS-STORE rule 8 and conformance rung 2, held on the output side.
#
# Rule 8: a listing import file is UTF-8 without a BOM, with CRLF as the only line break - inside a quoted cell as much as
# between records - and no trailing newline. Rung 2: the writer refuses an output that holds a BOM or a bare LF. Measured
# (2026-07-27, 2026-08-07): a BOM is rejected, and one bare LF closing the last row leaves the last quoted cell open,
# swallows the next record and makes Partner Center name a language that has nothing wrong.
#
# Assert-ListingCsvDialect judges the finished text before anything is written, and throws with the reason on the first
# refusal. Dot-source this file; build-store-listing-csv.ps1 calls it from Write-ListingCsv and Test-StoreListing.ps1
# calls it directly, because a BOM or an open cell cannot be reached through the builder's own command line.

function Assert-ListingCsvDialect {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        # The writer is asked to emit the export's own BOM (a no-change round trip). The BOM then comes from the encoder,
        # never from the text, so a U+FEFF at the start of the text is refused with or without this switch.
        [switch] $WithBom
    )

    $why = 'WINDOWS-STORE rule 8: UTF-8 without a BOM, CRLF as the only line break, inside a quoted cell as much as between records, no trailing newline'
    if ($Content.Length -gt 0 -and $Content[0] -eq [char] 0xFEFF) {
        $asked = if ($WithBom) { 'the encoder already writes the one the export carried' } else { 'none was asked for' }
        throw "Refusing to write the listing CSV: the text starts with a BOM ($asked). $why."
    }

    $inQuotes = $false
    $record = 1
    for ($index = 0; $index -lt $Content.Length; $index++) {
        $character = $Content[$index]
        if ($character -eq '"') { $inQuotes = -not $inQuotes; continue }
        if ($character -eq "`r") {
            if ($index + 1 -lt $Content.Length -and $Content[$index + 1] -eq "`n") {
                if (-not $inQuotes) { $record++ }
                $index++
                continue
            }
            throw "Refusing to write the listing CSV: a bare carriage return in record $record. $why."
        }
        if ($character -eq "`n") {
            throw "Refusing to write the listing CSV: a bare line feed in record $record. $why."
        }
    }

    if ($inQuotes) {
        throw "Refusing to write the listing CSV: the last quoted cell is left open (record $record). $why."
    }
    if ($Content.EndsWith("`n", [StringComparison]::Ordinal)) {
        throw "Refusing to write the listing CSV: the file ends with a line break. $why."
    }
}
