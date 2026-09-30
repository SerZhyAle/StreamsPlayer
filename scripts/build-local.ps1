[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Message
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    # check.ps1 reports its verdict through the exit code (0 PASS, 1 FAIL, 2 CANNOT VERIFY) rather than by
    # throwing, so it has to be read here, before the next native command overwrites $LASTEXITCODE (SP-0157).
    & .\scripts\check.ps1
    $checkExit = $LASTEXITCODE
    if ($checkExit -ne 0) {
        $verdict = switch ($checkExit) { 1 { 'FAIL' } 2 { 'CANNOT VERIFY' } default { 'UNKNOWN' } }
        throw "Release-parity check did not pass: $verdict (exit $checkExit). Nothing was staged or committed."
    }
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace validation failed. Nothing was staged or committed.' }
    git add --all
    if ($LASTEXITCODE -ne 0) { throw "Staging failed (exit $LASTEXITCODE)." }
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) { throw 'Nothing to commit.' }
    git commit -m $Message
    if ($LASTEXITCODE -ne 0) { throw "Commit failed (exit $LASTEXITCODE)." }
    Write-Host 'Local build and commit completed. Nothing was pushed or published.' -ForegroundColor Green
}
finally { Pop-Location }
