<#
    SP-0139: shared pieces of the pre-release code-audit campaign - which files are audited (class A, and
    class B behind a switch), how a file is counted, and which risk signals are counted in it.

    Everything that decides membership or risk is a table at the top of this module, so a new signal or a
    new class-A location is one row, not a change to the slicing. The three campaign tools import it:

        New-AuditSlices.ps1        the slicer (manifest)
        New-AuditSliceTickets.ps1  the fan-out (one ticket per slice)
        Get-AuditCampaign.ps1      the summary (coverage, statuses, findings; exit code)

    The method the tickets carry is docs/agent/CODE_AUDIT.md.
#>

Set-StrictMode -Version Latest

# Class A - the shipping code. Each rule: the project a file belongs to, a root directory, a file pattern,
# whether to recurse, and an optional exclusion on the file name.
$script:ClassARules = @(
    @{ Project = 'Core';    Root = 'src/StreamsPlayer.Core';            Filter = '*.cs';   Recurse = $true }
    @{ Project = 'App';     Root = 'src/StreamsPlayer.App';             Filter = '*.cs';   Recurse = $true }
    @{ Project = 'App';     Root = 'src/StreamsPlayer.App';             Filter = '*.xaml'; Recurse = $true; ExcludeName = '^Localization\.[A-Za-z-]+\.xaml$' }
    @{ Project = 'Harness'; Root = 'tools/StreamsPlayer.CatalogHarness'; Filter = '*.cs';   Recurse = $true }
    @{ Project = 'Tooling'; Root = '.';                                 Filter = '*.ps1';  Recurse = $false }
    @{ Project = 'Tooling'; Root = '.';                                 Filter = 'Directory.Build.*'; Recurse = $false }
    @{ Project = 'Tooling'; Root = 'scripts';                           Filter = '*.ps1';  Recurse = $false }
    @{ Project = 'Tooling'; Root = 'tools';                             Filter = 'build-*.ps1'; Recurse = $false }
    @{ Project = 'Tooling'; Root = '.github/workflows';                 Filter = '*.yml';  Recurse = $false }
    @{ Project = 'Tooling'; Root = 'src';                               Filter = '*.csproj'; Recurse = $true }
    @{ Project = 'Tooling'; Root = 'tools/StreamsPlayer.CatalogHarness'; Filter = '*.csproj'; Recurse = $false }
    @{ Project = 'Tooling'; Root = 'installer';                         Filter = '*.iss';  Recurse = $false }
    @{ Project = 'Tooling'; Root = 'msix';                              Filter = 'build-msix.ps1'; Recurse = $false }
    @{ Project = 'Tooling'; Root = 'msix';                              Filter = 'AppxManifest.xml'; Recurse = $false }
    @{ Project = 'Tooling'; Root = 'winget/templates';                  Filter = '*.yaml'; Recurse = $false }
)

# Class B - audited only behind -IncludeClassB: tests, the localization dictionaries, the site and Store
# tooling and these audit tools. Membership changes with the switch; the slicing does not.
$script:ClassBRules = @(
    @{ Project = 'Tests';        Root = 'tests';                 Filter = '*.cs';     Recurse = $true }
    @{ Project = 'Tests';        Root = 'tests';                 Filter = '*.csproj'; Recurse = $true }
    @{ Project = 'Localization'; Root = 'src/StreamsPlayer.App'; Filter = 'Localization.*.xaml'; Recurse = $false }
    @{ Project = 'DevTooling';   Root = 'tools';                 Filter = '*.ps1';    Recurse = $false; ExcludeName = '^build-' }
    @{ Project = 'DevTooling';   Root = 'tools/site';            Filter = '*.ps1';    Recurse = $true }
    @{ Project = 'DevTooling';   Root = 'tools/store';           Filter = '*.ps1';    Recurse = $true }
    @{ Project = 'DevTooling';   Root = 'tools/audit';           Filter = '*.ps*1';   Recurse = $false }
)

# Directories never walked: build output, VCS and IDE state, package staging.
$script:ExcludedSegments = @('bin', 'obj', '.git', '.vs', 'node_modules', 'stage', 'dist')

# The size budget from docs/agent/CODE_QUALITY.md; a file above it counts one "oversize" signal.
$script:LineBudget = 500

