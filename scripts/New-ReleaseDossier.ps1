#requires -Version 7.0
<#
.SYNOPSIS
    Gathers everything one release produced into ONE folder, checks every channel live, and writes README.md with
    what is done and what is left to do by hand.

.DESCRIPTION
    A release leaves its results in six places: the GitHub Release, the winget pull request, the Store package, the
    verdict files, the release notes and the site. This script is the one place that answers "where is it and what is
    its state". It changes nothing on any remote: it reads the release, the site, the winget pull request and (when
    msstore is configured) the Store submission status, and it writes only under artifacts/release-<version>/ and a
    copy of the Store package into msix/dist/ (the folder a person looks in first).

    The Store package is built in a scratch CLONE of the tag, never in the working tree: a tag's package must be the
    tag's own tree, and a working tree may hold edits that are not part of the release (a queue robot's, a person's).

    Layout of artifacts/release-<version>/
        README.md      the state of every channel, the paths, and the steps left by hand
        github/        the four release assets, re-hashed against their sidecars
        store/         the unsigned MSIX (upload this one), its sha256, the What's new texts, the manual steps
        winget/        the five manifests rendered from the tag and validated; the pull request address
        evidence/      the audit, smoke and Store preflight verdicts as the tag holds them

    Exit 0 when every automated item is in the state it should be after a release; 1 when one is not (the README says which).

.PARAMETER Version
    YY.MMDD.HHmm. Default: the version of the newest v* tag.

.PARAMETER SkipStorePackage
    Do not build the Store package (it takes about a minute); an existing msix/dist copy is only reported.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/New-ReleaseDossier.ps1
    pwsh -NoProfile -File ./scripts/New-ReleaseDossier.ps1 -Version 26.1112.1622
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $SkipStorePackage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

if (-not $Version) {
    $tag = (git describe --tags --abbrev=0 --match 'v*' 2>$null)
    if (-not $tag) { throw 'No v* tag found; pass -Version.' }
    $Version = $tag.Substring(1)
}
if ($Version -notmatch '^\d{2}\.\d{4}\.\d{4}$') { throw "Version '$Version' must use YY.MMDD.HHmm." }
$releaseDay = [datetime]::ParseExact($Version, 'yy.MMdd.HHmm', [Globalization.CultureInfo]::InvariantCulture).ToString('yyyy-MM-dd')
$tag = "v$Version"
$commit = (git rev-parse --verify "refs/tags/$tag^{commit}" 2>$null)
if (-not $commit) { throw "Tag $tag does not exist locally." }

$dossier = Join-Path $root "artifacts/release-$Version"
foreach ($sub in '', 'github', 'store', 'winget', 'evidence') { New-Item -ItemType Directory -Force (Join-Path $dossier $sub) | Out-Null }

