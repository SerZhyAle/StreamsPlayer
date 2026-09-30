#requires -Version 7.0
<#
.SYNOPSIS
    SP-0139: measure an audit campaign - coverage, slices by status, findings by severity, the tickets it produced.

.DESCRIPTION
    The campaign's state is read from the tree and the tickets, never from what a ticket says about itself:

      coverage   audited files in the tree that no slice holds (uncovered), files two slices hold, and files a
                 slice holds that are gone from the tree (vanished - informational)
      slices     each slice's ticket (the one carrying `**Audit slice:** <name>` in PLAN/ or PLAN/DONE/) and its
                 first **Status:** line; a slice without a ticket is open
      findings   the `**Findings:** High n | Medium n | Low n` line of each slice's `## Last Audit`
      tickets    every SP id on a slice's `**Tickets:**` line, with its status; an id with no ticket is an error

    The campaign is closed when every slice is Verified or Archived with a Last Audit, nothing is uncovered or
    held twice, every slice with a High or Medium finding names a ticket, and every named ticket exists.

.PARAMETER Parent
    The umbrella ticket id.

.PARAMETER Manifest
    The manifests to read. Defaults to every audit-slices*.json in the parent ticket's research folder.

.OUTPUTS
    Exit 0 - campaign closed; 1 - slices open or a coverage/ticket gap; 2 - cannot verify.
    The last line is the verdict: `audit-campaign: CLOSED|OPEN (..)|CANNOT VERIFY (..)`.
#>
[CmdletBinding()]
param(
    [string] $Parent = 'SP-0139',
    [string] $RepoRoot = (Join-Path $PSScriptRoot '..' '..'),
    [string[]] $Manifest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AuditCampaign.psm1') -Force

function Fail([string] $Message) {
    [Console]::Out.WriteLine("audit-campaign: CANNOT VERIFY ($Message)")
    exit 2
}

$root = (Resolve-Path -LiteralPath $RepoRoot).Path
$planRoot = Join-Path $root 'PLAN'
if (-not (Test-Path -LiteralPath $planRoot -PathType Container)) { Fail 'no PLAN folder' }

if (-not $Manifest) {
    $parentTicket = @(Get-AuditPlanTickets -PlanRoot $planRoot | Where-Object Id -eq $Parent) | Select-Object -First 1
    if ($null -eq $parentTicket) { Fail "no ticket $Parent" }
    $research = Join-Path ([System.IO.Path]::GetDirectoryName($parentTicket.Path)) ([System.IO.Path]::GetFileNameWithoutExtension($parentTicket.Path)) 'research'
    $Manifest = @(Get-ChildItem -LiteralPath $research -Filter 'audit-slices*.json' -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object FullName)
    if ($Manifest.Count -eq 0) { Fail "no audit-slices*.json under $research" }
}

$slices = [System.Collections.Generic.List[object]]::new()
$classB = $false
foreach ($path in $Manifest) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "manifest '$path' not found" }
    try { $m = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { Fail "manifest '$path' is not JSON" }
    if ($m.parent -ne $Parent) { Fail "manifest '$path' belongs to $($m.parent), not $Parent" }
    if ($m.classB) { $classB = $true }
    foreach ($s in $m.slices) { $slices.Add($s) }
}
if ($slices.Count -eq 0) { Fail 'the manifests hold no slice' }

# --- coverage ----------------------------------------------------------------------------------------------
$holder = @{}
$twice = [System.Collections.Generic.List[string]]::new()
foreach ($s in $slices) {
    foreach ($f in $s.files) {
        if ($holder.ContainsKey($f.path)) { $twice.Add("$($f.path) ($($holder[$f.path]), $($s.name))") } else { $holder[$f.path] = $s.name }
    }
}
$tree = @(Get-AuditFileSet -RepoRoot $root -IncludeClassB:$classB | ForEach-Object Path)
$treeSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$tree, [System.StringComparer]::Ordinal)
$uncovered = @($tree | Where-Object { -not $holder.ContainsKey($_) })
$vanished = @($holder.Keys | Where-Object { -not $treeSet.Contains($_) } | Sort-Object)

# --- tickets -----------------------------------------------------------------------------------------------
$tickets = @{}
$sliceTicket = @{}
foreach ($t in Get-AuditPlanTickets -PlanRoot $planRoot) {
    $tickets[$t.Id] = $t
    $line = Select-String -LiteralPath $t.Path -Pattern '^\*\*Audit slice:\*\*\s*(.+?)\s*$' | Select-Object -First 1
    if ($line) { $sliceTicket[$line.Matches[0].Groups[1].Value] = $t }
}

