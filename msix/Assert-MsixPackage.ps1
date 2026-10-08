#requires -Version 7.0
<#
.SYNOPSIS
  SP-0190: prove, on the packed MSIX archive, what WINDOWS-STORE rules 1-4 say a Store package must be.

.DESCRIPTION
  The package is judged out of the packed file - the thing Partner Center receives - never out of the values
  that were substituted into the manifest, so a script that agrees with its own wrong defaults cannot pass.
  The reserved identity is held here as constants of its own, independent of build-msix.ps1's parameters.

  One verdict line per assertion, then one summary line:
      msix-package: PASS <assertion> - <evidence>
      msix-package: FAIL <assertion> - <what was found>
      msix-package: PASS|FAIL (<n> assertions)
  Fail closed: a missing archive, an unreadable manifest or an absent value is a FAIL, never a pass. Exit 0 only
  when every assertion passed; any failure exits 1.

  Assertions:
    archive       the file exists and opens as a zip
    name          the output name fits the mode - the Store upload is never named like (or as) the signed test build
    manifest      AppxManifest.xml is a member and parses
    identity-name, identity-publisher, display-name   equal the reserved values
    version       equals the version derived from the tag AND has the MSIX shape (four numeric parts, revision 0,
                  no leading zeros, every part at most 65535)
    signature     no AppxSignature.p7x in the unsigned upload; present in the self-signed test package
    notices       THIRD-PARTY-NOTICES.txt is a member of the archive (canon invariant 12)

.PARAMETER PackagePath
  The packed .msix to judge.

.PARAMETER ExpectedVersion
  The four-part identity version derived from the tag, e.g. 26.1003.30.0.

.PARAMETER SelfSigned
  The package is the locally signed test build: it must carry the signature and must be named *-selfsigned.msix.
  Without it the package is the Store upload candidate and must carry no signature.

.PARAMETER TestIdentity
  Judge Identity Name, Publisher and PublisherDisplayName against -IdentityName/-Publisher/-PublisherDisplayName
  instead of the reserved values, for a one-off package under another identity. The verdict then says the package
  is not Store-valid.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackagePath,
    [Parameter(Mandatory)] [string] $ExpectedVersion,
    [switch] $SelfSigned,
    [switch] $TestIdentity,
    [string] $IdentityName,
    [string] $Publisher,
    [string] $PublisherDisplayName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The permanent Partner Center identity for Store ID 9NBTD5SXB8TB (msix/README.md). Changing Name or Publisher
# orphans every installed copy.
$reservedName = 'SZA.StreamsPlayer'
$reservedPublisher = 'CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD'
$reservedDisplayName = 'SZA'
$signatureEntry = 'AppxSignature.p7x'
$noticesEntry = 'THIRD-PARTY-NOTICES.txt'

if ($TestIdentity) {
    foreach ($required in 'IdentityName', 'Publisher', 'PublisherDisplayName') {
        if (-not $PSBoundParameters.ContainsKey($required)) { throw "-TestIdentity needs -$required." }
    }
    $wantName, $wantPublisher, $wantDisplayName = $IdentityName, $Publisher, $PublisherDisplayName
}
else {
    $wantName, $wantPublisher, $wantDisplayName = $reservedName, $reservedPublisher, $reservedDisplayName
}

