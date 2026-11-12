Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestResultSummary.ps1"
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('temp/SP-0232/trx-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$path = Join-Path $fixture 'test.trx'
$run = 0
$failures = 0
# The closed word set of CHECK-VERDICT rule 2.5 that travels with each exit code (0 pass, 1 defect, 2 could not verify).
$words = @{ 0 = 'PASS'; 1 = 'FAIL'; 2 = 'COULD NOT VERIFY' }
function Check([int] $Total, [int] $Executed, [int] $Passed, [int] $Failed, [int] $Runner, [int] $Expected, [string] $Outcome = 'Completed') {
    $text = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="{0}"><Counters total="{1}" executed="{2}" passed="{3}" failed="{4}"/></ResultSummary></TestRun>' -f $Outcome, $Total, $Executed, $Passed, $Failed
    [System.IO.File]::WriteAllText($script:path, $text)
    $actual = Get-TestResultSummary -Directory $script:fixture -ExpectedFiles 1 -RunnerExitCode $Runner
    $script:run++
    if ($actual.ExitCode -ne $Expected -or $actual.Verdict -cne $script:words[$Expected] -or $actual.Run -ne $Executed -or $actual.Skipped -ne ($Total - $Executed)) {
        $script:failures++; Write-Host "FAIL: $text => $($actual | ConvertTo-Json -Compress)"
    }
}
Check 5 5 5 0 0 0
Check 0 0 0 0 0 2
Check 5 0 0 0 0 2
Check 5 4 4 0 0 2
Check 5 5 4 1 0 1 -Outcome Failed
Check 5 4 3 1 0 1 -Outcome Failed
Check 5 5 5 0 1 1
Check 5 5 4 0 0 2
Check 5 5 5 0 0 2 -Outcome Aborted
foreach ($content in '<broken', '<TestRun/>', '<TestRun><ResultSummary outcome="Completed"><Counters total="-1" executed="0" passed="0" failed="0"/></ResultSummary></TestRun>') {
    [System.IO.File]::WriteAllText($path, $content)
    $actual = Get-TestResultSummary -Directory $fixture -ExpectedFiles 1 -RunnerExitCode 0
    $run++
    if ($actual.ExitCode -ne 2 -or $actual.Verdict -cne $words[2]) { $failures++ }
}
Remove-Item -LiteralPath $path
$actual = Get-TestResultSummary -Directory $fixture -ExpectedFiles 1 -RunnerExitCode 0
$run++
if ($actual.ExitCode -ne 2 -or $actual.Verdict -cne $words[2]) { $failures++ }
$large = '<TestRun><ResultSummary outcome="Completed"><Counters total="9223372036854775807" executed="9223372036854775807" passed="9223372036854775807" failed="0"/></ResultSummary></TestRun>'
[System.IO.File]::WriteAllText($path, $large)
[System.IO.File]::WriteAllText((Join-Path $fixture 'overflow.trx'), $large)
$actual = Get-TestResultSummary -Directory $fixture -ExpectedFiles 2 -RunnerExitCode 0
$run++
if ($actual.ExitCode -ne 2 -or $actual.Verdict -cne $words[2]) { $failures++ }
# SP-0246 S3-1: the prune removes only old tests-<guid> folders beyond the newest -Keep, never the current run's, never another name.
$scratch = Join-Path $fixture 'scratch'
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
function New-ResultFolder([string] $Name, [int] $AgeDays) {
    $folder = Join-Path $script:scratch $Name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $folder 'x.trx'), 'x')
    (Get-Item -LiteralPath $folder).LastWriteTimeUtc = [datetime]::UtcNow.AddDays(-$AgeDays)
    return $folder
}
$ids = 0..8 | ForEach-Object { [string]::Format('{0:x32}', $_) }
$old = 0..8 | ForEach-Object { New-ResultFolder ('tests-' + $ids[$_]) (30 + $_) }   # [0] is the newest of the old ones, [8] the oldest
$young = New-ResultFolder ('tests-' + ('a' * 32)) 1
$odd = New-ResultFolder 'tests-notahexguid' 400
$other = New-ResultFolder 'keepme' 400
$removed = @(Remove-StaleTestResults -Scratch $scratch -Current $old[8])
# Newest first: young, old[0..2] are the four kept; old[3..7] are old and beyond them; old[8] is the current run's.
$expectedGone = 3..7 | ForEach-Object { 'tests-' + $ids[$_] }
$run++
if ((($removed | Sort-Object) -join ',') -cne (($expectedGone | Sort-Object) -join ',')) {
    $failures++; Write-Host "FAIL: prune removed '$($removed -join ',')', expected '$($expectedGone -join ',')'"
}
foreach ($kept in @($young, $odd, $other, $old[0], $old[1], $old[2], $old[8])) {
    $run++
    if (-not (Test-Path -LiteralPath $kept)) { $failures++; Write-Host "FAIL: prune removed $kept" }
}
foreach ($gone in 3..7) {
    $run++
    if (Test-Path -LiteralPath $old[$gone]) { $failures++; Write-Host "FAIL: prune left $($old[$gone])" }
}
$run++
if (@(Remove-StaleTestResults -Scratch (Join-Path $fixture 'absent')).Count -ne 0) { $failures++; Write-Host 'FAIL: a missing scratch folder removed something' }
$verdict = if ($failures) { 'FAIL' } else { 'PASS' }
Write-Host "test-result-summary: $verdict run=$run skipped=0"
if ($failures) { exit 1 }
exit 0
