<#
    SP-0039 (plan phase 4, step 4.3): the release notes the What's new page lists. They are read from the files the
    Store listing is already written from - msix/listing/release-notes/<version>.<en-us|ru|uk>.txt, one per version
    and authored language - and rendered as the page body, so a release's notes are written once and never retyped
    on the site (SITE-STRUCTURE rule 14; SITE-EXPERIENCE rule 13: no version string is typed in page copy).

    A file is UTF-8, line 1 `Version <v>` (`Версия <v>`, `Версія <v>`), a blank line, then `- ` bullets, one per
    line. The reader is strict on purpose: the folder is hand-edited and a copy-paste between versions or languages
    is its usual mistake, so a stray file, a missing language, a header naming another version, a body line that is
    not a bullet and an impossible version stamp all throw with the file named, and nothing is guessed.

    The release date is not stored anywhere: it is the date part of the version, read the way
    .github/workflows/release.yml validates a tag (ParseExact on yy.MMdd.HHmm, invariant culture). The stamp is the
    author's local release time (AGENTS.md, version convention), so that date is the release day. Versions up to
    26.0806.2131 were stamped in UTC and may read a day early; none of them has a notes file.

    Both functions are pure: they return their result and write nothing, so tools/site/Test-ReleaseNotes.ps1 can run
    them against a fixture tree, and the generator can render once per language and keep the text. Nothing here
    holds state. The renderer reads ConvertTo-HtmlText from the caller's scope at call time, as the generator
    defines it (the same arrangement as SiteNotFound.ps1), so a bullet is escaped by the one function every other
    substitution site uses.

    Dot-source it:
        . "$PSScriptRoot/ReleaseNotes.ps1"
        $notes = @(Get-SiteReleaseNote -Root $root)          # wrap in @(): one version is one object
        $html = ConvertTo-ReleaseNotesHtml -Notes $notes -Code $language.DictionaryCode -Authored $humanAuthored

    The rendered lines carry their own indentation (a section sits eight spaces in, inside the privacy card), so a
    template places {{page.releaseNotes}} alone on a line at column 0, as 404.html does with its blocks.
#>

function Get-ReleaseNoteLanguage {
    # The languages the notes are authored in: the code in the file name, the dictionary code the site and the
    # decks use for the same language, and the word line 1 opens with. The Store listing says `en-us` where the
    # site says `en`; this table is the one place that maps them.
    return @(
        [pscustomobject]@{ File = 'en-us'; Code = 'en'; Word = 'Version' }
        [pscustomobject]@{ File = 'ru';    Code = 'ru'; Word = 'Версия' }
        [pscustomobject]@{ File = 'uk';    Code = 'uk'; Word = 'Версія' }
    )
}

function ConvertTo-ReleaseDate {
    # The release day of a version stamp YY.MMDD.HHmm. ParseExact throws on an impossible stamp (month 13, hour 25).
    # The invariant culture reads a two-digit year against the calendar's pivot (TwoDigitYearMax), so the year after
    # it would become the last century and sort as the oldest release with a century-old date. That is refused here
    # rather than printed, so the first release past the pivot fails the build and makes someone choose, instead of
    # the page silently listing it last and dated a hundred years early.
    param([Parameter(Mandatory)] [string] $Version)

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    try {
        $date = [datetime]::ParseExact($Version, 'yy.MMdd.HHmm', $culture)
    }
    catch [System.FormatException] {
        throw "'$Version' is not a version stamp (YY.MMDD.HHmm): $($_.Exception.Message)"
    }
    if ($date.Year -lt 2000) {
        throw "'$Version' reads as the year $($date.Year): a two-digit year is read against the calendar's pivot (TwoDigitYearMax $($culture.Calendar.TwoDigitYearMax)), so the reader needs a decision before the first release past it."
    }
    return $date
}

