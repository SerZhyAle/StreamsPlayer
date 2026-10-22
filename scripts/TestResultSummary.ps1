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
    $verdict = switch ($exitCode) { 0 { 'PASS' } 1 { 'FAIL' } 2 { 'CANNOT VERIFY' } }
    return [pscustomobject]@{ ExitCode = $exitCode; Verdict = $verdict; Run = $run; Skipped = $skipped }
}
