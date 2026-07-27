<#
    SP-0034: the shipped interface languages, for PowerShell tooling.

    There is exactly one declaration of the language list - StreamsPlayer.Core's InterfaceLanguages
    registry (src/StreamsPlayer.Core/InterfaceLanguages.cs). This file does not restate it; it loads
    the built Core assembly and reads it, so the site generator, the Store listing builder and the
    screenshot pipeline all derive from the same source the application uses. Adding a fourteenth
    language means editing the registry and nothing else.

    The endonyms come from the shipped English dictionary (Localization.en.xaml, the Language* keys),
    which is the other place they already exist. Nothing here holds a copy of either.

    Dot-source it:
        . "$PSScriptRoot/../InterfaceLanguages.ps1"
        $languages = Get-InterfaceLanguages
#>

# Deliberately no Set-StrictMode here: this file is dot-sourced, so a mode set at its top level would
# silently change the calling script's rules. Each consuming script sets its own.

function Get-RepositoryRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Get-InterfaceLanguages {
    [CmdletBinding()]
    param(
        # Which build of StreamsPlayer.Core to read. Release first, then Debug, unless pinned.
        [ValidateSet('Release', 'Debug')] [string] $Configuration
    )

    $root = Get-RepositoryRoot
    $configurations = if ($Configuration) { @($Configuration) } else { @('Release', 'Debug') }

    $assemblyPath = $null
    foreach ($candidate in $configurations) {
        $path = Join-Path $root "src/StreamsPlayer.Core/bin/$candidate/net10.0/StreamsPlayer.Core.dll"
        if (Test-Path -LiteralPath $path) { $assemblyPath = $path; break }
    }

    if (-not $assemblyPath) {
        throw "StreamsPlayer.Core.dll not found under src/StreamsPlayer.Core/bin ($($configurations -join ', ')). " +
              "The language registry is read from the built assembly, so build first: " +
              "dotnet build StreamsPlayer.sln -c Release"
    }

    # Load from bytes rather than LoadFrom: LoadFrom locks the file, and a later dotnet build in the
    # same session would fail with the assembly still held open.
    $assembly = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($assemblyPath))
    $registry = $assembly.GetType('StreamsPlayer.Core.InterfaceLanguages', $true)
    $entries = $registry.GetProperty('All').GetValue($null)

    if (-not $entries -or $entries.Count -eq 0) {
        throw "InterfaceLanguages.All is empty in $assemblyPath - the registry did not load."
    }

    $endonyms = Get-LanguageEndonym -Root $root

    $result = foreach ($entry in $entries) {
        $member = [string] $entry.Language
        if (-not $endonyms.ContainsKey($member)) {
            throw "Localization.en.xaml has no Language$member key, so $member has no endonym. " +
                  'Add the key to every dictionary; the parity gate expects it too.'
        }

        [pscustomobject]@{
            Language       = $member
            DictionaryCode = [string] $entry.DictionaryCode
            CultureCode    = [string] $entry.CultureCode
            ListingCode    = [string] $entry.ListingCode
            RightToLeft    = [bool] $entry.RightToLeft
            Endonym        = $endonyms[$member]
        }
    }

    return @($result)
}

function Get-LanguageEndonym {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Root)

    $dictionary = Join-Path $Root 'src/StreamsPlayer.App/Localization.en.xaml'
    if (-not (Test-Path -LiteralPath $dictionary)) {
        throw "English dictionary not found: $dictionary"
    }

    $endonyms = @{}
    $xml = [xml] (Get-Content -LiteralPath $dictionary -Raw -Encoding utf8)
    foreach ($node in $xml.DocumentElement.ChildNodes) {
        if ($node.NodeType -ne 'Element') { continue }
        $key = $node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        # Language, LanguageFilterName and LanguagePickerName are ordinary strings, not endonyms; only
        # Language<EnumMember> keys are, and Get-InterfaceLanguages asks for exactly those.
        if ($key -like 'Language?*') { $endonyms[$key.Substring(8)] = $node.InnerText }
    }

    return $endonyms
}