function Get-ReleaseDateText {
    # The date as ISO yyyy-MM-dd and nothing else. A culture's long date is not used on purpose: culture data differs
    # between machines and ICU versions, and a generated page that depended on it would make `build-site.ps1 -Check`
    # flap. The invariant culture also keeps the Gregorian calendar and the Latin digits on a machine set to another.
    param([Parameter(Mandatory)] [datetime] $Date)

    return $Date.ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Read-ReleaseNoteFile {
    # One notes file: the header line, a blank line, then the bullets. Returns the bullet texts, trimmed.
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [pscustomobject] $Language
    )

    $name = 'msix/listing/release-notes/' + [System.IO.Path]::GetFileName($Path)
    # A Windows checkout may turn LF into CRLF (.gitattributes keeps *.txt as CRLF), and the page must come out the
    # same either way; a carriage return left over after that is a stray one and is refused below.
    $text = [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    if ($text.Contains("`r")) { throw "${name}: a carriage return that is not part of a line ending." }
    if ($text.EndsWith("`n")) { $text = $text.Substring(0, $text.Length - 1) }
    $lines = $text.Split("`n")

    $header = "$($Language.Word) $Version"
    if ($lines[0] -cne $header) {
        throw "${name}: line 1 must be exactly '$header' (found '$($lines[0])') - a header naming another version is a copy-paste."
    }
    if ($lines.Count -lt 3 -or $lines[1] -ne '') {
        throw "${name}: line 2 must be empty and at least one '- ' bullet must follow."
    }

    $bullets = [System.Collections.Generic.List[string]]::new()
    for ($index = 2; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if (-not $line.StartsWith('- ', [System.StringComparison]::Ordinal)) {
            throw "${name}: line $($index + 1) is not a '- ' bullet (found '$line')."
        }
        $bullet = $line.Substring(2).Trim()
        if ($bullet -eq '') { throw "${name}: line $($index + 1) is an empty bullet." }
        $bullets.Add($bullet)
    }
    return $bullets.ToArray()
}

function Get-SiteReleaseNote {
    # Every release's notes, newest first: { Version; Date; Notes = { en; ru; uk } }, each language an array of bullet
    # texts. The folder holds exactly the versions that reached a channel (open point O1: the notes of a stamp that
    # was never tagged are deleted), so there is no exclusion list - every file is data or a mistake.
    param([Parameter(Mandatory)] [string] $Root)

    $folder = Join-Path $Root 'msix/listing/release-notes'
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        throw "msix/listing/release-notes does not exist under $Root."
    }
    $languages = Get-ReleaseNoteLanguage
    $namePattern = '^(?<v>[0-9]{2}\.[0-9]{4}\.[0-9]{4})\.(?<c>en-us|ru|uk)\.txt$'

    $byVersion = [ordered]@{}
    foreach ($item in Get-ChildItem -LiteralPath $folder) {
        if ($item.PSIsContainer -or -not ($item.Name -cmatch $namePattern)) {
            throw "msix/listing/release-notes/$($item.Name) is not a release-notes file (<version>.<en-us|ru|uk>.txt); a stray file is a mistake, not data."
        }
        $version = $Matches['v']
        if (-not $byVersion.Contains($version)) { $byVersion[$version] = @{} }
        $byVersion[$version][$Matches['c']] = $item.FullName
    }
    if ($byVersion.Count -eq 0) { throw 'msix/listing/release-notes holds no release notes.' }

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($version in $byVersion.Keys) {
        $date = ConvertTo-ReleaseDate -Version $version
        $missing = @($languages | Where-Object { -not $byVersion[$version].ContainsKey($_.File) } | ForEach-Object { $_.File })
        if ($missing.Count) { throw "msix/listing/release-notes: $version has no notes file for: $($missing -join ', ')." }

        $notes = [ordered]@{}
        foreach ($language in $languages) {
            # @() because a function returning one bullet hands back the string, not a one-element array.
            $notes[$language.Code] = @(Read-ReleaseNoteFile -Path $byVersion[$version][$language.File] -Version $version -Language $language)
        }
        $entries.Add([pscustomobject]@{ Version = $version; Date = $date; Notes = $notes })
    }
    return @($entries | Sort-Object -Property Date -Descending)
}

function ConvertTo-ReleaseNotesHtml {
    # The page body: one section per version, newest first, for one site language. A language the notes are authored
    # in (-Authored, the generator's human-authored list) gets its own bullets; every other language gets the English
    # ones inside a list marked lang="en" dir="ltr", so a reader of the German or the Arabic page can tell the
    # language changed and a right-to-left page does not mirror an English sentence. The version heading and the
    # date line are left to right on every page for the same reason: a version stamp and an ISO date are
    # left-to-right text, and a heading forced to ltr above a date that inherited rtl would sit at the left edge
    # of an Arabic or Urdu page with its date at the right.
    param(
        [Parameter(Mandatory)] $Notes,
        # The site's dictionary code of the page being rendered (en, ru, uk, de, ar, ..).
        [Parameter(Mandatory)] [string] $Code,
        [Parameter(Mandatory)] [string[]] $Authored
    )

    $entries = @($Notes)
    if ($entries.Count -eq 0) { throw 'There are no release notes to render.' }

    $own = $Authored -ccontains $Code
    $key = if ($own) { $Code } else { 'en' }
    $listOpen = if ($own) { '<ul class="trust-list">' } else { '<ul class="trust-list" lang="en" dir="ltr">' }

    $sections = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $entries) {
        if (-not $entry.Notes.Contains($key)) { throw "Release $($entry.Version) has no '$key' notes to render." }
        $id = 'v' + $entry.Version.Replace('.', '-')
        $isoDate = Get-ReleaseDateText -Date $entry.Date

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add(('        <section class="privacy-section" id="{0}" aria-labelledby="{0}-title">' -f $id))
        $lines.Add(('          <h2 id="{0}-title" dir="ltr">{1}</h2>' -f $id, $entry.Version))
        $lines.Add(('          <p class="release-date" dir="ltr"><time datetime="{0}">{0}</time></p>' -f $isoDate))
        $lines.Add("          $listOpen")
        foreach ($bullet in $entry.Notes[$key]) {
            $lines.Add(('            <li>{0}</li>' -f (ConvertTo-HtmlText $bullet)))
        }
        $lines.Add('          </ul>')
        $lines.Add('        </section>')
        $sections.Add($lines -join "`n")
    }
    return $sections -join "`n`n"
}
