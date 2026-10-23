#requires -Version 7.0
<#
.SYNOPSIS
  SP-0238: prove the committed msix/AppxManifest.xml template still packs, from any tree state.

.DESCRIPTION
  build-msix.ps1 is the only other thing that runs makeappx over the template, and it refuses a dirty tree and a HEAD
  off a tag - so a schema error in the template stays invisible until the day a Store package is cut (the .spres file
  association carried two invalid attributes from SP-0196 until SP-0238). This gate closes that gap: it fills the
  template the way build-msix.ps1 does, stages it with a placeholder executable and the product logos, and runs the
  Windows SDK's own `makeappx pack` over it. The SDK is the authority on the manifest schema; nothing here restates it.

    packs        makeappx accepts the filled template (exit 0)
    placeholders no __PLACEHOLDER__ is left after the fill (a new placeholder needs this script and build-msix.ps1)
    association  the staged manifest still declares the .spres file type association (SP-0196)
    control      the same stage with an attribute the schema does not define is refused - the gate is not vacuous

  Exit codes (CHECK-VERDICT): 0 passed, 1 a check failed, 2 could not verify (no Windows SDK, no built Core assembly).
  The last line is `msix-manifest: PASS|FAIL|COULD NOT VERIFY (..)`.

.PARAMETER Configuration
  Which build of StreamsPlayer.Core the language list is read from. Unpinned, the most recently built one.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# Same probe as build-msix.ps1: versioned SDK bin dirs, newest first, <version>\x64\<tool>.
function Find-SdkTool([string] $Name) {
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")
    foreach ($binRoot in $roots) {
        if (-not (Test-Path -LiteralPath $binRoot)) { continue }
        $tool = Get-ChildItem -LiteralPath $binRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+' } |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($tool) { return $tool }
    }
    return $null
}

$makeappx = Find-SdkTool 'makeappx.exe'
if (-not $makeappx) {
    Write-Host 'msix-manifest: the Windows SDK (makeappx.exe) was not found - winget install Microsoft.WindowsSDK'
    Write-Host 'msix-manifest: COULD NOT VERIFY (no makeappx)'
    exit 2
}

. (Join-Path $root 'tools/InterfaceLanguages.ps1')
try {
    $pinned = if ($Configuration) { @{ Configuration = $Configuration } } else { @{} }
    $languageResources = (Get-InterfaceLanguages @pinned | ForEach-Object { '<Resource Language="' + $_.ListingCode + '" />' }) -join ''
}
catch {
    Write-Host "msix-manifest: the interface language list could not be read: $($_.Exception.Message)"
    Write-Host 'msix-manifest: COULD NOT VERIFY (no built StreamsPlayer.Core)'
    exit 2
}

$failures = [System.Collections.Generic.List[string]]::new()
$checks = 0
function Check([bool] $Condition, [string] $Label, [string] $Detail = '') {
    $script:checks++
    if ($Condition) { Write-Host "msix-manifest: PASS $Label" }
    else {
        $script:failures.Add($Label)
        Write-Host "msix-manifest: FAIL $Label"
        if ($Detail) { Write-Host $Detail }
    }
}

# The values only need to be schema-valid; the reserved identity is judged by Assert-MsixPackage.ps1 on the real package.
function Get-FilledManifest {
    $text = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
    $text = $text.Replace('__LANGUAGE_RESOURCES__', $languageResources)
    $text.Replace('__IDENTITY_NAME__', 'SZA.StreamsPlayer').Replace('__PUBLISHER__', 'CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD').Replace('__PUBLISHER_DISPLAY_NAME__', 'SZA').Replace('__VERSION__', '26.1.1.0')
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0238-msix-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    function New-Stage([string] $Name, [string] $Manifest) {
        $stage = Join-Path $work $Name
        New-Item -ItemType Directory -Path (Join-Path $stage 'Assets') -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $stage 'StreamsPlayer.exe'), 'MZ')
        foreach ($logo in 'Square44x44Logo.png', 'Square71x71Logo.png', 'Square150x150Logo.png', 'StoreLogo.png') {
            Copy-Item -LiteralPath (Join-Path $root "assets/msix/$logo") -Destination (Join-Path $stage "Assets/$logo") -Force
        }
        [System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $Manifest, [System.Text.UTF8Encoding]::new($false))
        $stage
    }
    function Invoke-Pack([string] $Stage, [string] $Name) {
        $package = Join-Path $work "$Name.msix"
        $output = & $makeappx pack /o /d $Stage /p $package 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ Exit = $LASTEXITCODE; Output = ($output -join "`n") }
    }

    $filled = Get-FilledManifest
    Check ($filled -notmatch '__[A-Z_]+__') 'placeholders: none left after the fill' ($filled | Select-String '__[A-Z_]+__' -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique | Out-String)

    $stage = New-Stage 'template' $filled
    $pack = Invoke-Pack $stage 'template'
    Check ($pack.Exit -eq 0) 'packs: makeappx accepts the filled template' "makeappx exit $($pack.Exit):`n$($pack.Output)"

    $xml = [xml] $filled
    $association = $xml.SelectNodes("//*[local-name()='FileType']") | Where-Object { $_.InnerText -ceq '.spres' }
    Check (@($association).Count -eq 1) 'association: the manifest declares the .spres file type exactly once'

    # Negative control: an attribute no schema defines (the shape of the SP-0238 defect) must stop the pack.
    $broken = $filled -replace '(<uap:FileTypeAssociation Name="spres")', '$1 AllowOn="desktop"'
    Check ($broken -cne $filled) 'control: the broken fixture differs from the template'
    $controlPack = Invoke-Pack (New-Stage 'control' $broken) 'control'
    # The refusal must name the injected attribute, so the control cannot pass on the strength of some other defect.
    Check ($controlPack.Exit -ne 0 -and $controlPack.Output -match 'AllowOn') 'control: makeappx refuses a file type association with an undefined attribute' "makeappx exit $($controlPack.Exit); this gate would not catch SP-0238:`n$($controlPack.Output)"
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures.Count -eq 0) { Write-Host "msix-manifest: PASS ($checks checks)"; exit 0 }
Write-Host "msix-manifest: FAIL ($($failures -join '; '))"
exit 1
