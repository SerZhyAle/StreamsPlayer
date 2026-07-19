[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($NoRestore) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -Run -NoRestore
}
else {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -Run
}
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
ранный 