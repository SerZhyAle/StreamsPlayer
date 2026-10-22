[CmdletBinding()]
param(
    [string] $CatalogRoot = $env:SZA_CONTRACTS_ROOT,
    # SP-0225: rewrite the vendored copy of the family's message table (item CZ) and ladder (item CY) from the catalog.
    [switch] $ExportFailureTable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $CatalogRoot) { throw 'Pass -CatalogRoot or set SZA_CONTRACTS_ROOT to the shared contract catalog.' }
$contract = Join-Path $CatalogRoot 'device-exchange/README.md'
$rows = Get-Content -LiteralPath $contract -Encoding utf8
$bindings = [ordered]@{
    'exchange server' = 'ExchangeTitle'
    'broadcasting' = 'BroadcastTitle'
    'cast to your own device' = 'BroadcastCastMenu'
}
$failures = [System.Collections.Generic.List[string]]::new()
$count = 0
function Read-Dictionary([string] $code) {
    [xml] $dictionary = Get-Content -LiteralPath (Join-Path $repository "src/StreamsPlayer.App/Localization.$code.xaml") -Raw -Encoding utf8
    $values = @{}
    foreach ($node in $dictionary.DocumentElement.ChildNodes) {
        if ($node.NodeType -eq [Xml.XmlNodeType]::Element) {
            $values[$node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')] = $node.InnerText
        }
    }
    return $values
}

foreach ($code in @('en', 'ru', 'uk')) {
    $values = Read-Dictionary $code
    $column = @{ en = 2; ru = 3; uk = 4 }[$code]
    foreach ($term in $bindings.Keys) {
        $row = @($rows | Where-Object { $_.StartsWith("| $term |", [StringComparison]::Ordinal) })
        if ($row.Count -ne 1) { throw "Expected one DEVICE-EXCHANGE CW row for '$term'." }
        $expected = $row[0].Split('|')[$column].Trim()
        if (-not $values.ContainsKey($bindings[$term]) -or -not $values[$bindings[$term]].Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("$code/$($bindings[$term]): expected '$expected'")
        }
        $count++
    }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Output "expected: EN/RU/UK fixed labels match DEVICE-EXCHANGE 2 CW | actual: PASS ($count labels)"

# ---- SP-0225: the message table (item CZ) and the ladder of checks (item CY) --------------------------------------
# The first cell of the table row each product code is worded by. A code with no row of its own in the table is
# read from the sentence that words it (the generic refusal of item CZ's notes, the amendment of 2026-10-17).
$tableRows = [ordered]@{
    'bad-credentials' = '`bad-credentials`'
    'bad-credentials-code' = '`bad-credentials` with a one-time code'
    'device-revoked' = '`device-revoked`'
    'signed-out' = '`signed-out`'
    'version-unsupported' = '`version-unsupported`'
    'tls-required' = '`tls-required`'
    'id-collision' = '`id-collision`'
    'capacity' = '`capacity`'
    'port-unavailable' = '`port-unavailable`'
    'unavailable' = '`unavailable`'
    'rate-limited' = '`rate-limited`'
    'password-policy' = '`password-policy`'
    'limit-reached' = '`limit-reached`'
    'not-permitted' = '`not-permitted`'
    'declined' = '`declined`'
    'timeout' = '`timeout`'
    'unsupported' = '`unsupported`'
    'unreachable-server' = '`unreachable` to a server'
    'unanswered' = '`unanswered`'
    'tls-failed' = '`tls-failed`'
    'certificate-changed' = '`certificate-changed`'
    'empty-credential' = '`empty-credential`'
}
$cz = $rows | Select-String -Pattern '^\*\*CZ \[NEW\]' | Select-Object -First 1
$dd = $rows | Select-String -Pattern '^\*\*DD \[NEW\]' | Select-Object -First 1
if (-not $cz -or -not $dd) { throw 'Item CZ or item DD was not found in DEVICE-EXCHANGE.' }
$czLines = $rows[($cz.LineNumber - 1)..($dd.LineNumber - 2)]
$czTable = @{}
foreach ($line in $czLines) {
    if (-not $line.StartsWith('| `', [StringComparison]::Ordinal)) { continue }
    $cells = $line.Split('|')
    if ($cells.Count -lt 8) { continue }
    $czTable[$cells[1].Trim()] = [pscustomobject]@{
        kind = $cells[2].Trim(); step = $cells[3].Trim().Trim('`')
        en = $cells[4].Trim(); ru = $cells[5].Trim(); uk = $cells[6].Trim()
    }
}

$exported = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $tableRows.GetEnumerator()) {
    if (-not $czTable.ContainsKey($entry.Value)) { throw "Item CZ has no row '$($entry.Value)' for '$($entry.Key)'." }
    $row = $czTable[$entry.Value]
    $kind = if ($row.kind -like '*cast*' -and $entry.Key -in @('declined', 'timeout', 'unsupported')) { 'cast' } elseif ($row.kind -like 'wire*') { 'wire' } else { $row.kind }
    $exported.Add([ordered]@{ code = $entry.Key; kind = $kind; step = $(if ($row.step -eq '-') { $null } else { $row.step }); en = $row.en; ru = $row.ru; uk = $row.uk })
}

# The generic refusal (item CZ, notes): English, Russian and Ukrainian in one sentence of the table's notes.
$genericAt = 0..($rows.Count - 1) | Where-Object { $rows[$_].Contains('An unknown `refused.reason`') } | Select-Object -First 1
if ($null -eq $genericAt) { throw 'The generic refusal note of item CZ was not found.' }
$genericText = $rows[$genericAt] + ' ' + $rows[$genericAt + 1]
$refusedEn = 'The server refused this.'; $refusedRu = 'Сервер отказал в этом.'; $refusedUk = 'Сервер відмовив у цьому.'
foreach ($sentence in @($refusedEn, $refusedRu, $refusedUk)) {
    if (-not $genericText.Contains($sentence)) { throw "The generic refusal '$sentence' is no longer in item CZ." }
}
$exported.Add([ordered]@{ code = 'refused'; kind = 'wire'; step = 'operator'; en = $refusedEn; ru = $refusedRu; uk = $refusedUk })

# The local row of the amendment of 2026-10-17: three quoted lines that start with the language code.
$amendment = $rows | Select-String -Pattern 'Code `format-unsupported`' | Select-Object -First 1
if (-not $amendment) { throw 'The format-unsupported amendment was not found.' }
$start = $amendment.LineNumber - 4
$block = $rows[($start - 1)..($amendment.LineNumber - 1)]
$format = @{}
foreach ($line in $block) {
    if ($line -match '^(EN|RU|UK) "(.+)"\s*$') { $format[$Matches[1].ToLowerInvariant()] = $Matches[2] }
}
if ($format.Count -ne 3) { throw 'The three texts of format-unsupported were not found.' }
$exported.Add([ordered]@{ code = 'format-unsupported'; kind = 'local'; step = $null; en = $format['en']; ru = $format['ru']; uk = $format['uk'] })

$ci = $rows | Select-String -Pattern '^\*\*CY \[NEW\]' | Select-Object -First 1
$da = $rows | Select-String -Pattern '^\*\*DA \[NEW\]' | Select-Object -First 1
$steps = [System.Collections.Generic.List[object]]::new()
foreach ($line in $rows[($ci.LineNumber - 1)..($da.LineNumber - 2)]) {
    if ($line -notmatch '^\| (\d+) \| `([a-z-]+)` \| (any|network|internet) \|') { continue }
    $cells = $line.Split('|')
    $steps.Add([ordered]@{ number = [int] $cells[1].Trim(); id = $cells[2].Trim().Trim('`'); applies = $cells[3].Trim()
        en = $cells[4].Trim(); ru = $cells[5].Trim(); uk = $cells[6].Trim() })
}
if ($steps.Count -ne 15) { throw "Item CY should hold 15 steps; found $($steps.Count)." }

$vector = [ordered]@{
    source = 'DEVICE-EXCHANGE 1.5 section 9 items CZ (messages) and CY (ladder) and the amendment of 2026-10-17 (format-unsupported); SP-0225'
    rows = $exported
    steps = $steps
}
$vectorPath = Join-Path $repository 'tests/StreamsPlayer.Core.Tests/Vectors/exchange-failure-table.json'
$json = ($vector | ConvertTo-Json -Depth 6) + "`n"
if ($ExportFailureTable) {
    [System.IO.File]::WriteAllText($vectorPath, $json, [System.Text.UTF8Encoding]::new($false))
    Write-Output "exported: $($exported.Count) rows and $($steps.Count) steps to tests/StreamsPlayer.Core.Tests/Vectors/exchange-failure-table.json"
} else {
    $vendored = [System.IO.File]::ReadAllText($vectorPath, [System.Text.UTF8Encoding]::new($false))
    if ($vendored.ReplaceLineEndings("`n") -ne $json) { throw 'The vendored message table differs from the catalog. Run with -ExportFailureTable and review the diff.' }
}

# The dictionaries hold the table verbatim in EN, RU and UK. (The CI test holds the same through the vendored vector.)
$keyOf = @{ 'bad-credentials' = 'ExchangeBadCredentials'; 'bad-credentials-code' = 'ExchangeBadCredentialsCode'; 'device-revoked' = 'ExchangeDeviceRevoked'
    'signed-out' = 'ExchangeSignedOut'; 'version-unsupported' = 'ExchangeVersionUnsupported'; 'tls-required' = 'ExchangeTlsRequired'
    'id-collision' = 'ExchangeIdCollision'; 'capacity' = 'ExchangeCapacity'; 'port-unavailable' = 'ExchangePortUnavailable'
    'unavailable' = 'ExchangeUnavailable'; 'rate-limited' = 'ExchangeRateLimited'; 'password-policy' = 'ExchangePasswordPolicy'
    'limit-reached' = 'ExchangeLimitReached'; 'not-permitted' = 'ExchangeNotPermitted'; 'refused' = 'ExchangeRefused'
    'declined' = 'BroadcastCastDeclined'; 'timeout' = 'BroadcastCastTimeout'; 'unsupported' = 'BroadcastCastUnsupported'
    'unreachable-server' = 'ExchangeUnreachable'; 'unanswered' = 'ExchangeUnanswered'; 'tls-failed' = 'ExchangeTlsFailed'
    'certificate-changed' = 'ExchangeCertificateChanged'; 'empty-credential' = 'ExchangeEmptyCredential'; 'format-unsupported' = 'ExchangeFormatUnsupported' }
$tableCount = 0
foreach ($code in @('en', 'ru', 'uk')) {
    $values = Read-Dictionary $code
    foreach ($row in $exported) {
        $key = $keyOf[$row.code]
        if (-not $values.ContainsKey($key) -or $values[$key] -cne $row[$code]) { $failures.Add("$code/${key}: expected '$($row[$code])'") }
        $tableCount++
    }
    foreach ($step in $steps) {
        $key = 'ExchangeStep' + ((($step.id -split '-') | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }) -join '')
        if (-not $values.ContainsKey($key) -or $values[$key] -cne $step[$code]) { $failures.Add("$code/${key}: expected '$($step[$code])'") }
        $tableCount++
    }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Output "expected: EN/RU/UK messages and steps match DEVICE-EXCHANGE 9 CZ and CY | actual: PASS ($tableCount strings)"
