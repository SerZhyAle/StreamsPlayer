<#
.SYNOPSIS
    SP-0113: vendors the portfolio icon vocabulary's glyphs this product uses and regenerates what is
    derived from them.

.DESCRIPTION
    This product is a consumer of ICON-SET / ICON-RENDER / ICON-EXTERNAL. The contract store is not part
    of a clone, so nothing the build or the tests read may point into it: the glyphs this product draws
    are vendored under assets/glyphs/, and every derived file is generated from those copies.

    src/StreamsPlayer.App/Glyphs.map.json   the map - which meanings, which pending keys, which chrome
    assets/glyphs/<id>.svg                  byte-for-byte copies of the catalog's glyphs/<id>.svg
    assets/glyphs/PROVENANCE.txt            contract versions and the SHA-256 of every copy
    assets/glyphs/own/<id>.svg              this product's drawing of a catalog glyph it cannot paint as
                                            drawn (the map says why), used in place of the copy
    assets/glyphs/pending/<key>.svg         this product's drawing for a meaning asked for by proposal
    assets/glyphs/private/<key>.svg         chrome and illustration, never a vocabulary meaning

    Generated (never hand-edit):
    src/StreamsPlayer.App/Glyphs.xaml       one geometry per key, merged into App.xaml
    docs/assets/glyphs/<id>.svg             the README copies: the same drawing in a fixed grey that
                                            keeps 3:1 on GitHub's light and dark page, because an <img>
                                            cannot inherit currentColor

    Modes:
    (default)  import from the catalog when it is reachable, then regenerate the derived files;
               -Offline regenerates from the vendored copies without touching the catalog.
    -Check     change nothing; exit 0 when the vendored copies equal the catalog and every derived file
               is current, 1 on any drift (each named), 2 when the catalog cannot be reached.

    The catalog root comes from -CatalogRoot or the SZA_CONTRACTS_ROOT environment variable - never a
    literal path, because a clone elsewhere has no such drive.

.EXAMPLE
    pwsh -NoProfile -File ./tools/Sync-IconGlyphs.ps1 -CatalogRoot $env:SZA_CONTRACTS_ROOT
    pwsh -NoProfile -File ./tools/Sync-IconGlyphs.ps1 -Check -CatalogRoot $env:SZA_CONTRACTS_ROOT
