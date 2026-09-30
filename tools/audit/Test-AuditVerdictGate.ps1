#requires -Version 7.0
<#
.SYNOPSIS
    SP-0180: fixture tests for the release code-audit verdict (Write-AuditVerdict.ps1, Assert-AuditVerdict.ps1).

.DESCRIPTION
    Builds a throwaway git repository with a few audited files and runs the real writer and gate against it:

      writer    a clean audit writes a verdict; a dirty tree, a High/Medium finding that is only ticketed, a
                fixed finding without evidence, a Low finding ticketed without an id, an exception without the
                owner's decision each write nothing
      gate      passes on the committed verdict; passes when only release-verdicts/ changed afterwards; fails
                when audited code changed after the audit, when the verdict is missing, when a file row is
                dropped, when the result is not PASS, when the version differs, when the baseline check is not PASS
      accepted  a High finding fixed with evidence, a High finding excepted by the owner, a Low finding ticketed

    Exit 0 when every check passes, 1 otherwise. The last line is `audit-verdict-gate: PASS|FAIL (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$writer = Join-Path $repo 'scripts/Write-AuditVerdict.ps1'
$gate = Join-Path $repo 'scripts/Assert-AuditVerdict.ps1'
$version = '26.0930.1200'

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}
function Invoke-Script([string] $Script, [string[]] $Arguments) {
    $out = & pwsh -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = ($out -join "`n") }
}
function Invoke-Git([string[]] $Arguments) {
    & git -C $script:fixture -c user.name=t -c user.email=t@example.invalid -c core.autocrlf=false @Arguments 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed" }
}
function Write-Findings([string] $Name, [object[]] $Findings, [string] $CheckResult = 'PASS') {
    $path = Join-Path $script:work "$Name.json"
    [ordered]@{ auditor = 'fixture'; checks = [ordered]@{ check = $CheckResult }; findings = $Findings } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8
    $path
}
function Write-Verdict([string] $Name, [object[]] $Findings) {
    Invoke-Script $writer @('-FindingsPath', (Write-Findings $Name $Findings), '-Root', $script:fixture)
}
function Invoke-Gate { Invoke-Script $gate @('-Version', $version, '-Root', $script:fixture) }
$verdictFile = { Join-Path $script:fixture "release-verdicts/$version.audit.json" }
function Edit-Verdict([scriptblock] $Edit) {
    $v = Get-Content -LiteralPath (& $verdictFile) -Raw | ConvertFrom-Json -Depth 10
    & $Edit $v
    $v | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (& $verdictFile) -Encoding utf8
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0180-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$work = Join-Path $fixture '..' ('sp0180-work-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $work = (Resolve-Path $work).Path
    foreach ($file in 'src/StreamsPlayer.Core/Alpha.cs', 'src/StreamsPlayer.Core/Beta.cs', 'src/StreamsPlayer.App/Main.xaml') {
        $full = Join-Path $fixture $file
        New-Item -ItemType Directory -Path (Split-Path -Parent $full) -Force | Out-Null
        Set-Content -LiteralPath $full -Value "// $file"
    }
    Set-Content -LiteralPath (Join-Path $fixture 'Directory.Build.props') -Value "<Project><PropertyGroup><Version>$version</Version></PropertyGroup></Project>"
    Invoke-Git @('init', '-q'); Invoke-Git @('add', '--all'); Invoke-Git @('commit', '-q', '-m', 'candidate')

    $alpha = 'src/StreamsPlayer.Core/Alpha.cs'
    $low = [ordered]@{ id = 'F1'; file = $alpha; line = 1; severity = 'Low'; summary = 'naming'; disposition = 'ticketed'; ticket = 'SP-0190' }

    # --- the writer refuses what it cannot stand behind ------------------------------------------------------
    Set-Content -LiteralPath (Join-Path $fixture 'dirty.txt') -Value 'x'
    $r = Write-Verdict 'clean' @()
    Check ($r.Exit -ne 0 -and -not (Test-Path (& $verdictFile))) 'a dirty tree writes no verdict'
    Remove-Item -LiteralPath (Join-Path $fixture 'dirty.txt')

    $high = [ordered]@{ id = 'F2'; file = $alpha; severity = 'High'; summary = 'leak'; disposition = 'ticketed'; ticket = 'SP-0191' }
    $r = Write-Verdict 'high-ticketed' @($high)
    Check ($r.Exit -ne 0 -and -not (Test-Path (& $verdictFile))) 'a High finding that is only ticketed writes no verdict'
    Check ($r.Output -match 'blocks the release') 'the refusal says why a High finding blocks'

    $medium = [ordered]@{ id = 'F3'; file = $alpha; severity = 'Medium'; summary = 'x'; disposition = 'ticketed'; ticket = 'SP-0192' }
    $r = Write-Verdict 'medium-ticketed' @($medium)
    Check ($r.Exit -ne 0 -and -not (Test-Path (& $verdictFile))) 'a Medium finding that is only ticketed writes no verdict'

    $fixedNoEvidence = [ordered]@{ id = 'F4'; file = $alpha; severity = 'High'; summary = 'x'; disposition = 'fixed' }
    Check ((Write-Verdict 'fixed-no-evidence' @($fixedNoEvidence)).Exit -ne 0) 'a fixed finding without evidence writes no verdict'

    $lowNoTicket = [ordered]@{ id = 'F5'; file = $alpha; severity = 'Low'; summary = 'x'; disposition = 'ticketed'; ticket = 'later' }
    Check ((Write-Verdict 'low-no-ticket' @($lowNoTicket)).Exit -ne 0) 'a Low finding ticketed without an SP id writes no verdict'

    $excNoOwner = [ordered]@{ id = 'F6'; file = $alpha; severity = 'High'; summary = 'x'; disposition = 'exception' }
    Check ((Write-Verdict 'exception-no-owner' @($excNoOwner)).Exit -ne 0) 'an exception without the owner decision writes no verdict'

    $failedCheck = Write-Findings 'check-fail' @() 'FAIL'
    $r = Invoke-Script $writer @('-FindingsPath', $failedCheck, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and -not (Test-Path (& $verdictFile))) 'a baseline check that is not PASS writes no verdict'
    if (Test-Path (& $verdictFile)) { Remove-Item (& $verdictFile) -Force }

    # --- a complete audit --------------------------------------------------------------------------------------
    $fixedHigh = [ordered]@{ id = 'F7'; file = $alpha; severity = 'High'; summary = 'race'; disposition = 'fixed'; evidence = 'expected: no race | actual: no race' }
    $excepted = [ordered]@{ id = 'F8'; file = 'src/StreamsPlayer.Core/Beta.cs'; severity = 'High'; summary = 'known limit'; disposition = 'exception'; ownerDecision = 'Owner accepts on 2026-09-30' }
    $r = Write-Verdict 'complete' @($low, $fixedHigh, $excepted)
    Check ($r.Exit -eq 0 -and (Test-Path (& $verdictFile))) 'a fixed High, an owner exception and a ticketed Low write a verdict'
    $rows = @((Get-Content (& $verdictFile) -Raw | ConvertFrom-Json).files)
    Check ($rows.Count -eq 4) 'every in-scope file (3 sources and Directory.Build.props) has a row'
    Check (((Invoke-Gate).Exit) -eq 0) 'the gate passes on the verdict it just wrote'

    Invoke-Git @('add', '--all'); Invoke-Git @('commit', '-q', '-m', 'audit verdict')
    Check ((Invoke-Gate).Exit -eq 0) 'the gate passes once the verdict is committed (only release-verdicts/ changed)'

    # --- the gate refuses ---------------------------------------------------------------------------------------
    Edit-Verdict { param($v) $v.files = @($v.files | Select-Object -Skip 1) }
    $r = Invoke-Gate
    Check ($r.Exit -ne 0 -and $r.Output -match 'not accounted for') 'a dropped file row fails the gate'
    Invoke-Git @('checkout', '--', 'release-verdicts')

    Edit-Verdict { param($v) $v.result = 'FAIL' }
    Check ((Invoke-Gate).Exit -ne 0) 'a result that is not PASS fails the gate'
    Invoke-Git @('checkout', '--', 'release-verdicts')

    Edit-Verdict { param($v) $v.version = '26.0930.1201' }
    Check ((Invoke-Gate).Exit -ne 0) 'a verdict for another version fails the gate'
    Invoke-Git @('checkout', '--', 'release-verdicts')

    Edit-Verdict { param($v) $v.checks.check = 'FAIL' }
    Check ((Invoke-Gate).Exit -ne 0) 'a baseline check that is not PASS fails the gate'
    Invoke-Git @('checkout', '--', 'release-verdicts')

    Edit-Verdict { param($v) $v.findings[0].disposition = 'ticketed'; $v.findings[0].severity = 'High' }
    Check ((Invoke-Gate).Exit -ne 0) 'a verdict edited to hold an unresolved High fails the gate'
    Invoke-Git @('checkout', '--', 'release-verdicts')

    Check ((Invoke-Gate).Exit -eq 0) 'the gate passes again on the restored verdict'

    Add-Content -LiteralPath (Join-Path $fixture $alpha) -Value '// changed after the audit'
    Invoke-Git @('add', '--all'); Invoke-Git @('commit', '-q', '-m', 'late change')
    $r = Invoke-Gate
    Check ($r.Exit -ne 0 -and $r.Output -match 'changed after the audited commit') 'audited code changed after the audit fails the gate'

    Remove-Item -LiteralPath (& $verdictFile) -Force
    Check ((Invoke-Gate).Exit -ne 0) 'a missing verdict fails the gate'
}
finally {
    foreach ($dir in $fixture, $work) {
        if ($dir -and (Test-Path -LiteralPath $dir)) {
            Get-ChildItem -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Attributes = 'Normal' } catch { } }
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($failures.Count -eq 0) { "audit-verdict-gate: PASS ($passed checks)"; exit 0 }
"audit-verdict-gate: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
