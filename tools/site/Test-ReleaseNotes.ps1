#requires -Version 7.0
<#
.SYNOPSIS
    SP-0039: fixture tests for the release-notes reader and renderer (ReleaseNotes.ps1) behind the What's new page.

.DESCRIPTION
    Builds throwaway trees with a msix/listing/release-notes folder, then runs the real functions against them:

      passes     a valid set parses and sorts newest first, every version with its three languages; a version
                 stamp becomes its ISO release day (26.1003.0030 -> 2026-10-03), also on a machine whose culture
                 uses another calendar; CRLF files, a BOM and a missing final newline read the same as plain LF;
                 bullets are trimmed; the page for en, ru and uk carries that language's bullets and no lang
                 attribute, the page for any other language the English bullets in a list marked lang="en"
                 dir="ltr"; one section per version with its id, labelled heading, left-to-right date (a time
                 element, also on the right-to-left pages, so heading and date share an edge) and bullets; a
                 bullet is escaped by the generator's own function; the output is the same on every call
      fails      a missing en-us, ru or uk file; a header naming another version, with the wrong word or with a
                 trailing space; a line 2 that is not empty; a body with no bullet, a line that is not a bullet,
                 an empty bullet, a blank line inside or after the bullets; a stray file or folder, or a file
                 name that is not <version>.<en-us|ru|uk>.txt; an impossible stamp (month 13, day 32, hour 25,
                 minute 60, 29 February of a common year) and a two-digit year past the calendar's pivot, which
                 the culture would read as the last century;
                 an empty or missing folder; an authored language without notes; nothing to render
      real tree  the repository's own folder parses, newest first, every version in three languages, and renders

    Exit 0 when every check passes, 1 otherwise. The last line is `release-notes: PASS|FAIL (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/ReleaseNotes.ps1"

# The generator's escaping function, as the renderer finds it in the caller's scope: the same four replacements
# build-site.ps1's ConvertTo-HtmlText makes (its inline-markup table is irrelevant to a bullet).
function ConvertTo-HtmlText {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

    return $Value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
}

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}
# True only when the action throws AND the message says why: a typo in the test would otherwise pass as a refusal.
function Throws([scriptblock] $Action, [string] $Pattern) {
    try { & $Action | Out-Null } catch { return ($_.Exception.Message -match $Pattern) }
    return $false
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("release-notes-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null

$utf8 = New-Object System.Text.UTF8Encoding($false)
$treeCount = 0
function New-Tree {
    # A fresh repository root with an empty release-notes folder; returns the root.
    $script:treeCount++
    $root = Join-Path $fixture "tree$script:treeCount"
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'msix/listing/release-notes') | Out-Null
    return $root
}
function Get-Word([string] $File) { return (Get-ReleaseNoteLanguage | Where-Object { $_.File -eq $File }).Word }
function Add-Note {
    param(
        [string] $Root, [string] $Version, [string] $File,
        # Null means the right header; a string replaces line 1 whole.
        [string] $Header = $null,
        [string[]] $Bullets = @("A change in $Version."),
        [string] $Eol = "`n",
        [switch] $Bom,
        [switch] $NoFinalNewline
    )
    if (-not $Header) { $Header = "$(Get-Word $File) $Version" }
    $text = (@($Header, '') + @($Bullets | ForEach-Object { "- $_" })) -join $Eol
    if (-not $NoFinalNewline) { $text += $Eol }
    $path = Join-Path $Root "msix/listing/release-notes/$Version.$File.txt"
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding([bool] $Bom)))
}
function Add-Raw([string] $Root, [string] $Name, [string] $Content) {
    [System.IO.File]::WriteAllText((Join-Path $Root "msix/listing/release-notes/$Name"), $Content, $utf8)
}
function Add-Version([string] $Root, [string] $Version) {
    foreach ($file in 'en-us', 'ru', 'uk') {
        $bullets = switch ($file) {
            'en-us' { @("English one of $Version.", "English two of $Version.") }
            'ru'    { @("Русский первый $Version.", "Русский второй $Version.") }
            'uk'    { @("Український перший $Version.", "Український другий $Version.") }
        }
        Add-Note -Root $Root -Version $Version -File $file -Bullets $bullets
    }
}
function New-ValidTree {
    $root = New-Tree
    # Created out of order on purpose: the reader sorts, the folder listing does not.
    foreach ($version in '26.0924.1704', '26.1003.0030', '26.0806.2225') { Add-Version $root $version }
    return $root
}
function Read-Notes([string] $Root) { return @(Get-SiteReleaseNote -Root $Root) }
function Has-Read([string] $Root, [string] $Pattern) { return Throws { Get-SiteReleaseNote -Root $Root } $Pattern }
function Count-Of([string] $Text, [string] $Needle) { return ([regex]::Matches($Text, [regex]::Escape($Needle))).Count }