#>
[CmdletBinding()]
param(
    [string] $CatalogRoot = $env:SZA_CONTRACTS_ROOT,
    [switch] $Check,
    [switch] $Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$mapPath = Join-Path $repository 'src/StreamsPlayer.App/Glyphs.map.json'
$assets = Join-Path $repository 'assets/glyphs'
$xamlPath = Join-Path $repository 'src/StreamsPlayer.App/Glyphs.xaml'
$readmeGlyphs = Join-Path $repository 'docs/assets/glyphs'
$provenancePath = Join-Path $assets 'PROVENANCE.txt'
# 128/128/128: 3.95:1 on white and 4.8:1 on GitHub's dark page (#0D1117) - ICON-RENDER 3 on both.
$readmeGrey = '#808080'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$map = Get-Content -LiteralPath $mapPath -Raw -Encoding utf8 | ConvertFrom-Json
$drift = [System.Collections.Generic.List[string]]::new()

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-SiteOnly($Meaning) {
    return @($Meaning.surfaces | Where-Object { -not $_.StartsWith('site ') }).Count -eq 0
}

# ------------------------------------------------------------------------------------------- import

$catalogGlyphs = $null
$versions = $null
if (-not $Offline) {
    if (-not $CatalogRoot -or -not (Test-Path -LiteralPath (Join-Path $CatalogRoot 'iconography/glyphs'))) {
        $message = "The contract catalog is not reachable (pass -CatalogRoot or set SZA_CONTRACTS_ROOT)."
        if ($Check) { Write-Host $message -ForegroundColor Yellow; exit 2 }
        throw "$message Use -Offline to regenerate from the vendored copies."
    }

    $catalogGlyphs = Join-Path $CatalogRoot 'iconography/glyphs'
    $readme = Get-Content -LiteralPath (Join-Path $CatalogRoot 'iconography/README.md') -Raw -Encoding utf8
    $versions = [ordered]@{}
    foreach ($id in 'ICON-SET', 'ICON-RENDER', 'ICON-EXTERNAL') {
        $match = [regex]::Match($readme, "id: $id\r?\nversion: ([0-9.]+)")
        if (-not $match.Success) { throw "The catalog README declares no version for $id." }
        $versions[$id] = $match.Groups[1].Value
        if ($map.contracts.$id -ne $versions[$id]) {
            $drift.Add("contract $id is $($versions[$id]) in the catalog, $($map.contracts.$id) in the map")
        }
    }

    foreach ($meaning in $map.meanings) {
        $source = Join-Path $catalogGlyphs "$($meaning.id).svg"
        if (-not (Test-Path -LiteralPath $source)) { throw "The catalog has no glyph for $($meaning.id)." }
        $target = Join-Path $assets "$($meaning.id).svg"
        $same = (Test-Path -LiteralPath $target) -and ((Get-Sha256 $source) -eq (Get-Sha256 $target))
        if ($same) { continue }
        if ($Check) { $drift.Add("assets/glyphs/$($meaning.id).svg differs from the catalog"); continue }
        Copy-Item -LiteralPath $source -Destination $target -Force
        Write-Host "imported $($meaning.id)" -ForegroundColor Cyan
    }
}

# ---------------------------------------------------------------------------------------- rendering

function Get-Matrix([string] $Transform) {
    # The exporter writes translate() and scale() chains only; anything else is refused, not guessed.
    $m = @(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
    foreach ($step in [regex]::Matches($Transform, '(\w+)\(([^)]*)\)')) {
        $values = @($step.Groups[2].Value -split '[\s,]+' | Where-Object { $_ } | ForEach-Object { [double]::Parse($_, [cultureinfo]::InvariantCulture) })
        switch ($step.Groups[1].Value) {
            'translate' {
                $tx = $values[0]; $ty = if ($values.Count -gt 1) { $values[1] } else { 0.0 }
                $m = @($m[0], $m[1], $m[2], $m[3], ($m[0] * $tx + $m[2] * $ty + $m[4]), ($m[1] * $tx + $m[3] * $ty + $m[5]))
            }
            'scale' {
                $sx = $values[0]; $sy = if ($values.Count -gt 1) { $values[1] } else { $sx }
                $m = @(($m[0] * $sx), ($m[1] * $sx), ($m[2] * $sy), ($m[3] * $sy), $m[4], $m[5])
            }
            default { throw "Unsupported transform '$($step.Groups[1].Value)' in '$Transform'." }
        }
    }
    return ($m | ForEach-Object { ([math]::Round($_, 6)).ToString([cultureinfo]::InvariantCulture) }) -join ','
}

function ConvertTo-GeometryXaml([string] $Key, [string] $SvgPath) {
    [xml] $svg = Get-Content -LiteralPath $SvgPath -Raw -Encoding utf8
    $manager = New-Object System.Xml.XmlNamespaceManager($svg.NameTable)
    $manager.AddNamespace('s', 'http://www.w3.org/2000/svg')
    $paths = @($svg.SelectNodes('//s:path', $manager))
    $groups = @($svg.SelectNodes('//s:g', $manager))
    if ($groups.Count -gt 1) { throw "$SvgPath nests groups; not supported." }
    # A glyph is a filled silhouette (ICON-RENDER 1). Chrome is not a glyph: a check mark or a submenu
    # arrow is stroked by its control template, like the platform's own, so private/ may be a stroke.
    $isPrivate = [System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName($SvgPath)) -eq 'private'
    foreach ($path in $paths) {
        $filled = $path.GetAttribute('fill') -eq 'currentColor' -and -not $path.HasAttribute('stroke')
        $stroked = $isPrivate -and $path.GetAttribute('fill') -eq 'none' -and $path.GetAttribute('stroke') -eq 'currentColor'
        if (-not ($filled -or $stroked) -or $path.HasAttribute('opacity')) {
            throw "$SvgPath paints something other than a currentColor fill; give this product an own/ drawing."
        }
    }

    $matrix = if ($groups.Count -eq 1 -and $groups[0].HasAttribute('transform')) { Get-Matrix $groups[0].GetAttribute('transform') } else { $null }
    # The source folder, not the file name: a state's file name carries '--', which no XML comment may.
    $folder = [System.IO.Path]::GetRelativePath($assets, [System.IO.Path]::GetDirectoryName($SvgPath)).Replace('\', '/')
    $origin = switch ($folder) { '.' { 'catalog copy' } 'own' { 'own drawing of a catalog glyph' } default { "$folder drawing" } }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("    <!-- $origin -->")

    $figures = foreach ($path in $paths) {
        $rule = if ($path.GetAttribute('fill-rule') -eq 'evenodd') { 'EvenOdd' } else { 'Nonzero' }
        [pscustomobject]@{ Rule = $rule; Data = [System.Security.SecurityElement]::Escape($path.GetAttribute('d')) }
    }

    if ($paths.Count -eq 1) {
        $f = $figures[0]
        if ($matrix) {
            $lines.Add("    <PathGeometry x:Key=`"$Key`" FillRule=`"$($f.Rule)`" Figures=`"$($f.Data)`">")
            $lines.Add("        <PathGeometry.Transform><MatrixTransform Matrix=`"$matrix`" /></PathGeometry.Transform>")
            $lines.Add('    </PathGeometry>')
        } else {
            $lines.Add("    <PathGeometry x:Key=`"$Key`" FillRule=`"$($f.Rule)`" Figures=`"$($f.Data)`" />")
        }
    } else {
        if (@($figures | Where-Object Rule -eq 'EvenOdd').Count) { throw "$SvgPath combines several paths with evenodd; not supported." }
        $lines.Add("    <GeometryGroup x:Key=`"$Key`" FillRule=`"Nonzero`">")
        if ($matrix) { $lines.Add("        <GeometryGroup.Transform><MatrixTransform Matrix=`"$matrix`" /></GeometryGroup.Transform>") }
        foreach ($f in $figures) { $lines.Add("        <PathGeometry FillRule=`"Nonzero`" Figures=`"$($f.Data)`" />") }
        $lines.Add('    </GeometryGroup>')
    }
    return $lines
}

$entries = [System.Collections.Generic.List[object]]::new()
foreach ($meaning in $map.meanings) {
    if (Test-SiteOnly $meaning) { continue }
    $own = Join-Path $assets "own/$($meaning.id).svg"
    $file = if (Test-Path -LiteralPath $own) { $own } else { Join-Path $assets "$($meaning.id).svg" }
    $entries.Add([pscustomobject]@{ Key = "Icon.$($meaning.id)"; File = $file })
}
foreach ($pending in $map.pending) {
    if ($pending.PSObject.Properties['site'] -and $pending.site) { continue }
    $entries.Add([pscustomobject]@{ Key = "Icon.pending.$($pending.key)"; File = Join-Path $assets "pending/$($pending.key).svg" })
}
foreach ($private in $map.private) {
    $entries.Add([pscustomobject]@{ Key = $private.key; File = Join-Path $assets "private/$($private.key).svg" })
}

$xaml = [System.Collections.Generic.List[string]]::new()
$xaml.Add('<!-- Generated by tools/Sync-IconGlyphs.ps1 from src/StreamsPlayer.App/Glyphs.map.json and assets/glyphs/.')
$xaml.Add('     Do not hand-edit: amend the map or the vendored drawing and rerun the script.')
$xaml.Add("     Icon.<id> is the ICON-SET meaning of that id ($($map.contracts.'ICON-SET')); Icon.pending.<key> is this product's")
$xaml.Add('     drawing for a meaning asked for by proposal; Chrome.* and Illustration.* are never a meaning. -->')
$xaml.Add('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"')
$xaml.Add('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">')
foreach ($entry in $entries) {
    foreach ($line in (ConvertTo-GeometryXaml -Key $entry.Key -SvgPath $entry.File)) { $xaml.Add($line) }
}
$xaml.Add('</ResourceDictionary>')
$xamlText = ($xaml -join "`r`n") + "`r`n"

# Every figure string has to be one WPF can parse; a string it cannot would only fail at start-up.
try {
    Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase -ErrorAction Stop
    $dictionary = [System.Windows.Markup.XamlReader]::Parse($xamlText)
    foreach ($entry in $entries) {
        $geometry = $dictionary[$entry.Key]
        if ($null -eq $geometry -or $geometry.Bounds.IsEmpty) { throw "$($entry.Key) parsed to an empty geometry." }
    }
} catch [System.Management.Automation.RuntimeException] {
    throw "Glyphs.xaml does not load in WPF: $($_.Exception.Message)"
}

$readmeFiles = [ordered]@{}
foreach ($meaning in $map.meanings) {
    $text = [System.IO.File]::ReadAllText((Join-Path $assets "$($meaning.id).svg"))
    $readmeFiles["$($meaning.id).svg"] = $text.Replace('currentColor', $readmeGrey)
}
foreach ($pending in $map.pending) {
    $file = Join-Path $assets "pending/$($pending.key).svg"
    if (Test-Path -LiteralPath $file) {
        $readmeFiles["$($pending.key).svg"] = [System.IO.File]::ReadAllText($file).Replace('currentColor', $readmeGrey)
    }
}

$provenance = [System.Collections.Generic.List[string]]::new()
$provenance.Add('# Vendored ICON-SET glyphs - copies of Contracts/iconography/glyphs/<id>.svg (owner: FastMediaSorter Android).')
$provenance.Add('# Written by tools/Sync-IconGlyphs.ps1; own/, pending/ and private/ are this product''s drawings and are not listed.')
$provenance.Add(('# ICON-SET {0}, ICON-RENDER {1}, ICON-EXTERNAL {2}' -f $map.contracts.'ICON-SET', $map.contracts.'ICON-RENDER', $map.contracts.'ICON-EXTERNAL'))
foreach ($meaning in $map.meanings | Sort-Object id) {
    $provenance.Add(('{0}.svg  {1}' -f $meaning.id, (Get-Sha256 (Join-Path $assets "$($meaning.id).svg"))))
}
$provenanceText = ($provenance -join "`n") + "`n"

function Save-Derived([string] $Path, [string] $Content) {
    $existing = if (Test-Path -LiteralPath $Path) { [System.IO.File]::ReadAllText($Path) } else { $null }
    if ($existing -eq $Content) { return }
    $relative = [System.IO.Path]::GetRelativePath($repository, $Path).Replace('\', '/')
    if ($Check) { $script:drift.Add("$relative is not current"); return }
    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Content, $utf8)
    Write-Host "wrote $relative" -ForegroundColor Cyan
}

Save-Derived -Path $xamlPath -Content $xamlText
Save-Derived -Path $provenancePath -Content $provenanceText
foreach ($name in $readmeFiles.Keys) { Save-Derived -Path (Join-Path $readmeGlyphs $name) -Content $readmeFiles[$name] }

$expected = @($readmeFiles.Keys)
if (Test-Path -LiteralPath $readmeGlyphs) {
    foreach ($stray in Get-ChildItem -LiteralPath $readmeGlyphs -Filter *.svg | Where-Object { $expected -notcontains $_.Name }) {
        if ($Check) { $drift.Add("docs/assets/glyphs/$($stray.Name) is not in the map") } else { Remove-Item -LiteralPath $stray.FullName }
    }
}

if ($Check) {
    if ($drift.Count) {
        $drift | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host "Icon glyphs: $($drift.Count) drift(s)." -ForegroundColor Red
        exit 1
    }
    Write-Host ("Icon glyphs: {0} meanings, {1} pending, {2} private - in sync with the catalog." -f
        @($map.meanings).Count, @($map.pending).Count, @($map.private).Count) -ForegroundColor Green
    exit 0
}

if ($drift.Count) { $drift | ForEach-Object { Write-Host "  note: $_" -ForegroundColor Yellow } }
Write-Host ("Icon glyphs: {0} geometries in Glyphs.xaml, {1} README copies." -f $entries.Count, $readmeFiles.Count) -ForegroundColor Green
