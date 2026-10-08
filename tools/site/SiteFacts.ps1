<#
    SP-0198 (SITE-REPRESENTATION rules 1 to 3): the facts the site copy states, typed once in their source.

    The declaration is tools/site/site-facts.json. Two facts are rendered into the copy decks as inline
    placeholders, so no deck types them:

      [[languages]]  how many interface languages ship        - the InterfaceLanguages registry (Core)
      [[windows]]    the minimum Windows, as the site names it - SupportedOSPlatformVersion of the App project

    The channels are not a number the page states. The cards and the prose lists name them, and the gate holds those
    to the declared list.

    Get-SiteFactProblem is the gate, all of it offline and decidable from the tree:

      source     the minimum Windows named by the MSIX manifest and the winget manifest agrees with the project's;
      channels   every declared channel has its packaging evidence in the tree, the deck has exactly one card for
                 each, and the prose that lists the install routes names the two that keep their name in every
                 language (Microsoft Store, winget) - a fifth card, or a lost route, is a finding;
      rendered   each key that states a fact carries its placeholder in every deck, so a typed number cannot
                 replace it, and no title or description (read raw by the structured data) carries one;
      pillars    the README, the Store listing and the site's lead sentence in English, Russian and Ukrainian first
                 name the pillars of POSITIONING.md in its order, and the site's key line lists them in all
                 thirteen locales (the ten machine-translated ones by count and by the terms that are not
                 translated).

    Not checked, on purpose: the wording around a pillar, and whether a translated key line means what the
    English one does.

    Dot-source it from the site generator:
        . "$PSScriptRoot/SiteFacts.ps1"
        $facts = Get-SiteFactValue -Root $root -LanguageCount $languages.Count
        $problems = Get-SiteFactProblem -Root $root -Deck $deckValues
#>

function Get-SiteFactProp {
    param($Object, [Parameter(Mandatory)] [string] $Name)

    # StrictMode is on in the caller, so an absent property is read through PSObject, not by name.
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { return $Object.PSObject.Properties[$Name].Value }
    return $null
}

function Read-SiteFactConfig {
    param([Parameter(Mandatory)] [string] $Root)

    $path = Join-Path $Root 'tools/site/site-facts.json'
    if (-not (Test-Path -LiteralPath $path)) { throw 'tools/site/site-facts.json does not exist - it declares the facts the site copy is checked against.' }
    try { return [System.IO.File]::ReadAllText($path) | ConvertFrom-Json }
    catch { throw "tools/site/site-facts.json is not valid JSON: $($_.Exception.Message)" }
}

function Get-SiteFactSourceValue {
    # The first capture group of the rule's pattern, read from the rule's file. Throws a sentence a person can act on.
    param([Parameter(Mandatory)] [string] $Root, [Parameter(Mandatory)] $Rule)

    $relative = [string] $Rule.path
    $path = Join-Path $Root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$relative does not exist." }
    $match = [regex]::Match([System.IO.File]::ReadAllText($path), [string] $Rule.pattern)
    if (-not $match.Success) { throw "$relative has no match for /$($Rule.pattern)/." }
    return $match.Groups[1].Value.Trim()
}

function Get-MinimumWindowsLabel {
    param([Parameter(Mandatory)] [string] $Version)

    $parsed = $null
    if (-not [version]::TryParse($Version, [ref] $parsed) -or $parsed.Major -ne 10) {
        throw "The minimum Windows '$Version' is not a Windows 10 or later build number (10.0.<build>)."
    }
    # Windows 10 and Windows 11 both report 10.0; Windows 11 begins at build 22000. A minimum below it runs on both.
    if ($parsed.Build -ge 22000) { return 'Windows 11' }
    return 'Windows 10 / 11'
}

function Get-SiteFactValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Root,
        # How many languages the InterfaceLanguages registry ships.
        [Parameter(Mandatory)] [int] $LanguageCount
    )

    $config = Read-SiteFactConfig -Root $Root
    $minimum = Get-SiteFactSourceValue -Root $Root -Rule $config.minimumWindows.source
    return [pscustomobject]@{
        Languages = $LanguageCount
        Windows   = Get-MinimumWindowsLabel -Version $minimum
    }
}