try {
    # --- a valid set
    $tree = New-ValidTree
    $notes = Read-Notes $tree
    Check ($notes.Count -eq 3) "a valid set of three versions yields three entries (got $($notes.Count))"
    Check (($notes.Version -join ' ') -eq '26.1003.0030 26.0924.1704 26.0806.2225') "versions come back newest first (got $($notes.Version -join ' '))"
    Check (@($notes | Where-Object { $_.Notes.Keys.Count -eq 3 -and $_.Notes.Contains('en') -and $_.Notes.Contains('ru') -and $_.Notes.Contains('uk') }).Count -eq 3) 'every version carries en, ru and uk'
    Check ($notes[0].Notes['en'].Count -eq 2 -and $notes[0].Notes['en'][0] -eq 'English one of 26.1003.0030.') 'the English bullets are the en-us file, in order'
    Check ($notes[0].Notes['ru'][1] -eq 'Русский второй 26.1003.0030.' -and $notes[0].Notes['uk'][0] -eq 'Український перший 26.1003.0030.') 'the Russian and Ukrainian bullets are their own files'
    Check ((Get-ReleaseDateText -Date $notes[0].Date) -eq '2026-10-03') 'the newest entry is dated by its version: 26.1003.0030 -> 2026-10-03'
    Check ((Get-ReleaseDateText -Date $notes[2].Date) -eq '2026-08-06') 'the oldest entry is dated by its version: 26.0806.2225 -> 2026-08-06'
    Check (@(Get-SiteReleaseNote -Root $tree | ForEach-Object { $_.Version }).Count -eq 3) 'the pipeline output is the same three entries'

    # --- dates
    Check ((Get-ReleaseDateText -Date (ConvertTo-ReleaseDate '26.1003.0030')) -eq '2026-10-03') '26.1003.0030 is the 3rd of October 2026'
    Check ((Get-ReleaseDateText -Date (ConvertTo-ReleaseDate '26.1231.2359')) -eq '2026-12-31') 'the last minute of the year keeps its day'
    Check ((Get-ReleaseDateText -Date (ConvertTo-ReleaseDate '26.0101.0000')) -eq '2026-01-01') 'the first minute of the year keeps its day'
    Check ((Get-ReleaseDateText -Date (ConvertTo-ReleaseDate '28.0229.1200')) -eq '2028-02-29') '29 February of a leap year is a date'
    foreach ($bad in @('26.1332.0000', '26.1032.0000', '26.1003.2500', '26.1003.0060', '26.0229.1200', 'not.a.stamp')) {
        Check (Throws { ConvertTo-ReleaseDate -Version $bad } 'is not a version stamp') "an impossible stamp '$bad' throws"
    }
    # The first year past the calendar's pivot, read from the calendar: the culture would give it to the last century.
    $pivot = [System.Globalization.CultureInfo]::InvariantCulture.Calendar.TwoDigitYearMax
    $pastPivot = '{0:00}.0101.0000' -f (($pivot + 1) % 100)
    Check ((ConvertTo-ReleaseDate -Version ('{0:00}.1231.2359' -f ($pivot % 100))).Year -eq $pivot) 'the last year the pivot reads as this century is accepted'
    Check (Throws { ConvertTo-ReleaseDate -Version $pastPivot } 'calendar''s pivot') "a two-digit year past the calendar pivot ($pastPivot) throws instead of reading as the last century"

    $savedCulture = [System.Globalization.CultureInfo]::CurrentCulture
    $tested = @()
    try {
        foreach ($name in 'th-TH', 'ar-SA', 'fa-IR') {
            $culture = $null
            try { $culture = [System.Globalization.CultureInfo]::GetCultureInfo($name) } catch [System.Globalization.CultureNotFoundException] { $culture = $null }
            if ($null -eq $culture) { continue }
            [System.Globalization.CultureInfo]::CurrentCulture = $culture
            $tested += $name
            $underCulture = Read-Notes $tree
            Check ((Get-ReleaseDateText -Date $underCulture[0].Date) -eq '2026-10-03') "the date prints as ISO under the $name culture"
            Check ((ConvertTo-ReleaseNotesHtml -Notes $underCulture -Code 'en' -Authored @('en', 'ru', 'uk')).Contains('<time datetime="2026-10-03">2026-10-03</time>')) "the page carries the ISO date under the $name culture"
        }
    }
    finally { [System.Globalization.CultureInfo]::CurrentCulture = $savedCulture }
    Write-Output "culture-independence checked under: $(if ($tested) { $tested -join ', ' } else { 'none available (invariant globalization)' })"

    # --- the reader refuses
    foreach ($file in 'en-us', 'ru', 'uk') {
        $t = New-ValidTree
        Remove-Item (Join-Path $t "msix/listing/release-notes/26.0924.1704.$file.txt")
        Check (Has-Read $t "26\.0924\.1704 has no notes file for: $file") "a version missing its $file file throws, naming the language"
    }
    $t = New-ValidTree
    Remove-Item (Join-Path $t 'msix/listing/release-notes/26.0924.1704.ru.txt'), (Join-Path $t 'msix/listing/release-notes/26.0924.1704.uk.txt')
    Check (Has-Read $t 'for: ru, uk') 'two missing languages are both named'

    $t = New-ValidTree
    Add-Note -Root $t -Version '26.1003.0030' -File 'ru' -Header 'Версия 26.0924.1704'
    Check (Has-Read $t 'release-notes/26\.1003\.0030\.ru\.txt: line 1 must be exactly') 'a header naming another version throws, naming the file'
    foreach ($case in @(
            @{ File = 'ru'; Header = 'Version 26.1003.0030'; Why = 'the English word in the Russian file' }
            @{ File = 'uk'; Header = 'Версия 26.1003.0030'; Why = 'the Russian word in the Ukrainian file' }
            @{ File = 'en-us'; Header = 'version 26.1003.0030'; Why = 'a lower-case word' }
            @{ File = 'en-us'; Header = 'Version 26.1003.0030 '; Why = 'a trailing space' }
            @{ File = 'en-us'; Header = 'Version  26.1003.0030'; Why = 'two spaces' }
            @{ File = 'en-us'; Header = '- Version 26.1003.0030'; Why = 'a bullet instead of a header' })) {
        $t = New-ValidTree
        Add-Note -Root $t -Version '26.1003.0030' -File $case.File -Header $case.Header
        Check (Has-Read $t 'line 1 must be exactly') "a wrong header throws ($($case.Why))"
    }

    $t = New-ValidTree
    Add-Raw $t '26.1003.0030.en-us.txt' "Version 26.1003.0030`n- straight after the header`n"
    Check (Has-Read $t 'line 2 must be empty') 'a line 2 that is not empty throws'
    foreach ($content in @("Version 26.1003.0030`n", "Version 26.1003.0030`n`n", "Version 26.1003.0030", '')) {
        $t = New-ValidTree
        Add-Raw $t '26.1003.0030.en-us.txt' $content
        Check (Has-Read $t 'line 1 must be exactly|line 2 must be empty') "a file with no bullet throws ($($content.Length) characters)"
    }
    foreach ($case in @(
            @{ Body = 'Plain text, no dash.'; Why = 'plain text' }
            @{ Body = '* A star bullet.'; Why = 'a star bullet' }
            @{ Body = '-No space after the dash.'; Why = 'no space after the dash' }
            @{ Body = '  - An indented bullet.'; Why = 'an indented bullet' }
            @{ Body = "- Fine.`nA second line of the same bullet."; Why = 'a wrapped bullet' }
            @{ Body = "- Fine.`n`n- A blank line between bullets."; Why = 'a blank line between bullets' }
            @{ Body = "- Fine.`n"; Why = 'a blank line after the bullets' })) {
        $t = New-ValidTree
        Add-Raw $t '26.1003.0030.uk.txt' ("Версія 26.1003.0030`n`n" + $case.Body + "`n")
        Check (Has-Read $t "26\.1003\.0030\.uk\.txt: line 3|26\.1003\.0030\.uk\.txt: line 4") "a body line that is not a bullet throws ($($case.Why))"
    }
    $t = New-ValidTree
    Add-Note -Root $t -Version '26.1003.0030' -File 'ru' -Bullets @('Fine.', '   ')
    Check (Has-Read $t 'is an empty bullet') 'an empty bullet throws'
    $t = New-ValidTree
    Add-Raw $t '26.1003.0030.ru.txt' "Версия 26.1003.0030`n`n- `n"
    Check (Has-Read $t 'is an empty bullet') 'a dash and a space alone is an empty bullet'
    $t = New-ValidTree
    Add-Raw $t '26.1003.0030.ru.txt' "Версия 26.1003.0030`r`n`r`n- Строка со стороны.`rA bare carriage return.`r`n"
    Check (Has-Read $t 'carriage return that is not part of a line ending') 'a bare carriage return throws'

    foreach ($stray in @('README.md', 'notes.txt', '26.1003.0030.de.txt', '26.1003.0030.en-us.txt.bak', '26.1003.003.en-us.txt', '26.1003.0030.en.txt', '26.0101.0000.EN-US.txt', '26.0101.0000.ru.TXT')) {
        $t = New-ValidTree
        Add-Raw $t $stray "Version 26.0101.0000`n`n- Whatever.`n"
        Check (Has-Read $t "release-notes/$([regex]::Escape($stray)) is not a release-notes file") "a stray file '$stray' throws, naming it"
    }
    $t = New-ValidTree
    New-Item -ItemType Directory -Path (Join-Path $t 'msix/listing/release-notes/old') | Out-Null
    Check (Has-Read $t 'release-notes/old is not a release-notes file') 'a stray folder throws'

    foreach ($case in @(
            @{ V = '26.1332.0000'; Why = 'month 13' }
            @{ V = '26.1032.0000'; Why = 'day 32' }
            @{ V = '26.1003.2500'; Why = 'hour 25' }
            @{ V = '26.1003.0060'; Why = 'minute 60' }
            @{ V = '26.0229.1200'; Why = '29 February of a common year' })) {
        $t = New-Tree
        Add-Version $t $case.V
        Check (Has-Read $t "'$([regex]::Escape($case.V))' is not a version stamp") "a file named for an impossible stamp throws ($($case.Why))"
    }
    $t = New-Tree
    Add-Version $t $pastPivot
    Check (Has-Read $t 'calendar''s pivot') 'a file named for a stamp past the calendar pivot throws'

    $t = New-Tree
    Check (Has-Read $t 'holds no release notes') 'an empty folder throws'
    Check (Has-Read (Join-Path $fixture 'no-such-root') 'does not exist') 'a missing folder throws'

    # --- the reader tolerates what a checkout and an editor produce
    $reference = Read-Notes (New-ValidTree)
    $t = New-Tree
    foreach ($version in '26.0924.1704', '26.1003.0030', '26.0806.2225') {
        foreach ($file in 'en-us', 'ru', 'uk') {
            $bullets = $reference | Where-Object { $_.Version -eq $version } | ForEach-Object { $_.Notes[@{ 'en-us' = 'en'; 'ru' = 'ru'; 'uk' = 'uk' }[$file]] }
            Add-Note -Root $t -Version $version -File $file -Bullets $bullets -Eol "`r`n"
        }
    }
    $crlf = Read-Notes $t
    Check ((ConvertTo-ReleaseNotesHtml -Notes $crlf -Code 'ru' -Authored @('en', 'ru', 'uk')) -ceq (ConvertTo-ReleaseNotesHtml -Notes $reference -Code 'ru' -Authored @('en', 'ru', 'uk'))) 'CRLF files render the same page as LF files'
    $t = New-Tree
    Add-Note -Root $t -Version '26.1003.0030' -File 'en-us' -Bom -Bullets @('With a byte order mark.')
    Add-Note -Root $t -Version '26.1003.0030' -File 'ru' -Bullets @('Без финального перевода строки.') -NoFinalNewline
    Add-Note -Root $t -Version '26.1003.0030' -File 'uk' -Bullets @('  Obrizano  ', 'Dva')
    $tolerated = Read-Notes $t
    Check ($tolerated.Count -eq 1 -and $tolerated[0].Notes['en'][0] -ceq 'With a byte order mark.') 'a byte order mark is not part of the header'
    Check ($tolerated[0].Notes['ru'][0] -ceq 'Без финального перевода строки.') 'a missing final newline is tolerated'
    Check ($tolerated[0].Notes['uk'][0] -ceq 'Obrizano' -and $tolerated[0].Notes['uk'].Count -eq 2) 'a bullet is trimmed'

    # --- the page
    $authored = @('en', 'ru', 'uk')
    $en = ConvertTo-ReleaseNotesHtml -Notes $notes -Code 'en' -Authored $authored
    Check ((Count-Of $en '<section ') -eq 3 -and (Count-Of $en '</section>') -eq 3) 'one section per version'
    Check ($en.Contains('<section class="privacy-section" id="v26-1003-0030" aria-labelledby="v26-1003-0030-title">')) 'a section has the version id and its labelled heading'
    Check ($en.Contains('<h2 id="v26-1003-0030-title" dir="ltr">26.1003.0030</h2>')) 'the heading is the version, left to right'
    Check ($en.Contains('<p class="release-date" dir="ltr"><time datetime="2026-10-03">2026-10-03</time></p>')) 'the date is a time element in ISO form, left to right'
    Check ((Count-Of $en '<p class="release-date" dir="ltr">') -eq 3) 'every version has its date line, left to right'
    Check ($en.IndexOf('id="v26-1003-0030"') -lt $en.IndexOf('id="v26-0924-1704"') -and $en.IndexOf('id="v26-0924-1704"') -lt $en.IndexOf('id="v26-0806-2225"')) 'sections run newest first'
    Check ((Count-Of $en '<li>') -eq 6 -and $en.Contains('<li>English one of 26.1003.0030.</li>')) 'every bullet is a list item'
    Check (-not $en.Contains('lang=') -and -not $en.Contains('dir="ltr">English')) 'an authored language marks no language on its list'
    Check ($en.Contains('<ul class="trust-list">') -and -not ($en -match '[А-Яа-я]')) 'the English page holds the English bullets only'
    Check (-not $en.Contains("`r") -and -not $en.StartsWith("`n") -and -not $en.EndsWith("`n")) 'the output has no carriage return and no leading or trailing newline'
    Check ($en.Contains("</section>`n`n        <section")) 'sections are separated by one blank line'
    Check (@($en -split "`n" | Where-Object { $_ -match '\s+$' }).Count -eq 0) 'no line ends in white space'

    $ru = ConvertTo-ReleaseNotesHtml -Notes $notes -Code 'ru' -Authored $authored
    Check ($ru.Contains('<li>Русский первый 26.1003.0030.</li>') -and -not $ru.Contains('English') -and -not $ru.Contains('lang=')) 'the Russian page holds the Russian bullets and marks no language'
    $uk = ConvertTo-ReleaseNotesHtml -Notes $notes -Code 'uk' -Authored $authored
    Check ($uk.Contains('<li>Український другий 26.0806.2225.</li>') -and -not $uk.Contains('English') -and -not $uk.Contains('lang=')) 'the Ukrainian page holds the Ukrainian bullets and marks no language'
    foreach ($code in 'de', 'ar', 'zh', 'ur') {
        $other = ConvertTo-ReleaseNotesHtml -Notes $notes -Code $code -Authored $authored
        Check ((Count-Of $other '<ul class="trust-list" lang="en" dir="ltr">') -eq 3) "$($code): every list is marked lang=`"en`" dir=`"ltr`""
        Check ($other.Contains('<li>English one of 26.1003.0030.</li>') -and -not ($other -match '[А-Яа-я]')) "$($code): the bullets are the English ones"
        Check ($other.Contains('<h2 id="v26-1003-0030-title" dir="ltr">26.1003.0030</h2>') -and $other.Contains('datetime="2026-10-03"')) "$($code): heading and date are the same as English"
    }
    # A right-to-left page must not split a version's heading from its date: the heading is forced left to right, so
    # the date line is too, or the heading sits at the left edge and the date at the right.
    foreach ($code in 'ar', 'ur') {
        $rtl = ConvertTo-ReleaseNotesHtml -Notes $notes -Code $code -Authored $authored
        Check ((Count-Of $rtl '<h2 ') -eq (Count-Of $rtl '<p class="release-date" dir="ltr"><time datetime="')) "$($code): every heading has a left-to-right date line beside it"
        Check (-not ($rtl -match '<p class="release-date"(?! dir="ltr")')) "$($code): no date line is left to inherit the right-to-left direction"
    }
    $withEnglishOnly = ConvertTo-ReleaseNotesHtml -Notes $notes -Code 'ru' -Authored @('en')
    Check ($withEnglishOnly.Contains('lang="en" dir="ltr"') -and $withEnglishOnly.Contains('English one')) 'the -Authored list decides: a language outside it gets the English bullets'
    Check (Throws { ConvertTo-ReleaseNotesHtml -Notes $notes -Code 'de' -Authored @('en', 'de') } "no 'de' notes to render") 'an authored language the notes do not carry throws'
    Check (Throws { ConvertTo-ReleaseNotesHtml -Notes @() -Code 'en' -Authored $authored } 'no release notes to render') 'nothing to render throws'

    $firstDe = ConvertTo-ReleaseNotesHtml -Notes $notes -Code 'de' -Authored $authored
    $null = ConvertTo-ReleaseNotesHtml -Notes $notes -Code 'ru' -Authored $authored
    Check (($firstDe -ceq (ConvertTo-ReleaseNotesHtml -Notes $notes -Code 'de' -Authored $authored)) -and ($en -ceq (ConvertTo-ReleaseNotesHtml -Notes $notes -Code 'en' -Authored $authored))) 'rendering holds no state: the same input gives the same text, whatever was rendered between'
    Check ((Read-Notes $tree | ForEach-Object { ConvertTo-ReleaseNotesHtml -Notes @($_) -Code 'en' -Authored $authored }).Count -eq 3) 'one version renders on its own'

    $t = New-Tree
    Add-Note -Root $t -Version '26.1003.0030' -File 'en-us' -Bullets @('Use <b> & "quotes" in 5 > 3.')
    Add-Note -Root $t -Version '26.1003.0030' -File 'ru' -Bullets @('Русский.')
    Add-Note -Root $t -Version '26.1003.0030' -File 'uk' -Bullets @('Український.')
    $escaped = ConvertTo-ReleaseNotesHtml -Notes (Read-Notes $t) -Code 'en' -Authored $authored
    Check ($escaped.Contains('<li>Use &lt;b&gt; &amp; &quot;quotes&quot; in 5 &gt; 3.</li>')) 'a bullet goes through the generator escaping function'

    # --- the repository's own folder
    $repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $real = Read-Notes $repositoryRoot
    Check ($real.Count -ge 1) 'the repository folder yields at least one version'
    $descending = $true
    for ($index = 1; $index -lt $real.Count; $index++) { if ($real[$index - 1].Date -le $real[$index].Date) { $descending = $false } }
    Check $descending 'the repository versions run strictly newest first'
    Check (@($real | Where-Object { $_.Notes['en'].Count -lt 1 -or $_.Notes['ru'].Count -lt 1 -or $_.Notes['uk'].Count -lt 1 }).Count -eq 0) 'every repository version has bullets in en, ru and uk'
    Check ((ConvertTo-ReleaseNotesHtml -Notes $real -Code 'de' -Authored $authored).Contains("id=`"v$($real[0].Version.Replace('.', '-'))`"")) 'the repository notes render, newest first'
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) {
    Write-Output "release-notes: FAIL ($($failures.Count) of $($passed + $failures.Count))"
    exit 1
}
Write-Output "release-notes: PASS ($passed checks)"
exit 0
