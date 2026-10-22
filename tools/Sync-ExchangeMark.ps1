<#
.SYNOPSIS
    SP-0234: vendors the EXCHANGE-MARK artwork this product draws, byte for byte, and records what it holds.

.DESCRIPTION
    This product is a consumer of EXCHANGE-MARK (rules 1, 5 and 6: the product copies the files of the
    catalog, draws nothing of its own and never re-exports a raster). The contract store is not part of a
    clone, so nothing the build or the tests read may point into it: the files are copied here.

    assets/exchange-mark/png/mark-<n>.png    the coloured mark at every size this product draws it at
                                             (the 100 to 200 per cent steps of 16 and 24 DIP, and the sizes
                                             above them for a larger scale, rule 5)
    assets/exchange-mark/PROVENANCE.txt      the contract version and the SHA-256 of every copy
    assets/glyphs/own/source.exchange-server.svg
                                             the one-tone glyph (svg/exchange-mark-mono.svg), which
                                             Sync-IconGlyphs.ps1 turns into the geometry of the header button;
                                             rule 6 lets the glyph's path be carried that way, unchanged

    The files travel to the App as Resource items and to the help pages through tools/help/build-help.ps1.
    Nothing here is generated from a drawing of this product's own.

    Modes:
    (default)  copy what differs from the catalog, then write PROVENANCE.txt.
    -Check     change nothing; exit 0 when every copy equals the catalog and PROVENANCE.txt is current,
               1 on any drift (each named), 2 when the catalog cannot be reached.

    The catalog root comes from -CatalogRoot or the SZA_CONTRACTS_ROOT environment variable - never a
    literal path, because a clone elsewhere has no such drive.

.EXAMPLE
    pwsh -NoProfile -File ./tools/Sync-ExchangeMark.ps1 -CatalogRoot $env:SZA_CONTRACTS_ROOT
    pwsh -NoProfile -File ./tools/Sync-ExchangeMark.ps1 -Check -CatalogRoot $env:SZA_CONTRACTS_ROOT
#>
[CmdletBinding()]
param(
    [string] $CatalogRoot = $env:SZA_CONTRACTS_ROOT,
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artwork = if ($CatalogRoot) { Join-Path $CatalogRoot 'exchange-mark/artwork' } else { $null }
$target = Join-Path $repository 'assets/exchange-mark'
$provenancePath = Join-Path $target 'PROVENANCE.txt'
$glyphCopy = Join-Path $repository 'assets/glyphs/own/source.exchange-server.svg'
$utf8 = New-Object System.Text.UTF8Encoding($false)

# 16 and 24 DIP at 100, 125, 150, 175 and 200 per cent are 16/20/24/28/32 and 24/30/36/42/48 pixels; 40, 56, 64
# and 96 are the next held sizes for a larger scale. A size between two steps is a PNG scaled DOWN (rule 5).
$steps = 16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 56, 64, 96

if (-not $artwork -or -not (Test-Path -LiteralPath (Join-Path $artwork 'MANIFEST.json'))) {
    $message = 'The contract catalog is not reachable (pass -CatalogRoot or set SZA_CONTRACTS_ROOT).'
    if ($Check) { Write-Host $message -ForegroundColor Yellow; exit 2 }
    throw $message
}

$manifest = Get-Content -LiteralPath (Join-Path $artwork 'MANIFEST.json') -Raw -Encoding utf8 | ConvertFrom-Json
$version = [string] $manifest.version
$drift = [System.Collections.Generic.List[string]]::new()

function Get-Sha256([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# [catalog path, product path]
$copies = [System.Collections.Generic.List[object]]::new()
foreach ($step in $steps) { $copies.Add(@("png/mark-$step.png", (Join-Path $target "png/mark-$step.png"))) }
$copies.Add(@('svg/exchange-mark-mono.svg', $glyphCopy))

foreach ($pair in $copies) {
    $entry = $manifest.files | Where-Object { $_.path -eq $pair[0] }
    if (-not $entry) { throw "The catalog manifest has no $($pair[0])." }
    $source = Join-Path $artwork $pair[0]
    if ((Get-Sha256 $source) -ne $entry.sha256) { throw "$($pair[0]) in the catalog does not match its own manifest." }
    $same = (Test-Path -LiteralPath $pair[1]) -and ((Get-Sha256 $pair[1]) -eq $entry.sha256)
    if ($same) { continue }
    $relative = [System.IO.Path]::GetRelativePath($repository, $pair[1]).Replace('\', '/')
    if ($Check) { $drift.Add("$relative differs from $($pair[0]) of EXCHANGE-MARK $version"); continue }
    New-Item -ItemType Directory -Force -Path (Split-Path $pair[1] -Parent) | Out-Null
    Copy-Item -LiteralPath $source -Destination $pair[1] -Force
    Write-Host "copied $($pair[0])" -ForegroundColor Cyan
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Vendored EXCHANGE-MARK artwork - byte-for-byte copies of Contracts/exchange-mark/artwork/<path> (owner: shared).')
$lines.Add('# Written by tools/Sync-ExchangeMark.ps1. A copy is never edited, re-exported or recoloured (EXCHANGE-MARK rules 1, 2, 6).')
$lines.Add("# EXCHANGE-MARK $version")
foreach ($pair in $copies) {
    $entry = $manifest.files | Where-Object { $_.path -eq $pair[0] }
    $where = [System.IO.Path]::GetRelativePath($repository, $pair[1]).Replace('\', '/')
    $lines.Add(('{0}  {1}  {2}' -f $pair[0], $entry.sha256, $where))
}
$provenance = ($lines -join "`n") + "`n"
$current = if (Test-Path -LiteralPath $provenancePath) { [System.IO.File]::ReadAllText($provenancePath).Replace("`r`n", "`n") } else { $null }
if ($current -ne $provenance) {
    if ($Check) { $drift.Add('assets/exchange-mark/PROVENANCE.txt is not current') }
    else {
        [System.IO.File]::WriteAllText($provenancePath, $provenance, $utf8)
        Write-Host 'wrote assets/exchange-mark/PROVENANCE.txt' -ForegroundColor Cyan
    }
}

if ($drift.Count) {
    $drift | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "Exchange mark: $($drift.Count) drift(s)." -ForegroundColor Red
    exit 1
}
Write-Host "Exchange mark: EXCHANGE-MARK $version, $($copies.Count) copies$(if ($Check) { ' - in sync with the catalog' } else { ' vendored' })." -ForegroundColor Green
exit 0
