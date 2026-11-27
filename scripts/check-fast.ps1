<#
    The fast tier: the checks that take 5 seconds or less, the only checks that run between releases (canon
    TESTING_AND_QA section 2, SP-0256). Nothing here restores, builds or runs the test suite; that is the
    release tier, scripts/check.ps1, which the release checklist and release.yml run.

    -Area picks the checks of the touched area; the default, all, runs every fast check. The checks of the
    chosen area run as parallel child processes (each writes only under its own GUID-named scratch folder), so
    a call costs about its slowest check under load, not the sum of them. Each child's output is printed in the
    order below, then one verdict. Between releases call the area of what changed; all is inside the 5 s cap too
    (4.0 s, STRWIN-0304) and is the call when the touched area is unclear.

      Area             Check                                                  Seconds  Measured
      docs             scripts/check-docs.ps1                                 1.60     direct call, 2026-10-30
      docs             tools/Build-CliDoc.ps1 -Check                          0.39     direct call, 2026-11-14
      docs             tools/Test-CliDoc.ps1                                  1.79     direct call, mean of 3, 2026-11-15
      help             scripts/check-help.ps1                                 0.47     direct call, 2026-10-30
      help             tools/help/build-help.ps1 -Check                      1.93     direct call, mean of 3, 2026-11-20
      site             tools/site/Test-HeldAddresses.ps1                      0.85     direct call, 2026-10-30
      site             tools/site/Test-ReleaseNotes.ps1                       0.95     direct call, 2026-10-30
      site             tools/site/Test-SiteDiscovery.ps1                      1.40     direct call, 2026-10-30
      site             tools/site/Test-SiteFacts.ps1                          0.92     direct call, 2026-10-30
      release-scripts  scripts/Test-TestResultSummary.ps1                     0.45     direct call, 2026-10-30
      release-scripts  scripts/Test-ExistingReleaseState.ps1                  0.40     direct call, 2026-10-30
      release-scripts  scripts/Test-ReleaseVersion.ps1                        1.31     direct call, mean of 3, 2026-11-15
      release-scripts  msix/Test-MsixIdentity.ps1                             0.72     direct call, 2026-11-12
      release-scripts  scripts/Test-StorePreflightCall.ps1                    1.17     direct call, mean of 3, 2026-11-15
      release-scripts  scripts/Test-ReleaseLedger.ps1                         1.16     direct call, 2026-11-18
      release-scripts  scripts/Test-CheckPlacement.ps1                       0.56     direct call, mean of 3, 2026-11-23 (STRWIN-0239 P6)
      snapshot         tools/build-catalog-snapshot.ps1 -Check -Offline       1.28     direct call, 2026-10-30

    Whole calls, wall time around the script. 2026-11-23 (STRWIN-0239 P6, placement-registry added as the 17th
    check, itself 0.56 s): all 6.1 to 6.6 s over three runs under load, and every area 1.5 to 1.7x its 2026-11-20
    mean the same day (docs 4.0, help 2.8, site 3.2, snapshot 2.1, release-scripts 2.4 to 2.9 s), so the machine,
    not the new check, carried the growth; the all call is to be re-measured on a quiet machine before it is
    called inside the cap again - between releases the area call stays the cheapest and the default.
    2026-11-20 (STRWIN-0304, help-build added): all 3.96 s (seven runs
    3.60 to 4.51 s; the verdict line read 3.3 to 4.2 s; 15 checks ran, the snapshot check skipped for want of a
    built Core - the 16-check variant with a built Core is the release's to measure) and help 2.23 s (2 checks;
    0.9 s with one). 2026-11-15 (SP-0290): all 3.64 s (twelve runs 3.38 to 3.89 s; the verdict
    line read 3.1 to 3.6 s), and the mean of 3 for docs 2.3, site 1.9, release-scripts 1.9, snapshot 1.7.
    Before SP-0290 (SP-0288): all 4.9 s, docs 3.4, release-scripts 3.8. The parallel run is slower than the slowest
    check alone, about twice for every check: in one profiled all-run Test-CliDoc, Test-ReleaseVersion and
    Test-StorePreflightCall finished at 3.6 to 3.95, 3.1 to 3.4 and 2.7 to 2.8 s against 1.8, 1.3 and 1.2 s alone, so
    the run is bound by the work started, not by the slowest check. Test-SiteDiscovery and check-docs now finish
    with Test-CliDoc (3.3 to 3.7 s in the run, 1.4 and 1.6 s alone) and are the next candidates.

    A check measured above 5 s, or never measured, is release tier and is not listed here; the measured list
    with both classes is in .claude/rules/build-and-checks.md ("Fast tier and release tier"). Moving a check
    into this list needs a measured time of 5 s or less recorded there first.

    Exit codes (CHECK-VERDICT): 0 passed, 1 a check failed, 2 a check could not verify. A child that answers 4
    ("not looked at yet") is read as 2, and so is a child still running 60 s after the call started. The last
    line names the area it judged (BUILD-EVIDENCE rule 1: a docs-area pass is not evidence about help), the
    failures and the elapsed wall time:
        check-fast: PASS (<area>, <n> checks, <m> skipped, <s> s)
        check-fast: FAIL (<area>, <names>; <s> s)
        check-fast: COULD NOT VERIFY (<area>, <names>; <s> s)
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'docs', 'help', 'site', 'release-scripts', 'snapshot')]
    [string] $Area = 'all'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$checks = @(
    [pscustomobject]@{ Area = 'docs'; Name = 'docs-quality'; File = 'scripts/check-docs.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'docs'; Name = 'cli-docs'; File = 'tools/Build-CliDoc.ps1'; Arguments = @('-Check') }
    [pscustomobject]@{ Area = 'docs'; Name = 'cli-docs-test'; File = 'tools/Test-CliDoc.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'help'; Name = 'help'; File = 'scripts/check-help.ps1'; Arguments = @() }
    # Fast since STRWIN-0304 (5.37 s on 2026-10-30; 1.93 s after STRWIN-0302's output-neutral rework).
    [pscustomobject]@{ Area = 'help'; Name = 'help-build'; File = 'tools/help/build-help.ps1'; Arguments = @('-Check') }
    [pscustomobject]@{ Area = 'site'; Name = 'held-addresses-test'; File = 'tools/site/Test-HeldAddresses.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'site'; Name = 'release-notes-test'; File = 'tools/site/Test-ReleaseNotes.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'site'; Name = 'site-discovery-test'; File = 'tools/site/Test-SiteDiscovery.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'site'; Name = 'site-facts-test'; File = 'tools/site/Test-SiteFacts.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'test-results-test'; File = 'scripts/Test-TestResultSummary.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'existing-release-test'; File = 'scripts/Test-ExistingReleaseState.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'release-version-test'; File = 'scripts/Test-ReleaseVersion.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'msix-identity-test'; File = 'msix/Test-MsixIdentity.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'store-preflight-call-test'; File = 'scripts/Test-StorePreflightCall.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'release-ledger-test'; File = 'scripts/Test-ReleaseLedger.ps1'; Arguments = @() }
    # CHECK-PLACEMENT (STRWIN-0239): the two-way check of scripts/check-placement.jsonl, which places every
    # check of this tier, of the release workflow and of the release checklist.
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'placement-registry'; File = 'scripts/Test-CheckPlacement.ps1'; Arguments = @() }
    # NeedsBuiltCore: the snapshot ceilings are read from the built StreamsPlayer.Core.dll (SP-0133), and the fast tier
    # builds nothing - push CI has no build at all. Without a build the check is skipped by name on the verdict line
    # (release.yml runs the same check after its own build); a checkout that was built still runs it.
    [pscustomobject]@{ Area = 'snapshot'; Name = 'snapshot-check'; File = 'tools/build-catalog-snapshot.ps1'; Arguments = @('-Check', '-Offline'); NeedsBuiltCore = $true }
)
$coreBuilt = (Test-Path -LiteralPath (Join-Path $root 'src/StreamsPlayer.Core/bin/Release/net10.0/StreamsPlayer.Core.dll')) -or
             (Test-Path -LiteralPath (Join-Path $root 'src/StreamsPlayer.Core/bin/Debug/net10.0/StreamsPlayer.Core.dll'))
$inArea = @($checks | Where-Object { $Area -eq 'all' -or $_.Area -eq $Area })
$skipped = @($inArea | Where-Object { $_.PSObject.Properties['NeedsBuiltCore'] -and $_.NeedsBuiltCore -and -not $coreBuilt })
$selected = @($inArea | Where-Object { $skipped -notcontains $_ })
$timeoutMs = 60000
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$scratch = Join-Path $root ('temp/check-fast/' + [guid]::NewGuid().ToString('N'))
Push-Location $root
try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $runs = foreach ($check in $selected) {
        $out = Join-Path $scratch "$($check.Name).out.txt"
        $err = Join-Path $scratch "$($check.Name).err.txt"
        $arguments = @('-NoProfile', '-File', (Join-Path $root $check.File)) + $check.Arguments
        $process = Start-Process -FilePath pwsh -ArgumentList $arguments -WorkingDirectory $root -NoNewWindow -PassThru `
            -RedirectStandardOutput $out -RedirectStandardError $err
        # Touching the handle now keeps ExitCode readable after the process ends.
        $null = $process.Handle
        [pscustomobject]@{ Check = $check; Process = $process; Out = $out; Err = $err }
    }

    $results = [ordered]@{}
    foreach ($run in $runs) {
        $remaining = [Math]::Max(0, $timeoutMs - [int]$clock.ElapsedMilliseconds)
        if ($run.Process.WaitForExit($remaining)) {
            $run.Process.WaitForExit()
            $code = $run.Process.ExitCode
        }
        else {
            $run.Process.Kill($true)
            Write-Host "check-fast: $($run.Check.Name) ran past $($timeoutMs / 1000) s and was stopped"
            $code = 2
        }
        foreach ($stream in $run.Out, $run.Err) {
            if (Test-Path -LiteralPath $stream) { Get-Content -LiteralPath $stream | ForEach-Object { Write-Host $_ } }
        }
        $results[$run.Check.Name] = $code
    }

    $clock.Stop()
    $seconds = '{0:N1} s' -f $clock.Elapsed.TotalSeconds
    # RRB-5: a run that judged nothing (the one requested area was skipped for want of a build) is not a pass.
    if ($results.Count -eq 0) {
        foreach ($check in $skipped) { Write-Host "check-fast: $($check.Name) skipped - no built StreamsPlayer.Core.dll; the release build runs it" }
        Write-Host "check-fast: COULD NOT VERIFY ($Area, no check ran; $($skipped.Count) skipped; $seconds)"
        exit 2
    }
    $failed = @($results.Keys | Where-Object { $results[$_] -notin 0, 2, 4 })
    $unverified = @($results.Keys | Where-Object { $results[$_] -in 2, 4 })
    foreach ($name in $results.Keys) { Write-Host ("check-fast: {0} exit {1}" -f $name, $results[$name]) }
    foreach ($check in $skipped) { Write-Host "check-fast: $($check.Name) skipped - no built StreamsPlayer.Core.dll; the release build runs it" }
    # BUILD-EVIDENCE rule 1: the verdict names the area it judged.
    if ($failed.Count) { Write-Host "check-fast: FAIL ($Area, $($failed -join ', '); $seconds)"; exit 1 }
    if ($unverified.Count) { Write-Host "check-fast: COULD NOT VERIFY ($Area, $($unverified -join ', '); $seconds)"; exit 2 }
    Write-Host "check-fast: PASS ($Area, $($results.Count) checks, $($skipped.Count) skipped, $seconds)"
    exit 0
}
catch {
    # Nothing anticipated this (pwsh missing from PATH, ..): nothing was judged, so the run could not verify
    # (CHECK-VERDICT 2.1, 2.5, 2.11).
    Write-Host "check-fast: COULD NOT VERIFY ($Area, $($_.Exception.Message -replace '\s+', ' '))"
    exit 2
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}
