#requires -Version 7.0
<#
.SYNOPSIS
    SP-0239 P3: fixture tests for the exit code and the last line of the gate scripts that answer in CHECK-VERDICT.

.DESCRIPTION
    Runs each script in a child pwsh and judges the two facts a caller branches on - the exit code and the last stdout
    line (rules 2.1, 2.5 and 2.11 of the contract). A script that locates its inputs relative to itself is copied into a
    throwaway tree first, so every case is deterministic and none touches the repository; check.ps1 runs against stub
    children and a stub `dotnet`, so no build or test run is started.

      the nine fixture runners (try/finally, no catch before SP-0239)
                        a throw inside the run could not be verified: exit 2, `COULD NOT VERIFY (..)`; a check that
                        already failed outranks it: exit 1 and the FAIL line. The throw is injected into a copy
      test.ps1          a results directory that already exists could not be judged: exit 2, `COULD NOT VERIFY`
                        (the PASS and FAIL words of the same line are Test-TestResultSummary.ps1's)
      check-docs.ps1    a clean tree passes; a finding fails; a document that cannot be read could not be verified; a
                        finding recorded before the stop still outranks it. The portal contribution (SP-0239 P5): a
                        control name that is no dictionary string fails (bold and guillemet forms), a page with no
                        dictionary could not be verified, a changed English source against an unchanged mirror
                        fingerprint fails, and CRLF line endings do not change the fingerprint
      check-help.ps1    a minimal help set passes; a missing source stylesheet or an unreadable page could not be verified;
                        a defect fails, also when a later page cannot be read; an index that omits a written page of its
                        own locale fails, and a link to another origin or locale does not count as a link to it
      build-help.ps1    -Check passes on what the generator wrote, fails on a stale page, could not verify a source that
                        does not parse or a copy that throws; a plain generator run keeps its exception. A translation
                        whose sourceHash is not the fingerprint of the English copy (or is missing) fails -Check, and
                        recording the new fingerprint clears it
      build-site.ps1    -Check fails on a vendored file that differs from its pin and could not verify a missing
                        provenance file; a plain generator run keeps its exception. The PASS line comes from the real
                        tree, which check.ps1 runs as `site-sync`
      make-demo.ps1     -Check passes three good files, fails a missing one (was exit 3), could not verify a locked one
      check.ps1         all-pass counts the skipped check on the verdict line; a child that answers 4 reads as 2; a
                        defect outranks it; a missing `dotnet` could not verify instead of throwing

    Exit 0 when every case passes, 1 when one fails, 2 when the fixture itself could not run. The last line is
    `check-verdict-scripts: PASS|FAIL|COULD NOT VERIFY run=N skipped=M`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pwsh = (Get-Process -Id $PID).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)

