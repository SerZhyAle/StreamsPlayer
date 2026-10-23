#Requires -Version 7.0
<#
.SYNOPSIS
    SP-0230: holds the generated in-app help set to CONTEXT-HELP - anatomy, language, no request, every link.

.DESCRIPTION
    The static half of rung 1 (CONTEXT-HELP section 4) over docs/help/, the render target of
    tools/help/build-help.ps1. The other half - the windows of the source against the map, the baseline, the
    copy, the anchors and F1 - is HelpGateTests in the test project; the map and the resolver have their own
    tests there as well.

      pages      every written page of the map exists in every authored locale, plus the index; no page is
                 left over that the map does not name
      anatomy    the parts of rule 8 in order - title, purpose line, picture (until the `shot` baseline is
                 empty only the pages not on it must have one), the order, the elements, related, footer;
                 `lang` and `dir` match the folder; every picture has alt text
      index      the index of a locale links every written page of that locale, by a relative same-folder link
                 (DOC-EXTERNAL-QUALITY section 8 item B: a subject index that links every page, held complete);
                 a page the index does not link fails, so a page nobody reaches cannot ship
      requests   a page makes none (rule 12): the referrer is withheld; nothing but an anchor leads to
                 another origin; no web font, script source, @import or url()
      links      every relative link and style sheet resolves; every #anchor names an id of its target; a
                 link between pages never leaves the locale; an address of the product site resolves against
                 docs/ (offline), and no other address appears
      assets     sza-kit.css beside the pages is byte-identical to docs/assets/sza-kit.css and help.css to
                 help/templates/help.css

    Exit code (CHECK-VERDICT): 0 pass, 1 a defect, 2 could not verify. The last line is the verdict.
#>
[CmdletBinding()]
param(
    [string] $Help,
    [string] $Root,
    [string] $BaseUrl = 'https://serzhyale.github.io/StreamsPlayer/'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0
if (-not $Root) { $Root = Split-Path $PSScriptRoot -Parent }
if (-not $Help) { $Help = Join-Path $Root 'docs/help' }

function Stop-Unverified([string] $Why) {
    Write-Host "help: COULD NOT VERIFY ($Why)"
    exit 2
}

function Has([object] $Object, [string] $Name) {
    return $null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name -and $null -ne $Object.$Name
}

$mapFile = Join-Path $Root 'help/help-map.json'
if (-not (Test-Path -LiteralPath $mapFile)) { Stop-Unverified 'no help/help-map.json' }
if (-not (Test-Path -LiteralPath $Help)) { Stop-Unverified "no $Help - run tools/help/build-help.ps1" }
# The two sources the generated copies beside the pages are compared with: without one there is nothing to compare against.
$kit = Join-Path $Root 'docs/assets/sza-kit.css'
$layer = Join-Path $Root 'help/templates/help.css'
foreach ($source in $kit, $layer) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { Stop-Unverified "no $source - the source of a generated copy" }
}
try { $map =Get-Content -LiteralPath $mapFile -Raw -Encoding utf8 | ConvertFrom-Json -Depth 30 }
catch { Stop-Unverified "help-map.json does not parse: $($_.Exception.Message)" }

$failures = [System.Collections.Generic.List[string]]::new()
function Fail([string] $Message) { $failures.Add($Message) }

# A terminating error nothing below anticipated (an unreadable page, a failing file API) stops the run before it can judge:
# could not verify, with a verdict line, not a stack trace and exit 1 (CHECK-VERDICT 2.1, 2.5, 2.11). Problems already
# recorded still outrank it (2.14).
trap {
    $reason = $_.Exception.Message -replace '\s+', ' '
    if ($failures.Count -eq 0) { Stop-Unverified $reason }
    foreach ($failure in $failures) { Write-Host "  FAIL  $failure" }
    Write-Host "help: the run stopped early: $reason"
    Write-Host "help: FAIL ($($failures.Count) problem(s))"
    exit 1
}

