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

$solutionPath = Join-Path $PSScriptRoot 'StreamPlayer.sln'
$appProjectPath = Join-Path $PSScriptRoot 'src\StreamPlayer.App\StreamPlayer.App.csproj'
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

    Write-Host "StreamPlayer — .NET SDK $sdkVersion, конфигурация $Configuration" -ForegroundColor Green

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
            (Join-Path $localOutputPath 'StreamPlayer.exe')
            $localDeployPaths | ForEach-Object { Join-Path $_ 'StreamPlayer.exe' }
        )

        foreach ($process in @(Get-Process -Name 'StreamPlayer' -ErrorAction SilentlyContinue)) {
            $processPath = try { $process.Path } catch { $null }
            if ($processPath -and $targetExePaths -contains $processPath) {
                Write-Host "Stopping local StreamPlayer: $processPath" -ForegroundColor Cyan
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

        $localExePath = Join-Path $localOutputPath 'StreamPlayer.exe'
        if (-not (Test-Path -LiteralPath $localExePath -PathType Leaf)) {
            throw "Local single-file executable was not created: $localExePath"
        }

        foreach ($deployPath in $localDeployPaths) {
            New-Item -ItemType Directory -Path $deployPath -Force | Out-Null
            $targetExePath = Join-Path $deployPath 'StreamPlayer.exe'
            Copy-Item -LiteralPath $localExePath -Destination $targetExePath -Force
            Write-Host "Local build deployed: $targetExePath" -ForegroundColor Green
        }
    }

    if ($Run) {
        Write-Host 'Запуск StreamPlayer...' -ForegroundColor Green
        Invoke-DotNet @(
            'run',
            '--project', $appProjectPath,
            '--configuration', $Configuration,
            '--no-build',
            '--no-restore'
        )
    }
}
finally {
    Pop-Location
}
