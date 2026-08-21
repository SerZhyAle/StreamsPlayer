<#
    SP-0092: builds the per-user setup executable locally, so it can be compiled and tested without
    cutting a release.

    This is the local twin of the "Build installer" step in .github/workflows/release.yml. Both compile
    installer/StreamsPlayer.iss against a staging tree; neither one releases anything. This script never
    tags, uploads, or publishes.

    Usage:
        ./tools/build-installer.ps1
            Publish a self-contained win-x64 tree into artifacts/installer-stage, then compile it.

        ./tools/build-installer.ps1 -SourceDir <path>
            Compile an existing staging tree - for example the release workflow's stage/StreamsPlayer.

        ./tools/build-installer.ps1 -Version 26.0820.1828
            Override the version. Default: the stamp in Directory.Build.props.

    The publish here is deliberately NOT single-file. LibVLCSharp resolves its natives from
    libvlc\win-x64\ beside the executable, and PublishSingleFile does not embed them - a lone
    StreamsPlayer.exe dies at startup. The installer exists precisely to deliver the whole tree.

    Requires Inno Setup 6 (ISCC.exe). It is not part of the .NET toolchain and is not installed by
    build.ps1; the script names the download if it is missing.
#>

[CmdletBinding()]
param(
    # The release version to stamp. Defaults to the stamp in Directory.Build.props.
    [string] $Version,
    # An already-published staging tree to package. Defaults to one this script publishes itself.
    [string] $SourceDir,
    # Where to write the installer. Defaults to artifacts/installer.
    [string] $OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $root 'installer/StreamsPlayer.iss'

if (-not (Test-Path -LiteralPath $script)) {
    throw "Installer script not found: $script"
}

if (-not $Version) {
    $propsPath = Join-Path $root 'Directory.Build.props'
    $props = [xml](Get-Content -LiteralPath $propsPath -Raw)
    $Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw "Could not read <Version> from $propsPath." }
    Write-Host "Version not supplied; using the Directory.Build.props stamp: $Version"
}

if ($Version -notmatch '^\d{2}\.\d{4}\.\d{4}$') {
    throw "Version '$Version' must use the house stamp YY.MMDD.HHmm."
}

if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts/installer' }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ---- the compiler, located before anything expensive -------------------------------------------
# Looked up first on purpose: publishing a self-contained tree takes minutes, and discovering the
# missing compiler afterwards would waste all of it.

# The per-user location is listed first and is not an afterthought: `winget install
# JRSoftware.InnoSetup` installs without elevation by default and lands in %LOCALAPPDATA%\Programs,
# not in either Program Files. Checking only the machine-wide paths reports "not installed" to someone
# who just installed it.
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $iscc) {
    $looked = ($isccCandidates | ForEach-Object { "  $_" }) -join [Environment]::NewLine
    throw @"
ISCC.exe (Inno Setup 6) was not found.

Looked in:
$looked

Install it from https://jrsoftware.org/isdl.php - or with: winget install JRSoftware.InnoSetup
"@
}

Write-Host "Inno Setup: $iscc"

# ---- the staging tree -------------------------------------------------------------------------

if ($SourceDir) {
    if (-not (Test-Path -LiteralPath $SourceDir)) { throw "SourceDir does not exist: $SourceDir" }
    Write-Host "Packaging the supplied staging tree: $SourceDir"
} else {
    $SourceDir = Join-Path $root 'artifacts/installer-stage'
    if (Test-Path -LiteralPath $SourceDir) { Remove-Item -LiteralPath $SourceDir -Recurse -Force }
    New-Item -ItemType Directory -Path $SourceDir -Force | Out-Null

    Write-Host "Publishing a self-contained win-x64 tree into $SourceDir .."
    $appProject = Join-Path $root 'src/StreamsPlayer.App/StreamsPlayer.App.csproj'
    & dotnet publish $appProject `
        -c Release -r win-x64 --self-contained true `
        "-p:Version=$Version" "-p:AssemblyVersion=$Version" `
        "-p:FileVersion=$Version" "-p:InformationalVersion=$Version" `
        -o $SourceDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

    # The release workflow copies these three beside the publish; the installer must carry the same set,
    # and THIRD-PARTY-NOTICES.txt is required inside every distributed package.
    foreach ($file in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $SourceDir -Force
    }
}

$notices = Join-Path $SourceDir 'THIRD-PARTY-NOTICES.txt'
if (-not (Test-Path -LiteralPath $notices)) {
    throw "THIRD-PARTY-NOTICES.txt is missing from the staging tree. Every distributed package must carry it."
}

$nativeDir = Join-Path $SourceDir 'libvlc/win-x64'
if (-not (Test-Path -LiteralPath $nativeDir)) {
    throw "libvlc/win-x64 is missing from the staging tree. The installed application would fail at startup in VideoFrameCaptureService..ctor. Was this published with PublishSingleFile?"
}

# ---- compile ----------------------------------------------------------------------------------

$sourceAbs = (Resolve-Path -LiteralPath $SourceDir).Path
$outputAbs = (Resolve-Path -LiteralPath $OutputDir).Path

Write-Host "Compiling $script .."
& $iscc "/DVersion=$Version" "/DSourceDir=$sourceAbs" "/O$outputAbs" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)." }

$expected = Join-Path $outputAbs "StreamsPlayer-$Version-windows-x64-setup.exe"
if (-not (Test-Path -LiteralPath $expected)) {
    throw "ISCC reported success but the installer is not at $expected. Check OutputBaseFilename in the script."
}

$size = (Get-Item -LiteralPath $expected).Length
$hash = (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash
Write-Host ""
Write-Host "Installer: $expected"
Write-Host ("Size:      {0:N0} bytes ({1:N1} MB)" -f $size, ($size / 1MB))
Write-Host "SHA256:    $hash"
Write-Host ""
Write-Host "This built an installer. It released nothing."
