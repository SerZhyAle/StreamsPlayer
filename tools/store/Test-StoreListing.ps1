#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = Join-Path $root ('temp/SP-0232/store-listing-' + [guid]::NewGuid().ToString('N'))
$deck = Join-Path $fixture 'deck'
New-Item -ItemType Directory -Force -Path $deck | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
$builder = Join-Path $PSScriptRoot 'build-store-listing-csv.ps1'
$failures = [System.Collections.Generic.List[string]]::new()
$run = 0

function Check([bool] $Condition, [string] $Name) {
    $script:run++
    if (-not $Condition) { $script:failures.Add($Name); Write-Host "FAIL: $Name" }
}
function Write-Terms([int] $WordsInLastTerm = 3) {
    $terms = @(1..6 | ForEach-Object { "term$_ two three" })
    $terms += if ($WordsInLastTerm -eq 3) { 'last two three' } else { 'last two three four' }
    [System.IO.File]::WriteAllText((Join-Path $deck 'search-terms.txt'), ($terms -join "`n"), $utf8)
}
function Write-Export([string] $Name, [switch] $Bom, [switch] $RetainedOverflow) {
    $lines = @('"Field","ID","Type","default","en-us"',
        '"Description","","Text","",""', '"ShortDescription","","Text","",""')
    $lines += @(1..7 | ForEach-Object {
        $value = if ($RetainedOverflow) { "old$_ two three four" } else { '' }
        '"SearchTerm{0}","","Text","","{1}"' -f $_, $value
    })
    # An unfamiliar row and column value must survive a no-change run too.
    $lines += '"FutureField","future-id","Text","untouched","unknown value"'
    $path = Join-Path $fixture $Name
    [System.IO.File]::WriteAllText($path, ($lines -join "`r`n"), [System.Text.UTF8Encoding]::new([bool] $Bom))
    return $path
}
function Invoke-Builder([string[]] $Arguments) {
    $output = & pwsh -NoProfile -File $builder @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

[System.IO.File]::WriteAllText((Join-Path $deck 'en-us.txt'), "@@Description`nDescription.`n@@ShortDescription`nShort.`n", $utf8)
[System.IO.File]::WriteAllText((Join-Path $deck 'forbidden-terms.txt'), '# no forbidden terms in fixture', $utf8)
Write-Terms
$export = Write-Export 'plain.csv'
$out = Join-Path $fixture 'result.csv'

foreach ($bom in $false, $true) {
    $source = Write-Export "roundtrip-$bom.csv" -Bom:$bom
    $result = Invoke-Builder @('-Export', $source, '-Out', $out, '-FillNothing')
    Check ($result.ExitCode -eq 0) "round trip succeeds (BOM=$bom)"
    Check ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $out).Hash) "round trip preserves bytes (BOM=$bom)"
}

$arguments = @('-Export', $export, '-Out', $out, '-DeckDirectory', $deck, '-AllowMissingLanguages', '-TermsOnly')
$result = Invoke-Builder $arguments
Check ($result.ExitCode -eq 0) 'exactly 21 words accepted'
$bytes = [System.IO.File]::ReadAllBytes($out)
Check (-not ($bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) 'new import has no BOM'
Check (-not ([System.IO.File]::ReadAllText($out) -match '(?<!\r)\n')) 'new import has no bare LF'

Write-Terms -WordsInLastTerm 4
[System.IO.File]::WriteAllText($out, 'keep existing output', $utf8)
$result = Invoke-Builder $arguments
Check ($result.ExitCode -ne 0 -and $result.Output -match 'en-us: search terms contain 22 words') 'overflow names language and count'
Check ([System.IO.File]::ReadAllText($out) -eq 'keep existing output') 'overflow preserves output'

$import = Join-Path $fixture 'import'
New-Item -ItemType Directory -Path $import | Out-Null
$sentinel = Join-Path $import 'keep.txt'
[System.IO.File]::WriteAllText($sentinel, 'keep staging', $utf8)
$result = Invoke-Builder ($arguments + @('-ImportFolder', $import))
Check ($result.ExitCode -ne 0 -and [System.IO.File]::ReadAllText($sentinel) -eq 'keep staging') 'overflow leaves staging untouched'

Write-Terms
$retained = Write-Export 'retained.csv' -RetainedOverflow
$result = Invoke-Builder @('-Export', $retained, '-Out', $out, '-DeckDirectory', $deck, '-AllowMissingLanguages', '-TermsOnly')
Check ($result.ExitCode -ne 0 -and $result.Output -match 'en-us: search terms contain 28 words') 'retained nonempty cells are counted'
Check ([System.IO.File]::ReadAllText($out) -eq 'keep existing output') 'merged overflow writes nothing'
$result = Invoke-Builder @('-Export', $retained, '-Out', $out, '-DeckDirectory', $deck, '-AllowMissingLanguages', '-TermsOnly', '-ReplaceCopy')
Check ($result.ExitCode -eq 0) 'explicit replacement of retained overflow succeeds with 21 words'

$foreign = Join-Path $fixture 'foreign-language.csv'
$foreignRows = Import-Csv -LiteralPath $export
foreach ($row in $foreignRows) {
    $value = if ($row.Field -match '^SearchTerm[1-7]$') { 'foreign two three four' } else { 'untouched' }
    $row | Add-Member -NotePropertyName 'ja-jp' -NotePropertyValue $value
}
$foreignRows | Export-Csv -LiteralPath $foreign -NoTypeInformation -Encoding utf8
[System.IO.File]::WriteAllText($out, 'keep foreign output', $utf8)
$result = Invoke-Builder @('-Export', $foreign, '-Out', $out, '-DeckDirectory', $deck, '-AllowMissingLanguages', '-TermsOnly')
Check ($result.ExitCode -ne 0 -and $result.Output -match 'ja-jp: search terms contain 28 words') 'retained unshipped language cells are validated too'
Check ([System.IO.File]::ReadAllText($out) -eq 'keep foreign output') 'foreign-column overflow writes nothing'

if ($failures.Count) { Write-Host "store-listing: FAIL run=$run skipped=0"; exit 1 }
Write-Host "store-listing: PASS run=$run skipped=0"
exit 0
