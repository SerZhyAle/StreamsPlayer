#requires -Version 7.0
<#
.SYNOPSIS
    Fast fixture tests for scripts/Invoke-ReleaseLedger.ps1 and docs/releases/channels.json (RELEASE-LEDGER 0.1).

.DESCRIPTION
    Verifies argument forwarding to a stub release-ledger.ps1, -BestEffort resilience (missing plugin, non-zero exit,
    throwing writer all leave exit 0), non-BestEffort failure propagation, -CheckRender PASS/FAIL/COULD NOT VERIFY
    against a scratch ledger, and that the repository's own docs/releases/channels.json declares every channel in
    .sza-canon.json.

    Exit 0 when every check passes, 1 on failure, 2 if setup failed before checks could run (CHECK-VERDICT).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$call = Join-Path $root 'scripts/Invoke-ReleaseLedger.ps1'

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Label) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Label); Write-Output "FAIL: $Label" }
}

function Invoke-Wrapper([hashtable] $Arguments) {
    $global:LASTEXITCODE = 0
    $text = (& $call @Arguments *>&1 | Out-String)
    $code = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
    return @{ Exit = $code; Text = $text }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('release-ledger-call-' + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    $stubDir = Join-Path $work 'stub'
    $stub = Join-Path $stubDir 'release-ledger.ps1'
    $calledFile = Join-Path $stubDir 'called.txt'
    $libFile = Join-Path $stubDir '_lib.ps1'
    New-Item -ItemType Directory -Path $stubDir -Force | Out-Null

    @'
param(
    [string]$Verb, [string]$RepoRoot, [string]$Package, [string]$Flavor, [string]$Kind,
    [string]$Version, [string]$Artifact, [string]$Key, [string]$State, [string]$Ref,
    [string]$Url, [string]$Note, [string]$Track, [string]$Rollout, [string]$Source,
    [string]$At, [int]$Last, [switch]$Json, [switch]$DryRun
)
$lines = foreach ($k in ($PSBoundParameters.Keys | Sort-Object)) { "$k=$($PSBoundParameters[$k])" }
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'called.txt') -Value $lines -Encoding utf8
$mode = $env:SZA_TEST_RL_MODE
if ($mode -eq 'fail') { Write-Output 'stub failed'; exit 1 }
if ($mode -eq 'throw') { throw 'stub boom' }
Write-Output "stub ok $Verb"
exit 0
'@ | Set-Content -LiteralPath $stub -Encoding utf8

    $repoCase = Join-Path $work 'repo'
    $ledgerDir = Join-Path $repoCase 'docs/releases'
    New-Item -ItemType Directory -Path $ledgerDir -Force | Out-Null

    # --- 1. Argument forwarding ---
    $env:SZA_TEST_RL_MODE = 'ok'
    $r = Invoke-Wrapper @{
        Root = $repoCase; LedgerScriptPath = $stub
        Verb = 'build'; Package = 'app'; Kind = 'test'; Flavor = 'win-x64'
        Version = '26.1118.2200'; Artifact = 'artifacts/local/win-x64/StreamsPlayer.exe'
    }
    Check ($r.Exit -eq 0 -and $r.Text -match 'stub ok build') "forwards build verb cleanly (exit $($r.Exit))"
    $called = (Get-Content -LiteralPath $calledFile) -join '|'
    Check ($called -match 'Verb=build' -and $called -match 'Package=app' -and $called -match 'Kind=test' -and $called -match 'Flavor=win-x64' -and $called -match 'Version=26\.1118\.2200' -and $called -match 'Artifact=artifacts/local/win-x64/StreamsPlayer\.exe') "forwards every build argument ($called)"
    Check ($called -match [regex]::Escape("RepoRoot=$repoCase")) "passes RepoRoot=$repoCase"

    # --- 2. -BestEffort never fails the caller ---
    $env:SZA_TEST_RL_MODE = 'fail'
    $r = Invoke-Wrapper @{ Root = $repoCase; LedgerScriptPath = $stub; Verb = 'build'; Package = 'app'; Kind = 'test'; Version = '26.1118.2200'; BestEffort = $true }
    Check ($r.Exit -eq 0 -and $r.Text -match 'warning') "BestEffort swallows non-zero writer exit (exit $($r.Exit))"
    $r = Invoke-Wrapper @{ Root = $repoCase; LedgerScriptPath = $stub; Verb = 'build'; Package = 'app'; Kind = 'test'; Version = '26.1118.2200' }
    Check ($r.Exit -eq 1) "without BestEffort, non-zero writer exit propagates (exit $($r.Exit))"

    $env:SZA_TEST_RL_MODE = 'throw'
    $r = Invoke-Wrapper @{ Root = $repoCase; LedgerScriptPath = $stub; Verb = 'build'; Package = 'app'; Kind = 'test'; Version = '26.1118.2200'; BestEffort = $true }
    Check ($r.Exit -eq 0 -and $r.Text -match 'warning') "BestEffort catches throwing writer (exit $($r.Exit))"

    $missing = Join-Path $work 'no-such/release-ledger.ps1'
    $r = Invoke-Wrapper @{ Root = $repoCase; LedgerScriptPath = $missing; Verb = 'build'; Package = 'app'; Kind = 'test'; Version = '26.1118.2200'; BestEffort = $true }
    Check ($r.Exit -eq 0 -and $r.Text -match 'skipped') "BestEffort skips cleanly when plugin is absent (exit $($r.Exit))"
    $r = Invoke-Wrapper @{ Root = $repoCase; LedgerScriptPath = $missing; Verb = 'show' }
    Check ($r.Exit -eq 2) "without BestEffort, missing plugin exits 2 (exit $($r.Exit))"

    # --- 3. -CheckRender against a stub _lib.ps1 ---
    @'
$script:RlUtf8 = [Text.UTF8Encoding]::new($false)
function Get-RlFiles([string]$Dir) { return [pscustomobject]@{ Render = (Join-Path $Dir 'RELEASES.md') } }
function Get-RlSnapshot([string]$Dir) { return [pscustomobject]@{ channels = @('github'); events = 0; warnings = @() } }
function Read-RlHistory([string]$Dir) { return [pscustomobject]@{ events = @() } }
function Format-RlMarkdown($Snapshot, $Events) { return "# Releases`n`nExpected.`n" }
'@ | Set-Content -LiteralPath $libFile -Encoding utf8

    $r = Invoke-Wrapper @{ Root = $repoCase; LedgerScriptPath = $stub; CheckRender = $true }
    Check ($r.Exit -eq 1 -and $r.Text -match 'channels\.json not found') "CheckRender fails when channels.json is absent (exit $($r.Exit))"

    '{"schema":"1","packages":[{"id":"app","title":"App"}],"channels":[]}' | Set-Content -LiteralPath (Join-Path $ledgerDir 'channels.json') -Encoding utf8
    [IO.File]::WriteAllText((Join-Path $ledgerDir 'RELEASES.md'), "# Releases`n`nExpected.`n", [Text.UTF8Encoding]::new($false))
    $r = Invoke-Wrapper @{ Root = $repoCase; LedgerScriptPath = $stub; CheckRender = $true }
    Check ($r.Exit -eq 0 -and $r.Text -match 'release-ledger-render: PASS') "CheckRender passes when RELEASES.md matches render (exit $($r.Exit))"

    [IO.File]::WriteAllText((Join-Path $ledgerDir 'RELEASES.md'), "# Releases`n`nStale.`n", [Text.UTF8Encoding]::new($false))
    $r = Invoke-Wrapper @{ Root = $repoCase; LedgerScriptPath = $stub; CheckRender = $true }
    Check ($r.Exit -eq 1 -and $r.Text -match 'out of date') "CheckRender fails when RELEASES.md is stale (exit $($r.Exit))"

    $r = Invoke-Wrapper @{ Root = $repoCase; LedgerScriptPath = $missing; CheckRender = $true }
    Check ($r.Exit -eq 2 -and $r.Text -match 'COULD NOT VERIFY') "CheckRender reports COULD NOT VERIFY when plugin is absent (exit $($r.Exit))"

    # --- 4. Repository channels.json agrees with .sza-canon.json ---
    $stamp = Get-Content -LiteralPath (Join-Path $root '.sza-canon.json') -Raw -Encoding utf8 | ConvertFrom-Json
    $channelsDoc = Get-Content -LiteralPath (Join-Path $root 'docs/releases/channels.json') -Raw -Encoding utf8 | ConvertFrom-Json
    Check ($channelsDoc.schema -ceq '1') 'repo channels.json carries schema "1"'
    $declaredKinds = @($channelsDoc.channels | ForEach-Object { [string]$_.channel })
    foreach ($c in @($stamp.channels)) {
        Check ($declaredKinds -contains [string]$c) "repo channels.json declares stamp channel '$c'"
    }
    $msstore = @($channelsDoc.channels | Where-Object { $_.channel -eq 'msstore' }) | Select-Object -First 1
    $identity = Get-Content -LiteralPath (Join-Path $root 'msix/identity.json') -Raw -Encoding utf8 | ConvertFrom-Json
    Check ($null -ne $msstore -and $msstore.storeId -ceq $identity.StoreId) 'repo channels.json msstore storeId matches msix/identity.json'
}
catch {
    $reason = $_.Exception.Message -replace '\s+', ' '
    if ($failures.Count -eq 0) { Write-Output "release-ledger-call: COULD NOT VERIFY ($reason)"; exit 2 }
    Write-Output "release-ledger-call: the run stopped early: $reason"
}
finally {
    Remove-Item Env:SZA_TEST_RL_MODE -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -eq 0) { Write-Output "release-ledger-call: PASS (run=$passed skipped=0)"; exit 0 }
Write-Output "release-ledger-call: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
