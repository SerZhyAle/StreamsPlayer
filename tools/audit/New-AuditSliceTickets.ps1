#requires -Version 7.0
<#
.SYNOPSIS
    SP-0139: create one self-contained audit ticket per slice of the campaign manifest, in risk order.

.DESCRIPTION
    Reads the manifest written by New-AuditSlices.ps1 and, for every slice that has no ticket yet, creates:

      PLAN/<id>_<slug>.md                      the ticket: goal, audited files, triage policy, the slice
                                               procedure copied from docs/agent/CODE_AUDIT.md, inline phases
      PLAN/<id>_<slug>/research/slice.md       files, lines, signals, context, siblings, known findings
      PLAN/<id>_<slug>/research/findings.md    the coverage and findings tables the audit fills in

    The id is never chosen here. The harness's capture-draft.ps1 allocates it inside the catalog lock, writes the
    ledger row (source `campaign`) and the spec file; this script then replaces the file's body with the slice
    ticket, writes the research files, and moves the ticket to `Tactical` with update.ps1 (the plan is inside the
    ticket) and a title. The id shape is the project profile's (grammar.ticketIdPattern).

    Idempotent by campaign and slice: a ticket under PLAN/ or PLAN/DONE/ that carries `**Audit slice:** <name>`
    and this campaign's `**Campaign:** <parent>` line is that slice's ticket. A ticket of another campaign that
    repeats the slice name is not, because the next campaign slices the same files into the same names. A second
    run creates nothing. A run that stops half-way (the harness refused a slice) leaves the tickets already made;
    run it again after the cause is gone.

    The script never edits the release queue: PLAN/RELEASE_QUEUE.md is a projection of the ledger, which the new
    rows reach by themselves, in risk order, and the owner orders the queue.

.PARAMETER Parent
    The umbrella ticket id, in the shape the project profile declares (or one of its legacy shapes).

.PARAMETER Manifest
    One or more manifests (the first slicing, then any tail manifests). Defaults to the parent ticket's
    research/audit-slices.json.

.PARAMETER TriagePolicy
    The umbrella's triage policy, copied into every ticket.

.PARAMETER Date
    The creation date written into the tickets (default: today, yyyy-MM-dd).

.PARAMETER DryRun
    Print what would be created; write nothing and start no harness script.

.PARAMETER CaptureScript
    The harness's capture-draft.ps1. Default: found by Find-AuditHarnessScript (CLAUDE_PLUGIN_ROOT, then the newest
    installed sza plugin). The fixture test passes a stand-in.

.PARAMETER StatusScript
    The harness's update.ps1. Default: the one next to the capture script.

