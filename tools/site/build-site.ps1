<#
    SP-0034: renders the GitHub Pages site in every shipped interface language.

    Input : tools/site/templates/*.html + site.js, and one copy deck per language in
            tools/site/copy/<dictionary-code>.txt.
    Output: docs/index.html and docs/privacy.html (English - the canonical root), plus
            docs/<code>/index.html and docs/<code>/privacy.html for every other language, and
            docs/site.js.

    Why static pages rather than the previous client-side swap: hreflang needs one URL per language.
    GitHub Pages deploys docs/ verbatim with no build step, so the generated files are committed
    output - re-run this script and commit whatever it changes.

    The language list is never written here. It comes from StreamsPlayer.Core's InterfaceLanguages
    registry via tools/InterfaceLanguages.ps1, so a fourteenth language needs no edit to this file.

    Usage:
      pwsh -NoProfile -File tools/site/build-site.ps1
      pwsh -NoProfile -File tools/site/build-site.ps1 -Check   # fail if docs/ is stale, write nothing
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

$root = Get-RepositoryRoot
$copyDirectory = Join-Path $root 'tools/site/copy'
$templateDirectory = Join-Path $root 'tools/site/templates'
$outputDirectory = Join-Path $root 'docs'

# The pages the generator owns, by template name and output file name.
$pages = @(
    [pscustomobject]@{ Name = 'home';    Template = 'index.html';   File = 'index.html' }
    [pscustomobject]@{ Name = 'privacy'; Template = 'privacy.html'; File = 'privacy.html' }
)

# Languages whose copy the owner wrote himself. Everything else is machine-produced and says so on
# the page. A new language is machine-translated until someone puts it in this list - the safe
# direction for an honesty notice.
$humanAuthored = @('en', 'ru', 'uk')

# Inline replacements a copy deck may carry. They keep a command, a path and an address out of the
# translated prose and give each one a left-to-right island, which is what makes the Arabic and Urdu
# pages readable.
$inlineMarkup = [ordered]@{
    '[[command]]' = '<code dir="ltr">winget install SerZhyAle.StreamsPlayer</code>'
    '[[appdata]]' = '<code dir="ltr">%LOCALAPPDATA%\StreamsPlayer</code>'
    '[[email]]'   = '<span dir="ltr">serzhyale@gmail.com</span>'
}

function ConvertTo-HtmlText {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

    # Escapes for both text nodes and double-quoted attributes, so one function covers every
    # substitution site in the templates.
    $escaped = $Value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
    foreach ($entry in $inlineMarkup.GetEnumerator()) {
        $escaped = $escaped.Replace($entry.Key, $entry.Value)
    }
    return $escaped
}

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

function Get-JsonLd {
    # Structured data for the page, emitted as a complete <script type="application/ld+json"> block.
    #
    # Built here rather than in the template on purpose: an HTML parser does NOT decode entities
    # inside a ld+json script element, so reusing the HTML-escaped {{page.title}} there would put a
    # literal &quot; into the JSON and break it. These values are therefore taken raw from the copy
    # deck and escaped by ConvertTo-Json, which is the correct escaping for this context.
    param(
        [Parameter(Mandatory)] $Page,
        [Parameter(Mandatory)] $Deck,
        [Parameter(Mandatory)] [string] $Canonical,
        [Parameter(Mandatory)] [string] $BaseUrl
    )

    $name = $Deck.Values["title-$($Page.Name)"]
    $description = $Deck.Values["description-$($Page.Name)"]

    if ($Page.Name -eq 'home') {
        $data = [ordered]@{
            '@context'            = 'https://schema.org'
            '@type'               = 'SoftwareApplication'
            'name'                = 'STREAMS Player'
            'description'         = $description
            'url'                 = $Canonical
            'image'               = "${BaseUrl}assets/og-card.png"
            'applicationCategory' = 'MultimediaApplication'
            'operatingSystem'     = 'Windows'
            'isAccessibleForFree' = $true
            'author'              = [ordered]@{ '@type' = 'Person'; 'name' = 'Serhii Zhyhunenko' }
            'offers'              = [ordered]@{ '@type' = 'Offer'; 'price' = '0'; 'priceCurrency' = 'USD' }
        }
    } else {
        $data = [ordered]@{
            '@context'    = 'https://schema.org'
            '@type'       = 'WebPage'
            'name'        = $name
            'description' = $description
            'url'         = $Canonical
            'isPartOf'    = [ordered]@{ '@type' = 'WebSite'; 'name' = 'STREAMS Player'; 'url' = $BaseUrl }
        }
    }

    $json = $data | ConvertTo-Json -Depth 5 -Compress
    return '    <script type="application/ld+json">{0}</script>' -f $json
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
foreach ($code in $decks.Keys) {
    if ($code -eq 'en') { continue }
    $deck = $decks[$code]

    $missing = @($english.Keys | Where-Object { -not $deck.Values.Contains($_) })
    $extra = @($deck.Keys | Where-Object { -not $english.Values.Contains($_) })
    if ($missing.Count) { $problems.Add("[$code] missing key(s): $($missing -join ', ')") }
    if ($extra.Count) { $problems.Add("[$code] key(s) English does not have: $($extra -join ', ')") }

    foreach ($key in $english.Keys) {
        if (-not $deck.Values.Contains($key)) { continue }
        if ([string]::IsNullOrWhiteSpace($deck.Values[$key])) {
            $problems.Add("[$code] $key is empty")
            continue
        }
        $expected = Get-Placeholder -Value $english.Values[$key]
        $actual = Get-Placeholder -Value $deck.Values[$key]
        if (($expected -join ',') -ne ($actual -join ',')) {
            $problems.Add("[$code] ${key}: expected placeholder(s) '$($expected -join ',')', found '$($actual -join ',')'")
        }
    }
}

if ($problems.Count) {
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "The site copy decks are not in parity with English ($($problems.Count) problem(s))."
}

Write-Host ("Copy decks: {0} languages x {1} keys" -f $decks.Count, $english.Keys.Count) -ForegroundColor Green

# --------------------------------------------------------------------------------------- rendering

$templates = @{}
foreach ($name in 'head', 'switcher', 'footer') {
    $templates[$name] = [System.IO.File]::ReadAllText((Join-Path $templateDirectory "_$name.html")).Replace("`r`n", "`n").TrimEnd("`n")
}

$written = [System.Collections.Generic.List[string]]::new()
$stale = [System.Collections.Generic.List[string]]::new()

function Save-Generated {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Content
    )

    # Every generated file lands either directly in docs/ or in docs/<code>/. docs/agent,
    # docs/assets, docs/localization and docs/specifications are hand-written and must never be
    # touched, so the target is checked rather than trusted.
    $relative = [System.IO.Path]::GetRelativePath($outputDirectory, $Path).Replace('\', '/')
    $depth = $relative.Split('/').Length
    if ($depth -gt 2 -or ($depth -eq 2 -and -not ($languages.DictionaryCode -contains $relative.Split('/')[0]))) {
        throw "Refusing to write outside the generated area: docs/$relative"
    }

    $existing = if (Test-Path -LiteralPath $Path) { [System.IO.File]::ReadAllText($Path) } else { $null }
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
        $template = [System.IO.File]::ReadAllText((Join-Path $templateDirectory $page.Template)).Replace("`r`n", "`n")

        foreach ($name in 'head', 'switcher', 'footer') {
            $template = $template.Replace("{{include:$name}}", $templates[$name])
        }

        $alternates = foreach ($other in $languages) {
            $otherUrl = if ($other.DictionaryCode -eq 'en') { '' } else { "$($other.DictionaryCode)/" }
            $leaf = if ($page.File -eq 'index.html') { '' } else { $page.File }
            '    <link rel="alternate" hreflang="{0}" href="{1}{2}{3}">' -f (Get-HrefLang -Language $other), $BaseUrl, $otherUrl, $leaf
        }
        $leafForDefault = if ($page.File -eq 'index.html') { '' } else { $page.File }
        $alternates += '    <link rel="alternate" hreflang="x-default" href="{0}{1}">' -f $BaseUrl, $leafForDefault

        $languageUrls = [ordered]@{}
        $options = [System.Collections.Generic.List[string]]::new()
        $links = [System.Collections.Generic.List[string]]::new()
        foreach ($other in $languages) {
            $url = Get-RelativeUrl -FromCode $urlCode -ToCode ($(if ($other.DictionaryCode -eq 'en') { '' } else { $other.DictionaryCode })) -File $page.File
            $languageUrls[$other.DictionaryCode] = $url
            $selected = if ($other.DictionaryCode -eq $code) { ' selected' } else { '' }
            $options.Add(('          <option value="{0}" data-code="{1}" lang="{2}"{3}>{4}</option>' -f
                (ConvertTo-HtmlText $url), $other.DictionaryCode, (Get-HrefLang -Language $other), $selected, (ConvertTo-HtmlText $other.Endonym)))
            $links.Add(('            <li><a href="{0}" lang="{1}">{2}</a></li>' -f
                (ConvertTo-HtmlText $url), (Get-HrefLang -Language $other), (ConvertTo-HtmlText $other.Endonym)))
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
            '{{page.canonical}}'      = $canonical
            '{{page.alternates}}'     = $alternates -join "`n"
            '{{page.languageUrls}}'   = ($languageUrls | ConvertTo-Json -Compress)
            '{{page.languageOptions}}' = $options -join "`n"
            '{{page.languageLinks}}'  = $links -join "`n"
            '{{page.machineNote}}'    = $machineNote
            '{{page.title}}'          = ConvertTo-HtmlText $deck.Values["title-$($page.Name)"]
            '{{page.description}}'    = ConvertTo-HtmlText $deck.Values["description-$($page.Name)"]
            '{{page.ogImage}}'        = "${BaseUrl}assets/og-card.png"
            '{{page.jsonLd}}'         = Get-JsonLd -Page $page -Deck $deck -Canonical $canonical -BaseUrl $BaseUrl
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

Save-Generated -Path (Join-Path $outputDirectory 'site.js') `
    -Content ([System.IO.File]::ReadAllText((Join-Path $templateDirectory 'site.js')).Replace("`r`n", "`n"))

# robots.txt and sitemap.xml. Both are generated from the same language x page product the pages
# themselves come from, so a fourteenth language or a third page lands in them without an edit here
# - the failure mode a hand-maintained sitemap always ends in is a URL list that silently stops
# matching the site.
$sitemapLines = [System.Collections.Generic.List[string]]::new()
$sitemapLines.Add('<?xml version="1.0" encoding="UTF-8"?>')
$sitemapLines.Add('<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">')
foreach ($page in $pages) {
    foreach ($language in $languages) {
        $code = $language.DictionaryCode
        $prefix = if ($code -eq 'en') { '' } else { "$code/" }
        $leaf = if ($page.File -eq 'index.html') { '' } else { $page.File }
        $url = "$BaseUrl$prefix$leaf"

        $sitemapLines.Add('  <url>')
        $sitemapLines.Add("    <loc>$url</loc>")
        # Every alternate is declared on every entry, which is what makes a multilingual sitemap
        # usable: a crawler that finds one URL learns the whole set.
        foreach ($other in $languages) {
            $otherPrefix = if ($other.DictionaryCode -eq 'en') { '' } else { "$($other.DictionaryCode)/" }
            $otherHref = Get-HrefLang -Language $other
            # Built into a variable first: inside a method-call argument list, -f binds tighter than
            # the comma, so an inline '{0}..{3}' -f a, b, c, d would hand the format only its first
            # argument and throw.
            $alternateLine = '    <xhtml:link rel="alternate" hreflang="{0}" href="{1}{2}{3}"/>' -f $otherHref, $BaseUrl, $otherPrefix, $leaf
            $sitemapLines.Add($alternateLine)
        }
        $defaultLine = '    <xhtml:link rel="alternate" hreflang="x-default" href="{0}{1}"/>' -f $BaseUrl, $leaf
        $sitemapLines.Add($defaultLine)
        $sitemapLines.Add("    <changefreq>monthly</changefreq>")
        $sitemapLines.Add('  </url>')
    }
}
$sitemapLines.Add('</urlset>')
Save-Generated -Path (Join-Path $outputDirectory 'sitemap.xml') -Content (($sitemapLines -join "`n") + "`n")

$robots = @(
    '# STREAMS Player - https://github.com/SerZhyAle/StreamsPlayer'
    '# Generated by tools/site/build-site.ps1 - do not hand-edit.'
    'User-agent: *'
    'Allow: /'
    ''
    "Sitemap: ${BaseUrl}sitemap.xml"
) -join "`n"
Save-Generated -Path (Join-Path $outputDirectory 'robots.txt') -Content ($robots + "`n")

# A language dropped from the registry leaves its folder behind. Only two-letter folders are
# considered, which is why docs/agent, docs/assets, docs/localization and docs/specifications cannot
# be caught by this, and only the two files this generator writes may be present.
foreach ($directory in Get-ChildItem -LiteralPath $outputDirectory -Directory) {
    if ($directory.Name -notmatch '^[a-z]{2}$') { continue }
    if ($languages.DictionaryCode -contains $directory.Name) { continue }
    $contents = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File | ForEach-Object { $_.Name })
    if (@($contents | Where-Object { $_ -notin @('index.html', 'privacy.html') }).Count) {
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
    if ($stale.Count) {
        Write-Host "Stale generated files:" -ForegroundColor Red
        $stale | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "docs/ does not match the generator ($($stale.Count) file(s)). Run tools/site/build-site.ps1 and commit the result."
    }
    Write-Host "docs/ is up to date." -ForegroundColor Green
    return
}

Write-Host ("Pages: {0} languages x {1} = {2} files, plus site.js" -f $languages.Count, $pages.Count, ($languages.Count * $pages.Count)) -ForegroundColor Green
if ($written.Count) {
    Write-Host "Changed:" -ForegroundColor Cyan
    $written | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host "No file changed - output was already current." -ForegroundColor Cyan
}
