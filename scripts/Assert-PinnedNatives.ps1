<#
.SYNOPSIS
  Proves a payload folder runs the native media engine this repository pins.

.DESCRIPTION
  SP-0156 (T-10): one check, one home, applied to everything that ships. The pin is the VideoLAN.LibVLC.Windows
  package version in the App project; the evidence is byte equality with that package's own x64 natives, with
  the file version printed beside it (the DLL says 3.0.23 for package 3.0.23.1, the last part being the
  package's own revision).

  SP-0133: these folders are shared with other tools - FastMediaSorter LITE keeps its own LibVLCSharp.dll and
  libvlc\ tree in their roots - so the comparison is always against a folder this build produced, never a
  shared root.

  SP-0157: the reference natives are found where NuGet itself put them, never at a guessed default location.
  First the package folders the restore of this project recorded (NUGET_PACKAGES, a NuGet.config
  globalPackagesFolder and fallback folders all land there), then NuGet's own answer for the global folder.

.EXAMPLE
  pwsh -NoProfile -File ./scripts/Assert-PinnedNatives.ps1 -Folder artifacts/publish/win-x64/Release -ProjectPath src/StreamsPlayer.App/StreamsPlayer.App.csproj
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Folder,
    [Parameter(Mandatory)] [string] $ProjectPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = [xml] (Get-Content -LiteralPath $ProjectPath -Raw)
$pinned = @($project.SelectNodes("//PackageReference[@Include='VideoLAN.LibVLC.Windows']") |
    ForEach-Object { $_.GetAttribute('Version') })
if ($pinned.Count -ne 1) { throw "Could not read the pinned VideoLAN.LibVLC.Windows version from $ProjectPath." }
$pinned = $pinned[0]

$candidates = @()
$assets = Join-Path (Split-Path $ProjectPath -Parent) 'obj\project.assets.json'
if (Test-Path -LiteralPath $assets) {
    $recorded = (Get-Content -LiteralPath $assets -Raw | ConvertFrom-Json).packageFolders
    if ($recorded) { $candidates += @($recorded.PSObject.Properties.Name) }
}
$locals = & dotnet nuget locals global-packages --list 2>$null
if ($LASTEXITCODE -eq 0) {
    $candidates += @($locals | ForEach-Object { if ("$_" -match '^\s*global-packages:\s*(.+?)\s*$') { $Matches[1] } })
}
$packageNatives = $candidates |
    ForEach-Object { Join-Path $_ "videolan.libvlc.windows\$pinned\build\x64" } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'libvlc.dll') } |
    Select-Object -First 1
if (-not $packageNatives) {
    $searched = if ($candidates) { ($candidates | Select-Object -Unique) -join '; ' } else { 'no package folder could be resolved' }
    throw "cannot verify: VideoLAN.LibVLC.Windows $pinned natives not found in the NuGet package folders ($searched); the payload in $Folder cannot be compared byte for byte. Run a restore and try again."
}
$expectedFileVersion = ($pinned.Split('.') | Select-Object -First 3) -join '.'

foreach ($name in 'libvlc.dll', 'libvlccore.dll') {
    $deployed = Join-Path $Folder "libvlc\win-x64\$name"
    if (-not (Test-Path -LiteralPath $deployed)) { throw "expected: $deployed | actual: missing" }
    $version = (Get-Item -LiteralPath $deployed).VersionInfo
    $actualFileVersion = "$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart)"
    if ($actualFileVersion -ne $expectedFileVersion) {
        throw "expected: $name $expectedFileVersion (package $pinned) | actual: $actualFileVersion in $Folder"
    }

    $reference = Join-Path $packageNatives $name
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "cannot verify: package $pinned has no $reference; the payload $name cannot be compared byte for byte."
    }
    if ((Get-FileHash -LiteralPath $deployed).Hash -ne (Get-FileHash -LiteralPath $reference).Hash) {
        throw "expected: $name identical to package $pinned | actual: different bytes in $Folder"
    }
    Write-Host "    expected: $name $expectedFileVersion (package $pinned), bytes of $reference | actual: $actualFileVersion, identical" -ForegroundColor Green
}
