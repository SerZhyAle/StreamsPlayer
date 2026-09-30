<#
    SP-0140: the DOC-INTERNAL-QUALITY gate (shared contract, documentation-quality domain, rules 1-7).

    Reads DOCUMENT_REGISTRY.jsonl at the repository root and judges every documentation file in the tree:

      registry   rule 1 - every entry is well-formed: topic, product area, one or more update triggers, a
                          role (source/render); a render entry names its generator and never is hand-edited.
      coverage   rule 2 - both ways: every declared path exists, and every documentation file (*.md anywhere,
                          *.html under docs/) is declared by exactly one entry or by an explicit ignore entry.
      links      rule 3 - every relative link and heading anchor in a source Markdown document resolves, and
                          never into a path git does not track (the planning folder is untracked on purpose).
      assets     rule 6 - every image a source document cites exists as a tracked file.
      style      rule 5 - no "...", ellipsis, em-dash or en-dash in prose (code spans and fences excluded).
      offline    rule 7 - no http:// reference, no embedded script, iframe, remote stylesheet/font or remote
                          image in a source document.

    Rule 4 (generated documentation in sync) is tools/site/build-site.ps1 -Check; scripts/check.ps1 and CI run
    both.

    Exit codes (CHECK-VERDICT): 0 passed, 1 a finding, 2 could not verify (no git, no or unreadable registry).
    The run never stops at the first finding; the last line is the verdict:
        docs-quality: PASS (<n> documents, <m> links)
        docs-quality: FAIL (<n> finding(s))
        docs-quality: CANNOT VERIFY (<reason>)

    Usage:
        pwsh -NoProfile -File scripts/check-docs.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$registryName = 'DOCUMENT_REGISTRY.jsonl'
$findings = [System.Collections.Generic.List[string]]::new()

function Add-Finding([string] $Rule, [string] $Where, [string] $Message) {
    $findings.Add("[$Rule] $Where - $Message")
}

function Exit-CannotVerify([string] $Reason) {
    Write-Host "docs-quality: $Reason" -ForegroundColor Red
    Write-Host "docs-quality: CANNOT VERIFY ($Reason)"
    exit 2
}

# ---------------------------------------------------------------- the file set git sees

# Tracked files plus untracked-but-not-ignored ones: a document written a minute ago must already be declared,
# and a gitignored path (PLAN/, bin/) is exactly what a clone never has.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
try { $gitOutput = & git -C $root -c core.quotepath=off ls-files --cached --others --exclude-standard }
catch { Exit-CannotVerify "git is not available: $($_.Exception.Message)" }
if ($LASTEXITCODE -ne 0) { Exit-CannotVerify "git ls-files failed (exit $LASTEXITCODE)" }
$files = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in @($gitOutput)) {
    $path = ([string] $line).Trim().Trim('"')
    if ($path -and (Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf)) { [void] $files.Add($path) }
}
if ($files.Count -eq 0) { Exit-CannotVerify 'git reported no files' }

$directories = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in $files) {
    $parts = $path.Split('/')
    for ($i = 1; $i -lt $parts.Length; $i++) { [void] $directories.Add(($parts[0..($i - 1)] -join '/')) }
}

$documents = @($files | Where-Object { $_ -match '\.md$' -or $_ -match '^docs/.+\.html$' } | Sort-Object)

# ---------------------------------------------------------------- rule 1: the registry

$registryPath = Join-Path $root $registryName
if (-not (Test-Path -LiteralPath $registryPath)) { Exit-CannotVerify "$registryName not found at the repository root" }

function ConvertTo-GlobRegex([string] $Glob) {
    $escaped = [regex]::Escape($Glob) -replace '\\\*\\\*/', '(?:.+/)?' -replace '\\\*\\\*', '.*' -replace '\\\*', '[^/]*'
    return [regex]::new("^$escaped$", 'IgnoreCase')
}

