<#
.SYNOPSIS
  Proves a payload folder carries the console companion of the command line and that it is a console program with no desktop
  framework behind it.

.DESCRIPTION
  SP-0275 (APP-COMMANDS rule 9): StreamsPlayerCli.exe is the executable a caller that pipes runs, so it must be in every
  payload the player ships in - the portable folder, the installer's staging tree and the Store package are all made from
  one publish folder of the application project, which publishes the companion into it - and it must stay what it was
  built to be: a console program that references the platform-neutral core and nothing of the window toolkit. The evidence
  is read from the published files: the executable and its host files are there, its dependency file names no
  window-toolkit assembly and no application assembly, and its runtime configuration names no desktop framework.

  The check is on the folder, so one script serves build.ps1 -Deploy, msix/build-msix.ps1 and release.yml, like the
  pinned-natives check beside it. It prints one PASS line and returns, or throws naming the file and what was found.

.EXAMPLE
  pwsh -NoProfile -File ./scripts/Assert-ConsoleCompanion.ps1 -Folder artifacts/publish/win-x64/Release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Folder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { throw "expected: the payload folder $Folder | actual: missing" }

foreach ($name in 'StreamsPlayerCli.exe', 'StreamsPlayerCli.dll', 'StreamsPlayerCli.deps.json', 'StreamsPlayerCli.runtimeconfig.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $Folder $name) -PathType Leaf)) {
        throw "expected: $name in $Folder | actual: missing (the application project publishes the console companion into its own publish folder)"
    }
}

# A console program that names one of these is no longer the small companion SP-0275 describes.
$forbidden = 'PresentationFramework', 'PresentationCore', 'Microsoft.WindowsDesktop.App', 'System.Windows.Forms'
$dependencies = Get-Content -LiteralPath (Join-Path $Folder 'StreamsPlayerCli.deps.json') -Raw
$runtime = Get-Content -LiteralPath (Join-Path $Folder 'StreamsPlayerCli.runtimeconfig.json') -Raw
foreach ($term in $forbidden) {
    if ($dependencies.Contains($term, [System.StringComparison]::OrdinalIgnoreCase)) { throw "expected: no $term in StreamsPlayerCli.deps.json | actual: found" }
    if ($runtime.Contains($term, [System.StringComparison]::OrdinalIgnoreCase)) { throw "expected: no $term in StreamsPlayerCli.runtimeconfig.json | actual: found" }
}

if ($dependencies -match '"StreamsPlayer\.dll"') { throw 'expected: StreamsPlayerCli.deps.json names no application assembly (StreamsPlayer.dll) | actual: found' }
if (-not $dependencies.Contains('StreamsPlayer.Core', [System.StringComparison]::Ordinal)) { throw 'expected: StreamsPlayerCli.deps.json references StreamsPlayer.Core | actual: absent' }

# Both executables are built from one command line and one stamp into one folder; a folder where they differ was not made by
# one publish, and the companion's `version` would then disagree with the player's.
$player = Join-Path $Folder 'StreamsPlayer.exe'
if (Test-Path -LiteralPath $player -PathType Leaf) {
    $playerVersion = (Get-Item -LiteralPath $player).VersionInfo.ProductVersion
    $companionVersion = (Get-Item -LiteralPath (Join-Path $Folder 'StreamsPlayerCli.exe')).VersionInfo.ProductVersion
    if ($playerVersion -ne $companionVersion) { throw "expected: StreamsPlayerCli.exe carries the version of StreamsPlayer.exe ($playerVersion) | actual: $companionVersion" }
}

Write-Host "console-companion: PASS StreamsPlayerCli.exe is in $Folder with a dependency file that names the core and no window toolkit"
