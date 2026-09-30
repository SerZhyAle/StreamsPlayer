#requires -Version 7.0
<#
.SYNOPSIS
  Fail unless the tree carries a complete, PASS code-audit verdict for a release version (SP-0180).

.DESCRIPTION
  A release is preflighted only when the code that is about to ship has been audited line by line
  (docs/agent/CODE_AUDIT.md) and the audit is committed beside the smoke verdict as
  release-verdicts/<version>.audit.json. PLAN/ is not tracked, so the verdict is self-contained: it carries the
  per-file coverage and every finding with its disposition. This gate reads only that file and the tree.

  It fails unless:

    - the verdict names gate 'code-audit', result 'PASS' and exactly the release version;
    - the audited commit is HEAD or an ancestor of it, and nothing outside release-verdicts/ changed since -
      a material change after the audit means the audit judged different code (reassess and write a new verdict);
    - the file rows are exactly the audited set of this tree (class A of tools/audit/AuditCampaign.psm1), each
      read in full, each row's finding count matching the findings that name it;
    - every finding has a severity (High / Medium / Low), a summary and a disposition:
        fixed      needs evidence (`expected: X | actual: Y`)
        ticketed   needs an SP-NNNN id; allowed for Low only - a High or Medium finding is release-blocking
        exception  needs ownerDecision - the owner's recorded decision to ship with it (any severity)
      so an unresolved High or Medium finding can never sit behind a PASS;
    - the recorded baseline check (scripts/check.ps1) is PASS. The playback smoke verdict stays its own file and
      its own gate; release.yml requires both.

  Exit 0 = pass; any failure throws, which exits non-zero.

.PARAMETER Version
  The release version, YY.MMDD.HHmm.

.PARAMETER Root
  The repository to judge. Defaults to the one this script lives in.

.PARAMETER Ref
  The commit the verdict must cover. Defaults to HEAD - the tagged commit in release.yml.

.EXAMPLE
  pwsh -NoProfile -File ./scripts/Assert-AuditVerdict.ps1 -Version 26.0930.1200
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Root,
    [string] $Ref = 'HEAD'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path $PSScriptRoot -Parent }
$Root = (Resolve-Path -LiteralPath $Root).Path

if ($Version -notmatch '^\d{2}\.\d{4}\.\d{4}$') { throw "Version '$Version' must use YY.MMDD.HHmm." }

$path = Join-Path $Root "release-verdicts/$Version.audit.json"
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "No code-audit verdict for $Version at release-verdicts/$Version.audit.json. Audit the candidate (docs/agent/CODE_AUDIT.md), run scripts/Write-AuditVerdict.ps1 on a clean committed tree, and commit the file before tagging."
}
try { $v = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -ErrorAction Stop }
catch { throw "Audit verdict $path is not valid JSON: $($_.Exception.Message)" }

$problems = [System.Collections.Generic.List[string]]::new()
function Has([object] $Object, [string] $Name) { $null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name }
function Text([object] $Object, [string] $Name) { if (Has $Object $Name) { [string]$Object.$Name } else { '' } }

if ((Text $v 'gate') -ne 'code-audit') { $problems.Add("gate is '$(Text $v 'gate')', not code-audit.") }
if ((Text $v 'result') -ne 'PASS') { $problems.Add("result is '$(Text $v 'result')', not PASS.") }
if ((Text $v 'version') -ne $Version) { $problems.Add("version is '$(Text $v 'version')', not the release version $Version.") }
$commit = Text $v 'commit'
if ($commit -notmatch '^[0-9a-f]{40}$') { $problems.Add("commit '$commit' is not a full commit id.") }
if (-not (Text $v 'auditor')) { $problems.Add('auditor is missing.') }
if (-not (Text $v 'when')) { $problems.Add('when is missing.') }

# --- the audit names the code that ships -------------------------------------------------------------------
if ($commit -match '^[0-9a-f]{40}$') {
    git -C $Root merge-base --is-ancestor $commit $Ref 2>$null
    if ($LASTEXITCODE -ne 0) {
        $problems.Add("audited commit $commit is not $Ref or an ancestor of it.")
    } else {
        $changed = @(git -C $Root diff --name-only "$commit" "$Ref")
        if ($LASTEXITCODE -ne 0) { throw "git diff $commit $Ref failed (exit $LASTEXITCODE)." }
        $material = @($changed | Where-Object { $_ -notlike 'release-verdicts/*' })
        if ($material.Count -gt 0) {
            $shown = ($material | Select-Object -First 5) -join ', '
            $problems.Add("$($material.Count) file(s) changed after the audited commit $commit outside release-verdicts/ (first: $shown) - the audit judged different code; audit again and write a new verdict.")
        }
    }
}

