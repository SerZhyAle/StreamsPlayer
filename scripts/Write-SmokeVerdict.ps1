<#
.SYNOPSIS
  Record a playback smoke PASS as the release verdict the release workflow requires (SP-0156).

.DESCRIPTION
  The smoke gate runs only on the owner's machine, so its PASS travels to release.yml as a committed file:
  release-verdicts/<version>.json. The writer refuses to hand out a verdict it cannot stand behind:

    - the tree must be clean - a verdict about a dirty working tree is a verdict about no commit;
    - the version must be the Directory.Build.props stamp, YY.MMDD.HHmm;
    - the tested binary's FileVersion must be that same stamp - a PASS about some other build would
      otherwise wear this build's name (the SP-0093 lesson: a version number is not evidence, but a
      verdict must at least name the build it judged);
    - with -AppPath the tested binary can be any build that carries that stamp, and the stamp stays
      constant from release prep to the verdict, so the binary must also not predate HEAD: a binary
      last written before the commit cannot be that commit's build (SP-0212 L5).

  The commit recorded is HEAD - the commit whose tree, minus the verdict file itself, was just tested.
  The release checklist commits the verdict and puts the tag on that commit, so the verdict for exactly
  the tagged version sits inside the tagged tree, which is what release.yml checks.

  A red smoke run writes nothing: only a PASS can become a verdict, because this file's presence is the
  claim release.yml acts on.

.PARAMETER AppPath
  The StreamsPlayer.exe the gate just tested.

.EXAMPLE
  pwsh -NoProfile -File ./scripts/Write-SmokeVerdict.ps1 -AppPath artifacts/smoke/StreamsPlayer.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AppPath,

    # The repository the verdict is about. Defaults to the one this script lives in; the worktree form is
    # for observing the writer against a clean checkout that cannot yet carry the (uncommitted) writer
    # itself, on the -PublishedVersions terms of assert-release-version.ps1.
    [string] $Root,

    # SP-0184 (A2-4): the checks this run did not make (-SkipVideo, -SkipRecording). A PASS that skipped something
    # is recorded as such, and release.yml refuses it: the switches are for a developer machine, not for a release.
    [string[]] $Skipped = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path $PSScriptRoot -Parent }

$propsPath = Join-Path $Root 'Directory.Build.props'
$props = [xml](Get-Content -LiteralPath $propsPath -Raw)
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "Could not read <Version> from $propsPath." }
if ($version -notmatch '^\d{2}\.\d{4}\.\d{4}$') { throw "Version '$version' must use the house stamp YY.MMDD.HHmm." }

Push-Location $Root
try {
    $dirty = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    if ($dirty) {
        throw "The working tree is not clean - commit everything first. A verdict is recorded against a commit, and a dirty tree's payload is no commit's payload."
    }
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git rev-parse HEAD failed.' }
    $commitStamp = [datetimeoffset]::MinValue
    $commitStampText = (git log -1 --format=%cI) -join ''
    if ($LASTEXITCODE -ne 0 -or -not [datetimeoffset]::TryParse($commitStampText, [ref] $commitStamp)) {
        throw "Could not read HEAD's commit date (got '$commitStampText')."
    }
}
finally { Pop-Location }

$testedItem = Get-Item -LiteralPath $AppPath
$tested = $testedItem.VersionInfo.FileVersion
if ($tested -ne $version) {
    throw "expected: the tested binary stamped $version (Directory.Build.props) | actual: FileVersion '$tested' in $AppPath - this verdict would name a build it did not test."
}

# SP-0212 (L5): a matching FileVersion is necessary, not sufficient - the stamp is set in release prep and
# never changes until the next one, so every build of this candidate carries it, including ones made
# before the last commit. The verdict names HEAD, so the binary must not predate HEAD's commit.
$binaryStamp = [datetimeoffset]::new($testedItem.LastWriteTimeUtc)
if ($binaryStamp -lt $commitStamp) {
    throw "expected: a binary built from commit $commit | actual: $AppPath was last written $($binaryStamp.ToString('u')), before that commit ($($commitStamp.ToString('u'))) - this verdict would name code it did not test. Re-run scripts/smoke-playback.ps1 without -AppPath so the current tree is published fresh, or pass -AppPath for a build of this commit."
}

$verdict = [ordered]@{
    gate    = 'smoke-playback'
    result  = 'PASS'
    version = $version
    commit  = $commit
    when    = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    skipped = @($Skipped)
}
$folder = Join-Path $Root 'release-verdicts'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$path = Join-Path $folder "$version.json"
$verdict | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
Write-Host "Release verdict written: $path (commit $commit). Commit it and put the v$version tag on that commit." -ForegroundColor Green
