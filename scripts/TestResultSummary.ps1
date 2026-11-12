function Get-TestResultSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [int] $ExpectedFiles,
        [Parameter(Mandatory)] [int] $RunnerExitCode
    )

    [long] $run = 0
    [long] $skipped = 0
    $failed = $RunnerExitCode -ne 0
    $unverified = $false
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -File -ErrorAction SilentlyContinue)
    if ($files.Count -ne $ExpectedFiles -or $ExpectedFiles -lt 1) { $unverified = $true }
    foreach ($file in $files) {
        try {
            $document = [System.Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($file.FullName)
            $counters = $document.SelectSingleNode('/*[local-name()="TestRun"]/*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
            if ($null -eq $counters) { throw 'No counters' }
            $values = @{}
            foreach ($name in 'total', 'executed', 'passed', 'failed') {
                $value = 0L
                if (-not [long]::TryParse($counters.GetAttribute($name), [ref] $value) -or $value -lt 0) {
                    throw "Invalid counter $name"
                }
                $values[$name] = $value
            }
            if ($values.executed -gt $values.total -or $values.passed + $values.failed -gt $values.executed) {
                throw 'Inconsistent counters'
            }
            $run += $values.executed
            $skipped += $values.total - $values.executed
            if ($values.failed -gt 0) { $failed = $true }
            if ($values.executed -eq 0 -or $values.passed -ne $values.executed) { $unverified = $true }
            $summary = $counters.ParentNode.GetAttribute('outcome')
            if ($summary -eq 'Failed') { $failed = $true }
            elseif ($summary -notin 'Completed', 'Passed') { $unverified = $true }
        }
        catch [System.Xml.XmlException], [System.IO.IOException], [System.UnauthorizedAccessException], [System.Management.Automation.RuntimeException] {
            # Unreadable counters are missing evidence, never an empty passing suite.
            $unverified = $true
        }
    }
    $exitCode = if ($failed) { 1 } elseif ($unverified -or $skipped -gt 0) { 2 } else { 0 }
    $verdict = switch ($exitCode) { 0 { 'PASS' } 1 { 'FAIL' } 2 { 'COULD NOT VERIFY' } }
    return [pscustomobject]@{ ExitCode = $exitCode; Verdict = $verdict; Run = $run; Skipped = $skipped }
}

# SP-0246 S3-1: every default scripts/test.ps1 run leaves a tests-<guid> folder of TRX files under temp/scratch. A folder is
# removed only when it is older than -MaxAgeDays AND not among the -Keep newest, so a recent run stays readable. Only a
# real directory named exactly tests-<32 hex> is touched (never a link, never another name), and never -Current.
# Returns the names it removed.
function Remove-StaleTestResults {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Scratch,
        [string] $Current = '',
        [int] $Keep = 4,
        [int] $MaxAgeDays = 7
    )

    if (-not (Test-Path -LiteralPath $Scratch -PathType Container)) { return @() }
    $scratchFull = [System.IO.Path]::GetFullPath($Scratch).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $currentFull = if ($Current) { [System.IO.Path]::GetFullPath($Current).TrimEnd('\', '/') } else { '' }
    $cutoff = [datetime]::UtcNow.AddDays(-$MaxAgeDays)
    $removed = [System.Collections.Generic.List[string]]::new()
    $candidates = @(Get-ChildItem -LiteralPath $Scratch -Directory -Filter 'tests-*' |
        Where-Object { $_.Name -match '^tests-[0-9a-f]{32}$' -and -not $_.LinkType } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip $Keep |
        Where-Object { $_.LastWriteTimeUtc -lt $cutoff })
    foreach ($candidate in $candidates) {
        $target = [System.IO.Path]::GetFullPath($candidate.FullName).TrimEnd('\', '/')
        if (-not $target.StartsWith($scratchFull, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($currentFull -and $target -ieq $currentFull) { continue }
        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
        $removed.Add($candidate.Name)
    }

    return $removed.ToArray()
}
