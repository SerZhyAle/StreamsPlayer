#Requires -Version 7.0
<#
.SYNOPSIS
    SP-0230: renders the in-app help pages (CONTEXT-HELP) from the help map, the copy and the UI dictionaries.

.DESCRIPTION
    help/help-map.json                       the surfaces and their help ids (embedded in StreamsPlayer.Core too)
    help/stamp.json                          the version and date the pages were written against (rule 8 item 7)
    help/content/<locale>/_chrome.json       the words around a page: headings, pills, footer, the index copy
    help/content/<locale>/<help-id>.json     the copy of one page; names come from the dictionary key it cites
    src/StreamsPlayer.App/Localization.<locale>.xaml    the UI strings (rule 13: one string source)
    help/templates/help.css                  the help layer over the kit (copied from the contract's reference)
    docs/assets/sza-kit.css                  the vendored kit, copied beside the pages byte for byte

    Writes (never hand-edit; the gate reads them as render targets):
    docs/help/<locale>/<help-id>.html and index.html for each authored locale
    docs/help/help.css, docs/help/sza-kit.css
    docs/help/mark-24.png, docs/help/mark-48.png    the exchange mark an entry draws (EXCHANGE-MARK, SP-0234)

    A locale with no folder under help/content/ is not generated: it opens the English page at run time
    (rule 13) and is a `locale` line of help/help-baseline.txt.

    Copy syntax inside a string (everything else is shown as text): **bold**, [[Ctrl+F]] for a key, and {@id}
    for a link to the entry of that id on the same page, shown as the entry's name.

    -Check writes nothing: exit 0 when docs/help matches what would be written, 1 when a file differs, is
    missing, or is left over (CHECK-VERDICT), 2 when the generator cannot run.

    -Check also holds each translation to the English copy it was rebuilt from (SP-0239 P5, DOC-EXTERNAL-QUALITY
    rule 3): every _chrome.json and page copy of a locale other than the source locale carries a top-level
    "sourceHash": "sha256:<16 hex>", the fingerprint (scripts/SourceFingerprint.ps1) of the same-named file of the
    source locale. The renderer ignores the property. A missing or malformed value, or an English file that now hashes
    to something else, fails the check: rebuild the translation from the current English copy, then record the new
    fingerprint the failure names. The plain generator run does not look at it.

.EXAMPLE
    pwsh -NoProfile -File ./tools/help/build-help.ps1
    pwsh -NoProfile -File ./tools/help/build-help.ps1 -Check
#>
[CmdletBinding()]
param(
    [switch] $Check,
    # The site's address; the same value as build-site.ps1's default, which owns it.
    [string] $BaseUrl = 'https://serzhyale.github.io/StreamsPlayer/'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$source = Join-Path $root 'help'
$output = Join-Path $root 'docs/help'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$rightToLeft = @('ar', 'ur')

function Stop-Unverified([string] $Why) {
    Write-Host "help-build: COULD NOT VERIFY ($Why)"
    exit 2
}

# -Check is a gate: a terminating error nothing below anticipated (an unreadable file, a throwing renderer) means the
# comparison was never made - could not verify, with a verdict line, not a stack trace and exit 1 (CHECK-VERDICT 2.1,
# 2.5, 2.11). A plain generator run keeps its exception.
trap {
    if (-not $Check) { break }
    Stop-Unverified ($_.Exception.Message -replace '\s+', ' ')
}

# Only the freshness comparison of -Check needs the fingerprint; the generator run does not depend on the file.
if ($Check) { . (Join-Path $root 'scripts/SourceFingerprint.ps1') }

function Read-Json([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { Stop-Unverified "missing $Path" }
    try { return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -Depth 30 }
    catch { Stop-Unverified "$Path does not parse: $($_.Exception.Message -replace '\s+', ' ')" }
}

function Has([object] $Object, [string] $Name) {
    return $null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name -and $null -ne $Object.$Name
}

function Get-Dictionary([string] $Code) {
    $path = Join-Path $root "src/StreamsPlayer.App/Localization.$Code.xaml"
    if (-not (Test-Path -LiteralPath $path)) { Stop-Unverified "no dictionary for $Code" }
    try { $xml = [xml](Get-Content -LiteralPath $path -Raw -Encoding utf8) }
    catch { Stop-Unverified "$path does not parse: $($_.Exception.Message -replace '\s+', ' ')" }
    $ns ='http://schemas.microsoft.com/winfx/2006/xaml'
    $table = @{}
    foreach ($node in $xml.DocumentElement.ChildNodes) {
        if ($node.NodeType -ne 'Element') { continue }
        $key = $node.GetAttribute('Key', $ns)
        if ($key) { $table[$key] = $node.InnerText }
    }
    return $table
}

# Only the four characters HTML reads as markup. WebUtility.HtmlEncode also turns every Latin-1 letter and the
# quotation marks of Russian and French into numeric entities, which reads fine in a browser and not at all in a diff.
function Format-Text([string] $Text) {
    return $Text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
}

# Copy syntax to HTML. $Names maps an entry id to the name shown for it, for {@id}.
function Format-Inline([string] $Text, [hashtable] $Names, [string] $Where) {
    $html = Format-Text $Text
    $html = [regex]::Replace($html, '\*\*(.+?)\*\*', '<b>$1</b>')
    $html = [regex]::Replace($html, '\[\[(.+?)\]\]', {
        param($m)
        (($m.Groups[1].Value -split '\+') | ForEach-Object { '<kbd>' + $_.Trim() + '</kbd>' }) -join ' + '
    })
    $html = [regex]::Replace($html, '\{@([a-z0-9-]+)\}', {
        param($m)
        $id = $m.Groups[1].Value
        if (-not $Names.ContainsKey($id)) { throw "$Where refers to {@$id}, which is no entry of the page" }
        '<a href="#e-' + $id + '">' + (Format-Text $Names[$id]) + '</a>'
    })
    return $html
}

function Get-Name([object] $Item, [hashtable] $Dictionary, [string] $Where) {
    if (Has $Item 'nameKey') {
        $key = [string] $Item.nameKey
        if (-not $Dictionary.ContainsKey($key)) { throw "$Where cites the UI string '$key', which the dictionary does not hold" }
        return $Dictionary[$key]
    }
    if (Has $Item 'name') { return [string] $Item.name }
    throw "$Where has neither a nameKey nor a name"
}

function Get-SiteAddress([string] $Locale, [string] $Page) {
    $folder = if ($Locale -eq 'en') { '' } else { "$Locale/" }
    return "$BaseUrl$folder$Page"
}

$map = Read-Json (Join-Path $source 'help-map.json')
$stamp = Read-Json (Join-Path $source 'stamp.json')
$baselineLines = if (Test-Path -LiteralPath (Join-Path $source 'help-baseline.txt')) {
    Get-Content -LiteralPath (Join-Path $source 'help-baseline.txt') -Encoding utf8
} else { @() }
$unwritten = @($baselineLines | Where-Object { $_ -match '^page (\S+)$' } | ForEach-Object { $Matches[1] })

$contentRoot = Join-Path $source 'content'
$locales = @(Get-ChildItem -LiteralPath $contentRoot -Directory | ForEach-Object { $_.Name } | Sort-Object)
if ($locales -notcontains $map.sourceLocale) { Stop-Unverified "the source locale $($map.sourceLocale) has no copy" }

$pageSurfaces = @($map.surfaces | Where-Object { $_.form -in 'page', 'both' -and $_.help -notin $unwritten })
$helpIds = @($map.surfaces | Where-Object { Has $_ 'help' } | ForEach-Object { $_.help })

$themeScript = @'
<script>
(function () {
  try {
    var q = new URLSearchParams(location.search).get('theme');
    var t = (q === 'light' || q === 'dark') ? q : (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
    document.documentElement.setAttribute('data-theme', t);
  } catch (e) { /* no script: help.css follows the system */ }
})();
</script>
'@
$entryScript = @'
<script>
/* Progressive enhancement only: the page is complete without it. */
(function () {
  function openHash() {
    var id = location.hash.slice(1);
    var el = id && document.getElementById(id);
    if (el && el.tagName === 'DETAILS') { el.open = true; }
  }
  addEventListener('hashchange', openHash);
  openHash();
})();
</script>
'@

function New-Head([string] $Locale, [string] $Title) {
    $direction = if ($rightToLeft -contains $Locale) { 'rtl' } else { 'ltr' }
    return @(
        '<!doctype html>'
        "<html lang=`"$Locale`" dir=`"$direction`">"
        '<head>'
        '<meta charset="utf-8">'
        '<meta name="viewport" content="width=device-width, initial-scale=1">'
        '<meta name="referrer" content="no-referrer">'
        "<title>$(Format-Text $Title)</title>"
        $themeScript.TrimEnd()
        '<link rel="stylesheet" href="../sza-kit.css">'
        '<link rel="stylesheet" href="../help.css">'
        '</head>'
        '<body class="help">'
        '<main class="container">'
        ''
    ) -join "`n"
}

function New-Footer([string] $Locale, [object] $Chrome, [hashtable] $Dictionary) {
    $line = ([string] $Chrome.footerWritten).Replace('{product}', $Dictionary['ProductName']).Replace('{version}', $stamp.version).Replace('{date}', $stamp.date)
    return @(
        '  <footer class="help-foot">'
        "    <span>$(Format-Text $line)</span>"
        "    <a href=`"index.html`">$(Format-Text $Chrome.indexLink)</a>"
        "    <a href=`"$(Get-SiteAddress $Locale 'support.html')`">$(Format-Text $Chrome.support)</a>"
        '  </footer>'
        ''
        '</main>'
    ) -join "`n"
}

function Format-Facts([object] $Item, [object] $Chrome, [hashtable] $Names, [string] $Where) {
    $rows = [System.Collections.Generic.List[string]]::new()
    foreach ($pair in @(@('why', 'factWhy'), @('next', 'factNext'), @('undo', 'factUndo'))) {
        if (Has $Item $pair[0]) {
            $rows.Add("          <dt>$(Format-Text $Chrome.($pair[1]))</dt><dd>$(Format-Inline ([string] $Item.($pair[0])) $Names $Where)</dd>")
        }
    }
    if (Has $Item 'shortcut') {
        $rows.Add("          <dt>$(Format-Text $Chrome.factShortcut)</dt><dd>$(Format-Inline ([string] $Item.shortcut) $Names $Where)</dd>")
    }
    if ($rows.Count -eq 0) { return '' }
    return "        <dl class=`"facts`">`n$($rows -join "`n")`n        </dl>"
}

function Format-Entry([object] $Item, [object] $Chrome, [hashtable] $Dictionary, [hashtable] $Names, [string] $Page) {
    $where = "$Page entry '$($Item.id)'"
    $name = Format-Text (Get-Name $Item $Dictionary $where)
    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.Add("    <details class=`"el`" id=`"e-$($Item.id)`">")
    # EXCHANGE-MARK rule 11 (SP-0234): the title of an entry about a control that shows the mark carries it inline, at
    # 24 px, the 48 px frame for a double-density screen. Decorative: the name beside it is the term (rule 14), and a
    # browser never mirrors an image, which is what rule 2 asks of a right-to-left page.
    $mark = if (Has $Item 'mark') { '<img src="../mark-24.png" srcset="../mark-24.png 1x, ../mark-48.png 2x" width="24" height="24" alt="">' } else { '' }
    $parts.Add("      <summary>$mark<span><span class=`"name`">$name</span><span class=`"one`">$(Format-Inline ([string] $Item.one) $Names $where)</span></span></summary>")
    $parts.Add('      <div class="full">')
    foreach ($paragraph in @($Item.full)) { $parts.Add("        <p>$(Format-Inline ([string] $paragraph) $Names $where)</p>") }
    $facts = Format-Facts $Item $Chrome $Names $where
    if ($facts) { $parts.Add($facts) }
    if (Has $Item 'tip') {
        $parts.Add("        <div class=`"note`"><span class=`"tag`">$(Format-Text $Chrome.tip)</span><p>$(Format-Inline ([string] $Item.tip) $Names $where)</p></div>")
    }
    $parts.Add('      </div>')
    $parts.Add('    </details>')
    return $parts -join "`n"
}

function New-Page([string] $Locale, [object] $Surface, [object] $Copy, [object] $Chrome, [hashtable] $Dictionary) {
    $page = "$Locale/$($Surface.help).json"
    $title = Get-Name $Surface $Dictionary "$page surface"
    $groups = if (Has $Copy 'groups') { @($Copy.groups) } else { @([pscustomobject]@{ elements = @($Copy.elements) }) }

    $names = @{}
    $seen = @{}
    foreach ($group in $groups) {
        foreach ($item in @($group.elements)) {
            if ($seen.ContainsKey($item.id)) { throw "$page has the entry id '$($item.id)' twice" }
            $seen[$item.id] = $true
            $names[$item.id] = Get-Name $item $Dictionary "$page entry '$($item.id)'"
        }
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add((New-Head $Locale "$($Chrome.titlePrefix) - $title"))
    $lines.Add('  <nav class="help-crumbs" aria-label="' + (Format-Text $Chrome.breadcrumb) + '">')
    $lines.Add("    <a href=`"index.html`">$(Format-Text $Chrome.indexLink)</a><span class=`"sep`" aria-hidden=`"true`">/</span><span>$(Format-Text $title)</span>")
    $lines.Add('  </nav>')
    $lines.Add('  <header class="hero">')
    $lines.Add("    <p class=`"kicker`">$(Format-Text $Chrome.kicker)</p>")
    $lines.Add("    <h1>$(Format-Text $title)</h1>")
    $lines.Add("    <p class=`"lead`">$(Format-Inline ([string] $Copy.lead) $names $page)</p>")
    $lines.Add('  </header>')
    $lines.Add('')

    $lines.Add("  <h2>$(Format-Text $Chrome.orderHeading)</h2>")
    if (Has $Copy 'order') {
        if (@($Copy.order).Count -gt 7) { throw "$page lists more than seven steps (CONTEXT-HELP rule 8 item 4)" }
        $lines.Add('  <ol class="order">')
        foreach ($step in @($Copy.order)) { $lines.Add("    <li>$(Format-Inline ([string] $step) $names $page)</li>") }
        $lines.Add('  </ol>')
    } elseif (Has $Copy 'orderNote') {
        $lines.Add("  <p>$(Format-Inline ([string] $Copy.orderNote) $names $page)</p>")
    } else { throw "$page has neither an order nor an orderNote" }
    $lines.Add('')

    $lines.Add("  <h2>$(Format-Text $Chrome.elementsHeading)</h2>")
    $grouped = Has $Copy 'groups'
    if (-not $grouped) { $lines.Add('  <div class="elements">') }
    foreach ($group in $groups) {
        if ($grouped) {
            $heading = Get-Name $group $Dictionary "$page group '$($group.id)'"
            $lines.Add("  <section id=`"$($group.id)`">")
            $lines.Add("  <h3>$(Format-Text $heading)</h3>")
            if (Has $group 'lead') { $lines.Add("  <p>$(Format-Inline ([string] $group.lead) $names $page)</p>") }
            $lines.Add('  <div class="elements">')
        }
        foreach ($item in @($group.elements)) {
            $lines.Add((Format-Entry $item $Chrome $Dictionary $names $page))
        }
        if ($grouped) { $lines.Add('  </div>'); $lines.Add('  </section>') }
    }
    if (-not $grouped) { $lines.Add('  </div>') }
    $lines.Add('')

    $lines.Add("  <h2>$(Format-Text $Chrome.relatedHeading)</h2>")
    $lines.Add('  <ul class="related">')
    foreach ($link in @($Copy.related)) {
        $note = Format-Inline ([string] $link.note) $names "$page related"
        if (Has $link 'help') {
            if ($link.help -notin $helpIds) { throw "$page links the help id '$($link.help)', which is no surface of the map" }
            if ($link.help -in $unwritten) { continue }   # its page is on the baseline; the link appears when the page is written
            $target = $map.surfaces | Where-Object { (Has $_ 'help') -and $_.help -eq $link.help } | Select-Object -First 1
            $label = Format-Text (Get-Name $target $Dictionary "$page related '$($link.help)'")
            $lines.Add("    <li><a href=`"$($link.help).html`"><b>$label</b><span class=`"pill`">$(Format-Text $Chrome.pillWindow)</span><small>$note</small></a></li>")
        } elseif (Has $link 'site') {
            $siteFile = switch ($link.site) { 'use' { 'index.html#use' } 'support' { 'support.html' } 'privacy' { 'privacy.html' } default { throw "$page links the site page '$($link.site)', which the generator does not know" } }
            $lines.Add("    <li><a href=`"$(Get-SiteAddress $Locale $siteFile)`"><b>$(Format-Text $Chrome.site.($link.site))</b><span class=`"pill gold`">$(Format-Text $Chrome.pillSite)</span><small>$note</small></a></li>")
        } else { throw "$page has a related item with neither a help id nor a site page" }
    }
    $lines.Add('  </ul>')
    $lines.Add('')
    $lines.Add((New-Footer $Locale $Chrome $Dictionary))
    $lines.Add($entryScript.TrimEnd())
    $lines.Add('</body>')
    $lines.Add('</html>')
    return ($lines -join "`n") + "`n"
}

function New-Index([string] $Locale, [object] $Chrome, [hashtable] $Dictionary, [hashtable] $Copies) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add((New-Head $Locale $Chrome.indexTitle))
    $lines.Add('  <header class="hero">')
    $lines.Add("    <p class=`"kicker`">$(Format-Text $Chrome.kicker)</p>")
    $lines.Add("    <h1>$(Format-Text $Chrome.indexTitle)</h1>")
    $lines.Add("    <p class=`"lead`">$(Format-Inline ([string] $Chrome.indexLead) @{} 'index lead')</p>")
    $lines.Add('  </header>')
    $lines.Add('')
    $lines.Add("  <h2>$(Format-Text $Chrome.indexAskHeading)</h2>")
    foreach ($paragraph in @($Chrome.indexAsk)) { $lines.Add("  <p>$(Format-Inline ([string] $paragraph) @{} 'index ask')</p>") }
    $lines.Add('')
    $lines.Add("  <h2>$(Format-Text $Chrome.indexWindowsHeading)</h2>")
    $lines.Add('  <ul class="related">')
    foreach ($surface in $pageSurfaces) {
        $label = Format-Text (Get-Name $surface $Dictionary "index surface '$($surface.id)'")
        $summary = Format-Text ([string] $Copies[$surface.help].summary)
        $lines.Add("    <li><a href=`"$($surface.help).html`"><b>$label</b><span class=`"pill`">$(Format-Text $Chrome.pillWindow)</span><small>$summary</small></a></li>")
    }
    $lines.Add('  </ul>')
    $lines.Add('')
    $lines.Add("  <h2>$(Format-Text $Chrome.relatedHeading)</h2>")
    $lines.Add('  <ul class="related">')
    foreach ($link in @($Chrome.indexRelated)) {
        $siteFile = switch ($link.site) { 'use' { 'index.html#use' } 'support' { 'support.html' } 'privacy' { 'privacy.html' } default { throw "index links the site page '$($link.site)', which the generator does not know" } }
        $lines.Add("    <li><a href=`"$(Get-SiteAddress $Locale $siteFile)`"><b>$(Format-Text $Chrome.site.($link.site))</b><span class=`"pill gold`">$(Format-Text $Chrome.pillSite)</span><small>$(Format-Text ([string] $link.note))</small></a></li>")
    }
    $lines.Add('  </ul>')
    $lines.Add('')
    $lines.Add((New-Footer $Locale $Chrome $Dictionary))
    $lines.Add('</body>')
    $lines.Add('</html>')
    return ($lines -join "`n") + "`n"
}

# ---------------------------------------------------------------------------------------- generate

$generated = [ordered]@{}   # path relative to docs/help, forward slashes -> text
foreach ($locale in $locales) {
    $dictionary = Get-Dictionary $locale
    $chrome = Read-Json (Join-Path $contentRoot "$locale/_chrome.json")
    $copies = @{}
    foreach ($surface in $pageSurfaces) {
        $file = Join-Path $contentRoot "$locale/$($surface.help).json"
        if (-not (Test-Path -LiteralPath $file)) { throw "$locale has no copy for the page '$($surface.help)', which the map names and the baseline does not owe" }
        $copies[$surface.help] = Read-Json $file
        $generated["$locale/$($surface.help).html"] = New-Page $locale $surface $copies[$surface.help] $chrome $dictionary
    }
    $generated["$locale/index.html"] = New-Index $locale $chrome $dictionary $copies
}

$generatedBytes = [ordered]@{}
foreach ($path in $generated.Keys) { $generatedBytes[$path] = $utf8.GetBytes($generated[$path]) }
$generatedBytes['help.css'] = [System.IO.File]::ReadAllBytes((Join-Path $source 'templates/help.css'))
$generatedBytes['sza-kit.css'] = [System.IO.File]::ReadAllBytes((Join-Path $root 'docs/assets/sza-kit.css'))
# EXCHANGE-MARK rule 6 (SP-0234): the two frames an entry's mark draws are copies of the vendored PNGs, byte for byte
# and under the names the contract gives them, so the contract's finder sees them.
foreach ($frame in 'mark-24.png', 'mark-48.png') {
    $generatedBytes[$frame] = [System.IO.File]::ReadAllBytes((Join-Path $root "assets/exchange-mark/png/$frame"))
}

# ---------------------------------------------------------------------------------------- translation freshness

$outdated = [System.Collections.Generic.List[string]]::new()
if ($Check) {
    $fingerprintForm = '^sha256:[0-9a-f]{16}$'
    $copyNames = @('_chrome') + @($pageSurfaces | ForEach-Object { $_.help })
    foreach ($locale in @($locales | Where-Object { $_ -ne $map.sourceLocale })) {
        foreach ($name in $copyNames) {
            $translation = "help/content/$locale/$name.json"
            $english = "help/content/$($map.sourceLocale)/$name.json"
            $copy = Read-Json (Join-Path $root $translation)
            $recorded = if (Has $copy 'sourceHash') { [string] $copy.sourceHash } else { '' }
            if ($recorded -notmatch $fingerprintForm) {
                $outdated.Add("$translation has no sourceHash of the form sha256:<16 hex> (the fingerprint of $english it was rebuilt from)")
                continue
            }
            $current = Get-SourceFingerprint (Join-Path $root $english)
            if ($current -ne $recorded) {
                $outdated.Add("$english is $current but $translation was rebuilt from $recorded - rebuild it from the current English copy, then record $current as its sourceHash")
            }
        }
    }
}

# ---------------------------------------------------------------------------------------- write or compare

$problems = [System.Collections.Generic.List[string]]::new()
foreach ($path in $generatedBytes.Keys) {
    $target = Join-Path $output $path
    $current = if (Test-Path -LiteralPath $target) { [System.IO.File]::ReadAllBytes($target) } else { $null }
    # .gitattributes checks *.html out with CRLF; the generator writes LF, so a page is compared with its line breaks
    # normalized, as build-site.ps1 does for the site. The two css files are copied byte for byte from sources that
    # are checked out the same way, so they are compared as they are.
    if ($null -ne $current -and $path.EndsWith('.html')) { $current = $utf8.GetBytes($utf8.GetString($current).Replace("`r`n", "`n")) }
    $same = $null -ne $current -and [System.Linq.Enumerable]::SequenceEqual([byte[]] $current, [byte[]] $generatedBytes[$path])
    if ($same) { continue }
    if ($Check) { $problems.Add("docs/help/$path is $(if ($null -eq $current) { 'missing' } else { 'stale' })"); continue }
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    [System.IO.File]::WriteAllBytes($target, $generatedBytes[$path])
    Write-Host "wrote docs/help/$path"
}

if (Test-Path -LiteralPath $output) {
    foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File) {
        $relative = [System.IO.Path]::GetRelativePath($output, $file.FullName).Replace('\', '/')
        if ($generatedBytes.Contains($relative)) { continue }
        if ($Check) { $problems.Add("docs/help/$relative is left over: nothing generates it"); continue }
        Remove-Item -LiteralPath $file.FullName -Force
        Write-Host "removed docs/help/$relative"
    }
}

if ($problems.Count -or $outdated.Count) {
    foreach ($problem in @($problems) + @($outdated)) { Write-Host "  $problem" }
    $reasons = @()
    if ($problems.Count) { $reasons += "$($problems.Count) file(s) out of date; run tools/help/build-help.ps1" }
    if ($outdated.Count) { $reasons += "$($outdated.Count) translation(s) older than their English copy; rebuild them and record its fingerprint" }
    Write-Host "help-build: FAIL ($($reasons -join '; '))"
    exit 1
}
Write-Host "help-build: PASS ($($locales.Count) locales, $($pageSurfaces.Count) pages and an index each$(if ($Check) { ', in sync' } else { ', written' }))"
exit 0