function Read-PositioningPillar {
    # The pillar table of the positioning source: one object per row with the English, Russian and Ukrainian terms.
    param([Parameter(Mandatory)] [string] $Root, [Parameter(Mandatory)] [string] $Source)

    $path = Join-Path $Root $Source
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$Source does not exist - it is the positioning source." }
    $section = [regex]::Match([System.IO.File]::ReadAllText($path).Replace("`r`n", "`n"), '(?ms)^## 2\..*?(?=^## |\z)')
    if (-not $section.Success) { throw "$Source has no '## 2.' pillar section." }

    $rows = @($section.Value.Split("`n") | Where-Object { $_.TrimStart().StartsWith('|') } |
        ForEach-Object { , @($_.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() }) })
    if ($rows.Count -lt 3) { throw "$Source '## 2.' holds no pillar table (a header, a rule and at least one row)." }
    $header = @($rows[0] | ForEach-Object { $_.ToLowerInvariant() })
    $column = @{}
    foreach ($name in '#', 'pillar', 'en', 'ru', 'uk') {
        $index = [array]::IndexOf($header, $name)
        if ($index -lt 0) { throw "$Source pillar table has no '$name' column." }
        $column[$name] = $index
    }

    $pillars = foreach ($row in $rows | Select-Object -Skip 2) {
        [pscustomobject]@{
            Number = [string] $row[$column['#']]
            Name   = [string] $row[$column['pillar']]
            Term   = @{ en = [string] $row[$column['en']]; ru = [string] $row[$column['ru']]; uk = [string] $row[$column['uk']] }
        }
    }
    $pillars = @($pillars)
    for ($i = 0; $i -lt $pillars.Count; $i++) {
        if ($pillars[$i].Number -ne [string] ($i + 1)) { throw "$Source pillar $($i + 1) is numbered '$($pillars[$i].Number)' - the rows are 1, 2, 3 in order." }
        foreach ($locale in 'en', 'ru', 'uk') {
            if ([string]::IsNullOrWhiteSpace($pillars[$i].Term[$locale])) { throw "$Source pillar $($i + 1) has no '$locale' term." }
        }
    }
    return $pillars
}

function ConvertTo-PositioningPlain {
    param([AllowEmptyString()] [string] $Text)

    $plain = [regex]::Replace($Text, '<[^>]+>', ' ').Replace('&amp;', '&').Replace('&nbsp;', ' ')
    return ([regex]::Replace($plain, '\s+', ' ')).ToLowerInvariant()
}

function Test-PillarOrder {
    # $null when every pillar is first named after the one before it, otherwise a sentence naming the first that is
    # not. The first mention is what is judged: a later one in a long README would hide a swapped lead line.
    param([AllowEmptyString()] [string] $Text, [Parameter(Mandatory)] [object[]] $Pillar, [Parameter(Mandatory)] [string] $Locale)

    $plain = ConvertTo-PositioningPlain $Text
    $at = -1
    for ($i = 0; $i -lt $Pillar.Count; $i++) {
        $term = $Pillar[$i].Term[$Locale].ToLowerInvariant()
        $first = $plain.IndexOf($term, [System.StringComparison]::Ordinal)
        if ($first -lt 0) { return "pillar $($i + 1) '$($Pillar[$i].Term[$Locale])' is missing" }
        if ($first -le $at) { return "pillar $($i + 1) '$($Pillar[$i].Term[$Locale])' is out of order" }
        $at = $first
    }
    return $null
}

