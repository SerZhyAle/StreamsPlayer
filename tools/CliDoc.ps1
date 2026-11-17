<#
    SP-0276 (APP-COMMANDS rules 18 and 20): the command-line reference is a render target of the manifest.

    This library holds the two things the generator and its fixtures share. Dot-source it:
        . "$PSScriptRoot/CliDoc.ps1"

      Read-CliManifest         reads docs/cli-commands.json; throws a sentence a person can act on.
      Get-CliManifestProblem   the member gate: every member of APP-COMMANDS rule 18 is there and every closed set holds.
      ConvertTo-CliDocMarkdown the render: docs/CLI.md, LF line ends, byte-identical for the same manifest.
      Test-CliDocCurrent       the rendered text equals the file on disk (line ends ignored).

    The manifest is the source of truth (it is embedded in the build and printed by `commands --json`); nothing here
    types a command, an option, a risk or an exit code. The prose around the tables is the only text this file owns.
#>

Set-StrictMode -Version Latest

$script:CliRisk = @('safe', 'reads-user-data', 'writes-user-data', 'leaves-machine')
$script:CliColdStart = @('start', 'oneshot', 'refuse')
$script:CliWait = @('done', 'accepted')
$script:CliKind = @('string', 'path', 'int', 'bool', 'enum', 'guid')
$script:CliShell = @('cmd', 'powershell')

function Get-CliProp {
    # StrictMode is on, so an absent property is read through PSObject, not by name.
    param($Object, [Parameter(Mandatory)] [string] $Name)

    if ($null -ne $Object -and $Object -isnot [string] -and $Object.PSObject.Properties[$Name]) { return $Object.PSObject.Properties[$Name].Value }
    return $null
}

function Get-CliList {
    # A member read as a list: an absent member or a null is an empty list, never a one-element list holding null.
    param($Object, [Parameter(Mandatory)] [string] $Name)

    $value = Get-CliProp $Object $Name
    if ($null -eq $value) { return , @() }
    return , @($value)
}

function Test-CliHas {
    param($Object, [Parameter(Mandatory)] [string] $Name)

    return ($null -ne $Object -and $Object -isnot [string] -and [bool] $Object.PSObject.Properties[$Name])
}

function Read-CliManifest {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "The command manifest $Path does not exist." }
    try { return [System.IO.File]::ReadAllText($Path) | ConvertFrom-Json }
    catch { throw "The command manifest $Path is not valid JSON: $($_.Exception.Message)" }
}

function Test-CliText {
    param($Value)

    return ($Value -is [string] -and -not [string]::IsNullOrWhiteSpace($Value))
}

function Test-CliInteger {
    param($Value)

    return ($Value -is [int] -or $Value -is [long])
}

