#requires -Version 7.0
<#
.SYNOPSIS
    SP-0139: create one self-contained audit ticket per slice of the campaign manifest, in risk order.

.DESCRIPTION
    Reads the manifest written by New-AuditSlices.ps1 and, for every slice that has no ticket yet, writes:

      PLAN/SP-NNNN_<slug>.md                      the ticket: goal, audited files, triage policy, the slice
                                                  procedure copied from docs/agent/CODE_AUDIT.md, inline phases
      PLAN/SP-NNNN_<slug>/research/slice.md       files, lines, signals, context, siblings, known findings
      PLAN/SP-NNNN_<slug>/research/findings.md    the coverage and findings tables the audit fills in

    Idempotent by slice name: a ticket under PLAN/ or PLAN/DONE/ that carries `**Audit slice:** <name>` is that
    slice's ticket, and a second run creates nothing. New ids continue after the highest SP id in PLAN/ and
    PLAN/DONE/. Status is `Tactical`, because the plan is inside the ticket.

    The script never edits the release queue - the owner orders it. It prints the queue rows for the slices,
    in manifest order, for pasting.

.PARAMETER Parent
    The umbrella ticket id.

.PARAMETER Manifest
    One or more manifests (the first slicing, then any tail manifests). Defaults to the parent ticket's
    research/audit-slices.json.

.PARAMETER TriagePolicy
    The umbrella's triage policy, copied into every ticket.

.PARAMETER Date
    The creation date written into the tickets (default: today, yyyy-MM-dd).

.PARAMETER DryRun
    Print what would be created; write nothing.

.OUTPUTS
    Exit 0 - done (created or nothing to create); 2 - cannot (manifest or procedure missing).
#>
[CmdletBinding()]
param(
    [string] $Parent = 'SP-0139',
    [string] $RepoRoot = (Join-Path $PSScriptRoot '..' '..'),
    [string[]] $Manifest,
    [string] $TriagePolicy = 'Every finding, at every severity, ends in a specification: a new `Draft` ticket, or an added requirement on the ticket that already owns the symptom. No finding is fixed inside the slice (owner decision of 2026-09-26: "need the specifications for any issue").',
    [string] $Date = (Get-Date -Format 'yyyy-MM-dd'),
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AuditCampaign.psm1') -Force

function Fail([string] $Message) {
    [Console]::Out.WriteLine("audit-tickets: CANNOT CREATE ($Message)")
    exit 2
}

$root = (Resolve-Path -LiteralPath $RepoRoot).Path
$planRoot = Join-Path $root 'PLAN'
if (-not (Test-Path -LiteralPath $planRoot -PathType Container)) { Fail 'no PLAN folder' }

if (-not $Manifest) {
    $parentTicket = Get-ChildItem -LiteralPath $planRoot -Filter "${Parent}_*.md" -File | Select-Object -First 1
    if ($null -eq $parentTicket) { Fail "no PLAN/${Parent}_*.md" }
    $Manifest = @(Join-Path $parentTicket.DirectoryName $parentTicket.BaseName 'research' 'audit-slices.json')
}
$slices = [System.Collections.Generic.List[object]]::new()
$sliceCount = 0
foreach ($path in $Manifest) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "manifest '$path' not found" }
    $m = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($m.parent -ne $Parent) { Fail "manifest '$path' belongs to $($m.parent), not $Parent" }
    foreach ($s in $m.slices) { $slices.Add([pscustomobject]@{ Slice = $s; Of = $m.slices.Count; Tail = [bool]$m.tail }) }
    $sliceCount += $m.slices.Count
}

$methodPath = Join-Path $root 'docs/agent/CODE_AUDIT.md'
if (-not (Test-Path -LiteralPath $methodPath)) { Fail 'docs/agent/CODE_AUDIT.md not found' }
$method = [System.IO.File]::ReadAllText($methodPath).Replace("`r`n", "`n")
$pm = [regex]::Match($method, '(?s)<!-- slice-procedure:begin -->\n(.*?)<!-- slice-procedure:end -->')
if (-not $pm.Success) { Fail 'the slice procedure markers are missing from docs/agent/CODE_AUDIT.md' }
$procedure = $pm.Groups[1].Value.TrimEnd() -replace '(?m)^### Slice procedure', '## Procedure'

# --- existing tickets --------------------------------------------------------------------------------------
$existing = @{}
$maxId = 0
foreach ($dir in @($planRoot, (Join-Path $planRoot 'DONE'))) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    foreach ($item in Get-ChildItem -LiteralPath $dir) {
        $idm = [regex]::Match($item.Name, '^SP-(\d{4})')
        if ($idm.Success) { $maxId = [Math]::Max($maxId, [int]$idm.Groups[1].Value) }
        if (-not $item.PSIsContainer -and $item.Name -like 'SP-*.md') {
            $line = Select-String -LiteralPath $item.FullName -Pattern '^\*\*Audit slice:\*\*\s*(.+?)\s*$' | Select-Object -First 1
            if ($line) { $existing[$line.Matches[0].Groups[1].Value] = [pscustomobject]@{ Id = $item.Name.Substring(0, 7); File = $item.Name; Dir = $dir } }
        }
    }
}