$entries = [System.Collections.Generic.List[object]]::new()
$lineNumber = 0
foreach ($raw in [System.IO.File]::ReadAllLines($registryPath)) {
    $lineNumber++
    if (-not $raw.Trim()) { continue }
    $where = "${registryName}:$lineNumber"
    try { $record = $raw | ConvertFrom-Json -AsHashtable }
    catch { Add-Finding 'registry' $where "not valid JSON: $($_.Exception.Message)"; continue }

    if ($record.ContainsKey('ignore')) {
        if (-not $record['reason']) { Add-Finding 'registry' $where "ignore entry '$($record['ignore'])' has no reason" }
        $entries.Add([pscustomobject]@{ Kind = 'ignore'; Pattern = [string] $record['ignore']; Regex = (ConvertTo-GlobRegex $record['ignore']); Where = $where; Role = 'ignore'; Matched = 0 })
        continue
    }

    $path = [string] $record['path']
    if (-not $path) { Add-Finding 'registry' $where 'entry has neither "path" nor "ignore"'; continue }
    foreach ($field in 'topic', 'area') {
        if (-not ([string] $record[$field]).Trim()) { Add-Finding 'registry' $where "$path has no $field" }
    }
    $triggers = @($record['triggers'] | Where-Object { ([string] $_).Trim() })
    if ($triggers.Count -eq 0) { Add-Finding 'registry' $where "$path has no update trigger" }
    $role = [string] $record['role']
    if ($role -notin 'source', 'render') { Add-Finding 'registry' $where "$path has role '$role' (expected source or render)" }
    if ($role -eq 'render' -and -not ([string] $record['generator']).Trim()) { Add-Finding 'registry' $where "render entry $path names no generator" }
    if ($role -eq 'source' -and $path.Contains('*')) { Add-Finding 'registry' $where "source entry $path is a pattern - a hand-written document is declared by its own path" }

    $entries.Add([pscustomobject]@{ Kind = 'path'; Pattern = $path; Regex = (ConvertTo-GlobRegex $path); Where = $where; Role = $role; Matched = 0 })
}

# ---------------------------------------------------------------- rule 2: reverse coverage

$sources = [System.Collections.Generic.List[string]]::new()
foreach ($document in $documents) {
    $owners = @($entries | Where-Object { $_.Regex.IsMatch($document) })
    foreach ($owner in $owners) { $owner.Matched++ }
    $declared = @($owners | Where-Object Kind -eq 'path')
    if ($owners.Count -eq 0) {
        Add-Finding 'coverage' $document "not declared in $registryName (add an entry, or an ignore entry with a reason)"
    } elseif ($declared.Count -gt 1) {
        Add-Finding 'coverage' $document "declared by $($declared.Count) entries ($(($declared.Where) -join ', ')) - one home per document"
    } elseif ($declared.Count -eq 1 -and $declared[0].Role -eq 'source' -and $document -match '\.md$') {
        $sources.Add($document)
    }
}
foreach ($entry in $entries) {
    if ($entry.Matched -gt 0) { continue }
    if ($entry.Kind -eq 'path' -and -not $entry.Pattern.Contains('*') -and $files.Contains($entry.Pattern)) {
        Add-Finding 'coverage' $entry.Where "$($entry.Pattern) is declared but is not a documentation file (*.md, docs/**/*.html)"
    } else {
        Add-Finding 'coverage' $entry.Where "$($entry.Kind) '$($entry.Pattern)' matches no file in the tree"
    }
}

# ---------------------------------------------------------------- rules 3, 5, 6, 7: the source documents

