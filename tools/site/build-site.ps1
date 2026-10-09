<#
    SP-0034: renders the GitHub Pages site in every shipped interface language.

    Input : tools/site/templates/*.html + site.js, and one copy deck per language in
            tools/site/copy/<dictionary-code>.txt.
    Output: docs/index.html, docs/privacy.html, docs/trust.html, docs/whats-new.html and docs/support.html (English -
            the canonical root), docs/404.html (the not-found page, SP-0194),
            plus the same five files under docs/<code>/ for every other language, and docs/site.js.
            The set is the $pages list below; the sitemap and the held-address check read it too.

    Why static pages rather than the previous client-side swap: hreflang needs one URL per language.
    GitHub Pages deploys docs/ verbatim with no build step, so the generated files are committed
    output - re-run this script and commit whatever it changes.

    The language list is never written here. It comes from StreamsPlayer.Core's InterfaceLanguages
    registry via tools/InterfaceLanguages.ps1, so a fourteenth language needs no edit to this file.

    Usage:
      pwsh -NoProfile -File tools/site/build-site.ps1
      pwsh -NoProfile -File tools/site/build-site.ps1 -Check   # fail if docs/ is stale, a held address
                                                               # (tools/site/held-addresses.json) does not
                                                               # resolve, or a fact or a pillar disagrees with
                                                               # its source (tools/site/site-facts.json,
                                                               # POSITIONING.md); write nothing

    The language count and the minimum Windows are rendered into the copy decks from their sources (SP-0198):
    a deck carries [[languages]] and [[windows]], never the number.

    The deck reader and parity check, the structured data, sitemap, robots.txt and verification tags, the
    not-found page and the release-notes reader live in SiteDecks.ps1, SiteDiscovery.ps1, SiteNotFound.ps1 and
    ReleaseNotes.ps1 (SP-0039). What's new is rendered from msix/listing/release-notes, so a notes file added there
    changes docs/ and the site check goes stale until this script is re-run.
#>
[CmdletBinding()]
param(
    # Report what would change and exit non-zero if anything is stale, instead of writing.
    [switch] $Check,
    [string] $BaseUrl = 'https://serzhyale.github.io/StreamsPlayer/'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/../InterfaceLanguages.ps1"
. "$PSScriptRoot/HeldAddresses.ps1"
. "$PSScriptRoot/SiteFacts.ps1"
. "$PSScriptRoot/SiteDecks.ps1"
. "$PSScriptRoot/SiteDiscovery.ps1"
. "$PSScriptRoot/SiteNotFound.ps1"
. "$PSScriptRoot/ReleaseNotes.ps1"

$root = Get-RepositoryRoot
$copyDirectory = Join-Path $root 'tools/site/copy'
$templateDirectory = Join-Path $root 'tools/site/templates'
$outputDirectory = Join-Path $root 'docs'

# The pages the generator owns, by template name and output file name.
$pages = @(
    [pscustomobject]@{ Name = 'home';    Template = 'index.html';   File = 'index.html' }
    [pscustomobject]@{ Name = 'privacy'; Template = 'privacy.html'; File = 'privacy.html' }
    # SP-0105: the INSTALL-TRUST page - what the SmartScreen warning is, why it appears, what to click,
    # and what the app never does. A page of its own rather than a home-page section, because it is the
    # URL a warned user is sent to from the README and the Distribution section.
    [pscustomobject]@{ Name = 'trust';   Template = 'trust.html';   File = 'trust.html' }
    # SP-0039 (SITE-STRUCTURE section 2): the release notes, rendered from msix/listing/release-notes, and the
    # support page. Addresses permanent from first publish (rule 8).
    [pscustomobject]@{ Name = 'whatsnew'; Template = 'whats-new.html'; File = 'whats-new.html' }
    [pscustomobject]@{ Name = 'support';  Template = 'support.html';   File = 'support.html' }
)

# Languages whose copy the owner wrote himself. Everything else is machine-produced and says so on
# the page. A new language is machine-translated until someone puts it in this list - the safe
# direction for an honesty notice.
$humanAuthored = @('en', 'ru', 'uk')

# PAGE-STYLE section 4.2: the three locales of the segmented header control, in this order, with the
# visible label and the key `sza-lang` holds (UA labels and keys `ua`; the ISO code stays `uk`).
$coreLocales = @(
    [pscustomobject]@{ Code = 'ru'; Key = 'ru'; Label = 'RU' }
    [pscustomobject]@{ Code = 'en'; Key = 'en'; Label = 'EN' }
    [pscustomobject]@{ Code = 'uk'; Key = 'ua'; Label = 'UA' }
)

# PAGE-STYLE section 0 step 1 (SITE-EXPERIENCE rule 1) and WAVE-PARTICLES section 7: the served kit and the
# served backdrop script are byte-identical to the pinned catalog files. A hand edit, or a line-ending
# rewrite, fails here before it reaches the site.
$vendoredPins = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $root 'tools/site/kit-provenance.txt')) {
    if ($line -match '^(?<file>[\w.\-]+)\s+(?<hash>[0-9A-Fa-f]{64})\s*$') { $vendoredPins[$Matches['file']] = $Matches['hash'].ToUpperInvariant() }
}
foreach ($vendored in 'sza-kit.css', 'wave-particles.js') {
    if (-not $vendoredPins.ContainsKey($vendored)) { throw "tools/site/kit-provenance.txt has no $vendored pin." }
    $vendoredPath = Join-Path $root "docs/assets/$vendored"
    if (-not (Test-Path -LiteralPath $vendoredPath)) { throw "docs/assets/$vendored is missing (vendor it: tools/site/kit-provenance.txt)." }
    $vendoredHash = (Get-FileHash -LiteralPath $vendoredPath -Algorithm SHA256).Hash
    if ($vendoredHash -ne $vendoredPins[$vendored]) {
        throw "docs/assets/$vendored is not the pinned file (SHA-256 $vendoredHash, pinned $($vendoredPins[$vendored])). Vendored files are never edited in this repository."
    }
}


# SITE-FAMILY-MAP rule 3: one contact address everywhere. It is the application's own constant
# (ProductInfo.AuthorEmail, the recipient of "Send logs to the author"), read from its source so the site and the app
# cannot name different people. A copy deck writes [[email]]; a template writes {{contact.email}} (replaced where the
# templates are loaded, so the shared footer renders the same on every page and on the not-found page).
$productInfoSource = [System.IO.File]::ReadAllText((Join-Path $root 'src/StreamsPlayer.App/ProductInfo.cs'))
$contactMatch = [regex]::Match($productInfoSource, 'AuthorEmail\s*=\s*"(?<address>[\w.+\-]+@[\w.\-]+)"')
if (-not $contactMatch.Success) { throw 'src/StreamsPlayer.App/ProductInfo.cs holds no AuthorEmail constant the site can read.' }
$contactEmail = $contactMatch.Groups['address'].Value

# Inline replacements a copy deck may carry. They keep a command, a path and an address out of the
# translated prose and give each one a left-to-right island, which is what makes the Arabic and Urdu
# pages readable.
$inlineMarkup = [ordered]@{
    '[[command]]' = '<code dir="ltr">winget install SerZhyAle.StreamsPlayer</code>'
    '[[appdata]]' = '<code dir="ltr">%LOCALAPPDATA%\StreamsPlayer</code>'
    '[[email]]'   = '<span dir="ltr">{0}</span>' -f $contactEmail
    '[[hash]]'    = '<code dir="ltr">Get-FileHash -Algorithm SHA256 &lt;file&gt;</code>'
    '[[installdir]]' = '<code dir="ltr">%LOCALAPPDATA%\Programs\StreamsPlayer</code>'
}

# SP-0113 (ICON-SET rule 8): where a page names a control it shows the control's glyph - the vocabulary
# drawing this product vendors under assets/glyphs/, inlined as a currentColor SVG so it takes the page's
# text colour in both themes. A template writes {{glyph:<id>}}, a copy deck [[glyph:<id>]]; both come out
# as the same markup. The glyph is decorative beside the name it accompanies, so it is hidden from
# assistive technology; where a glyph is a button's only content, the button carries the aria-label.
$glyphMarker = [regex] '(?:\{\{|\[\[)glyph:(?<id>[\w.\-]+)(?:\}\}|\]\])'
$glyphCache = @{}

function Get-GlyphSvg {
    param([Parameter(Mandatory)] [string] $Id)

    if ($glyphCache.ContainsKey($Id)) { return $glyphCache[$Id] }
    $candidates = @((Join-Path $root "assets/glyphs/$Id.svg"), (Join-Path $root "assets/glyphs/pending/$Id.svg"))
    $source = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $source) { throw "No vendored glyph for '$Id' (assets/glyphs/ - run tools/Sync-IconGlyphs.ps1)." }

    $svg = [System.IO.File]::ReadAllText($source)
    $viewBox = [regex]::Match($svg, 'viewBox="(?<v>[^"]+)"').Groups['v'].Value
    $inner = [regex]::Match($svg, '(?s)<svg[^>]*>(?<i>.*)</svg>').Groups['i'].Value
    $inner = [regex]::Replace($inner.Trim(), '>\s+<', '><')
    $markup = '<svg class="glyph" viewBox="{0}" width="16" height="16" aria-hidden="true" focusable="false">{1}</svg>' -f $viewBox, $inner
    $glyphCache[$Id] = $markup
    return $markup
}

function Expand-Glyphs {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Html)

    return $glyphMarker.Replace($Html, { param($match) Get-GlyphSvg -Id $match.Groups['id'].Value })
}

function ConvertTo-HtmlText {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

    # Escapes for both text nodes and double-quoted attributes, so one function covers every
    # substitution site in the templates.
    $escaped = $Value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
    foreach ($entry in $inlineMarkup.GetEnumerator()) {
        $escaped = $escaped.Replace($entry.Key, $entry.Value)
    }
    return Expand-Glyphs -Html $escaped
}

function Get-DocsUrl {
    param([Parameter(Mandatory)] [string] $Code)

    # PAGE-CONTENT "The landing always carries" item 4: a product with no documentation portal links the one
    # page that documents it - the README mirror of the visitor's language, as ProductInfo.InstructionsUrl does
    # (README.ru.md, README.uk.md, README.md for every other language).
    $name = switch ($Code) { 'ru' { 'README.ru.md' } 'uk' { 'README.uk.md' } default { 'README.md' } }
    return "https://github.com/SerZhyAle/StreamsPlayer/blob/main/$name"
}

function Get-RelativeUrl {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $FromCode,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $ToCode,
        [Parameter(Mandatory)] [string] $File
    )

    # '' is the canonical English root; any other code is a folder directly under it.
    $up = if ($FromCode) { '../' } else { '' }
    $down = if ($ToCode) { "$ToCode/" } else { '' }
    $leaf = if ($File -eq 'index.html') { '' } else { $File }
    $url = "$up$down$leaf"
    if ($url) { return $url }
    return './'
}

# ---------------------------------------------------------------- load and validate the copy decks

$languages = Get-InterfaceLanguages

# SP-0198 (SITE-REPRESENTATION rule 3): the language count and the minimum Windows are never typed in a deck.
# Each is a placeholder the decks carry and this renders from the fact's source; they join the
# markup table before the decks are compared, so a deck that types a number in its place fails the parity below.
$siteFacts = Get-SiteFactValue -Root $root -LanguageCount $languages.Count
$inlineMarkup['[[languages]]'] = [string] $siteFacts.Languages
$inlineMarkup['[[windows]]'] = $siteFacts.Windows

$decks = [ordered]@{}
$problems = [System.Collections.Generic.List[string]]::new()

foreach ($language in $languages) {
    $path = Join-Path $copyDirectory "$($language.DictionaryCode).txt"
    if (-not (Test-Path -LiteralPath $path)) {
        $problems.Add("$($language.DictionaryCode): no copy deck at tools/site/copy/$($language.DictionaryCode).txt")
        continue
    }
    $decks[$language.DictionaryCode] = Read-CopyDeck -Path $path
}

foreach ($file in Get-ChildItem -LiteralPath $copyDirectory -Filter '*.txt') {
    $code = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    if (-not ($languages.DictionaryCode -contains $code)) {
        $problems.Add("${code}: tools/site/copy/$($file.Name) belongs to no shipped language - delete it or add the language to the Core registry")
    }
}

if ($problems.Count) {
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "The site copy decks do not match the shipped language list ($($problems.Count) problem(s))."
}

$english = $decks['en']
foreach ($problem in Get-DeckParityProblem -English $english -Decks $decks -GlyphMarker $glyphMarker) { $problems.Add($problem) }

if ($problems.Count) {
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "The site copy decks are not in parity with English ($($problems.Count) problem(s))."
}

Write-Host ("Copy decks: {0} languages x {1} keys" -f $decks.Count, $english.Keys.Count) -ForegroundColor Green

# --------------------------------------------------------------------------------------- rendering

$templates = @{}
foreach ($name in 'head', 'switcher', 'languagerow', 'footer') {
    $templates[$name] = [System.IO.File]::ReadAllText((Join-Path $templateDirectory "_$name.html")).Replace("`r`n", "`n").TrimEnd("`n").Replace('{{contact.email}}', $contactEmail)
}

$written = [System.Collections.Generic.List[string]]::new()
$stale = [System.Collections.Generic.List[string]]::new()

# The recorded release notes (ReleaseNotes.ps1), read once and rendered once per language code: a malformed notes
# file fails the build here, before any page is written.
$releaseNotes = @(Get-SiteReleaseNote -Root $root)
$releaseNotesHtml = @{}

function Save-Generated {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Content
    )

    # Every generated file lands either directly in docs/ or in docs/<code>/. docs/agent,
    # docs/assets, docs/contracts and docs/localization are hand-written and
    # must never be touched, so the target is checked rather than trusted.
    $relative = [System.IO.Path]::GetRelativePath($outputDirectory, $Path).Replace('\', '/')
    $depth = $relative.Split('/').Length
    if ($depth -gt 2 -or ($depth -eq 2 -and -not ($languages.DictionaryCode -contains $relative.Split('/')[0]))) {
        throw "Refusing to write outside the generated area: docs/$relative"
    }

    # Line endings are normalized before comparing: .gitattributes checks docs/*.html out as CRLF on every
    # platform (CI included), the generator writes LF, and git stores LF either way - so only content counts.
    $existing = if (Test-Path -LiteralPath $Path) { [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n") } else { $null }
    if ($existing -eq $Content) { return }

    if ($Check) {
        $script:stale.Add("docs/$relative")
        return
    }

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
    $script:written.Add("docs/$relative")
}

foreach ($language in $languages) {
    $code = $language.DictionaryCode
    $isRoot = $code -eq 'en'
    $urlCode = if ($isRoot) { '' } else { $code }
    $deck = $decks[$code]

    foreach ($page in $pages) {
        $template = [System.IO.File]::ReadAllText((Join-Path $templateDirectory $page.Template)).Replace("`r`n", "`n").Replace('{{contact.email}}', $contactEmail)

        foreach ($name in 'head', 'switcher', 'languagerow', 'footer') {
            $template = $template.Replace("{{include:$name}}", $templates[$name])
        }
        $template = Expand-Glyphs -Html $template

        # Search-console verification tags (SiteDiscovery.ps1): the English root landing only. An absent tag takes
        # its whole line with it, so a null token leaves every page byte-identical.
        $verification = Get-VerificationMeta -Root $root -IsRootLanding ($isRoot -and $page.File -eq 'index.html')
        if (-not $verification) { $template = $template.Replace("{{page.verification}}`n", '') }

        # What's new: the notes are written in English, Russian and Ukrainian, so the ten other locales say that the
        # list below is English. Where the owner wrote the deck there is no note, and the empty case takes its whole
        # line with it for the same reason as the verification tag.
        $languageNote = if ($humanAuthored -contains $code) {
            ''
        } else {
            '      <p class="privacy-intro">{0}</p>' -f (ConvertTo-HtmlText $deck.Values['whatsnew-language-note'])
        }
        if (-not $languageNote) { $template = $template.Replace("{{page.languageNote}}`n", '') }
        if ($page.Name -eq 'whatsnew' -and -not $releaseNotesHtml.ContainsKey($code)) {
            $releaseNotesHtml[$code] = ConvertTo-ReleaseNotesHtml -Notes $releaseNotes -Code $code -Authored $humanAuthored
        }

        $alternates = foreach ($other in $languages) {
            $otherUrl = if ($other.DictionaryCode -eq 'en') { '' } else { "$($other.DictionaryCode)/" }
            $leaf = if ($page.File -eq 'index.html') { '' } else { $page.File }
            '    <link rel="alternate" hreflang="{0}" href="{1}{2}{3}">' -f (Get-HrefLang -Language $other), $BaseUrl, $otherUrl, $leaf
        }
        $leafForDefault = if ($page.File -eq 'index.html') { '' } else { $page.File }
        $alternates += '    <link rel="alternate" hreflang="x-default" href="{0}{1}">' -f $BaseUrl, $leafForDefault

        # PAGE-STYLE section 4.2: RU EN UA are the segmented control in the header, in that order, on every
        # page; every further locale is a text-only link in a secondary row under the header. Each is a
        # link to a standalone page, so both work without script.
        $languageUrls = [ordered]@{}
        $coreLinks = [System.Collections.Generic.List[string]]::new()
        $links = [System.Collections.Generic.List[string]]::new()
        foreach ($other in $languages) {
            $languageUrls[$other.DictionaryCode] = Get-RelativeUrl -FromCode $urlCode -ToCode ($(if ($other.DictionaryCode -eq 'en') { '' } else { $other.DictionaryCode })) -File $page.File
        }
        foreach ($core in $coreLocales) {
            $other = $languages | Where-Object { $_.DictionaryCode -eq $core.Code }
            if (-not $other) { throw "The core locale '$($core.Code)' of PAGE-STYLE section 4.2 is not in the shipped language list." }
            $current = if ($core.Code -eq $code) { ' aria-current="page"' } else { '' }
            $coreLinks.Add(('        <a href="{0}" data-lang="{1}" lang="{2}" hreflang="{2}" title="{3}"{4}>{5}</a>' -f
                (ConvertTo-HtmlText $languageUrls[$core.Code]), $core.Key, (Get-HrefLang -Language $other), (ConvertTo-HtmlText $other.Endonym), $current, $core.Label))
        }
        foreach ($other in $languages) {
            if ($coreLocales.Code -contains $other.DictionaryCode) { continue }
            $current = if ($other.DictionaryCode -eq $code) { ' aria-current="page"' } else { '' }
            $links.Add(('        <li><a href="{0}" data-locale="{1}" lang="{2}" hreflang="{2}" title="{3}"{4}>{5}</a></li>' -f
                (ConvertTo-HtmlText $languageUrls[$other.DictionaryCode]), $other.DictionaryCode, (Get-HrefLang -Language $other), (ConvertTo-HtmlText $other.Endonym), $current, $other.DictionaryCode.ToUpperInvariant()))
        }

        $machineNote = if ($humanAuthored -contains $code) {
            ''
        } else {
            @(
                '      <div class="container machine-note">'
                '        <p>{0}</p>' -f (ConvertTo-HtmlText $deck.Values['machine-note'])
                '      </div>'
            ) -join "`n"
        }

        $canonical = '{0}{1}{2}' -f $BaseUrl, $(if ($isRoot) { '' } else { "$code/" }), $(if ($page.File -eq 'index.html') { '' } else { $page.File })

        $structural = [ordered]@{
            '{{page.lang}}'           = Get-HrefLang -Language $language
            '{{page.code}}'           = $code
            '{{page.dir}}'            = if ($language.RightToLeft) { 'rtl' } else { 'ltr' }
            '{{page.entry}}'          = if ($isRoot) { 'true' } else { 'false' }
            '{{page.base}}'           = if ($isRoot) { '' } else { '../' }
            '{{page.home}}'           = Get-RelativeUrl -FromCode $urlCode -ToCode $urlCode -File 'index.html'
            '{{page.privacy}}'        = 'privacy.html'
            '{{page.trust}}'          = 'trust.html'
            '{{page.whatsnew}}'       = 'whats-new.html'
            '{{page.support}}'        = 'support.html'
            '{{page.releaseNotes}}'   = if ($page.Name -eq 'whatsnew') { $releaseNotesHtml[$code] } else { '' }
            '{{page.languageNote}}'   = $languageNote
            '{{page.docs}}'           = Get-DocsUrl -Code $code
            '{{page.canonical}}'      = $canonical
            '{{page.alternates}}'     = $alternates -join "`n"
            '{{page.languageUrls}}'   = ($languageUrls | ConvertTo-Json -Compress)
            '{{page.languageCore}}'   = $coreLinks -join "`n"
            '{{page.languageLinks}}'  = $links -join "`n"
            '{{page.machineNote}}'    = $machineNote
            '{{page.title}}'          = ConvertTo-HtmlText $deck.Values["title-$($page.Name)"]
            '{{page.description}}'    = ConvertTo-HtmlText $deck.Values["description-$($page.Name)"]
            '{{page.verification}}'   = $verification
            '{{page.ogImage}}'        = "${BaseUrl}assets/og-card.png"
            '{{page.jsonLd}}'         = Get-JsonLd -Page $page -Pages $pages -Deck $deck -Language $language -Canonical $canonical -BaseUrl $BaseUrl -ScreenshotDirectory (Join-Path $outputDirectory 'assets/screens')
        }

        foreach ($entry in $structural.GetEnumerator()) {
            $template = $template.Replace($entry.Key, [string] $entry.Value)
        }
        foreach ($key in $deck.Keys) {
            $template = $template.Replace("{{t.$key}}", (ConvertTo-HtmlText $deck.Values[$key]))
        }

        $leftover = [regex]::Matches($template, '\{\{[^}]+\}\}') | ForEach-Object { $_.Value } | Sort-Object -Unique
        if ($leftover) {
            throw "$code/$($page.File): unresolved template token(s): $($leftover -join ', ')"
        }

        $target = if ($isRoot) { Join-Path $outputDirectory $page.File } else { Join-Path $outputDirectory "$code/$($page.File)" }
        Save-Generated -Path $target -Content $template
    }
}

# SP-0194 (SITE-STRUCTURE rules 2 and 15): the not-found page, noindex and absent from the sitemap (SiteNotFound.ps1).
Save-Generated -Path (Join-Path $outputDirectory '404.html') `
    -Content (New-NotFoundPage -CopyDirectory $copyDirectory -TemplateDirectory $templateDirectory -Templates $templates -English $english -BaseUrl $BaseUrl)

Save-Generated -Path (Join-Path $outputDirectory 'site.js') `
    -Content ([System.IO.File]::ReadAllText((Join-Path $templateDirectory 'site.js')).Replace("`r`n", "`n"))

# sitemap.xml and robots.txt come from the same language x page product the pages do (SiteDiscovery.ps1).
Save-Generated -Path (Join-Path $outputDirectory 'sitemap.xml') -Content (New-SitemapXml -Pages $pages -Languages $languages -BaseUrl $BaseUrl)
Save-Generated -Path (Join-Path $outputDirectory 'robots.txt') -Content (New-RobotsTxt -BaseUrl $BaseUrl)

# A language dropped from the registry leaves its folder behind. Only two-letter folders are
# considered, which is why docs/agent, docs/assets, docs/contracts and docs/localization
# cannot be caught by this, and only the files this generator writes may be present.
foreach ($directory in Get-ChildItem -LiteralPath $outputDirectory -Directory) {
    if ($directory.Name -notmatch '^[a-z]{2}$') { continue }
    if ($languages.DictionaryCode -contains $directory.Name) { continue }
    $contents = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File | ForEach-Object { $_.Name })
    if (@($contents | Where-Object { $_ -notin $pages.File }).Count) {
        Write-Warning "docs/$($directory.Name)/ is not a shipped language but holds files this generator did not write - leaving it alone."
        continue
    }
    if ($Check) {
        $stale.Add("docs/$($directory.Name)/ (stale language folder)")
    } else {
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
        Write-Host "Removed stale language folder docs/$($directory.Name)/" -ForegroundColor Yellow
    }
}

if ($Check) {
    # SP-0193 (SITE-STRUCTURE rule 8): every address a surface outside docs/ holds resolves against the
    # page set just generated, and no surface holds one the list does not know.
    $generatedFiles = @(
        foreach ($language in $languages) {
            foreach ($page in $pages) {
                if ($language.DictionaryCode -eq 'en') { $page.File } else { "$($language.DictionaryCode)/$($page.File)" }
            }
        }
        '404.html', 'site.js', 'sitemap.xml', 'robots.txt'
    )
    $heldProblems = @(Get-HeldAddressProblem -Root $root -BaseUrl $BaseUrl -GeneratedFile $generatedFiles)

    # SP-0198 (SITE-REPRESENTATION rules 1 to 3): the facts the copy states agree with their sources, and the
    # surfaces list the pillars of POSITIONING.md in its order.
    $deckValues = [ordered]@{}
    foreach ($code in $decks.Keys) { $deckValues[$code] = $decks[$code].Values }
    $factProblems = @(Get-SiteFactProblem -Root $root -Deck $deckValues)

    if ($stale.Count) {
        Write-Host "Stale generated files:" -ForegroundColor Red
        $stale | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    if ($heldProblems.Count) {
        Write-Host "Held site addresses (tools/site/held-addresses.json):" -ForegroundColor Red
        $heldProblems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    if ($factProblems.Count) {
        Write-Host "Site facts and positioning (tools/site/site-facts.json, POSITIONING.md):" -ForegroundColor Red
        $factProblems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    if ($stale.Count) {
        throw "docs/ does not match the generator ($($stale.Count) file(s)). Run tools/site/build-site.ps1 and commit the result."
    }
    if ($heldProblems.Count) {
        throw "The held-address list and the site disagree ($($heldProblems.Count) problem(s))."
    }
    if ($factProblems.Count) {
        throw "The site copy and its fact sources disagree ($($factProblems.Count) problem(s))."
    }
    Write-Host "docs/ is up to date." -ForegroundColor Green
    Write-Host ("Held addresses: {0} listed, all resolve." -f (@((Get-Content -LiteralPath (Join-Path $root 'tools/site/held-addresses.json') -Raw | ConvertFrom-Json).addresses).Count)) -ForegroundColor Green
    Write-Host ("Site facts: {0} languages, {1} - rendered from their sources; channels and pillars hold on every surface." -f $siteFacts.Languages, $siteFacts.Windows) -ForegroundColor Green
    return
}

Write-Host ("Pages: {0} languages x {1} = {2} files, plus site.js" -f $languages.Count, $pages.Count, ($languages.Count * $pages.Count)) -ForegroundColor Green
if ($written.Count) {
    Write-Host "Changed:" -ForegroundColor Cyan
    $written | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host "No file changed - output was already current." -ForegroundColor Cyan
}
