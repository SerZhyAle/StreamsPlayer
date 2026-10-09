#requires -Version 7.0
<#
.SYNOPSIS
    SP-0039: fixture tests for what the site emits so that a search engine can find it (SiteDiscovery.ps1), and
    for the deck parity check split out beside it (SiteDecks.ps1).

.DESCRIPTION
    Builds a throwaway tree with a verification file and a few screenshots, three small languages and three
    pages, then runs the real functions against it:

      passes     null verification tokens render nothing; a Google token renders one tag on the English root
                 landing and none elsewhere, a Bing token likewise, both in a fixed order; a sitemap holds
                 languages x pages entries, each with every alternate and the x-default, and no not-found page;
                 robots.txt allows crawling and names the sitemap; every JSON-LD block is one block that parses,
                 whatever quotes and brackets the copy holds; the landing is a SoftwareApplication with the zero
                 price, the page's language, a durable download address and the locale's screenshot, and names the
                 release notes (releaseNotes) and the support page (softwareHelp) of its own locale, at the addresses
                 the sitemap lists; the other pages (the release notes and the support page among them) are a
                 WebPage that is part of the site; decks in parity pass
      fails      a malformed token, on every page; a missing or unparseable verification file; a landing whose
                 locale has no screenshot; a landing rendered from a page list without the release notes or the
                 support page, or with one of them twice; a deck with a missing key, an empty value, a drifted
                 glyph or a drifted placeholder
      absent     softwareVersion and any rating, on purpose (PAGE-CONTENT, SITE-EXPERIENCE rule 13)

    Then the same questions are put to the REAL generated pages (the section "generated pages"), after the fixture
    proved the gate can fail. It reads docs/ and judges structure only - never text, so a copy deck being rewritten
    does not move it:
      every page   the page table x languages, plus 404.html, has a footer link ending whats-new.html and one ending
                   support.html, each reaching the same locale's page (404: the English one); a link in the head
                   (canonical, alternate) does not count, so removing the footer link fails even on those two pages
      sitemap      holds exactly languages x pages <loc> entries, each the address the generator derives, no 404
      JSON-LD      exactly one block that parses on every page (404.html carries none, by design); the landing names
                   its locale's release notes and support page
    The page table is read from build-site.ps1's syntax tree, the languages from tools/site/copy (the generator throws
    unless those equal the shipped registry), and the address from the generator's BaseUrl default, so no second list
    exists. -DocsDirectory points the section at a copy (temp/SP-0039/..) to show it fail without touching docs/.

    Exit 0 when every check passes, 1 otherwise. The last line is `site-discovery: PASS|FAIL (..)`.
#>
[CmdletBinding()]
param(
    # The generated site the "generated pages" section judges; the repository's docs/ when omitted.
    [string] $DocsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/SiteDiscovery.ps1"
. "$PSScriptRoot/SiteDecks.ps1"

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}
function Throws([scriptblock] $Action) {
    try { & $Action | Out-Null } catch { return $true }
    return $false
}
function ThrowsLike([scriptblock] $Action, [string] $Pattern) {
    # True only when the action throws and the message names the reason, so a test cannot pass on an unrelated failure.
    try { & $Action | Out-Null } catch { return [bool] ($_.Exception.Message -match $Pattern) }
    return $false
}

function Read-GeneratorFact {
    # build-site.ps1 renders the site when it runs, so it cannot be dot-sourced to read its page table. Its syntax tree
    # can be read: the `$pages` assignment (a list of literals) is evaluated alone, and the BaseUrl parameter's default
    # is read as written. The gate below therefore judges the page set and the address the generator really uses and
    # holds no second list of either.
    param([Parameter(Mandatory)] [string] $Path)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref] $tokens, [ref] $parseErrors)
    if ($parseErrors.Count) { throw "$Path does not parse: $($parseErrors[0].Message)" }

    $assignments = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -eq 'pages'
            }, $true))
    if ($assignments.Count -ne 1) { throw "$Path assigns `$pages $($assignments.Count) times; the page table is expected exactly once." }
    $pages = @([scriptblock]::Create($assignments[0].Right.Extent.Text).Invoke() | ForEach-Object { $_ })
    if ($pages.Count -eq 0 -or @($pages | Where-Object { -not $_.PSObject.Properties['Name'] -or -not $_.PSObject.Properties['File'] }).Count) {
        throw "The `$pages table of $Path is empty or holds an entry without Name and File."
    }

    $parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'BaseUrl' })
    if ($parameter.Count -ne 1 -or $parameter[0].DefaultValue -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) {
        throw "$Path has no BaseUrl parameter with a literal default."
    }
    return [pscustomobject]@{ Pages = $pages; BaseUrl = $parameter[0].DefaultValue.Value }
}