function Get-ProseLines([string] $Path) {
    # Returns the lines with fenced blocks blanked and inline code spans removed, index-aligned with the file.
    $lines = [System.IO.File]::ReadAllLines($Path)
    $result = [string[]]::new($lines.Length)
    $fence = $null
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $marker = [regex]::Match($line, '^\s{0,3}(`{3,}|~{3,})')
        if ($fence) {
            if ($marker.Success -and $marker.Groups[1].Value[0] -eq $fence[0] -and $marker.Groups[1].Value.Length -ge $fence.Length) { $fence = $null }
            $result[$i] = ''
            continue
        }
        if ($marker.Success) { $fence = $marker.Groups[1].Value; $result[$i] = ''; continue }
        $result[$i] = [regex]::Replace($line, '(`+)(?:(?!\1).)+?\1', '')
    }
    return , $result
}

$slugCache = @{}
function Get-HeadingSlugs([string] $RelativePath) {
    if ($slugCache.ContainsKey($RelativePath)) { return $slugCache[$RelativePath] }
    $slugs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seen = @{}
    $fence = $null
    foreach ($line in [System.IO.File]::ReadAllLines((Join-Path $root $RelativePath))) {
        $marker = [regex]::Match($line, '^\s{0,3}(`{3,}|~{3,})')
        if ($fence) { if ($marker.Success -and $marker.Groups[1].Value[0] -eq $fence[0]) { $fence = $null }; continue }
        if ($marker.Success) { $fence = $marker.Groups[1].Value; continue }
        foreach ($explicit in [regex]::Matches($line, '<a\s+(?:name|id)\s*=\s*"([^"]+)"')) { [void] $slugs.Add($explicit.Groups[1].Value) }
        $heading = [regex]::Match($line, '^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$')
        if (-not $heading.Success) { continue }
        # GitHub's slug: the rendered text, lower-cased, punctuation dropped, spaces to hyphens, repeats numbered.
        $text = $heading.Groups[1].Value
        $text = [regex]::Replace($text, '!?\[([^\]]*)\]\([^)]*\)', '$1')
        $text = [regex]::Replace($text, '<[^>]+>', '')
        $text = $text.Replace('`', '').Replace('*', '')
        $slug = [regex]::Replace($text.ToLowerInvariant(), '[^\p{L}\p{Mn}\p{Nd}\p{Pc} -]', '').Replace(' ', '-')
        if ($seen.ContainsKey($slug)) { $seen[$slug]++; $slug = "$slug-$($seen[$slug])" } else { $seen[$slug] = 0 }
        [void] $slugs.Add($slug)
    }
    $slugCache[$RelativePath] = $slugs
    return $slugs
}