function Get-CliManifestProblem {
    # One sentence per problem, each naming the entry and the member. Empty when the manifest is whole.
    param([Parameter(Mandatory)] $Manifest)

    $problems = [System.Collections.Generic.List[string]]::new()
    $version = '^\d+\.\d+$'

    if (-not (Test-CliHas $Manifest 'schemaVersion') -or -not (Test-CliInteger $Manifest.schemaVersion)) { $problems.Add('manifest: schemaVersion is missing or not an integer.') }
    foreach ($member in 'product', 'exe') {
        if (-not (Test-CliText (Get-CliProp $Manifest $member))) { $problems.Add("manifest: $member is missing or empty.") }
    }
    $cliVersion = Get-CliProp $Manifest 'cliVersion'
    if (-not (Test-CliText $cliVersion) -or $cliVersion -notmatch $version) { $problems.Add('manifest: cliVersion is missing or is not MAJOR.MINOR.') }

    if (Test-CliHas $Manifest 'environment') {
        foreach ($variable in @($Manifest.environment)) {
            $label = "environment $(Get-CliProp $variable 'name')"
            if (-not (Test-CliText (Get-CliProp $variable 'name'))) { $problems.Add('environment: an entry has no name.') }
            if ((Get-CliProp $variable 'selectsInstance') -isnot [bool]) { $problems.Add("${label}: selectsInstance is missing or not true or false.") }
            if (-not (Test-CliText (Get-CliProp $variable 'description'))) { $problems.Add("${label}: description is missing or empty.") }
        }
    }

    $commands = (Get-CliList $Manifest 'commands')
    if ($commands.Count -eq 0) { $problems.Add('manifest: commands is missing or empty.'); return $problems.ToArray() }

    $names = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($command in $commands) {
        $name = [string] (Get-CliProp $command 'name')
        if ($name -cnotmatch '^[a-z][a-z0-9-]*( [a-z][a-z0-9-]*)?$') { $problems.Add("command '$name': name is not one or two lower-case words (rule 1)."); $name = "#$($names.Count + 1)" }
        elseif (-not $names.Add($name)) { $problems.Add("command '$name': the name is listed twice.") }
        $label = "command '$name'"

        foreach ($member in 'summary', 'synopsis') {
            if (-not (Test-CliText (Get-CliProp $command $member))) { $problems.Add("${label}: $member is missing or empty.") }
        }
        foreach ($member in 'aliases', 'args', 'returns', 'exitCodes', 'examples') {
            if (-not (Test-CliHas $command $member)) { $problems.Add("${label}: $member is missing.") }
        }
        if ((Test-CliHas $command 'exitCodes') -and @($command.exitCodes).Count -eq 0) { $problems.Add("${label}: exitCodes is empty.") }
        if ((Test-CliHas $command 'examples') -and @($command.examples).Count -eq 0) { $problems.Add("${label}: examples is empty.") }

        foreach ($pair in @(@('risk', $script:CliRisk), @('coldStart', $script:CliColdStart), @('wait', $script:CliWait))) {
            $value = Get-CliProp $command $pair[0]
            if ($null -eq $value) { $problems.Add("${label}: $($pair[0]) is missing.") }
            elseif ($pair[1] -cnotcontains $value) { $problems.Add("${label}: $($pair[0]) '$value' is not one of $($pair[1] -join ', ').") }
        }
        $timeout = Get-CliProp $command 'timeoutMs'
        if (-not (Test-CliInteger $timeout)) { $problems.Add("${label}: timeoutMs is missing or not an integer.") }
        elseif ($timeout -lt 1 -or $timeout -gt 600000) { $problems.Add("${label}: timeoutMs $timeout is outside 1 to 600000.") }
        foreach ($member in 'interactive', 'idempotent') {
            if ((Get-CliProp $command $member) -isnot [bool]) { $problems.Add("${label}: $member is missing or not true or false.") }
        }
        $since = Get-CliProp $command 'since'
        if (-not (Test-CliText $since) -or $since -notmatch $version) { $problems.Add("${label}: since is missing or is not MAJOR.MINOR.") }
        if ((Test-CliHas $command 'exe') -and -not (Test-CliText $command.exe)) { $problems.Add("${label}: exe is present but empty.") }

        $optionNames = [System.Collections.Generic.HashSet[string]]::new()
        foreach ($arg in (Get-CliList $command 'args')) { [void] $optionNames.Add([string] (Get-CliProp $arg 'name')) }

        # SP-0279, item C: forward is always or never; a never verb is done by the receiving process and starts nothing.
        $forward = Get-CliProp $command 'forward'
        if (Test-CliHas $command 'forward') {
            if (@('always', 'never') -cnotcontains $forward) { $problems.Add("${label}: forward '$forward' is not always or never.") }
            elseif ($forward -ceq 'never' -and ((Get-CliProp $command 'coldStart') -cne 'oneshot' -or (Get-CliProp $command 'wait') -cne 'done')) {
                $problems.Add("${label}: a forward never command must have coldStart oneshot and wait done (item C).")
            }
        }
        # SP-0279, rule 18 oneOf: groups of option names; every name is an option of the command.
        if (Test-CliHas $command 'oneOf') {
            foreach ($group in @($command.oneOf)) {
                foreach ($optionName in @($group)) {
                    if (-not $optionNames.Contains([string] $optionName)) { $problems.Add("${label}: oneOf names '$optionName', which is not an option.") }
                }
            }
        }
        # SP-0279, item E: an alias is text or an object with a spelling; every placeholder maps to an option of the command.
        foreach ($alias in (Get-CliList $command 'aliases')) {
            if ($alias -is [string]) { continue }
            if ($null -eq $alias -or -not (Test-CliHas $alias 'spelling') -or $alias.spelling -isnot [string]) { $problems.Add("${label}: an alias is neither text nor an object with a spelling."); continue }
            $aliasLabel = "$label alias '$($alias.spelling)'"
            foreach ($member in 'legacy', 'answers') {
                if ((Test-CliHas $alias $member) -and (Get-CliProp $alias $member) -isnot [bool]) { $problems.Add("${aliasLabel}: $member is not true or false.") }
            }
            if (Test-CliHas $alias 'maps') {
                foreach ($placeholder in @($alias.maps.PSObject.Properties)) {
                    if (-not $optionNames.Contains([string] $placeholder.Value)) { $problems.Add("${aliasLabel}: {$($placeholder.Name)} maps to '$($placeholder.Value)', which is not an option.") }
                }
            }
        }
        foreach ($arg in (Get-CliList $command 'args')) {
            $argName = [string] (Get-CliProp $arg 'name')
            $argLabel = "$label option '$argName'"
            if (-not (Test-CliText $argName)) { $problems.Add("${label}: an option has no name."); continue }
            # SP-0279, item H: a sensitive option is also taken from a file the caller names, --<name>-file.
            if (Test-CliHas $arg 'sensitive') {
                if ((Get-CliProp $arg 'sensitive') -isnot [bool]) { $problems.Add("${argLabel}: sensitive is not true or false.") }
                elseif ($arg.sensitive -and -not $optionNames.Contains("$argName-file")) { $problems.Add("${argLabel}: a sensitive option needs the option $argName-file (item H).") }
            }
            $kind = Get-CliProp $arg 'kind'
            if ($null -eq $kind) { $problems.Add("${argLabel}: kind is missing.") }
            elseif ($script:CliKind -cnotcontains $kind) { $problems.Add("${argLabel}: kind '$kind' is not one of $($script:CliKind -join ', ').") }
            if ((Get-CliProp $arg 'required') -isnot [bool]) { $problems.Add("${argLabel}: required is missing or not true or false.") }
            if (-not (Test-CliText (Get-CliProp $arg 'description'))) { $problems.Add("${argLabel}: description is missing or empty.") }
            if ($kind -ceq 'enum' -and (Get-CliList $arg 'values').Count -eq 0) { $problems.Add("${argLabel}: an enum option has no values.") }
        }
        foreach ($return in (Get-CliList $command 'returns')) {
            $returnName = [string] (Get-CliProp $return 'name')
            $returnLabel = "$label returned value '$returnName'"
            if (-not (Test-CliText $returnName)) { $problems.Add("${label}: a returned value has no name."); continue }
            # Rule 18 closes the kinds of an option; a returned value's kind is free text (string, int, bool, array, ..).
            if (-not (Test-CliText (Get-CliProp $return 'kind'))) { $problems.Add("${returnLabel}: kind is missing or empty.") }
            if (-not (Test-CliText (Get-CliProp $return 'description'))) { $problems.Add("${returnLabel}: description is missing or empty.") }
        }
        foreach ($exit in (Get-CliList $command 'exitCodes')) {
            $code = Get-CliProp $exit 'code'
            if (-not (Test-CliInteger $code) -or $code -lt 0 -or $code -gt 255) { $problems.Add("${label}: an exit code is missing or is not 0 to 255.") }
            if (-not (Test-CliText (Get-CliProp $exit 'meaning'))) { $problems.Add("$label exit code ${code}: meaning is missing or empty.") }
        }
        $index = 0
        foreach ($example in (Get-CliList $command 'examples')) {
            $index++
            $shell = Get-CliProp $example 'shell'
            if ($null -eq $shell) { $problems.Add("$label example ${index}: shell is missing.") }
            elseif ($script:CliShell -cnotcontains $shell) { $problems.Add("$label example ${index}: shell '$shell' is not cmd or powershell.") }
            foreach ($member in 'line', 'expect') {
                if (-not (Test-CliText (Get-CliProp $example $member))) { $problems.Add("$label example ${index}: $member is missing or empty.") }
            }
        }
    }
    return $problems.ToArray()
}

