#requires -Version 7.0
<#
.SYNOPSIS
    SP-0139: cut the audited files into capped, risk-ordered audit slices and write the manifest.

.DESCRIPTION
    The whole shipping tree is too much for one careful reading, so it is cut into slices that one ticket each
    can audit line by line. The cut is deterministic - two runs on the same tree write the same bytes - and
    mechanical: the self-check refuses to write a manifest in which a file is missing or counted twice.

    Membership (class A, and class B with -IncludeClassB) and the risk signals are tables in
    AuditCampaign.psm1. Grouping:

      - Library, application and harness files form families by file-name stem (the name up to its first dot),
        so a markup file, its code-behind and every `Name.Concern.cs` partial share one family; a file whose
        partial type names another family joins that family. Tooling files form families by directory.
      - A markup file and its code-behind are one unit and are never separated.
      - A family over either limit is split by unit into balanced parts, and the family root is listed as
        read-for-context in every part that does not audit it; the parts name each other as siblings.
      - The remaining families of one project are packed in ordinal order of family name into balanced slices
        up to both limits. Projects never share a slice, so the library is judged against its boundary rule
        as a whole.

    Risk per slice = every counted signal of its audited files per thousand lines, with the divisor floored
    at a quarter of -MaxLines so a tiny slice cannot outrank a large one on a single signal. Slices are
    numbered in risk order (descending; ties by name), which is the order the fan-out creates tickets in.

.PARAMETER Parent
    The umbrella ticket id (SP-NNNN). Names the manifest's parent and the default manifest location
    (PLAN/<parent ticket folder>/research/audit-slices.json).

.PARAMETER RepoRoot
    Repository root. Defaults to two levels above this script; the self-test passes a fixture tree.

.PARAMETER MaxFiles
    Cap on audited files per slice (default 25).

.PARAMETER MaxLines
    Cap on audited lines per slice (default 4000). A single unit above the cap gets a slice of its own.

.PARAMETER IncludeClassB
    Also slice class B (tests, localization dictionaries, site and Store tooling).

.PARAMETER FileList
    A text file of repo-relative paths, one per line. Only those files are sliced - the input for a tail
    slice built from the summary's uncovered files. Every path must belong to the audited set.

.PARAMETER OutJson
    Manifest path; overrides the default location.

.OUTPUTS
    Exit 0 - manifest written; 2 - cannot slice (bad input, or the self-check failed).
