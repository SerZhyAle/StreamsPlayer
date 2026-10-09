<#
    SP-0039 (plan phase 2): everything the site emits so that a search engine can find and understand it -
    structured data, hreflang, the sitemap, robots.txt and the search-console verification tags - split out of
    build-site.ps1. Every function is pure: it returns the text and writes nothing, so tools/site/Test-SiteDiscovery.ps1
    can run it against a fixture.

    Not emitted, on purpose (PAGE-CONTENT: no claim the page cannot demonstrate; SITE-EXPERIENCE rule 13: no page
    names a version it did not render from the landing's source):
      softwareVersion                    the landing resolves its release at run time
      aggregateRating / ratingValue      the product has no rating it can demonstrate

    Dot-source it:
        . "$PSScriptRoot/SiteDiscovery.ps1"
        $json = Get-JsonLd -Page $page -Pages $pages -Deck $deck -Language $language -Canonical $canonical -BaseUrl $BaseUrl -ScreenshotDirectory $dir
        $sitemap = New-SitemapXml -Pages $pages -Languages $languages -BaseUrl $BaseUrl
#>

function Get-LocalePageUrl {
    # The canonical address of one page in one language: the base address, the language folder (none for the
    # English root) and the file (none for a landing). The sitemap and the structured data both come through here, so
    # a page the structured data points at is the page the sitemap lists.
    param(
        [Parameter(Mandatory)] $Page,
        [Parameter(Mandatory)] [pscustomobject] $Language,
        [Parameter(Mandatory)] [string] $BaseUrl
    )

    $prefix = if ($Language.DictionaryCode -eq 'en') { '' } else { "$($Language.DictionaryCode)/" }
    $leaf = if ($Page.File -eq 'index.html') { '' } else { $Page.File }
    return "$BaseUrl$prefix$leaf"
}

function Get-HrefLang {
    param([Parameter(Mandatory)] [pscustomobject] $Language)

    # The URL code plus a script subtag when the culture carries one (zh-Hans), and never a region:
    # hreflang="de" reaches German everywhere, hreflang="de-DE" only Germany. No per-language literal.
    $parts = $Language.CultureCode.Split('-')
    if ($parts.Length -gt 1 -and $parts[1].Length -eq 4) {
        return "$($Language.DictionaryCode)-$($parts[1])"
    }
    return $Language.DictionaryCode
}

function Get-JsonLd {
    # Structured data for the page, emitted as a complete <script type="application/ld+json"> block.
    #
    # Built here rather than in the template on purpose: an HTML parser does NOT decode entities
    # inside a ld+json script element, so reusing the HTML-escaped {{page.title}} there would put a
    # literal &quot; into the JSON and break it. These values are therefore taken raw from the copy
    # deck and escaped by ConvertTo-Json, which is the correct escaping for this context.
    #
    # One block per page: the landing of each locale is the SoftwareApplication, every other page a WebPage that is
    # part of the site. Repeating the application on every page would declare it once per page and locale.
    param(
        [Parameter(Mandatory)] $Page,
        # Every page the generator owns (Name and File): the landing names the release notes and the support page
        # of its own locale, and takes their addresses from here rather than from a second list of file names.
        [Parameter(Mandatory)] $Pages,
        [Parameter(Mandatory)] $Deck,
        # The language object of the registry: its hreflang is the block's inLanguage, its dictionary code names
        # the per-locale screenshot.
        [Parameter(Mandatory)] [pscustomobject] $Language,
        [Parameter(Mandatory)] [string] $Canonical,
        [Parameter(Mandatory)] [string] $BaseUrl,
        # docs/assets/screens, where tools/site/export-site-screenshots.ps1 writes grid-<dictionary-code>.jpg.
        [Parameter(Mandatory)] [string] $ScreenshotDirectory
    )

    $name = $Deck.Values["title-$($Page.Name)"]
    $description = $Deck.Values["description-$($Page.Name)"]
    $inLanguage = Get-HrefLang -Language $Language

    if ($Page.Name -eq 'home') {
        $screenshotFile = "grid-$($Language.DictionaryCode).jpg"
        if (-not (Test-Path -LiteralPath (Join-Path $ScreenshotDirectory $screenshotFile) -PathType Leaf)) {
            throw "The structured data names assets/screens/$screenshotFile, which does not exist (tools/site/export-site-screenshots.ps1 writes it)."
        }
        $repository = 'https://github.com/SerZhyAle/StreamsPlayer'

        # The same locale's release notes and support page. A missing page throws: a landing that quietly stops
        # naming them would be a regression nothing else reports.
        # The loop variable is not $name: that is the page title above, and a foreach assignment would overwrite it.
        $related = @{}
        foreach ($relatedName in 'whatsnew', 'support') {
            $target = @($Pages | Where-Object { $_.Name -eq $relatedName })
            if ($target.Count -ne 1) { throw "The structured data links the '$relatedName' page, which the page list does not hold exactly once." }
            $related[$relatedName] = Get-LocalePageUrl -Page $target[0] -Language $Language -BaseUrl $BaseUrl
        }

        $data = [ordered]@{
            '@context'            = 'https://schema.org'
            '@type'               = 'SoftwareApplication'
            'name'                = 'STREAMS Player'
            'description'         = $description
            'url'                 = $Canonical
            'inLanguage'          = $inLanguage
            'image'               = "${BaseUrl}assets/og-card.png"
            'screenshot'          = "${BaseUrl}assets/screens/$screenshotFile"
            'applicationCategory' = 'MultimediaApplication'
            'operatingSystem'     = 'Windows'
            # A durable address - the newest release, whichever it is - never a versioned asset.
            'downloadUrl'         = "$repository/releases/latest"
            'releaseNotes'        = $related['whatsnew']
            'softwareHelp'        = [ordered]@{ '@type' = 'CreativeWork'; 'url' = $related['support'] }
            'isAccessibleForFree' = $true
            'license'             = "$repository/blob/main/LICENSE"
            'author'              = [ordered]@{ '@type' = 'Person'; 'name' = 'Serhii Zhyhunenko'; 'url' = 'https://github.com/SerZhyAle' }
            'offers'              = [ordered]@{ '@type' = 'Offer'; 'price' = '0'; 'priceCurrency' = 'USD' }
        }
    } else {
        $data = [ordered]@{
            '@context'    = 'https://schema.org'
            '@type'       = 'WebPage'
            'name'        = $name
            'description' = $description
            'url'         = $Canonical
            'inLanguage'  = $inLanguage
            'isPartOf'    = [ordered]@{ '@type' = 'WebSite'; 'name' = 'STREAMS Player'; 'url' = $BaseUrl }
        }
    }

    # A '<' in a value (a closing script tag, a comment opener) would end the element early and break the page;
    # the JSON escape for it (backslash, u, 003c) is the same character to a JSON parser and inert to the HTML one.
    $json = ($data | ConvertTo-Json -Depth 5 -Compress).Replace('<', '\' + 'u003c')
    return '    <script type="application/ld+json">{0}</script>' -f $json
}

function Get-VerificationMeta {
    # The <meta> elements a search console reads from the page <head> to confirm the owner (Google Search Console
    # and Bing Webmaster Tools). The tokens live in tools/site/search-verification.json; both null means nothing is
    # rendered. Only the English root landing - the home page of the URL-prefix property - carries them, every other
    # page gets an empty string. A token that is not a plausible verification value throws on every page, so a
    # paste error fails the build instead of shipping a tag no console accepts.
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [bool] $IsRootLanding
    )

    $path = Join-Path $Root 'tools/site/search-verification.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'tools/site/search-verification.json does not exist - it holds the search-console verification tokens (null until a console issues one).'
    }
    try { $config = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json }
    catch { throw "tools/site/search-verification.json is not valid JSON: $($_.Exception.Message)" }

    $tags = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in @(@{ Key = 'google'; Name = 'google-site-verification' }, @{ Key = 'bing'; Name = 'msvalidate.01' })) {
        # StrictMode is on in the caller, so an absent property is read through PSObject, not by name.
        $property = $config.PSObject.Properties[$entry.Key]
        $token = if ($property) { $property.Value } else { $null }
        if ($null -eq $token) { continue }
        if ($token -isnot [string] -or $token -notmatch '^[A-Za-z0-9_-]{10,128}$') {
            throw "tools/site/search-verification.json: '$($entry.Key)' is not a verification token (letters, digits, '-' and '_', 10 to 128 characters)."
        }
        $tags.Add(('    <meta name="{0}" content="{1}">' -f $entry.Name, $token))
    }

    if (-not $IsRootLanding) { return '' }
    return $tags -join "`n"
}