function Get-GeneratedSiteProblem {
    # What is wrong with a generated site's navigation and discovery, read from the files and never from their prose:
    # one string per problem, none when the site holds. Used on the repository's docs/ and, with the fixture below, on
    # sites made to fail.
    param(
        [Parameter(Mandatory)] [string] $DocsDirectory,
        # The dictionary codes of the shipped languages; 'en' is the root, the others are folders.
        [Parameter(Mandatory)] [string[]] $LanguageCode,
        # The file name of every page the generator owns (the page table's File).
        [Parameter(Mandatory)] [string[]] $PageFile,
        [Parameter(Mandatory)] [string] $BaseUrl,
        # Every page links these two from its footer, in its own locale, and the landing's structured data names them.
        [string] $NotesFile = 'whats-new.html',
        [string] $SupportFile = 'support.html',
        [string] $LandingFile = 'index.html'
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    $docsRoot = [System.IO.Path]::GetFullPath($DocsDirectory)
    $ldPattern = '(?s)<script\b[^>]*\btype="application/ld\+json"[^>]*>(.*?)</script>'
    $footerLinks = @($NotesFile, $SupportFile)

    # Every page to judge: [relative path, locale folder ('' for the root), whether it is the not-found page].
    $targets = [System.Collections.Generic.List[object]]::new()
    foreach ($code in $LanguageCode) {
        $folder = if ($code -eq 'en') { '' } else { $code }
        foreach ($file in $PageFile) {
            $relative = if ($folder) { "$folder/$file" } else { $file }
            $targets.Add([pscustomobject]@{ Relative = $relative; Folder = $folder; File = $file; NotFound = $false })
        }
    }
    $targets.Add([pscustomobject]@{ Relative = '404.html'; Folder = ''; File = '404.html'; NotFound = $true })

    foreach ($target in $targets) {
        $path = Join-Path $docsRoot $target.Relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $problems.Add("$($target.Relative): the page does not exist"); continue }
        $html = [System.IO.File]::ReadAllText($path)

        # Footer links. Only an anchor inside the footer counts: the head names the page's own address as canonical and
        # alternate, so counting any href would let the footer link be removed from those pages unnoticed.
        $footer = [regex]::Match($html, '(?s)<footer\b.*?</footer>')
        if (-not $footer.Success) {
            $problems.Add("$($target.Relative): the page has no footer")
        } else {
            $hrefs = @([regex]::Matches($footer.Value, '<a\b[^>]*?\shref="([^"]*)"') | ForEach-Object { $_.Groups[1].Value })
            foreach ($link in $footerLinks) {
                $ending = @($hrefs | Where-Object { $_ -cmatch ('(^|/)' + [regex]::Escape($link) + '$') })
                if ($ending.Count -eq 0) { $problems.Add("$($target.Relative): the footer has no link ending $link"); continue }

                $expected = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $docsRoot $target.Folder) $link))
                $expectedRelative = if ($target.Folder) { "$($target.Folder)/$link" } else { $link }
                $reaches = $false
                foreach ($href in $ending) {
                    $resolved = if ($href.StartsWith($BaseUrl, [System.StringComparison]::Ordinal)) {
                        Join-Path $docsRoot $href.Substring($BaseUrl.Length)
                    } elseif ($href -match '^[A-Za-z][A-Za-z0-9+.-]*:') {
                        $null
                    } else {
                        Join-Path (Split-Path $path -Parent) $href
                    }
                    if ($resolved -and [System.IO.Path]::GetFullPath($resolved) -ceq $expected) { $reaches = $true }
                }
                if (-not $reaches) {
                    $problems.Add("$($target.Relative): the footer's $link link does not reach $expectedRelative")
                } elseif (-not (Test-Path -LiteralPath $expected -PathType Leaf)) {
                    $problems.Add("$($target.Relative): the footer's $link link reaches a file that does not exist")
                }
            }
        }

        # Structured data: one block that parses on a page, none on the not-found page.
        $blocks = @([regex]::Matches($html, $ldPattern))
        if ($target.NotFound) {
            if ($blocks.Count -ne 0) { $problems.Add("$($target.Relative): the not-found page carries $($blocks.Count) JSON-LD block(s); it carries none by design") }
            continue
        }
        if ($blocks.Count -ne 1) { $problems.Add("$($target.Relative): $($blocks.Count) JSON-LD blocks, exactly one is expected"); continue }
        $parsed = $null
        try { $parsed = $blocks[0].Groups[1].Value | ConvertFrom-Json } catch { $parsed = $null }
        if ($null -eq $parsed) { $problems.Add("$($target.Relative): the JSON-LD block does not parse"); continue }

        if ($target.File -eq $LandingFile) {
            $prefix = if ($target.Folder) { "$($target.Folder)/" } else { '' }
            $notes = $parsed.PSObject.Properties['releaseNotes']
            if ($null -eq $notes -or $notes.Value -cne "$BaseUrl${prefix}$NotesFile") { $problems.Add("$($target.Relative): releaseNotes is not this locale's $NotesFile") }
            $help = $parsed.PSObject.Properties['softwareHelp']
            $helpUrl = if ($null -ne $help -and $null -ne $help.Value -and $help.Value.PSObject.Properties['url']) { $help.Value.url } else { $null }
            if ($helpUrl -cne "$BaseUrl${prefix}$SupportFile") { $problems.Add("$($target.Relative): softwareHelp.url is not this locale's $SupportFile") }
        }
    }

    # The sitemap: the generator's own language x page product, and nothing else - the not-found page least of all.
    $sitemapPath = Join-Path $docsRoot 'sitemap.xml'
    if (-not (Test-Path -LiteralPath $sitemapPath -PathType Leaf)) {
        $problems.Add('sitemap.xml: the file does not exist')
    } else {
        $sitemap = [System.IO.File]::ReadAllText($sitemapPath)
        try { $null = [xml] $sitemap } catch { $problems.Add('sitemap.xml: not well-formed XML') }
        $locs = @([regex]::Matches($sitemap, '<loc>([^<]+)</loc>') | ForEach-Object { $_.Groups[1].Value })
        $expectedLocs = @(foreach ($code in $LanguageCode) {
                foreach ($file in $PageFile) {
                    $folder = if ($code -eq 'en') { '' } else { "$code/" }
                    $leaf = if ($file -eq $LandingFile) { '' } else { $file }
                    "$BaseUrl$folder$leaf"
                }
            })
        if ($locs.Count -ne $expectedLocs.Count) {
            $problems.Add("sitemap.xml: $($locs.Count) <loc> entries, languages x pages is $($LanguageCode.Count) x $($PageFile.Count) = $($expectedLocs.Count)")
        }
        if (@($locs | Where-Object { $_ -match '404' }).Count) { $problems.Add('sitemap.xml: lists the not-found page') }
        if ($locs.Count -ne @($locs | Select-Object -Unique).Count) { $problems.Add('sitemap.xml: an address is listed twice') }
        $missing = @($expectedLocs | Where-Object { $locs -cnotcontains $_ })
        $extra = @($locs | Where-Object { $expectedLocs -cnotcontains $_ -and $_ -notmatch '404' })
        if ($missing.Count) { $problems.Add("sitemap.xml: $($missing.Count) page address(es) missing, first: $($missing[0])") }
        if ($extra.Count) { $problems.Add("sitemap.xml: $($extra.Count) address(es) that are no page, first: $($extra[0])") }
    }

    return @($problems)
}

