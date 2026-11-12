#requires -Version 7.0
<#
.SYNOPSIS
    SP-0259: write tools/site/release-manifest.json, the release the site's download buttons link (PAGE-STYLE 3.10).

.DESCRIPTION
    Run by the release flow (scripts/release.ps1, step 2) in the tree that is tagged, after the release notes of the
    version are written and before the site is regenerated. The version must already have its notes in
    msix/listing/release-notes; the file is then read back through the generator's own reader, so a manifest this
    writes is one the generator accepts. Writes nothing else and contacts nothing.

    Exit 0 written (or already current), 1 refused.

.EXAMPLE
    pwsh -NoProfile -File tools/site/Write-ReleaseManifest.ps1 -Version 26.1025.1927
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/ReleaseNotes.ps1"
. "$PSScriptRoot/ReleaseManifest.ps1"

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$path = Join-Path $root 'tools/site/release-manifest.json'

try {
    $notes = @(Get-SiteReleaseNote -Root $root)
    if (-not (@($notes | ForEach-Object { $_.Version }) -ccontains $Version)) {
        throw "$Version has no release notes in msix/listing/release-notes; write them first."
    }
    $text = ConvertTo-SiteReleaseManifestJson -Version $Version
    $existing = if (Test-Path -LiteralPath $path) { [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n") } else { $null }
    if ($existing -ceq $text) {
        Write-Output "release-manifest: current ($Version)"
        exit 0
    }
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
    $null = Get-SiteReleaseManifest -Root $root -ReleaseNotes $notes
    Write-Output "release-manifest: written ($Version)"
    exit 0
}
catch {
    Write-Error "release-manifest: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
