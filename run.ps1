[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($NoRestore) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -Run -NoRestore -Deploy:$false
}
else {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -Run -Deploy:$false
}
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
