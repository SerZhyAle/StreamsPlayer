#requires -Version 7.0
<#
.SYNOPSIS
    SP-0212 (L7): fixture tests for the catalog-snapshot builder (tools/build-catalog-snapshot.ps1).

.DESCRIPTION
    Runs the builder against two local bank ZIPs with -BankPath/-OutputPath, so nothing is downloaded and
    the tracked snapshot is never touched:

      good bank   a snapshot is written, streams.csv is its first entry, and no staging archive (.tmp)
                  is left behind
      oversized   a bank whose snapshot would exceed BundledCatalogSnapshot.MaximumSnapshotBytes fails
                  validation, and the file the output path already held is left byte-identical - the
                  invalid archive is never moved over the target (the SP-0212 L7 defect: the move used to
                  run before the validation, so the invalid file was the one left in place)

    The contract (catalog URL, ceilings) is read from a built StreamsPlayer.Core assembly, exactly as the
    tool itself reads it - run this after a build (scripts/check.ps1 does).

    Exit 0 when every check passes, 1 otherwise. The last line is `catalog-snapshot-tool: PASS|FAIL (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$builder = Join-Path $repo 'tools/build-catalog-snapshot.ps1'

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}
function Invoke-Script([string[]] $Arguments) {
    $out = & pwsh -NoProfile -File $script:builder @Arguments 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = ($out -join "`n") }
}

# A bank ZIP's one mandatory entry is streams.csv; the fixture writes it to a folder and zips it, so the
# entry sits at the archive root under its own name. $Rows channel rows of a 26-letter alphabet: high
# enough entropy that the oversized case stays over the 8 MB snapshot ceiling after compression.
function New-FixtureBankFolder([string] $Folder, [int] $Rows) {
    New-Item -ItemType Directory -Path $Folder -Force | Out-Null
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('name,url,is_live')
    $random = [System.Random]::new(20261011)
    $buffer = [char[]]::new(64)
    for ($i = 0; $i -lt $Rows; $i++) {
        for ($c = 0; $c -lt $buffer.Length; $c++) { $buffer[$c] = [char](97 + $random.Next(26)) }
        $lines.Add(("station $($i + 1),https://example.invalid/$(-join $buffer)/stream,true"))
    }
    [System.IO.File]::WriteAllLines((Join-Path $Folder 'streams.csv'), $lines.ToArray())
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('sp0212-snapshot-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    # --- a good bank produces a valid snapshot ------------------------------------------------------------------
    $goodBankFolder = Join-Path $work 'good-bank'
    New-FixtureBankFolder $goodBankFolder 50
    $goodBank = Join-Path $work 'good-bank.zip'
    Compress-Archive -Path (Join-Path $goodBankFolder 'streams.csv') -DestinationPath $goodBank
    $out = Join-Path $work 'snapshot.zip'
    $r = Invoke-Script @('-BankPath', $goodBank, '-OutputPath', $out)
    Check ($r.Exit -eq 0) "a good bank builds a snapshot (got $($r.Exit): $($r.Output -join ' | '))"
    if (Test-Path -LiteralPath $out) {
        $stream = [System.IO.File]::OpenRead($out)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)
            try {
                Check ($archive.Entries.Count -gt 0 -and $archive.Entries[0].FullName.EndsWith('streams.csv')) 'streams.csv is the first entry'
                $reader = [System.IO.StreamReader]::new($archive.Entries[0].Open())
                try { $header = $reader.ReadLine() } finally { $reader.Dispose() }
                Check ($header -eq 'name,url,is_live') 'the snapshot carries the bank CSV'
            }
            finally { $archive.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    else { Check $false 'streams.csv is the first entry' }
    Check (-not (Test-Path -LiteralPath "$out.tmp")) 'no staging archive is left behind on success'

    # --- an oversized bank never replaces the target with an invalid snapshot (SP-0212 L7) ----------------------
    # 200,000 rows of a 26-letter alphabet make ~19 MB of CSV that compresses to well over the
    # 8 MB BundledCatalogSnapshot.MaximumSnapshotBytes ceiling, so the staged snapshot fails Test-Snapshot
    # after it is written - the moment the SP-0212 (L7) ordering matters.
    $bigBankFolder = Join-Path $work 'big-bank'
    New-FixtureBankFolder $bigBankFolder 200000
    $bigBank = Join-Path $work 'big-bank.zip'
    Compress-Archive -Path (Join-Path $bigBankFolder 'streams.csv') -DestinationPath $bigBank
    if ((Get-Item -LiteralPath $bigBank).Length -gt 128L * 1024 * 1024) { throw 'The oversized fixture bank itself exceeds the 128 MB bank ceiling.' }

    # The output path holds a file already; the refused run must leave it byte-identical.
    $target = Join-Path $work 'guarded-snapshot.zip'
    [System.IO.File]::WriteAllBytes($target, [byte[]]@(1, 2, 3, 4, 5, 6, 7, 8))
    $before = [System.IO.File]::ReadAllBytes($target)
    $r = Invoke-Script @('-BankPath', $bigBank, '-OutputPath', $target)
    Check ($r.Exit -ne 0 -and $r.Output -match 'over the declared ceiling') 'a snapshot over its ceiling fails the build'
    $after = [System.IO.File]::ReadAllBytes($target)
    Check ([System.Linq.Enumerable]::SequenceEqual($before, $after)) 'the file the output path already held is left byte-identical'
    Check (-not (Test-Path -LiteralPath "$target.tmp")) 'the invalid staged archive is removed, not left behind'
}
finally {
    if (Test-Path -LiteralPath $work) {
        Get-ChildItem -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Attributes = 'Normal' } catch { } }
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -eq 0) { "catalog-snapshot-tool: PASS ($passed checks)"; exit 0 }
"catalog-snapshot-tool: FAIL ($($failures.Count) of $($passed + $failures.Count) checks)"
exit 1
