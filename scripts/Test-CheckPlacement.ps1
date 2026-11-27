#requires -Version 7.0
<#
.SYNOPSIS
  CHECK-PLACEMENT (0.11): the two-way check of scripts/check-placement.jsonl, one record per check.

.DESCRIPTION
  Forward: a record's class must match where the check actually runs - a per-change record must be a named
  check of scripts/check-fast.ps1 (which ci.yml runs on every push), a release record must be invoked by a
  step of .github/workflows/release.yml, a build record must be invoked by every build runner it names.
  Backward: every child scripts/check.ps1 invokes, every check in check-fast.ps1's list, and every PowerShell
  script under scripts/, tools/ and msix/ that emits a `<subject>: <closed word>` verdict line must carry a
  record. The detector is textual, so a check that builds its verdict without a literal closed word, or that
  lives outside those roots, is a blind spot of this check and is named in the registry row, not hidden.

  A record carries file, class (rule 8's vocabulary: per-change, agent-closure, build, release, hand-run),
  runner, decided, ticket, state and reason. A seeded record is refused: this registry is written judged from
  the wiring, and a placeholder would need the named open owner rule 7 asks for.

    placement-registry: PASS (N records (per-change a, release b, build c, hand-run d), K verdict scripts)
    placement-registry: FAIL (<n> finding(s))
    placement-registry: COULD NOT VERIFY (<reason>)
  Exit 0 pass, 1 a finding, 2 could not verify (CHECK-VERDICT).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $Condition, [string] $Name, [string] $Actual) {
    if ($Condition) { return }
    $script:failures.Add("$Name | actual: $Actual")
}
function Read-Text([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "missing file: $Path" }
    [System.IO.File]::ReadAllText($Path)
}