# --- known findings from earlier dossiers ------------------------------------------------------------------
$dossierFindings = [System.Collections.Generic.List[object]]::new()
$tempRoot = Join-Path $root 'temp'
if (Test-Path -LiteralPath $tempRoot) {
    foreach ($dossier in Get-ChildItem -LiteralPath $tempRoot -Directory -Filter 'audit-*' | Sort-Object Name) {
        $file = Join-Path $dossier.FullName 'FINDINGS.md'
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $ticket = ''
        $current = $null
        foreach ($line in [System.IO.File]::ReadAllLines($file)) {
            $h = [regex]::Match($line, '^## (SP-\d{4})\b')
            if ($h.Success) { $ticket = $h.Groups[1].Value; $current = $null; continue }
            $b = [regex]::Match($line, '^- \*\*([A-Z]-\d{2}(?:\s*/\s*[A-Z]-\d{2})*)[^(]*\(([HML])')
            if ($b.Success) {
                $current = [pscustomobject]@{ Dossier = $dossier.Name; Id = ($b.Groups[1].Value -replace '\s', ''); Severity = $b.Groups[2].Value; Ticket = $ticket; Text = $line }
                $dossierFindings.Add($current)
                continue
            }
            if ($null -ne $current -and $line -match '^\s{2,}\S') { $current.Text += ' ' + $line.Trim() } else { $current = $null }
        }
    }
}

function Get-KnownFindings([string] $Path) {
    $name = [System.IO.Path]::GetFileName($Path)
    $pattern = '(?<![\w.])' + [regex]::Escape($name) + '(?!\w)(?!\.\w)'
    @($dossierFindings | Where-Object { $_.Text -match $pattern } | ForEach-Object { "$($_.Dossier) $($_.Id) ($($_.Severity)) -> $($_.Ticket)" })
}

function Format-Signals($Signals) {
    $parts = foreach ($p in $Signals.PSObject.Properties) { if ([int]$p.Value -gt 0) { "$($p.Name) $($p.Value)" } }
    if (-not $parts) { return '-' }
    return ($parts -join ', ')
}

