<#
    Builds src/StreamsPlayer.Core/Resources/country-names.json - the country name in every shipped interface
    language - from the Unicode CLDR territory names.

    Why a table and not RegionInfo: RegionInfo.DisplayName answers in the region's own language, not in the
    language the user chose for the interface, so a Ukrainian reader would see "Deutschland". The names are
    data, committed, and checked by CountryNamesTests against InterfaceLanguages and the flag folder.

    Input: a folder holding <language>/territories.json for every language in InterfaceLanguages, as published
    at https://github.com/unicode-org/cldr-json (cldr-json/cldr-localenames-full/main/<language>/).
    The flag folder decides which codes are kept: a country is offered with a flag or not at all.

    Usage:
        pwsh -NoProfile -File ./tools/build-country-names.ps1 -CldrDirectory <dir>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $CldrDirectory,
    [string] $FlagDirectory = (Join-Path $PSScriptRoot '../src/StreamsPlayer.App/Assets/Flags'),
    [string] $Output = (Join-Path $PSScriptRoot '../src/StreamsPlayer.Core/Resources/country-names.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'InterfaceLanguages.ps1')
$languages = @(Get-InterfaceLanguages | ForEach-Object { $_.DictionaryCode })

$names = @{}
foreach ($language in $languages) {
    $file = Join-Path $CldrDirectory "$language/territories.json"
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing CLDR file: $file" }
    $json = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
    $names[$language] = $json.main.$language.localeDisplayNames.territories
}

$codes = Get-ChildItem -LiteralPath $FlagDirectory -Filter '*.png' |
    Where-Object { $_.BaseName -match '^[a-z]{2}$' } |
    ForEach-Object { $_.BaseName.ToUpperInvariant() } |
    Sort-Object

$lines = New-Object System.Collections.Generic.List[string]
foreach ($code in $codes) {
    $entry = [ordered]@{}
    foreach ($language in $languages) {
        $value = $names[$language].$code
        if ([string]::IsNullOrWhiteSpace($value)) { throw "No '$language' name for $code in CLDR." }
        $entry[$language] = $value
    }
    $lines.Add('  "' + $code + '": ' + ($entry | ConvertTo-Json -Compress))
}

$text = "{`n" + ($lines -join ",`n") + "`n}`n"
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Output), $text, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($codes.Count) countries x $($languages.Count) languages to $Output"
