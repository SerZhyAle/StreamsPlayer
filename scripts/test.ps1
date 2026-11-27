[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $NoBuild,
    [string] $ResultsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/TestResultSummary.ps1"
$defaultResults = -not $ResultsDirectory
if ($defaultResults) { $ResultsDirectory = Join-Path $root ('temp/scratch/tests-' + [guid]::NewGuid().ToString('N')) }
Push-Location $root
try {
    # A fresh directory prevents stale TRX files from turning a missing suite into evidence.
    if (Test-Path -LiteralPath $ResultsDirectory) { throw 'The results directory must be new.' }
    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    # SP-0246 S3-1: each default run leaves a tests-<guid> folder. Only the default location is pruned - a
    # caller-chosen -ResultsDirectory is the caller's to clean.
    if ($defaultResults) { Remove-StaleTestResults -Scratch (Join-Path $root 'temp/scratch') -Current $ResultsDirectory | Out-Null }
    $projects = @(& dotnet sln StreamsPlayer.sln list | Where-Object { $_ -match '\.csproj$' })
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate solution projects.' }
    $testProjects = @($projects | Where-Object { [System.IO.File]::ReadAllText((Join-Path $root $_)) -match 'Microsoft.NET.Test.Sdk' })
    $expected = $testProjects.Count
    # One project at a time, StreamsPlayer.Core.Tests first. A solution-level `dotnet test` starts every test project at
    # once, and on a two-core runner that left the loopback and pipe tests of Core.Tests (their deadlines run from one to
    # thirty seconds) competing with the slow Exchange suite for the same two cores: three release runs of one commit failed
    # on three different timing tests. The verdict below still reads one TRX per project, so the contract is unchanged.
    $ordered = @($testProjects | Where-Object { $_ -match 'StreamsPlayer\.Core\.Tests' }) +
        @($testProjects | Where-Object { $_ -notmatch 'StreamsPlayer\.Core\.Tests' })
    $runnerExit = 0
    foreach ($project in $ordered) {
        $arguments = @('test', $project, '-c', $Configuration, '--logger', 'trx', '--results-directory', $ResultsDirectory)
        if ($NoBuild) { $arguments += '--no-build' }
        & dotnet @arguments
        # Every project runs even after one fails, as the solution run did: the verdict counts all of them.
        if ($LASTEXITCODE -ne 0 -and $runnerExit -eq 0) { $runnerExit = $LASTEXITCODE }
    }
    $result = Get-TestResultSummary -Directory $ResultsDirectory -ExpectedFiles $expected -RunnerExitCode $runnerExit
    # BUILD-EVIDENCE rule 1: the line names the configuration it judged, so a Debug run is never quoted as evidence about Release.
    Write-Host "tests: $($result.Verdict) ($Configuration, run=$($result.Run) skipped=$($result.Skipped))"
    exit $result.ExitCode
}
catch {
    Write-Host "tests: $($_.Exception.Message)"
    Write-Host "tests: COULD NOT VERIFY ($Configuration, run=0 skipped=0)"
    exit 2
}
finally { Pop-Location }