.OUTPUTS
    Exit 0 - done (created or nothing to create); 2 - cannot (manifest, procedure, profile or harness missing, or
    the harness refused a slice; the message names the ticket left behind, if any).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Parent,
    [string] $RepoRoot = (Join-Path $PSScriptRoot '..' '..'),
    [string[]] $Manifest,
    [string] $TriagePolicy = 'Every finding, at every severity, ends in a specification: a new `Draft` ticket, or an added requirement on the ticket that already owns the symptom. No finding is fixed inside the slice (owner decision of 2026-09-26: "need the specifications for any issue").',
    [string] $Date = (Get-Date -Format 'yyyy-MM-dd'),
    [switch] $DryRun,
    [string] $CaptureScript,
    [string] $StatusScript
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
try { $grammar = Get-AuditTicketGrammar -RepoRoot $root } catch { Fail $_.Exception.Message }
if (-not (Test-AuditTicketId -Grammar $grammar -Id $Parent)) { Fail "parent '$Parent' is not a ticket id of this repository ($($grammar.Display))" }

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
# Keyed by campaign and slice name ("<campaign>|<slice>"): a name alone would hand this campaign the closed
# tickets of an earlier campaign that sliced the same files into the same names.
$existing = @{}
foreach ($t in Get-AuditPlanTickets -PlanRoot $planRoot -Grammar $grammar) {
    $line = Select-String -LiteralPath $t.Path -Pattern '^\*\*Audit slice:\*\*\s*(.+?)\s*$' | Select-Object -First 1
    $campaign = Select-String -LiteralPath $t.Path -Pattern '^\*\*Campaign:\*\*\s*(\S+)' | Select-Object -First 1
    if ($line -and $campaign) { $existing["$($campaign.Matches[0].Groups[1].Value)|$($line.Matches[0].Groups[1].Value)"] = [pscustomobject]@{ Id = $t.Id } }
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
            $h = [regex]::Match($line, '^## (' + $grammar.Fragment + ')\b')
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

# --- harness -----------------------------------------------------------------------------------------------
# A harness script runs as a child with the repository as its project root and the ticket source `campaign`
# (update.ps1 has no -ProjectRoot, and capture-draft.ps1 has no -Source; both read these variables). The
# caller's values are put back afterwards.
function Invoke-Harness([string] $Script, [string[]] $Arguments) {
    $variables = [ordered]@{ SZA_PROJECT_ROOT = $root; SZA_TICKET_SOURCE = 'campaign' }
    $saved = @{}
    foreach ($name in $variables.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process')
    }
    try {
        $out = & (Get-Process -Id $PID).Path -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ Exit = $LASTEXITCODE; Output = @($out) }
    } finally {
        foreach ($name in $variables.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}

# Not named like the parameters: variable names are case-insensitive, and a [string] parameter turns $null into an empty string.
$captureTool = $null
$statusTool = $null

# --- create ------------------------------------------------------------------------------------------------
$created = 0
foreach ($entry in $slices) {
    $s = $entry.Slice
    $key = "$Parent|$($s.name)"
    if ($existing.ContainsKey($key)) { continue }

    if ($DryRun) {
        "would create  $($s.name)"
        $created++
        $existing[$key] = [pscustomobject]@{ Id = '(dry run)' }
        continue
    }

    if ($null -eq $captureTool) {
        $captureTool = Find-AuditHarnessScript -RelativePath 'tools/harness/spec_catalog/capture-draft.ps1' -Explicit $CaptureScript
        if (-not $captureTool -or -not (Test-Path -LiteralPath $captureTool -PathType Leaf)) {
            Fail 'capture-draft.ps1 of the sza plugin was not found - install the plugin or pass -CaptureScript'
        }
        $statusTool = if ($StatusScript) { $StatusScript } else { Join-Path (Split-Path -Parent $captureTool) 'update.ps1' }
        if (-not (Test-Path -LiteralPath $statusTool -PathType Leaf)) { Fail "update.ps1 not found at '$statusTool' - pass -StatusScript" }
    }

    # The ledger refuses an active ticket whose name repeats, and a Verified one is still active: the name
    # carries the campaign so that the next campaign's slice of the same family does not clash with this one's.
    $complexity = "medium - read-only audit of $($s.fileCount) files, $($s.lines) lines; findings become tickets, no source is edited"
    $draft = Invoke-Harness $captureTool @(
        '-Slug', $s.slug,
        '-Name', "$($Parent.ToLowerInvariant())-$($s.slug)",
        '-Text', "Audit slice $($s.name) of campaign $Parent. Created by tools/audit/New-AuditSliceTickets.ps1 on $Date; the generated slice ticket replaces this text.",
        '-Complexity', $complexity)
    if ($draft.Exit -ne 0) { Fail "capture-draft refused slice '$($s.name)' (exit $($draft.Exit)): $($draft.Output -join ' | ')" }
    $id = @($draft.Output | ForEach-Object { $_.Trim() } | Where-Object { Test-AuditTicketId -Grammar $grammar -Id $_ }) | Select-Object -Last 1
    if (-not $id) { Fail "capture-draft exited 0 but printed no ticket id for slice '$($s.name)': $($draft.Output -join ' | ')" }
    $base = "${id}_$($s.slug)"
    $ticketPath = Join-Path $planRoot "$base.md"
    $researchDir = Join-Path $planRoot $base 'research'
    if (-not (Test-Path -LiteralPath $ticketPath -PathType Leaf)) { Fail "capture-draft created $id but not PLAN/$base.md" }

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

**Status:** Draft
**Created:** $Date
**Complexity:** $complexity
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
  being audited; record ``expected: exit 0 | actual: ..``. Release tier (SP-0256): the campaign-wide baseline run
  recorded on the umbrella ticket for the same tree is the evidence; a slice does not run it again.
- [ ] 1.2 Known findings and signals reviewed. Verify: ``research/slice.md`` has its "Known findings" section and
  each audited file's signal counts.

### Phase 2 - Layered read and record

- [ ] 2.1 Every audited file read in full against every applicable layer. Verify: the coverage table in
  ``research/findings.md`` has $($s.fileCount) rows and none reads ``not read``.
- [ ] 2.2 Every finding recorded. Verify: each findings row names file and line, layer, severity, confidence and
  cost to the user.

### Phase 3 - Triage

- [ ] 3.1 Dedup search per finding across ``PLAN/``, ``PLAN/DONE/`` and ``temp/audit-*/FINDINGS.md``. Verify: every
  findings row's Spec column names a ticket id.
- [ ] 3.2 Every named ticket id exists, and each new ticket was created through the ledger (``capture-draft.ps1``),
  which gives it its release-queue row. Verify:
  ``pwsh -NoProfile -File tools/audit/Get-AuditCampaign.ps1 -Parent $Parent`` reports no unknown ticket for this
  slice.

### Phase 4 - Close

- [ ] 4.1 ``## Last Audit`` in the fixed shape of procedure step 5. Verify: the summary lists this slice with its
  counts.
- [ ] 4.2 Status ``Verified``, set with the status tool (``update.ps1``); it moves the ticket and its folder to
  ``PLAN/DONE/`` and updates the queue row.

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

    # The ledger row exists from here on: a failure below leaves it, and the message names the repair.
    try {
        New-Item -ItemType Directory -Path $researchDir -Force | Out-Null
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($ticketPath, $ticket.Replace("`r`n", "`n") + "`n", $utf8)
        [System.IO.File]::WriteAllText((Join-Path $researchDir 'slice.md'), $sliceDoc.Replace("`r`n", "`n") + "`n", $utf8)
        [System.IO.File]::WriteAllText((Join-Path $researchDir 'findings.md'), $findingsDoc.Replace("`r`n", "`n") + "`n", $utf8)
    } catch {
        Fail "$id is in the ledger but its slice files were not written ($($_.Exception.Message -replace '\s+', ' ')); write PLAN/$base.md by hand or retire the row with delete.ps1 -Id $id -Confirm"
    }
    $title = "Audit slice - $($s.name)"
    if ($title.Length -gt 80) { $title = $title.Substring(0, 80).TrimEnd() }
    $move = Invoke-Harness $statusTool @('-Id', $id, '-Status', 'Tactical', '-Title', $title)
    if ($move.Exit -ne 0) {
        Fail "$id exists with its files but update.ps1 refused Tactical (exit $($move.Exit): $($move.Output -join ' | ')); run update.ps1 -Id $id -Status Tactical"
    }
    "created $id  $($s.name)"
    $created++
    $existing[$key] = [pscustomobject]@{ Id = $id }
}

''
$verb = if ($DryRun) { 'would create' } else { 'created' }
"audit-tickets: OK ($verb $created, $($sliceCount - $created) already had a ticket)"
exit 0
