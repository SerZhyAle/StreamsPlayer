#requires -Version 7.0
<#
.SYNOPSIS
  SP-0247 (PACKAGE-VERSIONING P3): render the five winget templates for one release tag.

.DESCRIPTION
  The PackageVersion and the version inside the release URLs are derived from the tag by ConvertFrom-ReleaseTag -
  the derivation release.yml uses - so no version is typed by hand. The installer hash, the release date and the three
  locale release notes are parameters; a value left out keeps its REPLACE_ token, the script names every token still
  open, and winget/README.md says how to fill it. The files are written as winget-pkgs stores them: UTF-8 with a BOM
  and LF line endings.

  This script writes only into -OutputDirectory, which must be new or empty. It submits nothing and never touches
  manifests/.

.EXAMPLE
  pwsh -NoProfile -File ./winget/New-WingetManifests.ps1 -Tag v26.1025.1927 -OutputDirectory temp/winget/26.1025.1927 -InstallerSha256 <hash from the .sha256 sidecar> -ReleaseDate 2026-10-25
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $InstallerSha256,
    [string] $ReleaseDate,
    [string] $ReleaseNotes,
    [string] $ReleaseNotesRu,
    [string] $ReleaseNotesUk
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '../scripts/ReleaseVersion.ps1')
$version = ConvertFrom-ReleaseTag -Tag $Tag

if ($InstallerSha256 -and $InstallerSha256 -cnotmatch '^[0-9A-Fa-f]{64}$') { throw "-InstallerSha256 is not a SHA256 hex digest: '$InstallerSha256'." }
if ($ReleaseDate) {
    $parsedDate = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($ReleaseDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, 'None', [ref] $parsedDate)) {
        throw "-ReleaseDate must be an ISO date YYYY-MM-DD: '$ReleaseDate'."
    }
}
# Every token sits in a plain YAML scalar: a colon followed by a space makes the scanner read a nested mapping, and
# winget validate fails with "mapping values are not allowed in this context" (winget/README.md, 2026-07-27).
foreach ($notes in @{ ReleaseNotes = $ReleaseNotes; ReleaseNotesRu = $ReleaseNotesRu; ReleaseNotesUk = $ReleaseNotesUk }.GetEnumerator()) {
    if ($notes.Value -and $notes.Value.Contains(': ')) { throw "-$($notes.Key) contains ': ', which breaks the plain YAML scalar it lands in; use a spaced hyphen instead." }
    if ($notes.Value -and $notes.Value -match '[\r\n]') { throw "-$($notes.Key) spans several lines; the template holds it as a one-line scalar." }
}

$values = @{
    REPLACE_VERSION          = $version
    REPLACE_SHA256           = $(if ($InstallerSha256) { $InstallerSha256.ToUpperInvariant() } else { $null })
    REPLACE_RELEASE_DATE     = $ReleaseDate
    REPLACE_RELEASE_NOTES    = $ReleaseNotes
    REPLACE_RELEASE_NOTES_RU = $ReleaseNotesRu
    REPLACE_RELEASE_NOTES_UK = $ReleaseNotesUk
}

$templates = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'templates') -File -Filter '*.yaml' | Sort-Object Name)
if ($templates.Count -ne 5) { throw "Expected the five templates in winget/templates, found $($templates.Count)." }
if (Test-Path -LiteralPath $OutputDirectory) {
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) { throw "-OutputDirectory '$OutputDirectory' is not empty; render into a new folder." }
}
else { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }

$token = [regex]::new('REPLACE_[A-Z0-9_]+')
$open = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
$encoding = [System.Text.UTF8Encoding]::new($true)
foreach ($template in $templates) {
    $text = [System.IO.File]::ReadAllText($template.FullName).Replace("`r`n", "`n")
    $rendered = $token.Replace($text, {
            param($match)
            if (-not $values.ContainsKey($match.Value)) { throw "$($template.Name) holds the token $($match.Value), which this script does not know." }
            $value = $values[$match.Value]
            if ($value) { return $value }
            $null = $open.Add($match.Value)
            return $match.Value
        })
    $target = Join-Path $OutputDirectory $template.Name
    [System.IO.File]::WriteAllText($target, $rendered, $encoding)
    $declared = @([regex]::Matches($rendered, '(?m)^PackageVersion: (.+)$') | ForEach-Object { $_.Groups[1].Value })
    if ($declared.Count -ne 1 -or $declared[0] -cne $version) { throw "$($template.Name) rendered PackageVersion '$($declared -join ', ')', not '$version'." }
}

Write-Host "winget manifests for $version written to $OutputDirectory ($($templates.Count) files)."
if ($open.Count) { Write-Host "Still to fill by hand: $(@($open) -join ', ')." }
