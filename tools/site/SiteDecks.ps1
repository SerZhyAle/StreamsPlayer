<#
    SP-0039 (plan phase 2): the copy-deck reader and the deck parity check, split out of build-site.ps1.

    Dot-sourced by the site generator, after it has set up `$inlineMarkup` (the inline placeholders a deck may
    carry). Get-Placeholder reads that table from the caller's scope at call time, so a placeholder added to
    the table is seen by the parity check without an argument; nothing here holds state of its own.

    Dot-source it:
        . "$PSScriptRoot/SiteDecks.ps1"
        $deck = Read-CopyDeck -Path $path
        $problems = @(Get-DeckParityProblem -English $decks['en'] -Decks $decks -GlyphMarker $glyphMarker)
#>

function Read-CopyDeck {
    param([Parameter(Mandatory)] [string] $Path)

    $text = [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $keys = [System.Collections.Generic.List[string]]::new()
    $values = [ordered]@{}
    $currentKey = $null
    $buffer = [System.Collections.Generic.List[string]]::new()

    $flush = {
        if ($currentKey) {
            $value = ($buffer -join "`n").Trim()
            $values[$currentKey] = $value
        }
    }

    foreach ($line in $text.Split("`n")) {
        if ($line.StartsWith('@@')) {
            & $flush
            $currentKey = $line.Substring(2).Trim()
            if ($values.Contains($currentKey)) {
                throw "$([System.IO.Path]::GetFileName($Path)): duplicate key '$currentKey'."
            }
            $keys.Add($currentKey)
            $buffer = [System.Collections.Generic.List[string]]::new()
            continue
        }
        if ($null -eq $currentKey) { continue }   # header comments before the first @@ key
        $buffer.Add($line)
    }
    & $flush

    return [pscustomobject]@{
        Path   = $Path
        Keys   = $keys
        Values = $values
    }
}

function Get-Placeholder {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

    return @(($inlineMarkup.Keys | Where-Object { $Value.Contains($_) }) | Sort-Object)
}

function Get-DeckParityProblem {
    # Every non-English deck against English: the same keys, none empty, the same glyphs in the same order
    # (ICON-SET rule 8, SP-0113) and the same inline placeholders. Returns the problem sentences; the caller
    # decides whether they are fatal.
    param(
        [Parameter(Mandatory)] $English,
        [Parameter(Mandatory)] $Decks,
        [Parameter(Mandatory)] [regex] $GlyphMarker
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($code in $Decks.Keys) {
        if ($code -eq 'en') { continue }
        $deck = $Decks[$code]

        $missing = @($English.Keys | Where-Object { -not $deck.Values.Contains($_) })
        $extra = @($deck.Keys | Where-Object { -not $English.Values.Contains($_) })
        if ($missing.Count) { $problems.Add("[$code] missing key(s): $($missing -join ', ')") }
        if ($extra.Count) { $problems.Add("[$code] key(s) English does not have: $($extra -join ', ')") }

        foreach ($key in $English.Keys) {
            if (-not $deck.Values.Contains($key)) { continue }
            if ([string]::IsNullOrWhiteSpace($deck.Values[$key])) {
                $problems.Add("[$code] $key is empty")
                continue
            }
            # A glyph beside a control's name is part of what the sentence says, so every language carries
            # the same glyphs, in the same order, as English.
            $expectedGlyphs = @($GlyphMarker.Matches($English.Values[$key]) | ForEach-Object { $_.Groups['id'].Value }) -join ','
            $actualGlyphs = @($GlyphMarker.Matches($deck.Values[$key]) | ForEach-Object { $_.Groups['id'].Value }) -join ','
            if ($expectedGlyphs -ne $actualGlyphs) {
                $problems.Add("[$code] ${key}: expected glyph(s) '$expectedGlyphs', found '$actualGlyphs'")
            }
            $expected = Get-Placeholder -Value $English.Values[$key]
            $actual = Get-Placeholder -Value $deck.Values[$key]
            if (($expected -join ',') -ne ($actual -join ',')) {
                $problems.Add("[$code] ${key}: expected placeholder(s) '$($expected -join ',')', found '$($actual -join ',')'")
            }
        }
    }

    return $problems.ToArray()
}
