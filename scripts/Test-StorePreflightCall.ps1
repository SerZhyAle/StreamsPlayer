#requires -Version 7.0
<#
.SYNOPSIS
  SP-0261 Part 2: prove the release flow's call of the shared Store preflight (scripts/Invoke-StorePreflight.ps1).

.DESCRIPTION
  Fast, local and read-only: no build, no network, no Store call. The shared preflight is replaced by a stub script
  that records the arguments it received and answers with a fixed exit code and output, so every case is deterministic
  and never needs the msstore CLI or the owner's credential.

    passes     exit 0 from the stub with a ready result: the call exits 0 and says it passed; the stub got the product
               name, the Store identity file, `-Json` and the package version as the MSIX remap of <Version>
               (26.0723.0957 -> 26.723.957.0); `-CredentialExpiry` reaches it only when given
    stops      exit 1 (a pending submission, a version that is not greater): the call exits 1 and names the reason
    manual     exit 2, no shared script at all, an answer that cannot be read, and a result that claims success without
               the ready evidence: exit 0 and exactly one line saying the Store step is manual - never a pass
    refuses    no identity.json, or a <Version> that is no release stamp: exit 1, and the stub is never called
    record     one JSON line per run in release-verdicts/<version>.store-preflight.jsonl, holding only the allowed keys;
               an identifier-looking field in the stub's output reaches neither the record nor the console
    boundary   the script holds no msstore call of its own (the shared script owns the read-only allow-list)

  Exit 0 when every check passes, 1 otherwise, 2 when a setup step threw before anything was judged. The last line is
  `store-preflight-call: PASS|FAIL|COULD NOT VERIFY (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$call = Join-Path $root 'scripts/Invoke-StorePreflight.ps1'

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Label) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Label); Write-Output "FAIL: $Label" }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('store-preflight-call-' + [guid]::NewGuid().ToString('N'))
$stubTemplate = @'
param([string]$ProductName, [string]$PackageVersion, [string]$IdentityFile, [string]$CredentialExpiry, [switch]$Json)
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'called.txt') -Value ($PSBoundParameters.Keys | Sort-Object | ForEach-Object { "$_=$($PSBoundParameters[$_])" })
Write-Output '__OUTPUT__'
exit __EXIT__
'@

# A fixture tree (props, identity file) and the stub that stands in for the shared script.
function New-Case([string] $Name, [int] $Exit, [string] $Output, [string] $Version = '26.0723.0957', [bool] $WithIdentity = $true) {
    $dir = Join-Path $work $Name
    New-Item -ItemType Directory -Path (Join-Path $dir 'msix') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $dir 'Directory.Build.props') -Value "<Project><PropertyGroup><Version>$Version</Version></PropertyGroup></Project>"
    if ($WithIdentity) { Copy-Item -LiteralPath (Join-Path $root 'msix/identity.json') -Destination (Join-Path $dir 'msix') }
    $stub = Join-Path $dir 'stub/Store-Preflight.ps1'
    New-Item -ItemType Directory -Path (Split-Path $stub -Parent) -Force | Out-Null
    Set-Content -LiteralPath $stub -Value $stubTemplate.Replace('__OUTPUT__', $Output).Replace('__EXIT__', "$Exit")
    return [pscustomobject]@{ Dir = $dir; Stub = $stub; Called = (Join-Path $dir 'stub/called.txt') }
}
function Invoke-Call([object] $Case, [hashtable] $Extra = @{}, [string] $PreflightPath = $null) {
    if (-not $PreflightPath) { $PreflightPath = $Case.Stub }
    $global:LASTEXITCODE = 0
    $text = (& $call -Root $Case.Dir -PreflightPath $PreflightPath @Extra *>&1 | Out-String)
    return @{ Exit = $LASTEXITCODE; Text = $text }
}
function Get-Records([object] $Case, [string] $Version = '26.0723.0957') {
    $path = Join-Path $Case.Dir "release-verdicts/$Version.store-preflight.jsonl"
    if (-not (Test-Path -LiteralPath $path)) { return , @() }
    return , @(Get-Content -LiteralPath $path | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
}
function Get-Called([object] $Case) {
    if (-not (Test-Path -LiteralPath $Case.Called)) { return $null }
    return (Get-Content -LiteralPath $Case.Called) -join '|'
}
function Get-ManualLines([string] $Text) { return , @($Text -split '\r?\n' | Where-Object { $_ -match 'Store step is manual' }) }

try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $identity = Get-Content -LiteralPath (Join-Path $root 'msix/identity.json') -Raw | ConvertFrom-Json
    $allowed = 'kind', 'storeId', 'packageVersion', 'exit', 'mode', 'checkedAt', 'refusal', 'submission', 'pricing'

    # passes: the arguments, the remap, the record, and a field that must not travel
    $ready = '{"mode":"ready","checkedAt":"2026-11-12T10:00:00.0000000+01:00","submission":"published","pricing":"Free","sellerId":"SELLER-1234","storeId":"LEAKED12345"}'
    $case = New-Case 'ready' 0 $ready
    $r = Invoke-Call $case
    Check ($r.Exit -eq 0 -and $r.Text -match 'passed') "a ready result exits 0 and says it passed (exit $($r.Exit))"
    Check ($r.Text -match 'Not tested here') 'the pass line says what the preflight does not test'
    $called = Get-Called $case
    Check ($called -match 'PackageVersion=26\.723\.957\.0') "the stub got the MSIX remap of the version ($called)"
    Check ($called -match 'ProductName=Streams Player') 'the stub got the product display name'
    Check ($called -match 'Json=True') 'the stub was asked for the sanitized -Json result'
    Check ($called -match [regex]::Escape("IdentityFile=$(Join-Path $case.Dir 'msix/identity.json')")) 'the stub got the identity file of the fixture tree'
    Check ($called -notmatch 'CredentialExpiry') 'no credential expiry is passed when none was given'
    $records = Get-Records $case
    Check ($records.Count -eq 1) "one record line for one run (got $($records.Count))"
    $keys = if ($records.Count) { @($records[0].PSObject.Properties.Name) } else { @() }
    Check (@($keys | Where-Object { $_ -cnotin $allowed }).Count -eq 0) "the record holds only the allowed keys ($($keys -join ', '))"
    Check ($records.Count -and $records[0].storeId -ceq $identity.StoreId -and $records[0].packageVersion -ceq '26.723.957.0' -and $records[0].mode -ceq 'ready' -and $records[0].exit -eq 0) 'the record names the Store ID, the package version, mode ready and exit 0'
    Check ($records.Count -and $records[0].submission -ceq 'published' -and $records[0].pricing -ceq 'Free') 'the record carries the published and Free classifications'
    $recordText = Get-Content -LiteralPath (Join-Path $case.Dir 'release-verdicts/26.0723.0957.store-preflight.jsonl') -Raw
    Check ($recordText -notmatch 'SELLER|LEAKED' -and $r.Text -notmatch 'SELLER|LEAKED') 'an identifier-looking field of the stub output reaches neither the record nor the console'

    # a second run appends a line and passes the expiry through
    $r = Invoke-Call $case @{ CredentialExpiry = '2027-06-30' }
    Check ($r.Exit -eq 0 -and (Get-Records $case).Count -eq 2) 'a second run appends a second line'
    Check ((Get-Called $case) -match 'CredentialExpiry=2027-06-30') 'a given credential expiry reaches the shared preflight'

    # stops: the channel refuses
    $case = New-Case 'pending' 1 '{"mode":"refused","refusal":"pending-submission","submission":"non-published"}'
    $r = Invoke-Call $case
    Check ($r.Exit -eq 1 -and $r.Text -match 'STOP' -and $r.Text -match 'pending') "a pending submission stops the release step (exit $($r.Exit))"
    $records = Get-Records $case
    Check ($records.Count -eq 1 -and $records[0].mode -ceq 'refused' -and $records[0].refusal -ceq 'pending-submission' -and $records[0].exit -eq 1) 'the refusal is recorded with its category'
    $case = New-Case 'stale' 1 '{"mode":"refused","refusal":"version-not-greater"}'
    $r = Invoke-Call $case
    Check ($r.Exit -eq 1 -and $r.Text -match 'not greater than every live package version' -and $r.Text -match '26\.723\.957\.0') 'a version that is not greater stops and names the version'
    $case = New-Case 'price' 1 '{"mode":"refused","refusal":"pricing","pricing":"other"}'
    $r = Invoke-Call $case
    Check ($r.Exit -eq 1 -and $r.Text -match 'Free') 'a price that is not Free stops'
    $case = New-Case 'refused-garbage' 1 'not json at all'
    $r = Invoke-Call $case
    Check ($r.Exit -eq 1 -and $r.Text -match 'STOP') 'a refusal whose evidence cannot be read still stops (fail closed)'

    # manual: the Store step goes on by hand, never a failure and never a pass
    $case = New-Case 'manual' 2 '{"mode":"manual"}'
    $r = Invoke-Call $case
    Check ($r.Exit -eq 0 -and (Get-ManualLines $r.Text).Count -eq 1) "an unavailable channel prints one manual line and exits 0 (exit $($r.Exit), lines $((Get-ManualLines $r.Text).Count))"
    Check ((Get-Records $case)[0].mode -ceq 'manual') 'the manual run is recorded as manual'
    $r = Invoke-Call $case -PreflightPath (Join-Path $work 'no-such-folder/Store-Preflight.ps1')
    Check ($r.Exit -eq 0 -and (Get-ManualLines $r.Text).Count -eq 1 -and $r.Text -match 'was not found') 'a missing shared preflight is a manual Store step, not a failure'
    $records = Get-Records $case
    Check ($records.Count -eq 2 -and $records[1].mode -ceq 'manual' -and $null -eq $records[1].exit) 'a run that could not ask the Store is recorded with no exit code'
    $case = New-Case 'success-garbage' 0 'not json at all'
    $r = Invoke-Call $case
    Check ($r.Exit -eq 0 -and (Get-ManualLines $r.Text).Count -eq 1 -and $r.Text -notmatch 'passed') 'success without readable ready evidence is manual, not a pass'
    Check ((Get-Records $case)[0].mode -ceq 'manual') 'that run is recorded as manual'
    $case = New-Case 'odd-exit' 7 '{"mode":"ready"}'
    $r = Invoke-Call $case
    Check ($r.Exit -eq 0 -and (Get-ManualLines $r.Text).Count -eq 1 -and $r.Text -notmatch 'passed') 'an exit code the preflight does not define is manual, not a pass'

    # refuses: nothing is asked of the Store when the repository's own inputs are unusable
    $case = New-Case 'no-identity' 0 $ready -WithIdentity $false
    $r = Invoke-Call $case
    Check ($r.Exit -eq 1 -and $r.Text -match 'identity file was not found') "no identity.json stops before the Store is asked (exit $($r.Exit))"
    Check ($null -eq (Get-Called $case) -and -not (Test-Path -LiteralPath (Join-Path $case.Dir 'release-verdicts'))) 'the stub was not called and no record was written'
    $case = New-Case 'bad-version' 0 $ready -Version '26.1325.0957'
    $r = Invoke-Call $case
    Check ($r.Exit -eq 1 -and $null -eq (Get-Called $case)) 'a Version that is no release stamp stops before the Store is asked'

    # boundary: this script never runs msstore itself
    $source = Get-Content -LiteralPath $call -Raw
    $helpEnd = $source.IndexOf('#>')
    $code = if ($helpEnd -ge 0) { $source.Substring($helpEnd + 2) } else { $source }
    Check ($helpEnd -ge 0 -and $code -notmatch '(?i)msstore') 'the script code never names msstore - the shared preflight owns the CLI and its read-only allow-list'
}
catch {
    # A setup step threw instead of answering: nothing was judged past this point, so the run could not verify
    # (CHECK-VERDICT 2.1, 2.5, 2.11). A check that already failed still outranks it (2.14).
    $reason = $_.Exception.Message -replace '\s+', ' '
    if ($failures.Count -eq 0) { Write-Output "store-preflight-call: COULD NOT VERIFY ($reason)"; exit 2 }
    Write-Output "store-preflight-call: the run stopped early: $reason"
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures.Count -eq 0) { Write-Output "store-preflight-call: PASS ($passed checks)"; exit 0 }
Write-Output "store-preflight-call: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
