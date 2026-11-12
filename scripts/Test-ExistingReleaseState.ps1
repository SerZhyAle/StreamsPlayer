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
$listed = @([pscustomobject]@{ tag_name = 'v1'; draft = $false }, [pscustomobject]@{ tag_name = 'v2'; draft = $true })
Check (Test-DraftReleaseExists -Tag 'v2' -Releases $listed) 'a draft behind a tags-endpoint 404 is found in the release list'
Check (-not (Test-DraftReleaseExists -Tag 'v1' -Releases $listed)) 'a published release is not a draft'
Check (-not (Test-DraftReleaseExists -Tag 'v3' -Releases $listed)) 'an unlisted tag is not a draft'
Check (-not (Test-DraftReleaseExists -Tag 'v2')) 'an empty list holds no draft'
Write-Host "existing-release: PASS ($scenarios scenarios)"