# Risk signals for C# code. Each is counted per match; the manifest lists every count per file.
$script:CSharpSignals = [ordered]@{
    asyncVoid     = '\basync\s+void\b'
    subscriptions = '\+=\s*(?:async\s*)?(?:\([^()]*\)\s*=>|[A-Za-z_]\w*\s*=>|new\s+[\w.]*Handler\b|On[A-Z]\w*\s*;|[A-Za-z_][\w.]*(?:Handler|Changed|Tick|Elapsed|Closed|Closing|Loaded|Completed|Received|Ended|Stopped|Error)\s*;)'
    timers        = '\bnew\s+(?:System\.Threading\.|System\.Timers\.|System\.Windows\.Threading\.)?(?:Dispatcher)?Timer\s*\(|\bnew\s+PeriodicTimer\s*\('
    ownership     = '\bnew\s+(?:LibVLC|MediaPlayer|Media|CancellationTokenSource|HttpClient|HttpClientHandler|SocketsHttpHandler|FileStream|StreamWriter|StreamReader|SemaphoreSlim|Mutex|NamedPipe\w*Stream|Process|ZipArchive)\s*\(|:\s*(?:[\w<>.,\s]*,\s*)?I(?:Async)?Disposable\b'
    dispatcher    = '\bDispatcher\s*\.\s*(?:Begin)?Invoke(?:Async)?\b|\bDispatcher\.Yield\b'
    sharedState   = '\block\s*\(|\bInterlocked\.|\bvolatile\b|\bstatic\s+(?!readonly\b|class\b|extern\b|partial\b|async\b)[\w.]+(?:<[^;=(){}]*>)?(?:\[\])?\??\s+[A-Za-z_]\w*\s*(?:=(?!>)|;)'
    network       = '\bHttpClient\b|\.(?:GetAsync|GetStreamAsync|GetByteArrayAsync|GetStringAsync|SendAsync)\s*\(|\bTcpClient\b|\bnew\s+Socket\b'
    fileWrites    = '\bFile\.(?:Write\w*|Append\w*|Move|Copy|Delete|Replace|Create\w*|Open)\s*\(|\bnew\s+FileStream\s*\(|\bDirectory\.(?:Delete|Move|CreateDirectory)\s*\(|\bnew\s+StreamWriter\s*\(|\bFileMode\.(?:Create|Append|OpenOrCreate|Truncate)\b'
    processLaunch = '\bProcess\.Start\s*\(|\bnew\s+ProcessStartInfo\b|\bUseShellExecute\b'
    untypedCatch  = '\bcatch\s*(?:\{|$)|\bcatch\s*\(\s*(?:System\.)?Exception(?:\s+\w+)?\s*\)(?!\s*when\b)'
    nullForgiving = '(?<=[\w\)\]])!(?=\s*[.;,)\[\]]|\s*$)'
}

# Risk signals for build, release and CI tooling (PowerShell, workflows, installer and package manifests).
$script:ToolingSignals = [ordered]@{
    destructive      = '\bRemove-Item\b|\bClear-Content\b|\[(?:System\.)?IO\.(?:File|Directory)\]::Delete\b|\brm\s+-|\bDelete(?:Files|Dirs)\b'
    publish          = '\bgit\s+(?:push|tag|commit|add)\b|\bgh\s+(?:release|pr|api)\b|\bwingetcreate\b|action-gh-release|\bupload-artifact\b|\bdeploy-pages\b'
    network          = '\bInvoke-WebRequest\b|\bInvoke-RestMethod\b|\bcurl\b|\bwget\b|DownloadFile|https?://'
    processLaunch    = '\bStart-Process\b|&\s*\$\w+|\bdotnet\s+(?:publish|build|test|run|restore)\b|\bInvoke-Expression\b|\biscc\b|\bmakeappx\b|\bsigntool\b'
    swallowedFailure = '-ErrorAction\s+(?:SilentlyContinue|Ignore)|catch\s*\{\s*\}|continue-on-error:\s*true|\|\|\s*true\b|2>\s*\$null|\$ErrorActionPreference\s*=\s*[''"](?:SilentlyContinue|Continue)'
    forceFlag        = '(?<![\w-])-Force\b|--force\b'
}

function Get-AuditSignalTable {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path)
    switch -Regex ($Path) {
        '\.cs$' { return $script:CSharpSignals }
        '\.(ps1|psm1|yml|yaml|iss|props|targets|csproj|xml)$' { return $script:ToolingSignals }
        default { return [ordered]@{} }
    }
}

function Get-AuditLineBudget { $script:LineBudget }

