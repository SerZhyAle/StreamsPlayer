#requires -Version 7.0
<#
.SYNOPSIS
  SP-0247 (PACKAGE-VERSIONING P5, W4): read the release version back out of the produced artifacts.

.DESCRIPTION
  The version is read from the files a user receives - the staged StreamsPlayer.exe, the copy inside the portable
  ZIP (extracted to a temporary folder) and the setup EXE's own version resource - never from the variable the
  build was given, so a build that ignored its -p:Version cannot pass.

    exe, zip    FileVersion is the version exactly; ProductVersion is the version, optionally followed by the
                `+<commit>` the .NET SDK appends; the binary file version is the MSIX remap of the version
                (ConvertTo-MsixVersion), which is the order-preserving PE rendering of W2
    zip-entry   the archive holds StreamsPlayer.exe at its root
    setup       ProductVersion is the version. Inno writes AppVersion ({#Version}) there, padded with spaces, which
                are trimmed. The setup's FileVersion is the deliberate constant VersionInfoVersion of
                installer/StreamsPlayer.iss and is not the stamp, so it is not judged.

  One verdict line per assertion, then one summary line:
      release-version: PASS <assertion> - <evidence>
      release-version: FAIL <assertion> - <what was found>
      release-version: PASS (<n> assertions) | FAIL (<k> of <n> assertions) | COULD NOT VERIFY (<reason>)
  Exit 0 when every assertion passed, 1 when one failed, 2 when nothing could be judged (a version that is no
  vYY.MMDD.HHmm instant, no artifact named, or an unexpected error before any failure).

.PARAMETER Version
  The canonical release version, YY.MMDD.HHmm, as release.yml derives it from the tag.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $ExePath,
    [string] $ZipPath,
    [string] $SetupPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verdicts = [System.Collections.Generic.List[bool]]::new()
function Write-Verdict([bool] $Passed, [string] $Assertion, [string] $Detail) {
    $verdicts.Add($Passed)
    Write-Host ("release-version: {0} {1} - {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Assertion, $Detail)
}
function Read-VersionInfo([string] $Path) {
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $Path).Path)
    [pscustomobject]@{
        FileVersion    = [string] $info.FileVersion
        ProductVersion = [string] $info.ProductVersion
        Binary         = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
    }
}
function Test-AppVersion([string] $Label, [string] $Path, [string] $Source) {
    $info = Read-VersionInfo $Path
    if ($info.FileVersion -ceq $Version) { Write-Verdict $true "$Label-file-version" "FileVersion '$($info.FileVersion)' of $Source" }
    else { Write-Verdict $false "$Label-file-version" "FileVersion of $Source is '$($info.FileVersion)', not '$Version'" }
    if ($info.ProductVersion -cmatch ('^' + [regex]::Escape($Version) + '(\+[0-9a-f]{7,40})?$')) {
        Write-Verdict $true "$Label-product-version" "ProductVersion '$($info.ProductVersion)' of $Source"
    }
    else { Write-Verdict $false "$Label-product-version" "ProductVersion of $Source is '$($info.ProductVersion)', not '$Version' (optionally +<commit>)" }
    if ($info.Binary -ceq $binaryVersion) { Write-Verdict $true "$Label-binary-version" "binary file version $($info.Binary) of $Source is the remap of $Version" }
    else { Write-Verdict $false "$Label-binary-version" "binary file version of $Source is $($info.Binary), not $binaryVersion" }
}

$work = $null
try {
    . (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
    try { $null = ConvertFrom-ReleaseTag -Tag "v$Version"; $binaryVersion = ConvertTo-MsixVersion -Version $Version }
    catch { Write-Host "release-version: COULD NOT VERIFY (-Version '$Version' is not a YY.MMDD.HHmm instant: $($_.Exception.Message))"; exit 2 }
    if (-not ($ExePath -or $ZipPath -or $SetupPath)) {
        Write-Host 'release-version: COULD NOT VERIFY (no artifact named - pass -ExePath, -ZipPath or -SetupPath)'
        exit 2
    }

    if ($ExePath) {
        if (Test-Path -LiteralPath $ExePath -PathType Leaf) { Test-AppVersion 'exe' $ExePath $ExePath }
        else { Write-Verdict $false 'exe' "no such file: $ExePath" }
    }

    if ($ZipPath) {
        if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { Write-Verdict $false 'zip' "no such file: $ZipPath" }
        else {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0247-readback-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
            New-Item -ItemType Directory -Path $work -Force | Out-Null
            $extracted = Join-Path $work 'StreamsPlayer.exe'
            $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
            try {
                $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq 'StreamsPlayer.exe' } | Select-Object -First 1
                if ($entry) { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $extracted) }
            }
            finally { $archive.Dispose() }
            if (Test-Path -LiteralPath $extracted -PathType Leaf) {
                Write-Verdict $true 'zip-entry' "StreamsPlayer.exe extracted from $ZipPath"
                Test-AppVersion 'zip' $extracted "StreamsPlayer.exe inside $ZipPath"
            }
            else { Write-Verdict $false 'zip-entry' "$ZipPath holds no StreamsPlayer.exe at its root" }
        }
    }

    if ($SetupPath) {
        if (-not (Test-Path -LiteralPath $SetupPath -PathType Leaf)) { Write-Verdict $false 'setup' "no such file: $SetupPath" }
        else {
            $product = (Read-VersionInfo $SetupPath).ProductVersion.Trim()
            if ($product -ceq $Version) { Write-Verdict $true 'setup-product-version' "ProductVersion '$product' of $SetupPath" }
            else { Write-Verdict $false 'setup-product-version' "ProductVersion of $SetupPath is '$product', not '$Version'" }
        }
    }
}
catch {
    # Nothing past this point was judged. A failure already recorded still outranks it (CHECK-VERDICT 2.14).
    $reason = $_.Exception.Message -replace '\s+', ' '
    if (-not ($verdicts -contains $false)) { Write-Host "release-version: COULD NOT VERIFY ($reason)"; exit 2 }
    Write-Host "release-version: the run stopped early: $reason"
}
finally {
    if ($work -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

$failed = @($verdicts | Where-Object { -not $_ }).Count
if ($failed -eq 0) { Write-Host "release-version: PASS ($($verdicts.Count) assertions)"; exit 0 }
Write-Host "release-version: FAIL ($failed of $($verdicts.Count) assertions)"
exit 1
