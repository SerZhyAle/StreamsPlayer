[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet restore StreamPlayer.sln
    if ($LASTEXITCODE -ne 0) { throw "Restore failed (exit $LASTEXITCODE)." }
    dotnet build StreamPlayer.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
    dotnet test StreamPlayer.sln -c $Configuration --no-build --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)." }
}
finally { Pop-Location }
