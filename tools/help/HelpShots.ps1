function Get-HelpShotName([string] $Page, [string] $Locale, [string] $Theme) {
    if ($Page -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or $Locale -cnotmatch '^[a-z]{2}$' -or
        $Theme -cnotin 'dark', 'light') {
        throw "Invalid help capture identity: $Page/$Locale/$Theme."
    }
    return "shot-$Page-$Locale-$Theme.png"
}

function Assert-HelpShotSet($Set, [string] $Page, [string[]] $Locales, [string] $ImageRoot) {
    $date = [datetime]::MinValue
    if ($Set.version -cnotmatch '^\d{2}\.\d{4}\.\d{4}$' -or -not [datetime]::TryParseExact(
        $Set.date, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $date)) {
        throw "$Page has an invalid capture version or date."
    }

    $seen = @{}
    foreach ($shot in $Set.shots) {
        $name = Get-HelpShotName $Page $shot.locale $shot.theme
        if ($shot.locale -cnotin $Locales -or $seen.ContainsKey($name) -or
            "$($shot.width)" -cnotmatch '^\d{1,4}$' -or "$($shot.height)" -cnotmatch '^\d{1,4}$' -or
            $shot.width -lt 1 -or $shot.width -gt 4096 -or $shot.height -lt 1 -or $shot.height -gt 4096) {
            throw "Unexpected or invalid capture $name."
        }
        $seen[$name] = $true
        $path = Join-Path $ImageRoot $name
        if ($shot.sha256 -cnotmatch '^[A-F0-9]{64}$' -or -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $shot.sha256) {
            throw "$name is missing or differs from the generated capture."
        }

        $markerIds = @{}
        foreach ($marker in $shot.markers) {
            if ($marker.id -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or $markerIds.ContainsKey($marker.id) -or
                -not [double]::IsFinite($marker.x) -or -not [double]::IsFinite($marker.y) -or
                -not [double]::IsFinite($marker.width) -or -not [double]::IsFinite($marker.height) -or
                $marker.x -lt 0 -or $marker.y -lt 0 -or $marker.width -le 0 -or $marker.height -le 0 -or
                $marker.x + $marker.width -gt $shot.width -or $marker.y + $marker.height -gt $shot.height) {
                throw "$name has a duplicate or out-of-bounds marker '$($marker.id)'."
            }
            $markerIds[$marker.id] = $true
        }
        if (!$markerIds.Count) { throw "$name has no markers." }
    }
    if ($seen.Count -ne 2 * $Locales.Count) { throw "$Page needs one light and one dark capture for each authored locale." }
}

function Format-HelpShot($Set, [string] $Page, [string] $Locale, [string] $Title, $Items, [hashtable] $Names, [bool] $Rtl) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('  <figure class="shot">')
    $culture = [cultureinfo]::InvariantCulture
    foreach ($shot in ($Set.shots | Where-Object locale -CEQ $Locale)) {
        if (($shot.markers.id -join ',') -cne ($Items.id -join ',')) {
            throw "$Locale/$Page has different help entries from its captured markers. Regenerate the pictures."
        }
        $name = Get-HelpShotName $Page $Locale $shot.theme
        $alt = Format-Text $Title
        $lines.Add("    <div class=`"frame on-$($shot.theme)`">")
        $lines.Add("      <img src=`"../$name`" width=`"$($shot.width)`" height=`"$($shot.height)`" alt=`"$alt`">")
        $number = 0
        foreach ($marker in $shot.markers) {
            $number++
            $edge = if ($Rtl) { $marker.x + $marker.width } else { $marker.x }
            $x = (100.0 * $edge / $shot.width).ToString('0.####', $culture)
            $y = (100.0 * ($marker.y + [math]::Min($marker.height / 2, 14)) / $shot.height).ToString('0.####', $culture)
            $label = Format-Text "$number, $($Names[$marker.id])"
            $lines.Add("      <a class=`"pin`" style=`"--x:$x%;--y:$y%`" href=`"#e-$($marker.id)`" aria-label=`"$label`">$number</a>")
        }
        $lines.Add('    </div>')
    }
    $lines.Add('  </figure>')
    return ($lines -join "`n")
}
