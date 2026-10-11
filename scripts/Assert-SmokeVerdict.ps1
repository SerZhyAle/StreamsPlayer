<#
.SYNOPSIS
  Require the committed playback smoke verdict for one version (SP-0156, SP-0212 L4).

.DESCRIPTION
  The smoke gate runs only on the owner's machine, so its PASS travels as a committed file:
  release-verdicts/<version>.json, written by scripts/Write-SmokeVerdict.ps1. This is the gate that
  file has to pass, extracted from release.yml so the Store package build (msix/build-msix.ps1) refuses
  the same thing the release workflow refuses: a package must not be built from a version the smoke
  gate never judged. It throws on the first refusal, so a caller can simply call it.

  The verdict must name this version, carry gate 'smoke-playback' and result 'PASS', skip nothing
  (SP-0184 A2-4), and bind the code it judged (SP-0183 D2): its commit must be a full id that is HEAD
  or an ancestor of it, and nothing outside release-verdicts/ may have changed since - otherwise a PASS
  recorded for other code (same version stamp, older tree) would unlock this build.

.PARAMETER Version
  The YY.MMDD.HHmm version whose verdict the tagged tree must carry.

.PARAMETER Root
  The repository holding the verdict. Defaults to the one this script lives in.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d{2}\.\d{4}\.\d{4}$')] [string] $Version,
    [string] $Root
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path $PSScriptRoot -Parent }

$verdictPath = Join-Path $Root (Join-Path 'release-verdicts' "$Version.json")
if (-not (Test-Path -LiteralPath $verdictPath)) {
    throw "No smoke verdict for $Version at $verdictPath in this tree. Run scripts/smoke-playback.ps1 on a clean committed tree, commit release-verdicts/$Version.json, and put the tag on that commit."
}
$verdict = Get-Content -LiteralPath $verdictPath -Raw | ConvertFrom-Json
if ($verdict.gate -ne 'smoke-playback') { throw "Verdict $verdictPath names gate '$($verdict.gate)', not smoke-playback." }
if ($verdict.result -ne 'PASS') { throw "Verdict $verdictPath records result '$($verdict.result)', not PASS." }
# SP-0184 (A2-4): a PASS that skipped video or recording is a developer-machine run, not a release's.
$skippedChecks = @($verdict.skipped | Where-Object { $_ })
if ($skippedChecks.Count -gt 0) { throw "Verdict $verdictPath skipped $($skippedChecks -join ', '); run scripts/smoke-playback.ps1 without -SkipVideo or -SkipRecording." }
if ($verdict.version -ne $Version) { throw "Verdict $verdictPath names version '$($verdict.version)', not $Version." }
# SP-0183 D2: the verdict binds the code it judged.
$smokeCommit = [string]$verdict.commit
if ($smokeCommit -notmatch '^[0-9a-f]{40}$') { throw "Verdict $verdictPath names commit '$smokeCommit', which is not a full commit id." }
Push-Location $Root
try {
    git merge-base --is-ancestor $smokeCommit HEAD
    if ($LASTEXITCODE -ne 0) { throw "The smoke verdict's commit $smokeCommit is not HEAD or an ancestor of it." }
    $changedSinceSmoke = @(git diff --no-renames --name-only $smokeCommit HEAD)
    if ($LASTEXITCODE -ne 0) { throw "git diff $smokeCommit HEAD failed (exit $LASTEXITCODE)." }
}
finally { Pop-Location }
$materialSinceSmoke = @($changedSinceSmoke | Where-Object { $_ -notlike 'release-verdicts/*' })
if ($materialSinceSmoke.Count -gt 0) {
    throw "$($materialSinceSmoke.Count) file(s) changed after the smoke-tested commit $smokeCommit outside release-verdicts/ (first: $(($materialSinceSmoke | Select-Object -First 5) -join ', ')). The smoke judged different code; run scripts/smoke-playback.ps1 again."
}
Write-Host "Smoke verdict for $Version (commit $smokeCommit): PASS, $($verdict.when)."
