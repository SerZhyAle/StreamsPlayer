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
    git add --all
    if ($LASTEXITCODE -ne 0) { throw "Staging failed (exit $LASTEXITCODE)." }
    # SP-0184 (S2-7): the whitespace check judges what is about to be committed. Run before staging it only saw
    # unstaged edits to tracked files and skipped every new file; the staged diff covers both.
    git diff --cached --check
    if ($LASTEXITCODE -ne 0) {
        git reset --quiet
        throw 'Whitespace validation failed. The staging was undone and nothing was committed.'
    }
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) { throw 'Nothing to commit.' }
    git commit -m $Message
    if ($LASTEXITCODE -ne 0) { throw "Commit failed (exit $LASTEXITCODE)." }
    Write-Host 'Local build and commit completed. Nothing was pushed or published.' -ForegroundColor Green
}
finally { Pop-Location }