function Format-CliCell {
    # One table cell: no pipe and no line break, so the cell cannot end its row.
    param([AllowEmptyString()] [string] $Text)

    return ($Text -replace '\r?\n', ' ').Replace('|', '\|')
}

function Format-CliAlias {
    # An alias is text or an object (APP-COMMANDS item E): the spelling first, then what the object says about it.
    param($Alias)

    $spelling = if ($Alias -is [string]) { $Alias } else { [string] (Get-CliProp $Alias 'spelling') }
    $text = if ($spelling -eq '') { 'no argument at all, a bare start of the application' } else { '`' + $spelling + '`' }
    if ($Alias -is [string]) { return $text }

    $notes = [System.Collections.Generic.List[string]]::new()
    if ((Get-CliProp $Alias 'legacy') -eq $true) { $notes.Add('shipped before this command line existed') }
    if ((Get-CliProp $Alias 'answers') -eq $false) { $notes.Add('starts the application and returns no answer') }
    $maps = Get-CliProp $Alias 'maps'
    if ($null -ne $maps) {
        foreach ($placeholder in @($maps.PSObject.Properties)) { $notes.Add("{$($placeholder.Name)} is read as ``--$($placeholder.Value)``") }
    }
    foreach ($option in (Get-CliList $Alias 'implies')) { $notes.Add("implies ``$option``") }
    if ($notes.Count -eq 0) { return $text }
    return $text + ' (' + ($notes -join '; ') + ')'
}

