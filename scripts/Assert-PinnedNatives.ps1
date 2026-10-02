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

# SP-0183: compare the whole libvlc\win-x64 tree (plugins included) against the pinned package
$deployedLibVlcPath = Join-Path $Folder "libvlc\win-x64"
if (-not (Test-Path -LiteralPath $deployedLibVlcPath)) { throw "expected: $deployedLibVlcPath | actual: missing" }

$referenceRoot = (Resolve-Path -LiteralPath $packageNatives).Path
$deployedRoot = (Resolve-Path -LiteralPath $deployedLibVlcPath).Path

# The package's x64 folder also holds include\ and the import libraries, which the package's own targets never
# copy. The shipped set is exactly what VideoLAN.LibVLC.Windows.targets includes by default:
# libvlc.*, libvlccore.*, hrtfs\**, lua\**, plugins\**.
function Get-ShippedRelativePaths([string] $Root) {
    $top = @(Get-ChildItem -LiteralPath $Root -File | Where-Object { $_.Name -like 'libvlc.*' -or $_.Name -like 'libvlccore.*' })
    $tree = foreach ($name in 'hrtfs', 'lua', 'plugins') {
        $dir = Join-Path $Root $name
        if (Test-Path -LiteralPath $dir) { Get-ChildItem -LiteralPath $dir -Recurse -File }
    }
    @($top) + @($tree) | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName) }
}

$referenceRelative = @(Get-ShippedRelativePaths $referenceRoot)
if ($referenceRelative.Count -eq 0) { throw "cannot verify: package $pinned natives at $referenceRoot hold no shipped files." }
$deployedRelative = @(Get-ChildItem -LiteralPath $deployedRoot -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($deployedRoot, $_.FullName) })

$extraFiles = @($deployedRelative | Where-Object { $_ -notin $referenceRelative })
$missingFiles = @($referenceRelative | Where-Object { $_ -notin $deployedRelative })

if ($extraFiles.Count -gt 0) {
    throw "found $($extraFiles.Count) extra file(s) in deployed libvlc\win-x64: $($extraFiles -join ', ') compared to package ${pinned}"
}
if ($missingFiles.Count -gt 0) {
    throw "found $($missingFiles.Count) missing file(s) in deployed libvlc\win-x64: $($missingFiles -join ', ') compared to package ${pinned}"
}

# Compare each file byte for byte
$fileMismatches = @()
foreach ($relativePath in $referenceRelative) {
    $refHash = (Get-FileHash -LiteralPath (Join-Path $referenceRoot $relativePath) -Algorithm SHA256).Hash
    $depHash = (Get-FileHash -LiteralPath (Join-Path $deployedRoot $relativePath) -Algorithm SHA256).Hash
    if ($refHash -ne $depHash) { $fileMismatches += $relativePath }
}

if ($fileMismatches.Count -gt 0) {
    throw "found $($fileMismatches.Count) file(s) with different bytes in deployed libvlc\win-x64 compared to package ${pinned}: $($fileMismatches -join ', ')"
}
$referenceFiles = $referenceRelative

# Verify version on the key DLLs
$libvlcDll = Join-Path $deployedLibVlcPath "libvlc.dll"
if (Test-Path -LiteralPath $libvlcDll) {
    $version = (Get-Item -LiteralPath $libvlcDll).VersionInfo
    $actualFileVersion = "$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart)"
    if ($actualFileVersion -ne $expectedFileVersion) {
        throw "expected: libvlc.dll $expectedFileVersion (package $pinned) | actual: $actualFileVersion in $Folder"
    }
    Write-Host "    libvlc.dll version: $actualFileVersion (package $pinned)" -ForegroundColor Green
}

Write-Host "    libvlc\win-x64 tree: $($referenceFiles.Count) files compared, identical to package $pinned" -ForegroundColor Green
