#requires -Version 7.0
<#
.SYNOPSIS
    SP-0276: fixture tests for the command-reference generator and its member gate (Build-CliDoc.ps1, CliDoc.ps1).

.DESCRIPTION
    Reads the real manifest, then proves the gate on copies of it and on throwaway trees:

      passes    the shipped manifest has no problem; the render is the same twice and names every command, every
                option and every example; a tree whose docs/CLI.md was just rendered answers -Check with 0
      fails     a stale docs/CLI.md answers 1; a missing docs/CLI.md answers 1; an entry that lost any one member
                of rule 18 (each of them, one at a time) is named; a risk, a cold start, a wait, an option kind or a
                shell outside its closed set, an enum with no values, a timeout out of range, a doubled name, an
                empty exit table or example list, a missing top-level member; a manifest that is not JSON or is
                absent answers 2

    The two real processes (SP-0283) are started through System.Diagnostics.Process with every stream a pipe, and a
    failed process check prints the exit code and the head of what the child wrote after its name.

    Exit 0 when every check passes, 1 otherwise, 2 when nothing could be judged. The last line is
    `cli-doc-test: PASS|FAIL|COULD NOT VERIFY (..)`.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/CliDoc.ps1"

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check([bool] $Condition, [string] $Name) {
    if ($Condition) { $script:passed++ } else { $script:failures.Add($Name); Write-Output "FAIL: $Name" }
}

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestText = [System.IO.File]::ReadAllText((Join-Path $repository 'docs/cli-commands.json'))
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('cli-doc-' + [guid]::NewGuid().ToString('N'))

function New-Manifest($Keep = $null) {
    # SP-0290: the member gate costs 25 ms on the whole manifest and a case changes one or two entries. A case that names
    # the entries it needs - an index into the shipped list or a command name, in the order it will read them back - gates
    # only those; the shipped manifest is still gated whole, once, below. No argument: the whole manifest.
    $manifest = $manifestText | ConvertFrom-Json
    if ($null -ne $Keep) {
        $all = @($manifest.commands)
        $manifest.commands = @(foreach ($key in $Keep) {
                if ($key -is [int]) { $all[$key] } else { $all | Where-Object { $_.name -ceq $key } | Select-Object -First 1 }
            })
    }
    return $manifest
}
function Get-Problems($Manifest) { return , @(Get-CliManifestProblem -Manifest $Manifest) }
function Has($Problems, [string] $Pattern) { return @($Problems | Where-Object { $_ -match $Pattern }).Count -gt 0 }
function Remove-Member($Object, [string] $Name) { $Object.PSObject.Properties.Remove($Name) }
function Invoke-Builder([string] $Root, [switch] $Check) {
    # The library function the script is a call of; no process per case. One real process follows below.
    $result = @(Invoke-CliDocBuild -Root $Root -Check:$Check)
    return [int] $result[-1]
}
function New-Tree([string] $Name) {
    $root = Join-Path $fixture $Name
    New-Item -ItemType Directory -Path (Join-Path $root 'docs') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $root 'docs/cli-commands.json'), $manifestText)
    return $root
}
function Invoke-Script([string[]] $Arguments) {
    # SP-0283: one real process, owned by this function. Stdin, stdout and stderr are pipes (no console shared with the
    # siblings of a parallel run, no output file reopened between two calls, no job-object wait) and the exit code is
    # read from the handle this call holds. Answers the exit code and everything the child wrote, so a failure names
    # its cause instead of only its exit code.
    $info = [System.Diagnostics.ProcessStartInfo]::new('pwsh')
    foreach ($argument in (@('-NoProfile', '-File') + $Arguments)) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $process.StandardInput.Close()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) {
            $process.Kill($true)
            return [pscustomobject]@{ ExitCode = -1; Text = 'the process was stopped after 20 s' }
        }
        $process.WaitForExit()
        $text = ($output.Result + $errors.Result) -replace '\s+', ' '
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Text = $text.Trim() }
    }
    finally { $process.Dispose() }
}
function Format-Ran($Ran) {
    # What a failed process check prints after its name: the exit code and the head of what the child wrote.
    $text = [string] $Ran.Text
    if ($text.Length -gt 300) { $text = $text.Substring(0, 300) + '..' }
    return "exit $($Ran.ExitCode): $text"
}

