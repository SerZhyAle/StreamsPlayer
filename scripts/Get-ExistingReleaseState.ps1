<#
    SP-0184 S1-3: a complete published release is a no-op before any rebuild. ZIP timestamps and
    installer metadata are not reproducible; rebuilding them cannot prove the published bytes wrong.
    An incomplete release with any product asset fails closed and requires explicit recovery.
    Dot-source to test the decision without GitHub; direct execution only reads remote state.
#>
[CmdletBinding()]
param([string] $Version, [string] $Tag, [string] $Repository)

function Get-ExistingReleaseState {
    param([Parameter(Mandatory)] [string] $Version, [AllowEmptyCollection()] [object[]] $Assets = @())

    $names = @(
        "StreamsPlayer-$Version-windows-x64.zip",
        "StreamsPlayer-$Version-windows-x64.zip.sha256",
        "StreamsPlayer-$Version-windows-x64-setup.exe",
        "StreamsPlayer-$Version-windows-x64-setup.exe.sha256"
    )
    $present = @($Assets | Where-Object { $_.name -cin $names })
    if ($present.Count -eq 0) { return 'empty' }
    foreach ($name in $names) {
        $matching = @($present | Where-Object { $_.name -ceq $name })
        if ($matching.Count -ne 1 -or $matching[0].size -le 0 -or $matching[0].state -ne 'uploaded') {
            throw "Release assets are incomplete or invalid ($name). Published assets are immutable; recover the original asset set explicitly or cut a new version."
        }
    }
    return 'complete'
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    if (-not $Version -or -not $Tag -or -not $Repository) { throw 'Version, Tag and Repository are required.' }
    $out = gh api "repos/$Repository/releases/tags/$Tag" 2>&1
    if ($LASTEXITCODE -ne 0) {
        if (($out | Out-String) -notmatch 'HTTP 404') { throw "Release lookup failed: $($out | Out-String)" }
        $state = 'empty'
    }
    else {
        $release = $out | ConvertFrom-Json
        if ($release.draft) { throw 'An existing draft requires explicit recovery before publication.' }
        $state = Get-ExistingReleaseState -Version $Version -Assets @($release.assets)
    }
    "complete=$($state -eq 'complete')" | Out-File $env:GITHUB_OUTPUT -Append
    Write-Host "Existing release: $state."
}
