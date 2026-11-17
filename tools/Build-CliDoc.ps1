<#
.SYNOPSIS
    SP-0276: renders docs/CLI.md from docs/cli-commands.json and holds the two together.

.DESCRIPTION
    APP-COMMANDS rule 20: the command-line reference is a render target of the manifest of record (rule 18) and
    is never edited by hand (canon invariant 16). The command table, each command's synopsis, risk, exit codes
    and its cmd and PowerShell examples all come from the manifest; tools/CliDoc.ps1 holds the render.

    Modes:
    (default)  write docs/CLI.md.
    -Check     change nothing. Exit 0 when the manifest carries every member of rule 18 and docs/CLI.md is what
               it renders; 1 on a missing member, a value outside a closed set (each named) or a stale
               docs/CLI.md; 2 when the manifest cannot be read.

    -Root is the repository root (default: the one this script is in); the fixtures of tools/Test-CliDoc.ps1
    point it at a scratch tree.

    Exit codes (CHECK-VERDICT): 0 passed, 1 a check failed, 2 could not verify. The last line is
        build-cli-doc: PASS | FAIL (<reason>) | COULD NOT VERIFY (<reason>)

.EXAMPLE
    pwsh -NoProfile -File ./tools/Build-CliDoc.ps1
    pwsh -NoProfile -File ./tools/Build-CliDoc.ps1 -Check
#>
[CmdletBinding()]
param(
    [switch] $Check,
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'CliDoc.ps1')

$result = @(Invoke-CliDocBuild -Root $Root -Check:$Check)
$code = [int] $result[-1]
foreach ($line in @($result | Select-Object -SkipLast 1)) { Write-Host $line }
exit $code
