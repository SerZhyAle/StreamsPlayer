#requires -Version 7.0
<#
.SYNOPSIS
  SP-0261: prove that msix/identity.json is the identity the package build and the Store read-back already used.

.DESCRIPTION
  Fast, local and read-only: no build, no Windows SDK, no network, no Store call.

    agrees   the three build values equal the reserved constants held independently in msix/Assert-MsixPackage.ps1;
             Publisher, PublisherDisplayName, Store ID and Package Family Name equal the identity table of
             msix/README.md; Store ID and IARC rating id equal STORE_PUBLISHING.md
    derives  the Package Family Name is the Identity Name, an underscore and the 13-character hash of the Publisher
    shapes   the Store ID is twelve characters, the rating id (when present) is a GUID, no account identifier key
    build    msix/build-msix.ps1 reads the file and carries none of the three values as a literal; a copy of it in a
             folder with no identity.json stops with a message naming the file and creates nothing (no stage, no
             artifacts folder)
    refuses  the reader throws on an absent file, an empty file, text that is not JSON, a JSON array, and a file that
             lacks any one required value

  Exit 0 when every check passes, 1 otherwise, 2 when a setup step threw before anything was judged. The last line is
  `msix-identity: PASS|FAIL|COULD NOT VERIFY (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$msix = $PSScriptRoot
$root = Split-Path $msix -Parent
. (Join-Path $msix 'MsixIdentity.ps1')

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Label) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Label); Write-Output "FAIL: $Label" }
}
function Get-Match([string] $Text, [string] $Pattern) {
    $m = [regex]::Match($Text, $Pattern, 'Multiline')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

# The 13-character suffix of a Package Family Name: SHA-256 of the UTF-16LE publisher, first 8 bytes, 5 bits per
# character (padded with a zero bit), alphabet without i, l, o, u.
function Get-PublisherHash([string] $Publisher) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::Unicode.GetBytes($Publisher))
    $bits = (($hash[0..7] | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') }) -join '').PadRight(65, '0')
    $alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
    return -join (0..12 | ForEach-Object { $alphabet[[Convert]::ToInt32($bits.Substring($_ * 5, 5), 2)] })
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('msix-identity-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $identityPath = Join-Path $msix 'identity.json'
    $identity = Read-MsixIdentity -Path $identityPath

    # agrees: the read-back's own constants
    $assertText = Get-Content -LiteralPath (Join-Path $msix 'Assert-MsixPackage.ps1') -Raw
    Check ($identity.IdentityName -ceq (Get-Match $assertText "^\`$reservedName = '([^']+)'")) 'IdentityName equals the read-back constant'
    Check ($identity.Publisher -ceq (Get-Match $assertText "^\`$reservedPublisher = '([^']+)'")) 'Publisher equals the read-back constant'
    Check ($identity.PublisherDisplayName -ceq (Get-Match $assertText "^\`$reservedDisplayName = '([^']+)'")) 'PublisherDisplayName equals the read-back constant'

    # agrees: the two documents that state the same values
    $readme = Get-Content -LiteralPath (Join-Path $msix 'README.md') -Raw
    $cell = '\|\s*`([^`]+)`\s*\|'
    Check ($identity.IdentityName -ceq (Get-Match $readme ('^\|\s*`Package/Identity/Name`\s*' + $cell))) 'IdentityName equals the msix/README.md table'
    Check ($identity.Publisher -ceq (Get-Match $readme ('^\|\s*`Package/Identity/Publisher`\s*' + $cell))) 'Publisher equals the msix/README.md table'
    Check ($identity.PublisherDisplayName -ceq (Get-Match $readme ('^\|\s*`Package/Properties/PublisherDisplayName`\s*' + $cell))) 'PublisherDisplayName equals the msix/README.md table'
    Check ($identity.PackageFamilyName -ceq (Get-Match $readme ('^\|\s*Package Family Name \(PFN\)\s*' + $cell))) 'PackageFamilyName equals the msix/README.md table'
    Check ($identity.StoreId -ceq (Get-Match $readme ('^\|\s*Store ID\s*' + $cell))) 'StoreId equals the msix/README.md table'
    $playbook = Get-Content -LiteralPath (Join-Path $root 'STORE_PUBLISHING.md') -Raw
    Check ($identity.StoreId -ceq (Get-Match $playbook ('^\|\s*Store ID\s*' + $cell))) 'StoreId equals the STORE_PUBLISHING.md table'
    $iarc = $identity.PSObject.Properties['IarcRatingId']?.Value
    Check ($null -ne $iarc -and $iarc -ceq (Get-Match $playbook ('^\|\s*IARC Global Rating ID\s*' + $cell))) 'IarcRatingId equals the STORE_PUBLISHING.md table'

    # derives, shapes
    Check ($identity.PackageFamilyName -ceq ($identity.IdentityName + '_' + (Get-PublisherHash $identity.Publisher))) 'PackageFamilyName is the Identity Name plus the hash of the Publisher'
    Check ($identity.StoreId -cmatch '^[0-9A-Z]{12}$') 'StoreId has twelve characters'
    Check ($null -ne $iarc -and $iarc -match '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$') 'IarcRatingId is a GUID'
    $allowedKeys = 'IdentityName', 'Publisher', 'PublisherDisplayName', 'StoreId', 'PackageFamilyName', 'IarcRatingId'
    $extraKeys = @($identity.PSObject.Properties.Name | Where-Object { $_ -cnotin $allowedKeys })
    Check ($extraKeys.Count -eq 0) "identity.json holds only the six identity keys (extra: $($extraKeys -join ', '))"

    # build: the script reads the file and carries no literal of the three values
    $buildPath = Join-Path $msix 'build-msix.ps1'
    $buildText = Get-Content -LiteralPath $buildPath -Raw
    Check ($buildText.Contains('Read-MsixIdentity')) 'build-msix.ps1 reads the identity file'
    Check (-not $buildText.Contains($identity.IdentityName)) 'build-msix.ps1 carries no Identity Name literal'
    Check (-not $buildText.Contains($identity.Publisher)) 'build-msix.ps1 carries no Publisher literal'
    Check ($buildText -notmatch '\$PublisherDisplayName\s*=\s*''') 'build-msix.ps1 carries no PublisherDisplayName default'

    # build: no identity file, no build - and nothing is created before the stop
    $bare = Join-Path $work 'bare'
    $bareMsix = Join-Path $bare 'msix'
    New-Item -ItemType Directory -Path $bareMsix -Force | Out-Null
    Copy-Item -LiteralPath $buildPath, (Join-Path $msix 'MsixIdentity.ps1') -Destination $bareMsix
    $output = (& pwsh -NoProfile -File (Join-Path $bareMsix 'build-msix.ps1') 2>&1 | Out-String)
    $exit = $LASTEXITCODE
    Check ($exit -ne 0) "a build with no identity.json exits non-zero (exit $exit)"
    Check ($output -match 'identity file was not found') 'the stop names the missing identity file'
    Check (-not (Test-Path -LiteralPath (Join-Path $bare 'artifacts')) -and -not (Test-Path -LiteralPath (Join-Path $bareMsix 'stage'))) 'a build with no identity.json creates no publish or stage folder'

    # refuses: the reader
    function Test-Refused([string] $Label, [string] $Content) {
        $path = Join-Path $work ('case-' + [guid]::NewGuid().ToString('N') + '.json')
        if ($null -ne $Content) { Set-Content -LiteralPath $path -Value $Content -Encoding utf8 }
        $threw = $false
        try { $null = Read-MsixIdentity -Path $path } catch { $threw = $true }
        Check $threw "the reader refuses $Label"
    }
    Test-Refused 'an absent file' $null
    Test-Refused 'an empty file' ''
    Test-Refused 'text that is not JSON' 'not json'
    Test-Refused 'a JSON array' '[1, 2]'
    foreach ($key in 'IdentityName', 'Publisher', 'PublisherDisplayName', 'StoreId', 'PackageFamilyName') {
        $copy = [ordered]@{}
        foreach ($property in $identity.PSObject.Properties) { if ($property.Name -ne $key) { $copy[$property.Name] = $property.Value } }
        Test-Refused "a file without $key" ($copy | ConvertTo-Json)
        $copy[$key] = ' '
        Test-Refused "a file with a blank $key" ($copy | ConvertTo-Json)
    }
}
catch {
    # A setup step threw instead of answering: nothing was judged past this point, so the run could not verify
    # (CHECK-VERDICT 2.1, 2.5, 2.11). A check that already failed still outranks it (2.14).
    $reason = $_.Exception.Message -replace '\s+', ' '
    if ($failures.Count -eq 0) { Write-Output "msix-identity: COULD NOT VERIFY ($reason)"; exit 2 }
    Write-Output "msix-identity: the run stopped early: $reason"
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures.Count -eq 0) { Write-Output "msix-identity: PASS ($passed checks)"; exit 0 }
Write-Output "msix-identity: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
