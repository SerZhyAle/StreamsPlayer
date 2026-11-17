$ErrorActionPreference = 'Stop'

$scenarios = 0
$failures = 0
# SP-0263 (S2-3): a failed scenario is counted, not thrown, so the run reaches its verdict line; an error of the fixture
# itself is COULD NOT VERIFY (exit 2), the closed word set and exit codes of CHECK-VERDICT (0 pass, 1 defect, 2 could not verify).
function Check([bool] $Condition, [string] $Scenario) {
    $script:scenarios++
    if (-not $Condition) { $script:failures++ }
    Write-Host ("expected: $Scenario | actual: " + $(if ($Condition) { 'PASS' } else { 'FAIL' }))
}
# True only when the action throws AND the message says why: any other exception is a broken fixture, not a refusal.
function ThrowsLike([scriptblock] $Action, [string] $Pattern) {
    try { & $Action | Out-Null } catch { return [bool] ($_.Exception.Message -match $Pattern) }
    return $false
}
function Refuses([object[]] $Fixture) {
    return ThrowsLike { Get-ExistingReleaseState -Version $version -Assets $Fixture } 'Release assets are incomplete or invalid'
}
try {
    # SP-0291 (S2-2): the helper is sourced inside the try, so a missing one is a COULD NOT VERIFY with its own
    # verdict line, never a bare exit 1 with an error block. Sourced before $version is set: the helper's own
    # unbound -Version parameter lands in this scope with the source, and it must not clobber the caller's value.
    . (Join-Path $PSScriptRoot 'Get-ExistingReleaseState.ps1')
    $version = '26.1001.0140'
    $assets = @(
        "StreamsPlayer-$version-windows-x64.zip",
        "StreamsPlayer-$version-windows-x64.zip.sha256",
        "StreamsPlayer-$version-windows-x64-setup.exe",
        "StreamsPlayer-$version-windows-x64-setup.exe.sha256"
    ) | ForEach-Object { [pscustomobject]@{ name = $_; size = 100; state = 'uploaded' } }

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
}
catch {
    # SP-0291 (S2-2): a scenario that failed before the error outranks it (CHECK-VERDICT 2.14).
    if ($failures) {
        Write-Host "existing-release: FAIL ($failures of $scenarios scenarios failed; the run stopped early: $($_.Exception.Message))"
        exit 1
    }
    Write-Host "existing-release: COULD NOT VERIFY ($($_.Exception.Message))"
    exit 2
}
if ($failures) {
    Write-Host "existing-release: FAIL ($failures of $scenarios scenarios failed)"
    exit 1
}
Write-Host "existing-release: PASS (run=$scenarios skipped=0)"
exit 0
