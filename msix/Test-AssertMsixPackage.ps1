#requires -Version 7.0
<#
.SYNOPSIS
  SP-0190: fixture tests for msix/Assert-MsixPackage.ps1, the Store read-back of WINDOWS-STORE rules 1-4.

.DESCRIPTION
  Builds throwaway zip archives shaped like a packed MSIX (a manifest, the notices file, optionally a signature)
  and runs the real assertion script on each. No Windows SDK is needed.

    accepts   a correct unsigned upload; a correct self-signed test package; a test identity under -TestIdentity
    refuses   the wrong Publisher, Identity Name or PublisherDisplayName; a signed package named as the upload
              candidate; a package missing the notices file; a signed upload; an unsigned "self-signed" package;
              a version with a leading zero, a part over 65535, a revision other than 0, three parts or a value
              that is not the tag's remap; a missing archive, a missing or unreadable manifest, an absent
              Identity or an absent attribute (fail closed)

  Exit 0 when every check passes, 1 otherwise. The last line is `msix-package-tests: PASS|FAIL (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$assert = Join-Path $PSScriptRoot 'Assert-MsixPackage.ps1'
$name = 'SZA.StreamsPlayer'
$publisher = 'CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD'
$version = '26.1003.30.0'

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Label) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Label); Write-Output "FAIL: $Label" }
}

function New-Manifest($IdentityName, $IdentityPublisher, $IdentityVersion, $Display) {
    $identityAttributes = @()
    if ($null -ne $IdentityName) { $identityAttributes += "Name=`"$IdentityName`"" }
    if ($null -ne $IdentityPublisher) { $identityAttributes += "Publisher=`"$IdentityPublisher`"" }
    if ($null -ne $IdentityVersion) { $identityAttributes += "Version=`"$IdentityVersion`"" }
    $displayElement = if ($null -ne $Display) { "<PublisherDisplayName>$Display</PublisherDisplayName>" } else { '' }
    @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity $($identityAttributes -join ' ') ProcessorArchitecture="x64" />
  <Properties><DisplayName>StreamsPlayer</DisplayName>$displayElement</Properties>
</Package>
"@
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0190-msix-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null

# Writes a zip shaped like a packed MSIX. -Manifest $null leaves AppxManifest.xml out.
function New-Package([string] $FileName, $Manifest, [bool] $Notices = $true, [bool] $Signature = $false) {
    $path = Join-Path $work $FileName
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    $zip = [System.IO.Compression.ZipFile]::Open($path, 'Create')
    try {
        $members = [ordered]@{ 'StreamsPlayer.exe' = 'MZ' }
        if ($Notices) { $members['THIRD-PARTY-NOTICES.txt'] = 'notices' }
        if ($Signature) { $members['AppxSignature.p7x'] = 'PKCX' }
        if ($null -ne $Manifest) { $members['AppxManifest.xml'] = $Manifest }
        foreach ($member in $members.Keys) {
            $entry = $zip.CreateEntry($member)
            $writer = [System.IO.StreamWriter]::new($entry.Open(), [System.Text.UTF8Encoding]::new($false))
            try { $writer.Write($members[$member]) } finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    $path
}

function Invoke-Assert([string] $Path, [string[]] $Extra = @(), [string] $ExpectedVersion = $version) {
    $out = & pwsh -NoProfile -File $assert -PackagePath $Path -ExpectedVersion $ExpectedVersion @Extra 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = ($out -join "`n") }
}
function Refuses([object] $Result, [string] $Assertion) {
    $Result.Exit -ne 0 -and $Result.Output -match "msix-package: FAIL $Assertion " -and $Result.Output -match 'msix-package: FAIL \('
}

try {
    $good = New-Manifest $name $publisher $version 'SZA'

    # --- accepted ------------------------------------------------------------------------------------------
    $upload = New-Package 'StreamsPlayer-26.1003.0030-windows-x64.msix' $good
    $r = Invoke-Assert $upload
    Check ($r.Exit -eq 0 -and $r.Output -match 'msix-package: PASS \(9 assertions, upload-ready\)') 'a correct unsigned upload passes'
    $verdictLines = @($r.Output -split "`n" | Where-Object { $_ -match '^msix-package: (PASS|FAIL) \S+ - ' })
    Check ($verdictLines.Count -eq 9) "one verdict line per assertion (got $($verdictLines.Count) of 9)"
    foreach ($assertion in 'name', 'archive', 'manifest', 'identity-name', 'identity-publisher', 'display-name', 'version', 'signature', 'notices') {
        Check ([bool]($verdictLines | Where-Object { $_ -match "^msix-package: PASS $assertion - " })) "verdict line for $assertion"
    }

    $signedTest = New-Package 'StreamsPlayer-26.1003.0030-windows-x64-selfsigned.msix' $good -Signature $true
    $r = Invoke-Assert $signedTest @('-SelfSigned')
    Check ($r.Exit -eq 0 -and $r.Output -match 'local test package') 'a correct self-signed test package passes'

    $other = New-Package 'other-identity.msix' (New-Manifest 'Test.Other' 'CN=Test' $version 'Test')
    $r = Invoke-Assert $other @('-TestIdentity', '-IdentityName', 'Test.Other', '-Publisher', 'CN=Test', '-PublisherDisplayName', 'Test')
    Check ($r.Exit -eq 0 -and $r.Output -match 'not Store-valid') 'a test identity passes only under -TestIdentity and is called not Store-valid'
    $r = Invoke-Assert $other
    Check (Refuses $r 'identity-name') 'a test identity is refused without -TestIdentity'

    # --- requirement 5: the three named refusals --------------------------------------------------------------
    $r = Invoke-Assert (New-Package 'wrong-publisher.msix' (New-Manifest $name 'CN=SELF-SIGNED-PLACEHOLDER' $version 'SZA'))
    Check (Refuses $r 'identity-publisher') 'a package with the wrong publisher is refused'

    $r = Invoke-Assert (New-Package 'StreamsPlayer-signed-upload.msix' $good -Signature $true)
    Check (Refuses $r 'signature') 'a signed package named as the upload candidate is refused'
    $signedAsUpload = New-Package 'copy-selfsigned.msix' $good -Signature $true
    $r = Invoke-Assert $signedAsUpload
    Check (Refuses $r 'name') 'the self-signed test name offered as the upload candidate is refused'

    $r = Invoke-Assert (New-Package 'no-notices.msix' $good -Notices $false)
    Check (Refuses $r 'notices') 'a package without THIRD-PARTY-NOTICES.txt is refused'

    # --- identity values ---------------------------------------------------------------------------------------
    $r = Invoke-Assert (New-Package 'wrong-name.msix' (New-Manifest 'WRONG.StreamsPlayer' $publisher $version 'SZA'))
    Check (Refuses $r 'identity-name') 'the wrong Identity Name is refused'
    $r = Invoke-Assert (New-Package 'wrong-display.msix' (New-Manifest $name $publisher $version 'Someone Else'))
    Check (Refuses $r 'display-name') 'the wrong PublisherDisplayName is refused'
    $r = Invoke-Assert (New-Package 'case-name.msix' (New-Manifest 'sza.streamsplayer' $publisher $version 'SZA'))
    Check (Refuses $r 'identity-name') 'an Identity Name differing only by case is refused'

    # --- signature and name coupling ---------------------------------------------------------------------------
    $r = Invoke-Assert (New-Package 'unsigned-test-build-selfsigned.msix' $good) @('-SelfSigned')
    Check (Refuses $r 'signature') 'a package offered as self-signed that carries no signature is refused'
    $r = Invoke-Assert (New-Package 'selfsigned-wrong-name.msix' $good -Signature $true) @('-SelfSigned')
    Check (Refuses $r 'name') 'a self-signed package without the -selfsigned name is refused'

    # --- version -------------------------------------------------------------------------------------------------
    foreach ($bad in @(
            @('26.1003.030.0', 'a leading zero in a part'),
            @('26.1003.70000.0', 'a part over 65535'),
            @('26.1003.30.1', 'a revision other than 0'),
            @('26.1003.30', 'three parts'),
            @('26.1003.31.0', 'a version that is not the remap of the tag'),
            @('26.x.30.0', 'a non-numeric part'))) {
        $r = Invoke-Assert (New-Package 'version.msix' (New-Manifest $name $publisher $bad[0] 'SZA'))
        Check (Refuses $r 'version') "$($bad[1]) in the packed version is refused"
    }
    $r = Invoke-Assert (New-Package 'version-ok.msix' $good) -ExpectedVersion '26.1003.030.0'
    Check ($r.Exit -ne 0 -and $r.Output -match 'ExpectedVersion') 'a malformed expected version stops the script instead of blessing a package'

    # --- fail closed ---------------------------------------------------------------------------------------------
    $r = Invoke-Assert (Join-Path $work 'does-not-exist.msix')
    Check (Refuses $r 'archive') 'a missing archive is refused'
    $garbage = Join-Path $work 'not-a-zip.msix'
    Set-Content -LiteralPath $garbage -Value 'this is not a zip'
    $r = Invoke-Assert $garbage
    Check (Refuses $r 'archive') 'a file that is not an archive is refused'
    $r = Invoke-Assert (New-Package 'no-manifest.msix' $null)
    Check (Refuses $r 'manifest') 'an archive without AppxManifest.xml is refused'
    $r = Invoke-Assert (New-Package 'bad-xml.msix' '<Package><Identity')
    Check (Refuses $r 'manifest') 'an unreadable manifest is refused'
    $r = Invoke-Assert (New-Package 'no-identity.msix' '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Properties /></Package>')
    Check (Refuses $r 'identity') 'a manifest with no Identity element is refused'
    $r = Invoke-Assert (New-Package 'no-publisher-attr.msix' (New-Manifest $name $null $version 'SZA'))
    Check ((Refuses $r 'identity-publisher') -and $r.Output -match 'absent') 'an absent Publisher attribute is refused as absent'
    $r = Invoke-Assert (New-Package 'no-display.msix' (New-Manifest $name $publisher $version $null))
    Check (Refuses $r 'display-name') 'an absent PublisherDisplayName is refused'
    $r = Invoke-Assert (New-Package 'no-version-attr.msix' (New-Manifest $name $publisher $null 'SZA'))
    Check (Refuses $r 'version') 'an absent Version attribute is refused'
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures.Count -eq 0) { Write-Output "msix-package-tests: PASS ($passed checks)"; exit 0 }
Write-Output "msix-package-tests: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