# --- checks recorded beside the audit ----------------------------------------------------------------------
if (-not (Has $v 'checks') -or (Text $v.checks 'check') -ne 'PASS') {
    $problems.Add('checks.check is not PASS - record the result of scripts/check.ps1 (build, test, documentation, site) for the candidate.')
}

# --- coverage: every in-scope file, read in full -----------------------------------------------------------
Import-Module (Join-Path $PSScriptRoot '..' 'tools' 'audit' 'AuditCampaign.psm1') -Force
$expected = @(Get-AuditFileSet -RepoRoot $Root | ForEach-Object Path)
$rows = @(if (Has $v 'files') { $v.files } else { })
$rowByPath = @{}
foreach ($r in $rows) {
    $p = Text $r 'path'
    if ($rowByPath.ContainsKey($p)) { $problems.Add("file '$p' is listed twice.") } else { $rowByPath[$p] = $r }
}
$missing = @($expected | Where-Object { -not $rowByPath.ContainsKey($_) })
$extra = @($rowByPath.Keys | Where-Object { $expected -cnotcontains $_ })
if ($missing.Count) { $problems.Add("$($missing.Count) in-scope file(s) not accounted for (first: $(($missing | Select-Object -First 3) -join ', '))") }
if ($extra.Count) { $problems.Add("$($extra.Count) listed file(s) are not in scope (first: $(($extra | Select-Object -First 3) -join ', '))") }
foreach ($r in $rows) {
    if ((Has $r 'read') -eq $false -or $r.read -ne $true) { $problems.Add("file '$(Text $r 'path')' is not recorded as read in full.") }
}

# --- findings: each accounted for, none unresolved ---------------------------------------------------------
$findings = @(if (Has $v 'findings') { $v.findings } else { })
$perFile = @{}
$sev = @{ High = 0; Medium = 0; Low = 0 }
foreach ($f in $findings) {
    $id = Text $f 'id'
    $label = if ($id) { "finding $id" } else { 'a finding without id' }
    if (-not $id) { $problems.Add('a finding has no id.') }
    $s = Text $f 'severity'
    if ($sev.ContainsKey($s)) { $sev[$s]++ } else { $problems.Add("$label has severity '$s' (High, Medium or Low).") }
    if (-not (Text $f 'file')) { $problems.Add("$label names no file.") } else { $perFile[(Text $f 'file')] = 1 + [int]($perFile[(Text $f 'file')]) }
    if (-not (Text $f 'summary')) { $problems.Add("$label has no summary.") }
    switch (Text $f 'disposition') {
        'fixed' { if (-not (Text $f 'evidence')) { $problems.Add("$label is fixed without evidence.") } }
        'ticketed' {
            if ($s -ne 'Low') { $problems.Add("$label is $s and only ticketed - a High or Medium finding blocks the release until fixed or excepted by the owner.") }
            if ((Text $f 'ticket') -notmatch '^SP-\d{4}$') { $problems.Add("$label is ticketed without an SP-NNNN ticket.") }
        }
        'exception' { if (-not (Text $f 'ownerDecision')) { $problems.Add("$label is an exception without the owner's recorded decision.") } }
        default { $problems.Add("$label has disposition '$(Text $f 'disposition')' (fixed, ticketed or exception).") }
    }
}
foreach ($r in $rows) {
    $p = Text $r 'path'
    $n = [int]($perFile[$p])
    if ((Has $r 'findings') -eq $false -or [int]$r.findings -ne $n) { $problems.Add("file '$p' records $(Text $r 'findings') finding(s), the findings name it $n time(s).") }
}
if (Has $v 'summary') {
    foreach ($k in 'High', 'Medium', 'Low') {
        if ((Text $v.summary $k) -ne [string]$sev[$k]) { $problems.Add("summary.$k is '$(Text $v.summary $k)', the findings hold $($sev[$k]).") }
    }
} else { $problems.Add('summary is missing.') }

if ($problems.Count -gt 0) {
    foreach ($p in $problems) { Write-Host "code-audit verdict: $p" }
    throw "Audit verdict $path fails $($problems.Count) check(s)."
}
Write-Host "Code-audit verdict for ${Version}: PASS at commit $commit - $($expected.Count) files read, findings High $($sev.High) | Medium $($sev.Medium) | Low $($sev.Low), $($v.auditor), $($v.when)."
