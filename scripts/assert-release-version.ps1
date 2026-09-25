<#
.SYNOPSIS
    Fails unless a release version is greater than every version already published.

.DESCRIPTION
    A release stamp (YY.MMDD.HHmm) must exceed every published one on every channel (SP-0122). The published
    set is every vYY.MMDD.HHmm tag in the repository, plus the stamps that shipped on a channel without a
    GitHub tag ($OffTagVersions). The tag being released is excluded from the comparison, so a re-run of the
    newest release still passes. Exit code 0 = pass; any failure throws, which exits non-zero.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/assert-release-version.ps1 -Version 26.0925.1200
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    # Published versions to compare against instead of reading the repository's tags (tests, dry runs).
    [string[]] $PublishedVersions
)

$ErrorActionPreference = 'Stop'

# Stamps shipped on a channel that never carried a matching GitHub tag. The Microsoft Store shipped
# 26.0806.2225, which GitHub and winget never did (scripts/release.ps1, step 4).
$OffTagVersions = @('26.0806.2225')

$pattern = '^\d{2}\.\d{4}\.\d{4}$'
if ($Version -notmatch $pattern) { throw "Version '$Version' must use YY.MMDD.HHmm." }
[DateTime]::ParseExact($Version, 'yy.MMdd.HHmm', [Globalization.CultureInfo]::InvariantCulture) | Out-Null

if (-not $PSBoundParameters.ContainsKey('PublishedVersions')) {
    $root = Split-Path $PSScriptRoot -Parent
    $tags = git -C $root tag --list 'v*'
    if ($LASTEXITCODE -ne 0) { throw "git tag --list failed (exit $LASTEXITCODE)." }
    $PublishedVersions = @($tags | Where-Object { $_ -match '^v\d{2}\.\d{4}\.\d{4}$' } | ForEach-Object { $_.Substring(1) })
    if ($PublishedVersions.Count -eq 0) { throw 'No vYY.MMDD.HHmm tags found; fetch tags before running this check.' }
}

$others = @(@($PublishedVersions) + $OffTagVersions |
    Where-Object { $_ -match $pattern -and $_ -ne $Version } |
    Sort-Object { [version]$_ } -Unique)
$newest = $others | Select-Object -Last 1

if ($newest -and [version]$Version -le [version]$newest) {
    throw "Release version $Version is not greater than published version $newest."
}
Write-Host "PASS: $Version is greater than every published version (newest: $(if ($newest) { $newest } else { 'none' }))."
