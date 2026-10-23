<#
    The release-parity gate: Release restore + build, then every check that judges the tree.

      tests         dotnet test over the solution
      docs-quality  scripts/check-docs.ps1 - document registry, coverage, links, style, offline (SP-0140)
      site-sync     tools/site/build-site.ps1 -Check - docs/ matches its copy decks (DOC-INTERNAL-QUALITY rule 4),
                    every held site address resolves (SITE-STRUCTURE rule 8, SP-0193), and the facts the copy states
                    and the pillars of POSITIONING.md agree with their sources (SITE-REPRESENTATION rules 1 to 3, SP-0198)
      help-build    tools/help/build-help.ps1 -Check - docs/help matches the help map, the copy and the dictionaries
                    (CONTEXT-HELP rule 12, the pages are a render target; SP-0230)
      help          scripts/check-help.ps1 - the generated help pages hold CONTEXT-HELP's anatomy, language, no-request
                    and link rules in every authored locale; the windows against the map, the baseline and the anchors
                    are HelpGateTests in `tests` (SP-0230)
      held-addresses-test  tools/site/Test-HeldAddresses.ps1 - the held-address gate's own fixture tests
      site-facts-test      tools/site/Test-SiteFacts.ps1 - the site-facts and positioning gate's own fixture tests
      site-discovery-test  tools/site/Test-SiteDiscovery.ps1 - the structured data, sitemap, robots.txt and search-console
                           verification tags' own fixture tests, then the same questions put to the generated pages in
                           docs/ (footer links to What's new and Support, sitemap entries, one JSON-LD block per page;
                           structure only, never text) (SP-0039)
      release-notes-test   tools/site/Test-ReleaseNotes.ps1 - the release-notes reader and the What's new renderer's own
                           fixture tests, and the repository's notes folder read through them (SP-0039)
      msix-gate     msix/Test-AssertMsixPackage.ps1 - the Store package read-back refuses what it must (SP-0190)
      msix-manifest msix/Test-AppxManifest.ps1 - the committed manifest template packs under the Windows SDK's own
                    makeappx, from any tree state (build-msix.ps1 refuses a dirty tree, so it cannot be the
                    first to find a schema error) (SP-0238); COULD NOT VERIFY without the SDK
      existing-release-test scripts/Test-ExistingReleaseState.ps1 - complete publications skip rebuilding;
                            incomplete or invalid asset sets fail closed without replacement (SP-0184)
      audit-verdict-test tools/audit/Test-AuditVerdictGate.ps1 - the audit-verdict writer and gate's own
                            fixture tests; the gate that blocks a release must not be able to regress
                            unobserved (SP-0212 L1)
      audit-tools-test  tools/audit/Test-AuditTools.ps1 - the slicer, fan-out and summary tools' own
                            fixture tests, on the same terms (SP-0212 L1)
      snapshot-check    tools/build-catalog-snapshot.ps1 -Check -Offline - the tracked snapshot within its
                            size ceiling, streams.csv first and non-empty, a usable sourceDate; the
                            structural part only, so the gate stays offline (SP-0212 L2)
      smoke-verdict-test scripts/Test-SmokeVerdictGate.ps1 - the smoke-verdict writer and gate's own
                            fixture tests (SP-0212 L4, L5)
      snapshot-tool-test tools/Test-CatalogSnapshot.ps1 - the snapshot builder's own fixture tests; a
                            snapshot that fails validation never replaces the file it was aimed at
                            (SP-0212 L7)
      demo-check    tools/site/make-demo.ps1 -Check - the demo MP4, GIF and poster are all there, silent, short and within
                    their byte caps, read without ffmpeg (SP-0039); skipped, named above the verdict line and counted
                    on it, while docs/assets/demo holds no demo file, because nothing has been recorded yet
      check-verdict-scripts-test scripts/Test-CheckVerdictScripts.ps1 - the exit code and the last line of the gate
                    scripts that answer in CHECK-VERDICT, on throwaway trees and stubbed children (SP-0239)

    Exit codes (CHECK-VERDICT): 0 passed, 1 a check failed, 2 a check could not verify. A child that answers 4
    ("not looked at yet": a lock another run holds) is read as 2, and an exit nothing above anticipated (dotnet
    missing from PATH, ..) as 2 as well. A failed restore or build stops the run (nothing after it can be
    judged); every later check runs even when an earlier one fails, and the last line names all of them:
        check: PASS (<n> checks, <m> skipped)
        check: FAIL (<names>[; <m> skipped])
        check: COULD NOT VERIFY (<names>[; <m> skipped])
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
    pwsh -NoProfile -File (Join-Path $root 'scripts/test.ps1') -Configuration $Configuration -NoBuild
    $results['tests'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'scripts/Test-TestResultSummary.ps1')
    $results['test-results-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'scripts/check-docs.ps1')
    $results['docs-quality'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/site/build-site.ps1') -Check
    $results['site-sync'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/help/build-help.ps1') -Check
    $results['help-build'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'scripts/check-help.ps1')
    $results['help'] = $LASTEXITCODE
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
    pwsh -NoProfile -File (Join-Path $root 'msix/Test-AppxManifest.ps1') -Configuration $Configuration
    $results['msix-manifest'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/store/Test-StoreListing.ps1')
    $results['store-listing-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'scripts/Test-ExistingReleaseState.ps1')
    $results['existing-release-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/audit/Test-AuditVerdictGate.ps1')
    $results['audit-verdict-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/audit/Test-AuditTools.ps1')
    $results['audit-tools-test'] = $LASTEXITCODE
    # -Offline on purpose: the freshness comparison needs the network, and this gate stays offline and
    # deterministic so CI can run it. The release checklist runs the full -Check with the comparison.
    pwsh -NoProfile -File (Join-Path $root 'tools/build-catalog-snapshot.ps1') -Check -Offline
    $results['snapshot-check'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'scripts/Test-SmokeVerdictGate.ps1')
    $results['smoke-verdict-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'tools/Test-CatalogSnapshot.ps1')
    $results['snapshot-tool-test'] = $LASTEXITCODE
    pwsh -NoProfile -File (Join-Path $root 'scripts/Test-CheckVerdictScripts.ps1')
    $results['check-verdict-scripts-test'] = $LASTEXITCODE
    # Run once the files exist (SP-0039 6.4). A folder holding only some of the three is a defect and is judged, so the
    # gate keys on "any demo file", not on all of them; the check itself answers 1 for a file of the set that is absent.
    $skipped = [System.Collections.Generic.List[string]]::new()
    $demoDir = Join-Path $root 'docs/assets/demo'
    if ((Test-Path -LiteralPath $demoDir -PathType Container) -and @(Get-ChildItem -LiteralPath $demoDir -File -Filter 'streamsplayer-demo*').Count -gt 0) {
        pwsh -NoProfile -File (Join-Path $root 'tools/site/make-demo.ps1') -Check
        $results['demo-check'] = $LASTEXITCODE
    }
    else { Write-Host 'check: demo-check skipped - docs/assets/demo holds no demo file yet (SP-0039)'; $skipped.Add('demo-check') }

    # CHECK-VERDICT 2.1 and 2.14: a child exit of 4 is "not looked at yet", not a verdict, and an aggregator that does not
    # queue answers it as 2. Every other code but 0 and 2 stays a defect, as before (this gate has no advisory class).
    $failed = @($results.Keys | Where-Object { $results[$_] -notin 0, 2, 4 })
    $unverified = @($results.Keys | Where-Object { $results[$_] -in 2, 4 })
    $skippedNote = if ($skipped.Count) { "; $($skipped.Count) skipped" } else { '' }
    foreach ($name in $results.Keys) { Write-Host ("check: {0} exit {1}" -f $name, $results[$name]) }
    if ($failed.Count) { Write-Host "check: FAIL ($($failed -join ', ')$skippedNote)"; exit 1 }
    if ($unverified.Count) { Write-Host "check: COULD NOT VERIFY ($($unverified -join ', ')$skippedNote)"; exit 2 }
    Write-Host "check: PASS ($($results.Count) checks, $($skipped.Count) skipped)"
    exit 0
}
catch {
    # An exception nothing above anticipated (dotnet or pwsh missing from PATH, ..): nothing was judged, so the run could not
    # verify - a verdict line and exit 2, not a stack trace and exit 1 (CHECK-VERDICT 2.1, 2.5, 2.11). Every check runs in a
    # child process and answers with an exit code, so no judged result is lost here.
    Write-Host "check: COULD NOT VERIFY ($($_.Exception.Message -replace '\s+', ' '))"
    exit 2
}
finally { Pop-Location }
