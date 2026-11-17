#requires -Version 7.0
<#
.SYNOPSIS
  Fill msix/AppxManifest.xml into the manifest a package carries - one function, dot-sourced by msix/build-msix.ps1 and
  msix/Test-AppxManifest.ps1, so the gate packs exactly what the build packs.

.DESCRIPTION
  Get-MsixManifestText reads the template, keeps or removes the console companion's block and replaces the
  placeholders. It never writes a file.

  The companion block is everything from the line `<!-- companion:begin -->` to the line `<!-- companion:end -->`: the
  second Application (StreamsPlayerCli, AppListEntry="none") and its execution alias (SP-0275). Partner Center treats
  such an Application as a headless app and refuses the package unless the account holds the HeadlessAppBypass waiver;
  it rejected the package of 26.1117.1838 for exactly that. So the block is removed unless -Companion is passed, and
  -Companion is for a package whose account holds the waiver. The template must hold exactly one such block, its
  markers on lines of their own; anything else throws, so a template edit that loses a marker cannot quietly ship the
  companion to the Store or drop it from a waiver build.
#>

# No Set-StrictMode here: this file is dot-sourced, and a mode set at its top level would apply to the rest of
# build-msix.ps1 too.

function Get-MsixManifestText {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $TemplatePath,
        [Parameter(Mandatory)][string] $LanguageResources,
        [Parameter(Mandatory)][string] $IdentityName,
        [Parameter(Mandatory)][string] $Publisher,
        [Parameter(Mandatory)][string] $PublisherDisplayName,
        [Parameter(Mandatory)][string] $Version,
        [switch] $Companion
    )

    $text = Get-Content -LiteralPath $TemplatePath -Raw -Encoding utf8
    $begin = [regex]::Matches($text, '(?m)^[ \t]*<!-- companion:begin -->[ \t]*\r?\n')
    $end = [regex]::Matches($text, '(?m)^[ \t]*<!-- companion:end -->[ \t]*\r?\n')
    if ($begin.Count -ne 1 -or $end.Count -ne 1 -or $begin[0].Index -ge $end[0].Index) {
        throw "$TemplatePath must hold exactly one companion block: a line '<!-- companion:begin -->' followed later by a line '<!-- companion:end -->' (found $($begin.Count) begin, $($end.Count) end)."
    }
    $blockStart = $begin[0].Index
    $blockEnd = $end[0].Index + $end[0].Length
    $text = if ($Companion) {
        # Keep the block, drop only its two marker lines.
        $text.Substring(0, $blockStart) + $text.Substring($blockStart + $begin[0].Length, $end[0].Index - $blockStart - $begin[0].Length) + $text.Substring($blockEnd)
    }
    else {
        $text.Substring(0, $blockStart) + $text.Substring($blockEnd)
    }

    $text = $text.Replace('__LANGUAGE_RESOURCES__', $LanguageResources)
    $text.Replace('__IDENTITY_NAME__', $IdentityName).Replace('__PUBLISHER__', $Publisher).Replace('__PUBLISHER_DISPLAY_NAME__', $PublisherDisplayName).Replace('__VERSION__', $Version)
}
