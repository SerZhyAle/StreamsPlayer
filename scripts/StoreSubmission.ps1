#requires -Version 7.0
<#
.SYNOPSIS
  Read what `msstore submission get <StoreId>` printed - read only, dot-sourced by scripts/New-ReleaseDossier.ps1.

.DESCRIPTION
  ConvertFrom-MsstoreSubmissionText turns the command's output into the submission object. The command prints a few
  status lines, then JSON wrapped at the console width with raw line breaks inside string values; the line breaks are
  removed before parsing (STORE_PUBLISHING.md, Phase 7). With a submission pending it is that submission; with none it
  is the last published one, and Pending says which.

  Get-StoreWhatsNewState compares the submission with a released version: whether it carries that version's package
  (MSIX form, each component int-cast plus ".0") and which listings' What's new does not name the version in its first
  line. Partner Center copies What's new forward from the previous submission in every language, so a text nobody
  replaced still names an older version - the submission of 26.1117.1838 went out with the text of 26.1010.0106.
#>

# No Set-StrictMode here: this file is dot-sourced.

function ConvertFrom-MsstoreSubmissionText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    $start = $Text.IndexOf('{')
    $submission = $null
    if ($start -ge 0) {
        $json = $Text.Substring($start) -replace "`r?`n", ''
        try { $submission = $json | ConvertFrom-Json -ErrorAction Stop } catch { $submission = $null }
    }
    [pscustomobject]@{
        Pending    = $Text -match 'Found Pending Submission'
        Submission = $submission
    }
}

function Get-StoreWhatsNewState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Submission,
        [Parameter(Mandatory)][ValidatePattern('^\d{2}\.\d{4}\.\d{4}$')][string] $Version
    )

    $msixVersion = (($Version.Split('.') | ForEach-Object { [int] $_ }) -join '.') + '.0'
    $packages = @($Submission.ApplicationPackages | Where-Object { $_ } | ForEach-Object { [string] $_.Version })
    $stale = [System.Collections.Generic.List[object]]::new()
    $languages = 0
    if ($Submission.Listings) {
        foreach ($listing in $Submission.Listings.PSObject.Properties) {
            $languages++
            $notes = [string] $listing.Value.BaseListing.ReleaseNotes
            $head = @($notes -split "`r?`n" | Where-Object { $_.Trim() })[0]
            $head = if ($head) { $head.Trim() } else { '' }
            if ($head -notmatch "(^|\D)$([regex]::Escape($Version))(\D|$)") {
                $stale.Add([pscustomobject]@{ Language = $listing.Name; Head = $head })
            }
        }
    }
    [pscustomobject]@{
        MsixVersion     = $msixVersion
        PackageVersions = $packages
        HasPackage      = $packages -contains $msixVersion
        Languages       = $languages
        Stale           = $stale.ToArray()
    }
}