function Resolve-RepositoryPath([string] $FromDocument, [string] $Target) {
    $base = if ($Target.StartsWith('/')) { '' } else { [System.IO.Path]::GetDirectoryName($FromDocument).Replace('\', '/') }
    $segments = [System.Collections.Generic.List[string]]::new()
    foreach ($part in (($base + '/' + $Target.TrimStart('/')) -split '/')) {
        if ($part -eq '' -or $part -eq '.') { continue }
        if ($part -eq '..') {
            if ($segments.Count -eq 0) { return $null }
            $segments.RemoveAt($segments.Count - 1)
            continue
        }
        $segments.Add($part)
    }
    return ($segments -join '/')
}

$linkPattern = [regex]::new('(?<image>!?)\[(?:[^\[\]]|\[[^\]]*\])*\]\(\s*(?<target><[^>]*>|[^)\s]+)(?:\s+"[^"]*")?\s*\)')
$referencePattern = [regex]::new('^\s{0,3}\[[^\]]+\]:\s*(?<target><[^>]*>|\S+)')
$htmlPattern = [regex]::new('<(?<tag>a|img|script|iframe|link|source|video|audio)\b[^>]*?\b(?<attribute>href|src)\s*=\s*["''](?<target>[^"'']*)["'']', 'IgnoreCase')
$linkCount = 0

foreach ($document in $sources) {
    $prose = Get-ProseLines (Join-Path $root $document)
    for ($i = 0; $i -lt $prose.Length; $i++) {
        $line = $prose[$i]
        if (-not $line) { continue }
        $where = "${document}:$($i + 1)"

        # rule 5 - house style in prose
        if ($line -match '\.\.\.|…') { Add-Finding 'style' $where 'three-dot ellipsis in prose (house style is "..")' }
        if ($line -match '[–—]') { Add-Finding 'style' $where 'en- or em-dash in prose (house style is a plain hyphen)' }

        # rule 7 - offline safety
        if ($line -match '(?i)http://') { Add-Finding 'offline' $where 'http:// reference (use https)' }
        if ($line -match '(?i)<(script|iframe)\b') { Add-Finding 'offline' $where "embedded <$($Matches[1].ToLowerInvariant())>" }
        if ($line -match '(?i)@import\b|@font-face\b|fonts\.googleapis\.com') { Add-Finding 'offline' $where 'remote font or stylesheet import' }

        $targets = [System.Collections.Generic.List[object]]::new()
        foreach ($match in $linkPattern.Matches($line)) { $targets.Add(@{ Target = $match.Groups['target'].Value; Image = [bool] $match.Groups['image'].Value; Tag = '' }) }
        $reference = $referencePattern.Match($line)
        if ($reference.Success) { $targets.Add(@{ Target = $reference.Groups['target'].Value; Image = $false; Tag = '' }) }
        foreach ($match in $htmlPattern.Matches($line)) {
            $tag = $match.Groups['tag'].Value.ToLowerInvariant()
            $targets.Add(@{ Target = $match.Groups['target'].Value; Image = ($tag -ne 'a'); Tag = $tag })
        }

        foreach ($item in $targets) {
            $target = $item.Target.Trim('<', '>').Trim()
            if (-not $target) { continue }
            $linkCount++
            $remote = $target -match '^(?i)(https?:)?//'
            if ($target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:' -and -not $remote) { continue }   # mailto:, and the like
            if ($remote) {
                if ($item.Image) { Add-Finding 'offline' $where "remote embed $target (an image, script or stylesheet must be a tracked file)" }
                continue
            }

            $hashIndex = $target.IndexOf('#')
            $pathPart = if ($hashIndex -ge 0) { $target.Substring(0, $hashIndex) } else { $target }
            $anchor = if ($hashIndex -ge 0) { [uri]::UnescapeDataString($target.Substring($hashIndex + 1)) } else { '' }
            $pathPart = [uri]::UnescapeDataString(($pathPart -split '\?')[0])
            $rule = if ($item.Image) { 'assets' } else { 'links' }

            $resolved = if ($pathPart) { Resolve-RepositoryPath $document $pathPart } else { $document }
            if ($null -eq $resolved) { Add-Finding $rule $where "$target leaves the repository"; continue }
            $isFile = $files.Contains($resolved)
            $isDirectory = ($resolved -eq '') -or $directories.Contains($resolved.TrimEnd('/'))
            if (-not $isFile -and -not $isDirectory) {
                $onDisk = $resolved -and (Test-Path -LiteralPath (Join-Path $root $resolved))
                $reason = if ($onDisk) { 'is not tracked by git (a clone never has it)' } else { 'does not exist' }
                Add-Finding $rule $where "$target -> $resolved $reason"
                continue
            }
            if ($anchor -and $isFile -and $resolved -match '\.md$') {
                if (-not (Get-HeadingSlugs $resolved).Contains($anchor)) { Add-Finding 'links' $where "$target - no heading with anchor #$anchor in $resolved" }
            }
        }
    }
}

# ---------------------------------------------------------------- verdict

if ($findings.Count -gt 0) {
    foreach ($finding in $findings) { Write-Host "  $finding" -ForegroundColor Red }
    Write-Host "docs-quality: FAIL ($($findings.Count) finding(s))"
    exit 1
}
Write-Host "docs-quality: PASS ($($documents.Count) documents, $($sources.Count) source documents checked, $linkCount links)"
exit 0
