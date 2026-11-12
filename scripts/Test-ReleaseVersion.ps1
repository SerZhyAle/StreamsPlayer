#requires -Version 7.0
<#
.SYNOPSIS
    SP-0247 (PACKAGE-VERSIONING section 4, rung 2a): fixed-instant tests for the tag derivation, the MSIX remap and
    the winget renderer.

.DESCRIPTION
    Every input is a fixed instant; nothing reads the clock.

      tag       ConvertFrom-ReleaseTag renders the registered `dotted` tag of pinned instants (the contract's own
                2609262322 among them); refuses an impossible stamp (month 0 or 13, day 0, February 30, February 29 of
                a common year, April 31, hour 24, minute 60) and a malformed tag (no `v`, a capital `V`, unpadded or
                over-long fields, a fourth field, surrounding blanks, other separators, the empty string)
      msix      ConvertTo-MsixVersion drops the leading zeros (26.0719.0131 -> 26.719.131.0) and holds the 65535
                ceiling at its boundary
      order     P2: for consecutive releases across a minute, hour, day, month and year boundary, the canonical
                version (as text and as a number) and the MSIX remap both order the later release above the earlier
      winget    winget/New-WingetManifests.ps1 writes the five manifests with the tag's version and no REPLACE_VERSION,
                as UTF-8 with a BOM and LF endings; names the tokens left open; refuses an impossible tag, a non-empty
                output folder and release notes holding ': '

    Exit 0 when every check passes, 1 otherwise, 2 when the run could not be completed. The last line is
    `release-version-tests: PASS|FAIL|COULD NOT VERIFY (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$renderer = Join-Path $PSScriptRoot '../winget/New-WingetManifests.ps1'

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name, [string] $Actual = '') {
    $shown = if ($Actual) { $Actual } elseif ($Condition) { 'PASS' } else { 'FAIL' }
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
    Write-Output "expected: $Name | actual: $shown"
}
# True only when the action throws AND the message says why: any other exception is a broken fixture, not a refusal.
function ThrowsLike([scriptblock] $Action, [string] $Pattern) {
    try { & $Action | Out-Null } catch { return [bool] ($_.Exception.Message -match $Pattern) }
    return $false
}
function Invoke-Renderer([string[]] $Arguments) {
    $out = & pwsh -NoProfile -File $script:renderer @Arguments 2>&1 | ForEach-Object { "$_" }
    # A refusal is rendered with colour codes and wrapped across lines that start with a pipe; all three are flattened
    # so a phrase cannot be cut in half by the console width.
    $flat = ((($out -join ' ') -replace "`e\[[0-9;]*[A-Za-z]", ' ') -replace '\s*\|\s*', ' ') -replace '\s+', ' '
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $flat }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0247-version-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    # --- tag: the registered rendering at pinned instants ---------------------------------------------------------
    foreach ($case in @(
            @('v26.0926.2322', '26.0926.2322', 'the contract instant 2609262322'),
            @('v26.0719.0131', '26.0719.0131', 'leading zeros kept in the canonical version'),
            @('v26.1025.1927', '26.1025.1927', 'a published release'),
            @('v26.0101.0000', '26.0101.0000', 'the first minute of a year'),
            @('v26.1231.2359', '26.1231.2359', 'the last minute of a year'),
            @('v28.0229.1200', '28.0229.1200', 'February 29 of a leap year'))) {
        $actual = ConvertFrom-ReleaseTag -Tag $case[0]
        Check ($actual -ceq $case[1]) "$($case[0]) -> $($case[1]) ($($case[2]))" $actual
    }

    $impossible = 'DateTime'
    foreach ($case in @(
            @('v26.1301.0000', 'month 13'),
            @('v26.0001.0000', 'month 0'),
            @('v26.0100.0000', 'day 0'),
            @('v26.0230.1200', 'February 30'),
            @('v26.0229.1200', 'February 29 of a common year'),
            @('v26.0431.1200', 'April 31'),
            @('v26.0719.2400', 'hour 24'),
            @('v26.0719.0160', 'minute 60'))) {
        $tag = $case[0]
        Check (ThrowsLike { ConvertFrom-ReleaseTag -Tag $tag } $impossible) "$tag is refused as an impossible stamp ($($case[1]))"
    }

    $malformed = 'Tag must use vYY\.MMDD\.HHmm\.'
    foreach ($case in @(
            @('26.0719.0131', 'no v prefix'),
            @('V26.0719.0131', 'a capital V'),
            @('v26.719.131', 'unpadded month-day and hour-minute'),
            @('v2026.0719.0131', 'a four-digit year'),
            @('v26.0719.0131.0', 'a fourth field'),
            @('v26-0719-0131', 'hyphens for dots'),
            @(' v26.0719.0131', 'a leading blank'),
            @('v26.0719.0131 ', 'a trailing blank'),
            @('', 'the empty string'))) {
        $tag = $case[0]
        Check (ThrowsLike { ConvertFrom-ReleaseTag -Tag $tag } $malformed) "'$tag' is refused as a malformed tag ($($case[1]))"
    }
    # `$` in .NET also matches before a final newline, so the shape check alone would let this through; ParseExact
    # refuses it, and either refusal is a refusal.
    Check (ThrowsLike { ConvertFrom-ReleaseTag -Tag "v26.0719.0131`n" } "$malformed|$impossible") 'a tag with a trailing newline is refused'
    # \d is a Unicode digit class: Arabic-Indic digits pass the shape check and must still be refused by ParseExact.
    $indic = 'v' + [string][char]0x0662 + [string][char]0x0666 + '.0719.0131'
    Check (ThrowsLike { ConvertFrom-ReleaseTag -Tag $indic } "$malformed|$impossible") 'a tag with non-ASCII digits is refused'

    # --- msix: the remap and its ceiling -----------------------------------------------------------------------------
    foreach ($case in @(
            @('26.0926.2322', '26.926.2322.0'),
            @('26.0719.0131', '26.719.131.0'),
            @('26.1025.1927', '26.1025.1927.0'),
            @('26.0101.0000', '26.101.0.0'),
            @('26.1231.2359', '26.1231.2359.0'),
            @('1.65535.0', '1.65535.0.0'),
            @('65535.65535.65535', '65535.65535.65535.0'))) {
        $actual = ConvertTo-MsixVersion -Version $case[0]
        Check ($actual -ceq $case[1]) "MSIX remap of $($case[0]) is $($case[1])" $actual
    }
    Check (ThrowsLike { ConvertTo-MsixVersion -Version '1.65536.0' } "Version part '65536' exceeds 65535\.") 'a part of 65536 is refused at the ceiling'
    Check (ThrowsLike { ConvertTo-MsixVersion -Version '65536.0.0' } "Version part '65536' exceeds 65535\.") 'a first part of 65536 is refused at the ceiling'

    # --- order: P2 across every field boundary ---------------------------------------------------------------------
    foreach ($pair in @(
            @('26.0719.0131', '26.0719.0132', 'a minute'),
            @('26.0719.0159', '26.0719.0200', 'an hour'),
            @('26.0719.2359', '26.0720.0000', 'a day'),
            @('26.0731.2359', '26.0801.0000', 'a month'),
            @('26.0930.2359', '26.1001.0000', 'a one-digit to two-digit month'),
            @('26.1231.2359', '27.0101.0000', 'a year'))) {
        $earlier, $later, $boundary = $pair
        Check ([string]::CompareOrdinal($earlier, $later) -lt 0) "$earlier orders below $later as text (across $boundary)"
        Check ([version] $earlier -lt [version] $later) "$earlier orders below $later as a number (across $boundary)"
        $msixEarlier = ConvertTo-MsixVersion -Version $earlier
        $msixLater = ConvertTo-MsixVersion -Version $later
        Check ([version] $msixEarlier -lt [version] $msixLater) "MSIX $msixEarlier orders below $msixLater (across $boundary)"
    }

    # --- winget: the renderer ------------------------------------------------------------------------------------------
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $full = Join-Path $work 'full'
    $sha = 'ab' * 32
    $r = Invoke-Renderer @('-Tag', 'v26.0719.0131', '-OutputDirectory', $full, '-InstallerSha256', $sha, '-ReleaseDate', '2026-07-19',
        '-ReleaseNotes', 'Fixture notes - en', '-ReleaseNotesRu', 'Fixture notes - ru', '-ReleaseNotesUk', 'Fixture notes - uk')
    Check ($r.Exit -eq 0) 'the renderer writes a complete set' "exit $($r.Exit)"
    $files = @(Get-ChildItem -LiteralPath $full -File -ErrorAction SilentlyContinue)
    Check ($files.Count -eq 5) 'five manifests are written' "$($files.Count)"
    foreach ($file in $files) {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $text = [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3)
        Check ($bytes.Length -gt 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) "$($file.Name) starts with a UTF-8 BOM"
        Check (-not $text.Contains("`r")) "$($file.Name) has LF endings only"
        Check ($text -cmatch '(?m)^PackageVersion: 26\.0719\.0131$') "$($file.Name) declares PackageVersion 26.0719.0131"
        Check (-not $text.Contains('REPLACE_')) "$($file.Name) has no REPLACE_ token left"
    }
    $installer = Join-Path $full 'SerZhyAle.StreamsPlayer.installer.yaml'
    if (Test-Path -LiteralPath $installer) {
        $installerText = [System.IO.File]::ReadAllText($installer)
        Check ($installerText.Contains('/download/v26.0719.0131/StreamsPlayer-26.0719.0131-windows-x64-setup.exe')) 'the installer URL carries the tag and the version'
        Check ($installerText.Contains("InstallerSha256: $($sha.ToUpperInvariant())")) 'the installer hash is written upper-case'
    }
    else { Check $false 'the installer manifest exists' }

    $partial = Join-Path $work 'partial'
    $r = Invoke-Renderer @('-Tag', 'v26.0719.0131', '-OutputDirectory', $partial)
    Check ($r.Exit -eq 0 -and $r.Text -match 'Still to fill by hand: REPLACE_RELEASE_DATE, REPLACE_RELEASE_NOTES, REPLACE_RELEASE_NOTES_RU, REPLACE_RELEASE_NOTES_UK, REPLACE_SHA256\.') 'a set without the optional values names every token left open' $r.Text
    $versionFile = Join-Path $partial 'SerZhyAle.StreamsPlayer.yaml'
    Check ((Test-Path -LiteralPath $versionFile) -and -not ([System.IO.File]::ReadAllText($versionFile)).Contains('REPLACE_VERSION')) 'the version is filled even when nothing else is'

    $r = Invoke-Renderer @('-Tag', 'v26.0230.1200', '-OutputDirectory', (Join-Path $work 'impossible'))
    Check ($r.Exit -ne 0 -and $r.Text -match 'DateTime' -and -not (Test-Path -LiteralPath (Join-Path $work 'impossible'))) 'an impossible tag writes nothing' $r.Text
    $r = Invoke-Renderer @('-Tag', 'v26.0719.0131', '-OutputDirectory', $full)
    Check ($r.Exit -ne 0 -and $r.Text -match 'is not empty') 'a non-empty output folder is refused' $r.Text
    $r = Invoke-Renderer @('-Tag', 'v26.0719.0131', '-OutputDirectory', (Join-Path $work 'colon'), '-ReleaseNotes', 'Fixed: playback')
    Check ($r.Exit -ne 0 -and $r.Text -match "contains ': '") "release notes holding ': ' are refused" $r.Text
}
catch {
    # A setup step or a subject threw instead of answering: nothing was judged past this point, so the run could not
    # verify (CHECK-VERDICT 2.1, 2.5, 2.11). A check that already failed still outranks it (2.14).
    $reason = $_.Exception.Message -replace '\s+', ' '
    if ($failures.Count -eq 0) { Write-Output "release-version-tests: COULD NOT VERIFY ($reason)"; exit 2 }
    Write-Output "release-version-tests: the run stopped early: $reason"
}
finally { if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } }

if ($failures.Count -eq 0) { Write-Output "release-version-tests: PASS ($passed checks)"; exit 0 }
Write-Output "release-version-tests: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
