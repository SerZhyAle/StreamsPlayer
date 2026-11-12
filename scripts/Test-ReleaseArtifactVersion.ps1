#requires -Version 7.0
<#
.SYNOPSIS
    SP-0247 (PACKAGE-VERSIONING P5, W4): fixture tests for scripts/Assert-ReleaseArtifactVersion.ps1, the read-back
    release.yml runs on the staged exe, the zip and the setup EXE.

.DESCRIPTION
    The subject reads version resources, so the fixture needs a real PE file: a one-file console build named
    StreamsPlayer and stamped 26.0719.0131 - leading zeros on purpose, so the binary file version has to be the remap
    26.719.131.0 - built in a throwaway folder, the way scripts/Test-SmokeVerdictGate.ps1 builds its binary. Nothing
    depends on a build output of this repository.

      accepts   the exe; a zip holding it at the root; the exe standing in for a setup EXE, whose ProductVersion is the
                version (no ISCC needed); all three at once
      refuses   another version on every field of every artifact; a zip whose exe is not at the root; a missing file;
                a file with no version resource
      cannot    a version that is no YY.MMDD.HHmm instant; no artifact named (exit 2, COULD NOT VERIFY)

    Exit 0 when every check passes, 1 otherwise, 2 when the run could not be completed (no `dotnet`, a failed fixture
    build). The last line is `release-artifact-version-tests: PASS|FAIL|COULD NOT VERIFY (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$assert = Join-Path $PSScriptRoot 'Assert-ReleaseArtifactVersion.ps1'
$version = '26.0719.0131'

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name, [object] $Result = $null) {
    $actual = if ($Result) { "exit $($Result.Exit), '$($Result.Last)'" } elseif ($Condition) { 'PASS' } else { 'FAIL' }
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
    Write-Output "expected: $Name | actual: $actual"
}
function Invoke-Assert([string[]] $Arguments) {
    $out = & pwsh -NoProfile -File $script:assert @Arguments 2>&1 | ForEach-Object { "$_" }
    $lines = @($out | ForEach-Object { $_ -replace "`e\[[0-9;]*[A-Za-z]", '' } | Where-Object { $_.Trim() })
    [pscustomobject]@{ Exit = $LASTEXITCODE; Last = $(if ($lines.Count) { $lines[-1] } else { '' }); Text = ($lines -join "`n") }
}
function New-Zip([string] $Path, [hashtable] $Members) {
    $zip = [System.IO.Compression.ZipFile]::Open($Path, 'Create')
    try {
        foreach ($name in $Members.Keys) { $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $Members[$name], $name) }
    }
    finally { $zip.Dispose() }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0247-readback-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $appDir = Join-Path $work 'app'
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $appDir 'StreamsPlayer.csproj') -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Version>$version</Version>
    <FileVersion>$version</FileVersion>
    <InformationalVersion>$version</InformationalVersion>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $appDir 'Program.cs') -Value 'internal static class Program { private static void Main() { } }'
    dotnet build (Join-Path $appDir 'StreamsPlayer.csproj') -c Release --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The fixture application build failed.' }
    $exe = Join-Path $appDir 'bin/Release/net10.0/StreamsPlayer.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Fixture executable not found at $exe" }

    $zip = Join-Path $work 'StreamsPlayer-fixture.zip'
    $readme = Join-Path $work 'README.md'
    Set-Content -LiteralPath $readme -Value 'fixture'
    New-Zip $zip @{ 'StreamsPlayer.exe' = $exe; 'README.md' = $readme; 'libvlc/win-x64/readme.txt' = $readme }
    $nested = Join-Path $work 'nested.zip'
    New-Zip $nested @{ 'StreamsPlayer/StreamsPlayer.exe' = $exe }

    # --- accepted ------------------------------------------------------------------------------------------------
    $r = Invoke-Assert @('-Version', $version, '-ExePath', $exe)
    Check ($r.Exit -eq 0 -and $r.Last -ceq 'release-version: PASS (3 assertions)' -and $r.Text -match 'binary file version 26\.719\.131\.0 ') 'the exe passes and its binary version is the remap 26.719.131.0' $r
    $r = Invoke-Assert @('-Version', $version, '-ZipPath', $zip)
    Check ($r.Exit -eq 0 -and $r.Last -ceq 'release-version: PASS (4 assertions)') 'the exe inside the zip passes' $r
    $r = Invoke-Assert @('-Version', $version, '-SetupPath', $exe)
    Check ($r.Exit -eq 0 -and $r.Last -ceq 'release-version: PASS (1 assertions)') 'a setup whose ProductVersion is the version passes' $r
    $r = Invoke-Assert @('-Version', $version, '-ExePath', $exe, '-ZipPath', $zip, '-SetupPath', $exe)
    Check ($r.Exit -eq 0 -and $r.Last -ceq 'release-version: PASS (8 assertions)') 'all three artifacts at once pass' $r
    Check (-not @(Get-ChildItem -LiteralPath ([System.IO.Path]::GetTempPath()) -Directory -Filter 'sp0247-readback-*' | Where-Object { $_.FullName -ne $work })) 'the extraction folder is removed afterwards'

    # --- refused -------------------------------------------------------------------------------------------------
    $r = Invoke-Assert @('-Version', '26.0719.0132', '-ExePath', $exe, '-ZipPath', $zip, '-SetupPath', $exe)
    $wrong = @('exe-file-version', 'exe-product-version', 'exe-binary-version', 'zip-file-version', 'zip-product-version', 'zip-binary-version', 'setup-product-version')
    $named = @($wrong | Where-Object { $r.Text -match "release-version: FAIL $_ - " })
    Check ($r.Exit -eq 1 -and $r.Last -ceq 'release-version: FAIL (7 of 8 assertions)' -and $named.Count -eq $wrong.Count) 'another version fails every field of every artifact' $r
    $r = Invoke-Assert @('-Version', $version, '-ZipPath', $nested)
    Check ($r.Exit -eq 1 -and $r.Text -match 'release-version: FAIL zip-entry - ') 'a zip whose exe is not at the root fails' $r
    $r = Invoke-Assert @('-Version', $version, '-ExePath', (Join-Path $work 'absent.exe'))
    Check ($r.Exit -eq 1 -and $r.Text -match 'release-version: FAIL exe - no such file') 'a missing exe fails' $r
    $r = Invoke-Assert @('-Version', $version, '-SetupPath', (Join-Path $work 'absent-setup.exe'))
    Check ($r.Exit -eq 1 -and $r.Text -match 'release-version: FAIL setup - no such file') 'a missing setup EXE fails' $r
    $r = Invoke-Assert @('-Version', $version, '-ExePath', $readme)
    Check ($r.Exit -eq 1 -and $r.Last -ceq 'release-version: FAIL (3 of 3 assertions)') 'a file with no version resource fails' $r

    # --- could not verify ----------------------------------------------------------------------------------------
    $r = Invoke-Assert @('-Version', '26.1301.0000', '-ExePath', $exe)
    Check ($r.Exit -eq 2 -and $r.Last -match '^release-version: COULD NOT VERIFY \(') 'a version that is no instant could not be verified' $r
    $r = Invoke-Assert @('-Version', $version)
    Check ($r.Exit -eq 2 -and $r.Last -match '^release-version: COULD NOT VERIFY \(no artifact named') 'no artifact named could not be verified' $r
}
catch {
    # A setup step or the subject threw instead of answering: nothing was judged past this point, so the run could not
    # verify (CHECK-VERDICT 2.1, 2.5, 2.11). A check that already failed still outranks it (2.14).
    $reason = $_.Exception.Message -replace '\s+', ' '
    if ($failures.Count -eq 0) { Write-Output "release-artifact-version-tests: COULD NOT VERIFY ($reason)"; exit 2 }
    Write-Output "release-artifact-version-tests: the run stopped early: $reason"
}
finally { if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } }

if ($failures.Count -eq 0) { Write-Output "release-artifact-version-tests: PASS ($passed checks)"; exit 0 }
Write-Output "release-artifact-version-tests: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