function ConvertTo-AuditRelativePath {
    param([string] $Root, [string] $FullName)
    $relative = [System.IO.Path]::GetRelativePath($Root, $FullName)
    return $relative.Replace('\', '/')
}

function Test-AuditExcludedPath {
    param([string] $RelativePath)
    foreach ($segment in $RelativePath.Split('/')) {
        if ($script:ExcludedSegments -contains $segment) { return $true }
    }
    return $false
}

<#
.SYNOPSIS
    Enumerate the audited files of a tree: class A always, class B with -IncludeClassB. Returns one object per
    file (Path, Project, Class), sorted by path with an ordinal comparison, each path at most once.
#>
function Get-AuditFileSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RepoRoot,
        [switch] $IncludeClassB
    )
    $root = (Resolve-Path -LiteralPath $RepoRoot).Path
    $rules = @($script:ClassARules | ForEach-Object { $_ + @{ Class = 'A' } })
    if ($IncludeClassB) { $rules += @($script:ClassBRules | ForEach-Object { $_ + @{ Class = 'B' } }) }

    $seen = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($rule in $rules) {
        $dir = Join-Path $root $rule.Root
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        $items = Get-ChildItem -LiteralPath $dir -Filter $rule.Filter -File -Recurse:$rule.Recurse -ErrorAction Stop
        foreach ($item in $items) {
            # -Filter is the Win32 matcher, where '*.ps1' also matches '.ps1xml'; re-test with the exact one.
            if (-not ($item.Name -like $rule.Filter)) { continue }
            $relative = ConvertTo-AuditRelativePath -Root $root -FullName $item.FullName
            if (Test-AuditExcludedPath $relative) { continue }
            if ($rule.ContainsKey('ExcludeName') -and $item.Name -match $rule.ExcludeName) { continue }
            # Class B never re-claims a class-A file (a class-A rule is more specific by construction).
            if ($seen.ContainsKey($relative)) { continue }
            $seen[$relative] = [pscustomobject]@{ Path = $relative; Project = $rule.Project; Class = $rule.Class }
        }
    }
    $keys = [string[]]$seen.Keys
    [System.Array]::Sort($keys, [System.StringComparer]::Ordinal)
    foreach ($key in $keys) { $seen[$key] }
}

<#
.SYNOPSIS
    Count one file: its lines (as File.ReadAllLines counts them), each risk signal, and the partial type it
    declares, if any.
#>
function Measure-AuditFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RepoRoot,
        [Parameter(Mandatory)][string] $Path
    )
    $full = Join-Path $RepoRoot $Path
    $lines = [System.IO.File]::ReadAllLines($full)
    $text = [string]::Join("`n", $lines)
    $signals = [ordered]@{}
    $table = Get-AuditSignalTable -Path $Path
    foreach ($name in $table.Keys) {
        $signals[$name] = [regex]::Matches($text, $table[$name], [System.Text.RegularExpressions.RegexOptions]::Multiline).Count
    }
    $signals['oversize'] = if ($lines.Length -gt $script:LineBudget) { 1 } else { 0 }

    $partialRoot = $null
    if ($Path -like '*.cs') {
        $m = [regex]::Match($text, '\bpartial\s+(?:class|record|struct)\s+([A-Za-z_]\w*)')
        if ($m.Success) { $partialRoot = $m.Groups[1].Value }
    }
    [pscustomobject]@{
        Path        = $Path
        Lines       = $lines.Length
        Signals     = $signals
        PartialType = $partialRoot
    }
}

<#
.SYNOPSIS
    Read a ticket's first **Status:** value (the word after the colon, before any dash or punctuation).
#>
function Get-AuditTicketStatus {
    param([Parameter(Mandatory)][string] $TicketPath)
    $line = Select-String -LiteralPath $TicketPath -Pattern '^\*\*Status:\*\*\s*`?([A-Za-z]+)' | Select-Object -First 1
    if ($null -eq $line) { return $null }
    return $line.Matches[0].Groups[1].Value
}

<#
.SYNOPSIS
    Find every ticket file under PLAN/ and PLAN/DONE/ (strategic tickets only, not tactical folders).
#>
function Get-AuditPlanTickets {
    param([Parameter(Mandatory)][string] $PlanRoot)
    $dirs = @($PlanRoot, (Join-Path $PlanRoot 'DONE')) | Where-Object { Test-Path -LiteralPath $_ -PathType Container }
    foreach ($dir in $dirs) {
        Get-ChildItem -LiteralPath $dir -Filter 'SP-*.md' -File | ForEach-Object {
            $m = [regex]::Match($_.Name, '^(SP-\d{4})_')
            if ($m.Success) { [pscustomobject]@{ Id = $m.Groups[1].Value; Path = $_.FullName; Name = $_.Name } }
        }
    }
}

Export-ModuleMember -Function Get-AuditFileSet, Measure-AuditFile, Get-AuditSignalTable, Get-AuditLineBudget,
    Get-AuditTicketStatus, Get-AuditPlanTickets