$baselineFile = Join-Path $Root 'help/help-baseline.txt'
$baseline = if (Test-Path -LiteralPath $baselineFile) {
    @(Get-Content -LiteralPath $baselineFile -Encoding utf8 | Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith('#') })
} else { @() }
$unwritten = @($baseline | Where-Object { $_ -match '^page (\S+)$' } | ForEach-Object { $Matches[1] })
$noPicture = @($baseline | Where-Object { $_ -match '^shot (\S+)$' } | ForEach-Object { $Matches[1] })

$written = @($map.surfaces | Where-Object { $_.form -in 'page', 'both' -and $_.help -notin $unwritten } | ForEach-Object { $_.help })
$expected = @($written) + @($map.index)
$locales = @(Get-ChildItem -LiteralPath $Help -Directory | ForEach-Object { $_.Name } | Sort-Object)
if ($locales.Count -eq 0) { Fail 'pages: docs/help holds no locale folder' }
if ($locales -notcontains $map.sourceLocale) { Fail "pages: the source locale $($map.sourceLocale) has no folder" }
$rightToLeft = @('ar', 'ur')

# ------------------------------------------------------------------------------------------- assets
if (-not (Test-Path -LiteralPath (Join-Path $Help 'sza-kit.css'))) { Fail 'assets: sza-kit.css is not beside the pages' }
elseif ((Get-FileHash -LiteralPath $kit).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Help 'sza-kit.css')).Hash) { Fail 'assets: sza-kit.css differs from docs/assets/sza-kit.css' }
if (-not (Test-Path -LiteralPath (Join-Path $Help 'help.css'))) { Fail 'assets: help.css is not beside the pages' }
elseif ((Get-FileHash -LiteralPath $layer).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Help 'help.css')).Hash) { Fail 'assets: help.css differs from help/templates/help.css' }

