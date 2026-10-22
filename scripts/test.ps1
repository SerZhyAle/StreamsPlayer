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
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $root ('temp/scratch/tests-' + [guid]::NewGuid().ToString('N')) }
Push-Location $root
try {
    # A fresh directory prevents stale TRX files from turning a missing suite into evidence.
    if (Test-Path -LiteralPath $ResultsDirectory) { throw 'The results directory must be new.' }
    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    $projects = @(& dotnet sln StreamsPlayer.sln list | Where-Object { $_ -match '\.csproj$' })
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate solution projects.' }
    $expected = @($projects | Where-Object { [System.IO.File]::ReadAllText((Join-Path $root $_)) -match 'Microsoft.NET.Test.Sdk' }).Count
    $arguments = @('test', 'StreamsPlayer.sln', '-c', $Configuration, '--logger', 'trx', '--results-directory', $ResultsDirectory)
    if ($NoBuild) { $arguments += '--no-build' }
    & dotnet @arguments
    $runnerExit = $LASTEXITCODE
    $result = Get-TestResultSummary -Directory $ResultsDirectory -ExpectedFiles $expected -RunnerExitCode $runnerExit
    Write-Host "tests: $($result.Verdict) run=$($result.Run) skipped=$($result.Skipped)"
    exit $result.ExitCode
}
catch {
    Write-Host "tests: $($_.Exception.Message)"
    Write-Host 'tests: CANNOT VERIFY run=0 skipped=0'
    exit 2
}
finally { Pop-Location }