function New-SitemapXml {
    # sitemap.xml, from the same language x page product the pages themselves come from, so a fourteenth language
    # or a further page lands in it without an edit - the failure mode a hand-maintained sitemap always ends in is
    # a URL list that silently stops matching the site. The not-found page is not in $Pages and so not here.
    param(
        [Parameter(Mandatory)] $Pages,
        [Parameter(Mandatory)] $Languages,
        [Parameter(Mandatory)] [string] $BaseUrl
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<?xml version="1.0" encoding="UTF-8"?>')
    $lines.Add('<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">')
    foreach ($page in $Pages) {
        foreach ($language in $Languages) {
            $leaf = if ($page.File -eq 'index.html') { '' } else { $page.File }
            $url = Get-LocalePageUrl -Page $page -Language $language -BaseUrl $BaseUrl

            $lines.Add('  <url>')
            $lines.Add("    <loc>$url</loc>")
            # Every alternate is declared on every entry, which is what makes a multilingual sitemap
            # usable: a crawler that finds one URL learns the whole set.
            foreach ($other in $Languages) {
                $otherPrefix = if ($other.DictionaryCode -eq 'en') { '' } else { "$($other.DictionaryCode)/" }
                $otherHref = Get-HrefLang -Language $other
                # Built into a variable first: inside a method-call argument list, -f binds tighter than
                # the comma, so an inline '{0}..{3}' -f a, b, c, d would hand the format only its first
                # argument and throw.
                $alternateLine = '    <xhtml:link rel="alternate" hreflang="{0}" href="{1}{2}{3}"/>' -f $otherHref, $BaseUrl, $otherPrefix, $leaf
                $lines.Add($alternateLine)
            }
            $defaultLine = '    <xhtml:link rel="alternate" hreflang="x-default" href="{0}{1}"/>' -f $BaseUrl, $leaf
            $lines.Add($defaultLine)
            $lines.Add('    <changefreq>monthly</changefreq>')
            $lines.Add('  </url>')
        }
    }
    $lines.Add('</urlset>')
    return ($lines -join "`n") + "`n"
}

function New-RobotsTxt {
    # Allows everything and names the sitemap. Crawlers read robots.txt only at the host root (RFC 9309), so the
    # copy under the project path reaches nobody; the sitemap reaches a search engine by console submission.
    param([Parameter(Mandatory)] [string] $BaseUrl)

    $robots = @(
        '# STREAMS Player - https://github.com/SerZhyAle/StreamsPlayer'
        '# Generated by tools/site/build-site.ps1 - do not hand-edit.'
        'User-agent: *'
        'Allow: /'
        ''
        "Sitemap: ${BaseUrl}sitemap.xml"
    ) -join "`n"
    return $robots + "`n"
}
