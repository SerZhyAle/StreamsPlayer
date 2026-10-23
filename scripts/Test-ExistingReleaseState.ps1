$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Get-ExistingReleaseState.ps1')
$version = '26.1001.0140'
$assets = @(
    "StreamsPlayer-$version-windows-x64.zip",
    "StreamsPlayer-$version-windows-x64.zip.sha256",
    "StreamsPlayer-$version-windows-x64-setup.exe",
    "StreamsPlayer-$version-windows-x64-setup.exe.sha256"
) | ForEach-Object { [pscustomobject]@{ name = $_; size = 100; state = 'uploaded' } }

$scenarios = 0
function Check([bool] $Condition, [string] $Scenario) {
    if (-not $Condition) { throw "expected: $Scenario | actual: FAIL" }
    $script:scenarios++
    Write-Host "expected: $Scenario | actual: PASS"
}
# True only when the action throws AND the message says why: any other exception is a broken fixture, not a refusal.
function ThrowsLike([scriptblock] $Action, [string] $Pattern) {
    try { & $Action | Out-Null } catch { return [bool] ($_.Exception.Message -match $Pattern) }
    return $false
}
function Refuses([object[]] $Fixture) {
    return ThrowsLike { Get-ExistingReleaseState -Version $version -Assets $Fixture } 'Release assets are incomplete or invalid'
}
Check ((Get-ExistingReleaseState -Version $version) -eq 'empty') 'no assets permits a fresh build'
Check ((Get-ExistingReleaseState -Version $version -Assets $assets) -eq 'complete') 'all published assets skip rebuilding'
Check (Refuses @($assets[0])) 'partial publication is refused without deleting assets'
Check (Refuses @($assets + $assets[0])) 'duplicate publication is refused'
$bad = @($assets | ForEach-Object { [pscustomobject]@{ name = $_.name; size = $_.size; state = $_.state } })
$bad[1].size = 0
Check (Refuses $bad) 'empty checksum is refused'
$bad[1].size = 100
$bad[1].state = 'new'
Check (Refuses $bad) 'unfinished upload is refused'
Check (-not (ThrowsLike { throw 'unrelated failure' } 'Release assets are incomplete or invalid')) 'an unrelated exception is not a refusal'
Write-Host "existing-release: PASS ($scenarios scenarios)"
