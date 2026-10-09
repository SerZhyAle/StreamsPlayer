<#
    SP-0194 (SITE-STRUCTURE rules 2 and 15): the not-found page, split out of build-site.ps1 (SP-0039, plan phase 2).

    GitHub Pages serves docs/404.html for any address nothing answers, at any depth, so a relative link on it
    would break one folder down - every address here is built from the base URL. A root 404 cannot know the
    visitor's locale, so it carries the core three (en, ru, uk), one block each, from its own small decks
    (tools/site/copy/notfound/<code>.txt). Search and a portal home do not exist at this tier; the landing is the
    way back. noindex, and absent from the sitemap.

    The function reads Read-CopyDeck (SiteDecks.ps1), ConvertTo-HtmlText, Expand-Glyphs and Get-DocsUrl from the
    caller's scope at call time, as the generator defines them, and returns the page text; the caller saves it.

    Dot-source it:
        . "$PSScriptRoot/SiteNotFound.ps1"
        $page = New-NotFoundPage -CopyDirectory $copyDirectory -TemplateDirectory $templateDirectory -Templates $templates -English $english -BaseUrl $BaseUrl
#>

function New-NotFoundPage {
    param(
        [Parameter(Mandatory)] [string] $CopyDirectory,
        [Parameter(Mandatory)] [string] $TemplateDirectory,
        # The shared chrome, by name (head, switcher, languagerow, footer); only the footer is used here.
        [Parameter(Mandatory)] $Templates,
        # The English landing deck: the shared footer and navigation keys come from it.
        [Parameter(Mandatory)] $English,
        [Parameter(Mandatory)] [string] $BaseUrl
    )

    $notFoundCodes = @('en', 'ru', 'uk')
    $notFoundDecks = [ordered]@{}
    foreach ($code in $notFoundCodes) {
        $path = Join-Path $CopyDirectory "notfound/$code.txt"
        if (-not (Test-Path -LiteralPath $path)) { throw "tools/site/copy/notfound/$code.txt is missing." }
        $notFoundDecks[$code] = Read-CopyDeck -Path $path
    }
    foreach ($code in $notFoundCodes) {
        $missing = @($notFoundDecks['en'].Keys | Where-Object { -not $notFoundDecks[$code].Values.Contains($_) -or [string]::IsNullOrWhiteSpace($notFoundDecks[$code].Values[$_]) })
        $extra = @($notFoundDecks[$code].Keys | Where-Object { -not $notFoundDecks['en'].Values.Contains($_) })
        if ($missing.Count -or $extra.Count) {
            throw "tools/site/copy/notfound/$code.txt does not match English (missing/empty: $($missing -join ', '); extra: $($extra -join ', '))."
        }
    }

    $notFoundEn = $notFoundDecks['en'].Values
    $notFoundBlocks = [System.Collections.Generic.List[string]]::new()
    $notFoundBlocks.Add('      <span class="eyebrow">404</span>')
    $notFoundBlocks.Add(('      <h1>{0}</h1>' -f (ConvertTo-HtmlText $notFoundEn['heading'])))
    $notFoundBlocks.Add(('      <p class="privacy-intro">{0}</p>' -f (ConvertTo-HtmlText $notFoundEn['text'])))
    $notFoundBlocks.Add(('      <div class="button-group"><a class="button button-primary" href="{0}">{1}</a></div>' -f $BaseUrl, (ConvertTo-HtmlText $notFoundEn['home'])))
    $notFoundBlocks.Add('      <div class="privacy-card">')
    foreach ($code in ($notFoundCodes | Where-Object { $_ -ne 'en' })) {
        $values = $notFoundDecks[$code].Values
        $notFoundBlocks.Add(('        <section class="privacy-section" lang="{0}">' -f $code))
        $notFoundBlocks.Add(('          <h2>{0}</h2>' -f (ConvertTo-HtmlText $values['heading'])))
        $notFoundBlocks.Add(('          <p>{0}</p>' -f (ConvertTo-HtmlText $values['text'])))
        $notFoundBlocks.Add(('          <div class="button-group"><a class="button button-ghost button-small" href="{0}">{1}</a></div>' -f $BaseUrl, (ConvertTo-HtmlText $values['home'])))
        $notFoundBlocks.Add('        </section>')
    }
    $notFoundBlocks.Add('      </div>')

    $notFoundPage = [System.IO.File]::ReadAllText((Join-Path $TemplateDirectory '404.html')).Replace("`r`n", "`n")
    $notFoundPage = $notFoundPage.Replace('{{include:footer}}', $Templates['footer'])
    $notFoundPage = Expand-Glyphs -Html $notFoundPage
    $notFoundStructural = [ordered]@{
        '{{page.title}}'          = ConvertTo-HtmlText $notFoundEn['title']
        '{{page.description}}'    = ConvertTo-HtmlText $notFoundEn['description']
        '{{page.base}}'           = $BaseUrl
        '{{page.home}}'           = $BaseUrl
        '{{page.privacy}}'        = "${BaseUrl}privacy.html"
        '{{page.trust}}'          = "${BaseUrl}trust.html"
        '{{page.whatsnew}}'       = "${BaseUrl}whats-new.html"
        '{{page.support}}'        = "${BaseUrl}support.html"
        '{{page.docs}}'           = Get-DocsUrl -Code 'en'
        '{{page.machineNote}}'    = ''
        '{{page.notFoundBlocks}}' = $notFoundBlocks -join "`n"
    }
    foreach ($entry in $notFoundStructural.GetEnumerator()) {
        $notFoundPage = $notFoundPage.Replace($entry.Key, [string] $entry.Value)
    }
    foreach ($key in $English.Keys) {
        $notFoundPage = $notFoundPage.Replace("{{t.$key}}", (ConvertTo-HtmlText $English.Values[$key]))
    }
    $notFoundLeftover = [regex]::Matches($notFoundPage, '\{\{[^}]+\}\}') | ForEach-Object { $_.Value } | Sort-Object -Unique
    if ($notFoundLeftover) { throw "404.html: unresolved template token(s): $($notFoundLeftover -join ', ')" }
    # SITE-STRUCTURE rule 15, observed on the output: noindex, and no address that depends on the folder depth.
    if ($notFoundPage -notmatch '<meta name="robots" content="noindex">') { throw '404.html must carry <meta name="robots" content="noindex">.' }
    $relativeAddress = [regex]::Matches($notFoundPage, '(?:href|src)="(?!https://|mailto:|#)[^"]*"') | ForEach-Object { $_.Value }
    if ($relativeAddress) { throw "404.html holds a relative address (it is served at any depth): $($relativeAddress -join ', ')" }
    return $notFoundPage
}
