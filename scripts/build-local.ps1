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
    & .\scripts\check.ps1
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace validation failed.' }
    git add --all
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) { throw 'Nothing to commit.' }
    git commit -m $Message
    if ($LASTEXITCODE -ne 0) { throw "Commit failed (exit $LASTEXITCODE)." }
    Write-Host 'Local build and commit completed. Nothing was pushed or published.' -ForegroundColor Green
}
finally { Pop-Location }