try {
    # ----- the shipped manifest and its render ---------------------------------------------------------------
    $manifest = New-Manifest
    Check ((Get-Problems $manifest).Count -eq 0) 'the shipped manifest has no problem'
    $first = ConvertTo-CliDocMarkdown -Manifest $manifest
    $second = ConvertTo-CliDocMarkdown -Manifest (New-Manifest)
    Check ($first -ceq $second) 'the render is the same twice'
    Check (-not $first.Contains("`r")) 'the render has LF line ends only'
    Check ($first.EndsWith("`n") -and -not $first.EndsWith("`n`n")) 'the render ends with exactly one line end'
    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($command in $manifest.commands) {
        if (-not $first.Contains("### $($command.name)`n")) { $missing.Add("section $($command.name)") }
        if (-not $first.Contains([string] $command.synopsis)) { $missing.Add("synopsis $($command.name)") }
        foreach ($arg in $command.args) { if (-not $first.Contains('`--' + $arg.name + '`')) { $missing.Add("option $($command.name) $($arg.name)") } }
        foreach ($exit in $command.exitCodes) { if (-not $first.Contains([string] $exit.meaning)) { $missing.Add("exit $($command.name) $($exit.code)") } }
        foreach ($example in $command.examples) { if (-not $first.Contains([string] $example.line)) { $missing.Add("example $($command.name)") } }
    }
    Check ($missing.Count -eq 0) "the render names every command, option, exit code and example ($($missing -join '; '))"
    Check ($first.Contains("lists all $(@($manifest.commands).Count) of them")) 'the command count in the text is the manifest count'

    # ----- the member gate: one member removed at a time -------------------------------------------------------
    foreach ($member in 'summary', 'synopsis', 'aliases', 'args', 'returns', 'risk', 'coldStart', 'wait', 'timeoutMs', 'interactive', 'idempotent', 'exitCodes', 'examples', 'since') {
        $changed = New-Manifest 6
        Remove-Member $changed.commands[0] $member
        $found = Get-Problems $changed
        Check (Has $found "command 'playback stop': $member is missing") "an entry without $member is named"
    }
    foreach ($member in 'schemaVersion', 'product', 'exe', 'cliVersion') {
        $changed = New-Manifest 0
        Remove-Member $changed $member
        Check (Has (Get-Problems $changed) "manifest: $member is missing") "a manifest without $member is named"
    }
    $changed = New-Manifest; Remove-Member $changed 'commands'
    Check (Has (Get-Problems $changed) 'commands is missing or empty') 'a manifest without commands is named'

    # ----- the closed sets and the shapes -----------------------------------------------------------------------
    $changed = New-Manifest 0; $changed.commands[0].risk = 'harmless'
    Check (Has (Get-Problems $changed) "risk 'harmless' is not one of") 'a risk outside the four classes fails'
    $changed = New-Manifest 0; $changed.commands[0].coldStart = 'sometimes'
    Check (Has (Get-Problems $changed) "coldStart 'sometimes'") 'a cold start outside the three modes fails'
    $changed = New-Manifest 0; $changed.commands[0].wait = 'later'
    Check (Has (Get-Problems $changed) "wait 'later'") 'a wait outside done and accepted fails'
    $changed = New-Manifest 0; $changed.commands[0].timeoutMs = 600001
    Check (Has (Get-Problems $changed) 'timeoutMs 600001 is outside') 'a timeout above 600000 fails'
    $changed = New-Manifest 0; $changed.commands[0].timeoutMs = 'soon'
    Check (Has (Get-Problems $changed) 'timeoutMs is missing or not an integer') 'a text timeout fails'
    $changed = New-Manifest 0; $changed.commands[0].interactive = 'no'
    Check (Has (Get-Problems $changed) 'interactive is missing or not true or false') 'a text flag fails'
    $changed = New-Manifest 0; $changed.commands[0].since = 'one'
    Check (Has (Get-Problems $changed) 'since is missing or is not MAJOR.MINOR') 'a since that is not a version fails'
    $changed = New-Manifest 0; $changed.commands[0].name = 'Commands'
    Check (Has (Get-Problems $changed) 'not one or two lower-case words') 'a name with a capital fails'
    $changed = New-Manifest 0, 1; $changed.commands[1].name = 'commands'
    Check (Has (Get-Problems $changed) "'commands': the name is listed twice") 'a doubled name fails'
    $changed = New-Manifest 0; $changed.commands[0].exitCodes = @()
    Check (Has (Get-Problems $changed) 'exitCodes is empty') 'an empty exit table fails'
    $changed = New-Manifest 0; $changed.commands[0].examples = @()
    Check (Has (Get-Problems $changed) 'examples is empty') 'an empty example list fails'
    $changed = New-Manifest 0; $changed.commands[0].exitCodes[0].code = 300
    Check (Has (Get-Problems $changed) 'an exit code is missing or is not 0 to 255') 'an exit code above 255 fails'
    $changed = New-Manifest 0; Remove-Member $changed.commands[0].exitCodes[0] 'meaning'
    Check (Has (Get-Problems $changed) 'meaning is missing or empty') 'an exit code without a meaning fails'
    $changed = New-Manifest 0; $changed.commands[0].examples[0].shell = 'bash'
    Check (Has (Get-Problems $changed) "shell 'bash' is not cmd or powershell") 'a shell outside cmd and powershell fails'
    $changed = New-Manifest 0; Remove-Member $changed.commands[0].examples[0] 'expect'
    Check (Has (Get-Problems $changed) 'expect is missing or empty') 'an example without its expectation fails'
    $changed = New-Manifest 0; $changed.commands[0].args[0].kind = 'blob'
    Check (Has (Get-Problems $changed) "option 'json': kind 'blob'") 'an option kind outside the closed set fails'
    $changed = New-Manifest 0; Remove-Member $changed.commands[0].args[0] 'required'
    Check (Has (Get-Problems $changed) "option 'json': required is missing") 'an option without required fails'
    $changed = New-Manifest 0; Remove-Member $changed.commands[0].args[0] 'description'
    Check (Has (Get-Problems $changed) "option 'json': description is missing") 'an option without a description fails'
    $volume = @($manifestText | ConvertFrom-Json | ForEach-Object { $_.commands } | Where-Object { $_.name -eq 'volume set' })
    $changed = New-Manifest 'volume set'
    $target = @($changed.commands | Where-Object { $_.name -eq 'volume set' })[0].args | Where-Object { $_.kind -eq 'enum' } | Select-Object -First 1
    $target.values = @()
    Check ($volume.Count -eq 1 -and (Has (Get-Problems $changed) 'an enum option has no values')) 'an enum option without values fails'
    $changed = New-Manifest @($manifest.commands | Where-Object { @($_.returns).Count -gt 0 })[0].name
    $withReturns = @($changed.commands | Where-Object { @($_.returns).Count -gt 0 })[0]
    Remove-Member $withReturns.returns[0] 'description'
    Check (Has (Get-Problems $changed) 'returned value .* description is missing') 'a returned value without a description fails'

    # ----- SP-0279: forward, alias objects, sensitive options, oneOf --------------------------------------------------
    $changed = New-Manifest 0, 1
    Check ($changed.commands[0].forward -ceq 'never' -and $changed.commands[1].forward -ceq 'never') 'commands and version are forward never'
    $changed.commands[0].coldStart = 'refuse'
    Check (Has (Get-Problems $changed) 'forward never command must have coldStart oneshot') 'a forward never command that is not oneshot fails'
    $changed = New-Manifest 0; $changed.commands[0].forward = 'sometimes'
    Check (Has (Get-Problems $changed) "forward 'sometimes'") 'a forward outside always and never fails'
    $changed = New-Manifest 'show'
    $show = @($changed.commands | Where-Object { $_.name -eq 'show' })[0]
    Check ($show.aliases[0].spelling -ceq '' -and $show.aliases[0].legacy -eq $true -and $show.aliases[0].answers -eq $false) 'the bare start is a legacy alias that returns no answer'
    Remove-Member $show.aliases[0] 'spelling'
    Check (Has (Get-Problems $changed) 'an alias is neither text nor an object with a spelling') 'an alias object without a spelling fails'
    $changed = New-Manifest 'play'
    $play = @($changed.commands | Where-Object { $_.name -eq 'play' })[0]
    $play.aliases[0].maps.url = 'nowhere'
    Check (Has (Get-Problems $changed) "maps to 'nowhere', which is not an option") 'an alias placeholder that maps to no option fails'
    $changed = New-Manifest 'play'
    $play = @($changed.commands | Where-Object { $_.name -eq 'play' })[0]
    $play.args = @($play.args | Where-Object { $_.name -ne 'url-file' })
    Check (Has (Get-Problems $changed) 'a sensitive option needs the option url-file') 'a sensitive option without its file option fails'
    $changed = New-Manifest 'channel add'
    $add = @($changed.commands | Where-Object { $_.name -eq 'channel add' })[0]
    $add.oneOf = @(, @('url', 'nothing'))
    Check (Has (Get-Problems $changed) "oneOf names 'nothing'") 'a oneOf group naming no option fails'
    Check ($first.Contains('Exactly one of `--url` or `--url-file` is given.')) 'channel add says exactly one of its two address options'
    Check ($first.Contains('Sensitive: never written to a log line')) 'a sensitive option is described as such'
    Check ($first.Contains('**Reserved words.**')) 'the guide says that a reserved word beats a path'

    # ----- the builder as a process: exit codes -------------------------------------------------------------------
    $current = New-Tree 'current'
    Check ((Invoke-Builder $current) -eq 0) 'the default mode writes docs/CLI.md and exits 0'
    Check (Test-Path -LiteralPath (Join-Path $current 'docs/CLI.md')) 'the default mode created docs/CLI.md'
    Check ((Invoke-Builder $current -Check) -eq 0) '-Check on a freshly rendered tree exits 0'
    $crlf = [System.IO.File]::ReadAllText((Join-Path $current 'docs/CLI.md')).Replace("`n", "`r`n")
    [System.IO.File]::WriteAllText((Join-Path $current 'docs/CLI.md'), $crlf)
    Check ((Invoke-Builder $current -Check) -eq 0) '-Check ignores the line ends the checkout gave the file'
    [System.IO.File]::AppendAllText((Join-Path $current 'docs/CLI.md'), "a hand edit`n")
    Check ((Invoke-Builder $current -Check) -eq 1) '-Check on a hand-edited docs/CLI.md exits 1'
    $stale = New-Tree 'stale'
    $edited = $manifestText.Replace('Print the list of commands this product accepts.', 'Print the commands.')
    Check ($edited -ne $manifestText) 'the stale fixture changed the manifest'
    [System.IO.File]::WriteAllText((Join-Path $stale 'docs/cli-commands.json'), $manifestText)
    $null = Invoke-Builder $stale
    [System.IO.File]::WriteAllText((Join-Path $stale 'docs/cli-commands.json'), $edited)
    Check ((Invoke-Builder $stale -Check) -eq 1) '-Check after a manifest edit without a re-render exits 1'
    $absent = New-Tree 'absent'
    Check ((Invoke-Builder $absent -Check) -eq 1) '-Check with no docs/CLI.md exits 1'
    $broken = New-Tree 'broken'
    $removed = ($manifestText | ConvertFrom-Json)
    Remove-Member $removed.commands[2] 'idempotent'
    [System.IO.File]::WriteAllText((Join-Path $broken 'docs/cli-commands.json'), ($removed | ConvertTo-Json -Depth 20))
    Check ((Invoke-Builder $broken) -eq 1) 'rendering a manifest that lacks a member exits 1'
    Check (-not (Test-Path -LiteralPath (Join-Path $broken 'docs/CLI.md'))) 'nothing is rendered from a manifest that lacks a member'
    $notJson = New-Tree 'notjson'
    [System.IO.File]::WriteAllText((Join-Path $notJson 'docs/cli-commands.json'), '{ not json')
    Check ((Invoke-Builder $notJson -Check) -eq 2) 'a manifest that is not JSON exits 2'
    $none = Join-Path $fixture 'none'
    New-Item -ItemType Directory -Path (Join-Path $none 'docs') -Force | Out-Null
    Check ((Invoke-Builder $none -Check) -eq 2) 'an absent manifest exits 2'

    # ----- the script as a process: the exit code a caller reads ---------------------------------------------------
    $builder = Join-Path $PSScriptRoot 'Build-CliDoc.ps1'
    $ran = Invoke-Script @($builder, '-Root', $stale, '-Check')
    Check ($ran.ExitCode -eq 1) "the script answers a stale docs/CLI.md with exit 1 as a process ($(Format-Ran $ran))"
    Check ($ran.Text.Contains('FAIL (docs/CLI.md is stale)')) "the process says why it answered 1 ($(Format-Ran $ran))"
    $ran = Invoke-Script @($builder, '-Root', $current)
    Check ($ran.ExitCode -eq 0) "the script renders a whole manifest with exit 0 as a process ($(Format-Ran $ran))"
    Check ($ran.Text.Contains('PASS (wrote docs/CLI.md')) "the process says what it wrote ($(Format-Ran $ran))"

    # ----- the repository itself ------------------------------------------------------------------------------------
    Check (Test-CliDocCurrent -Path (Join-Path $repository 'docs/CLI.md') -Rendered $first) 'docs/CLI.md of this repository is what its manifest renders'
}
catch {
    # A setup step or a subject function threw instead of answering: nothing was judged past this point, so the run could
    # not verify (CHECK-VERDICT 2.1, 2.5, 2.11). A check that already failed still outranks it (2.14).
    $reason = $_.Exception.Message -replace '\s+', ' '
    if ($failures.Count -eq 0) { Write-Output "cli-doc-test: COULD NOT VERIFY ($reason)"; exit 2 }
    Write-Output "cli-doc-test: the run stopped early: $reason"
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) {
    Write-Output "cli-doc-test: FAIL ($($failures.Count) of $($passed + $failures.Count))"
    exit 1
}
Write-Output "cli-doc-test: PASS (run=$passed skipped=0)"
exit 0