$fixture =Join-Path ([System.IO.Path]::GetTempPath()) ("site-discovery-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null

$utf8 = New-Object System.Text.UTF8Encoding($false)
function Write-Fixture([string] $Path, [string] $Content) {
    $full = Join-Path $fixture $Path
    New-Item -ItemType Directory -Force -Path (Split-Path $full -Parent) | Out-Null
    [System.IO.File]::WriteAllText($full, $Content, $utf8)
}
function Set-Tokens($Google, $Bing) {
    $json = [ordered]@{ '$comment' = 'fixture'; google = $Google; bing = $Bing } | ConvertTo-Json
    Write-Fixture 'tools/site/search-verification.json' $json
}

# An address that is not the product's, so the held-address gate has nothing to list for this file.
$baseUrl = 'https://example.test/site/'
$screens = Join-Path $fixture 'docs/assets/screens'
$languages = @(
    [pscustomobject]@{ DictionaryCode = 'en'; CultureCode = 'en-US' }
    [pscustomobject]@{ DictionaryCode = 'ru'; CultureCode = 'ru-RU' }
    [pscustomobject]@{ DictionaryCode = 'zh'; CultureCode = 'zh-Hans-CN' }
)
$pages = @(
    [pscustomobject]@{ Name = 'home';    File = 'index.html' }
    [pscustomobject]@{ Name = 'privacy'; File = 'privacy.html' }
    [pscustomobject]@{ Name = 'trust';   File = 'trust.html' }
    [pscustomobject]@{ Name = 'whatsnew'; File = 'whats-new.html' }
    [pscustomobject]@{ Name = 'support';  File = 'support.html' }
)
function New-JsonLdDeck {
    $values = [ordered]@{}
    foreach ($page in $pages) {
        $values["title-$($page.Name)"] = "Player: the `"$($page.Name)`" page <b> & more"
        $values["description-$($page.Name)"] = "A player's `"$($page.Name)`" description, with a backslash \ and a closing </script> in it."
    }
    return [pscustomobject]@{ Values = $values }
}
function Get-JsonLdBlock([string] $Text) { return @([regex]::Matches($Text, '(?s)<script type="application/ld\+json">(.*?)</script>')) }

try {
    New-Item -ItemType Directory -Force -Path $screens | Out-Null
    foreach ($language in $languages) { Write-Fixture "docs/assets/screens/grid-$($language.DictionaryCode).jpg" 'jpeg' }

    # --- hreflang
    Check ((Get-HrefLang -Language $languages[0]) -eq 'en') 'a language with a region has its code, not the region, as hreflang'
    Check ((Get-HrefLang -Language $languages[2]) -eq 'zh-Hans') 'a script subtag is kept in hreflang'

    # --- page addresses
    Check ((Get-LocalePageUrl -Page $pages[0] -Language $languages[0] -BaseUrl $baseUrl) -eq $baseUrl) 'the English landing resolves to the base address'
    Check ((Get-LocalePageUrl -Page $pages[0] -Language $languages[1] -BaseUrl $baseUrl) -eq "${baseUrl}ru/") 'a landing in another language is its folder, with no file name'
    Check ((Get-LocalePageUrl -Page $pages[3] -Language $languages[0] -BaseUrl $baseUrl) -eq "${baseUrl}whats-new.html") 'an English page is the base address and its file'
    Check ((Get-LocalePageUrl -Page $pages[4] -Language $languages[2] -BaseUrl $baseUrl) -eq "${baseUrl}zh/support.html") 'a page in another language sits under its language folder'

    # --- verification tags
    Set-Tokens $null $null
    Check ((Get-VerificationMeta -Root $fixture -IsRootLanding $true) -eq '') 'null tokens render nothing on the root landing'
    Check ((Get-VerificationMeta -Root $fixture -IsRootLanding $false) -eq '') 'null tokens render nothing elsewhere'

    Set-Tokens 'google-token_0123456789' $null
    $root = Get-VerificationMeta -Root $fixture -IsRootLanding $true
    Check ($root -eq '    <meta name="google-site-verification" content="google-token_0123456789">') 'a Google token renders one tag on the root landing'
    Check ((Get-VerificationMeta -Root $fixture -IsRootLanding $false) -eq '') 'a Google token renders nothing on any other page'

    Set-Tokens $null 'BING0123456789ABCDEF'
    Check ((Get-VerificationMeta -Root $fixture -IsRootLanding $true) -eq '    <meta name="msvalidate.01" content="BING0123456789ABCDEF">') 'a Bing token renders one tag on the root landing'

    Set-Tokens 'google-token_0123456789' 'BING0123456789ABCDEF'
    $both = (Get-VerificationMeta -Root $fixture -IsRootLanding $true) -split "`n"
    Check ($both.Count -eq 2 -and $both[0] -match 'google-site-verification' -and $both[1] -match 'msvalidate\.01') 'both tokens render in a fixed order, one tag each'
    Check ((Get-VerificationMeta -Root $fixture -IsRootLanding $false) -eq '') 'both tokens render nothing on any other page'

    Write-Fixture 'tools/site/search-verification.json' '{ "google": "only-google_0123456789" }'
    Check ((Get-VerificationMeta -Root $fixture -IsRootLanding $true) -match 'only-google_0123456789') 'a file without the bing property still renders'

    foreach ($bad in @('short', 'has space 0123456789', 'quote"0123456789', 'angle<0123456789>', ('x' * 129))) {
        Set-Tokens $bad $null
        Check (Throws { Get-VerificationMeta -Root $fixture -IsRootLanding $true }) "a malformed Google token throws on the root landing ('$($bad.Substring(0, [Math]::Min(12, $bad.Length)))')"
    }
    Set-Tokens $null 'bad token with spaces'
    Check (Throws { Get-VerificationMeta -Root $fixture -IsRootLanding $false }) 'a malformed Bing token throws on every page, not only the root landing'
    Write-Fixture 'tools/site/search-verification.json' '{ "google": 1234567890123 }'
    Check (Throws { Get-VerificationMeta -Root $fixture -IsRootLanding $true }) 'a token that is not a string throws'
    Write-Fixture 'tools/site/search-verification.json' '{ not json'
    Check (Throws { Get-VerificationMeta -Root $fixture -IsRootLanding $true }) 'an unparseable verification file throws'
    Remove-Item (Join-Path $fixture 'tools/site/search-verification.json')
    Check (Throws { Get-VerificationMeta -Root $fixture -IsRootLanding $true }) 'a missing verification file throws'

    # --- sitemap
    $sitemap = New-SitemapXml -Pages $pages -Languages $languages -BaseUrl $baseUrl
    $locs = @([regex]::Matches($sitemap, '<loc>([^<]+)</loc>') | ForEach-Object { $_.Groups[1].Value })
    Check ($locs.Count -eq $languages.Count * $pages.Count) "the sitemap holds languages x pages entries (got $($locs.Count))"
    Check (-not ($sitemap -match '404')) 'the sitemap does not list the not-found page'
    Check ($locs -contains $baseUrl) 'the English landing is the base address'
    Check ($locs -contains "${baseUrl}zh/privacy.html") 'a non-English page sits under its language folder'
    Check ($locs.Count -eq @($locs | Select-Object -Unique).Count) 'no sitemap entry is listed twice'
    $xml = $null
    try { $xml = [xml] $sitemap } catch { $xml = $null }
    Check ($null -ne $xml) 'the sitemap is well-formed XML'
    Check (@([regex]::Matches($sitemap, 'rel="alternate"')).Count -eq $locs.Count * ($languages.Count + 1)) 'every entry declares every language and the x-default'
    Check ($sitemap.Contains('hreflang="zh-Hans" href="https://example.test/site/zh/"')) 'an alternate carries the language code and its folder'
    Check ($sitemap.Contains('hreflang="x-default" href="https://example.test/site/privacy.html"')) 'the x-default is the English page'
    Check ($sitemap.EndsWith("`n") -and -not $sitemap.Contains("`r")) 'the sitemap ends in one newline and holds no carriage return'

    # --- robots.txt
    $robots = New-RobotsTxt -BaseUrl $baseUrl
    Check ($robots.Contains("Sitemap: ${baseUrl}sitemap.xml")) 'robots.txt names the sitemap'
    Check ($robots.Contains('Allow: /') -and -not ($robots -match 'Disallow')) 'robots.txt allows crawling and disallows nothing'
    Check ($robots.EndsWith("`n") -and -not $robots.Contains("`r")) 'robots.txt ends in one newline and holds no carriage return'

    # --- structured data
    $deck = New-JsonLdDeck
    foreach ($language in $languages) {
        $hreflang = Get-HrefLang -Language $language
        $localePrefix = if ($language.DictionaryCode -eq 'en') { '' } else { "$($language.DictionaryCode)/" }
        foreach ($page in $pages) {
            $canonical = "${baseUrl}$($language.DictionaryCode)/$($page.File)"
            $block = Get-JsonLd -Page $page -Pages $pages -Deck $deck -Language $language -Canonical $canonical -BaseUrl $baseUrl -ScreenshotDirectory $screens
            $found = @(Get-JsonLdBlock $block)
            Check ($found.Count -eq 1) "$($language.DictionaryCode)/$($page.File): exactly one JSON-LD block"
            if ($found.Count -ne 1) { continue }
            $parsed = $null
            try { $parsed = $found[0].Groups[1].Value | ConvertFrom-Json } catch { $parsed = $null }
            Check ($null -ne $parsed) "$($language.DictionaryCode)/$($page.File): the JSON-LD parses"
            if ($null -eq $parsed) { continue }

            Check ($parsed.inLanguage -eq $hreflang) "$($language.DictionaryCode)/$($page.File): inLanguage is the page's hreflang"
            Check ($parsed.url -eq $canonical) "$($language.DictionaryCode)/$($page.File): url is the canonical address"
            Check ($null -eq $parsed.PSObject.Properties['softwareVersion']) "$($language.DictionaryCode)/$($page.File): no version string"
            Check ($null -eq $parsed.PSObject.Properties['aggregateRating'] -and $null -eq $parsed.PSObject.Properties['review']) "$($language.DictionaryCode)/$($page.File): no rating or review"
            if ($page.Name -eq 'home') {
                Check ($parsed.'@type' -eq 'SoftwareApplication') "$($language.DictionaryCode): the landing is a SoftwareApplication"
                Check ($parsed.offers.price -eq '0') "$($language.DictionaryCode): the landing is free"
                Check ($parsed.downloadUrl -eq 'https://github.com/SerZhyAle/StreamsPlayer/releases/latest') "$($language.DictionaryCode): the download address is the newest release, never a version"
                Check ($parsed.screenshot -eq "${baseUrl}assets/screens/grid-$($language.DictionaryCode).jpg") "$($language.DictionaryCode): the screenshot is the locale's own"
                Check ($parsed.license -like '*/blob/main/LICENSE' -and $parsed.author.url -eq 'https://github.com/SerZhyAle') "$($language.DictionaryCode): license and author address are named"
                Check ($parsed.name -eq 'STREAMS Player') "$($language.DictionaryCode): the application keeps its name"

                # SP-0039 4.8: the release notes and the support page of the landing's own locale, at the addresses
                # the sitemap lists for them.
                $notesProperty = $parsed.PSObject.Properties['releaseNotes']
                Check ($null -ne $notesProperty -and $notesProperty.Value -eq "${baseUrl}${localePrefix}whats-new.html") "$($language.DictionaryCode): releaseNotes is the same locale's whats-new.html"
                Check ($null -ne $notesProperty -and $notesProperty.Value.StartsWith("${baseUrl}${localePrefix}") -and $notesProperty.Value.EndsWith('whats-new.html')) "$($language.DictionaryCode): releaseNotes starts with the locale's own prefix and ends in whats-new.html"
                Check ($null -ne $notesProperty -and $locs -contains $notesProperty.Value) "$($language.DictionaryCode): releaseNotes is an address the sitemap lists"
                $helpProperty = $parsed.PSObject.Properties['softwareHelp']
                Check ($null -ne $helpProperty -and $helpProperty.Value.'@type' -eq 'CreativeWork' -and $helpProperty.Value.url -eq "${baseUrl}${localePrefix}support.html") "$($language.DictionaryCode): softwareHelp is a CreativeWork at the same locale's support.html"
                Check ($null -ne $helpProperty -and $locs -contains $helpProperty.Value.url) "$($language.DictionaryCode): softwareHelp is an address the sitemap lists"
            } else {
                Check ($parsed.'@type' -eq 'WebPage' -and $parsed.isPartOf.'@type' -eq 'WebSite') "$($language.DictionaryCode)/$($page.File): a further page is a WebPage that is part of the site"
                Check ($null -eq $parsed.PSObject.Properties['releaseNotes'] -and $null -eq $parsed.PSObject.Properties['softwareHelp']) "$($language.DictionaryCode)/$($page.File): only the landing links the release notes and the support page"
                Check ($parsed.name -eq $deck.Values["title-$($page.Name)"]) "$($language.DictionaryCode)/$($page.File): quotes, brackets and ampersands survive in the name"
                Check ($parsed.description -eq $deck.Values["description-$($page.Name)"]) "$($language.DictionaryCode)/$($page.File): a backslash and a closing script tag survive in the description"
            }
        }
    }
    Remove-Item (Join-Path $screens 'grid-ru.jpg')
    $ruLanding = $languages[1]
    Check (Throws { Get-JsonLd -Page $pages[0] -Pages $pages -Deck $deck -Language $ruLanding -Canonical $baseUrl -BaseUrl $baseUrl -ScreenshotDirectory $screens }) 'a landing whose locale has no screenshot throws'
    Check (-not (Throws { Get-JsonLd -Page $pages[1] -Pages $pages -Deck $deck -Language $ruLanding -Canonical "${baseUrl}ru/privacy.html" -BaseUrl $baseUrl -ScreenshotDirectory $screens })) 'a further page does not need a screenshot'

    # A landing is only rendered from a page list that holds the release notes and the support page, each once.
    $enLanding = $languages[0]
    $withoutNotes = @($pages | Where-Object { $_.Name -ne 'whatsnew' })
    $withoutSupport = @($pages | Where-Object { $_.Name -ne 'support' })
    $notesTwice = @($pages) + $pages[3]
    Check (ThrowsLike { Get-JsonLd -Page $pages[0] -Pages $withoutNotes -Deck $deck -Language $enLanding -Canonical $baseUrl -BaseUrl $baseUrl -ScreenshotDirectory $screens } "'whatsnew' page, which the page list does not hold exactly once") 'a landing without a release-notes page in the page list throws'
    Check (ThrowsLike { Get-JsonLd -Page $pages[0] -Pages $withoutSupport -Deck $deck -Language $enLanding -Canonical $baseUrl -BaseUrl $baseUrl -ScreenshotDirectory $screens } "'support' page, which the page list does not hold exactly once") 'a landing without a support page in the page list throws'
    Check (ThrowsLike { Get-JsonLd -Page $pages[0] -Pages $notesTwice -Deck $deck -Language $enLanding -Canonical $baseUrl -BaseUrl $baseUrl -ScreenshotDirectory $screens } "'whatsnew' page, which the page list does not hold exactly once") 'a landing with the release-notes page listed twice throws'
    Check (-not (Throws { Get-JsonLd -Page $pages[2] -Pages $withoutNotes -Deck $deck -Language $enLanding -Canonical "${baseUrl}trust.html" -BaseUrl $baseUrl -ScreenshotDirectory $screens })) 'a further page does not need the release-notes page in the list'

    # --- deck parity (SiteDecks.ps1)
    $inlineMarkup = [ordered]@{ '[[email]]' = 'mail'; '[[command]]' = 'cmd' }
    $glyphMarker = [regex] '(?:\{\{|\[\[)glyph:(?<id>[\w.\-]+)(?:\}\}|\]\])'
    function New-ParityDeck([hashtable] $Overrides = @{}) {
        $values = [ordered]@{ 'a' = 'Write to [[email]]'; 'b' = 'Press [[glyph:nav.home]] then [[glyph:nav.back]]'; 'c' = 'Plain' }
        foreach ($key in $Overrides.Keys) { if ($null -eq $Overrides[$key]) { $values.Remove($key) } else { $values[$key] = $Overrides[$key] } }
        return [pscustomobject]@{ Keys = [string[]] @($values.Keys); Values = $values }
    }
    function Get-Parity($Other) {
        $decks = [ordered]@{ en = (New-ParityDeck); de = $Other }
        return @(Get-DeckParityProblem -English $decks['en'] -Decks $decks -GlyphMarker $glyphMarker)
    }
    function Has([string[]] $Problems, [string] $Pattern) { return @($Problems | Where-Object { $_ -match $Pattern }).Count -gt 0 }
    Check (@(Get-Parity (New-ParityDeck @{ a = 'Schreiben an [[email]]'; c = 'Einfach' })).Count -eq 0) 'a deck in parity passes'
    Check (Has (Get-Parity (New-ParityDeck @{ c = $null })) '\[de\] missing key\(s\): c') 'a missing key is reported'
    Check (Has (Get-Parity (New-ParityDeck @{ d = 'Extra' })) 'key\(s\) English does not have: d') 'a key English does not have is reported'
    Check (Has (Get-Parity (New-ParityDeck @{ c = '   ' })) '\[de\] c is empty') 'an empty value is reported'
    Check (Has (Get-Parity (New-ParityDeck @{ a = 'Schreiben' })) "\[de\] a: expected placeholder\(s\) '\[\[email\]\]', found ''") 'a lost placeholder is reported'
    Check (Has (Get-Parity (New-ParityDeck @{ b = 'Press [[glyph:nav.back]] then [[glyph:nav.home]]' })) '\[de\] b: expected glyph\(s\)') 'glyphs in another order are reported'

    # --- generated pages, part 1: the gate can fail. A small site is made with the real emitters (Get-JsonLd,
    # New-SitemapXml) and a footer in the generator's shape, then copied and broken one way at a time.
    function New-FixtureSite([string] $Directory) {
        $screenshots = Join-Path $Directory 'assets/screens'
        New-Item -ItemType Directory -Force -Path $screenshots | Out-Null
        foreach ($language in $languages) { [System.IO.File]::WriteAllText((Join-Path $screenshots "grid-$($language.DictionaryCode).jpg"), 'jpeg', $utf8) }
        $siteDeck = New-JsonLdDeck
        foreach ($language in $languages) {
            $folder = if ($language.DictionaryCode -eq 'en') { '' } else { "$($language.DictionaryCode)/" }
            foreach ($page in $pages) {
                $canonical = Get-LocalePageUrl -Page $page -Language $language -BaseUrl $baseUrl
                $ld = Get-JsonLd -Page $page -Pages $pages -Deck $siteDeck -Language $language -Canonical $canonical -BaseUrl $baseUrl -ScreenshotDirectory $screenshots
                # The canonical link in the head names the page's own address, as the generator's head does: it is what a
                # naive "any href" check would count in place of a removed footer link.
                $html = @(
                    '<!doctype html>', "<html lang=`"$($language.DictionaryCode)`">", '  <head>',
                    "    <link rel=`"canonical`" href=`"$canonical`">", $ld, '  </head>', '  <body>',
                    "    <main><h1>$($page.Name)</h1></main>", '    <footer class="site-footer">', '      <span class="footer-links">',
                    '        <a href="privacy.html">Privacy</a>', '        <a href="whats-new.html">New</a>', '        <a href="support.html">Support</a>',
                    '      </span>', '    </footer>', '  </body>', '</html>', ''
                ) -join "`n"
                Write-FixtureAt $Directory "$folder$($page.File)" $html
            }
        }
        # The not-found page: every address absolute, no structured data, as SiteNotFound.ps1 builds it.
        $notFound = @(
            '<!doctype html>', '<html lang="en">', '  <head><meta name="robots" content="noindex"></head>', '  <body>', '    <main><h1>404</h1></main>',
            '    <footer class="site-footer">', '      <span class="footer-links">',
            "        <a href=`"${baseUrl}privacy.html`">Privacy</a>", "        <a href=`"${baseUrl}whats-new.html`">New</a>", "        <a href=`"${baseUrl}support.html`">Support</a>",
            '      </span>', '    </footer>', '  </body>', '</html>', ''
        ) -join "`n"
        Write-FixtureAt $Directory '404.html' $notFound
        Write-FixtureAt $Directory 'sitemap.xml' (New-SitemapXml -Pages $pages -Languages $languages -BaseUrl $baseUrl)
    }
    function Write-FixtureAt([string] $Directory, [string] $Relative, [string] $Content) {
        $full = Join-Path $Directory $Relative
        New-Item -ItemType Directory -Force -Path (Split-Path $full -Parent) | Out-Null
        [System.IO.File]::WriteAllText($full, $Content, $utf8)
    }
    $siteCodes = @($languages | ForEach-Object { $_.DictionaryCode })
    $siteFiles = @($pages | ForEach-Object { $_.File })
    function Get-SiteProblems([string] $Directory) {
        # The leading comma keeps an empty result an empty array instead of nothing, which StrictMode cannot count.
        return , @(Get-GeneratedSiteProblem -DocsDirectory $Directory -LanguageCode $siteCodes -PageFile $siteFiles -BaseUrl $baseUrl)
    }
    $cleanSite = Join-Path $fixture 'site-clean'
    New-FixtureSite $cleanSite
    $mutantCount = 0
    function New-Mutant([string] $Relative, [scriptblock] $Edit) {
        # A copy of the clean site with one file changed (or, with no edit, one file removed). The tracked docs/ is never
        # a mutant's source.
        $script:mutantCount++
        $copy = Join-Path $fixture "mutant$script:mutantCount"
        Copy-Item -LiteralPath $cleanSite -Destination $copy -Recurse
        $path = Join-Path $copy $Relative
        if ($Edit) { [System.IO.File]::WriteAllText($path, (& $Edit ([System.IO.File]::ReadAllText($path))), $utf8) } else { Remove-Item -LiteralPath $path }
        return $copy
    }
    function Has-Problem([string[]] $Problems, [string] $Pattern) { return @($Problems | Where-Object { $_ -match $Pattern }).Count -gt 0 }

    $clean = Get-SiteProblems $cleanSite
    Check ($clean.Count -eq 0) "a site made with the real emitters passes the generated-pages gate (got: $($clean -join '; '))"

    $found = Get-SiteProblems (New-Mutant 'ru/trust.html' { param($t) $t.Replace('<a href="whats-new.html">New</a>', '') })
    Check ($found.Count -eq 1 -and (Has-Problem $found '^ru/trust\.html: the footer has no link ending whats-new\.html$')) 'removing the What''s new footer link from one page fails, naming that page and nothing else'
    $found = Get-SiteProblems (New-Mutant 'zh/whats-new.html' { param($t) $t.Replace('<a href="whats-new.html">New</a>', '') })
    Check (Has-Problem $found '^zh/whats-new\.html: the footer has no link ending whats-new\.html') 'a canonical link in the head does not stand in for the footer link on the What''s new page itself'
    $found = Get-SiteProblems (New-Mutant 'support.html' { param($t) $t.Replace('<a href="support.html">Support</a>', '') })
    Check (Has-Problem $found '^support\.html: the footer has no link ending support\.html') 'removing the Support footer link from the Support page itself fails'
    $found = Get-SiteProblems (New-Mutant '404.html' { param($t) $t.Replace("<a href=`"${baseUrl}support.html`">Support</a>", '') })
    Check (Has-Problem $found '^404\.html: the footer has no link ending support\.html') 'removing the Support footer link from the not-found page fails'
    $found = Get-SiteProblems (New-Mutant 'ru/privacy.html' { param($t) $t.Replace('<a href="whats-new.html">', '<a href="../whats-new.html">') })
    Check (Has-Problem $found '^ru/privacy\.html: the footer''s whats-new\.html link does not reach ru/whats-new\.html$') 'a footer link into another locale fails'
    $found = Get-SiteProblems (New-Mutant 'zh/trust.html' { param($t) $t.Replace('<a href="support.html">', '<a href="support.html#top">') })
    Check (Has-Problem $found '^zh/trust\.html: the footer has no link ending support\.html') 'a link whose address does not end in the page name does not count'
    $found = Get-SiteProblems (New-Mutant 'ru/support.html' $null)
    Check (Has-Problem $found '^ru/support\.html: the page does not exist') 'a missing page fails'
    Check (Has-Problem $found 'the footer''s support\.html link reaches a file that does not exist') 'a footer link to a page that is gone fails on the pages that link it'

    $found = Get-SiteProblems (New-Mutant 'zh/trust.html' { param($t) [regex]::Replace($t, '(?s)\s*<script type="application/ld\+json">.*?</script>', '') })
    Check (Has-Problem $found '^zh/trust\.html: 0 JSON-LD blocks') 'a page without structured data fails'
    $found = Get-SiteProblems (New-Mutant 'ru/index.html' { param($t) $t.Replace('</head>', '<script type="application/ld+json">{}</script></head>') })
    Check (Has-Problem $found '^ru/index\.html: 2 JSON-LD blocks') 'a page with two structured-data blocks fails'
    $found = Get-SiteProblems (New-Mutant 'zh/privacy.html' { param($t) $t.Replace('{"@context"', '{@context') })
    Check (Has-Problem $found '^zh/privacy\.html: the JSON-LD block does not parse') 'a structured-data block that does not parse fails'
    $found = Get-SiteProblems (New-Mutant '404.html' { param($t) $t.Replace('</head>', '<script type="application/ld+json">{}</script></head>') })
    Check (Has-Problem $found '^404\.html: the not-found page carries 1 JSON-LD') 'structured data on the not-found page fails'
    $found = Get-SiteProblems (New-Mutant 'ru/index.html' { param($t) $t.Replace('"releaseNotes":"https://example.test/site/ru/whats-new.html"', '"releaseNotes":"https://example.test/site/whats-new.html"') })
    Check (Has-Problem $found '^ru/index\.html: releaseNotes is not this locale''s whats-new\.html') 'a landing that names another locale''s release notes fails'

    $found = Get-SiteProblems (New-Mutant 'sitemap.xml' { param($t) $t.Replace("<loc>${baseUrl}zh/trust.html</loc>", '') })
    Check ((Has-Problem $found '^sitemap\.xml: 14 <loc> entries, languages x pages is 3 x 5 = 15') -and (Has-Problem $found 'missing, first: .*zh/trust\.html')) 'a sitemap short of one page fails, with the count and the address'
    $found = Get-SiteProblems (New-Mutant 'sitemap.xml' { param($t) $t.Replace('</urlset>', "<url><loc>${baseUrl}404.html</loc></url></urlset>") })
    Check (Has-Problem $found '^sitemap\.xml: lists the not-found page') 'a sitemap that lists the not-found page fails'
    $found = Get-SiteProblems (New-Mutant 'sitemap.xml' { param($t) $t.Replace('</urlset>', "<url><loc>${baseUrl}ru/trust.html</loc></url></urlset>") })
    Check (Has-Problem $found '^sitemap\.xml: an address is listed twice') 'a sitemap that lists an address twice fails'

    # --- generated pages, part 2: the repository's own docs/ (or the copy -DocsDirectory names).
    $repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $docs = if ($DocsDirectory) { (Resolve-Path -LiteralPath $DocsDirectory).Path } else { Join-Path $repositoryRoot 'docs' }
    $generator = Read-GeneratorFact -Path (Join-Path $PSScriptRoot 'build-site.ps1')
    $deckCodes = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'copy') -Filter '*.txt' -File | ForEach-Object { $_.BaseName } | Sort-Object)
    $generatorFiles = @($generator.Pages | ForEach-Object { $_.File })
    Check ($deckCodes -contains 'en' -and $deckCodes.Count -gt 1) 'the copy decks name the English root and at least one other language'
    Check (($generatorFiles -contains 'whats-new.html') -and ($generatorFiles -contains 'support.html')) 'the generator''s page table holds the What''s new and the Support pages'
    $realProblems = @(Get-GeneratedSiteProblem -DocsDirectory $docs -LanguageCode $deckCodes -PageFile $generatorFiles -BaseUrl $generator.BaseUrl)
    Write-Output "generated pages: $($deckCodes.Count) languages x $($generatorFiles.Count) pages + 404.html judged under $docs, $($realProblems.Count) problem(s)"
    if ($realProblems.Count) {
        foreach ($problem in $realProblems) { Check $false "generated pages: $problem" }
    } else {
        Check $true 'the generated pages hold'
    }
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) {
    Write-Output "site-discovery: FAIL ($($failures.Count) of $($passed + $failures.Count))"
    exit 1
}
Write-Output "site-discovery: PASS ($passed checks)"
exit 0
