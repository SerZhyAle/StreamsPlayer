#requires -Version 7.0
<#
.SYNOPSIS
  SP-0261 Part 2: the release flow's read-only look at the Microsoft Store, run before the tag.

.DESCRIPTION
  Calls the shared preflight of the sza plugin (tools/Store-Preflight.ps1) and nothing else. This script never starts
  msstore: the allow-list that keeps the channel read-only lives in the one shared implementation (WINDOWS-STORE
  section D, rules 21, 23, 25 and 26; their marks stay UNVERIFIED until a first draft has gone through the channel).

  It resolves three inputs itself:
    the Store ID      from msix/identity.json (rule 19); the shared script re-reads the same file and refuses a mismatch
    the package       <Version> of Directory.Build.props through ConvertTo-MsixVersion, the remap the package build uses
    the shared script -PreflightPath, else $env:CLAUDE_PLUGIN_ROOT, else the newest installed sza plugin that carries it

  What it prints is parsed from the shared script's sanitized -Json result, never its raw output: a mode, a refusal
  category, a published or non-published classification and a Free or other price. Not tested here: the package
  signature (rule 23(c); the package is built after the tag and read back by msix/Assert-MsixPackage.ps1),
  Store-managed consumables, a console edit of a submission (23(f)), the credential's expiry unless -CredentialExpiry is
  given, rule 22 (nothing is prepared or published) and rule 24 (no listing path is used), and that a write would succeed.

  One line per run is appended to release-verdicts/<version>.store-preflight.jsonl (rule 26): the Store ID, the package
  version, the shared script's exit code, its mode and the classifications above, and the time. No account identifier,
  credential or upload address can reach it - each field is checked against its own allowed shape before it is copied.
  Commit the file with the verdict files, before the tag.

  Exit codes: 0 the Store step goes on - the checks passed, or the channel is unavailable and the Store step is manual
  (one line says so, rule 21); 1 the release stops before the tag - the channel refuses this run (a pending submission,
  a version that is not greater, a price that is not Free, a product with no live submission, a state it cannot read) or
  the identity file or version is unusable. The channel never fails a release after the tag.
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path $PSScriptRoot -Parent),
    [string] $Version,
    [string] $PreflightPath,
    [string] $CredentialExpiry,
    [string] $RecordDirectory,
    [ValidateRange(5, 600)][int] $TimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$productName = 'Streams Player'   # a display name, never an account identifier
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'msix/MsixIdentity.ps1')
if (-not $RecordDirectory) { $RecordDirectory = Join-Path $Root 'release-verdicts' }

try {
    $identityPath = Join-Path $Root 'msix/identity.json'
    $identity = Read-MsixIdentity -Path $identityPath
    if (-not $Version) {
        $found = Select-String -LiteralPath (Join-Path $Root 'Directory.Build.props') -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if (-not $found) { throw 'Directory.Build.props has no <Version>.' }
        $Version = $found.Matches[0].Groups[1].Value
    }
    $msixVersion = ConvertTo-MsixVersion -Version (ConvertFrom-ReleaseTag -Tag "v$Version")
}
catch {
    Write-Output "Store preflight: STOP - the Store was not asked: $($_.Exception.Message)"
    exit 1
}

function Find-SharedPreflight {
    if ($PreflightPath) { return $PreflightPath }
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:CLAUDE_PLUGIN_ROOT) { $candidates.Add((Join-Path $env:CLAUDE_PLUGIN_ROOT 'tools/Store-Preflight.ps1')) }
    $cache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.claude/plugins/cache/sza-unified-rules/sza'
    if (Test-Path -LiteralPath $cache -PathType Container) {
        $newestFirst = Get-ChildItem -LiteralPath $cache -Directory | Sort-Object -Descending -Property {
            $parsed = $null
            if ([version]::TryParse($_.Name, [ref]$parsed)) { $parsed } else { [version]'0.0' }
        }
        foreach ($directory in $newestFirst) { $candidates.Add((Join-Path $directory.FullName 'tools/Store-Preflight.ps1')) }
    }
    return $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

# One bounded child, so a hung network call cannot hold the release; stderr is drained and never shown.
function Invoke-SharedPreflight([string] $Script) {
    $start = [Diagnostics.ProcessStartInfo]::new([Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)
    $arguments = @('-NoProfile', '-File', $Script, '-ProductName', $productName, '-PackageVersion', $msixVersion,
        '-IdentityFile', $identityPath, '-Json')
    if ($CredentialExpiry) { $arguments += @('-CredentialExpiry', $CredentialExpiry) }
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $null = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        return @{ Exit = $null; Text = '' }
    }
    $process.WaitForExit()
    return @{ Exit = $process.ExitCode; Text = $output.Result }
}

