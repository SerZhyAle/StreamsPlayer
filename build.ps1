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
$localDeployPaths = @(
    'C:\GD\i',
    'C:\GD\tc\SZA\_APP'
)

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
        throw "dotnet завершился с кодом $LASTEXITCODE."
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Не найден .NET SDK. Установите .NET 10 SDK и повторите запуск.'
}

Push-Location $PSScriptRoot
try {
    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Не удалось определить версию .NET SDK.'
    }

    $sdkMajor = [int]($sdkVersion.Split('.')[0])
    if ($sdkMajor -lt 10) {
        throw "Требуется .NET 10 SDK или новее. Найдена версия: $sdkVersion."
    }

    Write-Host "StreamsPlayer — .NET SDK $sdkVersion, конфигурация $Configuration" -ForegroundColor Green

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
        $localOutputPath = Join-Path $PSScriptRoot "artifacts\local\$Runtime"
        $targetExePaths = @(
            (Join-Path $localOutputPath 'StreamsPlayer.exe')
            $localDeployPaths | ForEach-Object { Join-Path $_ 'StreamsPlayer.exe' }
        )

        foreach ($process in @(Get-Process -Name 'StreamsPlayer' -ErrorAction SilentlyContinue)) {
            $processPath = try { $process.Path } catch { $null }
            if ($processPath -and $targetExePaths -contains $processPath) {
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

        Invoke-DotNet @(
            'publish',
            $appProjectPath,
            '--configuration', 'Release',
            '--runtime', $Runtime,
            '--self-contained', 'true',
            '--output', $localOutputPath,
            '--no-restore',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:DebugType=None',
            '-p:DebugSymbols=false'
        )

        $localExePath = Join-Path $localOutputPath 'StreamsPlayer.exe'
        if (-not (Test-Path -LiteralPath $localExePath -PathType Leaf)) {
            throw "Local single-file executable was not created: $localExePath"
        }

        foreach ($deployPath in $localDeployPaths) {
            New-Item -ItemType Directory -Path $deployPath -Force | Out-Null
            $targetExePath = Join-Path $deployPath 'StreamsPlayer.exe'
            Copy-Item -LiteralPath $localExePath -Destination $targetExePath -Force
            Write-Host "Local build deployed: $targetExePath" -ForegroundColor Green
        }
    }

    if ($Run) {
        Write-Host 'Запуск StreamsPlayer...' -ForegroundColor Green
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
            Write-Host "StreamsPlayer завершился с кодом $appExitCode (обычно нативное завершение LibVLC, не ошибка сборки)." -ForegroundColor Yellow
        }
        exit $appExitCode
    }
}
finally {
    Pop-Location
}
