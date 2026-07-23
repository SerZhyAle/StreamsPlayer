<#
.SYNOPSIS
  Build a Store-ready full-trust MSIX for StreamsPlayer.

.DESCRIPTION
  Publishes the WPF app self-contained for win-x64, copies the product logos,
  fills AppxManifest.xml and packs the resulting application. Use -SelfSign
  only for local testing; Partner Center packages must remain unsigned.
#>
# Defaults are the PERMANENT reserved Partner Center identity for Store ID 9NBTD5SXB8TB.
# Do not change them; every Store update must ship these exact three values. See msix/README.md.
[CmdletBinding()]
param(
    [string] $IdentityName = 'SZA.StreamsPlayer',
    [string] $Publisher = 'CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD',
    [string] $PublisherDisplayName = 'SZA',
    [string] $Version,
    [switch] $SelfSign
)

$ErrorActionPreference = 'Stop'
$msix = $PSScriptRoot
$root = Split-Path $msix -Parent
$stage = Join-Path $msix 'stage'
$dist = Join-Path $msix 'dist'

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

if (-not $Version) {
    $Version = "$((Get-Date).ToUniversalTime().ToString('yy.MMdd.HHmm')).0"
}
if ($Version -notmatch '^\d{2}\.\d{4}\.\d{4}\.0$') { throw 'Version must use YY.MMDD.HHmm.0.' }
$appVersion = $Version.Substring(0, $Version.Length - 2)
[DateTime]::ParseExact($appVersion, 'yy.MMdd.HHmm', [Globalization.CultureInfo]::InvariantCulture) | Out-Null
# The MSIX Identity Version schema forbids leading zeros in any part (e.g. 26.0723.0957.0 is
# rejected), so convert each component to an integer: 26.0723.0957.0 -> 26.723.957.0. This is
# still monotonic and unique per minute (MMDD and HHmm as ints preserve ordering).
$msixVersion = ($Version.Split('.') | ForEach-Object { [int]$_ }) -join '.'
foreach ($part in $msixVersion.Split('.')) { if ([int]$part -gt 65535) { throw "Version part '$part' exceeds 65535." } }

$makeappx = Find-SdkTool 'makeappx.exe'
if ($SelfSign) { $signtool = Find-SdkTool 'signtool.exe' }

Push-Location $root
try {
    dotnet publish .\src\StreamsPlayer.App\StreamsPlayer.App.csproj -c Release -r win-x64 --self-contained true --nologo `
        -p:Version=$appVersion -p:AssemblyVersion=$appVersion -p:FileVersion=$appVersion -p:InformationalVersion=$appVersion
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
}
finally { Pop-Location }

# The App targets a versioned Windows TFM (net10.0-windows10.0.19041.0), so resolve the
# publish dir instead of hardcoding the moniker — robust to TFM changes.
$publish = Get-ChildItem (Join-Path $root 'src\StreamsPlayer.App\bin\Release') -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'win-x64\publish' } |
    Where-Object { Test-Path (Join-Path $_ 'StreamsPlayer.exe') } |
    Sort-Object -Descending | Select-Object -First 1
if (-not $publish) { throw "Published StreamsPlayer.exe not found under src\StreamsPlayer.App\bin\Release\*\win-x64\publish." }

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $stage 'Assets'), $dist -Force | Out-Null
Copy-Item (Join-Path $publish '*') $stage -Recurse -Force
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt') -Force
Copy-Item (Join-Path $msix 'THIRD-PARTY-NOTICES.txt') (Join-Path $stage 'THIRD-PARTY-NOTICES.txt') -Force
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