$failures = [System.Collections.Generic.List[string]]::new()
$run = 0
function Check([bool] $Condition, [string] $Name) {
    $script:run++
    if (-not $Condition) { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}
# One case: the exit code and the last line, both named in the failure so a re-run is not needed to learn what happened.
function Expect([object] $Result, [int] $Exit, [string] $LastLine, [string] $Name) {
    Check ($Result.Exit -eq $Exit -and $Result.Last -match $LastLine) "$Name (expected: exit $Exit, last line /$LastLine/ | actual: exit $($Result.Exit), '$($Result.Last)')"
}
function Invoke-Script([string] $Script, [string[]] $Arguments = @(), [string] $Path = '') {
    $saved = $env:PATH
    if ($Path) { $env:PATH = $Path }
    try { $out = & $script:pwsh -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $env:PATH = $saved }
    $exit = $LASTEXITCODE
    $lines = @($out | ForEach-Object { $_ -replace "`e\[[0-9;]*[A-Za-z]", '' } | Where-Object { $_.Trim() })
    [pscustomobject]@{ Exit = $exit; Last = $(if ($lines.Count) { $lines[-1] } else { '' }); Text = ($lines -join "`n") }
}
function Write-File([string] $Path, [string] $Text) {
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text, $script:utf8)
}
function Write-Bytes([string] $Path, [byte[]] $Bytes) {
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($Path, $Bytes)
}
# Holds the files open so that no other process can read them: a document that exists but cannot be read.
function Invoke-Locked([string[]] $Paths, [scriptblock] $Action) {
    $streams = [System.Collections.Generic.List[System.IO.FileStream]]::new()
    try {
        foreach ($path in $Paths) { $streams.Add([System.IO.File]::Open($path, 'Open', 'Read', 'None')) }
        & $Action
    }
    finally { foreach ($stream in $streams) { $stream.Dispose() } }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0239-verdict-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    # --- scripts/test.ps1 --------------------------------------------------------------------------------------------
    # The results directory must be new, so an existing one stops the run before dotnet is started.
    $existing = Join-Path $work 'results'
    New-Item -ItemType Directory -Path $existing | Out-Null
    $r = Invoke-Script (Join-Path $repo 'scripts/test.ps1') @('-ResultsDirectory', $existing)
    Expect $r 2 '^tests: COULD NOT VERIFY run=0 skipped=0$' 'test.ps1: a results directory that is not new could not be verified'

    # --- the nine fixture runners: a throw inside the try body -------------------------------------------------------
    # Each runner is copied with `$PSScriptRoot` redirected to its own folder and a throw injected as the first statement
    # of its try block, so the catch is reached without breaking the runner's real inputs. Nothing was judged: exit 2 and
    # the could-not-verify word; with a check already failed: exit 1 and the FAIL line, which outranks it (rule 2.14).
    $runners = @(
        @{ Path = 'scripts/Test-SmokeVerdictGate.ps1'; Subject = 'smoke-verdict-gate' }
        @{ Path = 'tools/Test-CatalogSnapshot.ps1'; Subject = 'catalog-snapshot-tool' }
        @{ Path = 'tools/site/Test-HeldAddresses.ps1'; Subject = 'held-addresses' }
        @{ Path = 'tools/site/Test-ReleaseNotes.ps1'; Subject = 'release-notes' }
        @{ Path = 'tools/site/Test-SiteDiscovery.ps1'; Subject = 'site-discovery' }
        @{ Path = 'tools/site/Test-SiteFacts.ps1'; Subject = 'site-facts' }
        @{ Path = 'msix/Test-AssertMsixPackage.ps1'; Subject = 'msix-package-tests' }
        @{ Path = 'tools/audit/Test-AuditTools.ps1'; Subject = 'audit-tools' }
        @{ Path = 'tools/audit/Test-AuditVerdictGate.ps1'; Subject = 'audit-verdict-gate' }
    )
    $tryOpen = [regex]::new('(?m)^try \{[ \t]*\r?$')
    foreach ($runner in $runners) {
        $source = [System.IO.File]::ReadAllText((Join-Path $repo $runner.Path)).Replace('$PSScriptRoot', '$env:SP0239_PROBE_ROOT')
        Check ($tryOpen.IsMatch($source)) "$($runner.Path): the fixture found the try block to inject into"
        $env:SP0239_PROBE_ROOT = Join-Path $repo (Split-Path $runner.Path -Parent)
        foreach ($variant in 'plain', 'earlier-failure') {
            $inject = if ($variant -eq 'plain') { "throw 'synthetic setup failure'" } else { "`$failures.Add('synthetic earlier failure'); throw 'synthetic setup failure'" }
            $probe = Join-Path $work "probe-$variant-$(Split-Path $runner.Path -Leaf)"
            Write-File $probe ($tryOpen.Replace($source, "try {`n    $inject", 1))
            $r = Invoke-Script $probe
            if ($variant -eq 'plain') {
                Expect $r 2 "^$($runner.Subject): COULD NOT VERIFY \(synthetic setup failure\)$" "$($runner.Path): a throw inside the run could not be verified"
            }
            else {
                Expect $r 1 "^$($runner.Subject): FAIL \(1 of 1" "$($runner.Path): a check that failed before the throw outranks could-not-verify"
            }
        }
    }
    Remove-Item Env:SP0239_PROBE_ROOT -ErrorAction SilentlyContinue

    # --- scripts/check-docs.ps1 --------------------------------------------------------------------------------------
    function New-DocsTree([string] $Name) {
        $tree = Join-Path $work $Name
        Write-File (Join-Path $tree 'DOCUMENT_REGISTRY.jsonl') '{"path":"README.md","topic":"fixture","area":"fixture","triggers":["fixture"],"role":"source"}'
        Write-File (Join-Path $tree 'README.md') "# Fixture`n"
        New-Item -ItemType Directory -Path (Join-Path $tree 'scripts') -Force | Out-Null
        foreach ($name in 'check-docs.ps1', 'SourceFingerprint.ps1') {
            Copy-Item -LiteralPath (Join-Path $repo "scripts/$name") -Destination (Join-Path $tree "scripts/$name")
        }
        & git -C $tree init -q 2>&1 | Out-Null
        return $tree
    }
    $tree = New-DocsTree 'docs-clean'
    Expect (Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1')) 0 '^docs-quality: PASS \(' 'check-docs.ps1: a clean tree passes'

    $tree = New-DocsTree 'docs-finding'
    Write-File (Join-Path $tree 'extra.md') "# Undeclared`n"
    Expect (Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1')) 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1: an undeclared document fails'

    $tree = New-DocsTree 'docs-locked'
    $r = Invoke-Locked @((Join-Path $tree 'README.md')) { Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1') }
    Expect $r 2 '^docs-quality: COULD NOT VERIFY \(' 'check-docs.ps1: a document that cannot be read could not be verified'

    $tree = New-DocsTree 'docs-finding-then-locked'
    Write-File (Join-Path $tree 'extra.md') "# Undeclared`n"
    $r = Invoke-Locked @((Join-Path $tree 'README.md')) { Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1') }
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1: a finding recorded before the stop outranks could-not-verify'

    # --- scripts/check-docs.ps1: the portal contribution, terms and freshness (SP-0239 P5) ---------------------------
    # The "ru" page is Latin transliteration so this file stays ASCII: the rule compares strings, not scripts. The
    # defect it reproduces is the real one - a case ending inside the marks ("Nastroykakh" for the dictionary's
    # "Nastroyki"), which no dictionary holds.
    . (Join-Path $repo 'scripts/SourceFingerprint.ps1')
    $open = [string] [char] 0x00AB
    $close = [string] [char] 0x00BB
    $english = "# Guide`n`nOpen **Settings**, then **Library**.`n"
    $russian = "# Rukovodstvo`n`nOtkroyte **Nastroyki**, zatem $($open)Biblioteka$($close).`n"
    function New-Dictionary([hashtable] $Strings) {
        $rows = foreach ($key in $Strings.Keys) { "<sys:String x:Key=`"$key`">$($Strings[$key])</sys:String>" }
        return '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:sys="clr-namespace:System;assembly=mscorlib">' + ($rows -join '') + '</ResourceDictionary>'
    }
    # $Fingerprint: '' records the real fingerprint of the English page, 'none' omits the field, anything else is recorded.
    function New-PortalTree([string] $Name, [string] $Russian = $script:russian, [string] $Fingerprint = '') {
        $tree = New-DocsTree $Name
        Write-File (Join-Path $tree 'docs/portal/guide.md') $script:english
        Write-File (Join-Path $tree 'docs/portal/guide.ru.md') $Russian
        Write-File (Join-Path $tree 'src/StreamsPlayer.App/Localization.en.xaml') (New-Dictionary @{ Settings = 'Settings'; SettingsLibrary = 'Library' })
        Write-File (Join-Path $tree 'src/StreamsPlayer.App/Localization.ru.xaml') (New-Dictionary @{ Settings = 'Nastroyki'; SettingsLibrary = 'Biblioteka' })
        $recorded = if ($Fingerprint -eq '') { Get-SourceFingerprint (Join-Path $tree 'docs/portal/guide.md') } else { $Fingerprint }
        $mirror = if ($Fingerprint -eq 'none') { '' } else { ",`"mirrorOf`":`"docs/portal/guide.md`",`"mirrorOfHash`":`"$recorded`"" }
        $row = '{{"path":"{0}","topic":"fixture","area":"fixture","triggers":["fixture"],"role":"source"{1}}}'
        Write-File (Join-Path $tree 'DOCUMENT_REGISTRY.jsonl') ((($row -f 'README.md', ''), ($row -f 'docs/portal/guide.md', ''), ($row -f 'docs/portal/guide.ru.md', $mirror)) -join "`n")
        return $tree
    }
    $docsGate = 'scripts/check-docs.ps1'

    $tree = New-PortalTree 'portal-clean'
    Expect (Invoke-Script (Join-Path $tree $docsGate)) 0 '^docs-quality: PASS \(.*4 control names, 1 mirrors\)$' 'check-docs.ps1 terms: names that are strings of the dictionary, bold and guillemet, pass'

    $tree = New-PortalTree 'portal-inflected-bold' ($russian.Replace('**Nastroyki**', '**Nastroykakh**'))
    $r = Invoke-Script (Join-Path $tree $docsGate)
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 terms: a bold name that is no dictionary string fails'
    Check ($r.Text -match "\[terms\] docs/portal/guide\.ru\.md:3 - 'Nastroykakh' is no string of Localization\.ru\.xaml") "check-docs.ps1 terms: the finding names the page, the line and the name (expected: [terms] guide.ru.md:3 'Nastroykakh' | actual: '$($r.Text)')"

    $tree = New-PortalTree 'portal-inflected-guillemet' ($russian.Replace("$($open)Biblioteka$($close)", "$($open)Bibliotekye$($close)"))
    $r = Invoke-Script (Join-Path $tree $docsGate)
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 terms: a guillemet name that is no dictionary string fails'
    Check ($r.Text -match "\[terms\] docs/portal/guide\.ru\.md:3 - 'Bibliotekye'") "check-docs.ps1 terms: the guillemet finding names the name (expected: [terms] 'Bibliotekye' | actual: '$($r.Text)')"

    $tree = New-PortalTree 'portal-no-dictionary'
    Remove-Item -LiteralPath (Join-Path $tree 'src/StreamsPlayer.App/Localization.ru.xaml')
    Expect (Invoke-Script (Join-Path $tree $docsGate)) 2 '^docs-quality: COULD NOT VERIFY \(no src/StreamsPlayer\.App/Localization\.ru\.xaml' 'check-docs.ps1 terms: a page with no dictionary to judge it against could not be verified'

    $tree = New-PortalTree 'portal-stale'
    Write-File (Join-Path $tree 'docs/portal/guide.md') ($english + "`nA new sentence in the English original.`n")
    $r = Invoke-Script (Join-Path $tree $docsGate)
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 freshness: a changed English source with an unchanged mirror fingerprint fails'
    Check ($r.Text -match '\[freshness\] docs/portal/guide\.ru\.md - docs/portal/guide\.md is sha256:[0-9a-f]{16} but the mirror was rebuilt from sha256:') "check-docs.ps1 freshness: the finding names both fingerprints (expected: [freshness] .. is sha256:<16 hex> but the mirror was rebuilt from sha256: | actual: '$($r.Text)')"

    $tree = New-PortalTree 'portal-line-endings'
    Write-File (Join-Path $tree 'docs/portal/guide.md') $english.Replace("`n", "`r`n")
    Expect (Invoke-Script (Join-Path $tree $docsGate)) 0 '^docs-quality: PASS \(' 'check-docs.ps1 freshness: a checkout with CRLF line endings does not change the fingerprint'

    $tree = New-PortalTree 'portal-no-fingerprint' $russian 'none'
    $r = Invoke-Script (Join-Path $tree $docsGate)
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 freshness: a portal mirror that records no source fingerprint fails'
    Check ($r.Text -match '\[freshness\] docs/portal/guide\.ru\.md - .* has no mirrorOf') "check-docs.ps1 freshness: the finding says the mirror has no mirrorOf (expected: has no mirrorOf | actual: '$($r.Text)')"

    $tree = New-PortalTree 'portal-bad-fingerprint' $russian 'sha256:XYZ'
    Expect (Invoke-Script (Join-Path $tree $docsGate)) 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 freshness: a malformed fingerprint fails'

    # --- scripts/check-help.ps1 --------------------------------------------------------------------------------------
    $helpPage = '<!doctype html><html lang="{0}" dir="ltr"><head>{1}</head><body><h1>x</h1><p class="lead">x</p><ul class="related"></ul><footer class="help-foot"></footer></body></html>'
    $referrer = '<meta name="referrer" content="no-referrer">'
    function New-HelpTree([string] $Name) {
        $tree = Join-Path $work $Name
        Write-File (Join-Path $tree 'help/help-map.json') '{"sourceLocale":"en","index":"index","surfaces":[]}'
        Write-File (Join-Path $tree 'help/templates/help.css') 'layer'
        Write-File (Join-Path $tree 'docs/assets/sza-kit.css') 'kit'
        Write-File (Join-Path $tree 'docs/help/help.css') 'layer'
        Write-File (Join-Path $tree 'docs/help/sza-kit.css') 'kit'
        Write-File (Join-Path $tree 'docs/help/en/index.html') ($script:helpPage -f 'en', $script:referrer)
        return $tree
    }
    $checkHelp = Join-Path $repo 'scripts/check-help.ps1'
    $tree = New-HelpTree 'help-clean'
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 0 '^help: PASS \(' 'check-help.ps1: a minimal help set passes'

    $tree = New-HelpTree 'help-no-kit'
    Remove-Item -LiteralPath (Join-Path $tree 'docs/assets/sza-kit.css')
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 2 '^help: COULD NOT VERIFY \(no .*sza-kit\.css' 'check-help.ps1: a missing source kit could not be verified'

    $tree = New-HelpTree 'help-no-layer'
    Remove-Item -LiteralPath (Join-Path $tree 'help/templates/help.css')
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 2 '^help: COULD NOT VERIFY \(no .*help\.css' 'check-help.ps1: a missing source layer could not be verified'

    $tree = New-HelpTree 'help-defect'
    Write-File (Join-Path $tree 'docs/help/en/index.html') ($helpPage -f 'en', '')
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 1 '^help: FAIL \(1 problem\(s\)' 'check-help.ps1: a page that does not withhold the referrer fails'

    $tree = New-HelpTree 'help-locked'
    $r = Invoke-Locked @((Join-Path $tree 'docs/help/en/index.html')) { Invoke-Script $checkHelp @('-Root', $tree) }
    Expect $r 2 '^help: COULD NOT VERIFY \(' 'check-help.ps1: a page that cannot be read could not be verified'

    $tree = New-HelpTree 'help-defect-then-locked'
    Write-File (Join-Path $tree 'docs/help/de/index.html') ($helpPage -f 'de', '')
    $r = Invoke-Locked @((Join-Path $tree 'docs/help/en/index.html')) { Invoke-Script $checkHelp @('-Root', $tree) }
    Expect $r 1 '^help: FAIL \(1 problem\(s\)\)$' 'check-help.ps1: a problem recorded before the stop outranks could-not-verify'

    # SP-0239 P5: the index of every locale links every written page of that locale. One written page, `main`, on the
    # picture baseline so that it owes no screenshot; the second locale ("de") is the one whose index forgets it.
    $mainPage = '<!doctype html><html lang="{0}" dir="ltr"><head>{1}</head><body><h1>x</h1><p class="lead">x</p><h2>Order</h2><p>none</p><div class="elements"></div><ul class="related"></ul><footer class="help-foot"></footer></body></html>'
    $indexPage = '<!doctype html><html lang="{0}" dir="ltr"><head>{1}</head><body><h1>x</h1><p class="lead">x</p><ul class="related">{2}</ul><footer class="help-foot"></footer></body></html>'
    function New-IndexedHelpTree([string] $Name, [string] $GermanIndexLinks) {
        $tree = New-HelpTree $Name
        Write-File (Join-Path $tree 'help/help-map.json') '{"sourceLocale":"en","index":"index","surfaces":[{"id":"main","form":"page","help":"main"}]}'
        Write-File (Join-Path $tree 'help/help-baseline.txt') "shot main`n"
        $link = '<li><a href="main.html">m</a></li>'
        Write-File (Join-Path $tree 'docs/help/en/index.html') ($script:indexPage -f 'en', $script:referrer, $link)
        Write-File (Join-Path $tree 'docs/help/en/main.html') ($script:mainPage -f 'en', $script:referrer)
        Write-File (Join-Path $tree 'docs/help/de/index.html') ($script:indexPage -f 'de', $script:referrer, $GermanIndexLinks)
        Write-File (Join-Path $tree 'docs/help/de/main.html') ($script:mainPage -f 'de', $script:referrer)
        return $tree
    }
    $tree = New-IndexedHelpTree 'help-index-complete' '<li><a href="main.html">m</a></li>'
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 0 '^help: PASS \(4 pages in 2 locales' 'check-help.ps1 index: every index links the written page of its locale'

    $tree = New-IndexedHelpTree 'help-index-incomplete' ''
    $r = Invoke-Script $checkHelp @('-Root', $tree)
    Expect $r 1 '^help: FAIL \(1 problem\(s\); 4 pages in 2 locales' 'check-help.ps1 index: an index that omits a written page fails, once, for its own locale'
    Check ($r.Text -match 'FAIL  index: de/index\.html does not link the page main\.html' -and $r.Text -notmatch 'index: en/') "check-help.ps1 index: the failure names the locale and the page (expected: index: de/index.html does not link the page main.html | actual: '$($r.Text)')"

    $tree = New-IndexedHelpTree 'help-index-foreign-link' '<li><a href="https://example.test/main.html">m</a><a href="../en/main.html">m</a></li>'
    $r = Invoke-Script $checkHelp @('-Root', $tree)
    Check ($r.Exit -eq 1 -and $r.Text -match 'FAIL  index: de/index\.html does not link the page main\.html') "check-help.ps1 index: a link to another origin or another locale is not a link to the page (expected: exit 1 and the index finding for de | actual: exit $($r.Exit), '$($r.Text)')"

    # --- tools/help/build-help.ps1 -----------------------------------------------------------------------------------
    function New-HelpBuildTree([string] $Name) {
        $tree = Join-Path $work $Name
        New-Item -ItemType Directory -Path (Join-Path $tree 'tools/help') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo 'tools/help/build-help.ps1') -Destination (Join-Path $tree 'tools/help/build-help.ps1')
        New-Item -ItemType Directory -Path (Join-Path $tree 'scripts') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo 'scripts/SourceFingerprint.ps1') -Destination (Join-Path $tree 'scripts/SourceFingerprint.ps1')
        Write-File (Join-Path $tree 'help/help-map.json') '{"sourceLocale":"en","index":"index","surfaces":[]}'
        Write-File (Join-Path $tree 'help/stamp.json') '{"version":"1.0","date":"2026-01-01"}'
        $chrome = [ordered]@{
            indexTitle = 'Help'; kicker = 'k'; indexLead = 'lead'; indexAskHeading = 'ask'; indexAsk = @('a')
            indexWindowsHeading = 'windows'; relatedHeading = 'related'; indexRelated = @(); site = @{}
            pillWindow = 'w'; pillSite = 's'; footerWritten = '{product} {version} {date}'; indexLink = 'index'; support = 'support'
        }
        Write-File (Join-Path $tree 'help/content/en/_chrome.json') ($chrome | ConvertTo-Json -Depth 5)
        Write-File (Join-Path $tree 'help/templates/help.css') 'layer'
        Write-File (Join-Path $tree 'docs/assets/sza-kit.css') 'kit'
        Write-File (Join-Path $tree 'src/StreamsPlayer.App/Localization.en.xaml') '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:sys="clr-namespace:System;assembly=mscorlib"><sys:String x:Key="ProductName">Fixture</sys:String></ResourceDictionary>'
        foreach ($frame in 'mark-24.png', 'mark-48.png') { Write-Bytes (Join-Path $tree "assets/exchange-mark/png/$frame") ([byte[]]@(1, 2, 3)) }
        return $tree
    }
    $tree = New-HelpBuildTree 'build-help'
    $buildHelp = Join-Path $tree 'tools/help/build-help.ps1'
    Expect (Invoke-Script $buildHelp) 0 '^help-build: PASS \(.*written\)$' 'build-help.ps1: the generator writes the set'
    Expect (Invoke-Script $buildHelp @('-Check')) 0 '^help-build: PASS \(.*in sync\)$' 'build-help.ps1: -Check passes on what the generator wrote'
    Add-Content -LiteralPath (Join-Path $tree 'docs/help/en/index.html') -Value '<!-- stale -->'
    Expect (Invoke-Script $buildHelp @('-Check')) 1 '^help-build: FAIL \(1 file\(s\) out of date' 'build-help.ps1: -Check fails a stale page'
    Write-File (Join-Path $tree 'help/stamp.json') '{ not json'
    $r = Invoke-Script $buildHelp @('-Check')
    Expect $r 2 '^help-build: COULD NOT VERIFY \(.*stamp\.json does not parse' 'build-help.ps1: -Check could not verify a source that does not parse'
    Write-File (Join-Path $tree 'help/stamp.json') '{"version":"1.0","date":"2026-01-01"}'
    Write-File (Join-Path $tree 'help/content/en/_chrome.json') '{"indexTitle":"Help"}'
    Expect (Invoke-Script $buildHelp @('-Check')) 2 '^help-build: COULD NOT VERIFY \(' 'build-help.ps1: -Check could not verify a copy the renderer throws on'
    $r = Invoke-Script $buildHelp
    Check ($r.Exit -ne 0 -and $r.Text -notmatch 'help-build: COULD NOT VERIFY') "build-help.ps1: a plain generator run keeps its exception (expected: non-zero exit, no verdict line | actual: exit $($r.Exit), '$($r.Last)')"

    # SP-0239 P5: a translation records the fingerprint of the English copy it was rebuilt from. The tree gets a second
    # locale whose chrome copy is the English one plus "sourceHash"; the generator is run again after each change of the
    # English copy so that the pages are in sync and the stale translation is the only thing -Check can find.
    $tree = New-HelpBuildTree 'build-help-fresh'
    $buildHelp = Join-Path $tree 'tools/help/build-help.ps1'
    $englishChrome = Join-Path $tree 'help/content/en/_chrome.json'
    $translatedChrome = Join-Path $tree 'help/content/ru/_chrome.json'
    Copy-Item -LiteralPath (Join-Path $tree 'src/StreamsPlayer.App/Localization.en.xaml') -Destination (Join-Path $tree 'src/StreamsPlayer.App/Localization.ru.xaml')
    function Set-TranslatedChrome([string] $Fingerprint) {
        $copy = Get-Content -LiteralPath $script:englishChrome -Raw | ConvertFrom-Json -AsHashtable
        if ($Fingerprint) { $copy['sourceHash'] = $Fingerprint }
        Write-File $script:translatedChrome ($copy | ConvertTo-Json -Depth 5)
    }
    . (Join-Path $repo 'scripts/SourceFingerprint.ps1')
    Set-TranslatedChrome (Get-SourceFingerprint $englishChrome)
    Expect (Invoke-Script $buildHelp) 0 '^help-build: PASS \(2 locales' 'build-help.ps1 freshness: the generator writes both locales'
    Expect (Invoke-Script $buildHelp @('-Check')) 0 '^help-build: PASS \(2 locales.*in sync\)$' 'build-help.ps1 freshness: a translation that records the current English fingerprint passes'

    $changed = Get-Content -LiteralPath $englishChrome -Raw | ConvertFrom-Json -AsHashtable
    $changed['indexTitle'] = 'Help centre'
    Write-File $englishChrome ($changed | ConvertTo-Json -Depth 5)
    Invoke-Script $buildHelp | Out-Null
    $r = Invoke-Script $buildHelp @('-Check')
    Expect $r 1 '^help-build: FAIL \(1 translation\(s\) older than their English copy; rebuild them and record its fingerprint\)$' 'build-help.ps1 freshness: a changed English copy with an unchanged translation fingerprint fails'
    Check ($r.Text -match 'help/content/en/_chrome\.json is sha256:[0-9a-f]{16} but help/content/ru/_chrome\.json was rebuilt from sha256:') "build-help.ps1 freshness: the failure names both files and both fingerprints (expected: en/_chrome.json is sha256:.. but ru/_chrome.json was rebuilt from sha256: | actual: '$($r.Text)')"

    Set-TranslatedChrome (Get-SourceFingerprint $englishChrome)
    Invoke-Script $buildHelp | Out-Null
    Expect (Invoke-Script $buildHelp @('-Check')) 0 '^help-build: PASS \(2 locales.*in sync\)$' 'build-help.ps1 freshness: rebuilding the translation and recording the new fingerprint clears the failure'

    Set-TranslatedChrome ''
    Invoke-Script $buildHelp | Out-Null
    $r = Invoke-Script $buildHelp @('-Check')
    Expect $r 1 '^help-build: FAIL \(1 translation\(s\) older than their English copy' 'build-help.ps1 freshness: a translation that records no fingerprint fails'
    Check ($r.Text -match 'help/content/ru/_chrome\.json has no sourceHash of the form sha256:<16 hex>') "build-help.ps1 freshness: the failure says the translation has no sourceHash (expected: has no sourceHash of the form sha256:<16 hex> | actual: '$($r.Text)')"
    Expect (Invoke-Script $buildHelp) 0 '^help-build: PASS \(2 locales.*written\)$' 'build-help.ps1 freshness: the plain generator run does not look at the fingerprint'

    # --- tools/site/build-site.ps1 -----------------------------------------------------------------------------------
    $tree = Join-Path $work 'build-site'
    New-Item -ItemType Directory -Path (Join-Path $tree 'tools/site') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'tools/InterfaceLanguages.ps1') -Destination (Join-Path $tree 'tools/InterfaceLanguages.ps1')
    foreach ($name in 'build-site', 'HeldAddresses', 'SiteFacts', 'SiteDecks', 'SiteDiscovery', 'SiteNotFound', 'ReleaseNotes') {
        Copy-Item -LiteralPath (Join-Path $repo "tools/site/$name.ps1") -Destination (Join-Path $tree "tools/site/$name.ps1")
    }
    $buildSite = Join-Path $tree 'tools/site/build-site.ps1'
    Expect (Invoke-Script $buildSite @('-Check')) 2 '^site-sync: COULD NOT VERIFY \(' 'build-site.ps1: -Check could not verify a missing provenance file'
    $r = Invoke-Script $buildSite
    Check ($r.Exit -ne 0 -and $r.Text -notmatch 'site-sync:') "build-site.ps1: a plain generator run keeps its exception (expected: non-zero exit, no verdict line | actual: exit $($r.Exit), '$($r.Last)')"
    $pin = 'A' * 64
    Write-File (Join-Path $tree 'tools/site/kit-provenance.txt') "sza-kit.css  $pin`nwave-particles.js  $pin`n"
    Write-File (Join-Path $tree 'docs/assets/sza-kit.css') 'not the pinned kit'
    Write-File (Join-Path $tree 'docs/assets/wave-particles.js') 'not the pinned script'
    Expect (Invoke-Script $buildSite @('-Check')) 1 '^site-sync: FAIL \(docs/assets/sza-kit\.css is not the pinned file' 'build-site.ps1: -Check fails a vendored file that differs from its pin'

    # --- tools/site/make-demo.ps1 ------------------------------------------------------------------------------------
    function New-Box([string] $Type, [byte[]] $Payload) {
        $size = 8 + $Payload.Length
        $bytes = [System.Collections.Generic.List[byte]]::new()
        $bytes.AddRange([byte[]]@((($size -shr 24) -band 255), (($size -shr 16) -band 255), (($size -shr 8) -band 255), ($size -band 255)))
        $bytes.AddRange([System.Text.Encoding]::ASCII.GetBytes($Type))
        $bytes.AddRange($Payload)
        return , $bytes.ToArray()
    }
    function Join-Bytes([byte[][]] $Parts) {
        $all = [System.Collections.Generic.List[byte]]::new()
        foreach ($part in $Parts) { $all.AddRange($part) }
        return , $all.ToArray()
    }
    # ftyp, moov (mvhd: timescale 1000, duration 5000; one trak whose hdlr is vide), mdat: a silent 5 s file, moov first.
    $mvhd = New-Box 'mvhd' ([byte[]]@(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0xE8, 0, 0, 0x13, 0x88))
    $hdlr = New-Box 'hdlr' ([byte[]]@(0, 0, 0, 0, 0, 0, 0, 0) + [System.Text.Encoding]::ASCII.GetBytes('vide'))
    $trak = New-Box 'trak' (New-Box 'mdia' $hdlr)
    $mp4 = Join-Bytes @((New-Box 'ftyp' ([byte[]]@(105, 115, 111, 109, 0, 0, 0, 0))), (New-Box 'moov' (Join-Bytes @($mvhd, $trak))), (New-Box 'mdat' ([byte[]]@(0, 0, 0, 0))))
    $gif = [byte[]]@(71, 73, 70, 56, 57, 97, 0xD0, 0x02, 0x00, 0x01, 0, 0, 0, 0x3B)    # GIF89a, 720 px wide, trailer
    $poster = [byte[]]@(0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9)                              # SOI ... EOI
    function New-DemoFolder([string] $Name, [string[]] $Leave = @()) {
        $folder = Join-Path $work $Name
        $files = [ordered]@{ 'streamsplayer-demo.mp4' = $script:mp4; 'streamsplayer-demo.gif' = $script:gif; 'streamsplayer-demo-poster.jpg' = $script:poster }
        foreach ($file in $files.Keys) { if ($Leave -notcontains $file) { Write-Bytes (Join-Path $folder $file) $files[$file] } }
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        return $folder
    }
    $makeDemo = Join-Path $repo 'tools/site/make-demo.ps1'
    $folder = New-DemoFolder 'demo-good'
    Expect (Invoke-Script $makeDemo @('-Check', '-OutDir', $folder)) 0 '^demo-capture: PASS$' 'make-demo.ps1: -Check passes three good files'
    $folder = New-DemoFolder 'demo-missing' @('streamsplayer-demo.mp4')
    Expect (Invoke-Script $makeDemo @('-Check', '-OutDir', $folder)) 1 '^demo-capture: FAIL \(0 finding\(s\), 1 missing\)$' 'make-demo.ps1: -Check fails a missing file (it was exit 3 and MISSING)'
    $folder = New-DemoFolder 'demo-locked'
    $r = Invoke-Locked @((Join-Path $folder 'streamsplayer-demo.gif')) { Invoke-Script $makeDemo @('-Check', '-OutDir', $folder) }
    Expect $r 2 '^demo-capture: COULD NOT VERIFY \(streamsplayer-demo\.gif\)$' 'make-demo.ps1: -Check could not verify a file that cannot be read'

    # --- scripts/check.ps1, over stub children and a stub dotnet -----------------------------------------------------
    $checkText = Get-Content -LiteralPath (Join-Path $repo 'scripts/check.ps1') -Raw
    $children = @([regex]::Matches($checkText, "Join-Path \`$root '([^']+\.ps1)'") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    Check ($children.Count -gt 10 -and $children -contains 'tools/site/make-demo.ps1') "check.ps1: the fixture found its child scripts (expected: more than 10, make-demo among them | actual: $($children.Count))"
    function New-CheckTree([string] $Name, [hashtable] $Exits = @{}, [switch] $WithDemo) {
        $tree = Join-Path $work $Name
        New-Item -ItemType Directory -Path (Join-Path $tree 'scripts') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $script:repo 'scripts/check.ps1') -Destination (Join-Path $tree 'scripts/check.ps1')
        foreach ($child in $script:children) { Write-File (Join-Path $tree $child) "exit $(if ($Exits.ContainsKey($child)) { $Exits[$child] } else { 0 })`n" }
        if ($WithDemo) { Write-File (Join-Path $tree 'docs/assets/demo/streamsplayer-demo.mp4') 'stub' }
        Write-File (Join-Path $tree 'shim/dotnet.cmd') "@exit /b 0`r`n"
        return $tree
    }
    function Invoke-Check([string] $Tree) {
        return Invoke-Script (Join-Path $Tree 'scripts/check.ps1') @() ("$(Join-Path $Tree 'shim')$([System.IO.Path]::PathSeparator)$env:PATH")
    }
    $tree = New-CheckTree 'check-pass'
    Expect (Invoke-Check $tree) 0 "^check: PASS \($($children.Count - 1) checks, 1 skipped\)$" 'check.ps1: all children pass and the skipped demo check is counted'
    $tree = New-CheckTree 'check-pass-demo' -WithDemo
    Expect (Invoke-Check $tree) 0 "^check: PASS \($($children.Count) checks, 0 skipped\)$" 'check.ps1: all children pass with the demo check run'
    $tree = New-CheckTree 'check-busy' @{ 'scripts/check-docs.ps1' = 4 }
    Expect (Invoke-Check $tree) 2 '^check: COULD NOT VERIFY \(docs-quality; 1 skipped\)$' 'check.ps1: a child that answers 4 reads as 2'
    $tree = New-CheckTree 'check-busy-and-failed' @{ 'scripts/check-docs.ps1' = 4; 'tools/site/build-site.ps1' = 1 }
    Expect (Invoke-Check $tree) 1 '^check: FAIL \(site-sync; 1 skipped\)$' 'check.ps1: a defect outranks a child that answers 4'
    $tree = New-CheckTree 'check-odd-code' @{ 'scripts/check-docs.ps1' = 7 }
    Expect (Invoke-Check $tree) 1 '^check: FAIL \(docs-quality; 1 skipped\)$' 'check.ps1: a child code outside 0 to 4 is read as a defect (rule 2.14)'
    $tree = New-CheckTree 'check-no-dotnet'
    $emptyPath = Join-Path $work 'empty-path'
    New-Item -ItemType Directory -Path $emptyPath -Force | Out-Null
    Expect (Invoke-Script (Join-Path $tree 'scripts/check.ps1') @() $emptyPath) 2 '^check: COULD NOT VERIFY \(.*dotnet' 'check.ps1: a missing dotnet could not be verified instead of throwing'
}
catch {
    # The fixture itself could not run (a tool it needs is missing, a temp folder was refused): nothing was judged past
    # this point. A case that already failed still outranks it (CHECK-VERDICT 2.14).
    $reason = $_.Exception.Message -replace '\s+', ' '
    Write-Output "check-verdict-scripts: the run stopped early: $reason"
    if ($failures.Count -eq 0) { "check-verdict-scripts: COULD NOT VERIFY run=$run skipped=0"; exit 2 }
}
finally {
    Remove-Item Env:SP0239_PROBE_ROOT -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $work) {
        Get-ChildItem -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Attributes = 'Normal' } catch { } }
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -eq 0) { "check-verdict-scripts: PASS run=$run skipped=0"; exit 0 }
"check-verdict-scripts: FAIL run=$run skipped=0"
exit 1