$verdicts = [System.Collections.Generic.List[bool]]::new()
function Write-Verdict([bool] $Passed, [string] $Assertion, [string] $Detail) {
    $verdicts.Add($Passed)
    Write-Host ("msix-package: {0} {1} - {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Assertion, $Detail)
}
function Assert-Equal([string] $Assertion, [string] $Found, [string] $Wanted, [string] $What) {
    if ($Found -ceq $Wanted) { Write-Verdict $true $Assertion "$What '$Found'" }
    elseif ([string]::IsNullOrEmpty($Found)) { Write-Verdict $false $Assertion "$What is absent from the packed manifest (wanted '$Wanted')" }
    else { Write-Verdict $false $Assertion "$What is '$Found', not '$Wanted'" }
}

function Get-VersionShapeProblem([string] $Version) {
    $parts = $Version -split '\.'
    if ($parts.Count -ne 4) { return "'$Version' does not have four parts" }
    foreach ($part in $parts) {
        if ($part -notmatch '^(0|[1-9]\d{0,4})$') { return "part '$part' of '$Version' is not a number without leading zeros" }
        if ([int]$part -gt 65535) { return "part '$part' of '$Version' exceeds 65535" }
    }
    if ($parts[3] -ne '0') { return "revision '$($parts[3])' of '$Version' is not 0" }
    return $null
}

# The expected value itself must be well formed, or a typo on the caller's side would bless a bad package.
$expectedProblem = Get-VersionShapeProblem $ExpectedVersion
if ($expectedProblem) { throw "-ExpectedVersion is not an MSIX identity version: $expectedProblem." }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$fileName = [System.IO.Path]::GetFileName($PackagePath)
$isSelfSignedName = $fileName -clike '*-selfsigned.msix'
if ($SelfSigned) {
    if ($isSelfSignedName) { Write-Verdict $true 'name' "'$fileName' is a self-signed test name" }
    else { Write-Verdict $false 'name' "'$fileName' is the self-signed test package but is not named *-selfsigned.msix" }
}
elseif ($isSelfSignedName) {
    Write-Verdict $false 'name' "'$fileName' carries the self-signed test name and is offered as the upload candidate"
}
else { Write-Verdict $true 'name' "'$fileName' is the upload name" }

$archive = $null
try {
    try {
        if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "no such file: $PackagePath" }
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
        Write-Verdict $true 'archive' "$($archive.Entries.Count) entries"
    }
    catch {
        Write-Verdict $false 'archive' "cannot open the packed archive: $($_.Exception.Message)"
        $archive = $null
    }

    if ($archive) {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })

        $manifestXml = $null
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if (-not $manifestEntry) { Write-Verdict $false 'manifest' 'no AppxManifest.xml in the packed archive' }
        else {
            try {
                $reader = [System.IO.StreamReader]::new($manifestEntry.Open(), [System.Text.Encoding]::UTF8)
                try { $manifestXml = [xml] $reader.ReadToEnd() } finally { $reader.Dispose() }
                Write-Verdict $true 'manifest' 'AppxManifest.xml read from the archive'
            }
            catch {
                $manifestXml = $null
                Write-Verdict $false 'manifest' "AppxManifest.xml is unreadable: $($_.Exception.Message)"
            }
        }

        $identity = $null
        $displayNode = $null
        if ($manifestXml) {
            $identity = $manifestXml.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
            $displayNode = $manifestXml.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='PublisherDisplayName']")
            if (-not $identity) { Write-Verdict $false 'identity' 'the packed manifest has no Identity element' }
        }
        # Attribute reads go through GetAttribute, which yields '' for an absent attribute: an absent value is a FAIL.
        $packedName = if ($identity) { $identity.GetAttribute('Name') } else { '' }
        $packedPublisher = if ($identity) { $identity.GetAttribute('Publisher') } else { '' }
        $packedVersion = if ($identity) { $identity.GetAttribute('Version') } else { '' }
        $packedDisplay = if ($displayNode) { $displayNode.InnerText } else { '' }

        Assert-Equal 'identity-name' $packedName $wantName 'Identity Name'
        Assert-Equal 'identity-publisher' $packedPublisher $wantPublisher 'Identity Publisher'
        Assert-Equal 'display-name' $packedDisplay $wantDisplayName 'PublisherDisplayName'

        if ([string]::IsNullOrEmpty($packedVersion)) {
            Write-Verdict $false 'version' "Identity Version is absent from the packed manifest (wanted '$ExpectedVersion')"
        }
        else {
            $shapeProblem = Get-VersionShapeProblem $packedVersion
            if ($shapeProblem) { Write-Verdict $false 'version' "packed Identity Version: $shapeProblem" }
            elseif ($packedVersion -cne $ExpectedVersion) { Write-Verdict $false 'version' "packed Identity Version '$packedVersion' is not the version derived from the tag '$ExpectedVersion'" }
            else { Write-Verdict $true 'version' "Identity Version '$packedVersion' is the remap of the tag (four parts, revision 0, no leading zeros, parts at most 65535)" }
        }

        $signed = $entryNames -ccontains $signatureEntry
        if ($SelfSigned) {
            if ($signed) { Write-Verdict $true 'signature' "$signatureEntry present (signed - a local test, never uploaded)" }
            else { Write-Verdict $false 'signature' "the self-signed test package carries no $signatureEntry - signtool did not sign it" }
        }
        elseif ($signed) { Write-Verdict $false 'signature' "$signatureEntry is in the upload candidate - Microsoft signs at certification, a signed package is a local test" }
        else { Write-Verdict $true 'signature' "no $signatureEntry (unsigned, Store-ready)" }

        if ($entryNames -ccontains $noticesEntry) { Write-Verdict $true 'notices' "$noticesEntry is a member of the packed archive" }
        else { Write-Verdict $false 'notices' "$noticesEntry is not in the packed archive (WINDOWS-STORE rule 4)" }
    }
}
finally { if ($archive) { $archive.Dispose() } }

$failed = @($verdicts | Where-Object { -not $_ }).Count
if ($failed -eq 0) {
    $scope = if ($TestIdentity) { 'a test-identity package, not Store-valid' } elseif ($SelfSigned) { 'a local test package, never uploaded' } else { 'upload-ready' }
    Write-Host "msix-package: PASS ($($verdicts.Count) assertions, $scope)" -ForegroundColor Green
    exit 0
}
Write-Host "msix-package: FAIL ($failed of $($verdicts.Count) assertions)" -ForegroundColor Red
exit 1