function ConvertTo-CliDocMarkdown {
    param([Parameter(Mandatory)] $Manifest)

    $product = [string] $Manifest.product
    $exe = [string] $Manifest.exe
    $commands = @($Manifest.commands)
    $others = @($commands | ForEach-Object { Get-CliProp $_ 'exe' } | Where-Object { $_ } | Sort-Object -Unique)
    $listing = @($commands | Where-Object { $_.name -ceq 'commands' } | Select-Object -First 1)

    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add("# $product command line")
    $out.Add('')
    $out.Add('<!-- GENERATED by tools/Build-CliDoc.ps1 from docs/cli-commands.json. Do not edit by hand: change the manifest and run the tool. -->')
    $out.Add('')
    $out.Add("Scripts, scheduled tasks, shortcuts, other programs and development agents can give $product commands. This page lists all $($commands.Count) of them, command set version $($Manifest.cliVersion). It is generated from the manifest ``docs/cli-commands.json``, which is the one source of truth and the same object ``commands --json`` prints.")
    $out.Add('')
    $out.Add('## How to call')
    $out.Add('')
    $out.Add("- **The running copy does the work.** A command is carried to the copy of $product that is already running, and the process you start waits for the answer and exits with the answer's exit code. A command never opens a dialog and waits for a person.")
    if ($others.Count -gt 0) {
        $quoted = ($others | ForEach-Object { '`' + $_ + '`' }) -join ', '
        $out.Add("- **Two executables.** ``$exe`` is the window application. $quoted is its console companion, for pipes and for scripts that read standard output; the ``Executable`` column below says which one serves each command. Both are installed side by side.")
    }
    $out.Add("- **A window application has no console of its own.** The two channels a caller can always rely on are the exit code and the ``--result <path>`` file. To read the exit code of ``$exe``, wait for it: ``start /wait $exe ..`` in cmd, ``Start-Process -Wait -PassThru`` in PowerShell. The examples below use that form.")
    if ($listing.Count -gt 0) {
        $out.Add("- **List the commands.** ``$($listing[0].synopsis)``. It needs no running copy and opens no window; with ``--json`` it prints the manifest itself.")
    }
    $out.Add('- **The risk setting.** A command whose risk is above `safe` is refused with exit `12` and reason `switch-off` until the person turns on **Let other programs ask STREAMS Player to work with my files, screen, clipboard or the network** in Settings. The setting is off by default and no command can change it. Where an exit code below says "the setting of rule 13", it means this setting.')
    $out.Add('- **Strict.** An unknown command, an unknown option, a missing value or a value of the wrong kind is refused with exit `10` and nothing is executed. Commands and options are matched without regard to case; values never are.')
    $out.Add('- **Reserved words.** The first word of a command line is a command, or one of the spellings listed under a command as also accepted; either one wins over a file of the same name. To reach such a file, give it as a path with a folder in it (`.\show`) or to the command that opens a file (`play --file`).')
    $out.Add('')

    $environment = (Get-CliList $Manifest 'environment')
    if ($environment.Count -gt 0) {
        $out.Add('## Environment')
        $out.Add('')
        $out.Add('| Variable | Selects the instance | What it does |')
        $out.Add('| --- | --- | --- |')
        foreach ($variable in $environment) {
            $selects = if ($variable.selectsInstance) { 'yes' } else { 'no' }
            $out.Add("| ``$($variable.name)`` | $selects | $(Format-CliCell $variable.description) |")
        }
        $out.Add('')
    }

    $out.Add('## All commands')
    $out.Add('')
    $out.Add('| Command | Risk | With nothing running | Executable | What it does |')
    $out.Add('| --- | --- | --- | --- | --- |')
    foreach ($command in $commands) {
        $serves = [string] (Get-CliProp $command 'exe')
        if ($serves -eq '') { $serves = $exe }
        $out.Add("| [``$($command.name)``](#$(($command.name -replace ' ', '-'))) | ``$($command.risk)`` | ``$($command.coldStart)`` | ``$serves`` | $(Format-CliCell $command.summary) |")
    }
    $out.Add('')
    $out.Add('Risk: `safe` changes only the application''s own state or window and reads nothing of yours; `reads-user-data` puts something of yours into the answer or a file you name; `writes-user-data` creates or changes files or settings outside the application''s own data folder; `leaves-machine` sends anything off this PC. Only `safe` commands work while the setting above is off.')
    $out.Add('')
    $out.Add('With nothing running: `start` makes the process the running copy and then runs the command; `oneshot` does the work and exits without leaving a window or tray icon; `refuse` answers exit `11` (reason `not-running`).')
    $out.Add('')

    foreach ($command in $commands) {
        $serves = [string] (Get-CliProp $command 'exe')
        if ($serves -eq '') { $serves = $exe }
        $out.Add("### $($command.name)")
        $out.Add('')
        $out.Add([string] $command.summary)
        $out.Add('')
        $out.Add('```text')
        $out.Add([string] $command.synopsis)
        $out.Add('```')
        $out.Add('')
        $timeout = [string] ([double] $command.timeoutMs / 1000)
        $interactive = if ($command.interactive) { 'interactive' } else { 'never waits for a person' }
        $idempotent = if ($command.idempotent) { 'safe to run again' } else { 'running it again repeats the effect' }
        $forwarding = if ((Get-CliProp $command 'forward') -ceq 'never') { '; done by the process you start, never carried to a running copy' } else { '' }
        $out.Add("Risk ``$($command.risk)``; with nothing running ``$($command.coldStart)``; waits for ``$($command.wait)`` up to $timeout s; $interactive; $idempotent; executable ``$serves``$forwarding; since $($command.since).")
        $out.Add('')
        $aliases = @($command.aliases)
        if ($aliases.Count -gt 0) {
            $out.Add('Also accepted, as shipped spellings of this command:')
            $out.Add('')
            foreach ($alias in $aliases) { $out.Add("- $(Format-CliAlias $alias)") }
            $out.Add('')
        }
        $options = @($command.args)
        if ($options.Count -gt 0) {
            $out.Add('| Option | Kind | Required | Default | What it is |')
            $out.Add('| --- | --- | --- | --- | --- |')
            foreach ($arg in $options) {
                $kind = [string] $arg.kind
                $values = (Get-CliList $arg 'values')
                if ($kind -ceq 'enum' -and $values.Count -gt 0) { $kind = 'enum: ' + (($values | ForEach-Object { [string] $_ }) -join ', ') }
                $required = if ($arg.required) { 'yes' } else { 'no' }
                $default = Get-CliProp $arg 'default'
                $shown = if ($null -eq $default) { '-' } elseif ($default -is [bool]) { $default.ToString().ToLowerInvariant() } else { [string] $default }
                $description = [string] $arg.description
                if ((Get-CliProp $arg 'sensitive') -eq $true) { $description += " Sensitive: never written to a log line, an answer or a message; also taken from --$($arg.name)-file." }
                $out.Add("| ``--$($arg.name)`` | $(Format-CliCell $kind) | $required | $(Format-CliCell $shown) | $(Format-CliCell $description) |")
            }
            $out.Add('')
            if (Test-CliHas $command 'oneOf') {
                # The property is read directly, not through Get-CliProp: a function's output would unroll the groups.
                foreach ($group in @($command.oneOf)) {
                    $names = [System.Collections.Generic.List[string]]::new()
                    foreach ($optionName in @($group)) { $names.Add('`--' + $optionName + '`') }
                    $out.Add('Exactly one of ' + ($names -join ' or ') + ' is given.')
                    $out.Add('')
                }
            }
        }
        $returns = @($command.returns)
        if ($returns.Count -gt 0) {
            $out.Add('Returns, in the `data` member of the answer and in the `--result` file:')
            $out.Add('')
            $out.Add('| Name | Kind | What it is |')
            $out.Add('| --- | --- | --- |')
            foreach ($return in $returns) { $out.Add("| ``$($return.name)`` | $(Format-CliCell ([string] $return.kind)) | $(Format-CliCell $return.description) |") }
            $out.Add('')
        }
        $out.Add('| Exit code | Meaning |')
        $out.Add('| --- | --- |')
        foreach ($exit in @($command.exitCodes)) { $out.Add("| ``$($exit.code)`` | $(Format-CliCell $exit.meaning) |") }
        $out.Add('')
        foreach ($example in @($command.examples)) {
            $fence = if ($example.shell -ceq 'cmd') { 'cmd' } else { 'powershell' }
            $out.Add('```' + $fence)
            $out.Add([string] $example.line)
            $out.Add('```')
            $out.Add('')
            $out.Add("Expected: $($example.expect).")
            $out.Add('')
        }
    }
    return (($out -join "`n").TrimEnd("`n") + "`n")
}

