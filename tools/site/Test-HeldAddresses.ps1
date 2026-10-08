#requires -Version 7.0
<#
.SYNOPSIS
    SP-0193: fixture tests for the held-site-address gate (HeldAddresses.ps1).

.DESCRIPTION
    Builds a throwaway git repository with a few holder files and a held-address list, then runs the real
    gate against it:

      passes     a consistent list; an address ending a sentence, inside a markdown link or a quoted string; a
                 lookalike path (.../StreamsPlayerX/..) on the same host; a file named in the ignore rows; the
                 generator's own output under docs/
      fails      an entry the generator does not emit; an entry with a fragment the page lacks; a holder that no
                 longer holds its address; a holder that is not tracked; an address a tracked file holds that
                 the list omits; a second spelling of the base; an entry on another host; a duplicate entry; an
                 ignore row with no reason; a missing or unparseable list

    Exit 0 when every check passes, 1 otherwise. The last line is `held-addresses: PASS|FAIL (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/HeldAddresses.ps1"

$base = 'https://example.test/Site/'
$generated = @('index.html', 'privacy.html', 'ru/index.html', 'ru/privacy.html', 'site.js', 'sitemap.xml', 'robots.txt')

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("held-addresses-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null

function Write-Fixture([string] $Path, [string] $Content) {
    $full = Join-Path $fixture $Path
    New-Item -ItemType Directory -Force -Path (Split-Path $full -Parent) | Out-Null
    [System.IO.File]::WriteAllText($full, $Content)
}
function Write-List([object[]] $Addresses, [object[]] $Ignore = @()) {
    $document = [ordered]@{ ignore = $Ignore; addresses = $Addresses }
    Write-Fixture 'tools/site/held-addresses.json' ($document | ConvertTo-Json -Depth 6)
}
function Get-Problems { return ,@(Get-HeldAddressProblem -Root $fixture -BaseUrl $base -GeneratedFile $generated) }
function Has([string[]] $Problems, [string] $Pattern) { return @($Problems | Where-Object { $_ -match $Pattern }).Count -gt 0 }

try {
    & git -C $fixture init -q
    Write-Fixture 'docs/index.html' '<h1 id="install">x</h1> <a href="https://example.test/Site/privacy.html">self</a>'
    Write-Fixture 'docs/privacy.html' '<p>x</p>'
    Write-Fixture 'docs/sitemap.xml' '<loc>https://example.test/Site/</loc>'
    Write-Fixture 'README.md' @'
Site: https://example.test/Site/. Privacy: [policy](https://example.test/Site/privacy.html), or "https://example.test/Site/ru/".
A lookalike on the same host, https://example.test/SiteX/other.html, is some other project's page.
'@
    Write-Fixture 'listing.txt' "Website: https://example.test/Site/#install`n"
    Write-Fixture 'frozen/old.yaml' "PackageUrl: https://example.test/Site/retired.html`n"
    Write-List @(
        @{ url = 'https://example.test/Site/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/privacy.html'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/ru/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/#install'; heldBy = @('listing.txt') }
    ) @(@{ path = 'frozen'; why = 'pinned copy of a submitted manifest' })
    & git -C $fixture add -A | Out-Null

    $problems = Get-Problems
    Check ($problems.Count -eq 0) "consistent list passes (got: $($problems -join ' | '))"

    # --- resolve
    Write-List @(
        @{ url = 'https://example.test/Site/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/privacy.html'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/ru/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/#install'; heldBy = @('listing.txt') }
        @{ url = 'https://example.test/Site/gone.html'; heldBy = @('README.md') }
    ) @(@{ path = 'frozen'; why = 'x' })
    $problems = Get-Problems
    Check (Has $problems "gone\.html' does not resolve") 'an entry the generator does not emit fails'

    Write-Fixture 'listing.txt' "Website: https://example.test/Site/#missing`n"
    Write-List @(
        @{ url = 'https://example.test/Site/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/privacy.html'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/ru/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/#missing'; heldBy = @('listing.txt') }
    ) @(@{ path = 'frozen'; why = 'x' })
    $problems = Get-Problems
    Check (Has $problems 'no id="missing"') 'a fragment the page lacks fails'
    Write-Fixture 'listing.txt' "Website: https://example.test/Site/#install`n"

    # --- holder
    Write-List @(
        @{ url = 'https://example.test/Site/'; heldBy = @('README.md', 'listing.txt') }
        @{ url = 'https://example.test/Site/privacy.html'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/ru/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/#install'; heldBy = @('listing.txt') }
    ) @(@{ path = 'frozen'; why = 'x' })
    $problems = Get-Problems
    Check (Has $problems "listing\.txt holds 'https://example\.test/Site/', but it no longer does") 'a holder that no longer holds the address fails'

    Write-List @(
        @{ url = 'https://example.test/Site/'; heldBy = @('README.md', 'nowhere.md') }
        @{ url = 'https://example.test/Site/privacy.html'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/ru/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/#install'; heldBy = @('listing.txt') }
    ) @(@{ path = 'frozen'; why = 'x' })
    $problems = Get-Problems
    Check (Has $problems 'nowhere\.md is not a tracked file') 'a holder that is not tracked fails'

    # --- complete
    Write-Fixture 'extra.md' "See https://example.test/Site/privacy.html for details.`n"
    & git -C $fixture add -A | Out-Null
    Write-List @(
        @{ url = 'https://example.test/Site/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/privacy.html'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/ru/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/#install'; heldBy = @('listing.txt') }
    ) @(@{ path = 'frozen'; why = 'x' })
    $problems = Get-Problems
    Check (Has $problems "extra\.md holds site address 'https://example\.test/Site/privacy\.html'") 'an address a tracked file holds and the list omits fails'
    & git -C $fixture rm -q -f extra.md

    Write-Fixture 'README.md' "Site: https://example.test/Site`n"
    $problems = Get-Problems
    Check (Has $problems 'README\.md holds site address') 'a second spelling of the base is reported, not guessed'
    Write-Fixture 'README.md' @'
Site: https://example.test/Site/. Privacy: [policy](https://example.test/Site/privacy.html), or "https://example.test/Site/ru/".
A lookalike on the same host, https://example.test/SiteX/other.html, is some other project's page.
'@

    # --- list shape
    Write-List @(
        @{ url = 'https://example.test/Site/'; heldBy = @('README.md') }
        @{ url = 'https://example.test/Site/'; heldBy = @('README.md') }
    )
    $problems = Get-Problems
    Check (Has $problems 'twice') 'a duplicate entry fails'

    Write-List @(@{ url = 'https://elsewhere.test/'; heldBy = @('README.md') })
    $problems = Get-Problems
    Check (Has $problems 'not an address of this site') 'an entry on another host fails'

    Write-List @(@{ url = 'https://example.test/Site/'; heldBy = @('README.md') }) @(@{ path = 'frozen'; why = '' })
    $problems = Get-Problems
    Check (Has $problems 'carries no reason') 'an ignore row without a reason fails'

    Write-Fixture 'tools/site/held-addresses.json' '{ not json'
    $problems = Get-Problems
    Check (Has $problems 'not valid JSON') 'an unparseable list fails'

    Remove-Item (Join-Path $fixture 'tools/site/held-addresses.json')
    $problems = Get-Problems
    Check (Has $problems 'does not exist') 'a missing list fails'
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) {
    Write-Output "held-addresses: FAIL ($($failures.Count) of $($passed + $failures.Count))"
    exit 1
}
Write-Output "held-addresses: PASS ($passed checks)"
exit 0
