Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestResultSummary.ps1"
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('temp/SP-0232/trx-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$path = Join-Path $fixture 'test.trx'
$run = 0
$failures = 0
function Check([int] $Total, [int] $Executed, [int] $Passed, [int] $Failed, [int] $Runner, [int] $Expected, [string] $Outcome = 'Completed') {
    $text = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="{0}"><Counters total="{1}" executed="{2}" passed="{3}" failed="{4}"/></ResultSummary></TestRun>' -f $Outcome, $Total, $Executed, $Passed, $Failed
    [System.IO.File]::WriteAllText($script:path, $text)
    $actual = Get-TestResultSummary -Directory $script:fixture -ExpectedFiles 1 -RunnerExitCode $Runner
    $script:run++
    if ($actual.ExitCode -ne $Expected -or $actual.Run -ne $Executed -or $actual.Skipped -ne ($Total - $Executed)) {
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
    if ($actual.ExitCode -ne 2) { $failures++ }
}
Remove-Item -LiteralPath $path
$actual = Get-TestResultSummary -Directory $fixture -ExpectedFiles 1 -RunnerExitCode 0
$run++
if ($actual.ExitCode -ne 2) { $failures++ }
$large = '<TestRun><ResultSummary outcome="Completed"><Counters total="9223372036854775807" executed="9223372036854775807" passed="9223372036854775807" failed="0"/></ResultSummary></TestRun>'
[System.IO.File]::WriteAllText($path, $large)
[System.IO.File]::WriteAllText((Join-Path $fixture 'overflow.trx'), $large)
$actual = Get-TestResultSummary -Directory $fixture -ExpectedFiles 2 -RunnerExitCode 0
$run++
if ($actual.ExitCode -ne 2) { $failures++ }
$verdict = if ($failures) { 'FAIL' } else { 'PASS' }
Write-Host "test-result-summary: $verdict run=$run skipped=0"
if ($failures) { exit 1 }
exit 0