# Only a value of a known shape crosses into the record or the console.
function Get-EvidenceValue([object] $Evidence, [string] $Name, [string] $Pattern) {
    if ($null -eq $Evidence) { return $null }
    $property = $Evidence.PSObject.Properties[$Name]
    if ($null -eq $property -or $property.Value -isnot [string] -or $property.Value -cnotmatch $Pattern) { return $null }
    return $property.Value
}

$preflight = Find-SharedPreflight
$exit = $null
$evidence = $null
$manualReason = $null
if (-not $preflight -or -not (Test-Path -LiteralPath $preflight -PathType Leaf)) {
    $manualReason = 'the shared preflight (tools/Store-Preflight.ps1 of the sza plugin) was not found'
}
else {
    $result = Invoke-SharedPreflight $preflight
    $exit = $result.Exit
    $json = @($result.Text -split '\r?\n' | Where-Object { $_.TrimStart().StartsWith('{') }) | Select-Object -Last 1
    if ($json) {
        try { $evidence = $json | ConvertFrom-Json -ErrorAction Stop } catch { $evidence = $null }
    }
    if ($null -eq $exit) { $manualReason = "the preflight did not answer within $TimeoutSeconds s" }
    elseif ($exit -eq 2) { $manualReason = 'the channel is unavailable (the CLI is missing or unconfigured, the token was refused, or the recorded credential expired)' }
    elseif ($exit -eq 0 -and (Get-EvidenceValue $evidence 'mode' '^ready$') -ne 'ready') { $manualReason = 'the preflight reported success but its evidence could not be read, which is not a pass' }
    elseif ($exit -notin 0, 1) { $manualReason = 'the preflight ended in a way it does not define' }
}

$mode = if ($manualReason) { 'manual' } elseif ($exit -eq 0) { 'ready' } else { 'refused' }
$refusal = Get-EvidenceValue $evidence 'refusal' '^[a-z][a-z-]{0,39}$'
$record = [ordered]@{
    kind = 'read-only-preflight'; storeId = $identity.StoreId; packageVersion = $msixVersion
    exit = $exit; mode = $mode; checkedAt = (Get-Date).ToString('o')
}
if ($refusal) { $record.refusal = $refusal }
$submission = Get-EvidenceValue $evidence 'submission' '^(published|non-published)$'
if ($submission) { $record.submission = $submission }
$pricing = Get-EvidenceValue $evidence 'pricing' '^(Free|other)$'
if ($pricing) { $record.pricing = $pricing }
New-Item -ItemType Directory -Path $RecordDirectory -Force | Out-Null
$recordPath = Join-Path $RecordDirectory "$Version.store-preflight.jsonl"
[IO.File]::AppendAllText($recordPath, ((ConvertTo-Json $record -Compress) + "`n"), [Text.UTF8Encoding]::new($false))

$reasons = @{
    'pending-submission'  = 'a submission is already pending or not Published; clear it in Partner Center first'
    'mixed-edit'          = 'a submission the channel made is pending'
    'version-not-greater' = "package version $msixVersion is not greater than every live package version"
    'pricing'             = 'the Store does not report the price as Free'
    'mandatory-update'    = 'a live package carries a mandatory update, which the machine path does not support'
    'no-live-submission'  = 'the product has no live submission; the first publication stays in Partner Center'
    'identity-file'       = 'the identity file is unusable or disagrees with the given Store ID'
    'credential-expiry'   = 'the recorded credential expiry is not a date'
    'invalid-input'       = 'the product name, Store ID or package version was refused as input'
    'signed-package'      = 'the package is signed; the Store re-signs at certification'
    'invalid-package'     = 'the package cannot be read'
    'state-unreadable'    = 'the Store state could not be read or parsed'
}
$recordLabel = if ($PSBoundParameters.ContainsKey('RecordDirectory')) { $recordPath } else { "release-verdicts/$Version.store-preflight.jsonl" }
if ($manualReason) {
    Write-Output "Store preflight: the Store step is manual - $manualReason. The release goes on as it does without the channel (record: $recordLabel)."
    exit 0
}
if ($exit -eq 0) {
    Write-Output "Store preflight: passed (read-only). Nothing is pending, the price is Free and $msixVersion is greater than every live package."
    Write-Output "Not tested here: the package signature, Store-managed consumables, a console edit of a submission, the credential's expiry unless -CredentialExpiry is given, and that a write would succeed. The upload stays manual. Record: $recordLabel"
    exit 0
}
$reason = if ($refusal -and $reasons.ContainsKey($refusal)) { $reasons[$refusal] } else { 'the preflight refused this run' }
Write-Output "Store preflight: STOP - $reason. Do not tag; the Store step needs the owner (record: $recordLabel)."
exit 1
