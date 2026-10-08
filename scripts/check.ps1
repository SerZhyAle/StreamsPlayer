<#
    The release-parity gate: Release restore + build, then every check that judges the tree.

      tests         dotnet test over the solution
      docs-quality  scripts/check-docs.ps1 - document registry, coverage, links, style, offline (SP-0140)
      site-sync     tools/site/build-site.ps1 -Check - docs/ matches its copy decks (DOC-INTERNAL-QUALITY rule 4),
                    every held site address resolves (SITE-STRUCTURE rule 8, SP-0193), and the facts the copy states
                    and the pillars of POSITIONING.md agree with their sources (SITE-REPRESENTATION rules 1 to 3, SP-0198)
      held-addresses-test  tools/site/Test-HeldAddresses.ps1 - the held-address gate's own fixture tests
      site-facts-test      tools/site/Test-SiteFacts.ps1 - the site-facts and positioning gate's own fixture tests
      msix-gate     msix/Test-AssertMsixPackage.ps1 - the Store package read-back refuses what it must (SP-0190)

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
    pwsh -NoProfile -File (Join-Path $root 'msix/Test-AssertMsixPackage.ps1')
    $results['msix-gate'] = $LASTEXITCODE

    $failed = @($results.Keys | Where-Object { $results[$_] -ne 0 -and $results[$_] -ne 2 })
    $unverified = @($results.Keys | Where-Object { $results[$_] -eq 2 })
    foreach ($name in $results.Keys) { Write-Host ("check: {0} exit {1}" -f $name, $results[$name]) }
    if ($failed.Count) { Write-Host "check: FAIL ($($failed -join ', '))"; exit 1 }
    if ($unverified.Count) { Write-Host "check: CANNOT VERIFY ($($unverified -join ', '))"; exit 2 }
    Write-Host "check: PASS ($($results.Count) checks)"
    exit 0
}
finally { Pop-Location }
