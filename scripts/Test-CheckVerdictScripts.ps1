#requires -Version 7.0
<#
.SYNOPSIS
    SP-0239 P3: fixture tests for the exit code and the last line of the gate scripts that answer in CHECK-VERDICT.

.DESCRIPTION
    Runs each script in a child pwsh and judges the two facts a caller branches on - the exit code and the last stdout
    line (rules 2.1, 2.5 and 2.11 of the contract). A script that locates its inputs relative to itself is copied into a
    throwaway tree first, so every case is deterministic and none touches the repository; check.ps1 runs against stub
    children and a stub `dotnet`, so no build or test run is started.

      the fixture runners (nine with try/finally and no catch before SP-0239, and the two SP-0247 release-version
                        runners written to the same shape)
                        a throw inside the run could not be verified: exit 2, `COULD NOT VERIFY (..)`; a check that
                        already failed outranks it: exit 1 and the FAIL line. The throw is injected into a copy
      test.ps1          a results directory that already exists could not be judged: exit 2, `COULD NOT VERIFY`
                        (the PASS and FAIL words of the same line are Test-TestResultSummary.ps1's)
      check-docs.ps1    a clean tree passes; a finding fails; a document that cannot be read could not be verified; a
                        finding recorded before the stop still outranks it. The portal contribution (SP-0239 P5): a
                        control name that is no dictionary string fails (bold and guillemet forms), a page with no
                        dictionary could not be verified, a changed English source against an unchanged mirror
                        fingerprint fails, and CRLF line endings do not change the fingerprint
      check-help.ps1    a minimal help set passes; a missing source stylesheet or an unreadable page could not be verified;
                        a defect fails, also when a later page cannot be read; an index that omits a written page of its
                        own locale fails, and a link to another origin or locale does not count as a link to it
      build-help.ps1    -Check passes on what the generator wrote, fails on a stale page, could not verify a source that
                        does not parse or a copy that throws; a plain generator run keeps its exception. A translation
                        whose sourceHash is not the fingerprint of the English copy (or is missing) fails -Check, and
                        recording the new fingerprint clears it
      build-site.ps1    -Check fails on a vendored file that differs from its pin and could not verify a missing
                        provenance file; a plain generator run keeps its exception. The PASS line comes from the real
                        tree, which check.ps1 runs as `site-sync`
      make-demo.ps1     -Check passes three good files, fails a missing one (was exit 3), could not verify a locked one
      check.ps1         all-pass counts the skipped check on the verdict line; a child that answers 4 reads as 2; a
                        defect outranks it; a missing `dotnet` could not verify instead of throwing
      SP-0291 fixtures  the summary and existing-release self-tests answer in CHECK-VERDICT too: a throw inside the
                        run could not be verified, a case that already failed outranks it (rule 2.14), and a missing
                        helper is a verdict line, never a bare exit 1 with an error block

    Exit 0 when every case passes, 1 when one fails, 2 when the fixture itself could not run. The last line is
    `check-verdict-scripts: PASS|FAIL|COULD NOT VERIFY run=N skipped=M`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pwsh = (Get-Process -Id $PID).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)

