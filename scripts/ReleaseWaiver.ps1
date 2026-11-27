<#
.SYNOPSIS
  The owner's recorded decision to ship one version without named release gates. Functions only.

.DESCRIPTION
  A waiver is not a verdict: it says that a gate did NOT run, who decided that, and when. It travels as
  release-verdicts/<version>.waiver.json inside the tagged tree, binds exactly one version, and names every gate it
  covers, so it can never carry over to the next release. A gate consults it only when the gate's own evidence is
  absent: a recorded red result is never covered. Dot-sourced by scripts/Assert-SmokeVerdict.ps1,
  scripts/Assert-AuditVerdict.ps1 and .github/workflows/release.yml.
#>

function Get-ReleaseWaiver {
    <# The waiver of this version when it names this gate, otherwise $null. A malformed waiver throws. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [ValidateSet('smoke-playback', 'code-audit', 'test-suite')] [string] $Gate
    )

    $path = Join-Path $Root "release-verdicts/$Version.waiver.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { $waiver = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Release waiver $path is not valid JSON: $($_.Exception.Message)" }

    foreach ($field in 'record', 'version', 'decidedBy', 'decidedOn', 'decision', 'waived') {
        if ($waiver.PSObject.Properties.Name -notcontains $field -or -not $waiver.$field) { throw "Release waiver $path lacks '$field'." }
    }
    if ($waiver.record -cne 'release-waiver') { throw "Release waiver $path is a '$($waiver.record)' record, not release-waiver." }
    if ($waiver.version -cne $Version) { throw "Release waiver $path names version '$($waiver.version)', not $Version." }
    $unknown = @($waiver.waived | Where-Object { $_ -cnotin 'smoke-playback', 'code-audit', 'test-suite' })
    if ($unknown.Count) { throw "Release waiver $path names unknown gate(s): $($unknown -join ', ')." }

    if (@($waiver.waived) -cnotcontains $Gate) { return $null }
    return $waiver
}

function Write-ReleaseWaiverNotice {
    <# Says loudly that a gate did not run; on a GitHub runner it is a warning annotation of the run. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Waiver,
        [Parameter(Mandatory)] [string] $Gate
    )

    $date = if ($Waiver.decidedOn -is [datetime]) { $Waiver.decidedOn.ToString('yyyy-MM-dd') } else { [string]$Waiver.decidedOn }
    $message = "Gate $Gate did NOT run for $($Waiver.version) - waived by $($Waiver.decidedBy) on $date (release-verdicts/$($Waiver.version).waiver.json)."
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::warning::$message" }
    else { Write-Warning $message }
}
