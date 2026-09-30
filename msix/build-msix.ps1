<#
.SYNOPSIS
  Build a Store-ready full-trust MSIX for StreamsPlayer.

.DESCRIPTION
  Publishes the WPF app self-contained for win-x64, copies the product logos,
  fills AppxManifest.xml and packs the resulting application. Use -SelfSign
  only for local testing; Partner Center packages must remain unsigned.

  SP-0156: the package is built only from a clean working tree whose HEAD is
  exactly at a vYY.MMDD.HHmm tag, and the version comes from that tag - the
  script refuses a dirty tree and refuses a HEAD off the tag, because the
  Store once shipped a version no tag carries (26.0806.2225).
#>
# Defaults are the PERMANENT reserved Partner Center identity for Store ID 9NBTD5SXB8TB.
# Do not change them; every Store update must ship these exact three values. See msix/README.md.
[CmdletBinding()]
param(
    [string] $IdentityName = 'SZA.StreamsPlayer',
    [string] $Publisher = 'CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD',
    [string] $PublisherDisplayName = 'SZA',
    [switch] $SelfSign
)

$ErrorActionPreference = 'Stop'
$msix = $PSScriptRoot
$root = Split-Path $msix -Parent
$stage = Join-Path $msix 'stage'
$dist = Join-Path $msix 'dist'
# SP-0133: the package is built from a publish folder of its own, emptied first. It used to publish into the
# shared bin\Release\<tfm>\win-x64\publish folder, which no publish ever cleans, and then pack whichever such
# folder sorted newest - so a file dropped by an earlier build, or a folder left by an older target framework,
# could ship to the Store.
$publish = Join-Path $root 'artifacts\msix-publish'

function Find-SdkTool([string] $Name) {
    # Enumerate versioned SDK bin dirs and probe <version>\x64\<tool>. A single
    # Get-ChildItem '...\bin\*\x64' -Filter matches nothing (mid-path wildcard + -Filter),
    # so resolve the version directories explicitly, newest first.
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $tool = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+' } |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($tool) { return $tool }
    }
    throw "$Name was not found. Install the Windows SDK: winget install Microsoft.WindowsSDK"
}

# SP-0156 (T-03): the version comes from the version tag at HEAD, never from the clock and never from a
# hand-typed value, and the tree is packed only when it is exactly that tag - the Store has already
# shipped a version no tag carries (26.0806.2225). The checklist builds the package after the tag exists
# locally and before it is pushed.
Push-Location $root
try {
    $dirty = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    if ($dirty) {
        throw "The working tree is not clean - the package would pack edits no commit carries. Build the Store package from a clean checkout at the version tag: git worktree add <dir> v<version> (msix/README.md)."
    }
    $tag = git describe --exact-match --tags HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or $tag -cnotmatch '^v\d{2}\.\d{4}\.\d{4}$') {
        throw "HEAD is not exactly at a vYY.MMDD.HHmm tag ($((git describe --tags --always HEAD))). The Store package takes its version from the tag; tag this commit first (the checklist tags before the package is built and before the tag is pushed)."
    }
}
finally { Pop-Location }
$appVersion = $tag.Substring(1)
[DateTime]::ParseExact($appVersion, 'yy.MMdd.HHmm', [Globalization.CultureInfo]::InvariantCulture) | Out-Null
# The MSIX Identity Version schema forbids leading zeros in any part (e.g. 26.0723.0957.0 is
# rejected), so convert each component to an integer: 26.0723.0957.0 -> 26.723.957.0. This is
# still monotonic and unique per minute (MMDD and HHmm as ints preserve ordering).
$msixVersion = ($appVersion.Split('.') | ForEach-Object { [int]$_ }) -join '.'
foreach ($part in $msixVersion.Split('.')) { if ([int]$part -gt 65535) { throw "Version part '$part' exceeds 65535." } }

$makeappx = Find-SdkTool 'makeappx.exe'
if ($SelfSign) { $signtool = Find-SdkTool 'signtool.exe' }

if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
Push-Location $root
try {
    dotnet publish .\src\StreamsPlayer.App\StreamsPlayer.App.csproj -c Release -r win-x64 --self-contained true --nologo `
        -o $publish `
        -p:Version=$appVersion -p:AssemblyVersion=$appVersion -p:FileVersion=$appVersion -p:InformationalVersion=$appVersion
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
}
finally { Pop-Location }
if (-not (Test-Path -LiteralPath (Join-Path $publish 'StreamsPlayer.exe'))) { throw "Published StreamsPlayer.exe not found in $publish." }

# SP-0156 (T-10): the Store package carries the same pinned-natives evidence as every other channel -
# the native tree present and byte-identical to the package the project pins, not merely present.
& (Join-Path $root 'scripts/Assert-PinnedNatives.ps1') -Folder $publish -ProjectPath (Join-Path $root 'src/StreamsPlayer.App/StreamsPlayer.App.csproj')

# SP-0157: the stage is overlaid below, so whatever survives this removal is packed. A locked file from an earlier
# stage fails the build here rather than riding into the Store package.
if (Test-Path -LiteralPath $stage) {
    try { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Stop }
    catch { throw "Cannot empty the stage folder $stage - a leftover would be packed: $($_.Exception.Message)" }
    if (Test-Path -LiteralPath $stage) { throw "Cannot empty the stage folder $stage - it still exists after removal." }
}
New-Item -ItemType Directory -Path (Join-Path $stage 'Assets'), $dist -Force | Out-Null
Copy-Item (Join-Path $publish '*') $stage -Recurse -Force
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt') -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') (Join-Path $stage 'THIRD-PARTY-NOTICES.txt') -Force
$logoSource = Join-Path $root 'assets\msix'
foreach ($logo in 'Square44x44Logo.png', 'Square71x71Logo.png', 'Square150x150Logo.png', 'StoreLogo.png') {
    $source = Join-Path $logoSource $logo
    if (-not (Test-Path $source)) { throw "Product logo not found: $source" }
    Copy-Item $source (Join-Path $stage "Assets\$logo") -Force
}

$manifest = Get-Content (Join-Path $msix 'AppxManifest.xml') -Raw
$manifest = $manifest.Replace('__IDENTITY_NAME__', $IdentityName).Replace('__PUBLISHER__', $Publisher).Replace('__PUBLISHER_DISPLAY_NAME__', $PublisherDisplayName).Replace('__VERSION__', $msixVersion)
Set-Content -Path (Join-Path $stage 'AppxManifest.xml') -Value $manifest -Encoding utf8

$package = Join-Path $dist "StreamsPlayer-$appVersion-windows-x64.msix"
& $makeappx pack /o /d $stage /p $package
if ($LASTEXITCODE -ne 0) { throw "makeappx failed (exit $LASTEXITCODE)." }

if ($SelfSign) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName 'StreamsPlayer MSIX test certificate' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $pfx = Join-Path $dist 'streamsplayer-test.pfx'
    $cer = Join-Path $dist 'streamsplayer-test.cer'
    $password = ConvertTo-SecureString -String 'streamsplayer-msix-test' -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    & $signtool sign /fd SHA256 /f $pfx /p 'streamsplayer-msix-test' $package
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)." }
    Remove-Item $pfx -Force
    Write-Host "Local test package: $package" -ForegroundColor Green
    Write-Host "Trust the certificate as administrator, then install:" -ForegroundColor Yellow
    Write-Host "Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "Add-AppxPackage -Path `"$package`""
}
else { Write-Host "Unsigned Store-ready package: $package" -ForegroundColor Green }
