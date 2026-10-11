#requires -Version 7.0
<#
.SYNOPSIS
    SP-0212 (L4, L5): fixture tests for the smoke-verdict gate and writer (Assert-SmokeVerdict.ps1, Write-SmokeVerdict.ps1).

.DESCRIPTION
    Builds a throwaway git repository and judges the two scripts against it:

      gate      passes on a committed verdict that names HEAD; refuses a missing verdict, a verdict naming
                another gate, a result that is not PASS, a run that skipped a check, another version, a
                commit that is not a full id, a commit that is not an ancestor, code that changed after
                the smoke-tested commit outside release-verdicts/; passes again when only release-verdicts/
                changed afterwards
      writer    refuses a tested binary that predates HEAD's commit (SP-0212 L5: the version stamp stays
                constant from release prep to the verdict, so an older build of the same candidate would
                otherwise be recorded as this commit's PASS), writes the verdict for a binary written after
                the commit, refuses a dirty tree and a binary with another FileVersion

    The writer's binary is a one-file console build stamped with the fixture's own version, because the
    verdict is only meaningful for an executable that carries the stamp the writer compares.

    Exit 0 when every check passes, 1 otherwise. The last line is `smoke-verdict-gate: PASS|FAIL (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$gate = Join-Path $repo 'scripts/Assert-SmokeVerdict.ps1'
$writer = Join-Path $repo 'scripts/Write-SmokeVerdict.ps1'
$version = '26.1010.0106'

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}
function Invoke-Script([string] $Script, [string[]] $Arguments) {
    $out = & pwsh -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" }
    # The console renders a refusal with colour codes and wraps it across lines, marking each wrapped
    # line with a leading pipe; the checks match against text stripped of all three, so a phrase cannot
    # be cut in half by the rendering.
    $flat = ((($out -join ' ') -replace "`e\[[0-9;]*[A-Za-z]", ' ') -replace '\s*\|\s*', ' ') -replace '\s+', ' '
    [pscustomobject]@{ Exit = $LASTEXITCODE; Flat = $flat }
}
function Invoke-Git([string[]] $Arguments) {
    & git -C $script:fixture -c user.name=t -c user.email=t@example.invalid -c core.autocrlf=false @Arguments 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed" }
}
function Write-Verdict([object] $Verdict) {
    $folder = Join-Path $script:fixture 'release-verdicts'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $Verdict | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder "$version.json") -Encoding utf8
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0212-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0212-smoke-work-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'Directory.Build.props') -Value "<Project><PropertyGroup><Version>$version</Version></PropertyGroup></Project>"
    Set-Content -LiteralPath (Join-Path $fixture 'src.txt') -Value 'fixture'
    Invoke-Git @('init', '-q'); Invoke-Git @('add', '--all'); Invoke-Git @('commit', '-q', '-m', 'candidate')
    $head = (& git -C $fixture rev-parse HEAD).Trim()

    # --- the gate refuses what it cannot stand behind (SP-0212 L4) ---------------------------------------------
    $r = Invoke-Script $gate @('-Version', $version, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match 'No smoke verdict') 'a missing verdict fails the gate'

    Write-Verdict ([ordered]@{ gate = 'audit'; result = 'PASS'; version = $version; commit = $head; when = '2026-10-10T02:30:40+02:00'; skipped = @() })
    $r = Invoke-Script $gate @('-Version', $version, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match 'not smoke-playback') 'a verdict naming another gate fails'

    Write-Verdict ([ordered]@{ gate = 'smoke-playback'; result = 'FAIL'; version = $version; commit = $head; when = '2026-10-10T02:30:40+02:00'; skipped = @() })
    Check ((Invoke-Script $gate @('-Version', $version, '-Root', $fixture)).Exit -ne 0) 'a result that is not PASS fails'

    Write-Verdict ([ordered]@{ gate = 'smoke-playback'; result = 'PASS'; version = $version; commit = $head; when = '2026-10-10T02:30:40+02:00'; skipped = @('video') })
    $r = Invoke-Script $gate @('-Version', $version, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match 'skipped video') 'a PASS that skipped a check fails'

    Write-Verdict ([ordered]@{ gate = 'smoke-playback'; result = 'PASS'; version = '26.1010.0107'; commit = $head; when = '2026-10-10T02:30:40+02:00'; skipped = @() })
    $r = Invoke-Script $gate @('-Version', $version, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match '26.1010.0107') 'a verdict for another version fails'

    Write-Verdict ([ordered]@{ gate = 'smoke-playback'; result = 'PASS'; version = $version; commit = $head.Substring(0, 10); when = '2026-10-10T02:30:40+02:00'; skipped = @() })
    $r = Invoke-Script $gate @('-Version', $version, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match 'not a full commit id') 'a commit that is not a full id fails'

    Write-Verdict ([ordered]@{ gate = 'smoke-playback'; result = 'PASS'; version = $version; commit = '0123456789abcdef0123456789abcdef01234567'; when = '2026-10-10T02:30:40+02:00'; skipped = @() })
    $r = Invoke-Script $gate @('-Version', $version, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match 'ancestor') 'a commit that is not an ancestor fails'

    Write-Verdict ([ordered]@{ gate = 'smoke-playback'; result = 'PASS'; version = $version; commit = $head; when = '2026-10-10T02:30:40+02:00'; skipped = @() })
    Check ((Invoke-Script $gate @('-Version', $version, '-Root', $fixture)).Exit -eq 0) 'a committed verdict naming HEAD passes'

    Invoke-Git @('add', '--all'); Invoke-Git @('commit', '-q', '-m', 'verdict')
    Check ((Invoke-Script $gate @('-Version', $version, '-Root', $fixture)).Exit -eq 0) 'the gate passes when only release-verdicts/ changed afterwards'

    Set-Content -LiteralPath (Join-Path $fixture 'src.txt') -Value 'changed after the smoke'
    Invoke-Git @('add', '--all'); Invoke-Git @('commit', '-q', '-m', 'late change')
    $r = Invoke-Script $gate @('-Version', $version, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match 'changed after the smoke-tested commit') 'code changed after the smoke-tested commit fails'

    # --- the writer binds the tested binary to HEAD's commit (SP-0212 L5) --------------------------------------
    # Drop the gate section's committed verdict first, so a refused writer has no earlier file to point at,
    # and move HEAD: the writer records the commit that is HEAD when it runs.
    Invoke-Git @('rm', '-q', "release-verdicts/$version.json")
    Invoke-Git @('commit', '-q', '-m', 'drop the fixture verdict')
    $head = (& git -C $fixture rev-parse HEAD).Trim()
    $commitStamp = [datetimeoffset]::Parse((& git -C $fixture log -1 --format=%cI))

    # A one-file console build stamped with the fixture's own version: the writer compares FileVersion
    # against Directory.Build.props, so the fixture binary must carry the same stamp a real one would.
    $appDir = Join-Path $work 'smokeapp'
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $appDir 'smokeapp.csproj') -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <Version>$version</Version>
    <FileVersion>$version</FileVersion>
  </PropertyGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $appDir 'Program.cs') -Value 'internal static class Program { private static void Main() { } }'
    dotnet build (Join-Path $appDir 'smokeapp.csproj') -c Release --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The fixture application build failed.' }
    $exe = Join-Path $appDir 'bin/Release/net10.0/smokeapp.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Fixture executable not found at $exe" }
    $verdictPath = Join-Path $fixture "release-verdicts/$version.json"

    $stale = Join-Path $work 'stale-smokeapp.exe'
    Copy-Item -LiteralPath $exe -Destination $stale -Force
    (Get-Item -LiteralPath $stale).LastWriteTimeUtc = $commitStamp.UtcDateTime.AddHours(-1)
    $r = Invoke-Script $writer @('-AppPath', $stale, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match 'before that commit') 'a tested binary older than HEAD is refused'
    Check (-not (Test-Path -LiteralPath $verdictPath)) 'the refused binary writes no verdict'

    Set-Content -LiteralPath (Join-Path $fixture 'uncommitted.txt') -Value 'dirty'
    $fresh = Join-Path $work 'fresh-smokeapp.exe'
    Copy-Item -LiteralPath $exe -Destination $fresh -Force
    (Get-Item -LiteralPath $fresh).LastWriteTimeUtc = $commitStamp.UtcDateTime.AddMinutes(1)
    $r = Invoke-Script $writer @('-AppPath', $fresh, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match 'not clean') 'a dirty tree writes no verdict'
    Remove-Item -LiteralPath (Join-Path $fixture 'uncommitted.txt')

    $other = Join-Path $work 'not-an-exe.txt'
    Set-Content -LiteralPath $other -Value 'not a binary'
    $r = Invoke-Script $writer @('-AppPath', $other, '-Root', $fixture)
    Check ($r.Exit -ne 0 -and $r.Flat -match 'this verdict would name a build it did not test') 'a binary with another FileVersion is refused'

    if (Test-Path -LiteralPath $verdictPath) { Remove-Item -LiteralPath $verdictPath -Force }
    $r = Invoke-Script $writer @('-AppPath', $fresh, '-Root', $fixture)
    Check ($r.Exit -eq 0 -and (Test-Path -LiteralPath $verdictPath)) 'a binary written after the commit writes the verdict'
    if (Test-Path -LiteralPath $verdictPath) {
        $recorded = Get-Content -LiteralPath $verdictPath -Raw | ConvertFrom-Json
        Check ($recorded.commit -eq $head -and $recorded.version -eq $version) 'the verdict names the commit and the version it judged'
    }
    else { Check $false 'the verdict names the commit and the version it judged' }

    # --- the gate and the writer agree -------------------------------------------------------------------------
    Check ((Invoke-Script $gate @('-Version', $version, '-Root', $fixture)).Exit -eq 0) 'the gate passes on the verdict the writer just wrote'
}
finally {
    foreach ($dir in $fixture, $work) {
        if ($dir -and (Test-Path -LiteralPath $dir)) {
            Get-ChildItem -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Attributes = 'Normal' } catch { } }
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($failures.Count -eq 0) { "smoke-verdict-gate: PASS ($passed checks)"; exit 0 }
"smoke-verdict-gate: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
