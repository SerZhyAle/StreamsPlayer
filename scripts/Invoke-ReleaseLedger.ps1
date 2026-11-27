#requires -Version 7.0
<#
.SYNOPSIS
    Repository wrapper around the sza plugin's release-ledger writer (RELEASE-LEDGER 0.1).

.DESCRIPTION
    Resolves tools/harness/releases/release-ledger.ps1 from -LedgerScriptPath, $env:CLAUDE_PLUGIN_ROOT, or the newest
    installed sza plugin under ~/.claude/plugins/cache/sza-unified-rules/sza/, then forwards the requested verb and
    arguments with -RepoRoot pointing at this repository.

    Build scripts pass -BestEffort so a missing plugin (CI, outside contributor) or a ledger error warns and exits 0
    without failing the build (RELEASE_AND_DISTRIBUTION section 10).

    -CheckRender verifies that docs/releases/RELEASES.md matches what the writer renders from channels.json,
    builds.json, and history.jsonl (CHECK-VERDICT: 0 PASS, 1 FAIL, 2 COULD NOT VERIFY).
#>
[CmdletBinding()]
param(
    [ValidateSet('init', 'build', 'publish', 'confirm', 'show', 'history', 'render', 'probe', 'import')]
    [string] $Verb,
    [string] $Root = (Split-Path $PSScriptRoot -Parent),
    [string] $LedgerScriptPath,
    [string] $Package,
    [string] $Flavor,
    [string] $Kind,
    [string] $Version,
    [string] $Artifact,
    [string] $Key,
    [string] $State,
    [string] $Ref,
    [string] $Url,
    [string] $Note,
    [string] $Track,
    [string] $Rollout,
    [string] $Source,
    [string] $At,
    [int] $Last = 0,
    [switch] $Json,
    [switch] $DryRun,
    [switch] $BestEffort,
    [switch] $CheckRender
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-ReleaseLedgerScript {
    if ($LedgerScriptPath) { return $LedgerScriptPath }
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:CLAUDE_PLUGIN_ROOT) {
        $candidates.Add((Join-Path $env:CLAUDE_PLUGIN_ROOT 'tools/harness/releases/release-ledger.ps1'))
    }
    $cache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.claude/plugins/cache/sza-unified-rules/sza'
    if (Test-Path -LiteralPath $cache -PathType Container) {
        $newestFirst = Get-ChildItem -LiteralPath $cache -Directory | Sort-Object -Descending -Property {
            $parsed = $null
            if ([version]::TryParse($_.Name, [ref]$parsed)) { $parsed } else { [version]'0.0' }
        }
        foreach ($directory in $newestFirst) {
            $candidates.Add((Join-Path $directory.FullName 'tools/harness/releases/release-ledger.ps1'))
        }
    }
    return $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

function Resolve-LedgerDirectory([string] $RepoRoot) {
    $relative = 'docs/releases'
    $profilePath = Join-Path $RepoRoot '.sza-profile.json'
    if (Test-Path -LiteralPath $profilePath -PathType Leaf) {
        try {
            $profile = Get-Content -LiteralPath $profilePath -Raw -Encoding utf8 | ConvertFrom-Json
            if ($profile.PSObject.Properties['paths'] -and $profile.paths.PSObject.Properties['releasesDir']) {
                $configured = [string]$profile.paths.releasesDir
                if (-not [string]::IsNullOrWhiteSpace($configured)) { $relative = $configured }
            }
        }
        catch {
            Write-Verbose "release-ledger: could not parse .sza-profile.json; using default docs/releases"
        }
    }
    if ([IO.Path]::IsPathRooted($relative)) { return [IO.Path]::GetFullPath($relative) }
    return [IO.Path]::GetFullPath((Join-Path $RepoRoot $relative))
}

$resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
$script = Find-ReleaseLedgerScript

if ($CheckRender) {
    if (-not $script -or -not (Test-Path -LiteralPath $script -PathType Leaf)) {
        Write-Host 'release-ledger-render: COULD NOT VERIFY (sza plugin release-ledger.ps1 not found)'
        exit 2
    }
    $libPath = Join-Path (Split-Path $script -Parent) '_lib.ps1'
    if (-not (Test-Path -LiteralPath $libPath -PathType Leaf)) {
        Write-Host 'release-ledger-render: COULD NOT VERIFY (release-ledger _lib.ps1 not found)'
        exit 2
    }
    $ledgerDir = Resolve-LedgerDirectory $resolvedRoot
    $channelsPath = Join-Path $ledgerDir 'channels.json'
    if (-not (Test-Path -LiteralPath $channelsPath -PathType Leaf)) {
        Write-Host "release-ledger-render: FAIL ($channelsPath not found)"
        exit 1
    }
    try {
        . $libPath
        $snap = Get-RlSnapshot $ledgerDir
        if ($snap.warnings.Count -gt 0) {
            Write-Host ("release-ledger-render: FAIL ($($snap.warnings -join '; '))")
            exit 1
        }
        $hist = Read-RlHistory $ledgerDir
        $expected = Format-RlMarkdown -Snapshot $snap -Events $hist.events
        $renderPath = (Get-RlFiles $ledgerDir).Render
        if (-not (Test-Path -LiteralPath $renderPath -PathType Leaf)) {
            Write-Host "release-ledger-render: FAIL ($renderPath is missing; run scripts/Invoke-ReleaseLedger.ps1 -Verb render)"
            exit 1
        }
        $actual = [IO.File]::ReadAllText($renderPath, $script:RlUtf8).Replace("`r`n", "`n")
        if ($actual -cne $expected) {
            Write-Host "release-ledger-render: FAIL ($renderPath is out of date; run scripts/Invoke-ReleaseLedger.ps1 -Verb render)"
            exit 1
        }
        Write-Host "release-ledger-render: PASS ($($snap.channels.Count) channel(s), $($snap.events) event(s))"
        exit 0
    }
    catch {
        Write-Host ("release-ledger-render: COULD NOT VERIFY ($($_.Exception.Message -replace '\s+', ' '))")
        exit 2
    }
}

if (-not $Verb) {
    $message = 'Invoke-ReleaseLedger.ps1 requires -Verb or -CheckRender.'
    if ($BestEffort) {
        Write-Host "release-ledger: skipped ($message)" -ForegroundColor Yellow
        $global:LASTEXITCODE = 0
        exit 0
    }
    Write-Error $message -ErrorAction Continue
    exit 2
}

if (-not $script -or -not (Test-Path -LiteralPath $script -PathType Leaf)) {
    $message = 'sza plugin release-ledger.ps1 not found'
    if ($BestEffort) {
        Write-Host "release-ledger: skipped ($message)" -ForegroundColor Yellow
        $global:LASTEXITCODE = 0
        exit 0
    }
    Write-Error "release-ledger: $message" -ErrorAction Continue
    exit 2
}

$forward = [ordered]@{
    Verb     = $Verb
    RepoRoot = $resolvedRoot
}
foreach ($name in 'Package', 'Flavor', 'Kind', 'Version', 'Artifact', 'Key', 'State', 'Ref', 'Url', 'Note', 'Track', 'Rollout', 'Source', 'At') {
    if ($PSBoundParameters.ContainsKey($name) -and -not [string]::IsNullOrWhiteSpace([string]$PSBoundParameters[$name])) {
        $forward[$name] = $PSBoundParameters[$name]
    }
}
if ($PSBoundParameters.ContainsKey('Last')) { $forward['Last'] = $Last }
if ($Json) { $forward['Json'] = $true }
if ($DryRun) { $forward['DryRun'] = $true }

try {
    $global:LASTEXITCODE = 0
    & $script @forward
    $code = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
}
catch {
    if ($BestEffort) {
        Write-Host "release-ledger: warning ($($_.Exception.Message))" -ForegroundColor Yellow
        $global:LASTEXITCODE = 0
        exit 0
    }
    throw
}

if ($code -ne 0) {
    if ($BestEffort) {
        Write-Host "release-ledger: warning (exit $code; build result is unchanged)" -ForegroundColor Yellow
        $global:LASTEXITCODE = 0
        exit 0
    }
    exit $code
}
exit 0