# ------------------------------------------------------------------------------------------- pages
$siteRoot = Join-Path $Root 'docs'
$idCache = @{}
function Get-Ids([string] $Path) {
    if (-not $idCache.ContainsKey($Path)) {
        $idCache[$Path] = @([regex]::Matches((Get-Content -LiteralPath $Path -Raw -Encoding utf8), '\bid="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
    }
    return $idCache[$Path]
}

$pageCount = 0
$linkCount = 0
foreach ($locale in $locales) {
    $folder = Join-Path $Help $locale
    foreach ($leftover in @(Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.BaseName -notin $expected -or $_.Extension -ne '.html' })) {
        Fail "pages: $locale/$($leftover.Name) is not a page the map names"
    }

    foreach ($id in $expected) {
        $file = Join-Path $folder "$id.html"
        $where = "$locale/$id.html"
        if (-not (Test-Path -LiteralPath $file)) { Fail "pages: $where is missing"; continue }
        $pageCount++
        $html = Get-Content -LiteralPath $file -Raw -Encoding utf8
        $markup = [regex]::Replace($html, '<script>.*?</script>', '', 'Singleline')

        $direction = if ($rightToLeft -contains $locale) { 'rtl' } else { 'ltr' }
        if ($html -notmatch ('<html lang="' + $locale + '" dir="' + $direction + '">')) { Fail "anatomy: $where has the wrong lang or dir" }
        if ($html -notmatch '<meta name="referrer" content="no-referrer">') { Fail "requests: $where does not withhold the referrer (rule 12)" }
        if ($markup -match '<(?!a )[^>]*\b(src|href)="https?:') { Fail "requests: $where loads something from another origin (rule 12)" }
        if ($markup -match '@import|url\(' -or $html -match '<script[^>]*\bsrc=' -or $html -match 'fonts\.googleapis') { Fail "requests: $where imports a style, a script or a font (rule 12)" }
        if ($html -match '<img\b(?![^>]*\balt=)') { Fail "anatomy: $where has a picture with no alt text" }

        # The parts of rule 8, in order. The index is a different page: it has no screenshot and no order.
        $isIndex = $id -eq $map.index
        $parts = if ($isIndex) {
            @('<h1>', 'class="lead"', 'class="related"', 'class="help-foot"')
        } else {
            $list = @('<h1>', 'class="lead"')
            if ($id -notin $noPicture) { $list += 'class="shot"' }
            $list + @('<h2>', 'class="elements"', 'class="related"', 'class="help-foot"')
        }
        $at = -1
        foreach ($part in $parts) {
            $index = $html.IndexOf($part, $at + 1, [StringComparison]::Ordinal)
            if ($index -lt 0) { Fail "anatomy: $where lacks '$part' in its place (CONTEXT-HELP rule 8)"; break }
            $at = $index
        }
        if (-not $isIndex -and $id -notin $noPicture -and $html -notmatch 'class="on-dark"') { Fail "anatomy: $where is not on the picture baseline and has no picture" }
        if (-not $isIndex -and $html -notmatch 'class="order"' -and $html -notmatch '</h2>\s*<p>') { Fail "anatomy: $where has neither an order list nor a note that it has none" }

        $pageIds = Get-Ids $file
        if (@($pageIds | Group-Object | Where-Object Count -gt 1).Count) { Fail "anatomy: $where repeats an id" }

        foreach ($match in [regex]::Matches($markup, '(?:href|src)="([^"]+)"')) {
            $target = $match.Groups[1].Value
            $linkCount++
            if ($target.StartsWith('#')) {
                if ($target.Length -gt 1 -and $target.Substring(1) -notin $pageIds) { Fail "links: $where points at $target, which is no id of the page" }
                continue
            }
            if ($target -like 'https://*') {
                if (-not $target.StartsWith($BaseUrl, [StringComparison]::Ordinal)) { Fail "links: $where links $target, an address that is neither relative nor the product site's"; continue }
                $path, $fragment = ($target.Substring($BaseUrl.Length)) -split '#', 2
                if ([string]::IsNullOrEmpty($path) -or $path.EndsWith('/')) { $path += 'index.html' }
                $resolved = Join-Path $siteRoot $path
                if (-not (Test-Path -LiteralPath $resolved)) { Fail "links: $where links the site page $path, which docs/ does not hold"; continue }
                if ($fragment -and $fragment -notin (Get-Ids $resolved)) { Fail "links: $where links $target, whose page has no such id" }
                continue
            }
            if ($target -match '^[a-z][a-z0-9+.-]*:') { Fail "links: $where uses the scheme of $target"; continue }
            $path, $fragment = $target -split '#', 2
            $resolved = [System.IO.Path]::GetFullPath((Join-Path $folder $path))
            if (-not $resolved.StartsWith([System.IO.Path]::GetFullPath($Help), [StringComparison]::OrdinalIgnoreCase)) { Fail "links: $where links $target, which leaves the help folder"; continue }
            if (-not (Test-Path -LiteralPath $resolved)) { Fail "links: $where links $target, which is not there"; continue }
            if ($path -match '\.html$') {
                if ((Split-Path $resolved -Parent) -ne (Resolve-Path -LiteralPath $folder).Path) { Fail "links: $where links $target, a page of another locale (a page stays in its own language)" }
                if ($fragment -and $fragment -notin (Get-Ids $resolved)) { Fail "links: $where links $target, which has no such id" }
            }
        }
    }

    # Completeness of the index: a page of the map that this locale's index does not link is a page nobody reaches.
    $indexFile = Join-Path $folder "$($map.index).html"
    if (Test-Path -LiteralPath $indexFile) {
        $indexMarkup = [regex]::Replace((Get-Content -LiteralPath $indexFile -Raw -Encoding utf8), '<script>.*?</script>', '', 'Singleline')
        $indexLinks = @([regex]::Matches($indexMarkup, 'href="([^"#/:]+)\.html(?:#[^"]*)?"') | ForEach-Object { $_.Groups[1].Value })
        foreach ($id in $written) {
            if ($id -notin $indexLinks) { Fail "index: $locale/$($map.index).html does not link the page $id.html" }
        }
    }
}

foreach ($failure in $failures) { Write-Host "  FAIL  $failure" }
$summary = "$pageCount pages in $($locales.Count) locales, $(@($map.surfaces).Count) surfaces, $linkCount links, $(@($baseline).Count) baseline lines"
if ($failures.Count) {
    Write-Host "help: FAIL ($($failures.Count) problem(s); $summary)"
    exit 1
}
Write-Host "help: PASS ($summary)"
exit 0
