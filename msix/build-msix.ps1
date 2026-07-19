<#
.SYNOPSIS
  Build a Store-ready full-trust MSIX for StreamPlayer.

.DESCRIPTION
  Publishes the WPF app self-contained for win-x64, copies the product logos,
  fills AppxManifest.xml and packs the resulting application. Use -SelfSign
  only for local testing; Partner Center packages must remain unsigned.
#>
[CmdletBinding()]
param(
    [string] $IdentityName = 'SerZhyAle.StreamPlayer',
    [string] $Publisher = 'CN=SerZhyAle',
    [string] $PublisherDisplayName = 'SerZhyAle',
    [string] $Version,
    [switch] $SelfSign
)

$ErrorActionPreference = 'Stop'
$msix = $PSScriptRoot
$root = Split-Path $msix -Parent
$stage = Join-Path $msix 'stage'
$dist = Join-Path $msix 'dist'

function Find-SdkTool([string] $Name) {
    $tool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64' -Filter $Name -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) { throw "$Name was not found. Install the Windows SDK: winget install Microsoft.WindowsSDK" }
    return $tool.FullName
}

if (-not $Version) {
    $Version = "$((Get-Date).ToUniversalTime().ToString('yy.MMdd.HHmm')).0"
}
if ($Version -notmatch '^\d{2}\.\d{4}\.\d{4}\.0$') { throw 'Version must use YY.MMDD.HHmm.0.' }
$appVersion = $Version.Substring(0, $Version.Length - 2)
[DateTime]::ParseExact($appVersion, 'yy.MMdd.HHmm', [Globalization.CultureInfo]::InvariantCulture) | Out-Null
foreach ($part in $Version.Split('.')) { if ([int]$part -gt 65535) { throw "Version part '$part' exceeds 65535." } }

$makeappx = Find-SdkTool 'makeappx.exe'
if ($SelfSign) { $signtool = Find-SdkTool 'signtool.exe' }

Push-Location $root
try {
    dotnet publish .\src\StreamPlayer.App\StreamPlayer.App.csproj -c Release -r win-x64 --self-contained true --nologo `
        -p:Version=$appVersion -p:AssemblyVersion=$appVersion -p:FileVersion=$appVersion -p:InformationalVersion=$appVersion
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
}
finally { Pop-Location }

$publish = Join-Path $root 'src\StreamPlayer.App\bin\Release\net10.0-windows\win-x64\publish'
if (-not (Test-Path (Join-Path $publish 'StreamPlayer.exe'))) { throw "Published StreamPlayer.exe not found in $publish." }

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $stage 'Assets'), $dist -Force | Out-Null
Copy-Item (Join-Path $publish '*') $stage -Recurse -Force
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt') -Force
$logoSource = Join-Path $root 'assets\msix'
foreach ($logo in 'Square44x44Logo.png', 'Square71x71Logo.png', 'Square150x150Logo.png', 'StoreLogo.png') {
    $source = Join-Path $logoSource $logo
    if (-not (Test-Path $source)) { throw "Product logo not found: $source" }
    Copy-Item $source (Join-Path $stage "Assets\$logo") -Force
}

$manifest = Get-Content (Join-Path $msix 'AppxManifest.xml') -Raw
$manifest = $manifest.Replace('__IDENTITY_NAME__', $IdentityName).Replace('__PUBLISHER__', $Publisher).Replace('__PUBLISHER_DISPLAY_NAME__', $PublisherDisplayName).Replace('__VERSION__', $Version)
Set-Content -Path (Join-Path $stage 'AppxManifest.xml') -Value $manifest -Encoding utf8

$package = Join-Path $dist "StreamPlayer-$appVersion-windows-x64.msix"
& $makeappx pack /o /d $stage /p $package
if ($LASTEXITCODE -ne 0) { throw "makeappx failed (exit $LASTEXITCODE)." }

if ($SelfSign) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName 'StreamPlayer MSIX test certificate' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $pfx = Join-Path $dist 'streamplayer-test.pfx'
    $cer = Join-Path $dist 'streamplayer-test.cer'
    $password = ConvertTo-SecureString -String 'streamplayer-msix-test' -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    & $signtool sign /fd SHA256 /f $pfx /p 'streamplayer-msix-test' $package
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)." }
    Remove-Item $pfx -Force
    Write-Host "Local test package: $package" -ForegroundColor Green
    Write-Host "Trust the certificate as administrator, then install:" -ForegroundColor Yellow
    Write-Host "Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "Add-AppxPackage -Path `"$package`""
}
else { Write-Host "Unsigned Store-ready package: $package" -ForegroundColor Green }
