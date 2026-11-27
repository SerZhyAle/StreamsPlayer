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
      profile   the umbrella id is checked against the fixture's .sza-profile.json: the current shape and a legacy
                shape pass, another shape and a missing profile are refused (exit 2)
      fan-out   one Tactical ticket per slice, created through the capture and status scripts (stand-ins for the
                harness's capture-draft.ps1 and update.ps1, so the gate needs no plugin) with the ids they return,
                source `campaign`, a campaign-scoped name and a title; the procedure copied in, research files
                written; a slice that already has a legacy-id ticket gets none; a refused capture stops with exit 2
                and leaves nothing; a second run creates nothing
      summary   open while slices are open (exit 1); an uncovered file is reported; slice tickets and produced
                tickets of both id shapes are counted; a Verified slice without a Last Audit, or naming a ticket
                that does not exist, is a problem; closed (exit 0) when all hold

    The stand-ins keep the harness's contract (the id is the last output line, the file is PLAN/<id>_<slug>.md,
    the project root and ticket source arrive as environment variables). The real harness is exercised by hand:
    the evidence is recorded on STRWIN-0298.

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
    New-FixtureFile $fixture 'PLAN/STRWIN-0001_umbrella.md' 3 "# STRWIN-0001: Umbrella`n`n**Status:** BlockByOtherTask - fixture"
    New-FixtureFile $fixture 'PLAN/DONE/SP-0005_old.md' 3 "# SP-0005: Old`n`n**Status:** Verified - fixture"
    New-Item -ItemType Directory -Path (Join-Path $fixture 'docs/agent') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'docs/agent/CODE_AUDIT.md') -Destination (Join-Path $fixture 'docs/agent/CODE_AUDIT.md')
    # The id grammar comes from the profile: the current shape and the retired one that closed tickets keep.
    [System.IO.File]::WriteAllText((Join-Path $fixture '.sza-profile.json'), @'
{
  "version": "1",
  "projectName": "AuditFixture",
  "grammar": { "ticketIdPattern": "^STRWIN-\\d{4}$", "legacyTicketIdPatterns": ["^SP-\\d{4}$"] }
}
'@)

    $classA = @(
        "$core/Alpha.cs", "$core/Beta.cs", "$core/Delta.cs", "$core/Gamma.cs",
        "$app/Main.Alpha.cs", "$app/Main.Beta.cs", "$app/Main.Gamma.cs", "$app/Main.xaml", "$app/Main.xaml.cs",
        "$app/Small.xaml", "$app/Small.xaml.cs", "$app/Stray.cs", "$app/Tiny.cs",
        'tools/StreamsPlayer.CatalogHarness/Program.cs', 'build.ps1', 'scripts/check.ps1')
    $limits = @('-MaxFiles', '4', '-MaxLines', '300')
    $manifestPath = Join-Path $fixture 'PLAN/STRWIN-0001_umbrella/research/audit-slices.json'

    # --- slicer ------------------------------------------------------------------------------------------------
    $r = Invoke-Tool $slicer (@('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture) + $limits)
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
    $r2 = Invoke-Tool $slicer (@('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture) + $limits)
    $second = [System.IO.File]::ReadAllBytes($manifestPath)
    Check ($r2.Exit -eq 0 -and [System.Linq.Enumerable]::SequenceEqual($first, $second)) 'same tree, same manifest bytes'

    $classBPath = Join-Path $fixture 'classb.json'
    $r3 = Invoke-Tool $slicer (@('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture, '-IncludeClassB', '-OutJson', $classBPath) + $limits)
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
    $r4 = Invoke-Tool $slicer (@('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture, '-FileList', $listPath, '-OutJson', $tailPath) + $limits)
    $mt = Get-Content -LiteralPath $tailPath -Raw | ConvertFrom-Json
    $allT = @($mt.slices | ForEach-Object { $_.files } | ForEach-Object path | Sort-Object)
    Check ($r4.Exit -eq 0 -and $mt.tail -and ($allT -join ',') -eq "$app/Tiny.cs,$core/Gamma.cs") '-FileList slices only the listed files'
    Set-Content -LiteralPath $listPath -Value @('src/Nowhere.cs')
    $r5 = Invoke-Tool $slicer (@('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture, '-FileList', $listPath, '-OutJson', $tailPath) + $limits)
    Check ($r5.Exit -eq 2) '-FileList refuses a file outside the audited set'

    # --- profile -----------------------------------------------------------------------------------------------
    Set-Content -LiteralPath $listPath -Value @("$core/Gamma.cs")
    $legacyOut = Join-Path $fixture 'legacy-parent.json'
    $rl = Invoke-Tool $slicer (@('-Parent', 'SP-0001', '-RepoRoot', $fixture, '-FileList', $listPath, '-OutJson', $legacyOut) + $limits)
    Check ($rl.Exit -eq 0) "the slicer accepts an umbrella id of a legacy shape (got $($rl.Exit): $($rl.Output -join ' | '))"
    $rx = Invoke-Tool $slicer (@('-Parent', 'FOO-0001', '-RepoRoot', $fixture, '-FileList', $listPath, '-OutJson', $legacyOut) + $limits)
    Check ($rx.Exit -eq 2 -and ($rx.Output -join ' ') -match 'not a ticket id of this repository') 'the slicer refuses an id outside the profile grammar'
    $rf = Invoke-Tool $fanout @('-Parent', 'FOO-0001', '-RepoRoot', $fixture, '-DryRun')
    Check ($rf.Exit -eq 2 -and ($rf.Output -join ' ') -match 'not a ticket id of this repository') 'the fan-out refuses an id outside the profile grammar'
    $bare = Join-Path $fixture 'no-profile'
    New-Item -ItemType Directory -Path (Join-Path $bare 'PLAN') -Force | Out-Null
    $rn = Invoke-Tool $slicer @('-Parent', 'STRWIN-0001', '-RepoRoot', $bare)
    Check ($rn.Exit -eq 2 -and ($rn.Output -join ' ') -match 'no \.sza-profile\.json') 'a repository without a profile has no id shape: exit 2'

    # --- fan-out -----------------------------------------------------------------------------------------------
    # Stand-ins for the harness's capture-draft.ps1 and update.ps1, keeping their contract: the id is the last output
    # line, the file is PLAN/<id>_<slug>.md, the project root and the ticket source arrive as environment variables.
    $stubs = Join-Path $fixture 'stubs'
    New-Item -ItemType Directory -Path $stubs -Force | Out-Null
    $captureStub = Join-Path $stubs 'capture-draft.ps1'
    $statusStub = Join-Path $stubs 'update.ps1'
    [System.IO.File]::WriteAllText($captureStub, @'
param([string] $Slug, [string] $Name, [string] $Text, [string] $Complexity)
$root = $env:SZA_PROJECT_ROOT
if (Test-Path -LiteralPath (Join-Path $root 'stub-refuse.flag')) { Write-Host 'stub: refused'; exit 1 }
$counter = Join-Path $root 'stub-next-id.txt'
$n = [int](Get-Content -LiteralPath $counter) + 1
Set-Content -LiteralPath $counter -Value $n
$id = 'STRWIN-{0:D4}' -f $n
[System.IO.File]::WriteAllText((Join-Path $root "PLAN/${id}_$Slug.md"), "# ${id}_$Slug`n`n**Status:** Draft`n**Complexity:** $Complexity`n`n$Text`n")
Add-Content -LiteralPath (Join-Path $root 'stub-calls.log') -Value "capture|$id|$Slug|$Name|$($env:SZA_TICKET_SOURCE)|$Complexity"
Write-Host "capture-draft: PLAN/${id}_$Slug.md"
Write-Output $id
'@)
    [System.IO.File]::WriteAllText($statusStub, @'
param([string] $Id, [string] $Status, [string] $Title)
$root = $env:SZA_PROJECT_ROOT
$file = (Get-ChildItem -LiteralPath (Join-Path $root 'PLAN') -Filter "${Id}_*.md" -File | Select-Object -First 1).FullName
$text = [System.IO.File]::ReadAllText($file) -replace '(?m)^\*\*Status:\*\*[^\n]*', "**Status:** $Status"
[System.IO.File]::WriteAllText($file, $text)
Add-Content -LiteralPath (Join-Path $root 'stub-calls.log') -Value "status|$Id|$Status|$Title"
Write-Output "$Id Draft -> $Status"
'@)
    $callLog = Join-Path $fixture 'stub-calls.log'
    Set-Content -LiteralPath (Join-Path $fixture 'stub-next-id.txt') -Value 40
    $fanArgs = @('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture, '-Date', '2026-01-02', '-CaptureScript', $captureStub, '-StatusScript', $statusStub)
    $planDir = Join-Path $fixture 'PLAN'

    # A refused capture stops the run before anything is made.
    Set-Content -LiteralPath (Join-Path $fixture 'stub-refuse.flag') -Value 'x'
    $fr = Invoke-Tool $fanout $fanArgs
    Check ($fr.Exit -eq 2 -and ($fr.Output -join ' ') -match 'CANNOT CREATE \(capture-draft refused slice') 'a refused capture stops the fan-out with exit 2'
    Check (@(Get-ChildItem -LiteralPath $planDir -Filter '*_audit_*').Count -eq 0 -and -not (Test-Path -LiteralPath $callLog)) 'a refused capture leaves no ticket behind'
    Remove-Item -LiteralPath (Join-Path $fixture 'stub-refuse.flag')

    # The first slice already has a ticket under a legacy id.
    New-FixtureFile $fixture 'PLAN/DONE/SP-0009_audit_prior.md' 3 "# SP-0009: Prior`n`n**Status:** Verified - fixture`n**Audit slice:** $($m.slices[0].name)`n**Campaign:** STRWIN-0001 (fixture)"
    $f1 = Invoke-Tool $fanout $fanArgs
    Check ($f1.Exit -eq 0) "fan-out exits 0 (got $($f1.Exit): $($f1.Output -join ' | '))"
    $made = @(Get-ChildItem -LiteralPath $planDir -Filter 'STRWIN-*_audit_*.md' -File | Sort-Object Name)
    Check ($made.Count -eq $m.slices.Count - 1) "one ticket per slice but the one that has a legacy ticket ($($made.Count) of $($m.slices.Count))"
    Check ($made.Count -gt 0 -and $made[0].Name.StartsWith('STRWIN-0041_')) 'the ids are the ones the capture script returned'
    Check (@(Get-ChildItem -LiteralPath $planDir -Filter 'SP-*').Count -eq 0) 'no ticket is created under the retired prefix'
    $calls = @(Get-Content -LiteralPath $callLog)
    $captures = @($calls | Where-Object { $_ -like 'capture|*' })
    $moves = @($calls | Where-Object { $_ -like 'status|*' })
    Check ($captures.Count -eq $made.Count -and $moves.Count -eq $made.Count) "each ticket is created once and moved once ($($captures.Count) captures, $($moves.Count) moves)"
    Check (@($captures | Where-Object { $_.Split('|')[4] -ne 'campaign' }).Count -eq 0) 'every capture carries the ticket source campaign'
    Check (@($captures | Where-Object { -not $_.Split('|')[3].StartsWith('strwin-0001-audit_') }).Count -eq 0) 'the ledger name carries the campaign'
    Check (@($moves | Where-Object { $_ -notmatch '^status\|STRWIN-\d{4}\|Tactical\|Audit slice - ' }).Count -eq 0) 'every ticket is moved to Tactical with a title'
    $firstTicket = if ($made.Count) { [System.IO.File]::ReadAllText($made[0].FullName) } else { '' }
    Check ($firstTicket -match '(?m)^\*\*Status:\*\* Tactical' -and $firstTicket -match "(?m)^\*\*Audit slice:\*\* $([regex]::Escape($m.slices[1].name))$") 'ticket carries status and slice name, risk order'
    Check ($firstTicket -match '(?m)^\*\*Complexity:\*\* medium - read-only audit of ' -and $firstTicket -match '(?m)^\*\*Created:\*\* 2026-01-02$') 'ticket carries its complexity and creation date'
    Check ($firstTicket -match '(?m)^## Procedure' -and $firstTicket -match 'Layered read' -and $firstTicket -notmatch 'slice-procedure:') 'procedure copied in'
    Check ($firstTicket -notmatch 'SP-NNNN') 'the procedure names no retired id shape'
    Check ($firstTicket -notmatch 'STRWIN-0001_umbrella') 'ticket does not depend on the umbrella file'
    $research = if ($made.Count) { Join-Path $made[0].DirectoryName $made[0].BaseName 'research' } else { $fixture }
    Check ((Test-Path (Join-Path $research 'slice.md')) -and (Test-Path (Join-Path $research 'findings.md'))) 'research files written'
    $f2 = Invoke-Tool $fanout $fanArgs
    $again = @(Get-ChildItem -LiteralPath $planDir -Filter 'STRWIN-*_audit_*.md' -File)
    Check ($f2.Exit -eq 0 -and $again.Count -eq $made.Count -and @(Get-Content -LiteralPath $callLog).Count -eq $calls.Count -and ($f2.Output -join "`n") -match 'created 0,') 'second fan-out creates nothing'
    $legacyTicket = Join-Path $fixture 'PLAN/DONE/SP-0009_audit_prior.md'

    # --- summary -----------------------------------------------------------------------------------------------
    $s1 = Invoke-Tool $summary @('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture)
    Check ($s1.Exit -eq 1 -and ($s1.Output[-1] -match '^audit-campaign: OPEN')) "open campaign exits 1 (got $($s1.Exit))"
    Check (($s1.Output -join "`n") -match 'coverage: 0 uncovered, 0 in two slices') 'fresh campaign fully covered'

    New-FixtureFile $fixture "$core/Epsilon.cs" 10
    $s2 = Invoke-Tool $summary @('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture)
    Check (($s2.Output -join "`n") -match "uncovered: $([regex]::Escape("$core/Epsilon.cs"))") 'a file added after slicing is reported uncovered'
    Remove-Item -LiteralPath (Join-Path $fixture "$core/Epsilon.cs")

    foreach ($t in $made) {
        $text = [System.IO.File]::ReadAllText($t.FullName) -replace '(?m)^\*\*Status:\*\* Tactical[^\n]*', '**Status:** Verified - fixture'
        [System.IO.File]::WriteAllText($t.FullName, $text)
    }
    $s3 = Invoke-Tool $summary @('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture)
    $s3Text = $s3.Output -join "`n"
    Check ($s3.Exit -eq 1 -and $s3Text -match 'without a parsable Last Audit') 'Verified without a Last Audit is a problem'
    Check ($s3Text -match "$([regex]::Escape($made[0].Name.Substring(0, 11)))\s+Verified" -and $s3Text -match 'SP-0009\s+Verified') 'slice tickets of both id shapes are counted'

    # The first slice ticket produced one ticket of each shape; the legacy slice ticket produced none.
    $produced = 'STRWIN-0100, SP-0101'
    $i = 0
    foreach ($path in (@($made | ForEach-Object FullName) + $legacyTicket)) {
        $tickets = if ($i -eq 0) { $produced } else { 'none' }
        $high = if ($i -eq 0) { 1 } else { 0 }
        $block = "`n## Last Audit`n**Date:** 2026-01-03`n**Coverage:** 1 of 1 audited files read in full`n**Findings:** High $high | Medium 0 | Low 0`n**Tickets:** $tickets`n**Inline fixes:** none`n"
        [System.IO.File]::AppendAllText($path, $block)
        $i++
    }
    $s4 = Invoke-Tool $summary @('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture)
    $s4Text = $s4.Output -join "`n"
    Check ($s4.Exit -eq 1 -and $s4Text -match 'names STRWIN-0100, which has no ticket' -and $s4Text -match 'names SP-0101, which has no ticket') 'a named ticket must exist, whichever shape it has'

    New-FixtureFile $fixture 'PLAN/STRWIN-0100_found.md' 3 "# STRWIN-0100: Found`n`n**Status:** Draft - fixture"
    New-FixtureFile $fixture 'PLAN/DONE/SP-0101_found_old.md' 3 "# SP-0101: Found`n`n**Status:** Draft - fixture"
    $s5 = Invoke-Tool $summary @('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture)
    $s5Text = $s5.Output -join "`n"
    Check ($s5.Exit -eq 0 -and $s5.Output[-1] -eq 'audit-campaign: CLOSED') "closed campaign exits 0 (got $($s5.Exit): $($s5.Output[-1]))"
    Check ($s5Text -match 'findings: High 1 \| Medium 0 \| Low 0' -and $s5Text -match 'STRWIN-0100 \(Draft\)' -and $s5Text -match 'SP-0101 \(Draft\)') 'findings and produced tickets of both shapes counted'

    # --- a second campaign over the same slice names -----------------------------------------------------------
    # The next campaign slices the same files into the same slice names. Its fan-out makes its own tickets, its
    # summary does not take the first campaign's closed tickets for its slices, and the first campaign is unchanged.
    $manifest2 = Join-Path $fixture 'campaign2-slices.json'
    $r6 = Invoke-Tool $slicer (@('-Parent', 'STRWIN-0002', '-RepoRoot', $fixture, '-OutJson', $manifest2) + $limits)
    $campaign2 = Get-Content -LiteralPath $manifest2 -Raw | ConvertFrom-Json
    Check ($r6.Exit -eq 0 -and (@($campaign2.slices | ForEach-Object name) -join '|') -eq (@($m.slices | ForEach-Object name) -join '|')) "the second umbrella slices the same tree into the same slice names (got $($r6.Exit))"
    $fan2Args = @('-Parent', 'STRWIN-0002', '-RepoRoot', $fixture, '-Manifest', $manifest2, '-Date', '2026-01-03', '-CaptureScript', $captureStub, '-StatusScript', $statusStub)
    $fan2 = Invoke-Tool $fanout $fan2Args
    $made2 = @(Get-ChildItem -LiteralPath $planDir -Filter 'STRWIN-*_audit_*.md' -File | Where-Object { [System.IO.File]::ReadAllText($_.FullName) -match '(?m)^\*\*Campaign:\*\* STRWIN-0002 ' })
    Check ($fan2.Exit -eq 0 -and $made2.Count -eq $campaign2.slices.Count) "the second campaign gets one ticket per slice of its own ($($made2.Count) of $($campaign2.slices.Count), exit $($fan2.Exit): $($fan2.Output -join ' | '))"
    $fan2Again = Invoke-Tool $fanout $fan2Args
    Check ($fan2Again.Exit -eq 0 -and ($fan2Again.Output -join "`n") -match 'created 0,') 'the second campaign creates nothing on a second run'
    $fan1Again = Invoke-Tool $fanout $fanArgs
    Check ($fan1Again.Exit -eq 0 -and ($fan1Again.Output -join "`n") -match 'created 0,') 'the first campaign still creates nothing once the second one exists'
    $s6 = Invoke-Tool $summary @('-Parent', 'STRWIN-0002', '-RepoRoot', $fixture, '-Manifest', $manifest2)
    Check ($s6.Exit -eq 1 -and (($s6.Output -join "`n") -match "slices: Tactical $($campaign2.slices.Count)\r?\n")) "the second campaign's slices are its own open tickets, not the first campaign's closed ones (exit $($s6.Exit))"
    $s7 = Invoke-Tool $summary @('-Parent', 'STRWIN-0001', '-RepoRoot', $fixture)
    Check ($s7.Exit -eq 0 -and $s7.Output[-1] -eq 'audit-campaign: CLOSED') "the first campaign stays closed (got $($s7.Exit): $($s7.Output[-1]))"
} catch {
    # A setup step or an audit tool threw instead of answering: nothing was judged past this point, so the run could
    # not verify (CHECK-VERDICT 2.1, 2.5, 2.11). A check that already failed still outranks it (2.14).
    $reason = $_.Exception.Message -replace '\s+', ' '
    if ($failures.Count -eq 0) { "audit-tools: COULD NOT VERIFY ($reason)"; exit 2 }
    "audit-tools: the run stopped early: $reason"
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -eq 0) {
    "audit-tools: PASS (run=$passed skipped=0)"
    exit 0
}
"audit-tools: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