# --- create ------------------------------------------------------------------------------------------------
$created = 0
$rows = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $slices) {
    $s = $entry.Slice
    if ($existing.ContainsKey($s.name)) {
        $e = $existing[$s.name]
        $rel = if ($e.Dir -like '*DONE') { "DONE/$($e.File)" } else { $e.File }
        $rows.Add("| | [$($e.Id)]($rel) Audit slice - $($s.name) | Should | - | ``Tactical`` | ``/streamsplayer-spec-dev`` |")
        continue
    }
    $maxId++
    $id = 'SP-{0:D4}' -f $maxId
    $base = "${id}_$($s.slug)"
    $ticketPath = Join-Path $planRoot "$base.md"
    $researchDir = Join-Path $planRoot $base 'research'

    $fileRows = foreach ($f in $s.files) { "| ``$($f.path)`` | $($f.lines) | $(Format-Signals $f.signals) |" }
    $context = if (@($s.context).Count) { (@($s.context) | ForEach-Object { "``$_``" }) -join ', ' } else { 'none' }
    $siblings = if (@($s.siblings).Count) { (@($s.siblings) | ForEach-Object { "``$_``" }) -join ', ' } else { 'none' }
    $known = foreach ($f in $s.files) {
        $hits = @(Get-KnownFindings $f.path)
        if ($hits.Count) { "- ``$($f.path)``: " + ($hits -join '; ') }
    }
    if (-not $known) { $known = @('- none of the audited files is named by an earlier dossier finding') }
    $coverage = foreach ($f in $s.files) { "| ``$($f.path)`` | $($f.lines) | not read | - |" }
    $rank = if ($entry.Tail) { "tail manifest, rank $($s.index) of $($entry.Of)" } else { "rank $($s.index) of $($entry.Of) in the campaign manifest" }

    $ticket = @"
# ${id}: Audit slice - $($s.name)

**Status:** Tactical - plan inline; created by the $Parent fan-out on $Date.
**Audit slice:** $($s.name)
**Campaign:** $Parent (full code audit before the next release). Context only - this ticket is executable on its own.
**Risk:** $($s.risk) ($rank)
**Scope:** $($s.fileCount) audited files, $($s.lines) lines. Read for context, not audited: $context. Sibling slices: $siblings.
**Research:** [research/slice.md]($base/research/slice.md) (files, signals, known findings), [research/findings.md]($base/research/findings.md) (coverage and findings)

## Goal

Every audited file of this slice is read line by line against the audit layers of the procedure below; every
finding is recorded with a severity and a confidence and ends with an owner, so that this slice can say of each
file that it was audited, and nothing it found is left without a specification.

## Audited files

| File | Lines | Risk signals |
| --- | ---: | --- |
$($fileRows -join "`n")

## Triage policy

$TriagePolicy

$procedure

## Plan

### Phase 1 - Pre-scan

- [ ] 1.1 Baseline green. Verify: ``pwsh -NoProfile -File ./build.ps1 -Test -Deploy:`$false`` exits 0 on the tree
  being audited; record ``expected: exit 0 | actual: ..``. A campaign-wide baseline run on the same tree counts.
- [ ] 1.2 Known findings and signals reviewed. Verify: ``research/slice.md`` has its "Known findings" section and
  each audited file's signal counts.

### Phase 2 - Layered read and record

- [ ] 2.1 Every audited file read in full against every applicable layer. Verify: the coverage table in
  ``research/findings.md`` has $($s.fileCount) rows and none reads ``not read``.
- [ ] 2.2 Every finding recorded. Verify: each findings row names file and line, layer, severity, confidence and
  cost to the user.

### Phase 3 - Triage

- [ ] 3.1 Dedup search per finding across ``PLAN/``, ``PLAN/DONE/`` and ``temp/audit-*/FINDINGS.md``. Verify: every
  findings row's Spec column names an SP id.
- [ ] 3.2 Every named SP id exists, and each new ticket has a release-queue row. Verify:
  ``pwsh -NoProfile -File tools/audit/Get-AuditCampaign.ps1 -Parent $Parent`` reports no unknown ticket for this
  slice.

### Phase 4 - Close

- [ ] 4.1 ``## Last Audit`` in the fixed shape of procedure step 5. Verify: the summary lists this slice with its
  counts.
- [ ] 4.2 Status ``Verified``; the ticket and its folder move to ``PLAN/DONE/``; the queue row is updated.

## Done criteria

- $($s.fileCount) of $($s.fileCount) audited files read in full, recorded in the coverage table.
- Every finding has a specification that exists in ``PLAN/`` or ``PLAN/DONE/``.
- ``## Last Audit`` present in the fixed shape.
"@

    $sliceDoc = @"
# $($s.name) - slice research

Written by the $Parent fan-out on $Date from the campaign manifest; the counts are the slicer's regex hints,
not findings.

- Project: $($s.project). Families: $((@($s.families) | ForEach-Object { "``$_``" }) -join ', ').
- Audited: $($s.fileCount) files, $($s.lines) lines. Risk $($s.risk) ($rank).
- Read for context, not audited: $context.
- Sibling slices: $siblings.

## Files

| File | Lines | Risk signals |
| --- | ---: | --- |
$($fileRows -join "`n")

## Slice signal totals

$(Format-Signals $s.signals)

## Known findings

From the earlier audit dossiers (``temp/audit-*/FINDINGS.md``), by file. A known finding is linked, not
re-reported; new evidence for it is one added line on its ticket.

$($known -join "`n")
"@

    $findingsDoc = @"
# $($s.name) - findings

## Coverage

| File | Lines | Read | Findings |
| --- | ---: | --- | ---: |
$($coverage -join "`n")

## Findings

| # | File:line | Layer | Severity | Confidence | Finding and cost to the user | Spec |
| --- | --- | --- | --- | --- | --- | --- |
"@

    if ($DryRun) {
        "would create $id  $($s.name)"
    } else {
        New-Item -ItemType Directory -Path $researchDir -Force | Out-Null
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($ticketPath, $ticket.Replace("`r`n", "`n") + "`n", $utf8)
        [System.IO.File]::WriteAllText((Join-Path $researchDir 'slice.md'), $sliceDoc.Replace("`r`n", "`n") + "`n", $utf8)
        [System.IO.File]::WriteAllText((Join-Path $researchDir 'findings.md'), $findingsDoc.Replace("`r`n", "`n") + "`n", $utf8)
        "created $id  $($s.name)"
    }
    $created++
    $existing[$s.name] = [pscustomobject]@{ Id = $id; File = "$base.md"; Dir = $planRoot }
    $rows.Add("| | [$id]($base.md) Audit slice - $($s.name) | Should | - | ``Tactical`` | ``/streamsplayer-spec-dev`` |")
}

''
'Release-queue rows (manifest order; the owner places and orders them):'
$rows
''
$verb = if ($DryRun) { 'would create' } else { 'created' }
"audit-tickets: OK ($verb $created, $($sliceCount - $created) already had a ticket)"
exit 0
