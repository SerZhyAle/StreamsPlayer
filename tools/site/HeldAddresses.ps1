<#
    SP-0193 (SITE-STRUCTURE rule 8): the list of site addresses held outside the site, and the gate that
    resolves it.

    The list is tools/site/held-addresses.json. Three things are checked, all offline and all decidable
    from the tree, which is what keeps the false-positive rate near zero:

      resolve   every listed address names a file the generator emits (a page, site.js, sitemap.xml,
                robots.txt); an address with a #fragment names an id that exists in that page;
      holder    every file an entry says holds it is tracked and still contains it, so the list cannot
                outlive the surface it describes;
      complete  every tracked file outside docs/'s generated set - minus the ignore rows, each with its
                reason - that mentions an address of this site is listed as a holder of exactly that
                address, so a new README line, listing or manifest cannot add an unlisted address.

    Not checked, on purpose: whether the page answers on the network (an offline gate would fail on a
    flaky connection) and whether the page says what the holder claims it does.

    Dot-source it from the site generator:
        . "$PSScriptRoot/HeldAddresses.ps1"
        $problems = Get-HeldAddressProblem -Root $root -BaseUrl $BaseUrl -GeneratedFile $relativePaths
#>

function Get-HeldAddressProblem {
    [CmdletBinding()]
    param(
        # Repository root; git runs here.
        [Parameter(Mandatory)] [string] $Root,
        # The site's base address, with the trailing slash (build-site.ps1 -BaseUrl).
        [Parameter(Mandatory)] [string] $BaseUrl,
        # Every file the generator emits, as a path relative to docs/ with forward slashes.
        [Parameter(Mandatory)] [string[]] $GeneratedFile,
        [string] $ListPath = (Join-Path $Root 'tools/site/held-addresses.json')
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    $listName = [System.IO.Path]::GetRelativePath($Root, $ListPath).Replace('\', '/')

    if (-not (Test-Path -LiteralPath $ListPath)) {
        $problems.Add("$listName does not exist - the held-address list is the gate's input.")
        return $problems.ToArray()
    }
    try {
        $list = [System.IO.File]::ReadAllText($ListPath) | ConvertFrom-Json
    } catch {
        $problems.Add("$listName is not valid JSON: $($_.Exception.Message)")
        return $problems.ToArray()
    }
    # StrictMode is on in the caller, so an absent property is read through PSObject, not by name.
    $entries = @(if ($list.PSObject.Properties['addresses']) { $list.addresses })
    $ignored = @(if ($list.PSObject.Properties['ignore']) { $list.ignore })
    if (-not $entries.Count) {
        $problems.Add("$listName lists no address.")
        return $problems.ToArray()
    }
    foreach ($row in $ignored) {
        if ([string]::IsNullOrWhiteSpace([string] $row.why)) {
            $problems.Add("$listName ignore row '$($row.path)' carries no reason.")
        }
    }

    $generated = [System.Collections.Generic.HashSet[string]]::new([string[]] $GeneratedFile)
    $hostPath = $BaseUrl -replace '^https?://', ''
    $hostPath = $hostPath.TrimEnd('/')

    # Splits an address into the docs/-relative file it names and its fragment; $null file means the
    # address is not under the base.
    function Split-SiteAddress([string] $Address) {
        $bare = $Address -replace '^https?://', ''
        if (-not $bare.StartsWith($hostPath, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
        $rest = $bare.Substring($hostPath.Length)
        if ($rest -ne '' -and [string] $rest[0] -notin '/', '#', '?') { return $null }   # another path that merely starts alike
        $fragment = ''
        $hash = $rest.IndexOf('#')
        if ($hash -ge 0) { $fragment = $rest.Substring($hash + 1); $rest = $rest.Substring(0, $hash) }
        $query = $rest.IndexOf('?')
        if ($query -ge 0) { $rest = $rest.Substring(0, $query) }
        $path = $rest.TrimStart('/')
        if ($path -eq '' -or $path.EndsWith('/')) { $path += 'index.html' }
        return [pscustomobject]@{ File = $path; Fragment = $fragment }
    }

    # ----- resolve + holder -------------------------------------------------------------------------
    $listedUrls = @{}   # normalized address -> its holders, for the completeness pass
    foreach ($entry in $entries) {
        $url = [string] $entry.url
        if ($url -notmatch '^https://') { $problems.Add("$listName entry '$url' is not an https:// address."); continue }
        $target = Split-SiteAddress $url
        if ($null -eq $target) {
            $problems.Add("$listName entry '$url' is not an address of this site ($BaseUrl); the list holds only this site's addresses.")
            continue
        }
        if ($listedUrls.ContainsKey($url)) { $problems.Add("$listName lists '$url' twice."); continue }
        $holders = @(if ($entry.PSObject.Properties['heldBy']) { $entry.heldBy })
        $listedUrls[$url] = $holders
        if (-not $holders.Count) { $problems.Add("$listName entry '$url' names no holder.") }

        if (-not $generated.Contains($target.File)) {
            $problems.Add("held address '$url' does not resolve: the generator emits no docs/$($target.File). Restore the page, add a forwarder at the old address, or update every holder and this list.")
        } elseif ($target.Fragment) {
            $page = Join-Path (Join-Path $Root 'docs') $target.File
            $html = if (Test-Path -LiteralPath $page) { [System.IO.File]::ReadAllText($page) } else { '' }
            $anchor = [regex]::Escape($target.Fragment)
            if ($html -notmatch "\sid=[`"']$anchor[`"']") {
                $problems.Add("held address '$url' does not resolve: docs/$($target.File) has no id=`"$($target.Fragment)`" (an anchor an outside surface links is never renamed).")
            }
        }
    }

    # ----- scan the tree once ------------------------------------------------------------------------
    $pathspecs = @('.') + @($GeneratedFile | ForEach-Object { ":(exclude)docs/$_" }) + @($ignored | ForEach-Object { ":(exclude)$($_.path)" }) + ":(exclude)$listName"
    $needle = [regex]::Escape($hostPath).Replace('\/', '/')
    $raw = & git -C $Root -c core.quotepath=off grep -I -i -n -o -E "$needle[^][:space:])>`"'``<,;*]*" -- @pathspecs 2>&1
    if ($LASTEXITCODE -gt 1) {
        $problems.Add("git grep failed (exit $LASTEXITCODE): $($raw -join ' ')")
        return $problems.ToArray()
    }

    $found = @{}   # file -> set of normalized addresses
    foreach ($line in @($raw | Where-Object { $_ })) {
        $first = $line.IndexOf(':'); $second = $line.IndexOf(':', $first + 1)
        if ($first -lt 1 -or $second -lt 0) { continue }
        $file = $line.Substring(0, $first)
        $match = $line.Substring($second + 1).TrimEnd('.', ':', '!', '?')
        $address = "https://$match"
        if ($null -eq (Split-SiteAddress $address)) { continue }
        if (-not $found.ContainsKey($file)) { $found[$file] = [System.Collections.Generic.HashSet[string]]::new() }
        # Normalize the host and base spelling to the base's, keep the path as written (the host is
        # case-insensitive, the path is not).
        $null = $found[$file].Add(($BaseUrl.TrimEnd('/') + $address.Substring("https://$hostPath".Length)))
    }

    # ----- holder: each named holder still holds it ----------------------------------------------------
    foreach ($url in $listedUrls.Keys) {
        foreach ($holder in $listedUrls[$url]) {
            $holds = $found.ContainsKey($holder) -and $found[$holder].Contains($url)
            if ($holds) { continue }
            $tracked = (& git -C $Root ls-files --error-unmatch -- $holder 2>&1) ; $isTracked = $LASTEXITCODE -eq 0
            $problems.Add($(if ($isTracked) { "$listName says $holder holds '$url', but it no longer does - update the list." }
                            else { "$listName says $holder holds '$url', but $holder is not a tracked file - update the list." }))
        }
    }

    # ----- complete: nothing holds an address the list does not know ------------------------------------
    foreach ($file in ($found.Keys | Sort-Object)) {
        foreach ($address in ($found[$file] | Sort-Object)) {
            if ($listedUrls.ContainsKey($address) -and $listedUrls[$address] -contains $file) { continue }
            $problems.Add("$file holds site address '$address' that $listName does not list for it - add it (it must resolve), or remove it from the file.")
        }
    }

    return $problems.ToArray()
}
