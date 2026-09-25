[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $Run,
    [switch] $Test,
    [switch] $Clean,
    [switch] $Publish,
    [switch] $Deploy = $true,
    [switch] $NoRestore,

    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [string] $OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$solutionPath = Join-Path $PSScriptRoot 'StreamsPlayer.sln'
$appProjectPath = Join-Path $PSScriptRoot 'src\StreamsPlayer.App\StreamsPlayer.App.csproj'
# SP-0133: these folders are shared with other tools - FastMediaSorter LITE keeps its own LibVLCSharp.dll and
# libvlc\ tree in their roots - so StreamsPlayer is installed into a StreamsPlayer\ folder of its own under each
# and never writes to the shared root. Before SP-0133 only a single-file StreamsPlayer.exe was copied into the
# root, where it ran on whatever libvlc\ the other tool had left there instead of the version this repo pins.
$localDeployRoots = @(
    'C:\GD\i',
    'C:\GD\tc\SZA\_APP'
)
$localDeployFolderName = 'StreamsPlayer'

if ($Deploy) {
    if ($PSBoundParameters.ContainsKey('Configuration') -and $Configuration -ne 'Release') {
        throw 'Local deployment supports only the Release configuration.'
    }
    if ($Runtime -ne 'win-x64') {
        throw 'Local deployment supports only win-x64 to avoid replacing the desktop build with an incompatible executable.'
    }

    $Configuration = 'Release'
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

# SP-0133: proves a published or deployed folder runs the native engine this repo pins. The pin is the
# VideoLAN.LibVLC.Windows package version in the App project; the evidence is byte equality with that package's
# own x64 natives, with the file version printed beside it (the DLL says 3.0.23 for package 3.0.23.1, the last
# part being the package's own revision).
function Assert-PinnedNatives {
    param(
        [Parameter(Mandatory)] [string] $Folder,
        [Parameter(Mandatory)] [string] $ProjectPath
    )

    $project = [xml] (Get-Content -LiteralPath $ProjectPath -Raw)
    $pinned = @($project.SelectNodes("//PackageReference[@Include='VideoLAN.LibVLC.Windows']") |
        ForEach-Object { $_.GetAttribute('Version') })
    if ($pinned.Count -ne 1) { throw "Could not read the pinned VideoLAN.LibVLC.Windows version from $ProjectPath." }
    $pinned = $pinned[0]

    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
    $packageNatives = Join-Path $packages "videolan.libvlc.windows\$pinned\build\x64"
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
        if (Test-Path -LiteralPath $reference) {
            if ((Get-FileHash -LiteralPath $deployed).Hash -ne (Get-FileHash -LiteralPath $reference).Hash) {
                throw "expected: $name identical to package $pinned | actual: different bytes in $Folder"
            }
        }
        else {
            Write-Host "    (package cache has no $reference; file version checked, bytes not compared)" -ForegroundColor Yellow
        }
        Write-Host "    expected: $name $expectedFileVersion (package $pinned) | actual: $actualFileVersion" -ForegroundColor Green
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install the .NET 10 SDK and try again.'
}

Push-Location $PSScriptRoot
try {
    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not determine the .NET SDK version.'
    }

    $sdkMajor = [int]($sdkVersion.Split('.')[0])
    if ($sdkMajor -lt 10) {
        throw ".NET 10 SDK or newer is required. Found version: $sdkVersion."
    }

    Write-Host "StreamsPlayer - .NET SDK $sdkVersion, configuration $Configuration" -ForegroundColor Green

    if ($Clean) {
        Invoke-DotNet @('clean', $solutionPath, '--configuration', $Configuration)
    }

    if (-not $NoRestore) {
        Invoke-DotNet @('restore', $solutionPath)
    }

    Invoke-DotNet @(
        'build',
        $solutionPath,
        '--configuration', $Configuration,
        '--no-restore'
    )

    if ($Test) {
        Invoke-DotNet @(
            'test',
            $solutionPath,
            '--configuration', $Configuration,
            '--no-build',
            '--no-restore'
        )
    }

    if ($Publish) {
        if ([string]::IsNullOrWhiteSpace($OutputPath)) {
            $OutputPath = Join-Path $PSScriptRoot "artifacts\publish\$Runtime\$Configuration"
        }

        if (-not $NoRestore) {
            Invoke-DotNet @(
                'restore',
                $appProjectPath,
                '--runtime', $Runtime
            )
        }

        Invoke-DotNet @(
            'publish',
            $appProjectPath,
            '--configuration', $Configuration,
            '--runtime', $Runtime,
            '--self-contained', 'false',
            '--output', $OutputPath,
            '--no-restore'
        )
        Write-Host "Готовая публикация: $OutputPath" -ForegroundColor Green
    }

    if ($Deploy) {
        # SP-0133: the local install is the release's payload - the same self-contained folder publish that
        # release.yml ships, the native LibVLC tree included - mirrored into a folder of its own. A single-file
        # executable carries no libvlc\ (the natives are content files, not bundled), so it ran on whatever
        # native tree the target folder happened to hold, and died at startup in a clean one (SP-0119).
        $localOutputPath = Join-Path $PSScriptRoot "artifacts\local\$Runtime"
        $deployTargets = @($localDeployRoots | ForEach-Object { Join-Path $_ $localDeployFolderName })
        $legacyExePaths = @($localDeployRoots | ForEach-Object { Join-Path $_ 'StreamsPlayer.exe' })

        # Only the copies this deploy replaces are closed: one running from the publish folder, from a target
        # folder, or from a pre-SP-0133 root executable this deploy removes. Any other StreamsPlayer is left alone.
        $replacedFolders = @($localOutputPath) + $deployTargets
        foreach ($process in @(Get-Process -Name 'StreamsPlayer' -ErrorAction SilentlyContinue)) {
            $processPath = try { $process.Path } catch { $null }
            if (-not $processPath) { continue }
            $insideReplaced = @($replacedFolders | Where-Object {
                $processPath.StartsWith($_.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
            }).Count -gt 0
            if ($insideReplaced -or $legacyExePaths -contains $processPath) {
                Write-Host "Stopping local StreamsPlayer: $processPath" -ForegroundColor Cyan
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit(5000) | Out-Null
            }
        }

        if (-not $NoRestore) {
            Invoke-DotNet @(
                'restore',
                $appProjectPath,
                '--runtime', $Runtime
            )
        }

        # Emptied first, so nothing an earlier publish left behind can be mirrored out.
        if (Test-Path -LiteralPath $localOutputPath) { Remove-Item -LiteralPath $localOutputPath -Recurse -Force }
        # The release.yml publish, minus the version stamp only a tag supplies.
        Invoke-DotNet @(
            'publish',
            $appProjectPath,
            '--configuration', 'Release',
            '--runtime', $Runtime,
            '--self-contained', 'true',
            '--output', $localOutputPath,
            '--no-restore'
        )

        $localExePath = Join-Path $localOutputPath 'StreamsPlayer.exe'
        if (-not (Test-Path -LiteralPath $localExePath -PathType Leaf)) {
            throw "Published executable was not created: $localExePath"
        }
        Assert-PinnedNatives -Folder $localOutputPath -ProjectPath $appProjectPath

        foreach ($target in $deployTargets) {
            # /MIR deletes whatever the source lacks, so it is only ever pointed at a folder of our own name.
            if ((Split-Path $target -Leaf) -ne $localDeployFolderName) { throw "Refusing to mirror into $target." }
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            & robocopy $localOutputPath $target /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE) mirroring into $target." }
            $global:LASTEXITCODE = 0 # robocopy's 1-7 are success codes; do not let one become this script's exit code
            Assert-PinnedNatives -Folder $target -ProjectPath $appProjectPath
            Write-Host "Local build deployed: $(Join-Path $target 'StreamsPlayer.exe')" -ForegroundColor Green
        }

        # The owner's decision (SP-0133): the pre-SP-0133 root executable is removed rather than left to run on
        # the other tool's natives. Only that one file - nothing else in the shared root is ours.
        foreach ($legacy in $legacyExePaths) {
            if (Test-Path -LiteralPath $legacy -PathType Leaf) {
                Remove-Item -LiteralPath $legacy -Force
                Write-Host "Removed the old single-file copy: $legacy (shortcuts to it must point into $localDeployFolderName\)" -ForegroundColor Yellow
            }
        }
    }

    if ($Run) {
        Write-Host 'Launching StreamsPlayer...' -ForegroundColor Green
        # Interactive launch: the app's own exit code must not be treated as a build failure.
        # LibVLC native teardown can return a non-zero code on a normal close; surface it, do not throw.
        $runArgs = @(
            'run',
            '--project', $appProjectPath,
            '--configuration', $Configuration,
            '--no-build',
            '--no-restore'
        )
        Write-Host "dotnet $($runArgs -join ' ')" -ForegroundColor Cyan
        & dotnet @runArgs
        $appExitCode = $LASTEXITCODE
        if ($appExitCode -ne 0) {
            Write-Host "StreamsPlayer exited with code $appExitCode (usually a native LibVLC teardown, not a build failure)." -ForegroundColor Yellow
        }
        exit $appExitCode
    }
}
finally {
    Pop-Location
}