$failures = [System.Collections.Generic.List[string]]::new()
$run = 0
function Check([bool] $Condition, [string] $Name) {
    $script:run++
    if (-not $Condition) { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}
# One case: the exit code and the last line, both named in the failure so a re-run is not needed to learn what happened.
function Expect([object] $Result, [int] $Exit, [string] $LastLine, [string] $Name) {
    Check ($Result.Exit -eq $Exit -and $Result.Last -match $LastLine) "$Name (expected: exit $Exit, last line /$LastLine/ | actual: exit $($Result.Exit), '$($Result.Last)')"
}
function Invoke-Script([string] $Script, [string[]] $Arguments = @(), [string] $Path = '') {
    $saved = $env:PATH
    if ($Path) { $env:PATH = $Path }
    try { $out = & $script:pwsh -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $env:PATH = $saved }
    $exit = $LASTEXITCODE
    $lines = @($out | ForEach-Object { $_ -replace "`e\[[0-9;]*[A-Za-z]", '' } | Where-Object { $_.Trim() })
    [pscustomobject]@{ Exit = $exit; Last = $(if ($lines.Count) { $lines[-1] } else { '' }); Text = ($lines -join "`n") }
}
function Write-File([string] $Path, [string] $Text) {
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text, $script:utf8)
}
function Write-Bytes([string] $Path, [byte[]] $Bytes) {
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($Path, $Bytes)
}
# Holds the files open so that no other process can read them: a document that exists but cannot be read.
function Invoke-Locked([string[]] $Paths, [scriptblock] $Action) {
    $streams = [System.Collections.Generic.List[System.IO.FileStream]]::new()
    try {
        foreach ($path in $Paths) { $streams.Add([System.IO.File]::Open($path, 'Open', 'Read', 'None')) }
        & $Action
    }
    finally { foreach ($stream in $streams) { $stream.Dispose() } }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0239-verdict-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    # --- scripts/test.ps1 --------------------------------------------------------------------------------------------
    # The results directory must be new, so an existing one stops the run before dotnet is started.
    $existing = Join-Path $work 'results'
    New-Item -ItemType Directory -Path $existing | Out-Null
    $r = Invoke-Script (Join-Path $repo 'scripts/test.ps1') @('-ResultsDirectory', $existing)
    Expect $r 2 '^tests: COULD NOT VERIFY run=0 skipped=0$' 'test.ps1: a results directory that is not new could not be verified'

    # --- SP-0282, CHECK-VERDICT item A.1: the pass line of a self-test or an operation proof ----------------------------
    # A script that runs a fixed list of its own assertions prints `PASS (run=N skipped=M)`; the legacy `PASS (N checks)`
    # spelling stays readable for a reader but no writer of this product prints it. Held statically: the source of each
    # script is read, never run.
    $testLinePaths = @(
        'tools/Test-CliDoc.ps1', 'tools/Test-CatalogSnapshot.ps1', 'tools/audit/Test-AuditVerdictGate.ps1', 'tools/audit/Test-AuditTools.ps1',
        'msix/Test-MsixIdentity.ps1', 'msix/Test-AssertMsixPackage.ps1', 'msix/Test-AppxManifest.ps1', 'msix/Assert-MsixPackage.ps1',
        'tools/site/Test-SiteFacts.ps1', 'tools/site/Test-SiteDiscovery.ps1', 'tools/site/Test-ReleaseNotes.ps1', 'tools/site/Test-HeldAddresses.ps1',
        'scripts/Test-ReleaseArtifactVersion.ps1', 'scripts/Test-ReleaseVersion.ps1', 'scripts/Test-StorePreflightCall.ps1',
        'scripts/Test-SmokeVerdictGate.ps1', 'scripts/Test-ExistingReleaseState.ps1', 'scripts/Assert-ReleaseArtifactVersion.ps1')
    $legacyPass = [regex]::new('PASS \(\$[\w().$]+ (checks|assertions|scenarios)')
    $grammarPass = [regex]::new('PASS \(run=\$[\w().$]+ skipped=0')
    foreach ($testLinePath in $testLinePaths) {
        $testLineSource = [System.IO.File]::ReadAllText((Join-Path $repo $testLinePath))
        Check (-not $legacyPass.IsMatch($testLineSource)) "$($testLinePath): no pass line prints the legacy 'PASS (N checks)' spelling"
        Check ($grammarPass.IsMatch($testLineSource)) "$($testLinePath): the pass line prints 'PASS (run=N skipped=0)'"
    }

    # --- the fixture runners: a throw inside the try body -------------------------------------------------------
    # Each runner is copied with `$PSScriptRoot` redirected to its own folder and a throw injected as the first statement
    # of its try block, so the catch is reached without breaking the runner's real inputs. Nothing was judged: exit 2 and
    # the could-not-verify word; with a check already failed: exit 1 and the FAIL line, which outranks it (rule 2.14).
    $runners = @(
        @{ Path = 'scripts/Test-SmokeVerdictGate.ps1'; Subject = 'smoke-verdict-gate' }
        @{ Path = 'tools/Test-CatalogSnapshot.ps1'; Subject = 'catalog-snapshot-tool' }
        @{ Path = 'tools/site/Test-HeldAddresses.ps1'; Subject = 'held-addresses' }
        @{ Path = 'tools/site/Test-ReleaseNotes.ps1'; Subject = 'release-notes' }
        @{ Path = 'tools/site/Test-SiteDiscovery.ps1'; Subject = 'site-discovery' }
        @{ Path = 'tools/site/Test-SiteFacts.ps1'; Subject = 'site-facts' }
        @{ Path = 'msix/Test-AssertMsixPackage.ps1'; Subject = 'msix-package-tests' }
        @{ Path = 'msix/Test-MsixIdentity.ps1'; Subject = 'msix-identity' }
        @{ Path = 'scripts/Test-StorePreflightCall.ps1'; Subject = 'store-preflight-call' }
        @{ Path = 'tools/audit/Test-AuditTools.ps1'; Subject = 'audit-tools' }
        @{ Path = 'tools/audit/Test-AuditVerdictGate.ps1'; Subject = 'audit-verdict-gate' }
        @{ Path = 'scripts/Test-ReleaseVersion.ps1'; Subject = 'release-version-tests' }
        @{ Path = 'scripts/Test-ReleaseArtifactVersion.ps1'; Subject = 'release-artifact-version-tests' }
    )
    $tryOpen = [regex]::new('(?m)^try \{[ \t]*\r?$')
    foreach ($runner in $runners) {
        $source = [System.IO.File]::ReadAllText((Join-Path $repo $runner.Path)).Replace('$PSScriptRoot', '$env:SP0239_PROBE_ROOT')
        Check ($tryOpen.IsMatch($source)) "$($runner.Path): the fixture found the try block to inject into"
        $env:SP0239_PROBE_ROOT = Join-Path $repo (Split-Path $runner.Path -Parent)
        foreach ($variant in 'plain', 'earlier-failure') {
            $inject = if ($variant -eq 'plain') { "throw 'synthetic setup failure'" } else { "`$failures.Add('synthetic earlier failure'); throw 'synthetic setup failure'" }
            $probe = Join-Path $work "probe-$variant-$(Split-Path $runner.Path -Leaf)"
            Write-File $probe ($tryOpen.Replace($source, "try {`n    $inject", 1))
            $r = Invoke-Script $probe
            if ($variant -eq 'plain') {
                Expect $r 2 "^$($runner.Subject): COULD NOT VERIFY \(synthetic setup failure\)$" "$($runner.Path): a throw inside the run could not be verified"
            }
            else {
                Expect $r 1 "^$($runner.Subject): FAIL \(1 of 1" "$($runner.Path): a check that failed before the throw outranks could-not-verify"
            }
        }
    }
    Remove-Item Env:SP0239_PROBE_ROOT -ErrorAction SilentlyContinue

    $env:SP0239_PROBE_ROOT = Join-Path $repo 'scripts'
    $source = [System.IO.File]::ReadAllText((Join-Path $repo 'scripts/Test-MeasureCaptureTimes.ps1')).Replace('$PSScriptRoot', '$env:SP0239_PROBE_ROOT')
    foreach ($variant in 'plain', 'earlier-failure') {
        $inject = if ($variant -eq 'plain') { "throw 'synthetic setup failure'" }
            else { "`$failures.Add('synthetic earlier failure'); throw 'synthetic setup failure'" }
        $probe = Join-Path $work "measure-$variant.ps1"
        Write-File $probe ($tryOpen.Replace($source, "try {`n    $inject", 1))
        $r = Invoke-Script $probe
        if ($variant -eq 'plain') {
            Expect $r 2 '^test-measure-capture-times: COULD NOT VERIFY run=0 skipped=0$' 'capture-time fixture setup failure has a verdict'
        } else {
            Expect $r 1 '^test-measure-capture-times: FAIL run=0 skipped=0$' 'capture-time fixture preserves an earlier failure verdict'
        }
    }
    Remove-Item Env:SP0239_PROBE_ROOT -ErrorAction SilentlyContinue

    # --- SP-0291 (S2-2): the two scripted self-tests the fast tier re-runs --------------------------------------------
    # A throw inside the run could not be verified (exit 2). A case that already failed outranks the throw (rule 2.14):
    # the line says FAIL and the exit is 1. Both scripts source their helper inside the try, so a probe root that holds
    # no helper is the third case: a verdict line and exit 2, never a bare exit 1 with an error block.
    $sp0291Cases = @(
        @{ Path = 'scripts/Test-TestResultSummary.ps1'; Subject = 'test-result-summary'; Helper = 'TestResultSummary.ps1' }
        @{ Path = 'scripts/Test-ExistingReleaseState.ps1'; Subject = 'existing-release'; Helper = 'Get-ExistingReleaseState.ps1' }
    )
    foreach ($sp0291Case in $sp0291Cases) {
        $sp0291Source = [System.IO.File]::ReadAllText((Join-Path $repo $sp0291Case.Path)).Replace('$PSScriptRoot', '$env:SP0239_PROBE_ROOT')
        Check ($tryOpen.IsMatch($sp0291Source)) "$($sp0291Case.Path): the fixture found the try block to inject into"
        foreach ($sp0291Variant in 'plain', 'earlier-failure', 'no-helper') {
            $sp0291Inject = if ($sp0291Variant -eq 'plain') { "throw 'synthetic setup failure'" }
                elseif ($sp0291Variant -eq 'earlier-failure') { "`$failures = 1; throw 'synthetic setup failure'" }
                else { '' }
            $sp0291Probe = Join-Path $work ("sp0291-$($sp0291Case.Subject)-$sp0291Variant.ps1")
            Write-File $sp0291Probe ($(if ($sp0291Inject) { $tryOpen.Replace($sp0291Source, "try {`n    $sp0291Inject", 1) } else { $sp0291Source }))
            if ($sp0291Variant -eq 'no-helper') {
                $env:SP0239_PROBE_ROOT = New-Item -ItemType Directory -Path (Join-Path $work 'no-helper-root') -Force | Select-Object -ExpandProperty FullName
            }
            else {
                $env:SP0239_PROBE_ROOT = Join-Path $repo (Split-Path $sp0291Case.Path -Parent)
            }
            $r = Invoke-Script $sp0291Probe
            if ($sp0291Variant -eq 'plain') {
                Expect $r 2 "^$($sp0291Case.Subject): COULD NOT VERIFY \(synthetic setup failure\)$" "$($sp0291Case.Path): a throw inside the run could not be verified"
            }
            elseif ($sp0291Variant -eq 'earlier-failure') {
                Expect $r 1 "^$($sp0291Case.Subject): FAIL .*the run stopped early" "$($sp0291Case.Path): a case that failed before the throw outranks could-not-verify (rule 2.14)"
            }
            else {
                Check ($r.Exit -eq 2 -and $r.Text -match "$($sp0291Case.Subject): COULD NOT VERIFY") "$($sp0291Case.Path): a missing helper is a verdict line, not a bare exit 1 (the reason is the command word itself, a multi-line error record, so the text carries the verdict line)"
            }
        }
    }
    Remove-Item Env:SP0239_PROBE_ROOT -ErrorAction SilentlyContinue

    # --- scripts/check-docs.ps1 --------------------------------------------------------------------------------------
    # SP-0287: a registry declares the roots of the maintained corpus (item V); the base tree declares its one document.
    $fixtureRoot = '{"root":"README.md","reason":"fixture"}'
    $fixtureReadme = '{"path":"README.md","topic":"fixture","area":"fixture","triggers":["fixture"],"role":"source"}'
    function New-DocsTree([string] $Name) {
        $tree = Join-Path $work $Name
        Write-File (Join-Path $tree 'DOCUMENT_REGISTRY.jsonl') ($script:fixtureRoot + "`n" + $script:fixtureReadme)
        Write-File (Join-Path $tree 'README.md') "# Fixture`n"
        New-Item -ItemType Directory -Path (Join-Path $tree 'scripts') -Force | Out-Null
        foreach ($name in 'check-docs.ps1', 'SourceFingerprint.ps1') {
            Copy-Item -LiteralPath (Join-Path $repo "scripts/$name") -Destination (Join-Path $tree "scripts/$name")
        }
        & git -C $tree init -q 2>&1 | Out-Null
        return $tree
    }
    $tree = New-DocsTree 'docs-clean'
    Expect (Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1')) 0 '^docs-quality: PASS \(' 'check-docs.ps1: a clean tree passes'

    $tree = New-DocsTree 'docs-duplicate'
    $registry = Join-Path $tree 'DOCUMENT_REGISTRY.jsonl'
    [System.IO.File]::AppendAllText($registry, "`n" + $fixtureReadme)
    $r = Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1')
    Expect $r 1 '^docs-quality: FAIL \(' 'duplicate registry ownership fails'
    Check ($r.Text -match 'DOCUMENT_REGISTRY.jsonl:2, DOCUMENT_REGISTRY.jsonl:3' -and $r.Text -notmatch 'Where\(') "duplicate ownership names the registry rows, not a Where method signature (expected: DOCUMENT_REGISTRY.jsonl:2, DOCUMENT_REGISTRY.jsonl:3 | actual: '$($r.Text)')"

    # An undeclared document is judged where a root covers it: the fixture declares notes/ and leaves notes/extra.md out.
    $tree = New-DocsTree 'docs-finding'
    [System.IO.File]::AppendAllText((Join-Path $tree 'DOCUMENT_REGISTRY.jsonl'), "`n" + '{"root":"notes/","reason":"fixture"}')
    Write-File (Join-Path $tree 'notes/extra.md') "# Undeclared`n"
    Expect (Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1')) 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1: an undeclared document under a root fails'

    # SP-0282, DOC-INTERNAL-QUALITY item W: a render is recorded with its generator and its check, and the check's script is a
    # file of the tree. The render below is generated by and checked with scripts the fixture tree holds.
    function New-RenderTree([string] $Name, [string] $RenderRecord) {
        $renderTree = New-DocsTree $Name
        Write-File (Join-Path $renderTree 'docs/page.html') '<!doctype html><title>x</title>'
        [System.IO.File]::AppendAllText((Join-Path $renderTree 'DOCUMENT_REGISTRY.jsonl'), "`n" + '{"root":"docs/","reason":"fixture"}' + "`n" + $RenderRecord)
        return $renderTree
    }
    $renderHead = '{"path":"docs/page.html","topic":"fixture","area":"fixture","triggers":["fixture"],"role":"render","generator":"scripts/SourceFingerprint.ps1"'
    $tree = New-RenderTree 'docs-render-clean' ($renderHead + ',"check":"scripts/check-docs.ps1 -Check"}')
    Expect (Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1')) 0 '^docs-quality: PASS \(' 'check-docs.ps1: a render with a generator and a check passes'
    $tree = New-RenderTree 'docs-render-no-check' ($renderHead + '}')
    $r = Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1')
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1: a render that names no check fails'
    Check ($r.Text -match 'render entry docs/page\.html names no check') "check-docs.ps1: the finding names the render (expected: render entry docs/page.html names no check | actual: '$($r.Text)')"
    $tree = New-RenderTree 'docs-render-missing-check' ($renderHead + ',"check":"scripts/nothing-here.ps1 -Check"}')
    $r = Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1')
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1: a render whose check names a script the tree does not hold fails'
    Check ($r.Text -match 'its script is not a file of the tree') "check-docs.ps1: the finding says the script is missing (expected: its script is not a file of the tree | actual: '$($r.Text)')"

    # SP-0287, DOC-INTERNAL-QUALITY item V: the maintained corpus is what the declared roots cover, and the ignores are explicit.
    # Each case starts from the base tree (root README.md, record README.md), appends its records and writes its files; `Replace`
    # starts from the records alone. The first line of the output is judged by exit code and last line, the finding by its words.
    $notesRoot = '{"root":"notes/","reason":"fixture"}'
    $notesIgnore = '{"ignore":"notes/n.md","reason":"fixture"}'
    $notesRecord = '{"path":"notes/n.md","topic":"fixture","area":"fixture","triggers":["fixture"],"role":"source"}'
    $subRecord = '{"path":"notes/sub/m.md","topic":"fixture","area":"fixture","triggers":["fixture"],"role":"source"}'
    $oneFinding = '^docs-quality: FAIL \(1 finding\(s\)\)$'
    $note = @{ 'notes/n.md' = "# Note`n" }
    $rootCases = @(
        @{ Name = 'no-root'; Replace = $true; Records = @($fixtureReadme); Files = @{}; Exit = 1; Last = $oneFinding; Text = 'declares no root of the maintained corpus'; Label = 'a registry with no root record fails' }
        @{ Name = 'root-no-reason'; Records = @('{"root":"notes/"}', $notesRecord); Files = $note; Exit = 1; Last = $oneFinding; Text = "root 'notes/' has no reason"; Label = 'a root with no reason fails' }
        @{ Name = 'root-missing'; Records = @('{"root":"gone/","reason":"fixture"}'); Files = @{}; Exit = 1; Last = $oneFinding; Text = "root 'gone/' names no tracked directory"; Label = 'a root that names no tracked directory fails' }
        @{ Name = 'root-empty'; Records = @('{"root":"empty/","reason":"fixture"}'); Files = @{ 'empty/x.txt' = 'x' }; Exit = 1; Last = $oneFinding; Text = "root 'empty/' covers no documentation file"; Label = 'a root that holds no documentation file fails' }
        @{ Name = 'root-twice'; Records = @('{"root":"README.md","reason":"again"}'); Files = @{}; Exit = 1; Last = $oneFinding; Text = "root 'README.md' is declared twice"; Label = 'a root declared twice fails' }
        @{ Name = 'root-nested'; Records = @($notesRoot, '{"root":"notes/sub/","reason":"fixture"}', $notesRecord, $subRecord); Files = @{ 'notes/n.md' = "# Note`n"; 'notes/sub/m.md' = "# Sub`n" }; Exit = 1; Last = $oneFinding; Text = "root 'notes/sub/' lies inside root 'notes/'"; Label = 'a root inside another root fails' }
        @{ Name = 'two-kinds'; Records = @('{"root":"notes/","path":"notes/n.md","reason":"fixture"}'); Files = $note; Exit = 1; Last = $oneFinding; Text = 'record carries path and root'; Label = 'a record that declares two things fails' }
        @{ Name = 'root-bad-path'; Records = @('{"root":"../notes/","reason":"fixture"}'); Files = @{}; Exit = 1; Last = $oneFinding; Text = "root '\.\./notes/' is not a repository-relative path"; Label = 'a root that leaves the repository fails' }
        @{ Name = 'outside'; Records = @(); Files = $note; Exit = 0; Last = '^docs-quality: PASS \(.*1 outside every root, '; Text = 'note: notes/n\.md lies under no declared root'; Label = 'a document under no root is outside the corpus, noted and not judged' }
        @{ Name = 'outside-recorded'; Records = @($notesRecord); Files = $note; Exit = 1; Last = $oneFinding; Text = 'records notes/n\.md, which lies under no declared root'; Label = 'a record for a document under no root fails' }
        @{ Name = 'ignore-excludes'; Records = @($notesRoot, $notesIgnore); Files = $note; Exit = 0; Last = '^docs-quality: PASS \('; Text = ''; Label = 'a document under a root that a reasoned ignore excludes needs no record' }
        @{ Name = 'ignore-and-record'; Records = @($notesRoot, $notesIgnore, $notesRecord); Files = $note; Exit = 1; Last = $oneFinding; Text = 'recorded by DOCUMENT_REGISTRY\.jsonl:\d+ and excluded by the ignore'; Label = 'a document both recorded and excluded fails' }
        @{ Name = 'ignore-no-reason'; Records = @($notesRoot, '{"ignore":"notes/n.md"}'); Files = $note; Exit = 1; Last = $oneFinding; Text = "ignore entry 'notes/n\.md' has no reason"; Label = 'an ignore with no reason fails' }
        @{ Name = 'ignore-nothing'; Records = @($notesRoot, '{"ignore":"notes/none.md","reason":"fixture"}', $notesRecord); Files = $note; Exit = 1; Last = $oneFinding; Text = "ignore 'notes/none\.md' matches no documentation file under a declared root"; Label = 'an ignore that excludes nothing fails' }
    )
    foreach ($case in $rootCases) {
        $tree = New-DocsTree "docs-v-$($case.Name)"
        $lines = if ($case['Replace']) { $case.Records } else { @($fixtureRoot, $fixtureReadme) + $case.Records }
        Write-File (Join-Path $tree 'DOCUMENT_REGISTRY.jsonl') ($lines -join "`n")
        foreach ($file in $case.Files.Keys) { Write-File (Join-Path $tree $file) $case.Files[$file] }
        $r = Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1')
        Expect $r $case.Exit $case.Last "check-docs.ps1 item V: $($case.Label)"
        if ($case.Text) { Check ($r.Text -match $case.Text) "check-docs.ps1 item V: $($case.Label) - the output says it (expected: /$($case.Text)/ | actual: '$($r.Text)')" }
    }

    $tree = New-DocsTree 'docs-locked'
    $r = Invoke-Locked @((Join-Path $tree 'README.md')) { Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1') }
    Expect $r 2 '^docs-quality: COULD NOT VERIFY \(' 'check-docs.ps1: a document that cannot be read could not be verified'

    $tree = New-DocsTree 'docs-finding-then-locked'
    [System.IO.File]::AppendAllText((Join-Path $tree 'DOCUMENT_REGISTRY.jsonl'), "`n" + '{"root":"notes/","reason":"fixture"}')
    Write-File (Join-Path $tree 'notes/extra.md') "# Undeclared`n"
    $r = Invoke-Locked @((Join-Path $tree 'README.md')) { Invoke-Script (Join-Path $tree 'scripts/check-docs.ps1') }
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1: a finding recorded before the stop outranks could-not-verify'

    # --- scripts/check-docs.ps1: the portal contribution, terms and freshness (SP-0239 P5) ---------------------------
    # The "ru" page is Latin transliteration so this file stays ASCII: the rule compares strings, not scripts. The
    # defect it reproduces is the real one - a case ending inside the marks ("Nastroykakh" for the dictionary's
    # "Nastroyki"), which no dictionary holds.
    . (Join-Path $repo 'scripts/SourceFingerprint.ps1')
    $open = [string] [char] 0x00AB
    $close = [string] [char] 0x00BB
    $english = "# Guide`n`nOpen **Settings**, then **Library**.`n"
    $russian = "# Rukovodstvo`n`nOtkroyte **Nastroyki**, zatem $($open)Biblioteka$($close).`n"
    function New-Dictionary([hashtable] $Strings) {
        $rows = foreach ($key in $Strings.Keys) { "<sys:String x:Key=`"$key`">$($Strings[$key])</sys:String>" }
        return '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:sys="clr-namespace:System;assembly=mscorlib">' + ($rows -join '') + '</ResourceDictionary>'
    }
    # $Fingerprint: '' records the real fingerprint of the English page, 'none' omits the field, anything else is recorded.
    function New-PortalTree([string] $Name, [string] $Russian = $script:russian, [string] $Fingerprint = '') {
        $tree = New-DocsTree $Name
        Write-File (Join-Path $tree 'docs/portal/guide.md') $script:english
        Write-File (Join-Path $tree 'docs/portal/guide.ru.md') $Russian
        Write-File (Join-Path $tree 'src/StreamsPlayer.App/Localization.en.xaml') (New-Dictionary @{ Settings = 'Settings'; SettingsLibrary = 'Library' })
        Write-File (Join-Path $tree 'src/StreamsPlayer.App/Localization.ru.xaml') (New-Dictionary @{ Settings = 'Nastroyki'; SettingsLibrary = 'Biblioteka' })
        $recorded = if ($Fingerprint -eq '') { Get-SourceFingerprint (Join-Path $tree 'docs/portal/guide.md') } else { $Fingerprint }
        $mirror = if ($Fingerprint -eq 'none') { '' } else { ",`"mirrorOf`":`"docs/portal/guide.md`",`"mirrorOfHash`":`"$recorded`"" }
        $row = '{{"path":"{0}","topic":"fixture","area":"fixture","triggers":["fixture"],"role":"source"{1}}}'
        Write-File (Join-Path $tree 'DOCUMENT_REGISTRY.jsonl') (($script:fixtureRoot, '{"root":"docs/","reason":"fixture"}', ($row -f 'README.md', ''), ($row -f 'docs/portal/guide.md', ''), ($row -f 'docs/portal/guide.ru.md', $mirror)) -join "`n")
        return $tree
    }
    $docsGate = 'scripts/check-docs.ps1'

    $tree = New-PortalTree 'portal-clean'
    Expect (Invoke-Script (Join-Path $tree $docsGate)) 0 '^docs-quality: PASS \(.*4 control names, 1 mirrors\)$' 'check-docs.ps1 terms: names that are strings of the dictionary, bold and guillemet, pass'

    $tree = New-PortalTree 'portal-inflected-bold' ($russian.Replace('**Nastroyki**', '**Nastroykakh**'))
    $r = Invoke-Script (Join-Path $tree $docsGate)
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 terms: a bold name that is no dictionary string fails'
    Check ($r.Text -match "\[terms\] docs/portal/guide\.ru\.md:3 - 'Nastroykakh' is no string of Localization\.ru\.xaml") "check-docs.ps1 terms: the finding names the page, the line and the name (expected: [terms] guide.ru.md:3 'Nastroykakh' | actual: '$($r.Text)')"

    $tree = New-PortalTree 'portal-inflected-guillemet' ($russian.Replace("$($open)Biblioteka$($close)", "$($open)Bibliotekye$($close)"))
    $r = Invoke-Script (Join-Path $tree $docsGate)
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 terms: a guillemet name that is no dictionary string fails'
    Check ($r.Text -match "\[terms\] docs/portal/guide\.ru\.md:3 - 'Bibliotekye'") "check-docs.ps1 terms: the guillemet finding names the name (expected: [terms] 'Bibliotekye' | actual: '$($r.Text)')"

    $tree = New-PortalTree 'portal-no-dictionary'
    Remove-Item -LiteralPath (Join-Path $tree 'src/StreamsPlayer.App/Localization.ru.xaml')
    Expect (Invoke-Script (Join-Path $tree $docsGate)) 2 '^docs-quality: COULD NOT VERIFY \(no src/StreamsPlayer\.App/Localization\.ru\.xaml' 'check-docs.ps1 terms: a page with no dictionary to judge it against could not be verified'

    $tree = New-PortalTree 'portal-stale'
    Write-File (Join-Path $tree 'docs/portal/guide.md') ($english + "`nA new sentence in the English original.`n")
    $r = Invoke-Script (Join-Path $tree $docsGate)
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 freshness: a changed English source with an unchanged mirror fingerprint fails'
    Check ($r.Text -match '\[freshness\] docs/portal/guide\.ru\.md - docs/portal/guide\.md is sha256:[0-9a-f]{16} but the mirror was rebuilt from sha256:') "check-docs.ps1 freshness: the finding names both fingerprints (expected: [freshness] .. is sha256:<16 hex> but the mirror was rebuilt from sha256: | actual: '$($r.Text)')"

    $tree = New-PortalTree 'portal-line-endings'
    Write-File (Join-Path $tree 'docs/portal/guide.md') $english.Replace("`n", "`r`n")
    Expect (Invoke-Script (Join-Path $tree $docsGate)) 0 '^docs-quality: PASS \(' 'check-docs.ps1 freshness: a checkout with CRLF line endings does not change the fingerprint'

    $tree = New-PortalTree 'portal-no-fingerprint' $russian 'none'
    $r = Invoke-Script (Join-Path $tree $docsGate)
    Expect $r 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 freshness: a portal mirror that records no source fingerprint fails'
    Check ($r.Text -match '\[freshness\] docs/portal/guide\.ru\.md - .* has no mirrorOf') "check-docs.ps1 freshness: the finding says the mirror has no mirrorOf (expected: has no mirrorOf | actual: '$($r.Text)')"

    $tree = New-PortalTree 'portal-bad-fingerprint' $russian 'sha256:XYZ'
    Expect (Invoke-Script (Join-Path $tree $docsGate)) 1 '^docs-quality: FAIL \(1 finding\(s\)\)$' 'check-docs.ps1 freshness: a malformed fingerprint fails'

    # --- scripts/check-help.ps1 --------------------------------------------------------------------------------------
    $helpPage = '<!doctype html><html lang="{0}" dir="ltr"><head>{1}</head><body><h1>x</h1><p class="lead">x</p><ul class="related"></ul><footer class="help-foot"></footer></body></html>'
    $referrer = '<meta name="referrer" content="no-referrer">'
    $robots = '<meta name="robots" content="noindex">'
    $headMeta = $referrer + $robots
    function New-HelpTree([string] $Name) {
        $tree = Join-Path $work $Name
        Write-File (Join-Path $tree 'help/help-map.json') '{"sourceLocale":"en","index":"index","surfaces":[]}'
        Write-File (Join-Path $tree 'help/templates/help.css') 'layer'
        Write-File (Join-Path $tree 'docs/assets/sza-kit.css') 'kit'
        Write-File (Join-Path $tree 'docs/help/help.css') 'layer'
        Write-File (Join-Path $tree 'docs/help/sza-kit.css') 'kit'
        Write-File (Join-Path $tree 'docs/help/en/index.html') ($script:helpPage -f 'en', $script:headMeta)
        return $tree
    }
    $checkHelp = Join-Path $repo 'scripts/check-help.ps1'
    $tree = New-HelpTree 'help-clean'
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 0 '^help: PASS \(' 'check-help.ps1: a minimal help set passes'

    $tree = New-HelpTree 'help-no-kit'
    Remove-Item -LiteralPath (Join-Path $tree 'docs/assets/sza-kit.css')
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 2 '^help: COULD NOT VERIFY \(no .*sza-kit\.css' 'check-help.ps1: a missing source kit could not be verified'

    $tree = New-HelpTree 'help-no-layer'
    Remove-Item -LiteralPath (Join-Path $tree 'help/templates/help.css')
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 2 '^help: COULD NOT VERIFY \(no .*help\.css' 'check-help.ps1: a missing source layer could not be verified'

    $tree = New-HelpTree 'help-defect'
    Write-File (Join-Path $tree 'docs/help/en/index.html') ($helpPage -f 'en', $robots)
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 1 '^help: FAIL \(1 problem\(s\)' 'check-help.ps1: a page that does not withhold the referrer fails'

    # SP-0282, CONTEXT-HELP item G: a help page carries noindex; the one defect here is its absence, named with the page.
    $tree = New-HelpTree 'help-no-noindex'
    Write-File (Join-Path $tree 'docs/help/en/index.html') ($helpPage -f 'en', $referrer)
    $r = Invoke-Script $checkHelp @('-Root', $tree)
    Expect $r 1 '^help: FAIL \(1 problem\(s\)' 'check-help.ps1: a page with no noindex fails'
    Check ($r.Text -match 'FAIL  search: en/index\.html carries no noindex') "check-help.ps1: the noindex failure names the page (expected: search: en/index.html carries no noindex | actual: '$($r.Text)')"

    $tree = New-HelpTree 'help-locked'
    $r = Invoke-Locked @((Join-Path $tree 'docs/help/en/index.html')) { Invoke-Script $checkHelp @('-Root', $tree) }
    Expect $r 2 '^help: COULD NOT VERIFY \(' 'check-help.ps1: a page that cannot be read could not be verified'

    $tree = New-HelpTree 'help-defect-then-locked'
    Write-File (Join-Path $tree 'docs/help/de/index.html') ($helpPage -f 'de', $robots)
    $r = Invoke-Locked @((Join-Path $tree 'docs/help/en/index.html')) { Invoke-Script $checkHelp @('-Root', $tree) }
    Expect $r 1 '^help: FAIL \(1 problem\(s\)\)$' 'check-help.ps1: a problem recorded before the stop outranks could-not-verify'

    # SP-0239 P5: the index of every locale links every written page of that locale. One written page, `main`, on the
    # picture baseline so that it owes no screenshot; the second locale ("de") is the one whose index forgets it.
    $mainPage = '<!doctype html><html lang="{0}" dir="ltr"><head>{1}</head><body><h1>x</h1><p class="lead">x</p><h2>Order</h2><p>none</p><div class="elements"></div><ul class="related"></ul><footer class="help-foot"></footer></body></html>'
    $indexPage = '<!doctype html><html lang="{0}" dir="ltr"><head>{1}</head><body><h1>x</h1><p class="lead">x</p><ul class="related">{2}</ul><footer class="help-foot"></footer></body></html>'
    function New-IndexedHelpTree([string] $Name, [string] $GermanIndexLinks) {
        $tree = New-HelpTree $Name
        Write-File (Join-Path $tree 'help/help-map.json') '{"sourceLocale":"en","index":"index","surfaces":[{"id":"main","form":"page","help":"main"}]}'
        Write-File (Join-Path $tree 'help/help-baseline.txt') "shot main`n"
        $link = '<li><a href="main.html">m</a></li>'
        Write-File (Join-Path $tree 'docs/help/en/index.html') ($script:indexPage -f 'en', $script:headMeta, $link)
        Write-File (Join-Path $tree 'docs/help/en/main.html') ($script:mainPage -f 'en', $script:headMeta)
        Write-File (Join-Path $tree 'docs/help/de/index.html') ($script:indexPage -f 'de', $script:headMeta, $GermanIndexLinks)
        Write-File (Join-Path $tree 'docs/help/de/main.html') ($script:mainPage -f 'de', $script:headMeta)
        return $tree
    }
    $tree = New-IndexedHelpTree 'help-index-complete' '<li><a href="main.html">m</a></li>'
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 0 '^help: PASS \(4 pages in 2 locales' 'check-help.ps1 index: every index links the written page of its locale'

    # SP-0282, CHECK-BASELINE item B: the baseline is held against its copy at HEAD. A tree with no git is not compared and
    # the verdict line says so; a tree with a commit judges the lines the working file added.
    $r = Invoke-Script $checkHelp @('-Root', $tree)
    Expect $r 0 '^help: PASS \(.*baseline not compared with HEAD, no committed copy\)$' 'check-help.ps1 baseline: a tree with no git is not compared and says so'
    function New-CommittedHelpTree([string] $Name, [string] $Committed, [string] $Working) {
        $committedTree = New-IndexedHelpTree $Name '<li><a href="main.html">m</a></li>'
        Write-File (Join-Path $committedTree 'help/help-baseline.txt') $Committed
        & git -C $committedTree init --quiet 2>&1 | Out-Null
        & git -C $committedTree add --all 2>&1 | Out-Null
        & git -C $committedTree -c user.name=fixture -c user.email=fixture@example.test -c commit.gpgsign=false commit --quiet -m fixture 2>&1 | Out-Null
        Write-File (Join-Path $committedTree 'help/help-baseline.txt') $Working
        return $committedTree
    }
    $tree = New-CommittedHelpTree 'help-baseline-same' "shot main`n" "shot main`n"
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 0 '^help: PASS \(.*baseline held against HEAD\)$' 'check-help.ps1 baseline: a baseline equal to its committed copy passes'
    $tree = New-CommittedHelpTree 'help-baseline-raised' "shot main`n" "button foo`nshot main`n"
    $r = Invoke-Script $checkHelp @('-Root', $tree)
    Expect $r 1 '^help: FAIL \(1 problem\(s\); ' 'check-help.ps1 baseline: a line the working file added fails'
    Check ($r.Text -match 'FAIL  baseline: help/help-baseline\.txt gained 1 line\(s\) that the copy at HEAD does not hold: button foo - ') "check-help.ps1 baseline: the failure names the added line (expected: gained 1 line(s) ... button foo | actual: '$($r.Text)')"
    $tree = New-CommittedHelpTree 'help-baseline-paid' "button gone`nshot main`n" "shot main`n"
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 0 '^help: PASS \(.*baseline held against HEAD\)$' 'check-help.ps1 baseline: a line the working file removed passes (paying debt down)'
    $tree = New-CommittedHelpTree 'help-baseline-commented' "shot main`n" "# a reason beside the line`nshot main`n"
    Expect (Invoke-Script $checkHelp @('-Root', $tree)) 0 '^help: PASS \(.*baseline held against HEAD\)$' 'check-help.ps1 baseline: a comment line is not an identifier'

    $tree = New-IndexedHelpTree 'help-index-incomplete' ''
    $r = Invoke-Script $checkHelp @('-Root', $tree)
    Expect $r 1 '^help: FAIL \(1 problem\(s\); 4 pages in 2 locales' 'check-help.ps1 index: an index that omits a written page fails, once, for its own locale'
    Check ($r.Text -match 'FAIL  index: de/index\.html does not link the page main\.html' -and $r.Text -notmatch 'index: en/') "check-help.ps1 index: the failure names the locale and the page (expected: index: de/index.html does not link the page main.html | actual: '$($r.Text)')"

    $tree = New-IndexedHelpTree 'help-index-foreign-link' '<li><a href="https://example.test/main.html">m</a><a href="../en/main.html">m</a></li>'
    $r = Invoke-Script $checkHelp @('-Root', $tree)
    Check ($r.Exit -eq 1 -and $r.Text -match 'FAIL  index: de/index\.html does not link the page main\.html') "check-help.ps1 index: a link to another origin or another locale is not a link to the page (expected: exit 1 and the index finding for de | actual: exit $($r.Exit), '$($r.Text)')"

    # --- tools/help/build-help.ps1 -----------------------------------------------------------------------------------
    function New-HelpBuildTree([string] $Name) {
        $tree = Join-Path $work $Name
        New-Item -ItemType Directory -Path (Join-Path $tree 'tools/help') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo 'tools/help/build-help.ps1') -Destination (Join-Path $tree 'tools/help/build-help.ps1')
        New-Item -ItemType Directory -Path (Join-Path $tree 'scripts') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo 'scripts/SourceFingerprint.ps1') -Destination (Join-Path $tree 'scripts/SourceFingerprint.ps1')
        Write-File (Join-Path $tree 'help/help-map.json') '{"sourceLocale":"en","index":"index","surfaces":[]}'
        Write-File (Join-Path $tree 'help/stamp.json') '{"version":"1.0","date":"2026-01-01"}'
        $chrome = [ordered]@{
            indexTitle = 'Help'; kicker = 'k'; indexLead = 'lead'; indexAskHeading = 'ask'; indexAsk = @('a')
            indexWindowsHeading = 'windows'; relatedHeading = 'related'; indexRelated = @(); site = @{}
            pillWindow = 'w'; pillSite = 's'; footerWritten = '{product} {version} {date}'; indexLink = 'index'; support = 'support'
        }
        Write-File (Join-Path $tree 'help/content/en/_chrome.json') ($chrome | ConvertTo-Json -Depth 5)
        Write-File (Join-Path $tree 'help/templates/help.css') 'layer'
        Write-File (Join-Path $tree 'docs/assets/sza-kit.css') 'kit'
        Write-File (Join-Path $tree 'src/StreamsPlayer.App/Localization.en.xaml') '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:sys="clr-namespace:System;assembly=mscorlib"><sys:String x:Key="ProductName">Fixture</sys:String></ResourceDictionary>'
        foreach ($frame in 'mark-24.png', 'mark-48.png') { Write-Bytes (Join-Path $tree "assets/exchange-mark/png/$frame") ([byte[]]@(1, 2, 3)) }
        return $tree
    }
    $tree = New-HelpBuildTree 'build-help'
    $buildHelp = Join-Path $tree 'tools/help/build-help.ps1'
    Expect (Invoke-Script $buildHelp) 0 '^help-build: PASS \(.*written\)$' 'build-help.ps1: the generator writes the set'
    Expect (Invoke-Script $buildHelp @('-Check')) 0 '^help-build: PASS \(.*in sync\)$' 'build-help.ps1: -Check passes on what the generator wrote'
    Add-Content -LiteralPath (Join-Path $tree 'docs/help/en/index.html') -Value '<!-- stale -->'
    Expect (Invoke-Script $buildHelp @('-Check')) 1 '^help-build: FAIL \(1 file\(s\) out of date' 'build-help.ps1: -Check fails a stale page'
    Write-File (Join-Path $tree 'help/stamp.json') '{ not json'
    $r = Invoke-Script $buildHelp @('-Check')
    Expect $r 2 '^help-build: COULD NOT VERIFY \(.*stamp\.json does not parse' 'build-help.ps1: -Check could not verify a source that does not parse'
    Write-File (Join-Path $tree 'help/stamp.json') '{"version":"1.0","date":"2026-01-01"}'
    Write-File (Join-Path $tree 'help/content/en/_chrome.json') '{"indexTitle":"Help"}'
    Expect (Invoke-Script $buildHelp @('-Check')) 2 '^help-build: COULD NOT VERIFY \(' 'build-help.ps1: -Check could not verify a copy the renderer throws on'
    $r = Invoke-Script $buildHelp
    Check ($r.Exit -ne 0 -and $r.Text -notmatch 'help-build: COULD NOT VERIFY') "build-help.ps1: a plain generator run keeps its exception (expected: non-zero exit, no verdict line | actual: exit $($r.Exit), '$($r.Last)')"

    # SP-0239 P5: a translation records the fingerprint of the English copy it was rebuilt from. The tree gets a second
    # locale whose chrome copy is the English one plus "sourceHash"; the generator is run again after each change of the
    # English copy so that the pages are in sync and the stale translation is the only thing -Check can find.
    $tree = New-HelpBuildTree 'build-help-fresh'
    $buildHelp = Join-Path $tree 'tools/help/build-help.ps1'
    $englishChrome = Join-Path $tree 'help/content/en/_chrome.json'
    $translatedChrome = Join-Path $tree 'help/content/ru/_chrome.json'
    Copy-Item -LiteralPath (Join-Path $tree 'src/StreamsPlayer.App/Localization.en.xaml') -Destination (Join-Path $tree 'src/StreamsPlayer.App/Localization.ru.xaml')
    function Set-TranslatedChrome([string] $Fingerprint) {
        $copy = Get-Content -LiteralPath $script:englishChrome -Raw | ConvertFrom-Json -AsHashtable
        if ($Fingerprint) { $copy['sourceHash'] = $Fingerprint }
        Write-File $script:translatedChrome ($copy | ConvertTo-Json -Depth 5)
    }
    . (Join-Path $repo 'scripts/SourceFingerprint.ps1')
    Set-TranslatedChrome (Get-SourceFingerprint $englishChrome)
    Expect (Invoke-Script $buildHelp) 0 '^help-build: PASS \(2 locales' 'build-help.ps1 freshness: the generator writes both locales'
    Expect (Invoke-Script $buildHelp @('-Check')) 0 '^help-build: PASS \(2 locales.*in sync\)$' 'build-help.ps1 freshness: a translation that records the current English fingerprint passes'

    $changed = Get-Content -LiteralPath $englishChrome -Raw | ConvertFrom-Json -AsHashtable
    $changed['indexTitle'] = 'Help centre'
    Write-File $englishChrome ($changed | ConvertTo-Json -Depth 5)
    Invoke-Script $buildHelp | Out-Null
    $r = Invoke-Script $buildHelp @('-Check')
    Expect $r 1 '^help-build: FAIL \(1 translation\(s\) older than their English copy; rebuild them and record its fingerprint\)$' 'build-help.ps1 freshness: a changed English copy with an unchanged translation fingerprint fails'
    Check ($r.Text -match 'help/content/en/_chrome\.json is sha256:[0-9a-f]{16} but help/content/ru/_chrome\.json was rebuilt from sha256:') "build-help.ps1 freshness: the failure names both files and both fingerprints (expected: en/_chrome.json is sha256:.. but ru/_chrome.json was rebuilt from sha256: | actual: '$($r.Text)')"

    Set-TranslatedChrome (Get-SourceFingerprint $englishChrome)
    Invoke-Script $buildHelp | Out-Null
    Expect (Invoke-Script $buildHelp @('-Check')) 0 '^help-build: PASS \(2 locales.*in sync\)$' 'build-help.ps1 freshness: rebuilding the translation and recording the new fingerprint clears the failure'

    Set-TranslatedChrome ''
    Invoke-Script $buildHelp | Out-Null
    $r = Invoke-Script $buildHelp @('-Check')
    Expect $r 1 '^help-build: FAIL \(1 translation\(s\) older than their English copy' 'build-help.ps1 freshness: a translation that records no fingerprint fails'
    Check ($r.Text -match 'help/content/ru/_chrome\.json has no sourceHash of the form sha256:<16 hex>') "build-help.ps1 freshness: the failure says the translation has no sourceHash (expected: has no sourceHash of the form sha256:<16 hex> | actual: '$($r.Text)')"
    Expect (Invoke-Script $buildHelp) 0 '^help-build: PASS \(2 locales.*written\)$' 'build-help.ps1 freshness: the plain generator run does not look at the fingerprint'

    # --- tools/site/build-site.ps1 -----------------------------------------------------------------------------------
    $tree = Join-Path $work 'build-site'
    New-Item -ItemType Directory -Path (Join-Path $tree 'tools/site') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'tools/InterfaceLanguages.ps1') -Destination (Join-Path $tree 'tools/InterfaceLanguages.ps1')
    foreach ($name in 'build-site', 'HeldAddresses', 'SiteFacts', 'SiteDecks', 'SiteDiscovery', 'SiteNotFound', 'ReleaseNotes', 'ReleaseManifest') {
        Copy-Item -LiteralPath (Join-Path $repo "tools/site/$name.ps1") -Destination (Join-Path $tree "tools/site/$name.ps1")
    }
    $buildSite = Join-Path $tree 'tools/site/build-site.ps1'
    Expect (Invoke-Script $buildSite @('-Check')) 2 '^site-sync: COULD NOT VERIFY \(' 'build-site.ps1: -Check could not verify a missing provenance file'
    $r = Invoke-Script $buildSite
    Check ($r.Exit -ne 0 -and $r.Text -notmatch 'site-sync:') "build-site.ps1: a plain generator run keeps its exception (expected: non-zero exit, no verdict line | actual: exit $($r.Exit), '$($r.Last)')"
    $pin = 'A' * 64
    Write-File (Join-Path $tree 'tools/site/kit-provenance.txt') "sza-kit.css  $pin`nwave-particles.js  $pin`n"
    Write-File (Join-Path $tree 'docs/assets/sza-kit.css') 'not the pinned kit'
    Write-File (Join-Path $tree 'docs/assets/wave-particles.js') 'not the pinned script'
    Expect (Invoke-Script $buildSite @('-Check')) 1 '^site-sync: FAIL \(docs/assets/sza-kit\.css is not the pinned file' 'build-site.ps1: -Check fails a vendored file that differs from its pin'

    # --- tools/site/make-demo.ps1 ------------------------------------------------------------------------------------
    function New-Box([string] $Type, [byte[]] $Payload) {
        $size = 8 + $Payload.Length
        $bytes = [System.Collections.Generic.List[byte]]::new()
        $bytes.AddRange([byte[]]@((($size -shr 24) -band 255), (($size -shr 16) -band 255), (($size -shr 8) -band 255), ($size -band 255)))
        $bytes.AddRange([System.Text.Encoding]::ASCII.GetBytes($Type))
        $bytes.AddRange($Payload)
        return , $bytes.ToArray()
    }
    function Join-Bytes([byte[][]] $Parts) {
        $all = [System.Collections.Generic.List[byte]]::new()
        foreach ($part in $Parts) { $all.AddRange($part) }
        return , $all.ToArray()
    }
    # ftyp, moov (mvhd: timescale 1000, duration 5000; one trak whose hdlr is vide), mdat: a silent 5 s file, moov first.
    $mvhd = New-Box 'mvhd' ([byte[]]@(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0xE8, 0, 0, 0x13, 0x88))
    $hdlr = New-Box 'hdlr' ([byte[]]@(0, 0, 0, 0, 0, 0, 0, 0) + [System.Text.Encoding]::ASCII.GetBytes('vide'))
    $trak = New-Box 'trak' (New-Box 'mdia' $hdlr)
    $mp4 = Join-Bytes @((New-Box 'ftyp' ([byte[]]@(105, 115, 111, 109, 0, 0, 0, 0))), (New-Box 'moov' (Join-Bytes @($mvhd, $trak))), (New-Box 'mdat' ([byte[]]@(0, 0, 0, 0))))
    $gif = [byte[]]@(71, 73, 70, 56, 57, 97, 0xD0, 0x02, 0x00, 0x01, 0, 0, 0, 0x3B)    # GIF89a, 720 px wide, trailer
    $poster = [byte[]]@(0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9)                              # SOI ... EOI
    function New-DemoFolder([string] $Name, [string[]] $Leave = @()) {
        $folder = Join-Path $work $Name
        $files = [ordered]@{ 'streamsplayer-demo.mp4' = $script:mp4; 'streamsplayer-demo.gif' = $script:gif; 'streamsplayer-demo-poster.jpg' = $script:poster }
        foreach ($file in $files.Keys) { if ($Leave -notcontains $file) { Write-Bytes (Join-Path $folder $file) $files[$file] } }
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        return $folder
    }
    $makeDemo = Join-Path $repo 'tools/site/make-demo.ps1'
    $folder = New-DemoFolder 'demo-good'
    Expect (Invoke-Script $makeDemo @('-Check', '-OutDir', $folder)) 0 '^demo-capture: PASS$' 'make-demo.ps1: -Check passes three good files'
    $folder = New-DemoFolder 'demo-missing' @('streamsplayer-demo.mp4')
    Expect (Invoke-Script $makeDemo @('-Check', '-OutDir', $folder)) 1 '^demo-capture: FAIL \(0 finding\(s\), 1 missing\)$' 'make-demo.ps1: -Check fails a missing file (it was exit 3 and MISSING)'
    $folder = New-DemoFolder 'demo-locked'
    $r = Invoke-Locked @((Join-Path $folder 'streamsplayer-demo.gif')) { Invoke-Script $makeDemo @('-Check', '-OutDir', $folder) }
    Expect $r 2 '^demo-capture: COULD NOT VERIFY \(streamsplayer-demo\.gif\)$' 'make-demo.ps1: -Check could not verify a file that cannot be read'

    # --- scripts/check.ps1, over stub children and a stub dotnet -----------------------------------------------------
    $checkText = Get-Content -LiteralPath (Join-Path $repo 'scripts/check.ps1') -Raw
    $children = @([regex]::Matches($checkText, "Join-Path \`$root '([^']+\.ps1)'") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    Check ($children.Count -gt 10 -and $children -contains 'tools/site/make-demo.ps1') "check.ps1: the fixture found its child scripts (expected: more than 10, make-demo among them | actual: $($children.Count))"
    function New-CheckTree([string] $Name, [hashtable] $Exits = @{}, [switch] $WithDemo) {
        $tree = Join-Path $work $Name
        New-Item -ItemType Directory -Path (Join-Path $tree 'scripts') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $script:repo 'scripts/check.ps1') -Destination (Join-Path $tree 'scripts/check.ps1')
        foreach ($child in $script:children) { Write-File (Join-Path $tree $child) "exit $(if ($Exits.ContainsKey($child)) { $Exits[$child] } else { 0 })`n" }
        if ($WithDemo) { Write-File (Join-Path $tree 'docs/assets/demo/streamsplayer-demo.mp4') 'stub' }
        Write-File (Join-Path $tree 'shim/dotnet.cmd') "@exit /b 0`r`n"
        return $tree
    }
    function Invoke-Check([string] $Tree) {
        return Invoke-Script (Join-Path $Tree 'scripts/check.ps1') @() ("$(Join-Path $Tree 'shim')$([System.IO.Path]::PathSeparator)$env:PATH")
    }
    $tree = New-CheckTree 'check-pass'
    Expect (Invoke-Check $tree) 0 "^check: PASS \($($children.Count - 1) checks, 1 skipped\)$" 'check.ps1: all children pass and the skipped demo check is counted'
    $tree = New-CheckTree 'check-pass-demo' -WithDemo
    Expect (Invoke-Check $tree) 0 "^check: PASS \($($children.Count) checks, 0 skipped\)$" 'check.ps1: all children pass with the demo check run'
    $tree = New-CheckTree 'check-busy' @{ 'scripts/check-docs.ps1' = 4 }
    Expect (Invoke-Check $tree) 2 '^check: COULD NOT VERIFY \(docs-quality; 1 skipped\)$' 'check.ps1: a child that answers 4 reads as 2'
    $tree = New-CheckTree 'check-busy-and-failed' @{ 'scripts/check-docs.ps1' = 4; 'tools/site/build-site.ps1' = 1 }
    Expect (Invoke-Check $tree) 1 '^check: FAIL \(site-sync; 1 skipped\)$' 'check.ps1: a defect outranks a child that answers 4'
    $tree = New-CheckTree 'check-odd-code' @{ 'scripts/check-docs.ps1' = 7 }
    Expect (Invoke-Check $tree) 1 '^check: FAIL \(docs-quality; 1 skipped\)$' 'check.ps1: a child code outside 0 to 4 is read as a defect (rule 2.14)'
    $tree = New-CheckTree 'check-no-dotnet'
    $emptyPath = Join-Path $work 'empty-path'
    New-Item -ItemType Directory -Path $emptyPath -Force | Out-Null
    Expect (Invoke-Script (Join-Path $tree 'scripts/check.ps1') @() $emptyPath) 2 '^check: COULD NOT VERIFY \(.*dotnet' 'check.ps1: a missing dotnet could not be verified instead of throwing'
}
catch {
    # The fixture itself could not run (a tool it needs is missing, a temp folder was refused): nothing was judged past
    # this point. A case that already failed still outranks it (CHECK-VERDICT 2.14).
    $reason = $_.Exception.Message -replace '\s+', ' '
    Write-Output "check-verdict-scripts: the run stopped early: $reason"
    if ($failures.Count -eq 0) { "check-verdict-scripts: COULD NOT VERIFY run=$run skipped=0"; exit 2 }
}
finally {
    Remove-Item Env:SP0239_PROBE_ROOT -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $work) {
        Get-ChildItem -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Attributes = 'Normal' } catch { } }
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -eq 0) { "check-verdict-scripts: PASS run=$run skipped=0"; exit 0 }
"check-verdict-scripts: FAIL run=$run skipped=0"
exit 1
