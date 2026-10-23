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

    The portal contribution (docs/portal/*.md, the exchange guide sections handed to the shared media exchange portal)
    is also judged against DOC-EXTERNAL-QUALITY, SP-0239 P5:

      terms      rule 4 - a control name must be exactly a string of the interface. The locale comes from the file
                          name (streamsplayer.md = en, streamsplayer.ru.md = ru, streamsplayer.uk.md = uk) and the
                          reference is src/StreamsPlayer.App/Localization.<locale>.xaml, the decoded value of any key,
                          compared ordinally. Recognition syntax: in prose (code spans and fences excluded) every bold
                          span **Name** and every guillemet pair «Name» is a control name. Nothing else in those files
                          may be bold or guillemet-quoted; any other form of quotation is not recognized and escapes
                          the rule. A control name is never inflected inside its marks: "in **Settings**" is written
                          "open **Settings**" or "in the «Settings» window", the case ending stays outside.
      freshness  rule 3 - a translation records the fingerprint of the English text it was rebuilt from. In
                          DOCUMENT_REGISTRY.jsonl the entry of docs/portal/<name>.<ru|uk>.md carries
                          "mirrorOf":"docs/portal/<name>.md" and "mirrorOfHash":"sha256:<16 hex>" (the form of
                          scripts/SourceFingerprint.ps1). It fails when the field is missing or malformed, or when the
                          English file now hashes to something else: rebuild the translation from the current English
                          text, then record the new fingerprint named in the finding. Any other entry that carries
                          mirrorOf is held to the same check.

    Exit codes (CHECK-VERDICT): 0 passed, 1 a finding, 2 could not verify (no git, no or unreadable registry, a portal
    page with no dictionary to judge it against).
    The run never stops at the first finding; the last line is the verdict:
        docs-quality: PASS (<n> documents, <m> links)
        docs-quality: FAIL (<n> finding(s))
        docs-quality: COULD NOT VERIFY (<reason>)

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
    Write-Host "docs-quality: COULD NOT VERIFY ($Reason)"
    exit 2
}

# A terminating error nothing below anticipated (an unreadable document, a failing file API) stops the run before it can
# judge: could not verify, with a verdict line, not a stack trace and exit 1 (CHECK-VERDICT 2.1, 2.5, 2.11). Findings
# already recorded still outrank it (2.14).
trap {
    $reason = $_.Exception.Message -replace '\s+', ' '
    if ($findings.Count -eq 0) { Exit-CannotVerify $reason }
    foreach ($finding in $findings) { Write-Host "  $finding" -ForegroundColor Red }
    Write-Host "docs-quality: the run stopped early: $reason"
    Write-Host "docs-quality: FAIL ($($findings.Count) finding(s))"
    exit 1
}

# The fingerprint of the translation-freshness rule has one definition, shared with tools/help/build-help.ps1 -Check.
. (Join-Path $PSScriptRoot 'SourceFingerprint.ps1')

# ---------------------------------------------------------------- the file set git sees

# Tracked files plus untracked-but-not-ignored ones: a document written a minute ago must already be declared,
# and a gitignored path (PLAN/, bin/) is exactly what a clone never has.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
try { $gitOutput = & git -C $root -c core.quotepath=off ls-files --cached --others --exclude-standard }
catch { Exit-CannotVerify "git is not available: $($_.Exception.Message)" }
if ($LASTEXITCODE -ne 0) { Exit-CannotVerify "git ls-files failed (exit $LASTEXITCODE)" }
# SP-0184 (A2-5): ordinal, not case-insensitive. A link whose letter case differs from the tracked path resolves
# on a Windows disk but 404s on GitHub Pages and on a Linux clone, so the set that judges links must be exact.
$files = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in @($gitOutput)) {
    $path = ([string] $line).Trim().Trim('"')
    if ($path -and (Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf)) { [void] $files.Add($path) }
}
if ($files.Count -eq 0) { Exit-CannotVerify 'git reported no files' }

$directories = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
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
        $entries.Add([pscustomobject]@{ Kind = 'ignore'; Pattern = [string] $record['ignore']; Regex = (ConvertTo-GlobRegex $record['ignore']); Where = $where; Role = 'ignore'; Matched = 0; Record = $record })
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

    $entries.Add([pscustomobject]@{ Kind = 'path'; Pattern = $path; Regex = (ConvertTo-GlobRegex $path); Where = $where; Role = $role; Matched = 0; Record = $record })
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
                # The outer @() is what keeps this an array: an empty `if` result collapses to $null, and under
                # strict mode $null.Count throws instead of letting the finding below say "does not exist".
                $caseMatch = @(if ($resolved) { $files | Where-Object { $_ -ieq $resolved } | Select-Object -First 1 })
                $reason = if ($caseMatch.Count -gt 0) { "differs in letter case from the tracked path $($caseMatch[0]) (it resolves on Windows only)" }
                          elseif ($onDisk) { 'is not tracked by git (a clone never has it)' }
                          else { 'does not exist' }
                Add-Finding $rule $where "$target -> $resolved $reason"
                continue
            }
            if ($anchor -and $isFile -and $resolved -match '\.md$') {
                if (-not (Get-HeadingSlugs $resolved).Contains($anchor)) { Add-Finding 'links' $where "$target - no heading with anchor #$anchor in $resolved" }
            }
        }
    }
}

# ---------------------------------------------------------------- DOC-EXTERNAL-QUALITY rule 4: terms

$xamlNamespace = 'http://schemas.microsoft.com/winfx/2006/xaml'
$dictionaryCache = @{}
function Get-InterfaceStrings([string] $Locale) {
    if (-not $dictionaryCache.ContainsKey($Locale)) {
        $relative = "src/StreamsPlayer.App/Localization.$Locale.xaml"
        if (-not (Test-Path -LiteralPath (Join-Path $root $relative))) { throw "no $relative to judge the control names of the $Locale portal page against" }
        $xml = [xml] [System.IO.File]::ReadAllText((Join-Path $root $relative))
        $strings = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($node in $xml.DocumentElement.ChildNodes) {
            if ($node.NodeType -eq 'Element' -and $node.GetAttribute('Key', $xamlNamespace)) { [void] $strings.Add($node.InnerText) }
        }
        $dictionaryCache[$Locale] = $strings
    }
    return , $dictionaryCache[$Locale]
}

$portalPages = @($files | Where-Object { $_ -match '^docs/portal/[^/]+\.md$' } | Sort-Object)
$controlNamePattern = [regex]::new('\*\*(?<bold>[^*]+)\*\*|«(?<quoted>[^»]+)»')
$termCount = 0
foreach ($page in $portalPages) {
    $locale = if ($page -match '\.(?<code>[a-z]{2})\.md$') { $Matches['code'] } else { 'en' }
    $prose = Get-ProseLines (Join-Path $root $page)
    for ($i = 0; $i -lt $prose.Length; $i++) {
        foreach ($match in $controlNamePattern.Matches($prose[$i])) {
            $name = $match.Groups['bold'].Value + $match.Groups['quoted'].Value
            $termCount++
            if (-not (Get-InterfaceStrings $locale).Contains($name)) {
                Add-Finding 'terms' "${page}:$($i + 1)" "'$name' is no string of Localization.$locale.xaml (quote a control exactly as the interface shows it; the case ending stays outside the marks)"
            }
        }
    }
}

# ---------------------------------------------------------------- DOC-EXTERNAL-QUALITY rule 3: freshness

$fingerprintForm = '^sha256:[0-9a-f]{16}$'
$mirrorCount = 0
foreach ($entry in @($entries | Where-Object Kind -eq 'path')) {
    $record = $entry.Record
    $expectedSource = if ($entry.Pattern -match '^docs/portal/(?<stem>[^/]+?)\.[a-z]{2}\.md$') { "docs/portal/$($Matches['stem']).md" } else { '' }
    if (-not $expectedSource -and -not $record.ContainsKey('mirrorOf') -and -not $record.ContainsKey('mirrorOfHash')) { continue }
    $mirrorCount++

    $source = [string] $record['mirrorOf']
    $recorded = [string] $record['mirrorOfHash']
    if (-not $source) { Add-Finding 'freshness' $entry.Pattern "$($entry.Where) has no mirrorOf (the English source $expectedSource it is rebuilt from)"; continue }
    if ($expectedSource -and $source -cne $expectedSource) { Add-Finding 'freshness' $entry.Pattern "$($entry.Where) names $source as its source (expected $expectedSource)"; continue }
    if (-not $files.Contains($source)) { Add-Finding 'freshness' $entry.Pattern "$($entry.Where) names $source as its source, which is no tracked file"; continue }
    if ($recorded -notmatch $fingerprintForm) { Add-Finding 'freshness' $entry.Pattern "$($entry.Where) has no mirrorOfHash of the form sha256:<16 hex> (the fingerprint of $source it was rebuilt from)"; continue }
    $current = Get-SourceFingerprint (Join-Path $root $source)
    if ($current -ne $recorded) {
        Add-Finding 'freshness' $entry.Pattern "$source is $current but the mirror was rebuilt from $recorded - rebuild it from the current English text, then record $current as mirrorOfHash in $($entry.Where)"
    }
}

# ---------------------------------------------------------------- verdict

if ($findings.Count -gt 0) {
    foreach ($finding in $findings) { Write-Host "  $finding" -ForegroundColor Red }
    Write-Host "docs-quality: FAIL ($($findings.Count) finding(s))"
    exit 1
}
Write-Host "docs-quality: PASS ($($documents.Count) documents, $($sources.Count) source documents checked, $linkCount links, $termCount control names, $mirrorCount mirrors)"
exit 0