try {
    # --- the registry itself ---------------------------------------------------------------------------------------
    $records = @()
    $lineNo = 0
    foreach ($line in ((Read-Text (Join-Path $root 'scripts/check-placement.jsonl')) -split "`n" | Where-Object { $_.Trim() })) {
        $lineNo++
        try { $record = $line | ConvertFrom-Json -ErrorAction Stop }
        catch { $failures.Add("record $lineNo is one JSON object | actual: $($_.Exception.Message)"); continue }
        foreach ($field in 'file', 'class', 'runner', 'decided', 'ticket', 'state', 'reason') {
            $value = $record.PSObject.Properties[$field]
            Check ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string] $value.Value)) "record $lineNo carries $field" 'the field is missing or empty'
        }
        Check ($record.file -match '\.ps1$') "record $lineNo is keyed by a PowerShell file" "$($record.file)"
        Check ($record.state -ceq 'judged') "record $($record.file) is judged, not seeded (rule 7: a placeholder needs a named open owner)" "$($record.state)"
        Check ($record.class -in 'per-change', 'agent-closure', 'build', 'release', 'hand-run') "record $($record.file) uses a rule 8 class" "$($record.class)"
        Check ($record.decided -match '^\d{4}-\d{2}-\d{2}$') "record $($record.file) carries a date" "$($record.decided)"
        $records += $record
    }
    $byFile = @{}
    foreach ($record in $records) {
        Check (-not $byFile.ContainsKey($record.file)) 'one record per check' "$($record.file) is recorded twice"
        $byFile[$record.file] = $record
        Check (Test-Path -LiteralPath (Join-Path $root $record.file) -PathType Leaf) 'the recorded check exists' "$($record.file)"
    }

    # --- forward: a record must match where the check runs ----------------------------------------------------------
    $fastText = Read-Text (Join-Path $root 'scripts/check-fast.ps1')
    $fastFiles = @([regex]::Matches($fastText, "File = '([^']+)'") | ForEach-Object { $_.Groups[1].Value })
    $releaseText = Read-Text (Join-Path $root '.github/workflows/release.yml')
    $ciText = Read-Text (Join-Path $root '.github/workflows/ci.yml')
    foreach ($record in ($records | Where-Object { $_.class -eq 'per-change' })) {
        # A library runs inside its wrapper ("through <wrapper>"), and the fast tier's own aggregator is the
        # script ci.yml invokes directly; both are placed when their runner really runs them.
        $through = [regex]::Match($record.runner, 'through ([^\s;)]+)').Groups[1].Value
        $direct = $record.runner -match '^\.github/workflows/ci\.yml \(' -and
            ($ciText -cmatch [regex]::Escape([System.IO.Path]::GetFileName($record.file)))
        Check (($fastFiles -contains $record.file) -or ($through -and ($fastFiles -contains $through)) -or $direct) 'a per-change record is a named check of check-fast.ps1 (directly, through its wrapper, or by ci.yml itself)' "$($record.file)"
    }
    foreach ($record in ($records | Where-Object { $_.class -eq 'release' })) {
        $base = [System.IO.Path]::GetFileName($record.file)
        Check ($releaseText -cmatch [regex]::Escape($base)) 'a release record is invoked by release.yml' "$($record.file): $base not found in release.yml"
    }
    foreach ($record in ($records | Where-Object { $_.class -eq 'build' })) {
        foreach ($runner in ($record.runner -split ';')) {
            $runner = $runner.Trim() -replace ' \(.*\)$', ''
            try { $runnerText = Read-Text (Join-Path $root $runner) }
            catch { $failures.Add("the build runner of $($record.file) exists | actual: $($_.Exception.Message)"); continue }
            $base = [System.IO.Path]::GetFileName($record.file)
            Check ($runnerText -cmatch [regex]::Escape($base)) "$runner invokes the build check it names" "$($record.file): $base not found in $runner"
        }
    }

    # --- backward: a check that runs must carry a record ------------------------------------------------------------
    $checkText = Read-Text (Join-Path $root 'scripts/check.ps1')
    $checkChildren = @([regex]::Matches($checkText, 'Join-Path \$root ''([^'']+\.ps1)''') |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    Check ($checkChildren.Count -gt 20) "the backward scan found check.ps1's children" "expected: more than 20 | actual: $($checkChildren.Count)"
    foreach ($child in $checkChildren) {
        Check ($byFile.ContainsKey($child)) 'a check.ps1 child carries a record' "$child"
    }
    foreach ($fastFile in $fastFiles) {
        Check ($byFile.ContainsKey($fastFile)) 'a check-fast.ps1 check carries a record' "$fastFile"
    }

    # A verdict line emitted anywhere under scripts/, tools/ or msix/ needs a record. The subject may hold spaces
    # ("Smoke verdict for 26.1117.1838 (commit ab12cd34): PASS, .."), so the subject is any prefix before the colon.
    $verdictScriptCount = 0
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $root 'scripts'), (Join-Path $root 'tools'), (Join-Path $root 'msix') -Recurse -Filter '*.ps1' -File)) {
        $relative = [System.IO.Path]::GetRelativePath($root, $file.FullName) -replace '\\', '/'
        $text = [System.IO.File]::ReadAllText($file.FullName)
        if ($text -notmatch '[A-Za-z][A-Za-z0-9 ._-]{0,60}:\s*(PASS WITH ADVISORIES|COULD NOT VERIFY|NOT APPLICABLE|PASS|FAIL)\b') { continue }
        $verdictScriptCount++
        Check ($byFile.ContainsKey($relative)) 'a verdict-line script carries a record' "$relative"
    }

    # --- verdict -----------------------------------------------------------------------------------------------------
    $counts = @(foreach ($class in 'per-change', 'agent-closure', 'build', 'release', 'hand-run') {
        $n = @($records | Where-Object { $_.class -eq $class }).Count
        if ($n) { "$class $n" }
    })
    if ($failures.Count) {
        foreach ($failure in $failures) { Write-Host "FAIL: $failure" }
        Write-Host "placement-registry: FAIL ($($failures.Count) finding(s))"
        exit 1
    }
    Write-Host "placement-registry: PASS ($($records.Count) records ($($counts -join ', ')), $($checkChildren.Count + $fastFiles.Count) runner entries, $verdictScriptCount verdict scripts)"
    exit 0
}
catch {
    # A prerequisite nothing above anticipated (a runner file moved, ..): nothing past it was judged.
    Write-Host "placement-registry: COULD NOT VERIFY ($($_.Exception.Message -replace '\s+', ' '))"
    exit 2
}