function Read-LastAudit([string] $Path) {
    $text = [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $section = [regex]::Match($text, '(?ms)^## Last Audit\s*\n(.*?)(?=^## |\z)')
    if (-not $section.Success) { return $null }
    $body = $section.Groups[1].Value
    $f = [regex]::Match($body, '(?m)^\*\*Findings:\*\*\s*High\s+(\d+)\s*\|\s*Medium\s+(\d+)\s*\|\s*Low\s+(\d+)')
    $t = [regex]::Match($body, '(?m)^\*\*Tickets:\*\*\s*(.*)$')
    $c = [regex]::Match($body, '(?m)^\*\*Coverage:\*\*\s*(\d+)\s+of\s+(\d+)')
    [pscustomobject]@{
        Parsed  = $f.Success -and $t.Success
        High    = if ($f.Success) { [int]$f.Groups[1].Value } else { 0 }
        Medium  = if ($f.Success) { [int]$f.Groups[2].Value } else { 0 }
        Low     = if ($f.Success) { [int]$f.Groups[3].Value } else { 0 }
        Tickets = [string[]]@(if ($t.Success) { [regex]::Matches($t.Groups[1].Value, 'SP-\d{4}') | ForEach-Object Value })
        Read    = if ($c.Success) { [int]$c.Groups[1].Value } else { -1 }
        Of      = if ($c.Success) { [int]$c.Groups[2].Value } else { -1 }
    }
}

$closedStates = @('Verified', 'Archived')
$byStatus = [ordered]@{}
$totals = [ordered]@{ High = 0; Medium = 0; Low = 0 }
$produced = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
$problems = [System.Collections.Generic.List[string]]::new()
$open = [System.Collections.Generic.List[string]]::new()
$rowsOut = [System.Collections.Generic.List[string]]::new()

foreach ($s in ($slices | Sort-Object { [int]$_.index }, name)) {
    $t = $sliceTicket[$s.name]
    if ($null -eq $t) {
        $status = 'NoTicket'
        $open.Add($s.name)
        $rowsOut.Add(('  {0,-9} {1,-12} {2}' -f '-', $status, $s.name))
    } else {
        $status = Get-AuditTicketStatus -TicketPath $t.Path
        if (-not $status) { $status = 'Unknown' }
        $audit = Read-LastAudit $t.Path
        $counts = '-'
        if ($null -ne $audit -and $audit.Parsed) {
            $totals.High += $audit.High; $totals.Medium += $audit.Medium; $totals.Low += $audit.Low
            foreach ($id in $audit.Tickets) {
                [void]$produced.Add($id)
                if (-not $tickets.ContainsKey($id)) { $problems.Add("$($t.Id) names $id, which has no ticket") }
            }
            if (($audit.High + $audit.Medium) -gt 0 -and $audit.Tickets.Count -eq 0) {
                $problems.Add("$($t.Id) has High/Medium findings and names no ticket")
            }
            if ($audit.Of -ge 0 -and $audit.Read -lt $audit.Of) { $problems.Add("$($t.Id) read $($audit.Read) of $($audit.Of) files") }
            $counts = "H$($audit.High) M$($audit.Medium) L$($audit.Low)"
        }
        if ($closedStates -contains $status) {
            if ($null -eq $audit -or -not $audit.Parsed) { $problems.Add("$($t.Id) is $status without a parsable Last Audit") }
        } else {
            $open.Add("$($t.Id) $($s.name)")
        }
        $rowsOut.Add(('  {0,-9} {1,-12} {2,-12} {3}' -f $t.Id, $status, $counts, $s.name))
    }
    if (-not $byStatus.Contains($status)) { $byStatus[$status] = 0 }
    $byStatus[$status]++
}

# --- report ------------------------------------------------------------------------------------------------
$fileCount = $holder.Count
"campaign $Parent - $($slices.Count) slices, $fileCount files in $($Manifest.Count) manifest(s)"
"coverage: $($uncovered.Count) uncovered, $($twice.Count) in two slices, $($vanished.Count) vanished"
foreach ($u in $uncovered) { "  uncovered: $u" }
foreach ($d in $twice) { "  twice: $d" }
foreach ($v in $vanished) { "  vanished: $v" }
'slices: ' + (($byStatus.Keys | ForEach-Object { "$_ $($byStatus[$_])" }) -join ' | ')
$rowsOut
"findings: High $($totals.High) | Medium $($totals.Medium) | Low $($totals.Low)"
if ($produced.Count) {
    'tickets: ' + (($produced | ForEach-Object {
        if ($tickets.ContainsKey($_)) { "$_ ($(Get-AuditTicketStatus -TicketPath $tickets[$_].Path))" } else { "$_ (missing)" }
    }) -join ', ')
} else { 'tickets: none' }
foreach ($p in $problems) { "  problem: $p" }

if ($open.Count -eq 0 -and $uncovered.Count -eq 0 -and $twice.Count -eq 0 -and $problems.Count -eq 0) {
    'audit-campaign: CLOSED'
    exit 0
}
$why = @()
if ($open.Count) { $why += "$($open.Count) slice(s) open" }
if ($uncovered.Count) { $why += "$($uncovered.Count) uncovered" }
if ($twice.Count) { $why += "$($twice.Count) in two slices" }
if ($problems.Count) { $why += "$($problems.Count) problem(s)" }
"audit-campaign: OPEN ($($why -join ', '))"
exit 1