$items = [System.Collections.Generic.List[object]]::new()
function Add-Item([string] $Channel, [string] $Item, [string] $State, [string] $Where = '', [string] $Note = '') {
    $items.Add([pscustomobject]@{ Channel = $Channel; Item = $Item; State = $State; Where = $Where; Note = $Note })
    Write-Host ('{0,-8} {1,-34} {2,-8} {3}' -f $Channel, $Item, $State, $Note)
}
function Step([string] $Channel, [string] $Item, [scriptblock] $Body) {
    try { & $Body }
    catch { Add-Item $Channel $Item 'FAIL' '' ($_.Exception.Message -replace '\s+', ' ') }
}
function Show-Path([string] $Path) { [IO.Path]::GetRelativePath($root, $Path).Replace('\', '/') }

# --- the tag --------------------------------------------------------------------------------------------------------
Step 'git' 'tag on origin' {
    $remote = (git ls-remote --tags origin $tag 2>$null)
    if ($remote) { Add-Item 'git' 'tag on origin' 'ok' '' "$tag -> $($commit.Substring(0, 7))" }
    else { Add-Item 'git' 'tag on origin' 'FAIL' '' "$tag is not on origin" }
}

# --- GitHub Release ---------------------------------------------------------------------------------------------------
$setupSha = $null
Step 'github' 'release assets' {
    $dir = Join-Path $dossier 'github'
    gh release download $tag --dir $dir --clobber 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "gh release download failed (exit $LASTEXITCODE)." }
    $names = @("StreamsPlayer-$Version-windows-x64.zip", "StreamsPlayer-$Version-windows-x64-setup.exe")
    foreach ($name in $names) {
        $path = Join-Path $dir $name
        $side = "$path.sha256"
        if (-not (Test-Path $path) -or -not (Test-Path $side)) { throw "Asset or sidecar missing: $name" }
        $want = ((Get-Content $side -Raw).Trim() -split '\s+')[0].ToUpperInvariant()
        $got = (Get-FileHash $path -Algorithm SHA256).Hash
        if ($want -ne $got) { throw "$name does not match its .sha256 sidecar." }
        if ($name -like '*setup.exe') { $script:setupSha = $got }
        Add-Item 'github' $name 'ok' (Show-Path $path) ('sha256 matches the sidecar, {0:N1} MB' -f ((Get-Item $path).Length / 1MB))
    }
}

# --- evidence and notes, read from the tag itself -----------------------------------------------------------------------
Step 'evidence' 'verdicts and notes' {
    $files = @("release-verdicts/$Version.audit.json", "release-verdicts/$Version.json", "release-verdicts/$Version.store-preflight.jsonl")
    foreach ($f in $files) {
        $target = Join-Path $dossier "evidence/$(Split-Path $f -Leaf)"
        git show "${tag}:$f" 2>$null | Set-Content -LiteralPath $target -Encoding utf8
        if ($LASTEXITCODE -ne 0 -or -not (Get-Item $target).Length) { Remove-Item $target -ErrorAction SilentlyContinue; Add-Item 'evidence' (Split-Path $f -Leaf) 'MISSING' '' "not in $tag"; continue }
        Add-Item 'evidence' (Split-Path $f -Leaf) 'ok' (Show-Path $target)
    }
    foreach ($lang in 'en-us', 'ru', 'uk') {
        $f = "msix/listing/release-notes/$Version.$lang.txt"
        $target = Join-Path $dossier "store/whats-new.$lang.txt"
        git show "${tag}:$f" 2>$null | Set-Content -LiteralPath $target -Encoding utf8
        if ($LASTEXITCODE -ne 0) { Add-Item 'store' "What's new $lang" 'MISSING' '' "not in $tag"; continue }
        Add-Item 'store' "What's new $lang" 'ok' (Show-Path $target)
    }
}

# --- the Store package, built from the tag in a scratch clone --------------------------------------------------------------
$storePackage = Join-Path $dossier "store/StreamsPlayer-$Version-windows-x64.msix"
Step 'store' 'unsigned MSIX' {
    if ($SkipStorePackage) {
        $existing = Join-Path $root "msix/dist/StreamsPlayer-$Version-windows-x64.msix"
        if (Test-Path $existing) { Copy-Item $existing $storePackage -Force; Add-Item 'store' 'unsigned MSIX' 'ok' (Show-Path $storePackage) 'copied from msix/dist, not rebuilt' }
        else { Add-Item 'store' 'unsigned MSIX' 'MISSING' '' 'run without -SkipStorePackage' }
        return
    }
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "sp-release-$Version"
    if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
    try {
        git clone --quiet --no-hardlinks $root $scratch 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'git clone of the tag failed.' }
        git -C $scratch checkout --quiet --detach $tag 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git checkout of $tag failed." }
        # The build script takes the version from the tag at HEAD, refuses a dirty tree and asserts the packed identity.
        pwsh -NoProfile -File (Join-Path $scratch 'msix/build-msix.ps1') 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "msix/build-msix.ps1 failed (exit $LASTEXITCODE); run it in a clone of $tag to read its output." }
        $built = Join-Path $scratch "msix/dist/StreamsPlayer-$Version-windows-x64.msix"
        if (-not (Test-Path $built)) { throw 'The build produced no unsigned package.' }
        Copy-Item $built $storePackage -Force
    }
    finally { if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue } }
    $hash = (Get-FileHash $storePackage -Algorithm SHA256).Hash
    Set-Content "$storePackage.sha256" "$hash  StreamsPlayer-$Version-windows-x64.msix" -Encoding ascii
    # The folder a person looks in first.
    $dist = Join-Path $root 'msix/dist'; New-Item -ItemType Directory -Force $dist | Out-Null
    Copy-Item $storePackage $dist -Force
    Add-Item 'store' 'unsigned MSIX' 'ok' (Show-Path $storePackage) ('built from {0} in a scratch clone; also copied to msix/dist; {1:N1} MB' -f $tag, ((Get-Item $storePackage).Length / 1MB))
}