function Get-SiteFactProblem {
    [CmdletBinding()]
    param(
        # Repository root.
        [Parameter(Mandatory)] [string] $Root,
        # Every copy deck, by dictionary code: a dictionary of key -> value, as build-site.ps1 reads them.
        [Parameter(Mandatory)] $Deck
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    try { $config = Read-SiteFactConfig -Root $Root } catch { return @($_.Exception.Message) }
    $english = $Deck['en']

    # ----- source: the three declarations of the minimum Windows say one thing --------------------------
    try {
        $minimum = Get-SiteFactSourceValue -Root $Root -Rule $config.minimumWindows.source
        [void] (Get-MinimumWindowsLabel -Version $minimum)
        foreach ($other in @($config.minimumWindows.agree)) {
            try {
                $value = Get-SiteFactSourceValue -Root $Root -Rule $other
                if ($value -ne $minimum) {
                    $problems.Add("minimum Windows: $($other.path) says $value, $($config.minimumWindows.source.path) says $minimum - they are one fact.")
                }
            } catch { $problems.Add("minimum Windows: $($_.Exception.Message)") }
        }
    } catch { $problems.Add("minimum Windows: $($_.Exception.Message)") }

    # ----- channels: declared, evidenced, and named by the deck exactly --------------------------------
    $channelKeys = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($channel in @($config.channels)) {
        if (-not $channelKeys.Add([string] $channel.deckKey)) { $problems.Add("channel '$($channel.id)': deck key $($channel.deckKey) is declared twice.") }
        if (-not $english.Contains([string] $channel.deckKey)) { $problems.Add("channel '$($channel.id)': the English deck has no $($channel.deckKey) key.") }
        foreach ($evidence in @($channel.evidence)) {
            $file = Join-Path $Root ([string] $evidence.path)
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $problems.Add("channel '$($channel.id)': $($evidence.path) does not exist - the channel has no packaging in the tree."); continue }
            $needle = Get-SiteFactProp $evidence 'contains'
            if ($needle -and -not [System.IO.File]::ReadAllText($file).Contains([string] $needle)) {
                $problems.Add("channel '$($channel.id)': $($evidence.path) no longer mentions '$needle'.")
            }
        }
    }
    foreach ($key in @($english.Keys | Where-Object { $_ -match '^channel-[a-z0-9]+$' })) {
        if (-not $channelKeys.Contains($key)) { $problems.Add("the English deck names a channel in $key that site-facts.json does not declare.") }
    }

    foreach ($code in $Deck.Keys) {
        foreach ($key in @($config.channelProse)) {
            if (-not $Deck[$code].Contains($key)) { $problems.Add("[$code] $key is missing."); continue }
            foreach ($channel in @($config.channels | Where-Object { Get-SiteFactProp $_ 'name' })) {
                # A route may be named by its command instead of its word ("install with [[command]]").
                $marker = [string] (Get-SiteFactProp $channel 'orMarker')
                $named = $Deck[$code][$key].IndexOf([string] $channel.name, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    ($marker -and $Deck[$code][$key].Contains($marker))
                if (-not $named) { $problems.Add("[$code] $key does not name the $($channel.id) channel ($($channel.name)).") }
            }
        }
    }

    # ----- rendered: a fact is a placeholder, never a number ---------------------------------------------
    foreach ($fact in 'languages', 'windows') {
        foreach ($key in @($config.renderedKeys.$fact)) {
            $typed = [System.Collections.Generic.List[string]]::new()
            foreach ($code in $Deck.Keys) {
                if (-not $Deck[$code].Contains($key)) { $problems.Add("[$code] $key is missing."); continue }
                if (-not $Deck[$code][$key].Contains("[[$fact]]")) { $typed.Add($code) }
            }
            if ($typed.Count) { $problems.Add("[$($typed -join ' ')] $key does not carry [[$fact]] - a fact is rendered from its source, not typed.") }
        }
    }
    foreach ($code in $Deck.Keys) {
        foreach ($key in @($Deck[$code].Keys | Where-Object { $_ -like 'title-*' -or $_ -like 'description-*' })) {
            if ($Deck[$code][$key] -match '\[\[(languages|windows)\]\]') {
                $problems.Add("[$code] $key carries a fact placeholder, but the structured data reads it raw - state the fact in the page, not the title.")
            }
        }
    }

    # ----- pillars: the positioning source's order, on every surface ----------------------------------------
    $positioning = $config.positioning
    try { $pillars = @(Read-PositioningPillar -Root $Root -Source ([string] $positioning.source)) }
    catch { $problems.Add($_.Exception.Message); return $problems.ToArray() }

    foreach ($surface in @($positioning.surfaces)) {
        $file = Join-Path $Root ([string] $surface.path)
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $problems.Add("$($surface.path) does not exist - it is a positioning surface."); continue }
        $reason = Test-PillarOrder -Text ([System.IO.File]::ReadAllText($file)) -Pillar $pillars -Locale ([string] $surface.locale)
        if ($reason) { $problems.Add("$($surface.path): $reason in $($positioning.source) order.") }
    }
    $lead = $positioning.deckLead
    foreach ($locale in @($lead.locales)) {
        if (-not $Deck.Contains($locale) -or -not $Deck[$locale].Contains([string] $lead.key)) { $problems.Add("[$locale] $($lead.key) is missing."); continue }
        $reason = Test-PillarOrder -Text $Deck[$locale][[string] $lead.key] -Pillar $pillars -Locale $locale
        if ($reason) { $problems.Add("[$locale] $($lead.key): $reason in $($positioning.source) order.") }
    }

    $keyLine = $positioning.deckKeyLine
    foreach ($code in $Deck.Keys) {
        if (-not $Deck[$code].Contains([string] $keyLine.key)) { $problems.Add("[$code] $($keyLine.key) is missing."); continue }
        $items = @($Deck[$code][[string] $keyLine.key].Split([string] $keyLine.separator) | ForEach-Object { $_.Trim() })
        if ($items.Count -ne $pillars.Count) { $problems.Add("[$code] $($keyLine.key) lists $($items.Count) items, $($positioning.source) has $($pillars.Count) pillars."); continue }
        for ($i = 0; $i -lt $pillars.Count; $i++) {
            $terms = @('en', 'ru', 'uk' | ForEach-Object { $pillars[$i].Term[$_] })
            $untranslated = @($terms | Select-Object -Unique).Count -eq 1
            $expected = if ($untranslated) { $terms[0] } elseif ($pillars[$i].Term.ContainsKey($code)) { $pillars[$i].Term[$code] } else { $null }
            if ($expected -and $items[$i] -ne $expected) {
                $problems.Add("[$code] $($keyLine.key) item $($i + 1) is '$($items[$i])', $($positioning.source) says '$expected'.")
            }
        }
    }

    return $problems.ToArray()
}
