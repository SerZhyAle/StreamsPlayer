#requires -Version 7.0
<#
.SYNOPSIS
    SP-0139: fixture tests for the audit-campaign tools (slicer, fan-out, summary).

.DESCRIPTION
    Builds a small repository-shaped tree in a temporary folder, runs the three tools against it with small
    limits, and checks the properties the campaign depends on:

      slicer    every audited file in exactly one slice; limits respected; markup and code-behind together;
                an oversized family split with its root as context and its parts naming each other; a partial
                type filed under another name joins its family; small families packed; build output and class B
                left out; one project per slice; the same tree gives the same bytes; -IncludeClassB changes
                membership only; -FileList slices only the listed files
      fan-out   one Tactical ticket per slice, ids continuing after the highest existing one, the procedure
                copied in, research files written; a second run creates nothing
      summary   open while slices are open (exit 1); an uncovered file is reported; a Verified slice without a
                Last Audit, or naming a ticket that does not exist, is a problem; closed (exit 0) when all hold

    Exit 0 when every check passes, 1 otherwise. The last line is `audit-tools: PASS|FAIL (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..' '..')).Path
$slicer = Join-Path $here 'New-AuditSlices.ps1'
$fanout = Join-Path $here 'New-AuditSliceTickets.ps1'
$summary = Join-Path $here 'Get-AuditCampaign.ps1'

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}

function Invoke-Tool([string] $Script, [string[]] $Arguments) {
    $out = & pwsh -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = @($out) }
}

function New-FixtureFile([string] $Root, [string] $Path, [int] $Lines, [string] $Head = '') {
    $full = Join-Path $Root $Path
    New-Item -ItemType Directory -Path (Split-Path -Parent $full) -Force | Out-Null
    $body = [System.Collections.Generic.List[string]]::new()
    if ($Head) { $body.AddRange([string[]]$Head.Split("`n")) }
    while ($body.Count -lt $Lines) { $body.Add("// line $($body.Count + 1)") }
    [System.IO.File]::WriteAllText($full, ($body -join "`n") + "`n")
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("sp0139-audit-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    # --- fixture tree ------------------------------------------------------------------------------------------
    $core = 'src/StreamsPlayer.Core'; $app = 'src/StreamsPlayer.App'
    New-FixtureFile $fixture "$core/Alpha.cs" 60 'namespace X;'
    New-FixtureFile $fixture "$core/Beta.cs" 60 "namespace X;`nasync void Run() { }"
    New-FixtureFile $fixture "$core/Gamma.cs" 60
    New-FixtureFile $fixture "$core/Delta.cs" 250 "catch (Exception ex)`nlock (gate) { }"
    New-FixtureFile $fixture "$app/Main.xaml" 120
    New-FixtureFile $fixture "$app/Main.xaml.cs" 150 'public partial class Main { }'
    New-FixtureFile $fixture "$app/Main.Alpha.cs" 90 'public partial class Main { }'
    New-FixtureFile $fixture "$app/Main.Beta.cs" 90 'public partial class Main { }'
    New-FixtureFile $fixture "$app/Main.Gamma.cs" 90 'public partial class Main { }'
    New-FixtureFile $fixture "$app/Stray.cs" 40 'public partial class Main { }'
    New-FixtureFile $fixture "$app/Small.xaml" 20
    New-FixtureFile $fixture "$app/Small.xaml.cs" 20
    New-FixtureFile $fixture "$app/Tiny.cs" 10
    New-FixtureFile $fixture "$app/Localization.en.xaml" 30
    New-FixtureFile $fixture "$app/obj/Generated.cs" 30
    New-FixtureFile $fixture "tools/StreamsPlayer.CatalogHarness/Program.cs" 20
    New-FixtureFile $fixture 'scripts/check.ps1' 30 'Remove-Item -Force x'
    New-FixtureFile $fixture 'build.ps1' 30
    New-FixtureFile $fixture 'tests/X.Tests/FooTests.cs' 40
    New-FixtureFile $fixture 'PLAN/SP-0001_umbrella.md' 3 "# SP-0001: Umbrella`n`n**Status:** BlockByOtherTask - fixture"
    New-FixtureFile $fixture 'PLAN/DONE/SP-0005_old.md' 3 "# SP-0005: Old`n`n**Status:** Verified - fixture"
    New-Item -ItemType Directory -Path (Join-Path $fixture 'docs/agent') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'docs/agent/CODE_AUDIT.md') -Destination (Join-Path $fixture 'docs/agent/CODE_AUDIT.md')

    $classA = @(
        "$core/Alpha.cs", "$core/Beta.cs", "$core/Delta.cs", "$core/Gamma.cs",
        "$app/Main.Alpha.cs", "$app/Main.Beta.cs", "$app/Main.Gamma.cs", "$app/Main.xaml", "$app/Main.xaml.cs",
        "$app/Small.xaml", "$app/Small.xaml.cs", "$app/Stray.cs", "$app/Tiny.cs",
        'tools/StreamsPlayer.CatalogHarness/Program.cs', 'build.ps1', 'scripts/check.ps1')
    $limits = @('-MaxFiles', '4', '-MaxLines', '300')
    $manifestPath = Join-Path $fixture 'PLAN/SP-0001_umbrella/research/audit-slices.json'

    # --- slicer ------------------------------------------------------------------------------------------------
    $r = Invoke-Tool $slicer (@('-Parent', 'SP-0001', '-RepoRoot', $fixture) + $limits)
    Check ($r.Exit -eq 0) "slicer exits 0 (got $($r.Exit): $($r.Output -join ' | '))"
    $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $all = @($m.slices | ForEach-Object { $_.files } | ForEach-Object path)
    Check ($all.Count -eq $classA.Count) "every class-A file once ($($all.Count) held, $($classA.Count) expected)"
    Check (@($all | Select-Object -Unique).Count -eq $all.Count) 'no file in two slices'
    Check (@($classA | Where-Object { $all -notcontains $_ }).Count -eq 0) 'no class-A file left out'
    Check ($all -notcontains "$app/obj/Generated.cs") 'build output is not audited'
    Check ($all -notcontains "$app/Localization.en.xaml") 'dictionaries are class B'
    Check ($all -notcontains 'tests/X.Tests/FooTests.cs') 'tests are class B'

    $sliceOf = @{}; foreach ($s in $m.slices) { foreach ($f in $s.files) { $sliceOf[$f.path] = $s } }
    foreach ($s in $m.slices) {
        $paths = @($s.files | ForEach-Object path)
        $single = ($paths.Count -le 2 -and ($paths -join ',') -match '\.xaml')
        Check ($single -or ($s.lines -le 300 -and $s.fileCount -le 4)) "slice '$($s.name)' within limits"
        Check ($s.project -and $s.name.StartsWith($s.project.ToLowerInvariant() + ':')) "slice '$($s.name)' names its one project"
    }
    Check ($sliceOf["$app/Main.xaml"].name -eq $sliceOf["$app/Main.xaml.cs"].name) 'markup and code-behind together'
    Check ($sliceOf["$app/Small.xaml"].name -eq $sliceOf["$app/Small.xaml.cs"].name) 'small markup pair together'
    $mainParts = @($m.slices | Where-Object { $_.name -like 'app:Main part *' })
    Check ($mainParts.Count -ge 2) "oversized family split ($($mainParts.Count) parts)"
    foreach ($p in $mainParts) {
        $audits = @($p.files | ForEach-Object path) -contains "$app/Main.xaml.cs"
        $hasContext = @($p.context) -contains "$app/Main.xaml.cs"
        Check ($audits -xor $hasContext) "part '$($p.name)' audits the root or lists it as context"
        Check (@($p.siblings).Count -eq $mainParts.Count - 1) "part '$($p.name)' names its siblings"
    }
    Check ($sliceOf["$app/Stray.cs"].name -like 'app:Main part *') 'a partial type joins its family'
    Check ($sliceOf["$core/Alpha.cs"].name -eq $sliceOf["$core/Beta.cs"].name) 'small families packed'
    Check ($sliceOf["$core/Alpha.cs"].name -ne $sliceOf["$app/Tiny.cs"].name) 'library and application never share'
    Check ($sliceOf['build.ps1'].project -eq 'Tooling' -and $sliceOf['scripts/check.ps1'].project -eq 'Tooling') 'tooling slices'
    $ranks = @($m.slices | ForEach-Object risk)
    $sortedRanks = @($ranks | Sort-Object -Descending)
    Check ((($ranks -join ',') -eq ($sortedRanks -join ','))) 'slices numbered in descending risk'
    Check ($sliceOf["$core/Beta.cs"].signals.asyncVoid -ge 1) 'async void counted'

    $first = [System.IO.File]::ReadAllBytes($manifestPath)
    $r2 = Invoke-Tool $slicer (@('-Parent', 'SP-0001', '-RepoRoot', $fixture) + $limits)
    $second = [System.IO.File]::ReadAllBytes($manifestPath)
    Check ($r2.Exit -eq 0 -and [System.Linq.Enumerable]::SequenceEqual($first, $second)) 'same tree, same manifest bytes'

    $classBPath = Join-Path $fixture 'classb.json'
    $r3 = Invoke-Tool $slicer (@('-Parent', 'SP-0001', '-RepoRoot', $fixture, '-IncludeClassB', '-OutJson', $classBPath) + $limits)
    $mb = Get-Content -LiteralPath $classBPath -Raw | ConvertFrom-Json
    $allB = @($mb.slices | ForEach-Object { $_.files } | ForEach-Object path)
    Check ($r3.Exit -eq 0 -and $allB -contains "$app/Localization.en.xaml" -and $allB -contains 'tests/X.Tests/FooTests.cs') 'class B adds its files'
    $shapeA = @($m.slices | ForEach-Object { "$($_.name)=" + (@($_.files | ForEach-Object path) -join ',') } | Sort-Object)
    $shapeB = @($mb.slices | Where-Object { @('Core', 'App', 'Harness', 'Tooling') -contains $_.project } |
        ForEach-Object { "$($_.name)=" + (@($_.files | ForEach-Object path) -join ',') } | Sort-Object)
    Check (($shapeA -join ';') -eq ($shapeB -join ';')) 'class B changes membership, not the class-A slices'

    $listPath = Join-Path $fixture 'tail.txt'
    Set-Content -LiteralPath $listPath -Value @("$core/Gamma.cs", "$app/Tiny.cs")
    $tailPath = Join-Path $fixture 'tail.json'
    $r4 = Invoke-Tool $slicer (@('-Parent', 'SP-0001', '-RepoRoot', $fixture, '-FileList', $listPath, '-OutJson', $tailPath) + $limits)
    $mt = Get-Content -LiteralPath $tailPath -Raw | ConvertFrom-Json
    $allT = @($mt.slices | ForEach-Object { $_.files } | ForEach-Object path | Sort-Object)
    Check ($r4.Exit -eq 0 -and $mt.tail -and ($allT -join ',') -eq "$app/Tiny.cs,$core/Gamma.cs") '-FileList slices only the listed files'
    Set-Content -LiteralPath $listPath -Value @('src/Nowhere.cs')
    $r5 = Invoke-Tool $slicer (@('-Parent', 'SP-0001', '-RepoRoot', $fixture, '-FileList', $listPath, '-OutJson', $tailPath) + $limits)
    Check ($r5.Exit -eq 2) '-FileList refuses a file outside the audited set'

    # --- fan-out -----------------------------------------------------------------------------------------------
    $f1 = Invoke-Tool $fanout @('-Parent', 'SP-0001', '-RepoRoot', $fixture, '-Date', '2026-01-02')
    Check ($f1.Exit -eq 0) "fan-out exits 0 (got $($f1.Exit): $($f1.Output -join ' | '))"
    $made = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'PLAN') -Filter 'SP-*_audit_*.md' -File | Sort-Object Name)
    Check ($made.Count -eq $m.slices.Count) "one ticket per slice ($($made.Count) of $($m.slices.Count))"
    Check ($made.Count -gt 0 -and $made[0].Name.StartsWith('SP-0006_')) 'ids continue after the highest existing id'
    $firstTicket = if ($made.Count) { [System.IO.File]::ReadAllText($made[0].FullName) } else { '' }
    Check ($firstTicket -match '(?m)^\*\*Status:\*\* Tactical' -and $firstTicket -match "(?m)^\*\*Audit slice:\*\* $([regex]::Escape($m.slices[0].name))$") 'ticket carries status and slice name, risk order'
    Check ($firstTicket -match '(?m)^## Procedure' -and $firstTicket -match 'Layered read' -and $firstTicket -notmatch 'slice-procedure:') 'procedure copied in'
    Check ($firstTicket -notmatch 'SP-0001_umbrella') 'ticket does not depend on the umbrella file'
    $research = if ($made.Count) { Join-Path $made[0].DirectoryName $made[0].BaseName 'research' } else { $fixture }
    Check ((Test-Path (Join-Path $research 'slice.md')) -and (Test-Path (Join-Path $research 'findings.md'))) 'research files written'
    $f2 = Invoke-Tool $fanout @('-Parent', 'SP-0001', '-RepoRoot', $fixture, '-Date', '2026-01-02')
    $again = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'PLAN') -Filter 'SP-*_audit_*.md' -File)
    Check ($f2.Exit -eq 0 -and $again.Count -eq $made.Count -and ($f2.Output -join "`n") -match 'created 0,') 'second fan-out creates nothing'

    # --- summary -----------------------------------------------------------------------------------------------
    $s1 = Invoke-Tool $summary @('-Parent', 'SP-0001', '-RepoRoot', $fixture)
    Check ($s1.Exit -eq 1 -and ($s1.Output[-1] -match '^audit-campaign: OPEN')) "open campaign exits 1 (got $($s1.Exit))"
    Check (($s1.Output -join "`n") -match 'coverage: 0 uncovered, 0 in two slices') 'fresh campaign fully covered'

    New-FixtureFile $fixture "$core/Epsilon.cs" 10
    $s2 = Invoke-Tool $summary @('-Parent', 'SP-0001', '-RepoRoot', $fixture)
    Check (($s2.Output -join "`n") -match "uncovered: $([regex]::Escape("$core/Epsilon.cs"))") 'a file added after slicing is reported uncovered'
    Remove-Item -LiteralPath (Join-Path $fixture "$core/Epsilon.cs")

    foreach ($t in $made) {
        $text = [System.IO.File]::ReadAllText($t.FullName) -replace '(?m)^\*\*Status:\*\* Tactical[^\n]*', '**Status:** Verified - fixture'
        [System.IO.File]::WriteAllText($t.FullName, $text)
    }
    $s3 = Invoke-Tool $summary @('-Parent', 'SP-0001', '-RepoRoot', $fixture)
    Check ($s3.Exit -eq 1 -and ($s3.Output -join "`n") -match 'without a parsable Last Audit') 'Verified without a Last Audit is a problem'

    $i = 0
    foreach ($t in $made) {
        $tickets = if ($i -eq 0) { 'SP-0100' } else { 'none' }
        $high = if ($i -eq 0) { 1 } else { 0 }
        $block = "`n## Last Audit`n**Date:** 2026-01-03`n**Coverage:** 1 of 1 audited files read in full`n**Findings:** High $high | Medium 0 | Low 0`n**Tickets:** $tickets`n**Inline fixes:** none`n"
        [System.IO.File]::AppendAllText($t.FullName, $block)
        $i++
    }
    $s4 = Invoke-Tool $summary @('-Parent', 'SP-0001', '-RepoRoot', $fixture)
    Check ($s4.Exit -eq 1 -and ($s4.Output -join "`n") -match 'names SP-0100, which has no ticket') 'a named ticket must exist'

    New-FixtureFile $fixture 'PLAN/SP-0100_found.md' 3 "# SP-0100: Found`n`n**Status:** Draft - fixture"
    $s5 = Invoke-Tool $summary @('-Parent', 'SP-0001', '-RepoRoot', $fixture)
    Check ($s5.Exit -eq 0 -and $s5.Output[-1] -eq 'audit-campaign: CLOSED') "closed campaign exits 0 (got $($s5.Exit): $($s5.Output[-1]))"
    Check (($s5.Output -join "`n") -match 'findings: High 1 \| Medium 0 \| Low 0' -and ($s5.Output -join "`n") -match 'SP-0100 \(Draft\)') 'findings and produced tickets counted'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -eq 0) {
    "audit-tools: PASS ($passed checks)"
    exit 0
}
"audit-tools: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
