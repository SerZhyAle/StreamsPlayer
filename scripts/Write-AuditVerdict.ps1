#requires -Version 7.0
<#
.SYNOPSIS
  Record a completed pre-release code audit as release-verdicts/<version>.audit.json (SP-0180).

.DESCRIPTION
  The audit itself follows docs/agent/CODE_AUDIT.md. This writer turns its outcome into the committed verdict
  release.yml requires, and refuses one it cannot stand behind:

    - the tree must be clean - an audit is about a commit, and a dirty tree's code is no commit's code;
    - the version is the Directory.Build.props stamp;
    - the verdict is written only if scripts/Assert-AuditVerdict.ps1 accepts it, so a High or Medium finding
      that is neither fixed nor excepted by the owner, a missing evidence line or a bad ticket id writes nothing.

  Coverage is generated from the audited file set of the tree (every in-scope file, read in full); running the
  writer is the auditor's statement that the read happened, the same way the smoke writer's presence is the
  statement that playback passed. The findings file is the only input:

    {
      "auditor": "name",
      "checks": { "check": "PASS" },
      "findings": [
        { "id": "F1", "file": "src/StreamsPlayer.Core/X.cs", "line": 42, "severity": "Low",
          "summary": "what is wrong", "disposition": "ticketed", "ticket": "SP-0190" },
        { "id": "F2", "file": "...", "severity": "High", "summary": "...", "disposition": "fixed",
          "evidence": "expected: X | actual: Y" },
        { "id": "F3", "file": "...", "severity": "Medium", "summary": "...", "disposition": "exception",
          "ownerDecision": "what the owner decided, and when" }
      ]
    }

  `checks.check` records the result of scripts/check.ps1 for this candidate; write `PASS` only after running it.
  Commit the verdict, then run scripts/smoke-playback.ps1 (its verdict needs a clean tree too) and tag the
  commit. Any later change outside release-verdicts/ invalidates this verdict: audit again.

.PARAMETER FindingsPath
  The findings file described above. An empty findings list is a clean audit.

.PARAMETER Root
  The repository the verdict is about. Defaults to the one this script lives in.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FindingsPath,
    [string] $Root
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path $PSScriptRoot -Parent }
$Root = (Resolve-Path -LiteralPath $Root).Path

$props = [xml](Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw)
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Could not read <Version> from Directory.Build.props.' }
if ($version -notmatch '^\d{2}\.\d{4}\.\d{4}$') { throw "Version '$version' must use the house stamp YY.MMDD.HHmm." }

$dirty = git -C $Root status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
if ($dirty) { throw 'The working tree is not clean - commit everything first. An audit verdict is recorded against a commit.' }
$commit = git -C $Root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'git rev-parse HEAD failed.' }

$in = Get-Content -LiteralPath $FindingsPath -Raw | ConvertFrom-Json -ErrorAction Stop
foreach ($name in 'auditor', 'checks', 'findings') {
    if ($in.PSObject.Properties.Name -notcontains $name) { throw "Findings file lacks '$name'." }
}
$findings = @($in.findings)

Import-Module (Join-Path $PSScriptRoot '..' 'tools' 'audit' 'AuditCampaign.psm1') -Force
$files = @(Get-AuditFileSet -RepoRoot $Root | ForEach-Object {
    $p = $_.Path
    [ordered]@{ path = $p; read = $true; findings = @($findings | Where-Object { $_.file -ceq $p }).Count }
})
$count = { param($s) @($findings | Where-Object { $_.severity -eq $s }).Count }

$verdict = [ordered]@{
    gate      = 'code-audit'
    result    = 'PASS'
    version   = $version
    commit    = $commit
    when      = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    auditor   = [string]$in.auditor
    procedure = 'docs/agent/CODE_AUDIT.md'
    checks    = $in.checks
    summary   = [ordered]@{ High = (& $count 'High'); Medium = (& $count 'Medium'); Low = (& $count 'Low') }
    findings  = $findings
    files     = $files
}
$folder = Join-Path $Root 'release-verdicts'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$path = Join-Path $folder "$version.audit.json"
$verdict | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8

try { & (Join-Path $PSScriptRoot 'Assert-AuditVerdict.ps1') -Version $version -Root $Root }
catch {
    Remove-Item -LiteralPath $path -Force
    throw "No verdict written - $($_.Exception.Message)"
}
Write-Host "Audit verdict written: $path (commit $commit). Commit it, then run the smoke gate and tag that commit." -ForegroundColor Green