function Test-CliDocCurrent {
    # True when the file on disk is what the manifest renders; line ends are not compared.
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Rendered)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $onDisk = [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    return ($onDisk -ceq $Rendered)
}

function Invoke-CliDocBuild {
    # The whole of tools/Build-CliDoc.ps1 as a function, so the fixtures call it without a process per case.
    # Writes its lines to the output stream and returns the exit code: 0 passed, 1 failed, 2 could not verify.
    param([Parameter(Mandatory)] [string] $Root, [switch] $Check)

    $manifestPath = Join-Path $Root 'docs/cli-commands.json'
    $documentPath = Join-Path $Root 'docs/CLI.md'
    try {
        $manifest = Read-CliManifest -Path $manifestPath
    }
    catch {
        Write-Output "build-cli-doc: $($_.Exception.Message)"
        Write-Output 'build-cli-doc: COULD NOT VERIFY (the manifest cannot be read)'
        return 2
    }

    $problems = @(Get-CliManifestProblem -Manifest $manifest)
    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) { Write-Output "build-cli-doc: $problem" }
        Write-Output "build-cli-doc: FAIL (the manifest has $($problems.Count) problem(s); nothing was rendered)"
        return 1
    }

    $rendered = ConvertTo-CliDocMarkdown -Manifest $manifest
    $count = @($manifest.commands).Count
    if ($Check) {
        if (-not (Test-CliDocCurrent -Path $documentPath -Rendered $rendered)) {
            Write-Output 'build-cli-doc: docs/CLI.md is not what docs/cli-commands.json renders - run tools/Build-CliDoc.ps1 and commit both.'
            Write-Output 'build-cli-doc: FAIL (docs/CLI.md is stale)'
            return 1
        }
        Write-Output "build-cli-doc: PASS ($count commands, docs/CLI.md current)"
        return 0
    }

    [System.IO.File]::WriteAllText($documentPath, $rendered, (New-Object System.Text.UTF8Encoding $false))
    Write-Output "build-cli-doc: PASS (wrote docs/CLI.md, $count commands)"
    return 0
}
