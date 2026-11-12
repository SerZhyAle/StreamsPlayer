<#
    The fast tier: the checks that take 5 seconds or less, the only checks that run between releases (canon
    TESTING_AND_QA section 2, SP-0256). Nothing here restores, builds or runs the test suite; that is the
    release tier, scripts/check.ps1, which the release checklist and release.yml run.

    -Area picks the checks of the touched area; the default, all, runs every fast check. The checks of the
    chosen area run as parallel child processes (each writes only under its own GUID-named scratch folder), so
    one call stays inside the 5 s budget. Each child's output is printed in the order below, then one verdict.

      Area             Check                                                  Seconds  Measured
      docs             scripts/check-docs.ps1                                 1.60     direct call, 2026-10-30
      help             scripts/check-help.ps1                                 0.47     direct call, 2026-10-30
      site             tools/site/Test-HeldAddresses.ps1                      0.85     direct call, 2026-10-30
      site             tools/site/Test-ReleaseNotes.ps1                       0.95     direct call, 2026-10-30
      site             tools/site/Test-SiteDiscovery.ps1                      1.40     direct call, 2026-10-30
      site             tools/site/Test-SiteFacts.ps1                          0.92     direct call, 2026-10-30
      release-scripts  scripts/Test-TestResultSummary.ps1                     0.45     direct call, 2026-10-30
      release-scripts  scripts/Test-ExistingReleaseState.ps1                  0.40     direct call, 2026-10-30
      release-scripts  scripts/Test-ReleaseVersion.ps1                        2.59     direct call, 2026-10-30
      release-scripts  msix/Test-MsixIdentity.ps1                             0.72     direct call, 2026-11-12
      release-scripts  scripts/Test-StorePreflightCall.ps1                    2.8      direct call, 2026-11-12
      snapshot         tools/build-catalog-snapshot.ps1 -Check -Offline       1.28     direct call, 2026-10-30

    A check measured above 5 s, or never measured, is release tier and is not listed here; the measured list
    with both classes is in AGENTS.md "Fast checks and the release tier". Moving a check into this list needs a
    measured time of 5 s or less recorded there first.

    Exit codes (CHECK-VERDICT): 0 passed, 1 a check failed, 2 a check could not verify. A child that answers 4
    ("not looked at yet") is read as 2, and so is a child still running 60 s after the call started. The last
    line names the failures and the elapsed wall time:
        check-fast: PASS (<n> checks, <s> s)
        check-fast: FAIL (<names>; <s> s)
        check-fast: COULD NOT VERIFY (<names>; <s> s)
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
    [pscustomobject]@{ Area = 'help'; Name = 'help'; File = 'scripts/check-help.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'site'; Name = 'held-addresses-test'; File = 'tools/site/Test-HeldAddresses.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'site'; Name = 'release-notes-test'; File = 'tools/site/Test-ReleaseNotes.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'site'; Name = 'site-discovery-test'; File = 'tools/site/Test-SiteDiscovery.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'site'; Name = 'site-facts-test'; File = 'tools/site/Test-SiteFacts.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'test-results-test'; File = 'scripts/Test-TestResultSummary.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'existing-release-test'; File = 'scripts/Test-ExistingReleaseState.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'release-version-test'; File = 'scripts/Test-ReleaseVersion.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'msix-identity-test'; File = 'msix/Test-MsixIdentity.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'release-scripts'; Name = 'store-preflight-call-test'; File = 'scripts/Test-StorePreflightCall.ps1'; Arguments = @() }
    [pscustomobject]@{ Area = 'snapshot'; Name = 'snapshot-check'; File = 'tools/build-catalog-snapshot.ps1'; Arguments = @('-Check', '-Offline') }
)
$selected = @($checks | Where-Object { $Area -eq 'all' -or $_.Area -eq $Area })
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
    $failed = @($results.Keys | Where-Object { $results[$_] -notin 0, 2, 4 })
    $unverified = @($results.Keys | Where-Object { $results[$_] -in 2, 4 })
    foreach ($name in $results.Keys) { Write-Host ("check-fast: {0} exit {1}" -f $name, $results[$name]) }
    if ($failed.Count) { Write-Host "check-fast: FAIL ($($failed -join ', '); $seconds)"; exit 1 }
    if ($unverified.Count) { Write-Host "check-fast: COULD NOT VERIFY ($($unverified -join ', '); $seconds)"; exit 2 }
    Write-Host "check-fast: PASS ($($results.Count) checks, $seconds)"
    exit 0
}
catch {
    # Nothing anticipated this (pwsh missing from PATH, ..): nothing was judged, so the run could not verify
    # (CHECK-VERDICT 2.1, 2.5, 2.11).
    Write-Host "check-fast: COULD NOT VERIFY ($($_.Exception.Message -replace '\s+', ' '))"
    exit 2
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}
