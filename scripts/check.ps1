<#
    The release-parity gate: Release restore + build, then every check that judges the tree.

      tests         dotnet test over the solution
      docs-quality  scripts/check-docs.ps1 - document registry, coverage, links, style, offline (SP-0140)
      site-sync     tools/site/build-site.ps1 -Check - docs/ matches its copy decks (DOC-INTERNAL-QUALITY rule 4),
                    every held site address resolves (SITE-STRUCTURE rule 8, SP-0193), and the facts the copy states
                    and the pillars of POSITIONING.md agree with their sources (SITE-REPRESENTATION rules 1 to 3, SP-0198)
      held-addresses-test  tools/site/Test-HeldAddresses.ps1 - the held-address gate's own fixture tests
      site-facts-test      tools/site/Test-SiteFacts.ps1 - the site-facts and positioning gate's own fixture tests
      site-discovery-test  tools/site/Test-SiteDiscovery.ps1 - the structured data, sitemap, robots.txt and search-console
                           verification tags' own fixture tests, then the same questions put to the generated pages in
                           docs/ (footer links to What's new and Support, sitemap entries, one JSON-LD block per page;
                           structure only, never text) (SP-0039)
      release-notes-test   tools/site/Test-ReleaseNotes.ps1 - the release-notes reader and the What's new renderer's own
                           fixture tests, and the repository's notes folder read through them (SP-0039)
      msix-gate     msix/Test-AssertMsixPackage.ps1 - the Store package read-back refuses what it must (SP-0190)
      demo-check    tools/site/make-demo.ps1 -Check - the demo MP4, GIF and poster are all there, silent, short and within
                    their byte caps, read without ffmpeg (SP-0039); skipped, and not counted, while docs/assets/demo
                    holds no demo file, because nothing has been recorded yet

    Exit codes (CHECK-VERDICT): 0 passed, 1 a check failed, 2 a check could not verify. A failed restore or
    build stops the run (nothing after it can be judged); every later check runs even when an earlier one
    fails, and the last line names all of them:
        check: PASS (<n> checks)
        check: FAIL (<names>)
        check: CANNOT VERIFY (<names>)
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet restore StreamsPlayer.sln
    if ($LASTEXITCODE -ne 0) { Write-Host "check: restore failed (exit $LASTEXITCODE)"; Write-Host 'check: FAIL (restore)'; exit 1 }
    dotnet build StreamsPlayer.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { Write-Host "check: build failed (exit $LASTEXITCODE)"; Write-Host 'check: FAIL (build)'; exit 1 }

    $results = [ordered]@{}
    dotnet test StreamsPlayer.sln -c $Configuration --no-build --verbosity normal
    $results['tests'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'scripts/check-docs.ps1')
    $results['docs-quality'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/site/build-site.ps1') -Check
    $results['site-sync'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/site/Test-HeldAddresses.ps1')
    $results['held-addresses-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/site/Test-SiteFacts.ps1')
    $results['site-facts-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/site/Test-SiteDiscovery.ps1')
    $results['site-discovery-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/site/Test-ReleaseNotes.ps1')
    $results['release-notes-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'msix/Test-AssertMsixPackage.ps1')
    $results['msix-gate'] = $LASTEXITCODE
    # Run once the files exist (SP-0039 6.4). A folder holding only some of the three is a defect and is judged, so the
    # gate keys on "any demo file", not on all of them; the check itself answers 3 for a file of the set that is absent.
    $demoDir = Join-Path $root 'docs/assets/demo'
    if ((Test-Path -LiteralPath $demoDir -PathType Container) -and @(Get-ChildItem -LiteralPath $demoDir -File -Filter 'streamsplayer-demo*').Count -gt 0) {
        pwsh -NoProfile -File (Join-Path $root 'tools/site/make-demo.ps1') -Check
        $results['demo-check'] = $LASTEXITCODE
    }
    else { Write-Host 'check: demo-check skipped - docs/assets/demo holds no demo file yet (SP-0039)' }

    $failed = @($results.Keys | Where-Object { $results[$_] -ne 0 -and $results[$_] -ne 2 })
    $unverified = @($results.Keys | Where-Object { $results[$_] -eq 2 })
    foreach ($name in $results.Keys) { Write-Host ("check: {0} exit {1}" -f $name, $results[$name]) }
    if ($failed.Count) { Write-Host "check: FAIL ($($failed -join ', '))"; exit 1 }
    if ($unverified.Count) { Write-Host "check: CANNOT VERIFY ($($unverified -join ', '))"; exit 2 }
    Write-Host "check: PASS ($($results.Count) checks)"
    exit 0
}
finally { Pop-Location }
