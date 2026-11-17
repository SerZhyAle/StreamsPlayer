<#
.SYNOPSIS
  SP-0277: the console-companion round of the playback smoke gate. Functions only; dot-sourced by scripts/smoke-playback.ps1.

.DESCRIPTION
  StreamsPlayerCli.exe is the executable a caller that pipes runs (SP-0275, APP-COMMANDS rule 9), and it ships in the same
  publish folder as the player. The playback rounds prove the player plays; nothing in them touches the companion, so
  whether it works in a real pipeline was a manual step of every release. This round drives it from the folder under test
  against a profile of its own (STREAMSPLAYER_DATA_DIRECTORY, the variable the player and the companion both resolve their
  first instance from) and judges five observations:

    nothing running   commands --json    exit 0, and the text parses with ConvertFrom-Json into a manifest that lists
                                         "commands" and "status" - a console program's output is captured by a pipeline;
                                         a window program's is not, so this is also what "opens no window" can mean here
                      status --json      exit 11 (not running)
                      play --url x       exit 10 (a start verb is the player's, not the companion's)
    the player runs   status --json      exit 0, the answer's data.pid is the launched process and data.profile is
                                         "relocated" - the companion reached the isolated copy, not the owner's own copy

  The player is started bare. An argument-free launch starts no stream, so the round needs no network and no audio device.
  A fresh profile would ask the clean-install question modally and `status` answers 11 ("busy") while a dialog is open,
  so the profile is seeded with a one-member catalog-state.json: the file existing means the machine has run the product
  before (SP-0059), and catalogSnapshotOfferDeclined means the older offer (SP-0052) is not asked either.

  The exit codes are APP-COMMANDS values already published in docs/cli-commands.json: 0 done, 10 usage, 11 busy or not running.
#>

