#requires -Version 7.0
<#
.SYNOPSIS
  SP-0238: prove the committed msix/AppxManifest.xml template still packs, from any tree state.

.DESCRIPTION
  build-msix.ps1 is the only other thing that runs makeappx over the template, and it refuses a dirty tree and a HEAD
  off a tag - so a schema error in the template stays invisible until the day a Store package is cut (the .spres file
  association carried two invalid attributes from SP-0196 until SP-0238). This gate closes that gap: it fills the
  template with the function build-msix.ps1 fills it with (msix/MsixManifest.ps1), stages it with placeholder
  executables and the product logos, and runs the Windows SDK's own `makeappx pack` over it. The SDK is the authority on
  the manifest schema; nothing here restates it. Both manifests a build can produce are judged: the default one (no
  companion - the one Partner Center accepts without the HeadlessAppBypass waiver) and the one -Companion produces.

    packs        makeappx accepts each filled manifest (exit 0)
    placeholders no __PLACEHOLDER__ and no companion marker is left after the fill (a new placeholder needs this script
                 and MsixManifest.ps1)
    association  each manifest still declares the .spres file type association (SP-0196)
    store        the default manifest declares one Application, no Application with AppListEntry="none" (the headless
                 app Partner Center refuses without the waiver), the alias StreamsPlayer.exe once, for StreamsPlayer.exe,
                 and no alias StreamsPlayerCli.exe
    companion    SP-0275: the -Companion manifest declares two Applications, and the player's alias StreamsPlayer.exe and
                 the console companion's alias StreamsPlayerCli.exe each exactly once, each for the executable of the same
                 name
    markers      a template that has lost its companion:end line is refused by the fill, not packed
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
. (Join-Path $PSScriptRoot 'MsixManifest.ps1')
function Get-FilledManifest([string] $TemplatePath = (Join-Path $PSScriptRoot 'AppxManifest.xml'), [switch] $Companion) {
    Get-MsixManifestText -TemplatePath $TemplatePath -LanguageResources $languageResources -IdentityName 'SZA.StreamsPlayer' -Publisher 'CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD' -PublisherDisplayName 'SZA' -Version '26.1.1.0' -Companion:$Companion
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0238-msix-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    function New-Stage([string] $Name, [string] $Manifest) {
        $stage = Join-Path $work $Name
        New-Item -ItemType Directory -Path (Join-Path $stage 'Assets') -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $stage 'StreamsPlayer.exe'), 'MZ')
        # SP-0275: the package holds the console companion too, and makeappx refuses an Executable that is not staged.
        [System.IO.File]::WriteAllText((Join-Path $stage 'StreamsPlayerCli.exe'), 'MZ')
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

    function Get-AliasNode([xml] $Xml, [string] $Executable) {
        @($Xml.SelectNodes("//*[local-name()='ExecutionAlias']") | Where-Object { $_.GetAttribute('Alias') -ceq $Executable })
    }
    # One alias per executable, and its extension runs the executable the alias is named for.
    function Test-Alias([xml] $Xml, [string] $Executable, [string] $Label) {
        $aliasNodes = @(Get-AliasNode $Xml $Executable)
        Check ($aliasNodes.Count -eq 1) "${Label}: $Executable is declared exactly once"
        $extension = if ($aliasNodes.Count -ge 1) { $aliasNodes[0].ParentNode.ParentNode } else { $null }
        Check ($null -ne $extension -and $extension.GetAttribute('Executable') -ceq $Executable) "${Label}: the extension of $Executable runs $Executable"
    }

    $variants = [ordered]@{ store = (Get-FilledManifest); companion = (Get-FilledManifest -Companion) }
    $documents = @{}
    foreach ($name in $variants.Keys) {
        $filled = $variants[$name]
        Check ($filled -notmatch '__[A-Z_]+__') "placeholders: none left after the $name fill" ($filled | Select-String '__[A-Z_]+__' -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique | Out-String)
        Check ($filled -notmatch 'companion:(begin|end)') "placeholders: no companion marker left in the $name manifest"

        $pack = Invoke-Pack (New-Stage $name $filled) $name
        Check ($pack.Exit -eq 0) "packs: makeappx accepts the $name manifest" "makeappx exit $($pack.Exit):`n$($pack.Output)"

        $documents[$name] = [xml] $filled
        $association = $documents[$name].SelectNodes("//*[local-name()='FileType']") | Where-Object { $_.InnerText -ceq '.spres' }
        Check (@($association).Count -eq 1) "association: the $name manifest declares the .spres file type exactly once"
    }

    $storeApplications = @($documents.store.SelectNodes("//*[local-name()='Application']"))
    Check ($storeApplications.Count -eq 1) "store: the default manifest declares one Application (found $($storeApplications.Count))"
    $headless = @($documents.store.SelectNodes("//*[local-name()='VisualElements']") | Where-Object { $_.GetAttribute('AppListEntry') -ceq 'none' })
    Check ($headless.Count -eq 0) 'store: no Application of the default manifest is hidden from the Start list (AppListEntry="none")'
    Test-Alias $documents.store 'StreamsPlayer.exe' 'store'
    Check (@(Get-AliasNode $documents.store 'StreamsPlayerCli.exe').Count -eq 0) 'store: the default manifest declares no alias StreamsPlayerCli.exe'

    # SP-0275: with -Companion both executables have their alias.
    $companionApplications = @($documents.companion.SelectNodes("//*[local-name()='Application']"))
    Check ($companionApplications.Count -eq 2) "companion: the -Companion manifest declares two Applications (found $($companionApplications.Count))"
    foreach ($executable in 'StreamsPlayer.exe', 'StreamsPlayerCli.exe') { Test-Alias $documents.companion $executable 'companion' }

    # A template that has lost a marker is refused by the fill, so it can neither ship the companion nor drop it unseen.
    $template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
    $unmarked = Join-Path $work 'unmarked.xml'
    [System.IO.File]::WriteAllText($unmarked, ($template -replace '(?m)^[ \t]*<!-- companion:end -->[ \t]*\r?\n', ''), [System.Text.UTF8Encoding]::new($false))
    $refused = $false
    try { $null = Get-FilledManifest -TemplatePath $unmarked } catch { $refused = $_.Exception.Message -match 'companion block' }
    Check $refused 'markers: a template without its companion:end line is refused by the fill'

    $filled = $variants.store

    # Negative control: an attribute no schema defines (the shape of the SP-0238 defect) must stop the pack.
    $broken = $filled -replace '(<uap:FileTypeAssociation Name="spres")', '$1 AllowOn="desktop"'
    Check ($broken -cne $filled) 'control: the broken fixture differs from the template'
    $controlPack = Invoke-Pack (New-Stage 'control' $broken) 'control'
    # The refusal must name the injected attribute, so the control cannot pass on the strength of some other defect.
    Check ($controlPack.Exit -ne 0 -and $controlPack.Output -match 'AllowOn') 'control: makeappx refuses a file type association with an undefined attribute' "makeappx exit $($controlPack.Exit); this gate would not catch SP-0238:`n$($controlPack.Output)"
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures.Count -eq 0) { Write-Host "msix-manifest: PASS (run=$checks skipped=0)"; exit 0 }
Write-Host "msix-manifest: FAIL ($($failures -join '; '))"
exit 1
