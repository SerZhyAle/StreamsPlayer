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
      verdict must at least name the build it judged).

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
    [Parameter(Mandatory)] [string] $AppPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$propsPath = Join-Path $root 'Directory.Build.props'
$props = [xml](Get-Content -LiteralPath $propsPath -Raw)
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "Could not read <Version> from $propsPath." }
if ($version -notmatch '^\d{2}\.\d{4}\.\d{4}$') { throw "Version '$version' must use the house stamp YY.MMDD.HHmm." }

Push-Location $root
try {
    $dirty = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    if ($dirty) {
        throw "The working tree is not clean - commit everything first. A verdict is recorded against a commit, and a dirty tree's payload is no commit's payload."
    }
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git rev-parse HEAD failed.' }
}
finally { Pop-Location }

$tested = (Get-Item -LiteralPath $AppPath).VersionInfo.FileVersion
if ($tested -ne $version) {
    throw "expected: the tested binary stamped $version (Directory.Build.props) | actual: FileVersion '$tested' in $AppPath - this verdict would name a build it did not test."
}

$verdict = [ordered]@{
    gate    = 'smoke-playback'
    result  = 'PASS'
    version = $version
    commit  = $commit
    when    = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
}
$folder = Join-Path $root 'release-verdicts'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$path = Join-Path $folder "$version.json"
$verdict | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
Write-Host "Release verdict written: $path (commit $commit). Commit it and put the v$version tag on that commit." -ForegroundColor Green