# --- winget manifests ---------------------------------------------------------------------------------------------------
Step 'winget' 'manifests' {
    if (-not $setupSha) { throw 'No installer hash: the GitHub step did not finish.' }
    function Notes([string] $lang) {
        $p = Join-Path $dossier "store/whats-new.$lang.txt"
        if (-not (Test-Path $p)) { throw "whats-new.$lang.txt is missing." }
        $text = (Get-Content $p -Encoding utf8 | Select-Object -Skip 2 | Where-Object { $_.StartsWith('- ') } | ForEach-Object { $_.Substring(2).Trim() }) -join ' '
        if ($text.Contains(': ')) { throw "The $lang notes hold ': ', which breaks the plain YAML scalar; use a spaced hyphen." }
        $text
    }
    $out = Join-Path $dossier 'winget'
    Get-ChildItem $out -File -ErrorAction SilentlyContinue | Remove-Item -Force
    pwsh -NoProfile -File (Join-Path $root 'winget/New-WingetManifests.ps1') -Tag $tag -OutputDirectory (Join-Path $dossier 'winget-render') `
        -InstallerSha256 $setupSha -ReleaseDate $releaseDay -ReleaseNotes (Notes 'en-us') -ReleaseNotesRu (Notes 'ru') -ReleaseNotesUk (Notes 'uk') 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "winget/New-WingetManifests.ps1 failed (exit $LASTEXITCODE)." }
    Move-Item (Join-Path $dossier 'winget-render/*') $out -Force
    Remove-Item (Join-Path $dossier 'winget-render') -Force
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget validate --manifest $out 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'winget validate rejected the manifests.' }
        Add-Item 'winget' 'manifests' 'ok' (Show-Path $out) 'five files rendered from the tag, winget validate passed'
    }
    else { Add-Item 'winget' 'manifests' 'ok' (Show-Path $out) 'rendered; winget is not installed here, so not validated' }
}
Step 'winget' 'pull request' {
    $login = (gh api user --jq .login 2>$null)
    $found = gh pr list -R microsoft/winget-pkgs --author $login --state all --search "SerZhyAle.StreamsPlayer $Version in:title" --json number,state,url,title 2>$null | ConvertFrom-Json
    if ($found) {
        $pr = $found | Select-Object -First 1
        Add-Item 'winget' 'pull request' $pr.state.ToLowerInvariant() $pr.url "#$($pr.number): merged means live in a day; open means the bots or a moderator are working"
        Set-Content (Join-Path $dossier 'winget/PULL_REQUEST.txt') $pr.url -Encoding ascii
    }
    else { Add-Item 'winget' 'pull request' 'TODO' '' "none found: submit the manifests of $(Show-Path (Join-Path $dossier 'winget')) (winget/README.md)" }
}

# --- the site -----------------------------------------------------------------------------------------------------------
Step 'site' 'Pages' {
    $r = Invoke-WebRequest 'https://serzhyale.github.io/StreamsPlayer/whats-new.html' -UseBasicParsing -TimeoutSec 30
    if ($r.Content.Contains($Version)) { Add-Item 'site' 'What''s new page' 'ok' 'https://serzhyale.github.io/StreamsPlayer/whats-new.html' "lists $Version" }
    else { Add-Item 'site' 'What''s new page' 'WAIT' 'https://serzhyale.github.io/StreamsPlayer/whats-new.html' 'does not list the version yet; Pages deploys after release.yml succeeds' }
}

# --- the Store submission, read only --------------------------------------------------------------------------------------
$storeId = $null
try { $storeId = (Get-Content (Join-Path $root 'msix/identity.json') -Raw | ConvertFrom-Json).StoreId } catch { }
Step 'store' 'submission status' {
    if (-not $storeId) { Add-Item 'store' 'submission status' 'n/a' '' 'msix/identity.json names no StoreId'; return }
    if (-not (Get-Command msstore -ErrorAction SilentlyContinue)) { Add-Item 'store' 'submission status' 'n/a' '' 'msstore is not configured here; read the console'; return }
    $text = (msstore submission status $storeId 2>&1 | ForEach-Object { "$_" }) -join ' '
    $status = if ($text -match 'Submission Status = (\w+)') { $Matches[1] } else { 'unknown' }
    $note = if ($text -match 'Could not find a Pending Submission') { 'nothing pending, this is the last published submission' } else { 'a submission is pending' }
    Add-Item 'store' 'submission status' $status.ToLowerInvariant() "https://partner.microsoft.com/en-us/dashboard/products/$storeId/overview" $note
}

# --- README -------------------------------------------------------------------------------------------------------------------
$bad = @($items | Where-Object { $_.State -in 'FAIL', 'MISSING' })
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# Release $Version")
[void]$sb.AppendLine('')
[void]$sb.AppendLine("Tag ``$tag`` at ``$($commit.Substring(0, 7))``, released $releaseDay. Written by scripts/New-ReleaseDossier.ps1 at $((Get-Date).ToString('yyyy-MM-dd HH:mm')); run it again to refresh.")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| Channel | Item | State | Where | Note |')
[void]$sb.AppendLine('| --- | --- | --- | --- | --- |')
foreach ($i in $items) { [void]$sb.AppendLine("| $($i.Channel) | $($i.Item) | **$($i.State)** | $($i.Where) | $($i.Note) |") }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## Left to do by hand')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('**Microsoft Store** - the upload, the listing text and the submit stay in Partner Center (STORE_PUBLISHING.md, Phase 7; the machine channel cannot upload a package for this product).')
[void]$sb.AppendLine('')
[void]$sb.AppendLine("1. Open https://partner.microsoft.com/en-us/dashboard/products/$storeId/overview and press Start update (or continue the draft).")
[void]$sb.AppendLine("2. Packages: upload ``$(Show-Path $storePackage)`` (the unsigned one; its .sha256 is beside it). Never upload a file with -selfsigned in its name.")
[void]$sb.AppendLine('3. Store listings: replace What''s new with store/whats-new.en-us.txt, .ru.txt and .uk.txt for those languages; the other languages take the English text.')
[void]$sb.AppendLine('4. Submit for certification. Certification takes days; the status shows in the console and in this script''s Store line.')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('**winget** - nothing by hand while the pull request is open. A moderator or the bots act; do not push to it or re-run the submission.')
[void]$sb.AppendLine('')
if ($bad.Count) {
    [void]$sb.AppendLine('## Needs attention')
    [void]$sb.AppendLine('')
    foreach ($b in $bad) { [void]$sb.AppendLine("- $($b.Channel) / $($b.Item): $($b.Note)") }
}
[IO.File]::WriteAllText((Join-Path $dossier 'README.md'), $sb.ToString(), [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host "dossier: $(Show-Path $dossier)/README.md"
if ($bad.Count) { Write-Host "release-dossier: FAIL ($($bad.Count) item(s) need attention)"; exit 1 }
Write-Host 'release-dossier: PASS'
exit 0
