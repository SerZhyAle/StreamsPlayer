#Requires -Version 7.0
<#
.SYNOPSIS
    The glyph index of the in-app help (CONTEXT-HELP 0.6 item R): which ICON-SET glyph a help entry shows beside its name.

.DESCRIPTION
    Dot-sourced by tools/help/build-help.ps1; defines Get-GlyphIndex and runs nothing.

    The pairing is read from the build, not authored: Glyphs.map.json says which glyph style draws which meaning, and
    the XAML says which element carries that style and which dictionary keys name it. A window's controls include the
    ones drawn by the user controls it places (STRWIN-0302): an element whose clr-namespace and local name resolve to
    the x:Class of a UserControl document of the App brings that document's elements in, transitively, each document
    once. Matching is by the resolved class, never by a prefix or a file name; a type of another assembly is a leaf.

    The result maps "<WindowClass>|<key>" to the meaning's SVG, and holds nothing for a key under which the window and
    everything it reaches draw two meanings: a conflict stays unassigned rather than guessed. The SVG is the one the
    product ships: assets/glyphs/own/<id>.svg where the product draws its own, the vendored assets/glyphs/<id>.svg
    otherwise; filled with currentColor, so it takes the page's text colour in either theme.
#>

function Get-GlyphIndex([object] $GlyphMap, [string] $AppFolder, [string] $GlyphFolder) {
    $styleToId = @{}
    foreach ($meaning in @($GlyphMap.meanings)) {
        foreach ($surface in @($meaning.surfaces)) {
            if ($surface -like 'style:*') { $styleToId[$surface.Substring(6)] = [string] $meaning.id }
        }
    }

    $xamlNamespace = 'http://schemas.microsoft.com/winfx/2006/xaml'
    $keysOf = @{}        # document -> dictionary key -> the meanings its own glyph-styled elements draw under that key
    $placedBy = @{}      # document -> the fully qualified classes of the clr-namespace elements it places
    $controlOf = @{}     # fully qualified x:Class of a UserControl root -> its document
    foreach ($file in Get-ChildItem -LiteralPath $AppFolder -Filter '*.xaml' -File) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
        $keys = @{}
        foreach ($element in [regex]::Matches($text, '<[A-Za-z][\w:.]*\s[^<>]*?>')) {
            $style = [regex]::Match($element.Value, 'Style="\{StaticResource (\w+)\}"')
            if (-not $style.Success -or -not $styleToId.ContainsKey($style.Groups[1].Value)) { continue }
            foreach ($key in [regex]::Matches($element.Value, '(?:Dynamic|Static)Resource (\w+)\}')) {
                $name = $key.Groups[1].Value
                if ($name -eq $style.Groups[1].Value) { continue }
                if (-not $keys.ContainsKey($name)) { $keys[$name] = [System.Collections.Generic.HashSet[string]]::new() }
                [void] $keys[$name].Add($styleToId[$style.Groups[1].Value])
            }
        }
        $keysOf[$file.BaseName] = $keys

        # Only a document with code behind places controls or is one; the dictionaries are skipped unparsed.
        if (-not $text.Contains('x:Class=')) { continue }
        $root = ([xml] $text).DocumentElement
        $class = $root.GetAttribute('Class', $xamlNamespace)
        if ($root.LocalName -eq 'UserControl' -and $class) { $controlOf[$class] = $file.BaseName }
        $placed = [System.Collections.Generic.HashSet[string]]::new()
        foreach ($node in $root.SelectNodes('//*')) {
            $clr = [regex]::Match($node.NamespaceURI, '^clr-namespace:([\w.]+)(?:;assembly=([\w.]+))?$')
            if (-not $clr.Success -or $node.LocalName.Contains('.')) { continue }   # not a clr type, or a property element
            if ($clr.Groups[2].Success -and $clr.Groups[2].Value -ne 'StreamsPlayer') { continue }   # another assembly's type
            [void] $placed.Add("$($clr.Groups[1].Value).$($node.LocalName)")
        }
        $placedBy[$file.BaseName] = $placed
    }

    $svgs = @{}
    $index = @{}
    foreach ($document in $keysOf.Keys) {
        # Everything the document reaches, each document once whatever the number of instances or a cycle.
        $meanings = @{}
        $reached = [System.Collections.Generic.HashSet[string]]::new()
        $pending = [System.Collections.Generic.Stack[string]]::new()
        $pending.Push($document)
        while ($pending.Count) {
            $current = $pending.Pop()
            if (-not $reached.Add($current)) { continue }
            foreach ($name in $keysOf[$current].Keys) {
                if (-not $meanings.ContainsKey($name)) { $meanings[$name] = [System.Collections.Generic.HashSet[string]]::new() }
                $meanings[$name].UnionWith($keysOf[$current][$name])
            }
            if (-not $placedBy.ContainsKey($current)) { continue }
            foreach ($class in $placedBy[$current]) {
                if ($controlOf.ContainsKey($class)) { $pending.Push($controlOf[$class]) }
            }
        }

        foreach ($name in $meanings.Keys) {
            if ($meanings[$name].Count -ne 1) { continue }
            $id = @($meanings[$name])[0]
            if (-not $svgs.ContainsKey($id)) {
                $own = Join-Path $GlyphFolder "own/$id.svg"
                $path = if (Test-Path -LiteralPath $own) { $own } else { Join-Path $GlyphFolder "$id.svg" }
                if (-not (Test-Path -LiteralPath $path)) { throw "Glyphs.map.json names the meaning '$id' and assets/glyphs has no $id.svg" }
                $svg = ((Get-Content -LiteralPath $path -Raw -Encoding utf8) -replace '\s*\r?\n\s*', '').Trim()
                $svg = $svg -replace '^<svg xmlns="http://www.w3.org/2000/svg" width="24(?:\.0)?" height="24(?:\.0)?"', '<svg xmlns="http://www.w3.org/2000/svg" class="glyph" width="20" height="20" aria-hidden="true" focusable="false"'
                if ($svg -notlike '<svg xmlns="http://www.w3.org/2000/svg" class="glyph"*') { throw "$path does not open with the 24 x 24 svg element the vocabulary draws" }
                $svgs[$id] = $svg.Replace('class="glyph"', "class=`"glyph`" data-glyph=`"$id`"")
            }
            $index["$document|$name"] = $svgs[$id]
        }
    }
    return $index
}