#>
[CmdletBinding()]
param(
    [string] $Parent = 'SP-0139',
    [string] $RepoRoot = (Join-Path $PSScriptRoot '..' '..'),
    [ValidateRange(1, 1000)][int] $MaxFiles = 25,
    [ValidateRange(100, 1000000)][int] $MaxLines = 4000,
    [switch] $IncludeClassB,
    [string] $FileList,
    [string] $OutJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AuditCampaign.psm1') -Force

function Fail([string] $Message) {
    # Straight to the console: a Fail inside a pipeline must not be captured into a variable and lost.
    [Console]::Out.WriteLine("audit-slices: CANNOT SLICE ($Message)")
    exit 2
}

$root = (Resolve-Path -LiteralPath $RepoRoot).Path
if ($Parent -notmatch '^SP-\d{4}$') { Fail "parent '$Parent' is not an SP-NNNN id" }

# --- enumerate ---------------------------------------------------------------------------------------------
$fileSet = @(Get-AuditFileSet -RepoRoot $root -IncludeClassB:$IncludeClassB)
if ($FileList) {
    if (-not (Test-Path -LiteralPath $FileList -PathType Leaf)) { Fail "file list '$FileList' not found" }
    $known = @{}
    foreach ($f in @(Get-AuditFileSet -RepoRoot $root -IncludeClassB)) { $known[$f.Path] = $f }
    $wanted = @(Get-Content -LiteralPath $FileList | ForEach-Object { $_.Trim().Replace('\', '/') } |
        Where-Object { $_ -ne '' } | Sort-Object -Unique -CaseSensitive)
    $fileSet = foreach ($p in $wanted) {
        if (-not $known.ContainsKey($p)) { Fail "'$p' is not an audited file" }
        $known[$p]
    }
    $fileSet = @($fileSet)
}
if ($fileSet.Count -eq 0) { Fail 'no audited files found' }

$measured = @{}
foreach ($f in $fileSet) { $measured[$f.Path] = Measure-AuditFile -RepoRoot $root -Path $f.Path }

# --- families and units ------------------------------------------------------------------------------------
$stemProjects = @('Core', 'App', 'Harness', 'Tests', 'Localization')

function Get-Stem([string] $Path) {
    $name = [System.IO.Path]::GetFileName($Path)
    return $name.Split('.')[0]
}

function Get-FamilyKey($File) {
    if ($stemProjects -contains $File.Project) { return Get-Stem $File.Path }
    $dir = [System.IO.Path]::GetDirectoryName($File.Path)
    if ([string]::IsNullOrEmpty($dir)) { return '(root)' }
    return $dir.Replace('\', '/')
}

$families = [ordered]@{}   # "<project>|<family>" -> list of paths
foreach ($f in $fileSet) {
    $key = "$($f.Project)|$(Get-FamilyKey $f)"
    if (-not $families.Contains($key)) { $families[$key] = [System.Collections.Generic.List[string]]::new() }
    $families[$key].Add($f.Path)
}
# A partial type declared under another family's name joins that family.
$projectOf = @{}; foreach ($f in $fileSet) { $projectOf[$f.Path] = $f.Project }
foreach ($f in $fileSet) {
    if ($stemProjects -notcontains $f.Project) { continue }
    $partial = $measured[$f.Path].PartialType
    $own = "$($f.Project)|$(Get-Stem $f.Path)"
    $target = "$($f.Project)|$partial"
    if ($partial -and $target -ne $own -and $families.Contains($target)) {
        [void]$families[$own].Remove($f.Path)
        $families[$target].Add($f.Path)
    }
}
foreach ($k in @($families.Keys)) { if ($families[$k].Count -eq 0) { $families.Remove($k) } }

function Get-FamilyRoot([string] $Project, [string] $Family, [string[]] $Paths) {
    if ($stemProjects -notcontains $Project) { return $null }
    foreach ($suffix in @('.xaml.cs', '.cs', '.xaml')) {
        $hit = $Paths | Where-Object { [System.IO.Path]::GetFileName($_) -ceq "$Family$suffix" } | Select-Object -First 1
        if ($hit) { return $hit }
    }
    return $null
}

# Units: X.xaml + X.xaml.cs together; everything else alone. The unit holding the family root comes first.
function Get-Units([string[]] $Paths, [string] $RootPath) {
    $sorted = [string[]]$Paths.Clone(); [System.Array]::Sort($sorted, [System.StringComparer]::Ordinal)
    $taken = @{}
    $units = [System.Collections.Generic.List[object]]::new()
    foreach ($p in $sorted) {
        if ($taken.ContainsKey($p)) { continue }
        $members = @($p)
        if ($p -like '*.xaml') {
            $codeBehind = "$p.cs"
            if ($sorted -ccontains $codeBehind) { $members += $codeBehind }
        } elseif ($p -like '*.xaml.cs') {
            $markup = $p.Substring(0, $p.Length - 3)
            if ($sorted -ccontains $markup) { $members = @($markup, $p) }
        }
        foreach ($m in $members) { $taken[$m] = $true }
        $lines = ($members | ForEach-Object { $measured[$_].Lines } | Measure-Object -Sum).Sum
        $units.Add([pscustomobject]@{ Paths = $members; Lines = [int]$lines; Files = $members.Count })
    }
    if ($RootPath) {
        $rootUnit = $units | Where-Object { $_.Paths -ccontains $RootPath } | Select-Object -First 1
        if ($rootUnit) { [void]$units.Remove($rootUnit); $units.Insert(0, $rootUnit) }
    }
    return , $units
}

# Balanced next-fit: the fewest bins next-fit needs at the limit, then the lowest target that still needs no
# more bins - which spreads the lines evenly instead of leaving a two-file remainder.
function Split-Balanced($Items) {
    function NextFit($Items, [int] $Target) {
        $bins = [System.Collections.Generic.List[object]]::new()
        $cur = $null
        foreach ($it in $Items) {
            if ($null -ne $cur -and (($cur.Lines + $it.Lines) -gt $Target -or ($cur.Files + $it.Files) -gt $MaxFiles)) {
                $bins.Add($cur); $cur = $null
            }
            if ($null -eq $cur) { $cur = [pscustomobject]@{ Items = [System.Collections.Generic.List[object]]::new(); Lines = 0; Files = 0 } }
            $cur.Items.Add($it); $cur.Lines += $it.Lines; $cur.Files += $it.Files
        }
        if ($null -ne $cur) { $bins.Add($cur) }
        return , $bins
    }
    $atLimit = NextFit $Items $MaxLines
    $n = $atLimit.Count
    if ($n -le 1) { return , $atLimit }
    $total = ($Items | Measure-Object -Property Lines -Sum).Sum
    $step = [Math]::Max(1, [int][Math]::Ceiling($MaxLines / 200))
    for ($t = [int][Math]::Ceiling($total / $n); $t -lt $MaxLines; $t += $step) {
        $bins = NextFit $Items $t
        if ($bins.Count -eq $n) { return , $bins }
    }
    return , $atLimit
}

# --- slices ------------------------------------------------------------------------------------------------
$slices = [System.Collections.Generic.List[object]]::new()
$projects = @($fileSet | ForEach-Object Project | Select-Object -Unique)
$projectOrder = @('Core', 'App', 'Harness', 'Tooling', 'Tests', 'Localization', 'DevTooling')
$projects = @($projectOrder | Where-Object { $projects -contains $_ })

foreach ($project in $projects) {
    $keys = [string[]]@($families.Keys | Where-Object { $_.StartsWith("$project|", [System.StringComparison]::Ordinal) })
    [System.Array]::Sort($keys, [System.StringComparer]::Ordinal)
    $packable = [System.Collections.Generic.List[object]]::new()
    foreach ($key in $keys) {
        $family = $key.Substring($project.Length + 1)
        $paths = [string[]]$families[$key]
        $lines = [int](($paths | ForEach-Object { $measured[$_].Lines } | Measure-Object -Sum).Sum)
        $rootPath = Get-FamilyRoot $project $family $paths
        if ($lines -le $MaxLines -and $paths.Count -le $MaxFiles) {
            $sortedPaths = [string[]]$paths.Clone(); [System.Array]::Sort($sortedPaths, [System.StringComparer]::Ordinal)
            $packable.Add([pscustomobject]@{ Family = $family; Paths = $sortedPaths; Lines = $lines; Files = $paths.Count })
            continue
        }
        $units = Get-Units $paths $rootPath
        $parts = Split-Balanced $units
        $prefix = "$($project.ToLowerInvariant()):$family"
        $names = @(if ($parts.Count -eq 1) { $prefix } else { for ($i = 1; $i -le $parts.Count; $i++) { "$prefix part $i of $($parts.Count)" } })
        for ($i = 0; $i -lt $parts.Count; $i++) {
            $audited = [string[]]@($parts[$i].Items | ForEach-Object { $_.Paths })
            $context = @()
            if ($rootPath -and ($audited -cnotcontains $rootPath)) { $context = @($rootPath) }
            $slices.Add([pscustomobject]@{
                Name = $names[$i]; Project = $project; Families = @($family)
                Files = $audited; Context = $context; Siblings = @($names | Where-Object { $_ -cne $names[$i] })
                SingleUnit = ($parts[$i].Items.Count -eq 1)
            })
        }
    }
    foreach ($bin in (Split-Balanced $packable)) {
        $fams = @($bin.Items | ForEach-Object Family)
        $label = if ($fams.Count -eq 1) { $fams[0] } else { "$($fams[0])..$($fams[-1])" }
        $slices.Add([pscustomobject]@{
            Name = "$($project.ToLowerInvariant()):$label"; Project = $project; Families = $fams
            Files = [string[]]@($bin.Items | ForEach-Object { $_.Paths }); Context = @(); Siblings = @()
            SingleUnit = ($bin.Items.Count -eq 1)
        })
    }
}

# --- self-check --------------------------------------------------------------------------------------------
$owner = @{}
foreach ($s in $slices) {
    foreach ($p in $s.Files) {
        if ($owner.ContainsKey($p)) { Fail "'$p' is in two slices ($($owner[$p]), $($s.Name))" }
        $owner[$p] = $s.Name
    }
}
foreach ($f in $fileSet) { if (-not $owner.ContainsKey($f.Path)) { Fail "'$($f.Path)' is in no slice" } }
foreach ($s in $slices) {
    $lines = ($s.Files | ForEach-Object { $measured[$_].Lines } | Measure-Object -Sum).Sum
    if (-not $s.SingleUnit -and ($lines -gt $MaxLines -or $s.Files.Count -gt $MaxFiles)) {
        Fail "slice '$($s.Name)' exceeds a limit ($($s.Files.Count) files, $lines lines)"
    }
}
$names = @($slices | ForEach-Object Name)
if (@($names | Select-Object -Unique).Count -ne $names.Count) { Fail 'two slices share a name' }

# --- risk and order ----------------------------------------------------------------------------------------
$floor = [Math]::Floor($MaxLines / 4)
$rows = foreach ($s in $slices) {
    $lines = [int](($s.Files | ForEach-Object { $measured[$_].Lines } | Measure-Object -Sum).Sum)
    $totals = [ordered]@{}
    $fileRows = foreach ($p in $s.Files) {
        $m = $measured[$p]
        foreach ($k in $m.Signals.Keys) {
            if (-not $totals.Contains($k)) { $totals[$k] = 0 }
            $totals[$k] += $m.Signals[$k]
        }
        [ordered]@{ path = $p; lines = $m.Lines; signals = $m.Signals }
    }
    $signalSum = ($totals.Values | Measure-Object -Sum).Sum
    $risk = [Math]::Round(1000.0 * $signalSum / [Math]::Max($lines, $floor), 2)
    [pscustomobject]@{ Slice = $s; Lines = $lines; Totals = $totals; FileRows = @($fileRows); Risk = $risk }
}
$ordered = @($rows | Sort-Object -Property @{ Expression = 'Risk'; Descending = $true }, @{ Expression = { $_.Slice.Name }; Descending = $false })

function Get-Slug([string] $Name) {
    $s = $Name.ToLowerInvariant().Replace('..', ' to ')
    $s = [regex]::Replace($s, '[^a-z0-9]+', '_').Trim('_')
    return "audit_$s"
}

$manifestSlices = for ($i = 0; $i -lt $ordered.Count; $i++) {
    $r = $ordered[$i]
    [ordered]@{
        index    = $i + 1
        name     = $r.Slice.Name
        slug     = Get-Slug $r.Slice.Name
        project  = $r.Slice.Project
        families = @($r.Slice.Families)
        lines    = $r.Lines
        fileCount = $r.Slice.Files.Count
        risk     = $r.Risk
        signals  = $r.Totals
        files    = $r.FileRows
        context  = @($r.Slice.Context)
        siblings = @($r.Slice.Siblings)
    }
}

$totalLines = [int](($fileSet | ForEach-Object { $measured[$_.Path].Lines } | Measure-Object -Sum).Sum)
$manifest = [ordered]@{
    parent  = $Parent
    classB  = [bool]$IncludeClassB
    tail    = [bool]$FileList
    limits  = [ordered]@{ maxFiles = $MaxFiles; maxLines = $MaxLines; lineBudget = (Get-AuditLineBudget); riskDivisorFloor = [int]$floor }
    totals  = [ordered]@{ files = $fileSet.Count; lines = $totalLines; slices = $ordered.Count }
    slices  = @($manifestSlices)
}

if (-not $OutJson) {
    $ticket = Get-ChildItem -LiteralPath (Join-Path $root 'PLAN') -Filter "${Parent}_*.md" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $ticket) { Fail "no PLAN/${Parent}_*.md ticket to hold the manifest; pass -OutJson" }
    $OutJson = Join-Path $ticket.DirectoryName ([System.IO.Path]::GetFileNameWithoutExtension($ticket.Name)) 'research' 'audit-slices.json'
}
$outDir = Split-Path -Parent $OutJson
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$json = ($manifest | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n"
[System.IO.File]::WriteAllText($OutJson, $json, [System.Text.UTF8Encoding]::new($false))

'{0,3}  {1,-58} {2,5} {3,6} {4,7}' -f '#', 'slice', 'files', 'lines', 'risk'
foreach ($s in $manifest.slices) { '{0,3}  {1,-58} {2,5} {3,6} {4,7:0.00}' -f $s.index, $s.name, $s.fileCount, $s.lines, $s.risk }
"audit-slices: OK ($($fileSet.Count) files, $totalLines lines, $($ordered.Count) slices) -> $OutJson"
exit 0