# The companion with the data-directory variable set for this call alone. Returns the exit code and the text (standard
# output and standard error, one string); the exit code is -1 when the program could not be started at all.
function Invoke-CompanionCli {
    param(
        [Parameter(Mandatory)] [string] $Cli,
        [Parameter(Mandatory)] [string] $ProfileDir,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    # A native program's stderr, merged into the pipeline, must not become a terminating error of the caller's preference.
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = -1
    $env:STREAMSPLAYER_DATA_DIRECTORY = $ProfileDir
    try {
        $lines = @(& $Cli @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    }
    catch {
        $lines = @($_.Exception.Message)
        $code = -1
    }
    finally { Remove-Item Env:STREAMSPLAYER_DATA_DIRECTORY -ErrorAction SilentlyContinue }
    [pscustomobject]@{ Exit = $code; Text = ($lines -join "`n") }
}

# The whole round. $ProfileDir must exist and be empty (the caller resets it); the round seeds it. Returns
#   Pass      true when every check is Ok
#   Checks    @{ Name; Expected; Actual; Ok } in the order made, for the caller to print
#   Observed  the values the release verdict keeps
# and never throws for a program that misbehaves - that is what the checks are for.
function Invoke-CompanionRound {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Cli,
        [Parameter(Mandatory)] [string] $App,
        [Parameter(Mandatory)] [string] $ProfileDir,
        [int] $Budget = 60,
        [double] $PollSeconds = 1,
        [string[]] $AppArguments = @()
    )

    $checks = [System.Collections.Generic.List[object]]::new()
    $observed = [ordered]@{}
    $add = {
        param([string] $Name, [string] $Expected, [string] $Actual, [bool] $Ok)
        $checks.Add([pscustomobject]@{ Name = $Name; Expected = $Expected; Actual = $Actual; Ok = $Ok })
    }
    $finish = { [pscustomobject]@{ Pass = (@($checks | Where-Object { -not $_.Ok }).Count -eq 0); Checks = $checks.ToArray(); Observed = $observed } }
    $brief = { param([string] $Text) $flat = ($Text -replace '\s+', ' ').Trim(); if ($flat.Length -gt 160) { $flat.Substring(0, 160) + '..' } else { $flat } }

    # " - <text>" for a failing line, or nothing when the program said nothing.
    $tail = { param([string] $Text) $short = & $brief $Text; if ($short) { " - $short" } else { '' } }

    if (-not (Test-Path -LiteralPath $Cli -PathType Leaf)) {
        & $add 'companion present' "StreamsPlayerCli.exe beside the application ($Cli)" 'missing - the application project publishes it into its own publish folder (SP-0275)' $false
        return (& $finish)
    }

    # --- nothing running ----------------------------------------------------------------------------------------------
    $commands = Invoke-CompanionCli -Cli $Cli -ProfileDir $ProfileDir -Arguments @('commands', '--json')
    $observed['commandsJsonExit'] = $commands.Exit
    $listed = @()
    $parseError = $null
    try { $listed = @((($commands.Text | ConvertFrom-Json -ErrorAction Stop).commands) | ForEach-Object { $_.name }) }
    catch { $parseError = & $brief $commands.Text }
    $observed['commandsListed'] = $listed.Count
    $manifestOk = $commands.Exit -eq 0 -and -not $parseError -and ($listed -contains 'commands') -and ($listed -contains 'status')
    $actual = if ($parseError) { "exit $($commands.Exit), not a manifest: $parseError" } else { "exit $($commands.Exit), $($listed.Count) commands listed" }
    & $add 'commands --json' 'exit 0 and a manifest object that lists commands and status' $actual $manifestOk

    $none = Invoke-CompanionCli -Cli $Cli -ProfileDir $ProfileDir -Arguments @('status', '--json')
    $observed['statusNoneRunningExit'] = $none.Exit
    & $add 'status, nothing running' 'exit 11' "exit $($none.Exit)$(if ($none.Exit -ne 11) { & $tail $none.Text })" ($none.Exit -eq 11)

    $start = Invoke-CompanionCli -Cli $Cli -ProfileDir $ProfileDir -Arguments @('play', '--url', 'x')
    $observed['startVerbExit'] = $start.Exit
    & $add 'play --url x' 'exit 10 (a start verb is the player''s)' "exit $($start.Exit)$(if ($start.Exit -ne 10) { & $tail $start.Text })" ($start.Exit -eq 10)

    # --- the player runs ----------------------------------------------------------------------------------------------
    [System.IO.File]::WriteAllText((Join-Path $ProfileDir 'catalog-state.json'), '{"catalogSnapshotOfferDeclined":true}', [System.Text.UTF8Encoding]::new($false))

    $env:STREAMSPLAYER_DATA_DIRECTORY = $ProfileDir
    try {
        $launch = @{ FilePath = $App; PassThru = $true }
        if ($AppArguments.Count -gt 0) { $launch['ArgumentList'] = $AppArguments }
        $proc = Start-Process @launch
    }
    catch {
        & $add 'player starts' "$App started" "could not be started: $($_.Exception.Message)" $false
        return (& $finish)
    }
    finally { Remove-Item Env:STREAMSPLAYER_DATA_DIRECTORY -ErrorAction SilentlyContinue }

    try {
        $answer = $null
        $exited = $null
        $deadline = [DateTime]::UtcNow.AddSeconds($Budget)
        do {
            if ($proc.HasExited) { $exited = $proc.ExitCode; break }
            $answer = Invoke-CompanionCli -Cli $Cli -ProfileDir $ProfileDir -Arguments @('status', '--json')
            if ($answer.Exit -eq 0) { break }
            Start-Sleep -Milliseconds ([int]($PollSeconds * 1000))
        } while ([DateTime]::UtcNow -lt $deadline)

        if ($null -ne $exited) {
            $observed['statusRunningExit'] = -1
            & $add 'status, player running' 'exit 0 from the launched player' "the player exited (code $exited) before it answered" $false
        }
        else {
            $observed['statusRunningExit'] = $answer.Exit
            $answeredPid = $null
            $answeredProfile = $null
            try {
                $data = ($answer.Text | ConvertFrom-Json -ErrorAction Stop).data
                $answeredPid = $data.pid
                $answeredProfile = $data.profile
            }
            catch { $null = $_ }
            $actual = if ($answer.Exit -ne 0) { "exit $($answer.Exit) within ${Budget}s - $(& $brief $answer.Text)" } else { "exit 0, pid $answeredPid, profile $answeredProfile" }
            & $add 'status, player running' 'exit 0 within the budget' $actual ($answer.Exit -eq 0)

            if ($answer.Exit -eq 0) {
                $mine = $answeredPid -eq $proc.Id -and $answeredProfile -eq 'relocated'
                $observed['answeredBySmokeCopy'] = $mine
                & $add 'answered by the launched copy' "data.pid $($proc.Id) and data.profile relocated" "data.pid $answeredPid and data.profile $answeredProfile" $mine
            }
        }
    }
    finally {
        if (-not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            $proc.WaitForExit(5000) | Out-Null
        }
    }
    return (& $finish)
}
